using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal sealed class GitReportPresentation
{
    private readonly ReportRequest request;
    private readonly CancellationToken token;
    private readonly Dictionary<string, ReportSource> sources;
    private readonly Dictionary<string, int> sourceOrder;
    private readonly HashSet<string> ambiguousRepositories;
    private readonly HashSet<string> multipleBranches;
    private readonly Dictionary<string, string> contributorNames;

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

    public string[] Cells(ReportSummaryRow row, bool bySource)
    {
        token.ThrowIfCancellationRequested();
        var counts = new[]
        {
            ReportFormatting.Number(row.Commits), ReportFormatting.Number(row.PullRequests),
            "+" + ReportFormatting.Number(row.Added), "-" + ReportFormatting.Number(row.Deleted)
        };
        return bySource
            ? [Contributor(row), row.SourceKey is null ? "\u2014" : Repository(sources[row.SourceKey]), .. counts]
            : [Contributor(row), .. counts, ReportFormatting.Join(", ", row.SourceKeys
                .OrderBy(key => sourceOrder[key]).Select(key => sources[key])
                .Select(source => ambiguousRepositories.Contains(source.Scope) ? QualifiedRepository(source) : source.Scope)
                .Distinct(StringComparer.OrdinalIgnoreCase), token)];
    }

    public string Contributor(ReportSummaryRow row) => contributorNames[row.ContributorKey];

    private Dictionary<string, string> ContributorNames(ReportDocument document)
    {
        var identities = request.Roster.Concat(document.Commits.SelectMany(commit =>
            new[] { commit.Entity.Record.Author, commit.Entity.Record.Committer }))
            .Concat(document.PullRequests.Select(pr => pr.Record.CompletedBy)).ToImmutableArray();
        var catalog = new ReportIdentityCatalog(request, identities, token);
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
        foreach (var commit in document.Commits)
        {
            token.ThrowIfCancellationRequested();
            var author = catalog.Get(commit.Entity.Record.Author).Key;
            var committer = catalog.Get(commit.Entity.Record.Committer).Key;
            if (author != committer)
                names[commit.ContributorKey] = $"Author: {names[author]} / Committer: {names[committer]}";
        }
        var rows = document.OverallRows.ToDictionary(row => row.ContributorKey,
            row => names.GetValueOrDefault(row.ContributorKey, row.Contributor), StringComparer.Ordinal);
        var collisions = rows.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.OverallRows)
            if (collisions.Contains(rows[row.ContributorKey])) rows[row.ContributorKey] = row.Contributor;
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
