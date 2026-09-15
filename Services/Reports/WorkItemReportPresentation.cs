using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal sealed class WorkItemReportPresentation
{
    public sealed record AreaRow(ReportSummaryRow Summary, string Area);
    public sealed record SourceGroup(ImmutableArray<string> SourceKeys, ImmutableArray<WorkItemDocument> Items);
    private readonly record struct AreaKey(string Organization, string Project, string Path);

    private readonly CancellationToken token;
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, int> sourceOrder;
    private readonly Dictionary<string, string> contributorNames;
    private readonly HashSet<string> ambiguousTeams;
    private readonly ILookup<string, WorkItemDocument> itemsByContributor;

    public WorkItemReportPresentation(ReportDocument document, CancellationToken token)
    {
        this.token = token;
        token.ThrowIfCancellationRequested();
        var request = document.Snapshot.Request;
        sources = request.Sources.ToDictionary(source => source.Key, StringComparer.Ordinal);
        sourceOrder = request.Sources.Select((source, index) => (source.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        contributorNames = ContributorNames(document);
        ambiguousTeams = request.Sources.Where(source => !string.IsNullOrWhiteSpace(source.Team))
            .GroupBy(source => source.Team!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(source => (source.Organization.ToUpperInvariant(), source.Project.ToUpperInvariant()))
                .Distinct().Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        OverallRows = document.OverallRows.OrderByDescending(row => row.WorkItems)
            .ThenBy(Contributor, StringComparer.Ordinal).ToImmutableArray();
        itemsByContributor = document.WorkItems.ToLookup(item => item.ContributorKey, StringComparer.Ordinal);

        var counts = new Dictionary<(string Member, AreaKey Area), ReportSummaryRow>();
        var areas = new Dictionary<AreaKey, (string Organization, string Project, string Path)>();
        foreach (var item in document.WorkItems)
        {
            token.ThrowIfCancellationRequested();
            var record = item.Entity.Record;
            var source = sources[record.SourceKey];
            var path = string.IsNullOrWhiteSpace(record.AreaPath) ? "" : record.AreaPath;
            var area = new AreaKey(record.Organization.ToUpperInvariant(), source.Project.ToUpperInvariant(), path.ToUpperInvariant());
            areas.TryAdd(area, (record.Organization, source.Project, path));
            var key = (item.ContributorKey, area);
            if (!counts.TryGetValue(key, out var row))
            {
                CheckRowCount(counts.Count);
                row = new ReportSummaryRow { ContributorKey = item.ContributorKey, Contributor = item.Contributor };
            }
            counts[key] = row with
            {
                WorkItems = row.WorkItems + 1,
                Bugs = row.Bugs + (record.Type == "Bug" ? 1 : 0),
                Tasks = row.Tasks + (record.Type == "Task" ? 1 : 0),
                ProductBacklogItems = row.ProductBacklogItems + (record.Type == "Product Backlog Item" ? 1 : 0),
                Other = row.Other + (record.Type is not ("Bug" or "Task" or "Product Backlog Item") ? 1 : 0)
            };
        }
        var ambiguousAreas = areas.Keys.GroupBy(area => area.Path, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var labels = new Dictionary<AreaKey, string>();
        var budget = new ReportInputBudget(token);
        foreach (var (key, area) in areas)
        {
            var path = area.Path.Length == 0 ? "Unknown area path" : area.Path;
            if (ambiguousAreas.Contains(key.Path))
            {
                budget.Record(area.Organization, "/", area.Project, "/", path);
                labels[key] = $"{area.Organization}/{area.Project}/{path}";
            }
            else
            {
                budget.Record(path);
                labels[key] = path;
            }
        }
        var byMember = counts.ToLookup(pair => pair.Key.Member, StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<AreaRow>();
        foreach (var member in OverallRows)
        {
            token.ThrowIfCancellationRequested();
            if (member.WorkItems == 0)
            {
                CheckRowCount(rows.Count);
                rows.Add(new(member, "\u2014"));
            }
            else
                foreach (var area in byMember[member.ContributorKey].OrderByDescending(pair => pair.Value.WorkItems)
                    .ThenBy(pair => labels[pair.Key.Area], StringComparer.Ordinal))
                {
                    CheckRowCount(rows.Count);
                    rows.Add(new(area.Value, labels[area.Key.Area]));
                }
        }
        AreaRows = rows.ToImmutable();
    }

    public ImmutableArray<ReportSummaryRow> OverallRows { get; }
    public ImmutableArray<AreaRow> AreaRows { get; }
    public string Contributor(ReportSummaryRow row) => contributorNames[row.ContributorKey];

    public static string[] Headers(bool byArea) => byArea
        ? ["\U0001F464 Team Member", "\U0001F4C2 Area Path", "\U0001F4CB Total Items", "\U0001F41E Bugs",
            "\U0001F527 Tasks", "\U0001F4E6 Product Backlog Items", "\U0001F9E9 Other"]
        : ["\U0001F464 Team Member", "\U0001F4CB Total Items", "\U0001F41E Bugs", "\U0001F527 Tasks",
            "\U0001F4E6 Product Backlog Items", "\U0001F9E9 Other", "\U0001F465 Teams"];

    private static string[] Counts(ReportSummaryRow row) =>
        [ReportFormatting.Number(row.WorkItems), ReportFormatting.Number(row.Bugs), ReportFormatting.Number(row.Tasks),
            ReportFormatting.Number(row.ProductBacklogItems), ReportFormatting.Number(row.Other)];

    public string[] Cells(AreaRow row) => [Contributor(row.Summary), row.Area, .. Counts(row.Summary)];
    public string[] Cells(ReportSummaryRow row) => [Contributor(row), .. Counts(row), Teams(row)];

    private string Teams(ReportSummaryRow row) => ReportFormatting.Join(", ", row.SourceKeys.Select(key => sources[key])
        .Where(source => !string.IsNullOrWhiteSpace(source.Team))
        .Select(source => ambiguousTeams.Contains(source.Team!)
            ? ReportFormatting.Join("/", [source.Organization, source.Project, source.Team!], token) : source.Team!)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal), token);

    public IEnumerable<SourceGroup> Groups(ReportSummaryRow member)
    {
        var groups = itemsByContributor[member.ContributorKey].GroupBy(item =>
            ReportFormatting.Join(",", item.Entity.SourceKeys.Select(key => ReportFormatting.Number(sourceOrder[key])), token),
            StringComparer.Ordinal);
        foreach (var group in groups.OrderBy(group => group.First().Entity.SourceKeys.Min(key => sourceOrder[key]))
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            yield return new(group.First().Entity.SourceKeys, group.OrderBy(CompletionDate)
                .ThenBy(item => item.Entity.Record.Id).ToImmutableArray());
        }
    }

    public string SourceCaption(ImmutableArray<string> keys) => ReportFormatting.Join("; ",
        keys.OrderBy(key => sourceOrder[key]).Select(key =>
        {
            var source = sources[key];
            return string.IsNullOrWhiteSpace(source.Team)
                ? ReportFormatting.Join("/", [source.Organization, source.Project, source.Scope], token)
                : ReportFormatting.Join("", [source.Organization, "/", source.Project, "/", source.Team, " (", source.Scope, ")"], token);
        }), token);

    private static DateTimeOffset CompletionDate(WorkItemDocument item) =>
        item.Periods.LastOrDefault(period => period.State.Equals("Done", StringComparison.OrdinalIgnoreCase))?.EnteredAt
        ?? item.Entity.Record.ClosedAt ?? item.Entity.Record.ChangedAt;

    private Dictionary<string, string> ContributorNames(ReportDocument document)
    {
        var identities = document.Snapshot.Request.Roster.Concat(document.WorkItems.Select(item => item.Entity.Record.Assignee))
            .ToImmutableArray();
        var catalog = new ReportIdentityCatalog(document.Snapshot.Request, identities, token);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            token.ThrowIfCancellationRequested();
            var resolved = catalog.Get(identity);
            names.TryAdd(resolved.Key, resolved.Key.StartsWith("UNRESOLVED:", StringComparison.Ordinal)
                ? resolved.Label : identity.DisplayName);
        }
        var result = document.OverallRows.ToDictionary(row => row.ContributorKey,
            row => names.GetValueOrDefault(row.ContributorKey, row.Contributor), StringComparer.Ordinal);
        var ambiguous = result.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.OverallRows)
            if (ambiguous.Contains(result[row.ContributorKey])) result[row.ContributorKey] = row.Contributor;
        return result;
    }

    private void CheckRowCount(int count)
    {
        token.ThrowIfCancellationRequested();
        if (count >= ReportRenderingLimits.MaximumSummaryRows)
            throw new ReportSizeLimitException("The work item area summary exceeds the 100,000-row construction limit. Select a smaller scope; no areas were omitted.");
    }
}
