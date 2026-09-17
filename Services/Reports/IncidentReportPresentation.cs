using System.Collections.Immutable;
using System.Globalization;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal sealed class IncidentReportPresentation
{
    public sealed record SourceGroup(ImmutableArray<string> SourceKeys, ImmutableArray<IncidentDocument> Incidents);
    public sealed record ContributedIncident(IncidentDocument Incident, ImmutableArray<ReportEntity<IncidentActionReportRecord>> Actions);
    public sealed record MemberActivityDetails(ImmutableArray<IncidentDocument> Created, ImmutableArray<ContributedIncident> Contributed);

    private readonly ReportDocument document;
    private readonly CancellationToken token;
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, int> sourceOrder;
    private readonly ReportIdentityCatalog catalog;
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly ILookup<string, ReportEntity<IncidentActionReportRecord>> actions;
    private readonly long[] totals = new long[5];

    public IncidentReportPresentation(ReportDocument document, CancellationToken token)
    {
        this.document = document;
        this.token = token;
        token.ThrowIfCancellationRequested();
        var request = document.Snapshot.Request;
        sources = request.Sources.ToDictionary(source => source.Key, StringComparer.Ordinal);
        sourceOrder = request.Sources.Select((source, index) => (source.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        var identities = request.Roster.Concat(document.Incidents.SelectMany(item =>
            new[] { item.Entity.Record.Owner, item.Entity.Record.MitigatedBy, item.Entity.Record.ResolvedBy }))
            .Concat(document.IncidentActions.Select(action => action.Record.Actor)).ToImmutableArray();
        catalog = new ReportIdentityCatalog(request, identities, token);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var resolved = catalog.Get(identity);
            names.TryAdd(resolved.Key, resolved.Key.StartsWith("UNRESOLVED:", StringComparison.Ordinal)
                ? resolved.Label : identity.DisplayName);
            labels.TryAdd(resolved.Key, resolved.Label);
        }
        var ambiguous = names.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in names.Keys.ToArray())
            if (ambiguous.Contains(names[key])) names[key] = labels[key];
        OverallRows = document.OverallRows.OrderByDescending(Activity)
            .ThenBy(Contributor, StringComparer.Ordinal).ToImmutableArray();
        var byMember = document.SourceRows.ToLookup(row => row.ContributorKey, StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<ReportSummaryRow>();
        foreach (var member in OverallRows)
        {
            token.ThrowIfCancellationRequested();
            totals[0] += member.Active;
            totals[1] += member.Created;
            totals[2] += member.Mitigated;
            totals[3] += member.Resolved;
            totals[4] += member.Contributed;
            if (Activity(member) == 0)
                rows.Add(member);
            else
                rows.AddRange(byMember[member.ContributorKey].Where(row => Activity(row) != 0)
                    .OrderByDescending(Activity).ThenBy(Path, StringComparer.Ordinal));
        }
        SourceRows = rows.ToImmutable();
        actions = document.IncidentActions.ToLookup(action => action.Record.IncidentKey, StringComparer.Ordinal);
    }

    public ImmutableArray<ReportSummaryRow> SourceRows { get; }
    public ImmutableArray<ReportSummaryRow> OverallRows { get; }
    public string Contributor(ReportSummaryRow row) => names.GetValueOrDefault(row.ContributorKey, row.Contributor);
    public string Path(ReportSummaryRow row) => row.SourceKey is null ? "\u2014" : sources[row.SourceKey].Label;
    public string MemberKey(ReportIdentity identity) => catalog.Get(identity).Key;

    public ImmutableDictionary<string, MemberActivityDetails> MemberActivityDetailsByKey()
    {
        token.ThrowIfCancellationRequested();
        var incidents = document.Incidents.ToDictionary(item => item.Entity.Record.EntityKey, StringComparer.Ordinal);
        var created = document.Incidents.Where(item => item.Entity.Record.Created)
            .ToLookup(item => MemberKey(item.Entity.Record.Owner), StringComparer.Ordinal);
        var contributions = new Dictionary<string, ImmutableArray<ContributedIncident>>(StringComparer.Ordinal);
        foreach (var member in document.IncidentActions.GroupBy(action => MemberKey(action.Record.Actor), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var items = ImmutableArray.CreateBuilder<ContributedIncident>();
            foreach (var group in member.GroupBy(action =>
            {
                token.ThrowIfCancellationRequested();
                return action.Record.IncidentKey;
            }, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                items.Add(new(incidents[group.Key], group.ToImmutableArray()));
            }
            contributions.Add(member.Key, items.ToImmutable());
        }
        var result = ImmutableDictionary.CreateBuilder<string, MemberActivityDetails>(StringComparer.Ordinal);
        foreach (var member in document.OverallRows)
        {
            token.ThrowIfCancellationRequested();
            var memberCreated = created[member.ContributorKey].ToImmutableArray();
            var memberContributed = contributions.GetValueOrDefault(member.ContributorKey, []);
            if (memberCreated.Length != member.Created || memberContributed.Length != member.Contributed)
                throw new InvalidOperationException("Incident member Created/Contributed details do not match the report's contributor totals.");
            result.Add(member.ContributorKey, new(memberCreated, memberContributed));
        }
        return result.ToImmutable();
    }

    private static long Activity(ReportSummaryRow row) =>
        (long)row.Active + row.Created + row.Mitigated + row.Resolved + row.Contributed;

    public static string[] Headers(bool bySource) => bySource
        ? ["\U0001F464 Team Member", "\U0001F4C2 ICM Path", "\U0001F7E2 Active", "\U0001F195 Created",
            "\U0001F527 Mitigated", "\u2705 Resolved", "\U0001F4AC Contributed"]
        : ["\U0001F464 Team Member", "\U0001F7E2 Active", "\U0001F195 Created",
            "\U0001F527 Mitigated", "\u2705 Resolved", "\U0001F4AC Contributed"];

    public string[] Cells(ReportSummaryRow row, bool bySource)
    {
        token.ThrowIfCancellationRequested();
        var counts = new[] { row.Active, row.Created, row.Mitigated, row.Resolved, row.Contributed }
            .Select(value => ReportFormatting.Number(value)).ToArray();
        return bySource ? [Contributor(row), Path(row), .. counts] : [Contributor(row), .. counts];
    }

    public string[] TotalCells() => ["\U0001F3C1 TOTAL", .. totals.Select(ReportFormatting.Number)];

    public IEnumerable<SourceGroup> Groups()
    {
        var groups = document.Incidents.GroupBy(item =>
            ReportFormatting.Join(",", item.Entity.SourceKeys.Select(key => ReportFormatting.Number(sourceOrder[key])), token),
            StringComparer.Ordinal);
        foreach (var group in groups.OrderBy(group => group.First().Entity.SourceKeys.Min(key => sourceOrder[key]))
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            yield return new(group.First().Entity.SourceKeys, group.ToImmutableArray());
        }
    }

    public string SourceCaption(ImmutableArray<string> keys) =>
        ReportFormatting.Join("; ", keys.OrderBy(key => sourceOrder[key]).Select(key => sources[key].Label), token);

    public ImmutableArray<string> Contributors(IncidentDocument incident) => actions[incident.Entity.Record.EntityKey]
        .Select(action => catalog.Get(action.Record.Actor).Key).Distinct(StringComparer.Ordinal)
        .Select(key => names[key]).Order(StringComparer.Ordinal).ToImmutableArray();

    public static string Date(DateTimeOffset? value) => value?.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture) ?? "-";

    public static string Duration(double? hours)
    {
        if (hours is null) return "-";
        var span = TimeSpan.FromHours(hours.Value);
        return span.Days > 0 ? $"{ReportFormatting.Number(span.Days)}d {ReportFormatting.Number(span.Hours)}h"
            : span.Hours > 0 ? $"{ReportFormatting.Number(span.Hours)}h {ReportFormatting.Number(span.Minutes)}m"
            : $"{ReportFormatting.Number(span.Minutes)}m";
    }
}
