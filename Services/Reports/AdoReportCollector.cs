using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using static ProductivityInsights.Services.Reports.AdoReportHttp;

namespace ProductivityInsights.Services.Reports;

public sealed partial class AdoReportCollector : IReportCollector
{
    private const int PageSize = 200;
    private const int WiqlPageSize = 2000;
    private readonly HttpClient client;
    private readonly Func<CancellationToken, Task<string>> tokenProvider;
    private readonly AdoReportCollectionLimits limits;

    public AdoReportCollector(HttpClient client, Func<CancellationToken, Task<string>> tokenProvider)
        : this(client, tokenProvider, new AdoReportCollectionLimits())
    {
    }

    public AdoReportCollector(HttpClient client, Func<CancellationToken, Task<string>> tokenProvider,
        AdoReportCollectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        this.client = client;
        this.tokenProvider = tokenProvider;
        this.limits = limits;
    }

    public async Task<ReportSnapshot> CollectAsync(
        ReportRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Analysis is not ("workItems" or "gitCommits"))
            throw Error(request.Analysis, "This collector supports only Work Items and Git Commits.");
        if (request.End < request.Start)
            throw Error(request.Analysis, "The effective end precedes the effective start.");
        if (request.Sources.IsDefaultOrEmpty)
            throw Error(request.Analysis, "No executed sources were captured.");
        if (request.IsTeams && request.Roster.IsDefaultOrEmpty)
            throw Error(request.Analysis, "The captured Teams roster is missing; activity cannot be attributed.");
        if (request.Sources.Select(source => source.Key).Distinct(StringComparer.Ordinal).Count() != request.Sources.Length)
            throw Error(request.Analysis, "Captured source keys are duplicated.");
        foreach (var source in request.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Key) || string.IsNullOrWhiteSpace(source.Organization)
                || string.IsNullOrWhiteSpace(source.Project))
                throw Error(source.Label, "A source key, organization, and project are required.");
        }

        var asOf = DateTimeOffset.UtcNow;
        var http = new AdoReportHttp(client, tokenProvider, limits);
        return request.Analysis == "workItems"
            ? await CollectWorkItemsAsync(http, request, asOf, progress, cancellationToken)
            : await CollectGitAsync(http, request, asOf, progress, cancellationToken);
    }

    private async Task<ReportSnapshot> CollectWorkItemsAsync(
        AdoReportHttp http, ReportRequest request, DateTimeOffset asOf, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var records = ImmutableArray.CreateBuilder<WorkItemReportRecord>();
        var cache = new Dictionary<string, WorkItemReportRecord>(StringComparer.OrdinalIgnoreCase);
        var start = WorkItemDateLiteral(request.EffectiveStartLiteral, request.Start, "start");
        var end = WorkItemDateLiteral(request.EffectiveEndLiteral, request.End, "end");
        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = $"Work Items {source.Label}";
            var area = string.IsNullOrEmpty(source.Scope) ? $"{source.Project}\\{source.Team}" : source.Scope;
            if (string.IsNullOrWhiteSpace(source.Scope) && string.IsNullOrWhiteSpace(source.Team))
                throw Error(context, "Area path or team is required.");
            progress?.Report($"Collecting {context}");
            var prefix = $"https://dev.azure.com/{Escape(source.Organization)}";
            var team = string.IsNullOrEmpty(source.Team) ? "" : $"/{Escape(source.Team)}";
            var wiqlUrl = $"{prefix}/{Escape(source.Project)}{team}/_apis/wit/wiql?$top={WiqlPageSize}&api-version=7.1";
            var ids = new List<int>();
            var lastId = 0;
            while (true)
            {
                var query = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Quote(source.Project)}'"
                    + $" AND [System.AreaPath] UNDER '{Quote(area)}' AND [System.State] = 'Done'"
                    + $" AND [Microsoft.VSTS.Common.StateChangeDate] >= '{start}'"
                    + $" AND [Microsoft.VSTS.Common.StateChangeDate] <= '{end}' AND [System.Id] > {lastId}"
                    + $" ORDER BY [System.Id] ASC ASOF '{asOf.UtcDateTime:O}'";
                var page = await http.JsonAsync(HttpMethod.Post, wiqlUrl, new { query },
                    $"{context}, WIQL after ID {lastId}", cancellationToken);
                var items = Array(page, "workItems", context);
                if (items.GetArrayLength() > WiqlPageSize)
                    throw Error(context, "WIQL exceeded its requested bounded page size.");
                foreach (var item in items.EnumerateArray())
                {
                    var id = Integer(item, "id", context, 1);
                    if (id <= lastId)
                        throw Error(context, $"WIQL ID pagination did not advance at work item {id}.");
                    ids.Add(id);
                    lastId = id;
                }
                if (items.GetArrayLength() == 0)
                    break;
            }

            foreach (var chunk in ids.Chunk(PageSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var missing = chunk.Where(id => !cache.ContainsKey($"{source.Organization}:{id}")).ToArray();
                if (missing.Length != 0)
                {
                    var batchContext = $"{context}, detail IDs {string.Join(",", missing)}";
                    var page = await http.JsonAsync(HttpMethod.Post,
                        $"{prefix}/_apis/wit/workitemsbatch?api-version=7.1",
                        new
                        {
                            ids = missing,
                            fields = WorkItemFields,
                            asOf = asOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                            errorPolicy = "Fail"
                        }, batchContext, cancellationToken);
                    var items = Array(page, "value", batchContext);
                    var expected = missing.ToHashSet();
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = Integer(item, "id", batchContext, 1);
                        if (!expected.Remove(id))
                            throw Error(batchContext, $"Unexpected or duplicate work item ID {id}.");
                        var itemContext = $"{context}, work item {id}";
                        var record = ReadWorkItem(item, source, itemContext);
                        var revision = Integer(item, "rev", itemContext, 1);
                        if (record.ChangedAt > asOf)
                            throw Error(itemContext, "Detail is newer than the captured as-of instant.");
                        if (!string.Equals(record.State, "Done", StringComparison.OrdinalIgnoreCase))
                            throw Error(itemContext, "Detail no longer satisfies the Done state query.");
                        if (ReportIdentityMatcher.Matches(record.Assignee, request))
                        {
                            record = record with
                            {
                                History = await CollectHistoryAsync(http, prefix, id, revision, asOf, itemContext, cancellationToken)
                            };
                        }
                        cache.Add($"{source.Organization}:{id}", record);
                    }
                    if (expected.Count != 0)
                        throw Error(batchContext, $"Required detail IDs were omitted: {string.Join(",", expected.Order())}.");
                }
                foreach (var id in chunk)
                {
                    var record = cache[$"{source.Organization}:{id}"];
                    if (ReportIdentityMatcher.Matches(record.Assignee, request))
                        records.Add(record with { SourceKey = source.Key });
                }
            }
        }
        return new ReportSnapshot
        {
            Request = request,
            AsOf = asOf,
            WorkItems = records.ToImmutable(),
            CoverageNotes =
            [
                $"Work-item selection: State = Done; StateChangeDate >= '{start}' and <= '{end}', matching the legacy inclusive date-only WIQL predicates (no client-side time-zone conversion or end-of-day expansion). Captured request time basis: {request.TimeZone}; Azure DevOps interprets the date-only literals.",
                request.EffectiveBoundsOrigin ?? "Effective date literals are reconstructed from the captured wall-clock bounds; original API default-evaluation timestamps are not inferred from UI caches.",
                $"WIQL and work-item details are pinned to {asOf:O}. Histories include every revision through the as-of detail revision; ongoing durations use this same instant.",
                "Work items are attributed to AssignedTo, matching the existing detail grouping, not to the actor who changed state. State transitions retain their actual actors.",
                "ClosedDate is optional exported metadata for completion duration, not a selection filter. Source memberships are preserved; overall totals must deduplicate organization-qualified work item IDs."
            ]
        };
    }

    private static readonly string[] WorkItemFields =
    [
        "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.AssignedTo",
        "System.CreatedDate", "System.ChangedDate", "Microsoft.VSTS.Common.ClosedDate",
        "Microsoft.VSTS.Common.Priority", "System.AreaPath", "System.IterationPath"
    ];

    private static string WorkItemDateLiteral(string? captured, DateTimeOffset fallback, string bound)
    {
        var literal = captured ?? fallback.DateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        if (literal.Length != 10 || literal[4] != '-' || literal[7] != '-'
            || literal.Where((_, index) => index is not (4 or 7)).Any(character => !char.IsAsciiDigit(character)))
            throw Error("Work Items", $"The captured effective {bound} must be a date-only yyyy-MM-dd WIQL literal.");
        return literal;
    }

    private static WorkItemReportRecord ReadWorkItem(JsonElement item, ReportSource source, string context)
    {
        var fields = Object(item, "fields", context);
        var id = Integer(item, "id", context, 1);
        var assignee = fields.TryGetProperty("System.AssignedTo", out var assigned)
            ? Identity(assigned, context, "Unassigned") : new ReportIdentity("Unassigned");
        return new WorkItemReportRecord
        {
            SourceKey = source.Key,
            Organization = source.Organization,
            Id = id,
            Title = Text(fields, "System.Title", context, allowEmpty: true),
            Type = Text(fields, "System.WorkItemType", context),
            State = Text(fields, "System.State", context),
            Assignee = assignee,
            CreatedAt = Date(fields, "System.CreatedDate", context),
            ChangedAt = Date(fields, "System.ChangedDate", context),
            ClosedAt = fields.TryGetProperty("Microsoft.VSTS.Common.ClosedDate", out var closed)
                && closed.ValueKind != JsonValueKind.Null ? Date(fields, "Microsoft.VSTS.Common.ClosedDate", context) : null,
            Priority = fields.TryGetProperty("Microsoft.VSTS.Common.Priority", out var priority)
                && priority.ValueKind != JsonValueKind.Null ? Integer(fields, "Microsoft.VSTS.Common.Priority", context) : null,
            AreaPath = Text(fields, "System.AreaPath", context),
            IterationPath = OptionalText(fields, "System.IterationPath", context) ?? "",
            Url = $"https://dev.azure.com/{Escape(source.Organization)}/{Escape(source.Project)}/_workitems/edit/{id}"
        };
    }

    private async Task<ImmutableArray<WorkItemStateRecord>> CollectHistoryAsync(
        AdoReportHttp http, string prefix, int id, int expectedRevision, DateTimeOffset asOf, string context,
        CancellationToken cancellationToken)
    {
        var history = ImmutableArray.CreateBuilder<WorkItemStateRecord>();
        var next = 1;
        DateTimeOffset? previousDate = null;
        while (next <= expectedRevision)
        {
            var pageContext = $"{context}, revisions after {next - 1}";
            var page = await http.JsonAsync(HttpMethod.Get,
                $"{prefix}/_apis/wit/workitems/{id}/revisions?$skip={next - 1}&$top={PageSize}&$expand=fields&api-version=7.1",
                null, pageContext, cancellationToken);
            var revisions = Array(page, "value", pageContext);
            if (revisions.GetArrayLength() == 0)
                throw Error(pageContext, $"History is incomplete: expected revision {next} through {expectedRevision}.");
            foreach (var revision in revisions.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rev = Integer(revision, "rev", pageContext, 1);
                if (rev != next)
                    throw Error(pageContext, $"History is not contiguous: expected revision {next}, received {rev}.");
                if (Integer(revision, "id", pageContext, 1) != id)
                    throw Error(pageContext, "History belongs to a different work item.");
                var fields = Object(revision, "fields", pageContext);
                var date = Date(fields, "System.ChangedDate", pageContext);
                if (date > asOf || (previousDate.HasValue && date < previousDate))
                    throw Error(pageContext, $"Revision {rev} has an inconsistent changed timestamp.");
                var state = Text(fields, "System.State", pageContext);
                var actor = fields.TryGetProperty("System.ChangedBy", out var changedBy)
                    ? Identity(changedBy, pageContext) : new ReportIdentity("Unknown");
                history.Add(new WorkItemStateRecord(state, date, actor));
                previousDate = date;
                next++;
                if (next > expectedRevision)
                    break;
            }
        }
        return history.ToImmutable();
    }

    private static ReportIdentity Identity(JsonElement value, string context, string unknown = "Unknown")
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new ReportIdentity(unknown);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var open = text.LastIndexOf('<');
            if (open >= 0 && text.EndsWith('>'))
                return new ReportIdentity(text[..open].Trim(), text[(open + 1)..^1].Trim());
            return new ReportIdentity(string.IsNullOrWhiteSpace(text) ? unknown : text,
                text.Contains('@') ? text : null);
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw Error(context, "An identity must be an object or a string.");
        return new ReportIdentity(
            OptionalText(value, "displayName", context) ?? OptionalText(value, "name", context) ?? unknown,
            OptionalText(value, "email", context),
            OptionalText(value, "id", context),
            OptionalText(value, "uniqueName", context) ?? OptionalText(value, "principalName", context));
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
