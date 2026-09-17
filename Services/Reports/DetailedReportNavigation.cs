using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal sealed class DetailedReportNavigation
{
    private readonly ReportRequest request;
    private readonly CancellationToken token;
    private readonly ReportIdentityCatalog identities;
    private readonly Dictionary<string, string> memberReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sourceReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, string> teamSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> destinations = new(StringComparer.Ordinal);
    private readonly List<(int Position, string Id)> targets = [];

    internal DetailedReportNavigation(ReportDocument document, CancellationToken token)
    {
        request = document.Snapshot.Request;
        this.token = token;
        sources = request.Sources.ToDictionary(source => source.Key, StringComparer.Ordinal);
        identities = new ReportIdentityCatalog(request, request.Roster, token);
        foreach (var member in request.Roster)
            memberReferences.TryAdd(identities.Get(member).Key, $"#report-member-reference-{memberReferences.Count}");
        if (request.Analysis == "incidentManagement")
        {
            foreach (var source in request.Sources)
            {
                token.ThrowIfCancellationRequested();
                sourceReferences.Add(source.Key, $"#report-source-reference-{sourceReferences.Count}");
            }
            foreach (var incident in document.Incidents.Where(incident => incident.Entity.Record.Active))
            {
                token.ThrowIfCancellationRequested();
                teamSources.TryAdd(ReportDocumentBuilder.TeamKey(incident.Entity.Record), incident.Entity.SourceKeys[0]);
            }
        }
    }

    internal string MemberLink(ReportIdentity member) => memberReferences[identities.Get(member).Key];
    internal string? SourceLink(string? key) => key is null ? null : sourceReferences.GetValueOrDefault(key);

    internal string? PathLink(string path, IEnumerable<string>? sourceKeys = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (sourceKeys is null) return SourceLink(teamSources.GetValueOrDefault(path));
        var memberships = sourceKeys.Select(key => sources[key]).ToArray();
        var source = memberships.FirstOrDefault(source => source.Scope == path || source.Label == path)
            ?? memberships.FirstOrDefault();
        return SourceLink(source?.Key);
    }

    internal void MemberSection(int position, string? contributorKey = null, IEnumerable<ReportIdentity>? actors = null)
    {
        var references = new List<string>();
        if (contributorKey is not null && memberReferences.TryGetValue(contributorKey, out var reference))
            references.Add(reference);
        if (actors is not null)
            foreach (var actor in actors)
            {
                token.ThrowIfCancellationRequested();
                if (memberReferences.TryGetValue(identities.Get(actor).Key, out var actorReference))
                    references.Add(actorReference);
            }
        Target(position, references, "member");
    }

    internal void SourceSection(int position, IEnumerable<string> sourceKeys) =>
        Target(position, sourceKeys.Select(SourceLink).OfType<string>(), "icm");

    private void Target(int position, IEnumerable<string> references, string kind)
    {
        var id = $"report-{kind}-{targets.Count}";
        var added = false;
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (destinations.TryAdd(reference, id)) added = true;
        }
        if (added) targets.Add((position, id));
    }

    internal IEnumerable<ReportIdentity> UnlinkedMembers() =>
        request.Roster.Where(member => !destinations.ContainsKey(MemberLink(member)));

    internal IEnumerable<ReportSource> UnlinkedSources() =>
        request.Sources.Where(source => SourceLink(source.Key) is { } reference && !destinations.ContainsKey(reference));

    internal void Apply(MarkdownDocument body)
    {
        // Positions are recorded by the writer, not guessed from possibly duplicated display names.
        var blocks = new List<Block>();
        foreach (var node in body.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (node is Block block && (block.Parent is MarkdownDocument || block is TableRow))
                blocks.Add(block);
        }
        blocks.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));
        var index = 0;
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            while (index < blocks.Count && blocks[index].Span.Start < target.Position) index++;
            if (index == blocks.Count || blocks[index].Span.Start > target.Position + 1)
                throw new InvalidOperationException("The detailed report navigation target could not be located.");
            var attributes = blocks[index].GetAttributes();
            if (attributes.Id is not null)
                throw new InvalidOperationException("The detailed report contains overlapping navigation targets.");
            attributes.Id = target.Id;
            attributes.AddProperty("tabindex", "-1");
        }
        foreach (var link in body.Descendants<LinkInline>())
        {
            token.ThrowIfCancellationRequested();
            if (link.Url is not { } reference || !reference.StartsWith("#report-", StringComparison.Ordinal)) continue;
            if (!destinations.TryGetValue(reference, out var id))
                throw new InvalidOperationException("The detailed report contains an unresolved navigation link.");
            link.Url = "#" + id;
            link.GetAttributes().AddClass(reference.StartsWith("#report-member-", StringComparison.Ordinal)
                ? "report-member-link" : "report-icm-link");
        }
    }
}
