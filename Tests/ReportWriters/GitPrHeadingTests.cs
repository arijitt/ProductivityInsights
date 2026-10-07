using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class GitPrHeadingTests
{
    public static void Run()
    {
        var baseline = ReportWriterFixtures.GitSnapshot();
        var commit = baseline.Commits[0] with { Message = "Merged PR 7: Original merge subject\nFull recorded body", ParentCount = 1 };
        var pr = baseline.PullRequests[0] with { Title = "Captured PR title" };
        var snapshot = baseline with { Commits = [commit], PullRequests = [pr] };
        Check(snapshot, ["\U0001F504 PR #7: Captured PR title"]);
        var outsideCommitter = new ReportIdentity("External Committer", "external@example.test", "external-id");
        var sharedWithoutCompletion = snapshot with
        {
            Commits = [commit with { Committer = outsideCommitter }],
            PullRequests = []
        };
        Check(sharedWithoutCompletion, ["\U0001F504 PR #7: Original merge subject"]);
        Require(ReportDocumentBuilder.Build(sharedWithoutCompletion).OverallRows.Sum(row => row.PullRequests) == 0,
            "A merge-subject heading must not fabricate completed-PR credit for a shared author/committer group.");
        var ordinaryHeading = "\U0001F504 Commit: same-sha...";
        foreach (var message in new[]
        {
            "Ordinary commit", "A reference to Merged PR 7: is not a merge subject",
            "Commit subject\nMerged PR 7: Body reference", "Revert \"Merged PR 7: Original merge subject\"",
            "Merged PR 7", "Merged PR : Missing number", "Merged PR -7: Invalid number",
            "Merged PR 0: Invalid number", "Merged PR 2147483648: Overflow",
            "Merged PR 7x: Invalid number", "Merged PR +7: Invalid number", "merged PR 7: Not the merge-subject format"
        })
        {
            Check(snapshot with { Commits = [commit with { Message = message, ParentCount = 2 }] }, [ordinaryHeading]);
            Check(sharedWithoutCompletion with { Commits = [sharedWithoutCompletion.Commits[0] with { Message = message }] }, [ordinaryHeading]);
        }
        foreach (var newline in new[] { "\n", "\r\n", "\r" })
        {
            Check(snapshot with { Commits = [commit with { Message = "Merged PR 7: Old title" + newline + "Additional evidence" }] },
                ["\U0001F504 PR #7: Captured PR title"]);
            Check(sharedWithoutCompletion with
            {
                Commits = [sharedWithoutCompletion.Commits[0] with
                    { Message = "Merged PR 7: \tRecorded subject title  " + newline + "Merged PR 99: Body reference only" }]
            }, ["\U0001F504 PR #7: Recorded subject title"]);
        }
        foreach (var title in new[]
        {
            "<script>not markup</script> & [title](javascript:bad) | <details>",
            "First title line\nSecond line with `code` and # heading"
        })
            Check(snapshot with { PullRequests = [pr with { Title = title }] }, ["\U0001F504 PR #7: " + title]);
        foreach (var title in new[] { "<script>not markup</script> & [title](javascript:bad) | <details>", "A title: with another colon" })
            Check(sharedWithoutCompletion with
            {
                Commits = [sharedWithoutCompletion.Commits[0] with { Message = "Merged PR 7: " + title + "\nPreserved body" }]
            }, ["\U0001F504 PR #7: " + title]);
        foreach (var subject in new[] { "Merged PR 7:", "Merged PR 7:  \t", "Merged PR 7:\nBody is not a title" })
            Check(sharedWithoutCompletion with
            {
                Commits = [sharedWithoutCompletion.Commits[0] with { Message = subject }]
            }, [ordinaryHeading]);
        Check(snapshot with { Commits = [commit with { Message = "Merged PR 99: Not captured" }] },
            ["\U0001F504 PR #99: Not captured"]);
        Check(sharedWithoutCompletion with
        {
            Commits = [sharedWithoutCompletion.Commits[0] with { Message = "Merged PR 2147483647: Maximum valid ID" }]
        }, ["\U0001F504 PR #2147483647: Maximum valid ID"]);
        Check(snapshot with { PullRequests = [] }, ["\U0001F504 PR #7: Original merge subject"]);
        Check(snapshot with { PullRequests = [pr with { Title = " " }] }, ["\U0001F504 PR #7: Original merge subject"]);
        Check(snapshot with { PullRequests = [pr with { RepositoryId = "repo-two", SourceKey = "source-c" }] },
            ["\U0001F504 PR #7: Original merge subject"]);
        Check(sharedWithoutCompletion with { Commits = [sharedWithoutCompletion.Commits[0], sharedWithoutCompletion.Commits[0] with { SourceKey = "source-b" }] },
            ["\U0001F504 PR #7: Original merge subject"]);
        Check(snapshot with { Commits = [commit with { Organization = "ORG-A", RepositoryId = "REPO-ONE" }] },
            ["\U0001F504 PR #7: Captured PR title"]);
        Check(snapshot with { Commits = [commit, commit with { SourceKey = "source-b" }] },
            ["\U0001F504 PR #7: Captured PR title"]);

        var otherSource = snapshot.Request.Sources[0] with { Key = "other-org", Organization = "other-org" };
        var collisions = snapshot with
        {
            Request = snapshot.Request with { Sources = snapshot.Request.Sources.Add(otherSource) },
            Commits =
            [
                commit,
                commit with { SourceKey = "source-c", RepositoryId = "repo-two" },
                commit with { SourceKey = "other-org", Organization = "other-org" }
            ],
            PullRequests =
            [
                pr,
                pr with { SourceKey = "source-c", RepositoryId = "repo-two", Title = "Other repository title" },
                pr with { SourceKey = "other-org", Organization = "other-org", Title = "Other organization title" }
            ]
        };
        var collisionHtml = HtmlDetailedReportWriter.Write(collisions);
        var items = ReportFoldMarkup.Parse(collisionHtml).Where(fold => fold.Kind == "item").ToArray();
        foreach (var record in collisions.PullRequests)
            Require(items.Count(item => Heading(item.Content) == "\U0001F504 PR #7: " + record.Title
                && Text(item.Content).Contains("Organization: " + record.Organization, StringComparison.Ordinal)
                && Text(item.Content).Contains("Repository ID: " + record.RepositoryId, StringComparison.Ordinal)) == 1,
                "Equal PR numbers and commit IDs in different organizations/repositories must never borrow another source's title.");

        foreach (var view in new[] { "details", "trends" })
        {
            var other = snapshot with { Request = snapshot.Request with { View = view } };
            Require(Headings(HtmlDetailedReportWriter.Write(other)).SequenceEqual([ordinaryHeading]),
                "Details and Trends must retain their original commit headings.");
            Require(Headings(HtmlDetailedReportWriter.Write(sharedWithoutCompletion with
                { Request = sharedWithoutCompletion.Request with { View = view } })).SequenceEqual([ordinaryHeading]),
                "Merge-subject fallback must not change shared-commit headings in Details or Trends.");
        }
        Require(MarkdownReportWriter.Write(snapshot).Contains("\U0001F504 Commit: same-sha...", StringComparison.Ordinal)
            && !MarkdownReportWriter.Write(snapshot).Contains("\U0001F504 PR", StringComparison.Ordinal),
            "Legacy Markdown must keep commit headings.");
        Require(HtmlSummaryReportWriter.Write(snapshot, "report_Detailed.html")
                == HtmlSummaryReportWriter.Write(snapshot with { Commits = [commit with { Message = "Ordinary commit" }] }, "report_Detailed.html"),
            "PR heading presentation must not alter summary HTML.");
        Require(HtmlSummaryReportWriter.Write(sharedWithoutCompletion, "report_Detailed.html")
                == HtmlSummaryReportWriter.Write(sharedWithoutCompletion with
                    { Commits = [sharedWithoutCompletion.Commits[0] with { Message = "Ordinary commit" }] }, "report_Detailed.html")
            && !MarkdownReportWriter.Write(sharedWithoutCompletion).Contains("\U0001F504 PR", StringComparison.Ordinal),
            "The display fallback must not alter summary HTML or legacy Markdown.");

        var longTitle = new string('x', 800_000) + " END TITLE";
        var large = snapshot with
        {
            Commits = Enumerable.Range(0, 96).Select(index => commit with { Id = index.ToString("x40") }).ToImmutableArray(),
            PullRequests = [pr with { Title = longTitle }]
        };
        var document = ReportDocumentBuilder.Build(large);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var bytes = HtmlDetailedReportWriter.WriteTo(document, TextWriter.Null);
        Require(bytes > 96L * longTitle.Length && bytes > ReportRenderingLimits.MaximumDetailedMarkupBytes
            && bytes < ReportRenderingLimits.MaximumExportFileBytes
            && GC.GetAllocatedBytesForCurrentThread() - allocated < ReportRenderingLimits.MaximumDetailedMarkupBytes,
            "Repeated full PR titles must stream from captured references, not inflate or truncate the structural Markdown buffer.");

        var longSubject = "Merged PR 7: " + new string('\u4e00', 800_000) + " END SUBJECT\nPreserved merge body";
        var largeFallback = sharedWithoutCompletion with
        {
            Commits = Enumerable.Range(0, 24).Select(index => sharedWithoutCompletion.Commits[0] with
                { Id = index.ToString("x40"), Message = longSubject }).ToImmutableArray()
        };
        document = ReportDocumentBuilder.Build(largeFallback);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        bytes = HtmlDetailedReportWriter.WriteTo(document, TextWriter.Null);
        Require(bytes > ReportRenderingLimits.MaximumDetailedMarkupBytes && bytes < ReportRenderingLimits.MaximumExportFileBytes
            && GC.GetAllocatedBytesForCurrentThread() - allocated < ReportRenderingLimits.MaximumInputCharacters,
            "Merge-subject titles must stream as slices of captured messages, without copying large repeated titles or inflating structural Markdown.");
    }

    private static void Check(ReportSnapshot snapshot, string[] expected)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var items = ReportFoldMarkup.Parse(html).Where(fold => fold.Kind == "item").ToArray();
        Require(Headings(html).SequenceEqual(expected) && items.Length == document.Commits.Length,
            "Each distinct commit needs one independently folding heading with the recorded PR number and full captured title when matched.");
        foreach (var commit in document.Commits)
        {
            var text = Text(items.Single(item => Text(item.Content).Contains("Commit ID: " + commit.Entity.Record.Id, StringComparison.Ordinal)).Content);
            Require(text.Contains("Full commit message", StringComparison.Ordinal)
                && text.ReplaceLineEndings("\n").Contains(commit.Entity.Record.Message.ReplaceLineEndings("\n"), StringComparison.Ordinal)
                && text.Contains("Parent count (not PR evidence):", StringComparison.Ordinal)
                && commit.Entity.Record.Files.All(file => text.Contains(file.Path, StringComparison.Ordinal)),
                "Renaming a heading must retain the full commit ID, message, parent count and every file.");
        }
        Require(document.OverallRows.Sum(row => row.Commits) == document.Commits.Length
            && document.OverallRows.Sum(row => row.PullRequests) == document.PullRequests.Length,
            "Commit-to-PR display matching must not change contribution counts or closedBy attribution.");
        Require(!Regex.IsMatch(html, @"<(?:script|img|iframe)\b", RegexOptions.IgnoreCase),
            "Captured PR titles must be encoded as text, never interpreted as markup.");
        using var stream = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, stream);
        Require(stream.ToString() == html, "Streamed and buffered headings must have identical complete titles.");
    }

    private static string[] Headings(string html) =>
        Regex.Matches(html, @"<h4 class=""report-line report-record-heading"">(.*?)</h4>", RegexOptions.Singleline)
            .Select(match => Text(match.Groups[1].Value)).ToArray();

    private static string Heading(string html) => Headings(html).Single();
    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Git PR headings: " + message);
    }
}
