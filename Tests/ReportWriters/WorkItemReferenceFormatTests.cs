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
        var multilineTitle = snapshot with
        {
            WorkItems = [early with { Title = "First line\nSecond [line] | <details> & text", Type = "Task (custom)" }]
        };
        var zeroNameCollision = snapshot with { Request = snapshot.Request with { Roster = [alex, blair, zero with { Name = alex.Name }] } };
        var zeroEncodedName = snapshot with { Request = snapshot.Request with { Roster = [alex, blair, zero with { Name = "Zero <member> & name" }] } };
        foreach (var detailSnapshot in new[] { snapshot, collision, unknown, empty, unsafeSnapshot, multilineTitle, zeroNameCollision, zeroEncodedName })
        {
            VerifyDetailedItems(detailSnapshot);
            VerifyZeroItemPeople(detailSnapshot);
        }
        VerifyHistoryIdentities(snapshot);
        VerifyPersonSummaries(snapshot);
        var mixedDurations = snapshot with
        {
            WorkItems =
            [
                Item(41, alex, "Task", @"Project\Root", 1),
                Item(42, alex, "Task", @"Project\Root", 2),
                Item(43, alex, "Bug", @"Project\Root", 4),
                Item(44, alex, "Product Backlog Item", @"Project\Root", 8),
                Item(45, alex, "Task", @"Project\Root", 1) with { History = [new("New", start, alex), new("Done", start, alex)] },
                Item(46, alex, "Bug", @"Project\Root", 1) with { History = [new("Active", start, alex)] },
                Item(47, alex, "Task", @"Project\Root", 1) with { History = [] }
            ]
        };
        var mixedSummary = PersonSummaries(HtmlDetailedReportWriter.Write(mixedDurations))
            .Single(text => text.Contains("Summary for Alex Example", StringComparison.Ordinal));
        VerifySummaryTables(mixedSummary, 7, [["Task", "4"], ["Bug", "2"], ["Product Backlog Item", "1"]],
            ["15.0", "1.0", "3.8", "8.0", "3.0", "4 of 7", "2"]);
        var missingSummary = PersonSummaries(HtmlDetailedReportWriter.Write(unknown))
            .Single(text => text.Contains("Summary for Unmatched Alias", StringComparison.Ordinal));
        VerifySummaryTables(missingSummary, 1, [["Bug", "1"]], ["0.0", "-", "-", "-", "-", "0 of 1", "1"]);
        var detailedText = Text(CompletionSection(HtmlDetailedReportWriter.Write(snapshot)));
        Require(detailedText.Contains("2026-07-02 00:00:00 - Blair Example", StringComparison.Ordinal)
            && detailedText.Contains("Active \u2192 Done (spent 1d in 'Active')", StringComparison.Ordinal)
            && detailedText.Contains("ongoing at report as-of 2026-08-01 00:00:00", StringComparison.Ordinal),
            "Rich history must preserve the actual transition actor, timestamp, elapsed state duration and fixed as-of boundary.");
        var missingSection = CompletionSection(HtmlDetailedReportWriter.Write(unknown));
        var missingText = Text(missingSection);
        Require(missingText.Contains("Last Changed:", StringComparison.Ordinal)
            && missingText.Contains("Unknown (insufficient state history)", StringComparison.Ordinal)
            && missingText.Contains("No state history was supplied", StringComparison.Ordinal)
            && !missingText.Contains("Closed Date:", StringComparison.Ordinal)
            && !missingText.Contains("Time to Complete:", StringComparison.Ordinal)
            && !missingSection.Contains("class=\"report-label\">Priority:</span>", StringComparison.Ordinal),
            "Missing closure, priority and history must remain explicit or absent, never fabricated.");
        var singleRevision = snapshot with { WorkItems = [early with { History = [new("Active", start, alex)] }] };
        var singleText = Text(CompletionSection(HtmlDetailedReportWriter.Write(singleRevision)));
        Require(singleText.Contains("Currently in Active since 2026-07-01 00:00:00", StringComparison.Ordinal)
            && singleText.Contains("Unknown (insufficient state history)", StringComparison.Ordinal),
            "A singleton history retains its recorded current state without inventing active-work metrics.");
        var unsafeDetails = CompletionSection(HtmlDetailedReportWriter.Write(unsafeSnapshot));
        Require(!unsafeDetails.Contains("<img", StringComparison.Ordinal) && !unsafeDetails.Contains("<script", StringComparison.Ordinal)
            && !unsafeDetails.Contains("href=\"javascript:", StringComparison.Ordinal)
            && Text(unsafeDetails).Contains("<img src=x> | unsafe", StringComparison.Ordinal)
            && unsafeDetails.Contains("Unsafe or unsupported URL", StringComparison.Ordinal),
            "Rich work-item details must encode recorded text and preserve unsafe-link warnings.");
        Require(!HtmlDetailedReportWriter.Write(standalone).Contains("\U0001F4CB Work Item #", StringComparison.Ordinal),
            "Non-Teams work item details must retain their original presentation.");

        WorkItemReportRecord Item(int id, ReportIdentity actor, string type, string area, int day) => new()
        {
            SourceKey = "root", Organization = "org", Id = id, Title = $"Example item {id}", Type = type, State = "Done",
            Assignee = actor, Priority = 2, CreatedAt = start, ChangedAt = start.AddDays(day), ClosedAt = start.AddDays(day),
            AreaPath = area, IterationPath = @"Project\Sprint", Url = $"https://example.test/work/{id}",
            History = [new("New", start.AddHours(-1), actor), new("Active", start, actor), new("Done", start.AddDays(day), blair)]
        };
    }

    private static void VerifyPersonSummaries(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var summaries = PersonSummaries(html);
        Require(summaries.Length == document.OverallRows.Count(row => row.WorkItems != 0),
            "Only people with work items need a detailed summary and time analysis.");
        var alex = summaries.Single(text => text.Contains("Summary for Alex Example", StringComparison.Ordinal));
        VerifySummaryTables(alex, 3, [["Bug", "1"], ["Feature", "1"], ["Task", "1"]],
            ["6.0", "1.0", "2.0", "3.0", "2.0", "3 of 3", "0"]);
        var blair = summaries.Single(text => text.Contains("Summary for Blair Example", StringComparison.Ordinal));
        VerifySummaryTables(blair, 1, [["Product Backlog Item", "1"]],
            ["1.0", "1.0", "1.0", "1.0", "1.0", "1 of 1", "0"]);
        Require(!summaries.Any(text => text.Contains("Summary for Zero Example", StringComparison.Ordinal)),
            "Zero-item people must not receive redundant empty summaries.");
        Require(!MarkdownReportWriter.Write(document).Contains("Summary for Alex Example", StringComparison.Ordinal)
            && !HtmlSummaryReportWriter.Write(document, "report_Detailed.html").Contains("Work Items with Active Work Time", StringComparison.Ordinal)
            && !HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = "details" } })
                .Contains("Summary for Alex Example", StringComparison.Ordinal),
            "New person summaries must be restricted to Teams Work Items detailed HTML.");
    }

    private static void VerifySummaryTables(string summary, int count, string[][] types, string[] statistics)
    {
        var tables = Bodies(summary);
        Require(Text(summary).Contains("Total work items completed: " + count, StringComparison.Ordinal)
            && tables.Length == 2 && tables[0].Length == types.Length
            && tables[0].Zip(types).All(pair => pair.First.SequenceEqual(pair.Second)),
            "The relocated summary must retain the exact deduplicated total and work-item type table.");
        Require(tables[1].Select(row => row[0]).SequenceEqual(new[]
            {
                "Total (known positive durations)", "Minimum", "Average", "Maximum", "Median",
                "Work items with positive active-work time",
                "Work items with unavailable / single-revision active-work duration"
            })
            && tables[1].Select(row => row[1]).SequenceEqual(statistics),
            "The relocated active-work table must retain all original measures, positive-duration statistics, and unavailable counts.");
    }

    private static void VerifyZeroItemPeople(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var people = ReportFoldMarkup.Parse(CompletionSection(html)).Where(fold => fold.Kind == "person").ToArray();
        var summaryRows = Bodies(HtmlSummaryReportWriter.Write(document, "report_Detailed.html"))[1];
        Require(people.Length == summaryRows.Length
            && ReportFoldMarkup.Parse(html).Count(fold => fold.Kind == "person") == people.Length,
            "All members need exactly one completion section, without redundant audit sections.");
        foreach (var row in summaryRows.Where(row => row[1] == "0"))
        {
            var heading = $"\U0001F464 {row[0]}: 0 work item(s) completed";
            var person = people.Single(fold => Text(fold.Content).Trim() == heading);
            Require(Regex.IsMatch(person.Content, @"<h3\b[^>]*class=""report-person-heading""")
                && Regex.IsMatch(person.Content, @"<div class=""report-person-content"">\s*</div>")
                && !person.Content.Contains("<table", StringComparison.Ordinal)
                && !person.Content.Contains("Time Analysis", StringComparison.Ordinal),
                "A zero-item member must display only the count in the same person heading style, without extra identity/statistics details.");
            var id = Regex.Match(person.Content, @"<h3\b[^>]*\bid=""([^""]+)""").Groups[1].Value;
            Require(id.Length != 0 && html.Contains("href=\"#" + id + "\"", StringComparison.Ordinal),
                "Every zero-item roster link must target its actual counted heading, including duplicate or encoded names.");
        }
        Require(!html.Contains("<member>", StringComparison.Ordinal),
            "Simplified zero-person headings must still encode captured names safely.");
    }

    private static string[] PersonSummaries(string html) =>
        ReportFoldMarkup.Parse(CompletionSection(html)).Where(fold => fold.Kind == "person")
            .Select(fold => fold.Content)
            .Where(content => content.Contains("<details class=\"report-item-details\"", StringComparison.Ordinal))
            .Select(content => content[..content.IndexOf("<details class=\"report-item-details\"", StringComparison.Ordinal)])
            .ToArray();

    private static void VerifyDetailedItems(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var summary = HtmlSummaryReportWriter.Write(document, "report_Detailed.html");
        var markdown = MarkdownReportWriter.Write(document);
        var html = HtmlDetailedReportWriter.Write(document);
        var section = CompletionSection(html);
        var headings = Regex.Matches(section, @"<h4 class=""report-line report-record-heading"">(.*?)</h4>", RegexOptions.Singleline)
            .Select(heading => Text(heading.Groups[1].Value)).ToArray();
        Require(headings.Length == document.WorkItems.Length
            && headings.All(heading => heading.StartsWith("\U0001F4CB Work Item #", StringComparison.Ordinal)),
            "Every distinct organization-qualified work item must have one rich entry, despite overlapping source selections.");
        foreach (var (label, icon) in new[]
        {
            ("Title:", "\U0001F4DD"), ("State:", "\U0001F504"), ("Assignee:", "\U0001F464"),
            ("Created Date:", "\U0001F4C5"), ("Time in Active Work:", "\u23F1\uFE0F")
        })
        {
            var fields = Regex.Matches(section, @"<span class=""report-icon"" aria-hidden=""true"">([^<]*)</span> <span class=""report-label"">"
                + Regex.Escape(label) + "</span>", RegexOptions.Singleline);
            Require(fields.Count == document.WorkItems.Length && fields.All(field => Text(field.Groups[1].Value) == icon),
                "Each rich entry must use the matching on-screen field icon: " + label);
        }
        var text = Text(section);
        foreach (var item in document.WorkItems)
        {
            var record = item.Entity.Record;
            Require(headings.Contains($"\U0001F4CB Work Item #{record.Id.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} - {record.Title} ({record.Type})")
                && text.Contains("Title: " + record.Title, StringComparison.Ordinal)
                && text.Contains("State: " + record.State, StringComparison.Ordinal)
                && text.Contains(record.Assignee.DisplayName, StringComparison.Ordinal),
                "Each heading must repeat the full title followed by the parenthesized type, while retaining the separate Title field and assignee.");
            var active = item.ActiveWorkDays is { } days
                ? days.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " days"
                : "Unknown (insufficient state history)";
            Require(text.Contains("Time in Active Work: " + active, StringComparison.Ordinal),
                "Detailed presentation must use the existing active-work calculation.");
        }
        Require(!html.Contains("All contributor work item details", StringComparison.Ordinal)
            && !html.Contains("Captured manager / report roster", StringComparison.Ordinal)
            && html.Contains("TEAM ROSTER", StringComparison.Ordinal)
            && html.Contains("Captured inputs and export query context", StringComparison.Ordinal)
            && html.Contains("Selected sources", StringComparison.Ordinal)
            && html.Contains("Coverage and metric definitions", StringComparison.Ordinal)
            && (document.WorkItems.IsEmpty || section.Contains("Source-qualified key", StringComparison.Ordinal))
            && summary == HtmlSummaryReportWriter.Write(document, "report_Detailed.html")
            && markdown == MarkdownReportWriter.Write(document)
            && !markdown.Contains("\U0001F4CB Work Item #", StringComparison.Ordinal)
            && markdown.Contains("All contributor work item details", StringComparison.Ordinal)
            && HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = "details" } })
                .Contains("All contributor work item details", StringComparison.Ordinal),
            "Teams detailed HTML omits duplicate audit/roster sections while retaining its top roster, other context, summary, legacy Markdown and non-Teams details.");
        var items = ReportFoldMarkup.Parse(section).Where(fold => fold.Kind == "item").ToArray();
        Require(items.Length == document.WorkItems.Length, "Every source-qualified item must have exactly one consolidated fold.");
        foreach (var item in document.WorkItems)
        {
            var record = item.Entity.Record;
            var evidence = items.Single(fold => Text(fold.Content).Contains("Source-qualified key: " + record.EntityKey + "\n", StringComparison.Ordinal)).Content;
            var evidenceText = Text(evidence);
            foreach (var field in new[]
            {
                "Recorded field evidence", "ID: " + record.Id, "Title: " + record.Title, "Type: " + record.Type,
                "State: " + record.State, "Created date: " + record.CreatedAt.ToString("O"),
                "Last changed: " + record.ChangedAt.ToString("O")
            })
                Require(evidenceText.Contains(field, StringComparison.Ordinal),
                    "Moving the audit records must preserve exact field evidence inside the correct item fold: " + field);
            var histories = Bodies(evidence);
            Require(item.Periods.IsEmpty ? histories.Length == 0
                : histories.Length == 2 && histories[0].Length == item.Periods.Length
                    && histories[0].Select(row => row[0]).SequenceEqual(item.Periods.Select(period => period.State))
                    && histories[0].Select(row => row[1]).SequenceEqual(item.Periods.Select(period => period.EnteredAt.ToString("O")))
                    && histories[0].Select(row => row[7]).SequenceEqual(item.Periods.Select(period => period.Days.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))),
                "Every item must retain its full recorded history table and state totals, in addition to its readable timeline.");
        }
        if (document.WorkItems.Length > 0)
            Require(section.Contains("State Change History", StringComparison.Ordinal),
                "Member details must include work-item histories before the global summaries.");
        using var output = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, output);
        Require(output.ToString() == html, "Streamed detailed downloads must preserve every rich entry.");
    }

    private static void VerifyHistoryIdentities(ReportSnapshot baseline)
    {
        static ReportIdentity WithPrincipal(ReportIdentity identity) =>
            identity with { PrincipalName = "principal-" + identity.Email };
        var snapshot = baseline with
        {
            Request = baseline.Request with { Roster = baseline.Request.Roster.Select(WithPrincipal).ToImmutableArray() },
            WorkItems = baseline.WorkItems.Select(item => item with
            {
                Assignee = WithPrincipal(item.Assignee),
                History = item.History.Select(revision => revision with { Actor = WithPrincipal(revision.Actor) }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var summary = HtmlSummaryReportWriter.Write(document, "report_Detailed.html");
        var html = HtmlDetailedReportWriter.Write(document);
        var histories = HistorySections(html);
        Require(histories.Length == document.WorkItems.Length * 2
            && histories.All(history => !history.Contains("principal:", StringComparison.Ordinal)
                && !history.Contains("ID:", StringComparison.Ordinal)),
            "Both the member timelines and relocated history tables must omit Principal and ID annotations.");
        foreach (var actor in document.WorkItems.SelectMany(item => item.Periods.Select(period => period.Actor)).Distinct())
            Require(histories.Any(history => history.Contains(actor.Name, StringComparison.Ordinal)
                && history.Contains("email: " + actor.Email, StringComparison.Ordinal)),
                "History must preserve each actor's recorded name and email, including the transition and current-state actors.");
        Require(Text(html).Contains("principal: principal-alex@example.test", StringComparison.Ordinal)
            && Text(html).Contains("ID: alex", StringComparison.Ordinal)
            && summary == HtmlSummaryReportWriter.Write(document, "report_Detailed.html"),
            "Identity details outside history and summary HTML must remain unchanged.");
        var other = HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = "details" } });
        Require(HistorySections(other).All(history => history.Contains("principal:", StringComparison.Ordinal)
                && history.Contains("ID:", StringComparison.Ordinal))
            && MarkdownReportWriter.Write(document).Contains("principal: principal-alex@example.test; ID: alex", StringComparison.Ordinal),
            "Non-Teams history and legacy Markdown must keep their existing full identity format.");
        Require(document.WorkItems.SelectMany(item => item.Periods).All(period => period.Actor.PrincipalName is not null && period.Actor.Id is not null),
            "Presentation must not remove identity metadata from captured records.");

        static string[] HistorySections(string content) =>
            Regex.Matches(content, @"<h5\b[^>]*>(.*?)</h5>(.*?)(?=<h[1-6]\b|$)", RegexOptions.Singleline)
                .Where(section => Text(section.Groups[1].Value).EndsWith("State Change History", StringComparison.OrdinalIgnoreCase))
                .Select(section => Text(section.Groups[2].Value)).ToArray();
    }

    private static string CompletionSection(string html)
    {
        var start = html.IndexOf("TEAM WORK ITEM COMPLETION DETAILS BY PERSON", StringComparison.Ordinal);
        var end = html.IndexOf("TEAM WORK ITEM SUMMARY BY AREA PATH", StringComparison.Ordinal);
        Require(start >= 0 && end > start, "Work-item completion details must retain their original report section.");
        return html[start..end];
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static string[][][] Bodies(string html) => Regex.Matches(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline)
        .Select(body => Regex.Matches(body.Groups[1].Value, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
            .Select(row => Regex.Matches(row.Groups[1].Value, @"<td>(.*?)</td>", RegexOptions.Singleline)
                .Select(cell => WebUtility.HtmlDecode(cell.Groups[1].Value)).ToArray()).ToArray()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Work item reference format: " + message);
    }
}
