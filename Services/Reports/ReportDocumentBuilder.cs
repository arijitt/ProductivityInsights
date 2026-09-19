using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public static class ReportDocumentBuilder
{
    public static ReportDocument Build(ReportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var token = cancellationToken;
        token.ThrowIfCancellationRequested();
        var request = snapshot.Request;
        if (request.Analysis is not ("workItems" or "gitCommits" or "incidentManagement"))
            throw new InvalidOperationException("The selected analysis is not supported by report export.");
        if (request.View is not ("details" or "teams" or "trends"))
            throw new InvalidOperationException("The selected view is not supported by report export.");
        if (request.View == "trends" && request.Analysis != "gitCommits")
            throw new InvalidOperationException("Work Item and Incident Trends are placeholder views without trend data. Export Details or Teams instead.");
        if (request.Start > request.End)
            throw new InvalidOperationException("The captured report start is after its end.");
        if (request.Analysis == "gitCommits" && request.TrendPeriod is not ("daily" or "monthly" or "yearly"))
            throw new InvalidOperationException("The captured Git trend interval is not supported.");
        Validate(snapshot, token);

        var skipped = snapshot.SkippedSources.Select(source => source.SourceKey).ToHashSet(StringComparer.Ordinal);
        var sources = request.Sources.Where(source => !skipped.Contains(source.Key))
            .ToDictionary(source => source.Key, StringComparer.Ordinal);
        var sourceOrder = request.Sources.Select((source, index) => (source.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        var budget = new ReportInputBudget(token);
        var catalog = new ReportIdentityCatalog(request, Identities(snapshot), token);
        var rows = new Dictionary<(string Member, string? Source), RowAccumulator>();
        RowAccumulator Row(string key, string label, string? source)
        {
            token.ThrowIfCancellationRequested();
            if (!rows.TryGetValue((key, source), out var row))
            {
                if (rows.Count == ReportRenderingLimits.MaximumSummaryRows)
                    throw new ReportSizeLimitException("The report exceeds the 100,000-row summary construction limit. Select fewer sources or members; no rows were omitted.");
                budget.Record(label, source);
                row = new RowAccumulator(new ReportSummaryRow
                {
                    ContributorKey = key, Contributor = label, SourceKey = source
                });
                rows.Add((key, source), row);
            }
            return row;
        }
        void Credit(string key, string label, ImmutableArray<string> memberships, Action<RowAccumulator> update)
        {
            token.ThrowIfCancellationRequested();
            var overall = Row(key, label, null);
            update(overall);
            foreach (var source in memberships)
            {
                token.ThrowIfCancellationRequested();
                overall.Sources.Add(source);
                var local = Row(key, label, source);
                local.Sources.Add(source);
                update(local);
            }
        }

        if (request.IsTeams && !snapshot.AllSourcesSkipped)
        {
            foreach (var identity in request.Roster)
            {
                var member = catalog.Get(identity);
                Row(member.Key, member.Label, null);
                foreach (var source in request.Sources.Where(source => !skipped.Contains(source.Key)))
                    Row(member.Key, member.Label, source.Key);
            }
        }

        var workItems = ImmutableArray.CreateBuilder<WorkItemDocument>();
        var commits = ImmutableArray.CreateBuilder<GitCommitDocument>();
        var incidents = ImmutableArray.CreateBuilder<IncidentDocument>();
        ImmutableArray<ReportEntity<PullRequestReportRecord>> pullRequests = [];
        ImmutableArray<ReportEntity<IncidentActionReportRecord>> actions = [];

        if (request.Analysis == "workItems")
        {
            var entities = Merge(snapshot.WorkItems, item => item.EntityKey, item => item.SourceKey,
                (left, right) => Same(left, right, token) ? left : Conflict<WorkItemReportRecord>(left.EntityKey), sources, token);
            foreach (var entity in entities)
            {
                token.ThrowIfCancellationRequested();
                var item = entity.Record;
                var member = catalog.Get(item.Assignee);
                var periods = StatePeriods(item, snapshot.AsOf, token);
                double? activeDays = item.History.Length < 2 ? null
                    : periods.Where(period => ActiveState(period.State)).Sum(period => period.Days);
                workItems.Add(new WorkItemDocument(entity, member.Key, member.Label, periods, activeDays));
                Credit(member.Key, member.Label, entity.SourceKeys, row =>
                {
                    var type = item.Type;
                    row.Value = row.Value with
                    {
                        WorkItems = row.Value.WorkItems + 1,
                        Bugs = row.Value.Bugs + (type == "Bug" ? 1 : 0),
                        Tasks = row.Value.Tasks + (type == "Task" ? 1 : 0),
                        ProductBacklogItems = row.Value.ProductBacklogItems + (type == "Product Backlog Item" ? 1 : 0),
                        Other = row.Value.Other + (type is not ("Bug" or "Task" or "Product Backlog Item") ? 1 : 0)
                    };
                });
            }
        }
        else if (request.Analysis == "gitCommits")
        {
            var entities = Merge(snapshot.Commits, item => item.EntityKey, item => item.SourceKey,
                (left, right) => Same(left, right, token) ? left : Conflict<GitReportRecord>(left.EntityKey), sources, token);
            foreach (var entity in entities)
            {
                token.ThrowIfCancellationRequested();
                var commit = entity.Record;
                var author = catalog.Get(commit.Author);
                var committer = catalog.Get(commit.Committer);
                var key = author.Key == committer.Key ? author.Key : PairKey(author.Key, committer.Key);
                var label = author.Key == committer.Key ? author.Label : $"Author: {author.Label} / Committer: {committer.Label}";
                long serviceAdded = 0, serviceDeleted = 0, testAdded = 0, testDeleted = 0;
                long filesAdded = 0, filesModified = 0, filesDeleted = 0;
                foreach (var file in commit.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var fileChange = FileChangeCounts(file.ChangeType);
                    filesAdded += fileChange.Added;
                    filesModified += fileChange.Modified;
                    filesDeleted += fileChange.Deleted;
                    if (file.Path.Contains("test", StringComparison.OrdinalIgnoreCase))
                    {
                        testAdded += file.Added;
                        testDeleted += file.Deleted;
                    }
                    else
                    {
                        serviceAdded += file.Added;
                        serviceDeleted += file.Deleted;
                    }
                }
                var added = serviceAdded + testAdded;
                var deleted = serviceDeleted + testDeleted;
                commits.Add(new GitCommitDocument(entity, key, label, added, deleted,
                    serviceAdded, serviceDeleted, testAdded, testDeleted)
                {
                    FilesAdded = filesAdded, FilesModified = filesModified, FilesDeleted = filesDeleted
                });
                Credit(key, label, entity.SourceKeys, row => row.Value = row.Value with
                {
                    Commits = row.Value.Commits + 1, Added = row.Value.Added + added,
                    Deleted = row.Value.Deleted + deleted, FilesChanged = row.Value.FilesChanged + commit.Files.Length,
                    FilesAdded = row.Value.FilesAdded + filesAdded, FilesModified = row.Value.FilesModified + filesModified,
                    FilesDeleted = row.Value.FilesDeleted + filesDeleted
                });
            }
            pullRequests = Merge(snapshot.PullRequests, item => item.EntityKey, item => item.SourceKey,
                (left, right) => left with { SourceKey = "" } == right with { SourceKey = "" }
                    ? left : Conflict<PullRequestReportRecord>(left.EntityKey), sources, token);
            foreach (var entity in pullRequests)
            {
                token.ThrowIfCancellationRequested();
                if (!InWindow(entity.Record.CompletedAt, request))
                    throw new InvalidOperationException($"PR {entity.Record.EntityKey} has completion evidence outside the captured period.");
                var member = catalog.Get(entity.Record.CompletedBy);
                Credit(member.Key, member.Label, entity.SourceKeys,
                    row => row.Value = row.Value with { PullRequests = row.Value.PullRequests + 1 });
            }
        }
        else
        {
            var entities = Merge(snapshot.Incidents, item => item.EntityKey, item => item.SourceKey,
                MergeIncident, sources, token);
            foreach (var entity in entities)
            {
                token.ThrowIfCancellationRequested();
                var item = entity.Record;
                incidents.Add(new IncidentDocument(entity,
                    NonnegativeHours(item.CreatedAt, item.ImpactStart),
                    NonnegativeHours(item.MitigatedAt, item.CreatedAt),
                    item.Status == "Resolved" ? NonnegativeHours(item.ResolvedAt, item.CreatedAt) : null));
                void IncidentCredit(ReportIdentity identity, Func<ReportSummaryRow, ReportSummaryRow> update)
                {
                    var member = catalog.Get(identity);
                    Credit(member.Key, member.Label, entity.SourceKeys, row => row.Value = update(row.Value));
                }
                if (item.Active) IncidentCredit(item.Owner, row => row with { Active = row.Active + 1 });
                if (item.Created) IncidentCredit(item.Owner, row => row with { Created = row.Created + 1 });
                if (item.Mitigated) IncidentCredit(item.MitigatedBy, row => row with { Mitigated = row.Mitigated + 1 });
                if (item.Resolved) IncidentCredit(item.ResolvedBy, row => row with { Resolved = row.Resolved + 1 });
                if (!item.Active && !item.Created && !item.Mitigated && !item.Resolved)
                {
                    var owner = catalog.Get(item.Owner);
                    Credit(owner.Key, owner.Label, entity.SourceKeys, _ => { });
                }
            }
            actions = Merge(snapshot.IncidentActions,
                IncidentActionKey.From, item => item.SourceKey,
                (left, right) => left with { SourceKey = "" } == right with { SourceKey = "" }
                    ? left : Conflict<IncidentActionReportRecord>(left.IncidentKey + "/" + left.ActionId), sources, token);
            var incidentMap = incidents.ToDictionary(item => item.Entity.Record.EntityKey, StringComparer.Ordinal);
            foreach (var entity in actions)
            {
                token.ThrowIfCancellationRequested();
                var action = entity.Record;
                if (!InWindow(action.Timestamp, request))
                    throw new InvalidOperationException($"Action {action.ActionId} has a timestamp outside the captured period.");
                if (!incidentMap.TryGetValue(action.IncidentKey, out var incident)
                    || entity.SourceKeys.Any(key => !incident.Entity.SourceKeys.Contains(key)))
                    throw new InvalidOperationException($"Action {action.ActionId} has no matching source-qualified incident membership.");
                var member = catalog.Get(action.Actor);
                Credit(member.Key, member.Label, entity.SourceKeys, row =>
                {
                    if (row.ContributedIncidents.Add(action.IncidentKey))
                        row.Value = row.Value with { Contributed = row.Value.Contributed + 1 };
                });
            }
        }

        var sortedWork = workItems.OrderByDescending(item => item.Entity.Record.ClosedAt ?? item.Entity.Record.ChangedAt)
            .ThenBy(item => item.Entity.Record.EntityKey, StringComparer.Ordinal).ToImmutableArray();
        var sortedCommits = commits.OrderByDescending(item => item.Entity.Record.CommitterDate)
            .ThenBy(item => item.Entity.Record.EntityKey, StringComparer.Ordinal).ToImmutableArray();
        var sortedIncidents = OrderIncidents(incidents, token);
        var summary = rows.Values.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            return row.Value with { SourceKeys = row.Sources.Order(StringComparer.Ordinal).ToImmutableArray() };
        }).ToArray();
        var baseTitle = request.Analysis switch
        {
            "workItems" => "Work Item Analysis",
            "gitCommits" => "Code Commit Analysis",
            _ => "Incident Resolutions Analysis"
        };
        var title = $"{(request.IsTeams ? "Team " : "")}{baseTitle} ({ReportFormatting.CalendarDate(request.Start)} to {ReportFormatting.CalendarDate(request.End)})";
        var sourceHeading = request.Analysis switch
        {
            "workItems" => "WORK ITEM SUMMARY BY AREA PATH",
            "gitCommits" => "CODE COMMIT SUMMARY BY REPOSITORY",
            _ => "INCIDENT SUMMARY BY ICM PATH"
        };
        var overallHeading = request.Analysis switch
        {
            "workItems" => "WORK ITEM SUMMARY",
            "gitCommits" => "CODE COMMIT SUMMARY",
            _ => $"INCIDENT SUMMARY ACROSS ALL {request.Sources.Select(source => source.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count()} ICM GROUP(S)"
        };
        token.ThrowIfCancellationRequested();
        return new ReportDocument
        {
            Snapshot = snapshot, Title = title,
            SourceHeading = (request.IsTeams ? "TEAM " : "") + sourceHeading,
            OverallHeading = (request.IsTeams ? "TEAM " : "") + overallHeading,
            MetricNotes = Notes(request),
            SourceRows = summary.Where(row => row.SourceKey is not null)
                .OrderBy(row => sourceOrder[row.SourceKey!])
                .ThenByDescending(Activity).ThenBy(row => row.Contributor, StringComparer.Ordinal).ToImmutableArray(),
            OverallRows = summary.Where(row => row.SourceKey is null).OrderByDescending(Activity)
                .ThenBy(row => row.Contributor, StringComparer.Ordinal).ToImmutableArray(),
            WorkItems = sortedWork, Commits = sortedCommits, Incidents = sortedIncidents,
            PullRequests = pullRequests.OrderByDescending(item => item.Record.CompletedAt)
                .ThenBy(item => item.Record.EntityKey, StringComparer.Ordinal).ToImmutableArray(),
            IncidentActions = actions.OrderBy(item => item.Record.Timestamp)
                .ThenBy(item => item.Record.IncidentKey, StringComparer.Ordinal)
                .ThenBy(item => item.Record.ActionId, StringComparer.Ordinal).ToImmutableArray(),
            GitTrends = BuildTrends(sortedCommits, request.TrendPeriod, token),
            IncidentGroups = request.Analysis == "incidentManagement"
                ? BuildIncidentGroups(sortedIncidents, catalog, token) : [],
            IncidentStatistics = SeverityStatistics(sortedIncidents, token),
            ActiveTeamStatistics = sortedIncidents.Where(item => item.Entity.Record.Active)
                .GroupBy(item => TeamKey(item.Entity.Record), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => SeverityStatistics(group, token).Select(stats => new IncidentTeamStatistics(group.Key, stats)))
                .ToImmutableArray()
        };
    }

    public static ReportDurationStatistics DurationStatistics(IEnumerable<double> values, CancellationToken cancellationToken = default)
    {
        var numbers = new List<double>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (numbers.Count == ReportRenderingLimits.MaximumRecords)
                throw new ReportSizeLimitException("The duration calculation exceeds the 200,000-record construction limit. Select a smaller scope; no durations were omitted.");
            if (!double.IsFinite(value))
                throw new InvalidOperationException("A report duration is not a finite value.");
            numbers.Add(value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            numbers.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return left.CompareTo(right);
            });
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Report duration sorting was cancelled.", exception, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (numbers.Count == 0) return new(0, 0, null, null, null, null);
        var total = 0d;
        foreach (var value in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += value;
        }
        var middle = numbers.Count / 2;
        var median = numbers.Count % 2 == 0 ? (numbers[middle - 1] + numbers[middle]) / 2 : numbers[middle];
        return new(numbers.Count, total, numbers[0], total / numbers.Count, numbers[^1], median);
    }

    private static ImmutableArray<ReportStatePeriod> StatePeriods(WorkItemReportRecord item, DateTimeOffset asOf, CancellationToken token)
    {
        var transitions = new List<WorkItemStateRecord>();
        foreach (var entry in item.History.OrderBy(entry => entry.EnteredAt))
        {
            token.ThrowIfCancellationRequested();
            if (entry.EnteredAt > asOf)
                throw new InvalidOperationException($"Work item {item.EntityKey} has state history after the report as-of instant.");
            if (transitions.Count == 0 || transitions[^1].State != entry.State)
                transitions.Add(entry);
        }
        var periods = ImmutableArray.CreateBuilder<ReportStatePeriod>();
        for (var i = 0; i < transitions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = transitions[i];
            DateTimeOffset? exit = i + 1 < transitions.Count ? transitions[i + 1].EnteredAt : null;
            periods.Add(new(entry.State, entry.EnteredAt, exit, entry.Actor, ((exit ?? asOf) - entry.EnteredAt).TotalDays));
        }
        return periods.ToImmutable();
    }

    private static (int Added, int Modified, int Deleted) FileChangeCounts(string changeType)
    {
        var types = changeType.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Has(string value) => types.Contains(value, StringComparer.OrdinalIgnoreCase);
        // A split rename is one modification at its destination, not an extra deletion at its source.
        if (Has("sourceRename")) return (0, 0, 0);
        if (Has("rename") || Has("targetRename")) return (0, 1, 0);
        if (Has("add")) return (1, 0, 0);
        if (Has("delete")) return (0, 0, 1);
        return (0, 1, 0);
    }

    private static ImmutableArray<GitTrendPeriod> BuildTrends(ImmutableArray<GitCommitDocument> commits, string interval, CancellationToken token)
    {
        var groups = new SortedDictionary<string, GitTrendPeriod>(StringComparer.Ordinal);
        var format = interval == "daily" ? "yyyy-MM-dd" : interval == "yearly" ? "yyyy" : "yyyy-MM";
        foreach (var commit in commits)
        {
            token.ThrowIfCancellationRequested();
            var period = commit.Entity.Record.CommitterDate.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            var old = groups.TryGetValue(period, out var existing) ? existing : new(period, 0, 0, 0, 0, 0, 0, 0);
            groups[period] = old with
            {
                Commits = old.Commits + 1, ServiceAdded = old.ServiceAdded + commit.ServiceAdded,
                ServiceDeleted = old.ServiceDeleted + commit.ServiceDeleted, TestAdded = old.TestAdded + commit.TestAdded,
                TestDeleted = old.TestDeleted + commit.TestDeleted, Added = old.Added + commit.Added, Deleted = old.Deleted + commit.Deleted,
                FilesAdded = old.FilesAdded + commit.FilesAdded, FilesModified = old.FilesModified + commit.FilesModified,
                FilesDeleted = old.FilesDeleted + commit.FilesDeleted
            };
        }
        return groups.Values.ToImmutableArray();
    }

    private static ImmutableArray<IncidentContributorGroup> BuildIncidentGroups(
        ImmutableArray<IncidentDocument> incidents, ReportIdentityCatalog catalog, CancellationToken token)
    {
        var active = incidents.Where(item => item.Entity.Record.Active).ToImmutableArray();
        var result = ImmutableArray.CreateBuilder<IncidentContributorGroup>();
        result.Add(new("Active", active, SeverityStatistics(active, token)));
        var groups = new Dictionary<string, (string Label, List<IncidentDocument> Items)>(StringComparer.Ordinal);
        foreach (var item in incidents)
        {
            token.ThrowIfCancellationRequested();
            var record = item.Entity.Record;
            if (!record.Mitigated && !record.Resolved) continue;
            var actor = record.ResolvedAt is not null ? record.ResolvedBy : record.MitigatedBy;
            var member = catalog.Get(actor);
            if (!groups.TryGetValue(member.Key, out var group))
            {
                group = (member.Label, []);
                groups.Add(member.Key, group);
            }
            group.Items.Add(item);
        }
        foreach (var group in groups.Values.OrderByDescending(group => group.Items.Count).ThenBy(group => group.Label, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var ordered = OrderIncidents(group.Items, token);
            result.Add(new(group.Label, ordered, SeverityStatistics(ordered, token)));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<IncidentSeverityStatistics> SeverityStatistics(IEnumerable<IncidentDocument> incidents, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<IncidentSeverityStatistics>();
        foreach (var group in incidents.GroupBy(item => item.Entity.Record.Severity).OrderBy(group => group.Key ?? int.MaxValue))
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(group.Key, group.Count(),
                DurationStatistics(group.Where(item => item.DetectHours.HasValue).Select(item => item.DetectHours!.Value), token),
                DurationStatistics(group.Where(item => item.MitigateHours.HasValue).Select(item => item.MitigateHours!.Value), token),
                DurationStatistics(group.Where(item => item.ResolveHours.HasValue).Select(item => item.ResolveHours!.Value), token)));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<IncidentDocument> OrderIncidents(IEnumerable<IncidentDocument> incidents, CancellationToken token) =>
        incidents.Select(item => { token.ThrowIfCancellationRequested(); return item; })
            .OrderBy(item => item.Entity.Record.Severity ?? int.MaxValue)
            .ThenBy(item => item.Entity.Record.Status == "Resolved" ? 0 : 1)
            .ThenByDescending(item => item.Entity.Record.ResolvedAt ?? item.Entity.Record.MitigatedAt ?? item.Entity.Record.ModifiedAt)
            .ThenBy(item => item.Entity.Record.EntityKey, StringComparer.Ordinal).ToImmutableArray();

    private static ImmutableArray<ReportEntity<T>> Merge<T, TKey>(
        ImmutableArray<T> records, Func<T, TKey> key, Func<T, string> source,
        Func<T, T, T> merge, Dictionary<string, ReportSource> sources, CancellationToken token) where TKey : notnull
    {
        var entities = new Dictionary<TKey, (T Record, HashSet<string> Sources)>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            var sourceKey = source(record);
            if (!sources.ContainsKey(sourceKey))
                throw new InvalidOperationException("A report record refers to a source that is not in the captured request.");
            var entityKey = key(record);
            if (entities.TryGetValue(entityKey, out var entity))
            {
                entity.Sources.Add(sourceKey);
                entities[entityKey] = (merge(entity.Record, record), entity.Sources);
            }
            else entities.Add(entityKey, (record, [sourceKey]));
        }
        return entities.Values.Select(entity =>
        {
            token.ThrowIfCancellationRequested();
            return new ReportEntity<T>(entity.Record, entity.Sources.Order(StringComparer.Ordinal).ToImmutableArray());
        }).ToImmutableArray();
    }

    private static bool Same(WorkItemReportRecord left, WorkItemReportRecord right, CancellationToken token) =>
        left with { SourceKey = "", History = [] } == right with { SourceKey = "", History = [] }
        && EqualSequence(left.History, right.History, token);
    private static bool Same(GitReportRecord left, GitReportRecord right, CancellationToken token) =>
        left with { SourceKey = "", Files = [] } == right with { SourceKey = "", Files = [] }
        && EqualSequence(left.Files, right.Files, token);
    private static bool EqualSequence<T>(ImmutableArray<T> left, ImmutableArray<T> right, CancellationToken token)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index])) return false;
        }
        return true;
    }
    private static IncidentReportRecord MergeIncident(IncidentReportRecord left, IncidentReportRecord right)
    {
        var normalizedLeft = left with { SourceKey = "", Active = false, Created = false, Mitigated = false, Resolved = false, Metadata = ImmutableDictionary<string, string>.Empty };
        var normalizedRight = right with { SourceKey = "", Active = false, Created = false, Mitigated = false, Resolved = false, Metadata = ImmutableDictionary<string, string>.Empty };
        if (normalizedLeft != normalizedRight || left.Metadata.Count != right.Metadata.Count
            || left.Metadata.Any(pair => !right.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value))
            return Conflict<IncidentReportRecord>(left.EntityKey);
        return left with
        {
            Active = left.Active || right.Active, Created = left.Created || right.Created,
            Mitigated = left.Mitigated || right.Mitigated, Resolved = left.Resolved || right.Resolved
        };
    }
    private static T Conflict<T>(string key) =>
        throw new InvalidOperationException($"Conflicting detail records for source-qualified entity {key}. Rerun the export to obtain a consistent snapshot.");

    private static bool ActiveState(string state) =>
        state.Equals("Committed", StringComparison.OrdinalIgnoreCase)
        || state.Equals("Active", StringComparison.OrdinalIgnoreCase)
        || state.Equals("In Progress", StringComparison.OrdinalIgnoreCase);
    private static bool InWindow(DateTimeOffset timestamp, ReportRequest request) => timestamp >= request.Start && timestamp <= request.End;
    private static double? NonnegativeHours(DateTimeOffset? end, DateTimeOffset? start) =>
        end is { } to && start is { } from && to >= from ? (to - from).TotalHours : null;
    private static string PairKey(string left, string right) => $"{left.Length}:{left}{right.Length}:{right}";
    internal static string TeamKey(IncidentReportRecord item) =>
        $"{ReportFormatting.Label(item.OwningTenant)}\\{ReportFormatting.Label(item.OwningTeam)} (silo {item.SiloId})";
    private static long Activity(ReportSummaryRow row) =>
        (long)row.WorkItems + row.Commits + row.PullRequests + row.Active + row.Created + row.Mitigated + row.Resolved + row.Contributed;

    private static IEnumerable<ReportIdentity> Identities(ReportSnapshot snapshot) =>
        snapshot.WorkItems.Select(item => item.Assignee)
            .Concat(snapshot.Commits.SelectMany(item => new[] { item.Author, item.Committer }))
            .Concat(snapshot.PullRequests.Select(item => item.CompletedBy))
            .Concat(snapshot.Incidents.SelectMany(item => new[] { item.Owner, item.MitigatedBy, item.ResolvedBy }))
            .Concat(snapshot.IncidentActions.Select(item => item.Actor));

    private static ImmutableArray<string> Notes(ReportRequest request)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        notes.Add("The export capture anchor is not an observation of individual earlier UI API call times. Effective export bounds govern fresh collection from the captured request; original per-call UI defaults may differ. Source-specific date interpretation is recorded in the coverage notes.");
        notes.Add("Per-source rows preserve overlapping selections. Overall rows deduplicate organization-qualified work items, repository-qualified commits/PRs, and silo-qualified incidents; they are not sums of source rows.");
        notes.Add(request.Analysis == "incidentManagement" && request.IsTeams
            ? "0 means no matching records in the available warehouse data, not proof of complete historical coverage. - means an absent label or unavailable duration. Unknown and unresolved identities are not guessed. Identity IDs/emails take precedence over unambiguous aliases/names."
            : "0 means a verified zero among successfully collected sources, not unavailable sources; skipped sources are excluded from activity rows and totals. - means an absent label or unavailable duration. Unknown and unresolved identities are not guessed. Identity IDs/emails take precedence over unambiguous aliases/names.");
        if (request.IsTeams) notes.Add("The captured manager/report roster is included, including members with no activity.");
        if (request.Analysis == "workItems")
            notes.Add("Work items are credited to the recorded assignee. Active work includes only Committed, Active, and In Progress states. Ongoing state periods use the shared as-of instant. As in the existing detail calculation, active-work analysis requires more than one history revision; a single revision is not treated as verified zero work. Time statistics include positive active-work durations and are calculated from distinct items, never averages of contributor averages.");
        if (request.Analysis == "gitCommits")
        {
            notes.Add("Commits and lines retain the existing author/committer-pair attribution. Different IDs/emails remain distinct even when names match. A pair is not two individual commit credits.");
            notes.Add("PRs Merged counts distinct completed PRs credited only to closedBy at closedDate in the captured period. Individual completion actors have their own rows; author, parent count, merge messages, and pair membership do not imply PR completion credit.");
            notes.Add("Git periods use the recorded committer-date calendar components, not author dates. Paths containing 'test' (case-insensitive) are test files; all other paths are service files. The existing trend's 'Pull Requests' data counts commits: this export labels it Commits and keeps authoritative PR evidence separate. Period totals include all data regardless of the chart/table toggle.");
        }
        if (request.Analysis == "incidentManagement")
        {
            notes.Add("Active is current ownership at the collection as-of instant, not in-period activity. Created credits the current owner of an in-period created incident, not its original creator. Mitigated and Resolved credit the recorded actors for the collector's current-status, in-period event memberships; a currently Resolved incident is not also counted as currently Mitigated.");
            notes.Add("Contributed counts distinct silo-qualified incidents per actor with recorded in-period actions, not actions, current ownership, or latest modification. Multiple aliases resolving to the same identity count once.");
            if (request.IsTeams)
                notes.Add("Teams actions without a warehouse HistoryId remain included when incident, actor, timestamp and action type are present. The Action ID is shown as Not provided; no HistoryId is invented. Only identical unkeyed evidence is deduplicated across source memberships. Existing source purge filters still apply; missing IDs do not establish independently verified history-purge completeness.");
            notes.Add("TTD = Created - Impact Start; TTM = Mitigated - Created when a mitigation exists; TTR = Resolved - Created for currently Resolved incidents. Only nonnegative durations enter statistics. Missing/negative durations are excluded, not zero. Overall statistics are weighted by distinct incidents. Closed contributor detail groups retain resolution-actor preference over mitigation actor; each group deduplicates incidents.");
        }
        return notes.ToImmutable();
    }

    private static void Validate(ReportSnapshot snapshot, CancellationToken token)
    {
        var budget = new ReportInputBudget(token);
        var request = snapshot.Request;
        if (request.Sources.IsDefault || request.Roster.IsDefault || request.RosterParentIndices.IsDefault || snapshot.WorkItems.IsDefault
            || snapshot.Commits.IsDefault || snapshot.PullRequests.IsDefault || snapshot.Incidents.IsDefault
            || snapshot.IncidentActions.IsDefault || snapshot.CoverageNotes.IsDefault || snapshot.SkippedSources.IsDefault)
            throw new InvalidOperationException("A report collection is uninitialized, rather than a verified empty collection.");
        var minimumRecords = (long)request.Sources.Length + request.Roster.Length
            + snapshot.WorkItems.Length + snapshot.Commits.Length + snapshot.PullRequests.Length
            + snapshot.Incidents.Length + snapshot.IncidentActions.Length + snapshot.CoverageNotes.Length + snapshot.SkippedSources.Length;
        if (minimumRecords > ReportRenderingLimits.MaximumRecords)
            throw new ReportSizeLimitException("The report exceeds the 200,000-record construction limit before detail expansion. Select a smaller scope; no records were omitted.");
        var availableSourceCount = request.Sources.Length - snapshot.SkippedSources.Length;
        if (request.IsTeams && availableSourceCount > 0
            && (long)request.Roster.Length * (availableSourceCount + 1L) > ReportRenderingLimits.MaximumSummaryRows)
            throw new ReportSizeLimitException("The captured roster/available-source combinations exceed the 100,000-row summary construction limit. Select fewer sources or members; no rows were omitted.");
        ValidateRosterHierarchy(request, token);
        budget.Record(request.Analysis, request.View, request.TimeZone, request.Manager, request.TrendPeriod,
            request.EffectiveStartLiteral, request.EffectiveEndLiteral, request.EffectiveBoundsOrigin);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            budget.Record(source.Key, source.Organization, source.Project, source.Scope, source.Branch, source.Team);
            if (string.IsNullOrWhiteSpace(source.Key) || !keys.Add(source.Key))
                throw new InvalidOperationException("Captured report sources must have unique, nonempty keys.");
        }
        foreach (var identity in request.Roster) budget.Identity(identity);
        foreach (var note in snapshot.CoverageNotes) budget.Record(note);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in snapshot.SkippedSources)
        {
            budget.Record(source.SourceKey, source.Reason);
            if (!keys.Contains(source.SourceKey) || !skipped.Add(source.SourceKey) || string.IsNullOrWhiteSpace(source.Reason))
                throw new InvalidOperationException("Skipped sources must identify unique captured source keys and a nonempty reason.");
        }
        foreach (var item in snapshot.WorkItems)
        {
            budget.Record(item.SourceKey, item.Organization, item.Title, item.Type, item.State, item.AreaPath, item.IterationPath, item.Url);
            budget.Identity(item.Assignee);
            if (item.History.IsDefault || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Organization))
                throw new InvalidOperationException("A work item has incomplete source identity or uninitialized history.");
            foreach (var state in item.History)
            {
                budget.Record(state.State);
                budget.Identity(state.Actor);
            }
        }
        foreach (var item in snapshot.Commits)
        {
            budget.Record(item.SourceKey, item.Organization, item.RepositoryId, item.Id, item.Message, item.Url);
            budget.Identity(item.Author);
            budget.Identity(item.Committer);
            if (item.Files.IsDefault || string.IsNullOrWhiteSpace(item.Organization)
                || string.IsNullOrWhiteSpace(item.RepositoryId) || string.IsNullOrWhiteSpace(item.Id))
                throw new InvalidOperationException("A commit has incomplete source identity or uninitialized changed files.");
            foreach (var file in item.Files)
            {
                budget.Record(file.Path, file.ChangeType);
                if (file.Added < 0 || file.Deleted < 0)
                    throw new InvalidOperationException($"Commit {item.EntityKey} has invalid negative file line counts.");
            }
        }
        foreach (var item in snapshot.PullRequests)
        {
            budget.Record(item.SourceKey, item.Organization, item.RepositoryId, item.Title, item.TargetBranch, item.Url);
            budget.Identity(item.CompletedBy);
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Organization) || string.IsNullOrWhiteSpace(item.RepositoryId))
                throw new InvalidOperationException("A completed PR has incomplete source identity.");
        }
        foreach (var item in snapshot.Incidents)
        {
            budget.Record(item.SourceKey, item.Id, item.SiloId, item.Title, item.Status,
                item.OwningTenant, item.OwningTeam, item.Mitigation, item.Url);
            budget.Identity(item.Owner);
            budget.Identity(item.MitigatedBy);
            budget.Identity(item.ResolvedBy);
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.SiloId))
                throw new InvalidOperationException("An incident has incomplete silo-qualified identity.");
            foreach (var pair in item.Metadata) budget.Record(pair.Key, pair.Value);
        }
        foreach (var item in snapshot.IncidentActions)
        {
            budget.Record(item.SourceKey, item.IncidentId, item.SiloId, item.ActionId, item.ActionType);
            budget.Identity(item.Actor);
            if (string.IsNullOrWhiteSpace(item.IncidentId) || string.IsNullOrWhiteSpace(item.SiloId)
                || item.ActionId is null || (item.ActionId.Length == 0 ? !request.IsTeams : string.IsNullOrWhiteSpace(item.ActionId))
                || string.IsNullOrWhiteSpace(item.ActionType))
                throw new InvalidOperationException("An incident action has incomplete source-qualified identity.");
        }
    }

    private static void ValidateRosterHierarchy(ReportRequest request, CancellationToken token)
    {
        var parents = request.RosterParentIndices;
        if (parents.IsEmpty) return;
        if (!request.IsTeams || parents.Length != request.Roster.Length || parents[0] is not null)
            throw new InvalidOperationException("Captured roster hierarchy must cover the complete roster and start with its manager.");
        var ancestors = new Stack<int>();
        ancestors.Push(0);
        for (var index = 1; index < parents.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (parents[index] is not { } parent || parent < 0 || parent >= index)
                throw new InvalidOperationException("Captured roster hierarchy contains an invalid manager reference.");
            while (ancestors.Count > 0 && ancestors.Peek() != parent) ancestors.Pop();
            if (ancestors.Count == 0)
                throw new InvalidOperationException("Captured roster hierarchy is not in the displayed parent/report order.");
            if (ancestors.Count > ReportRenderingLimits.MaximumRosterDepth)
                throw new ReportSizeLimitException("Captured roster hierarchy exceeds the export depth limit. No members were omitted.");
            ancestors.Push(index);
        }
    }

    private sealed class RowAccumulator(ReportSummaryRow value)
    {
        public ReportSummaryRow Value { get; set; } = value;
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ContributedIncidents { get; } = new(StringComparer.Ordinal);
    }
}
