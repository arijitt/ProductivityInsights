using System.Collections.Immutable;
using System.Globalization;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal sealed class GitReportPresentation
{
    public const string AuthorIcon = "\U0001F464";
    public const string CommitterIcon = "\u270D\uFE0F";

    private readonly ReportRequest request;
    private readonly CancellationToken token;
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, int> sourceOrder;
    private readonly HashSet<string> ambiguousRepositories;
    private readonly HashSet<string> multipleBranches;
    private readonly Dictionary<string, string> contributorNames;
    private readonly Dictionary<string, PullRequestReportRecord> pullRequests;
    private readonly Dictionary<string, (string Author, string Committer)> contributorRoles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> rosterLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> detailOrder = new(StringComparer.Ordinal);
    private readonly HashSet<string> commitParticipants = new(StringComparer.Ordinal);

    public GitReportPresentation(ReportDocument document, CancellationToken token)
    {
        request = document.Snapshot.Request;
        this.token = token;
        sources = request.Sources.ToDictionary(source => source.Key, StringComparer.Ordinal);
        sourceOrder = request.Sources.Select((source, index) => (source.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        ambiguousRepositories = request.Sources.GroupBy(source => source.Scope, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(QualifiedRepository).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        multipleBranches = request.Sources.GroupBy(QualifiedRepository, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(source => source.Branch).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        contributorNames = ContributorNames(document);
        pullRequests = document.PullRequests.ToDictionary(pr => pr.Record.EntityKey, pr => pr.Record, StringComparer.Ordinal);
        OverallRows = document.OverallRows.OrderByDescending(row => row.PullRequests)
            .ThenByDescending(row => row.Commits).ThenBy(Contributor, StringComparer.Ordinal)
            .ToImmutableArray();
        var byMember = document.SourceRows.ToLookup(row => row.ContributorKey, StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<ReportSummaryRow>();
        foreach (var member in OverallRows)
        {
            token.ThrowIfCancellationRequested();
            if (member.Commits == 0 && member.PullRequests == 0)
                rows.Add(member);
            else
                rows.AddRange(byMember[member.ContributorKey].Where(row => row.Commits != 0 || row.PullRequests != 0)
                    .OrderByDescending(row => row.PullRequests).ThenByDescending(row => row.Commits)
                    .ThenBy(row => Repository(sources[row.SourceKey!]), StringComparer.Ordinal));
        }
        SourceRows = rows.ToImmutable();
    }

    public ImmutableArray<ReportSummaryRow> SourceRows { get; }
    public ImmutableArray<ReportSummaryRow> OverallRows { get; }

    public static string[] Headers(bool bySource) => bySource
        ? ["\U0001F464 Team Member", "\U0001F4C2 Repository", "\U0001F4BE Commits", "\U0001F500 PRs Merged", "\u2795 Lines", "\u2796 Lines"]
        : ["\U0001F464 Team Member", "\U0001F4BE Commits", "\U0001F500 PRs Merged", "\u2795 Lines", "\u2796 Lines", "\U0001F4C2 Repositories"];

    public string[] Cells(ReportSummaryRow row, bool bySource, bool roleIcons = false)
    {
        token.ThrowIfCancellationRequested();
        var counts = new[]
        {
            ReportFormatting.Number(row.Commits), ReportFormatting.Number(row.PullRequests),
            "+" + ReportFormatting.Number(row.Added), "-" + ReportFormatting.Number(row.Deleted)
        };
        var contributor = roleIcons ? ContributorForHtml(row) : Contributor(row);
        return bySource
            ? [contributor, row.SourceKey is null ? "\u2014" : Repository(sources[row.SourceKey]), .. counts]
            : [contributor, .. counts, ReportFormatting.Join(", ", row.SourceKeys
                .OrderBy(key => sourceOrder[key]).Select(key => sources[key])
                .Select(source => ambiguousRepositories.Contains(source.Scope) ? QualifiedRepository(source) : source.Scope)
                .Distinct(StringComparer.OrdinalIgnoreCase), token)];
    }

    public string Contributor(ReportSummaryRow row) => contributorNames[row.ContributorKey];

    public IEnumerable<ReportSummaryRow> TeamsDetailRows()
    {
        var members = OverallRows.ToDictionary(row => row.ContributorKey, StringComparer.Ordinal);
        foreach (var (key, label) in rosterLabels)
        {
            token.ThrowIfCancellationRequested();
            // All-source failures have no summary rows; these detail-only placeholders never enter totals.
            members.TryAdd(key, new ReportSummaryRow { ContributorKey = key, Contributor = label });
        }
        return members.Values.Where(row => row.Commits != 0
            || rosterLabels.ContainsKey(row.ContributorKey) && !commitParticipants.Contains(row.ContributorKey))
            .OrderBy(row => detailOrder[row.ContributorKey])
            .ThenBy(row => contributorRoles.ContainsKey(row.ContributorKey))
            .ThenByDescending(row => row.Commits).ThenByDescending(row => row.Added)
            .ThenBy(Contributor, StringComparer.Ordinal);
    }

    public string PersonHeading(ReportSummaryRow row)
    {
        var name = ContributorForHtml(row);
        if (!contributorRoles.ContainsKey(row.ContributorKey)) name = AuthorIcon + " " + name;
        return $"{name}: {ReportFormatting.Number(row.Commits)} {(row.Commits == 0 || row.Commits == 1 ? "commit" : "commits")}";
    }

    public (int Id, ReadOnlyMemory<char> Title)? PullRequestForHeading(GitReportRecord commit)
    {
        token.ThrowIfCancellationRequested();
        var subject = commit.Message.AsSpan();
        var lineEnd = subject.IndexOfAny('\r', '\n');
        if (lineEnd >= 0) subject = subject[..lineEnd];
        const string prefix = "Merged PR ";
        if (!subject.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var colon = subject.IndexOf(':');
        if (colon <= prefix.Length
            || !int.TryParse(subject[prefix.Length..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0) return null;
        var key = $"{commit.Organization.ToUpperInvariant()}:{commit.RepositoryId.ToUpperInvariant()}:{id}";
        if (pullRequests.TryGetValue(key, out var pr) && !string.IsNullOrWhiteSpace(pr.Title))
            return (id, pr.Title.AsMemory());
        // A merge subject can name a PR excluded from completion attribution; use it only for display.
        var title = subject[(colon + 1)..];
        var leadingWhitespace = title.Length - title.TrimStart().Length;
        title = title.Trim();
        return title.IsEmpty ? null : (id, commit.Message.AsMemory(colon + 1 + leadingWhitespace, title.Length));
    }

    public string[] SummaryCells(ReportSummaryRow row, bool bySource)
    {
        var cells = Cells(row, bySource);
        cells[0] = ContributorForHtml(row, includeRoleLabels: false);
        return cells;
    }

    public string ContributorForHtml(ReportSummaryRow row, bool includeRoleLabels = true)
    {
        if (!request.IsTeams || !contributorRoles.TryGetValue(row.ContributorKey, out var roles))
            return Contributor(row);
        return includeRoleLabels
            ? $"{AuthorIcon} Author: {roles.Author} / {CommitterIcon} Committer: {roles.Committer}"
            : $"{AuthorIcon} {roles.Author} / {CommitterIcon} {roles.Committer}";
    }

    private Dictionary<string, string> ContributorNames(ReportDocument document)
    {
        var identities = request.Roster.Concat(document.Commits.SelectMany(commit =>
            new[] { commit.Entity.Record.Author, commit.Entity.Record.Committer }))
            .Concat(document.PullRequests.Select(pr => pr.Record.CompletedBy)).ToImmutableArray();
        var catalog = new ReportIdentityCatalog(request, identities, token);
        for (var index = 0; index < request.Roster.Length; index++)
        {
            var member = catalog.Get(request.Roster[index]);
            detailOrder.TryAdd(member.Key, index);
            rosterLabels.TryAdd(member.Key, member.Label);
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            token.ThrowIfCancellationRequested();
            var resolved = catalog.Get(identity);
            names.TryAdd(resolved.Key, resolved.Key.StartsWith("UNRESOLVED:", StringComparison.Ordinal)
                ? resolved.Label : identity.DisplayName);
            labels.TryAdd(resolved.Key, resolved.Label);
        }
        var duplicateNames = names.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in names.Keys.ToArray())
            if (duplicateNames.Contains(names[key])) names[key] = labels[key];
        var pairs = new Dictionary<string, (string Author, string Committer)>(StringComparer.Ordinal);
        foreach (var commit in document.Commits)
        {
            token.ThrowIfCancellationRequested();
            var author = catalog.Get(commit.Entity.Record.Author).Key;
            var committer = catalog.Get(commit.Entity.Record.Committer).Key;
            commitParticipants.Add(author);
            commitParticipants.Add(committer);
            // Captured roster order is depth-first; a shared group appears at its first participant.
            detailOrder[commit.ContributorKey] = Math.Min(detailOrder.GetValueOrDefault(author, int.MaxValue),
                detailOrder.GetValueOrDefault(committer, int.MaxValue));
            if (author != committer)
            {
                names[commit.ContributorKey] = $"Author: {names[author]} / Committer: {names[committer]}";
                pairs[commit.ContributorKey] = (author, committer);
            }
        }
        var rows = document.OverallRows.ToDictionary(row => row.ContributorKey,
            row => names.GetValueOrDefault(row.ContributorKey, row.Contributor), StringComparer.Ordinal);
        var collisions = rows.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.OverallRows)
        {
            var qualified = collisions.Contains(rows[row.ContributorKey]);
            if (qualified) rows[row.ContributorKey] = row.Contributor;
            if (pairs.TryGetValue(row.ContributorKey, out var pair))
                contributorRoles[row.ContributorKey] = qualified
                    ? (labels[pair.Author], labels[pair.Committer])
                    : (names[pair.Author], names[pair.Committer]);
        }
        foreach (var key in rosterLabels.Keys) rows.TryAdd(key, names[key]);
        return rows;
    }

    public string Repository(ReportSource source)
    {
        var name = QualifiedRepository(source);
        return multipleBranches.Contains(name) ? name + $" ({ReportFormatting.Label(source.Branch)})" : name;
    }

    public string CommitRepositories(ImmutableArray<string> keys) =>
        ReportFormatting.Join("; ", keys.Select(key => sources[key].Label), token);

    public static string Timestamp(DateTimeOffset value) =>
        ReportFormatting.CompactTimestamp(value);

    public static string Identity(ReportIdentity identity) => string.IsNullOrWhiteSpace(identity.Email)
        ? identity.DisplayName : $"{identity.DisplayName} <{identity.Email}>";

    public static string ChangeType(string type) => string.Join(", ", type.Split(',', StringSplitOptions.TrimEntries)
        .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public static string FileDelta(GitFileReportRecord file) => file.ChangeType.Trim().ToLowerInvariant() switch
    {
        "add" when file.Deleted == 0 => $"+{ReportFormatting.Number(file.Added)}",
        "delete" when file.Added == 0 => $"-{ReportFormatting.Number(file.Deleted)}",
        _ => $"+{ReportFormatting.Number(file.Added)}/-{ReportFormatting.Number(file.Deleted)}"
    };

    private static string QualifiedRepository(ReportSource source) =>
        $"{source.Organization}/{source.Project}/{source.Scope}";
}
