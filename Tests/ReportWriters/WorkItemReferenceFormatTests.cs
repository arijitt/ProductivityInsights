using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class WorkItemReferenceFormatTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var alex = new ReportIdentity("Alex Example", "alex@example.test", "alex");
        var blair = new ReportIdentity("Blair Example", "blair@example.test", "blair");
        var zero = new ReportIdentity("Zero Example", "zero@example.test", "zero");
        var early = Item(11, alex, "Bug", @"Project\Root\Child", 1);
        var late = Item(12, alex, "Task", @"Project\Root", 3);
        var snapshot = new ReportSnapshot
        {
            Request = new ReportRequest
            {
                RunId = Guid.NewGuid(), Analysis = "workItems", View = "teams", Manager = "Example Manager",
                Start = start, End = start.AddMonths(1).AddTicks(-1), ExecutedAt = start.AddMonths(1),
                TimeZone = "UTC", Roster = [alex, blair, zero],
                Sources =
                [
                    new("root", "org", "Project", @"Project\Root", Team: "Main Team"),
                    new("child", "org", "Project", @"Project\Root\Child", Team: "Child Team")
                ]
            },
            AsOf = start.AddMonths(1),
            WorkItems =
            [
                late, early, early with { SourceKey = "child" },
                Item(13, alex, "Feature", @"Project\Root\Child", 2),
                Item(14, blair, "Product Backlog Item", @"Project\Root", 1)
            ]
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlSummaryReportWriter.Write(document, "work.md");
        var markdown = MarkdownReportWriter.Write(document);
        var tables = Bodies(html);
        Require(tables.Length == 2, "HTML keeps exactly the reference's area and overall tables.");
        Require(tables[0].Length == 4 && tables[1].Length == 3, "Actual areas replace selected-query/zero-member matrices.");
        Require(tables[0][0].SequenceEqual(new[] { "Alex Example", @"Project\Root\Child", "2", "1", "0", "0", "1" }),
            "Source rows group deduplicated items by recorded area, not selected query roots.");
        Require(tables[0][1].SequenceEqual(new[] { "Alex Example", @"Project\Root", "1", "0", "1", "0", "0" }),
            "Areas are ordered by contributor activity, then area activity.");
        Require(tables[0][^1].SequenceEqual(new[] { "Zero Example", "\u2014", "0", "0", "0", "0", "0" }),
            "Zero members have one placeholder area row.");
        Require(tables[1][0].SequenceEqual(new[] { "Alex Example", "3", "1", "1", "0", "1", "Child Team, Main Team" }),
            "Overall rows use compact sorted team names and do not double count overlap.");
        Require(tables[1][^1][^1] == "", "Zero-member team cells stay empty.");
        Require(html.Contains("<div class=\"meta\">Generated 2026-08-01 00:00:00</div>", StringComparison.Ordinal)
            && html.Contains("table-layout:auto", StringComparison.Ordinal)
            && html.Contains("color:#808080", StringComparison.Ordinal), "HTML retains the reference's compact timestamp and dark automatic-width tables.");
        Require(html.IndexOf("<table>", StringComparison.Ordinal) < html.IndexOf("<details class=\"report-context\">", StringComparison.Ordinal),
            "Context stays available below, rather than displacing, the compact summaries.");
        var icons = new[] { "\U0001F464", "\U0001F4C2", "\U0001F4CB", "\U0001F41E", "\U0001F527", "\U0001F4E6", "\U0001F9E9", "\U0001F465" };
        foreach (var icon in icons)
            Require(html.Contains(icon, StringComparison.Ordinal) && markdown.Contains(icon, StringComparison.Ordinal), "Both formats retain the work-item summary icons.");

        var details = markdown.IndexOf("\U0001F3C6 TEAM WORK ITEM COMPLETION DETAILS BY PERSON", StringComparison.Ordinal);
        var summary = markdown.IndexOf("\U0001F4CA TEAM WORK ITEM SUMMARY BY AREA PATH", StringComparison.Ordinal);
        var context = markdown.IndexOf("Captured inputs and export query context", StringComparison.Ordinal);
        Require(details >= 0 && summary > details && context > summary, "Reference completion details and summaries precede the evidence appendix.");
        Require(markdown.Contains("\U0001F464 Alex Example: 3 work item", StringComparison.Ordinal)
            && markdown.Contains("\U0001F4E6 org/Project/Main Team", StringComparison.Ordinal), "Markdown uses compact person and selected-team sections.");
        Require(markdown.Contains(new string('\u2550', 120), StringComparison.Ordinal)
            && markdown.Contains(new string('\u2500', 120), StringComparison.Ordinal), "Reference boxed separators are retained.");
        Require(markdown.Contains("New \u2192 Active by Alex Example on 2026-07-01", StringComparison.Ordinal)
            && markdown.Contains("Active \u2192 Done by Blair Example on 2026-07-02", StringComparison.Ordinal),
            "Compact transitions use the next state's actual actor and date.");
        Require(markdown.Contains("State change history", StringComparison.Ordinal)
            && markdown.Contains("Total time by state", StringComparison.Ordinal)
            && markdown.Contains("Time in active work", StringComparison.Ordinal)
            && markdown.Contains(snapshot.Request.RunId.ToString("D"), StringComparison.Ordinal),
            "Full history, durations, identities and captured context remain in the appendix.");
        Require(!markdown.Contains("Processing team:", StringComparison.Ordinal)
            && !markdown.Contains("HTML report written to:", StringComparison.Ordinal)
            && !markdown.Contains("TEAM HIERARCHY", StringComparison.Ordinal), "Export must not fabricate console logs, server paths or uncaptured hierarchy levels.");
        var compact = markdown[details..summary];
        Require(Regex.Matches(compact, @"\\#11\b").Count == 1, "Overlapping source memberships must not repeat compact work items.");
        var chronological = snapshot with { WorkItems = [late, early, Item(13, alex, "Feature", @"Project\Root\Child", 2)] };
        var ordered = MarkdownReportWriter.Write(chronological);
        Require(ordered.IndexOf(@"\#11", StringComparison.Ordinal) < ordered.IndexOf(@"\#13", StringComparison.Ordinal)
            && ordered.IndexOf(@"\#13", StringComparison.Ordinal) < ordered.IndexOf(@"\#12", StringComparison.Ordinal),
            "Items within each team group are chronological by completion.");

        var collision = snapshot with
        {
            Request = snapshot.Request with
            {
                Sources = snapshot.Request.Sources.Add(new("other", "other", "Project", @"Project\Root", Team: "Main Team")),
                Roster = [alex, blair with { Name = alex.Name }, zero]
            },
            WorkItems = snapshot.WorkItems.Add(early with { SourceKey = "other", Organization = "other" })
                .Select(item => item.Assignee == blair ? item with { Assignee = blair with { Name = alex.Name } } : item).ToImmutableArray()
        };
        var collisionHtml = HtmlSummaryReportWriter.Write(collision, "work.md");
        Require(collisionHtml.Contains(@"org/Project/Project\Root\Child", StringComparison.Ordinal)
            && collisionHtml.Contains(@"other/Project/Project\Root\Child", StringComparison.Ordinal),
            "Equal area paths across organizations remain qualified.");
        Require(collisionHtml.Contains("org/Project/Main Team", StringComparison.Ordinal)
            && collisionHtml.Contains("other/Project/Main Team", StringComparison.Ordinal)
            && Bodies(collisionHtml)[1].Any(row => row[0].Contains("alex@example.test", StringComparison.Ordinal))
            && Bodies(collisionHtml)[1].Any(row => row[0].Contains("blair@example.test", StringComparison.Ordinal)), "Ambiguous teams and people retain source/identity qualification.");

        var unknown = snapshot with
        {
            WorkItems = [early with { AreaPath = "", Assignee = new("Unmatched Alias"), History = [], Priority = null, ClosedAt = null }]
        };
        Require(HtmlSummaryReportWriter.Write(unknown, "work.md").Contains("Unknown area path", StringComparison.Ordinal)
            && HtmlSummaryReportWriter.Write(unknown, "work.md").Contains("unresolved identity", StringComparison.Ordinal),
            "Missing areas and unmatched actors stay explicitly unknown.");
        Require(MarkdownReportWriter.Write(unknown).Contains("unknown, not zero", StringComparison.Ordinal), "Missing history retains its existing unknown-duration semantics.");
        var empty = snapshot with { WorkItems = [] };
        var emptyTables = Bodies(HtmlSummaryReportWriter.Write(empty, "work.md"));
        Require(emptyTables.All(table => table.Length == 3) && emptyTables[0].All(row => row[1] == "\u2014"),
            "Empty completed Teams snapshots include every roster member exactly once.");

        var unsafeSnapshot = snapshot with
        {
            WorkItems = [early with { Title = "<img src=x> | unsafe", AreaPath = @"Project\<script>|_Area", Url = "javascript:alert(1)" }]
        };
        var safeHtml = HtmlSummaryReportWriter.Write(unsafeSnapshot, "work.md");
        var safeMarkdown = MarkdownReportWriter.Write(unsafeSnapshot);
        Require(safeHtml.Contains("&lt;script&gt;", StringComparison.Ordinal) && !safeHtml.Contains("<script>", StringComparison.Ordinal),
            "Actual area labels are HTML encoded.");
        Require(safeMarkdown.Contains("\\|", StringComparison.Ordinal) && !safeMarkdown.Contains("](<javascript:", StringComparison.Ordinal),
            "Compact details and tables preserve safe Markdown and links.");
        var standalone = snapshot with { Request = snapshot.Request with { View = "details", Roster = [] } };
        Require(!MarkdownReportWriter.Write(standalone).Contains("TEAM WORK ITEM COMPLETION DETAILS", StringComparison.Ordinal)
            && !HtmlSummaryReportWriter.Write(standalone, "work.md").Contains("<details class=\"report-context\">", StringComparison.Ordinal),
            "The reference-format change is restricted to Teams + Work Items.");
        Require(document.OverallRows.Sum(row => row.WorkItems) == 4 && document.SourceRows.Sum(row => row.WorkItems) == 5
            && document.WorkItems.Sum(item => item.ActiveWorkDays) == 7,
            "Presentation does not replace existing source metrics, durations or record attribution.");

        WorkItemReportRecord Item(int id, ReportIdentity actor, string type, string area, int day) => new()
        {
            SourceKey = "root", Organization = "org", Id = id, Title = $"Example item {id}", Type = type, State = "Done",
            Assignee = actor, Priority = 2, CreatedAt = start, ChangedAt = start.AddDays(day), ClosedAt = start.AddDays(day),
            AreaPath = area, IterationPath = @"Project\Sprint", Url = $"https://example.test/work/{id}",
            History = [new("New", start.AddHours(-1), actor), new("Active", start, actor), new("Done", start.AddDays(day), blair)]
        };
    }

    private static string[][][] Bodies(string html) => Regex.Matches(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline)
        .Select(body => Regex.Matches(body.Groups[1].Value, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
            .Select(row => Regex.Matches(row.Groups[1].Value, @"<td>(.*?)</td>", RegexOptions.Singleline)
                .Select(cell => WebUtility.HtmlDecode(cell.Groups[1].Value)).ToArray()).ToArray()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Work item reference format: " + message);
    }
}
