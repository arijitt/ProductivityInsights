using System.Xml.Linq;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class GitZeroMemberTests
{
    public static void Run()
    {
        var baseline = ExportIntegrationTests.Snapshot(ExportIntegrationTests.Request(ExportIntegrationTests.PreparedComponent()));
        var manager = baseline.Request.Roster[0];
        var contributor = baseline.Request.Roster[1];
        var idle = new ReportIdentity("No <activity> & member", "idle@example.test", "idle");
        var snapshot = baseline with
        {
            Request = baseline.Request with { Roster = [manager, contributor, idle], RosterParentIndices = [null, 0, 0] }
        };
        Check(snapshot, [manager, idle]);
        Check(snapshot with
        {
            Request = snapshot.Request with { Roster = [manager, idle, contributor], RosterParentIndices = [null, 0, 1] }
        }, [manager, idle]);
        Check(snapshot with
        {
            Request = snapshot.Request with { Roster = [manager, idle, contributor], RosterParentIndices = [] }
        }, [manager, idle]);
        var empty = snapshot with { Commits = [], PullRequests = [] };
        Check(empty, [manager, contributor, idle]);
        var prOnly = empty with { PullRequests = [baseline.PullRequests[0] with { CompletedBy = idle }] };
        Check(prOnly, [manager, contributor, idle]);
        var prDocument = ReportDocumentBuilder.Build(prOnly);
        var prHtml = HtmlDetailedReportWriter.Write(prDocument);
        Require(prDocument.OverallRows.Sum(row => row.PullRequests) == 1
            && prDocument.OverallRows.Single(row =>
                row.Contributor.Contains(idle.Email!, StringComparison.Ordinal)).PullRequests == 1
            && !prHtml.Contains("Completed PR evidence", StringComparison.Ordinal),
            "Removing repeated PR evidence must not erase PR-only completion counts or attribution.");

        var pair = snapshot with
        {
            Commits = [snapshot.Commits[0] with { Committer = manager }],
            PullRequests = []
        };
        Check(pair, [idle]);
        var pairMain = MainElement(HtmlDetailedReportWriter.Write(pair));
        Require(Target(pairMain, manager) == Target(pairMain, contributor)
            && !Target(pairMain, manager).Value.EndsWith(": 0 commit", StringComparison.Ordinal)
            && pairMain.Descendants("details").Count(element => Class(element, "report-person-details")) == 2,
            "Shared author/committer participants must link to their real commit group, never receive duplicate zero-commit headings.");

        var sameName = idle with { Name = contributor.Name };
        Check(snapshot with
        {
            Request = snapshot.Request with { Roster = [manager, contributor, sameName] }
        }, [manager, sameName]);
        Check(empty with
        {
            Request = empty.Request with { Roster = [manager, contributor, sameName] }
        }, [manager, contributor, sameName]);

        var missing = snapshot.Request.Sources[0] with { Key = "missing-source", Scope = "Unavailable repository" };
        var partial = snapshot with
        {
            Request = snapshot.Request with { Sources = snapshot.Request.Sources.Add(missing) },
            SkippedSources = [new(missing.Key, "HTTP 403")]
        };
        var allSkipped = empty with { SkippedSources = [new(snapshot.Request.Sources[0].Key, "HTTP 404")] };
        foreach (var (skipped, unavailableCount) in new[]
        {
            (allSkipped, 3),
            (allSkipped with { Request = allSkipped.Request with { RosterParentIndices = [] } }, 3),
            (partial, 2),
            (partial with { Commits = [], PullRequests = prOnly.PullRequests }, 3),
            (partial with { Commits = pair.Commits, PullRequests = [] }, 1),
            (partial with { Request = partial.Request with { Roster = [manager, contributor, sameName] } }, 2),
            (allSkipped with { Request = allSkipped.Request with { Roster = [manager, contributor, sameName] } }, 3)
        })
        {
            var document = ReportDocumentBuilder.Build(skipped);
            var summary = HtmlSummaryReportWriter.Write(document, "report_Detailed.html");
            var html = HtmlDetailedReportWriter.Write(document);
            var main = MainElement(html);
            Require(ZeroHeadings(main).Length == unavailableCount
                && !html.Contains(": commit data unavailable", StringComparison.Ordinal)
                && main.Elements("p").Any(paragraph => paragraph.Value.Contains(
                    "Person headings count collected commits only. Activity from skipped sources is unavailable, not zero.", StringComparison.Ordinal)),
                "Partial or unavailable collection must use zero collected-commit headings with a visible, non-collapsible availability disclosure.");
            CheckPersonList(skipped, main);
            Require(!html.Contains("No collected detail section is attributed to this roster member", StringComparison.Ordinal),
                "Skipped sources must not send unrepresented roster members to a separate late fallback list.");
            var unavailable = ZeroHeadings(main);
            Require(unavailable.Length == unavailableCount && unavailable.All(heading =>
                !heading.Parent!.Parent!.Descendants("details").Any()
                && heading.Parent.Parent.Value.Contains("No commits were collected for this person.", StringComparison.Ordinal)
                && heading.Parent.Parent.Value.Contains("not evidence of zero activity", StringComparison.Ordinal)),
                "Zero collected-commit entries must preserve the missing-data explanation without invented commit details.");
            foreach (var member in skipped.Request.Roster)
            {
                var heading = Target(main, member);
                if (!unavailable.Contains(heading)) continue;
                Require(skipped.Request.Roster.Count(other => other.Name == member.Name) > 1
                    ? heading.Value.Contains(member.Name, StringComparison.Ordinal) && heading.Value.Contains(member.Email!, StringComparison.Ordinal)
                    : heading.Value == $"\U0001F464 {member.DisplayName}: 0 commit",
                    "Unavailable members must use the same compact name heading, qualifying duplicate display names.");
            }
            Require(ReportFoldMarkup.Parse(html).Count(fold => fold.Kind == "item") == document.Commits.Length
                && (!skipped.AllSourcesSkipped || document.OverallRows.IsEmpty && document.SourceRows.IsEmpty)
                && summary == HtmlSummaryReportWriter.Write(document, "report_Detailed.html")
                && !summary.Contains(": 0 commit", StringComparison.Ordinal)
                && !MarkdownReportWriter.Write(document).Contains(": 0 commit", StringComparison.Ordinal),
                "Detail-only unavailable members must not add commits or summary rows, or change legacy formats.");
            using var streamed = new StringWriter();
            HtmlDetailedReportWriter.WriteTo(document, streamed);
            Require(streamed.ToString() == html, "Unavailable roster destinations must match in streamed and buffered HTML.");
        }
        foreach (var view in new[] { "details", "trends" })
            Require(ZeroHeadings(MainElement(HtmlDetailedReportWriter.Write(snapshot with
                { Request = snapshot.Request with { View = view, RosterParentIndices = [] } }))).Length == 0,
                "Only Teams detailed Git reports gain concise zero-commit person headings.");
    }

    private static void Check(ReportSnapshot snapshot, ReportIdentity[] zeroMembers)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var main = MainElement(html);
        Require(ZeroHeadings(main).Length == zeroMembers.Length,
            "Only genuinely zero-commit roster members need a concise counted heading.");
        CheckPersonList(snapshot, main);
        var summary = main.Elements("h2").Single(heading => heading.Value.Contains("TEAM CODE COMMIT SUMMARY BY REPOSITORY", StringComparison.Ordinal));
        foreach (var member in zeroMembers)
        {
            var heading = Target(main, member);
            var person = heading.Parent!.Parent!;
            var content = person.Elements("div").Single(element => Class(element, "report-person-content"));
            var duplicateName = snapshot.Request.Roster.Count(other => other.Name == member.Name) > 1;
            Require(Class(heading, "report-person-heading") && Class(person, "report-person-details")
                && heading.Value.StartsWith("\U0001F464 ", StringComparison.Ordinal)
                && heading.Value.EndsWith(": 0 commit", StringComparison.Ordinal)
                && (duplicateName ? heading.Value.Contains(member.Name, StringComparison.Ordinal)
                        && heading.Value.Contains(member.Email!, StringComparison.Ordinal)
                    : heading.Value == $"\U0001F464 {member.DisplayName}: 0 commit")
                && !content.Elements().Any() && string.IsNullOrWhiteSpace(content.Value)
                && main.Elements().TakeWhile(element => element != summary).Contains(person),
                "Roster links must lead to normal, name-only counted headings in the main person list, with no extra body, line totals or identity fallback; duplicate names remain qualified.");
        }
        Require(!html.Contains("No collected detail section is attributed to this roster member", StringComparison.Ordinal)
            && !main.Descendants("activity").Any()
            && ReportFoldMarkup.Parse(html).Count(fold => fold.Kind == "item") == document.Commits.Length,
            "Complete snapshots must keep all commit folds and encoded names without duplicate fallback sections.");
        Require(!MarkdownReportWriter.Write(document).Contains(": 0 commit", StringComparison.Ordinal)
            && !HtmlSummaryReportWriter.Write(document, "report_Detailed.html").Contains(": 0 commit", StringComparison.Ordinal),
            "The zero-person presentation must not change legacy Markdown or summary HTML.");
        using var streamed = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, streamed);
        Require(streamed.ToString() == html, "Streamed and buffered reports must preserve the same zero-person headings and links.");
    }

    private static void CheckPersonList(ReportSnapshot snapshot, XElement main)
    {
        Require(snapshot.Request.Roster.Select(member => Target(main, member)).Distinct()
            .SequenceEqual(main.Descendants("h3").Where(element => Class(element, "report-person-heading"))),
            "All roster members, including unavailable members, must share one captured-order person list with shared groups appearing only once.");
        var summary = main.Elements("h2").Single(heading => heading.Value.Contains("TEAM CODE COMMIT SUMMARY BY REPOSITORY", StringComparison.Ordinal));
        Require(main.Elements().SkipWhile(element => element != summary)
            .All(element => !Class(element, "report-person-details")),
            "No person may appear in a separate section after the main detail list and summaries.");
    }

    private static XElement Target(XElement main, ReportIdentity member)
    {
        var link = main.Descendants("a").Single(element => Class(element, "report-member-link")
            && element.Ancestors().Any(parent => Class(parent, "report-roster-member"))
            && element.Value.Contains(member.Email!, StringComparison.Ordinal));
        var id = link.Attribute("href")!.Value[1..];
        return main.Descendants("h3").Single(element => (string?)element.Attribute("id") == id);
    }

    private static XElement[] ZeroHeadings(XElement main) => main.Descendants("h3")
        .Where(element => Class(element, "report-person-heading") && element.Value.EndsWith(": 0 commit", StringComparison.Ordinal)).ToArray();
    private static XElement MainElement(string html) =>
        XElement.Parse(html[html.IndexOf("<main>", StringComparison.Ordinal)..(html.IndexOf("</main>", StringComparison.Ordinal) + 7)]);
    private static bool Class(XElement element, string name) => ((string?)element.Attribute("class"))?.Split(' ').Contains(name) == true;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Git zero-member headings: " + message);
    }
}
