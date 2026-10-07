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
    private readonly Dictionary<string, string> incidentReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, string> teamSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> destinations = new(StringComparer.Ordinal);
    private readonly List<(int Position, string Id)> targets = [];
    private readonly List<(int Start, int End)> personSections = [];
    private readonly List<(int Start, int End)> itemSections = [];
    private readonly Dictionary<int, ReadOnlyMemory<char>> itemHeadingTitles = [];
    private readonly List<(int Start, int End)> contextSections = [];
    private readonly List<(int Start, int End)> queueSections = [];
    private readonly List<(int Start, int End)> ownerSections = [];
    private readonly List<(int Position, ReadOnlyMemory<char> Title, int Level)> recordHeadings = [];
    private int? rosterHierarchyPosition;

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
            foreach (var incident in document.Incidents)
            {
                token.ThrowIfCancellationRequested();
                incidentReferences.Add(incident.Entity.Record.EntityKey, $"#report-incident-reference-{incidentReferences.Count}");
            }
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
    internal void RosterHierarchy(int position) => rosterHierarchyPosition = position;
    internal void PersonSection(int start, int end)
    {
        if (request.IsTeams) personSections.Add((start, end));
    }
    internal void ItemSection(int start, int end, ReadOnlyMemory<char>? headingTitle = null)
    {
        if (request.IsTeams)
        {
            itemSections.Add((start, end));
            if (headingTitle is { } title) itemHeadingTitles.Add(start, title);
        }
    }
    internal void ContextSection(int start, int end) => contextSections.Add((start, end));
    internal void QueueSection(int start, int end) => queueSections.Add((start, end));
    internal void OwnerSection(int start, int end) => ownerSections.Add((start, end));
    internal void RecordHeading(int position, ReadOnlyMemory<char> title, int level) => recordHeadings.Add((position, title, level));
    internal string? SourceLink(string? key) => key is null ? null : sourceReferences.GetValueOrDefault(key);
    internal string IncidentLink(IncidentDocument incident) => incidentReferences[incident.Entity.Record.EntityKey];
    internal void IncidentSection(int position, IncidentDocument incident) =>
        Target(position, [IncidentLink(incident)], "incident");

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
        ApplySections(body, personSections, DetailedReportPresentation.PersonSectionStartKey, DetailedReportPresentation.PersonSectionEndKey, 3);
        ApplySections(body, itemSections, DetailedReportPresentation.ItemSectionStartKey, DetailedReportPresentation.ItemSectionEndKey, 4, 5);
        ApplySections(body, contextSections, DetailedReportPresentation.ContextSectionStartKey, DetailedReportPresentation.ContextSectionEndKey, 2);
        ApplySections(body, queueSections, DetailedReportPresentation.QueueSectionStartKey, DetailedReportPresentation.QueueSectionEndKey, 4);
        ApplySections(body, ownerSections, DetailedReportPresentation.OwnerSectionStartKey, DetailedReportPresentation.OwnerSectionEndKey, 3, 5);
        var headingIndex = 0;
        foreach (var heading in recordHeadings)
        {
            token.ThrowIfCancellationRequested();
            while (headingIndex < body.Count && body[headingIndex].Span.Start < heading.Position) headingIndex++;
            if (headingIndex == body.Count || body[headingIndex].Span.Start > heading.Position + 1
                || body[headingIndex] is not ParagraphBlock)
                throw new InvalidOperationException("The detailed report could not locate a recorded incident heading.");
            body[headingIndex].SetData(DetailedReportPresentation.ItemHeadingTitleKey, heading.Title);
            body[headingIndex].SetData(DetailedReportPresentation.RecordHeadingLevelKey, heading.Level);
        }
        if (rosterHierarchyPosition is { } position)
        {
            var roster = blocks.OfType<ParagraphBlock>().SingleOrDefault(block => block.Span.Start == position)
                ?? throw new InvalidOperationException("The detailed report could not locate its captured roster hierarchy.");
            roster.SetData(DetailedReportPresentation.RosterHierarchyKey, request.RosterParentIndices);
        }
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
                ? "report-member-link" : reference.StartsWith("#report-incident-", StringComparison.Ordinal)
                    ? "report-incident-link" : "report-icm-link");
        }
    }

    private void ApplySections(MarkdownDocument body, List<(int Start, int End)> sections,
        object startKey, object endKey, params int[] headingLevels)
    {
        var index = 0;
        foreach (var section in sections)
        {
            token.ThrowIfCancellationRequested();
            while (index < body.Count && body[index].Span.Start < section.Start) index++;
            if (index == body.Count || body[index].Span.Start > section.Start + 1
                || !(body[index] is ParagraphBlock || body[index] is HeadingBlock heading
                    && headingLevels.Contains(heading.Level)))
                throw new InvalidOperationException("The detailed report could not locate a folding section heading.");
            body[index].SetData(startKey, true);
            if (startKey == DetailedReportPresentation.ItemSectionStartKey
                && itemHeadingTitles.TryGetValue(section.Start, out var title))
                body[index].SetData(DetailedReportPresentation.ItemHeadingTitleKey, title);
            if (startKey == DetailedReportPresentation.ItemSectionStartKey && body[index] is ParagraphBlock && index + 1 < body.Count
                && body[index + 1] is ParagraphBlock && body[index + 1].Span.Start < section.End)
                body[index + 1].SetData(DetailedReportPresentation.ItemFieldsKey, true);
            while (index + 1 < body.Count && body[index + 1].Span.Start < section.End) index++;
            if (body[index].Span.End >= section.End)
                throw new InvalidOperationException("The detailed report could not preserve a folding section boundary.");
            body[index].SetData(endKey, true);
            index++;
        }
    }
}
