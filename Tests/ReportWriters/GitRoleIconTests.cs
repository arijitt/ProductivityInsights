using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class GitRoleIconTests
{
    private const string AuthorIcon = "\U0001F464";
    private const string CommitterIcon = "\u270D\uFE0F";

    public static void Run()
    {
        var baseline = ExportIntegrationTests.Snapshot(
            ExportIntegrationTests.Request(ExportIntegrationTests.PreparedComponent()));
        foreach (var duplicateNames in new[] { false, true })
        {
            var author = new ReportIdentity("Author <Name> / Committer: recorded", "author@example.test", "author");
            var committer = new ReportIdentity(duplicateNames ? author.Name : "Committer <Name>", "committer@example.test", "committer");
            var prOnly = new ReportIdentity("PR Only", "pr@example.test", "pr-only");
            var snapshot = baseline with
            {
                Request = baseline.Request with
                {
                    Roster = [baseline.Request.Roster[0], author, committer, prOnly],
                    RosterParentIndices = [null, 0, 1, 0]
                },
                Commits =
                [
                    baseline.Commits[0] with { Author = author, Committer = committer },
                    baseline.Commits[1] with { Author = author, Committer = author }
                ],
                PullRequests = [baseline.PullRequests[0] with { CompletedBy = prOnly }]
            };
            var document = ReportDocumentBuilder.Build(snapshot);
            var summary = HtmlSummaryReportWriter.Write(document, "report_Detailed.html");
            var detailed = HtmlDetailedReportWriter.Write(document);
            var summaryPairs = Cells(summary).Where(IsPair).ToArray();
            var detailedPairs = Cells(detailed).Where(IsPair).ToArray();
            Require(summaryPairs.Length == 2 && detailedPairs.Length == 2
                && detailedPairs.All(pair => pair.StartsWith(AuthorIcon + " Author:", StringComparison.Ordinal)
                    && pair.Contains(" / " + CommitterIcon + " Committer:", StringComparison.Ordinal))
                && detailedPairs.Select(pair => pair.Replace(AuthorIcon + " Author: ", AuthorIcon + " ", StringComparison.Ordinal)
                    .Replace(CommitterIcon + " Committer: ", CommitterIcon + " ", StringComparison.Ordinal)).SequenceEqual(summaryPairs),
                "Only summary HTML must omit generated role words; both files must preserve identical icons and qualified names.");
            foreach (var pair in summaryPairs)
            {
                Require(pair.Contains(author.Name, StringComparison.Ordinal) && pair.Contains(committer.Name, StringComparison.Ordinal),
                    "Role-like text inside names must remain exact, not be rewritten as decorative role labels.");
                if (duplicateNames)
                    Require(pair.Contains(author.Email!, StringComparison.Ordinal) && pair.Contains(committer.Email!, StringComparison.Ordinal),
                        "Equal display names must retain their distinct qualified identities.");
            }
            var headings = Regex.Matches(detailed, @"<h3\b[^>]*>(.*?)</h3>", RegexOptions.Singleline).Select(match => Text(match.Groups[1].Value)).ToArray();
            Require(headings.Any(text => text.StartsWith(AuthorIcon + " Contributor: " + detailedPairs[0], StringComparison.Ordinal))
                && headings.Any(text => text.StartsWith(CommitterIcon + " Committer: ", StringComparison.Ordinal)),
                "Paired headings must identify both roles; committer headings must retain their h3 structure and use the writing-hand icon.");
            foreach (var (label, icon) in new[] { ("Author:", AuthorIcon), ("Committer:", CommitterIcon) })
            {
                var fields = Regex.Matches(detailed,
                    @"<div class=""report-line""><span class=""report-icon"" aria-hidden=""true"">([^<]*)</span> <span class=""report-label"">"
                    + label + @"</span>(.*?)</div>", RegexOptions.Singleline);
                Require(fields.Count == document.Commits.Length && fields.All(field => Text(field.Groups[1].Value) == icon),
                    $"Every commit must show the on-screen {label} icon; found {fields.Count} of {document.Commits.Length} fields: "
                    + string.Join("\n", Regex.Matches(detailed, @"<div class=""report-line"">[^\n]*" + label + @"[^\n]*</div>")
                        .Select(match => match.Value)));
            }
            Require(Cells(summary).Contains("PR Only") && Cells(summary).Contains("Example Manager"),
                "PR-only and zero-activity members must not acquire invented author/committer roles.");
            Require(!summary.Contains("<Name>", StringComparison.Ordinal) && !detailed.Contains("<Name>", StringComparison.Ordinal),
                "Role decoration must preserve HTML encoding.");
            var markdown = MarkdownReportWriter.Write(document);
            Require(WebUtility.HtmlDecode(markdown).Contains("Author: " + author.Name, StringComparison.Ordinal)
                && !markdown.Contains(CommitterIcon, StringComparison.Ordinal),
                "The legacy Markdown output must remain undecorated.");
            using var summaryStream = new StringWriter();
            using var detailStream = new StringWriter();
            HtmlSummaryReportWriter.WriteTo(document, "report_Detailed.html", summaryStream);
            HtmlDetailedReportWriter.WriteTo(document, detailStream);
            Require(summaryStream.ToString() == summary && detailStream.ToString() == detailed,
                "Streamed downloads and buffered HTML helpers must render identical role icons.");

            foreach (var view in new[] { "details", "trends" })
            {
                var other = snapshot with { Request = snapshot.Request with { View = view, Roster = [], RosterParentIndices = [] } };
                Require(!HtmlSummaryReportWriter.Write(other, "report_Detailed.html").Contains(CommitterIcon, StringComparison.Ordinal)
                    && !HtmlDetailedReportWriter.Write(other).Contains(CommitterIcon, StringComparison.Ordinal),
                    "Non-Teams Git views must retain their existing presentation.");
            }
        }
        VerifyLabelCollision(baseline);
    }

    private static void VerifyLabelCollision(ReportSnapshot baseline)
    {
        var author = new ReportIdentity("Alice", "alice@example.test", "alice");
        var committer = new ReportIdentity("Bob", "bob@example.test", "bob");
        var misleading = new ReportIdentity("Author: Alice / Committer: Bob", "other@example.test", "other");
        var snapshot = baseline with
        {
            Request = baseline.Request with { Roster = [author, committer, misleading], RosterParentIndices = [] },
            Commits =
            [
                baseline.Commits[0] with { Author = author, Committer = committer },
                baseline.Commits[1] with { Author = misleading, Committer = misleading }
            ],
            PullRequests = []
        };
        foreach (var html in new[] { HtmlSummaryReportWriter.Write(snapshot, "report_Detailed.html"), HtmlDetailedReportWriter.Write(snapshot) })
        {
            var pairs = Cells(html).Where(IsPair).ToArray();
            Require(pairs.Length == 2 && pairs.All(pair => pair.Contains(author.Email!, StringComparison.Ordinal)
                && pair.Contains(committer.Email!, StringComparison.Ordinal) && !pair.Contains(misleading.Email!, StringComparison.Ordinal)),
                "Colliding labels must retain qualification, and a member name resembling a role pair must not acquire role icons.");
        }
    }

    private static bool IsPair(string text) => text.StartsWith(AuthorIcon + " ", StringComparison.Ordinal)
        && text.Contains(" / " + CommitterIcon + " ", StringComparison.Ordinal);

    private static string[] Cells(string html) => Regex.Matches(html, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)
        .Select(cell => Text(cell.Groups[1].Value)).ToArray();

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
