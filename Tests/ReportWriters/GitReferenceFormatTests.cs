using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class GitReferenceFormatTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var high = new ReportIdentity("Higher PRs", "high@example.test", "high");
        var more = new ReportIdentity("More Commits", "more@example.test", "more");
        var zero = new ReportIdentity("Zero Member", "zero@example.test", "zero");
        var snapshot = new ReportSnapshot
        {
            Request = new ReportRequest
            {
                RunId = Guid.NewGuid(), Analysis = "gitCommits", View = "teams", Manager = "Example Manager",
                Start = start, End = start.AddDays(30), ExecutedAt = start.AddMonths(1), TimeZone = "UTC",
                Roster = [high, more, zero],
                Sources = [new("alpha", "org", "project", "alpha", "main"), new("beta", "org", "project", "beta", "master")]
            },
            AsOf = start.AddMonths(1),
            Commits = [Commit("later-commit", "alpha", more, 2), Commit("earlier-commit", "alpha", more, 1), Commit("high-commit", "beta", high, 1)],
            PullRequests = [new("beta", "org", "beta-id", 1, "Completed PR", "refs/heads/master", high,
                start.AddDays(2), "https://example.test/pr/1")]
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var markdown = MarkdownReportWriter.Write(document);
        var html = HtmlSummaryReportWriter.Write(document, "report.md");

        var tableBodies = Regex.Matches(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline);
        Require(tableBodies.Count == 2, "Reference Teams HTML has source and overall summary tables.");
        var sourceRows = Rows(tableBodies[0].Groups[1].Value);
        var overallRows = Rows(tableBodies[1].Groups[1].Value);
        Require(sourceRows.Length == 3 && overallRows.Length == 3,
            "Inactive source/member combinations must collapse to one zero-member placeholder, not a source matrix.");
        Require(sourceRows[0].SequenceEqual(new[] { "Higher PRs", "org/project/beta", "1", "1", "+13", "-0", "0", "13", "0" }),
            "Reference source row uses repository paths without an unambiguous branch and signed counts.");
        Require(sourceRows[2].SequenceEqual(new[] { "Zero Member", "\u2014", "0", "0", "+0", "-0", "0", "0", "0" }),
            "Reference source table has one em-dash row for a zero-activity member.");
        Require(overallRows[0].SequenceEqual(new[] { "Higher PRs", "1", "1", "+13", "-0", "0", "13", "0", "beta" })
            && overallRows[2][^1] == "", "Overall repository names are compact; zero-member repository cells are empty.");
        Require(html.Contains("<h1>Team Code Commit Analysis (2026-07-01 to 2026-07-31)</h1>", StringComparison.Ordinal)
            && html.Contains("<div class=\"meta\">Generated 2026-08-01 00:00:00</div>", StringComparison.Ordinal),
            "Reference title and compact generated timestamp must precede the summaries.");
        Require(html.IndexOf("TEAM CODE COMMIT SUMMARY BY REPOSITORY", StringComparison.Ordinal)
            < html.IndexOf("Captured inputs, sources and coverage", StringComparison.Ordinal)
            && html.Contains("<details class=\"report-context\">", StringComparison.Ordinal),
            "Audit context must remain available without displacing the reference's compact summaries.");
        Require(html.Contains("table-layout:auto", StringComparison.Ordinal)
            && html.Contains("color:#808080", StringComparison.Ordinal), "Reference table sizing and metadata color are retained.");
        foreach (var icon in new[] { "\U0001F464", "\U0001F4C2", "\U0001F4BE", "\U0001F500", "\u2795", "\u2796" })
            Require(html.Contains(icon, StringComparison.Ordinal) && markdown.Contains(icon, StringComparison.Ordinal),
                "Both formats retain the reference summary icons.");

        var details = markdown.IndexOf("\U0001F4CB TEAM CODE COMMIT DETAILS BY PERSON:", StringComparison.Ordinal);
        var summary = markdown.IndexOf("\U0001F4CA TEAM CODE COMMIT SUMMARY BY REPOSITORY", StringComparison.Ordinal);
        var context = markdown.IndexOf("Captured inputs and export query context", StringComparison.Ordinal);
        Require(details >= 0 && summary > details && context > summary, "Markdown details, summaries, and context follow reference order.");
        Require(markdown.Contains(new string('\u2550', 120), StringComparison.Ordinal)
            && markdown.Contains(new string('\u2500', 120), StringComparison.Ordinal), "Markdown uses the reference boxed separators.");
        Require(markdown.IndexOf("\U0001F464 Committer: More Commits", StringComparison.Ordinal)
            < markdown.IndexOf("\U0001F464 Committer: Higher PRs", StringComparison.Ordinal),
            "Detailed contributor groups are ordered by commit count.");
        Require(markdown.IndexOf("\U0001F504 Commit: earlier-", StringComparison.Ordinal)
            < markdown.IndexOf("\U0001F504 Commit: later-co", StringComparison.Ordinal),
            "Detailed commits are chronological within each repository.");
        var plain = markdown.Replace("\\[", "[").Replace("\\]", "]").Replace("\\(", "(").Replace("\\)", ")").Replace("\\_", "_");
        Require(plain.Contains("\U0001F4CA Line Changes: +13 additions, -0 deletions", StringComparison.Ordinal)
            && plain.Contains("\U0001F4C1 Changed Files (13):", StringComparison.Ordinal)
            && plain.Contains("\U0001F4C4 /file_12.cs [Edit] (+1/-0)", StringComparison.Ordinal),
            "All changed files use the reference icons, change types, and signed deltas.");
        Require(markdown.Contains("Full commit message", StringComparison.Ordinal)
            && markdown.Contains("line one\n```text\n<untrusted>", StringComparison.Ordinal)
            && markdown.Contains("````text", StringComparison.Ordinal), "Multiline messages remain complete inside safe fences.");
        Require(markdown.Contains("Parent count", StringComparison.Ordinal)
            && markdown.Contains("Source-qualified key", StringComparison.Ordinal)
            && markdown.Contains("closedBy", StringComparison.Ordinal)
            && html.Contains(snapshot.Request.RunId.ToString("D"), StringComparison.Ordinal),
            "Formatting must not drop source identity, parent counts, PR evidence, or capture metadata.");
        Require(document.OverallRows.Sum(row => row.Commits) == 3 && document.OverallRows.Sum(row => row.PullRequests) == 1
            && document.Commits.Sum(commit => commit.Added) == 39, "Presentation must not change metric definitions or values.");

        var collision = snapshot with
        {
            Request = snapshot.Request with { Sources = snapshot.Request.Sources.Add(new("other", "other-org", "project", "beta", "main")) },
            Commits = snapshot.Commits.Add(Commit("other-commit", "other", high, 3) with { Organization = "other-org", RepositoryId = "other-beta-id" })
        };
        var collisionHtml = HtmlSummaryReportWriter.Write(collision, "report.md");
        Require(collisionHtml.Contains("org/project/beta, other-org/project/beta", StringComparison.Ordinal),
            "Duplicate repository names across organizations must remain source-qualified in the compact display.");
        var unresolved = snapshot with
        {
            PullRequests = snapshot.PullRequests.Add(new("alpha", "org", "alpha-id", 2, "Unknown actor PR", "refs/heads/main",
                new("Unmatched Alias"), start.AddDays(2), "https://example.test/pr/2"))
        };
        Require(HtmlSummaryReportWriter.Write(unresolved, "report.md").Contains("Unmatched Alias (unresolved identity)", StringComparison.Ordinal),
            "Compact formatting must not hide unresolved-identity status.");
        var branches = snapshot with
        {
            Request = snapshot.Request with { Sources = snapshot.Request.Sources.Add(new("alpha-release", "org", "project", "alpha", "release")) },
            Commits = snapshot.Commits.Add(snapshot.Commits[0] with { SourceKey = "alpha-release" })
        };
        var branchHtml = HtmlSummaryReportWriter.Write(branches, "report.md");
        Require(branchHtml.Contains("org/project/alpha (main)", StringComparison.Ordinal)
            && branchHtml.Contains("org/project/alpha (release)", StringComparison.Ordinal)
            && ReportDocumentBuilder.Build(branches).Commits.Length == 3,
            "Overlapping branch selections must remain distinguishable without double counting overall commits.");
        VerifyFileCounts(snapshot);

        GitReportRecord Commit(string id, string source, ReportIdentity identity, int day) => new()
        {
            SourceKey = source, Organization = "org", RepositoryId = source + "-id", Id = id,
            Author = identity, Committer = identity, AuthorDate = start.AddDays(day), CommitterDate = start.AddDays(day),
            ParentCount = 1, Message = "line one\n```text\n<untrusted>", Url = "https://example.test/commit/" + id,
            Files = Enumerable.Range(0, 13).Select(index => new GitFileReportRecord($"/file_{index}.cs", "edit", 1, 0)).ToImmutableArray()
        };
    }

    private static void VerifyFileCounts(ReportSnapshot baseline)
    {
        var commit = baseline.Commits[0] with
        {
            Files =
            [
                new("/added", " ADD ", 0, 0),
                new("/added-edited", "add, edit", 2, 1),
                new("/deleted", "delete", 0, 0),
                new("/edited", "EDIT", 0, 0),
                new("/old-name", "delete, sourceRename", 0, 0),
                new("/new-name", "edit, RENAME", 2, 1),
                new("/renamed", "targetRename", 0, 0),
                new("/encoding", "encoding", 0, 0),
                new("/property", "property", 0, 0),
                new("/binary", "edit", 0, 0),
                new("/submodule", "edit", 0, 0),
                new("/added-rename-target", "add, rename", 0, 0)
            ]
        };
        var repeat = commit with
        {
            Id = "next-commit", CommitterDate = commit.CommitterDate.AddMonths(1),
            Files = [new("/added", "add", 0, 0), new("/edited", "edit", 0, 0), new("/deleted", "DELETE", 0, 0)]
        };
        var snapshot = baseline with
        {
            Request = baseline.Request with
            {
                End = baseline.Request.End.AddYears(2),
                Sources = baseline.Request.Sources.Add(new("release", "org", "project", "alpha", "release"))
                    .Add(new("other-org", "other", "project", "alpha", "main"))
            },
            Commits =
            [
                commit, commit, commit with { SourceKey = "release" }, repeat,
                commit with { SourceKey = "beta", RepositoryId = "beta-id", Files = [new("/added", "add", 0, 0)] },
                commit with
                {
                    SourceKey = "other-org", Organization = "other", CommitterDate = commit.CommitterDate.AddYears(1),
                    Files = [new("/deleted", "delete", 0, 0)]
                },
                commit with { Id = "pair", Committer = baseline.Request.Roster[0], Files = [new("/edited", "edit", 0, 0)] }
            ]
        };
        foreach (var (view, interval) in new[]
        {
            ("teams", "daily"), ("details", "daily"),
            ("trends", "daily"), ("trends", "monthly"), ("trends", "yearly")
        })
        {
            var current = snapshot with
            {
                Request = snapshot.Request with
                {
                    View = view, TrendPeriod = interval,
                    Roster = view == "teams" ? snapshot.Request.Roster : []
                }
            };
            var document = ReportDocumentBuilder.Build(current);
            var html = HtmlSummaryReportWriter.Write(document, "report.md");
            var bodies = Regex.Matches(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline);
            var source = Rows(bodies[0].Groups[1].Value);
            var overall = Rows(bodies[1].Groups[1].Value);
            Require(source.Concat(overall).All(row => row.Length == 9), "Every HTML summary row has three file-count columns.");
            void Counts(string[] row, int offset, params string[] expected) =>
                Require(row.Skip(offset).Take(3).SequenceEqual(expected), $"Incorrect file counts for {view}/{interval}: {string.Join(", ", row)}");
            Counts(source.Single(row => row[0] == "More Commits" && row[1] == "org/project/alpha (main)"), 6, "3", "9", "2");
            Counts(source.Single(row => row[0] == "More Commits" && row[1] == "org/project/alpha (release)"), 6, "2", "8", "1");
            Counts(source.Single(row => row[0] == "More Commits" && row[1] == "org/project/beta"), 6, "1", "0", "0");
            Counts(source.Single(row => row[0] == "More Commits" && row[1] == "other/project/alpha"), 6, "0", "0", "1");
            Counts(overall.Single(row => row[0] == "More Commits"), 5, "4", "9", "3");
            Counts(overall.Single(row => row[0].StartsWith(view == "teams" ? "\U0001F464 " : "Author:", StringComparison.Ordinal)),
                5, "0", "1", "0");
            Counts(overall.Single(row => row[0] == "Higher PRs"), 5, "0", "0", "0");
            if (view == "teams")
                Counts(overall.Single(row => row[0] == "Zero Member"), 5, "0", "0", "0");
            Require(html.Contains("file-change occurrences per commit", StringComparison.Ordinal),
                "The HTML must distinguish file-change counts from unique paths and line counts.");
            var markdown = MarkdownReportWriter.Write(document);
            Require(!markdown.Contains("Files Added", StringComparison.Ordinal)
                && !markdown.Contains("Files Modified", StringComparison.Ordinal)
                && !markdown.Contains("Files Deleted", StringComparison.Ordinal),
                "The new columns must not change Markdown formatting.");
            if (view == "trends")
            {
                var periods = Rows(bodies[2].Groups[1].Value);
                Require(periods.Length == (interval == "yearly" ? 2 : 3) && periods.All(row => row.Length == 12),
                    "Each daily, monthly and yearly period must include file counts.");
                for (var column = 8; column <= 10; column++)
                    Require(periods.Sum(row => int.Parse(row[column])) == new[] { 4, 10, 3 }[column - 8],
                        "Trend file counts must use deduplicated commits across contributors and sources.");
                var total = Rows(Regex.Match(html, @"<tfoot>(.*?)</tfoot>", RegexOptions.Singleline).Groups[1].Value).Single();
                Require(total.Length == 12 && total[0] == "Total", "The trend total keeps the full column shape.");
                Counts(total, 8, "4", "10", "3");
            }
        }

        var empty = snapshot with { Commits = [], PullRequests = [] };
        var emptyTables = Regex.Matches(HtmlSummaryReportWriter.Write(empty, "report.md"), @"<tbody>(.*?)</tbody>", RegexOptions.Singleline);
        for (var index = 0; index < 2; index++)
        {
            var emptyRows = Rows(emptyTables[index].Groups[1].Value);
            Require(emptyRows.Length == snapshot.Request.Roster.Length
                && emptyRows.All(row => row.Length == 9 && row.Skip(index == 0 ? 6 : 5).Take(3).All(cell => cell == "0")),
                "Empty snapshots retain zero file counts in all three columns.");
        }
        var unavailable = empty with
        {
            SkippedSources = empty.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "Unavailable")).ToImmutableArray()
        };
        var unavailableHtml = HtmlSummaryReportWriter.Write(unavailable, "report.md");
        var unavailableTables = Regex.Matches(unavailableHtml, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline);
        Require(Rows(unavailableTables[0].Groups[1].Value).Length == 0 && Rows(unavailableTables[1].Groups[1].Value).Length == 0,
            "Unavailable repositories must not acquire fabricated zero file-count rows.");
    }

    private static string[][] Rows(string body) => Regex.Matches(body, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
        .Select(row => Regex.Matches(row.Groups[1].Value, @"<td>(.*?)</td>", RegexOptions.Singleline)
            .Select(cell => WebUtility.HtmlDecode(cell.Groups[1].Value)).ToArray()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Git reference format: " + message);
    }
}
