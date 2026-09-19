using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class ReportWriterFixtures
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ReportIdentity Alice = new("Same Name", "alice@example.test", "alice-id", "alice@example.test");
    private static readonly ReportIdentity Bob = new("Same Name", "bob@example.test", "bob-id", "bob@example.test");
    private static readonly ReportIdentity Zero = new("Zero Activity", "zero@example.test", "zero-id");
    private static readonly ReportIdentity Unknown = new("Unknown");
    private static readonly ReportSource SourceA = new("source-a", "org-a", "project", "Area\\Parent", Team: "Team A");
    private static readonly ReportSource SourceB = new("source-b", "org-a", "project", "Area\\Parent\\Child", Team: "Team B");
    private static readonly ReportSource SourceC = new("source-c", "org-b", "project", "Area", Team: "Team C");

    public static void Run()
    {
        WorkItemMetricsAndHistories();
        GitAttributionFilesEvidenceAndTrends();
        IncidentMetricsAndActionEvidence();
        TextAndLinkSafety();
        EmptyMissingInvalidAndConflictingInputs();
        LargeOutputAndCancellation();
        BoundedConstructionAndUtf8Preflight();
        EffectiveQueryBoundsMetadata();
        CultureIndependence();
        SkippedSourceDisclosureAndValidation();
    }

    private static void SkippedSourceDisclosureAndValidation()
    {
        var baseline = GitSnapshot();
        var skipped = new ReportSource("skipped", "org", "project", "<script>missing|repo</script>", "main");
        var snapshot = baseline with
        {
            Request = baseline.Request with { Sources = baseline.Request.Sources.Add(skipped) },
            SkippedSources = [new(skipped.Key, "HTTP 404 <img src=x onerror=alert(1)> | unavailable")]
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        Equal(ReportDocumentBuilder.Build(baseline).SourceRows.Length, document.SourceRows.Length,
            "Skipped repositories must not generate zero Teams rows.");
        var html = HtmlSummaryReportWriter.Write(document, "report.md");
        var markdown = MarkdownReportWriter.Write(document);
        Contains(html, "&lt;script&gt;missing|repo&lt;/script&gt;", "Skipped-source names must be encoded.");
        Contains(html, "&lt;img src=x onerror=alert(1)&gt;", "Skipped-source reasons must be encoded.");
        NotContains(html, "<script>", "Skipped metadata must not become executable HTML.");
        Contains(markdown, "\\| unavailable", "Skipped error pipes must not break Markdown tables.");
        Contains(markdown, "Skipped sources - excluded from totals", "Omissions must be visible in Markdown.");
        Contains(html, "Skipped sources - excluded from totals", "Omissions must be visible in HTML.");
        foreach (var invalid in new[]
        {
            snapshot with { SkippedSources = default },
            snapshot with { SkippedSources = [new("unknown", "HTTP 404")] },
            snapshot with { SkippedSources = [new(skipped.Key, "HTTP 404"), new(skipped.Key, "HTTP 403")] },
            snapshot with { SkippedSources = [new(skipped.Key, "")] },
            baseline with { SkippedSources = [new(baseline.Commits[0].SourceKey, "HTTP 404")] }
        })
            Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(invalid),
                "Invalid or counted skipped-source metadata must not produce a report.");
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(snapshot with
        {
            SkippedSources = [new(skipped.Key, new string('x', 1024 * 1024 + 1))]
        }), "Skipped reasons must obey the existing text-size safeguards.");
    }

    private static void WorkItemMetricsAndHistories()
    {
        var snapshot = WorkSnapshot();
        var document = ReportDocumentBuilder.Build(snapshot);
        Equal(4, document.WorkItems.Length, "Organization-qualified work item deduplication.");
        Equal(5, document.SourceRows.Sum(row => row.WorkItems), "Source memberships must survive overlap.");
        Equal(4, document.OverallRows.Sum(row => row.WorkItems), "Overall must not sum overlapping source rows.");
        Equal(2, document.WorkItems.Single(item => item.Entity.Record.EntityKey == "ORG-A:1").Entity.SourceKeys.Length, "Work item overlapping memberships.");
        var row = document.OverallRows.Single(item => item.Contributor.Contains("alice@example.test", StringComparison.Ordinal));
        Equal(2, row.WorkItems, "Alias assignee resolves to roster.");
        Equal(1, row.Bugs, "Bug count.");
        Equal(1, row.Tasks, "Task count.");
        Equal(0, document.OverallRows.Single(item => item.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).WorkItems, "Roster zero activity.");
        var stats = ReportDocumentBuilder.DurationStatistics(document.WorkItems
            .Where(item => item.ActiveWorkDays > 0).Select(item => item.ActiveWorkDays!.Value));
        Equal(3, stats.Count, "Duration denominator excludes unknown and zero durations.");
        Near(14, stats.Total, "Distinct active days total.");
        Near(2, stats.Minimum!.Value, "Minimum active days.");
        Near(14d / 3, stats.Average!.Value, "Weighted overall average, not average of contributor averages.");
        Near(8, stats.Maximum!.Value, "Maximum active days.");
        Near(4, stats.Median!.Value, "Median active days.");
        var first = document.WorkItems.Single(item => item.Entity.Record.EntityKey == "ORG-A:1");
        Equal(5, first.Periods.Length, "Repeated state revisions consolidate, every transition remains.");
        Equal("Done", first.Periods[^1].State, "Final state retained.");
        Near(5, first.Periods[^1].Days, "Final state duration uses snapshot as-of, not wall clock.");
        Equal("In Progress", first.Periods[2].State, "Intermediate state retained.");
        Equal(Bob, first.Periods[2].Actor, "State transition actor retained.");
        Equal<double?>(null, document.WorkItems.Single(item => item.Entity.Record.Id == 3).ActiveWorkDays, "Missing history is unknown, not zero.");
        var markdown = MarkdownReportWriter.Write(document);
        Contains(markdown, "State change history", "All work item histories.");
        Contains(markdown, "Transition actor", "Transition actor column.");
        Contains(markdown, "Total time by state", "State duration totals.");
        Contains(markdown, "Time to complete", "Completion duration.");
        Contains(markdown, "Captured manager / report roster", "Roster metadata.");
        Contains(markdown, "Zero Activity", "Zero member detail group.");
        Contains(markdown, snapshot.Request.RunId.ToString("D"), "Captured run identity.");
        Contains(markdown, "Export capture anchor", "Capture must not claim an observed legacy API execution timestamp.");
        Contains(markdown, "Effective export start", "Bounds are explicitly fresh-export bounds.");
        Contains(markdown, "not an observation", "Legacy per-call timestamp limitation is explicit.");
        Contains(markdown, "2026-07-11T00:00:00", "Shared as-of metadata.");
        Contains(markdown, "Area\\\\Parent", "Native Markdown preserves backslashes.");
        Contains(markdown, "Unknown", "Unknown assignee remains explicit.");
        var html = HtmlSummaryReportWriter.Write(document, "work.md");
        AssertSummaryShape(html, "workItems");
        NotContains(html, "State change history", "Detailed histories must not inflate HTML.");
        Contains(html, "Zero Activity", "HTML roster zero row.");
        Contains(html, "Export capture anchor", "HTML distinguishes capture from observed API calls.");
        Contains(html, "Effective export end", "HTML labels fresh-export bounds.");
        NotContains(html, ">Executed at:", "HTML must not mislabel the capture anchor.");
        var defaulted = snapshot with { Request = snapshot.Request with { RequestedStart = null, RequestedEnd = null } };
        Contains(HtmlSummaryReportWriter.Write(defaulted, "defaults.md"), "not supplied; see effective export bounds",
            "Unspecified dates do not imply observed UI defaults or assume every source uses the capture anchor.");
        Contains(html, "Team A, Team B", "Reference overall rows retain compact selected-team context.");
        foreach (var state in new[] { "Active", "Committed", "In Progress" })
        {
            var singleRecord = snapshot.WorkItems[0] with
            {
                State = state, ClosedAt = null, ChangedAt = Start,
                History = [new(state, Start, Alice)]
            };
            var singleton = ReportDocumentBuilder.Build(snapshot with { WorkItems = [singleRecord] });
            Equal<double?>(null, singleton.WorkItems[0].ActiveWorkDays,
                $"A singleton {state} revision is excluded from active-work statistics, matching existing UI.");
            Equal(1, singleton.WorkItems[0].Periods.Length, "The singleton state remains available as detailed evidence.");
            Contains(MarkdownReportWriter.Write(singleton), "| Work items with positive active-work time | 0 of 1 |",
                "A singleton active state must not enter the rendered duration denominator.");

            var repeated = ReportDocumentBuilder.Build(snapshot with
            {
                WorkItems = [singleRecord with
                {
                    ChangedAt = Start.AddDays(1),
                    History = [new(state, Start, Alice), new(state, Start.AddDays(1), Bob)]
                }]
            });
            var repeatedItem = repeated.WorkItems[0];
            Equal(2, repeatedItem.Entity.Record.History.Length, "Both raw revisions remain in the immutable input.");
            Equal(1, repeatedItem.Periods.Length, "Two identical-state revisions consolidate to one period.");
            Near(10, repeatedItem.ActiveWorkDays!.Value,
                $"Two raw {state} revisions qualify for active-work statistics even with only one consolidated period.");
            Equal(Start, repeatedItem.Periods[0].EnteredAt, "An unchanged-state revision must not reset the state's entry time.");
            Equal(Alice, repeatedItem.Periods[0].Actor, "An unchanged-state revision must not replace the original entry actor.");
            var qualifying = ReportDocumentBuilder.DurationStatistics(
                new[] { singleton.WorkItems[0].ActiveWorkDays, repeatedItem.ActiveWorkDays }
                    .Where(value => value > 0).Select(value => value!.Value));
            Equal(1, qualifying.Count, "Only the two-revision item belongs to the active-duration denominator.");
            Near(10, qualifying.Total, "Repeated-state revisions contribute the full as-of duration exactly once.");
            Near(10, qualifying.Average!.Value, "The excluded singleton cannot dilute the average.");
            Contains(MarkdownReportWriter.Write(repeated), "| Work items with positive active-work time | 1 of 1 |",
                "Rendered statistics use the original revision count, not consolidated-period count.");
        }
    }

    private static void GitAttributionFilesEvidenceAndTrends()
    {
        var snapshot = GitSnapshot();
        var document = ReportDocumentBuilder.Build(snapshot);
        Equal(3, document.Commits.Length, "Same SHA across repositories is distinct; branch overlap is deduplicated.");
        Equal(4, document.SourceRows.Sum(row => row.Commits), "Git per-source memberships preserved.");
        Equal(3, document.OverallRows.Sum(row => row.Commits), "Git overall deduplication.");
        Equal(2, document.PullRequests.Length, "Source-qualified completed PR deduplication.");
        Equal(2, document.OverallRows.Sum(row => row.PullRequests), "Distinct PR totals.");
        var pair = document.OverallRows.Single(row => row.Contributor.StartsWith("Author:", StringComparison.Ordinal));
        Equal(1, pair.Commits, "Equal display names with different emails remain an author/committer pair.");
        Equal(0, pair.PullRequests, "PR completion must not be guessed from pair membership.");
        var alice = Member(document, "alice@example.test");
        Equal(1, alice.Commits, "Alice commit credit.");
        Equal(1, alice.PullRequests, "PR completion alias resolves only to the correct person.");
        var bob = Member(document, "bob@example.test");
        Equal(1, bob.Commits, "Bob commit credit is not inflated by Alice's equal display name.");
        Equal(0, bob.PullRequests, "Authorship is not completion.");
        Equal(1, document.OverallRows.Single(row => row.Contributor == "Unknown").PullRequests, "Unknown completion actor stays explicit.");
        Equal(0, document.OverallRows.Single(row => row.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).Commits, "Git roster zero.");
        Equal(182L, document.Commits.Sum(commit => commit.Added), "All changed-file additions.");
        Equal(26L, document.Commits.Sum(commit => commit.Deleted), "All changed-file deletions.");
        Equal(27, document.Commits.Sum(commit => commit.Entity.Record.Files.Length), "Every changed file counted.");

        var expected = new Dictionary<string, int> { ["daily"] = 3, ["monthly"] = 2, ["yearly"] = 2 };
        foreach (var interval in expected)
        {
            var trend = ReportDocumentBuilder.Build(snapshot with { Request = snapshot.Request with { View = "trends", TrendPeriod = interval.Key } });
            Equal(interval.Value, trend.GitTrends.Length, "All trend periods: " + interval.Key);
            Equal(3, trend.GitTrends.Sum(period => period.Commits), "Trend commit count is not PR count.");
            Equal(156L, trend.GitTrends.Sum(period => period.ServiceAdded), "Existing service split.");
            Equal(24L, trend.GitTrends.Sum(period => period.ServiceDeleted), "Existing service deletion split.");
            Equal(26L, trend.GitTrends.Sum(period => period.TestAdded), "Existing test split.");
            Equal(2L, trend.GitTrends.Sum(period => period.TestDeleted), "Existing test deletion split.");
            var htmlTrend = HtmlSummaryReportWriter.Write(trend, "git.md");
            Equal(3, Regex.Matches(htmlTrend, "<table>").Count, "Trends has exactly one period appendix.");
            Contains(htmlTrend, "6.43:1", "Service/test ratio includes added and deleted lines.");
            if (interval.Key == "daily") Contains(htmlTrend, "0:0", "Zero service and test lines retain the existing 0:0 ratio.");
            var trendMarkdown = MarkdownReportWriter.Write(trend);
            Contains(trendMarkdown, "Full commit message", "Trends still retains all detailed records.");
        }
        var markdown = MarkdownReportWriter.Write(document);
        Contains(markdown, "file13", "Eleventh and subsequent files are not truncated.");
        Contains(markdown, "λ", "Unicode filenames survive.");
        Contains(markdown, snapshot.Commits[0].Message, "Full multiline message, including backticks, is exact inside a safe code fence.");
        Contains(markdown, "````text", "Message containing triple backticks requires a longer code fence.");
        Contains(markdown, "closedBy", "Completion identity evidence.");
        Contains(markdown, "closedDate", "Completion date evidence.");
        Contains(markdown, "Parent count", "Parent count remains separate from authoritative PR evidence.");
        Contains(markdown, "ORG-A:REPO-TWO:SAME-SHA", "Repository-qualified commit evidence.");
        var html = HtmlSummaryReportWriter.Write(document, "git.md");
        AssertSummaryShape(html, "gitCommits");
        Contains(html, "\U0001F464 Same Name", "Summary HTML preserves the author icon and identity.");
        Contains(html, "\u270D\uFE0F Same Name", "Summary HTML preserves the committer icon and identity.");
        NotContains(html, "file13", "All-file details stay in Markdown, not the HTML summary.");

        var highCounts = snapshot with
        {
            Commits = [snapshot.Commits[0] with { Files = [new("a.cs", "edit", int.MaxValue, 0), new("b.cs", "edit", int.MaxValue, 0)] }],
            PullRequests = []
        };
        Equal(2L * int.MaxValue, ReportDocumentBuilder.Build(highCounts).Commits[0].Added, "Line aggregation must not overflow Int32.");
        Contains(MarkdownReportWriter.Write(highCounts), "| Inf |", "Service-only trend uses the existing Inf ratio.");
        var ambiguous = ReportDocumentBuilder.Build(snapshot with
        {
            Commits = [], PullRequests = [snapshot.PullRequests[0] with { CompletedBy = new("Same Name") }]
        });
        Equal(0, Member(ambiguous, "alice@example.test").PullRequests, "An ambiguous name cannot become Alice's completion.");
        Equal(0, Member(ambiguous, "bob@example.test").PullRequests, "An ambiguous name cannot become Bob's completion.");
        Equal(1, ambiguous.OverallRows.Single(row => row.Contributor.Contains("unresolved identity", StringComparison.Ordinal)).PullRequests, "Ambiguous actor remains explicit.");
        var conflicting = ReportDocumentBuilder.Build(snapshot with
        {
            Request = snapshot.Request with { Roster = [Alice, Bob with { Id = Alice.Id }] },
            Commits = [snapshot.Commits[0], snapshot.Commits[0] with { Id = "other", Author = Bob with { Id = Alice.Id }, Committer = Bob with { Id = Alice.Id } }],
            PullRequests = []
        });
        Equal(2, conflicting.OverallRows.Length, "Conflicting emails with a shared unqualified identity ID must not merge.");
        True(conflicting.OverallRows.All(row => row.Commits == 1), "Both distinct email actors retain their own commit.");
        var outsideCommitter = ReportDocumentBuilder.Build(snapshot with
        {
            Commits = [snapshot.Commits[0] with { Committer = new("Outside Committer", "alice@outside.test", "outside-id") }],
            PullRequests = [snapshot.PullRequests[0]]
        });
        Equal(1, Member(outsideCommitter, "alice@example.test").PullRequests, "Captured roster resolution takes precedence over an outside committer's coincident alias.");
        var firstIdOnly = new ReportIdentity("Same Name", Id: "id-only-a");
        var secondIdOnly = new ReportIdentity("Same Name", Id: "id-only-b");
        var idOnly = ReportDocumentBuilder.Build(snapshot with
        {
            Request = snapshot.Request with { View = "details", Roster = [] },
            Commits =
            [
                snapshot.Commits[0] with { Author = firstIdOnly, Committer = firstIdOnly },
                snapshot.Commits[0] with { Id = "other-id-only", Author = secondIdOnly, Committer = secondIdOnly }
            ],
            PullRequests = []
        });
        Equal(2, idOnly.OverallRows.Length, "Distinct IDs without email must not merge through equal display names in Details.");
        True(idOnly.OverallRows.All(row => row.Commits == 1), "ID-only actors keep separate counts.");
    }

    private static void IncidentMetricsAndActionEvidence()
    {
        var snapshot = IncidentSnapshot();
        var document = ReportDocumentBuilder.Build(snapshot);
        Equal(6, document.Incidents.Length, "Silo-qualified deduplication.");
        Equal("Active", document.IncidentGroups[0].Contributor, "Active detail group must be first.");
        Equal(2, document.IncidentGroups[0].Incidents.Length, "Active distinct from period flags.");
        Equal(6, document.IncidentActions.Length, "Action evidence deduplicates repeated source membership, not separate action IDs.");
        var alice = Member(document, "alice@example.test");
        Equal(1, alice.Active, "Active credits current owner.");
        Equal(1, alice.Created, "Created credits current owner, not metadata creator.");
        Equal(1, alice.Resolved, "Resolved credits recorded resolution actor.");
        Equal(1, alice.Mitigated, "Mitigated credits recorded mitigation actor.");
        Equal(2, alice.Contributed, "Alias and full email are the same actor; repeated actions count once per incident.");
        var bob = Member(document, "bob@example.test");
        Equal(1, bob.Contributed, "Different actor can receive credit for the same incident.");
        Equal(1, bob.Active, "Equal-named actors remain separate.");
        Equal(1, document.OverallRows.Single(row => row.Contributor == "Unknown").Contributed, "Unknown action never guessed.");
        Equal(0, document.OverallRows.Single(row => row.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).Contributed, "ICM roster zero.");
        var severity = document.IncidentStatistics.Single(item => item.Severity == 2);
        Equal(4, severity.Count, "Distinct severity denominator.");
        Equal(3, severity.Detect.Count, "Invalid negative TTD is excluded.");
        Near(11, severity.Detect.Average!.Value, "Weighted TTD from 1,2,30 hours, not contributor averages.");
        Near(1, severity.Detect.Minimum!.Value, "TTD minimum.");
        Near(30, severity.Detect.Maximum!.Value, "TTD maximum.");
        Near(4, severity.Mitigate.Average!.Value, "TTM from incident durations 2 and 6.");
        Near(15, severity.Resolve.Average!.Value, "TTR from 10 and 20 hours.");
        var unknown = document.IncidentStatistics.Single(item => item.Severity is null);
        Equal(0, unknown.Detect.Count, "Missing severity and missing duration are not severity zero or zero hours.");
        Equal<double?>(null, unknown.Detect.Average, "Missing average is not zero.");
        Equal(2, document.ActiveTeamStatistics.Length, "Multi-team Active severity and TTD groups.");
        var markdown = MarkdownReportWriter.Write(document);
        var activeOffset = markdown.IndexOf("### Active\n", StringComparison.Ordinal);
        var closedOffset = markdown.IndexOf("### Same Name", activeOffset, StringComparison.Ordinal);
        True(activeOffset >= 0 && closedOffset > activeOffset, "Markdown Active group precedes contributor groups.");
        Contains(markdown, "Active severity by owning team", "Active multi-team severity table.");
        Contains(markdown, "Active TTD by owning team and severity", "Active multi-team TTD table.");
        Contains(markdown, "TTM by severity", "TTM min/avg/max.");
        Contains(markdown, "TTR by severity", "TTR min/avg/max.");
        Contains(markdown, "Recorded incident action history", "Action evidence histories.");
        Contains(markdown, "action-only", "Action-only incidents are included.");
        Contains(markdown, "IsOutage", "All typed visible metadata.");
        Contains(markdown, "NotTheOwner", "Original creator metadata retained without receiving Created credit.");
        Contains(markdown, snapshot.Incidents[0].Mitigation, "Multiline mitigation retained exactly.");
        Contains(markdown, "action-6", "Every action record is included.");
        var html = HtmlSummaryReportWriter.Write(document, "incidents.md");
        AssertSummaryShape(html, "incidentManagement");
        Contains(html, "ACROSS ALL 3 ICM GROUP(S)", "Dynamic owning-team count, not reference hard-code.");
        Contains(html, "Incident Resolutions Analysis", "Meaningful incident title.");
        NotContains(html, "TTM by severity", "Detailed severity/time data stays out of summary HTML.");
        NotContains(html, "action-6", "Detailed action history stays in Markdown.");
    }

    private static void TextAndLinkSafety()
    {
        const string hostile = "<img src=x onerror=\"boom\"> [x](javascript:alert(1)) | `text` \\ path\nsecond line Ω";
        var request = Request("workItems") with
        {
            Manager = hostile,
            Sources = [SourceA with { Scope = hostile }],
            Roster = [new(hostile, "hostile@example.test")]
        };
        var item = WorkItem(90, "org-a", "source-a", new(hostile, "hostile@example.test"), 2) with
        {
            Title = hostile, Url = "javascript:alert(1)", AreaPath = "a|b\nc\r\nd", IterationPath = "e`f"
        };
        var snapshot = new ReportSnapshot { Request = request, AsOf = Start.AddDays(10), WorkItems = [item] };
        var markdown = MarkdownReportWriter.Write(snapshot);
        NotContains(markdown, "<img", "Markdown never emits raw unsafe HTML for prose/table fields.");
        Contains(markdown, "&lt;img", "Markdown angle bracket escaping.");
        Contains(markdown, "\\|", "Markdown pipe escaping.");
        Contains(markdown, "\\`text\\`", "Markdown backtick escaping.");
        Contains(markdown, "&#10;", "Table multiline data is represented without raw br tags.");
        Contains(markdown, "Unsafe or unsupported URL", "Unsafe URL rejection is visible, not silently linked.");
        NotContains(markdown, "](<javascript:", "No executable Markdown URL.");
        var html = HtmlSummaryReportWriter.Write(snapshot, "report #1 & λ.md");
        NotContains(html, "<img", "HTML must encode text context.");
        Contains(html, "&lt;img src=x onerror=&quot;boom&quot;&gt;", "HTML context encoding.");
        Contains(html, "./report%20%231%20%26%20%CE%BB.md", "Matching sibling filename is URI encoded.");
        foreach (var invalid in new[] { "javascript:evil.md", "..\\escape.md", "/escape.md", "bad\n.md", "report.exe" })
            Throws<ArgumentException>(() => HtmlSummaryReportWriter.Write(snapshot, invalid), "Invalid sibling filename must fail.");
        foreach (var unsafeUrl in new[] { "data:text/html,evil", "file:///c:/temp", "https://user:password@example.test/", "https://example.test/\nscript" })
            NotContains(MarkdownReportWriter.Write(snapshot with { WorkItems = [item with { Url = unsafeUrl }] }),
                "](<", "Unsupported, credential-bearing, or control-character URL must not be linked.");
        var longUrl = "https://example.test/path?q=" + new string('a', 2048) + "&x=(pipe|backtick`)";
        var linked = MarkdownReportWriter.Write(snapshot with { WorkItems = [item with { Url = longUrl }] });
        Contains(linked, "](<https://example.test/path?q=", "Safe URL is linked.");
        Contains(linked, "%7C", "Pipes cannot break Markdown URL syntax.");
        Contains(linked, "%60", "Backticks cannot break Markdown URL syntax.");
        Contains(linked, new string('a', 2048), "Long URLs are not truncated.");
    }

    private static void EmptyMissingInvalidAndConflictingInputs()
    {
        foreach (var analysis in new[] { "workItems", "gitCommits", "incidentManagement" })
        {
            var snapshot = new ReportSnapshot { Request = Request(analysis), AsOf = Start.AddDays(10) };
            var document = ReportDocumentBuilder.Build(snapshot);
            Equal(3, document.OverallRows.Length, "An empty successful Teams snapshot still contains every roster member.");
            True(document.OverallRows.All(row => row.Commits + row.PullRequests + row.WorkItems + row.Active + row.Created + row.Contributed == 0), "True empty metrics are zero.");
            AssertSummaryShape(HtmlSummaryReportWriter.Write(document, "empty.md"), analysis);
            Contains(MarkdownReportWriter.Write(document), "Zero Activity", "Empty roster member included in full Markdown.");
        }
        foreach (var analysis in new[] { "workItems", "incidentManagement" })
            Throws<InvalidOperationException>(() => MarkdownReportWriter.Write(new ReportSnapshot
            {
                Request = Request(analysis) with { View = "trends" }, AsOf = Start
            }), "Placeholder trends must not be exported.");
        var work = WorkSnapshot();
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(work with
        {
            WorkItems = work.WorkItems.Add(work.WorkItems[0] with { Title = "Conflicting source detail" })
        }), "Conflicting overlapping records are explicit errors.");
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(work with { WorkItems = default }), "Uninitialized collections are not successful empty data.");
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(work with
        {
            WorkItems = [work.WorkItems[0] with { SourceKey = "missing-source" }]
        }), "Uncaptured source must fail.");
        var git = GitSnapshot();
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(git with
        {
            PullRequests = [git.PullRequests[0] with { CompletedAt = git.Request.End.AddTicks(1) }]
        }), "Out-of-window completion evidence must fail.");
        var incidents = IncidentSnapshot();
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(incidents with
        {
            IncidentActions = [incidents.IncidentActions[0] with { Timestamp = incidents.Request.Start.AddTicks(-1) }]
        }), "Out-of-window actions must fail.");
        Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(incidents with
        {
            IncidentActions = [incidents.IncidentActions[0] with { IncidentId = "missing-incident" }]
        }), "Orphan action evidence cannot become a misleading count.");
    }

    private static void LargeOutputAndCancellation()
    {
        var template = GitSnapshot().Commits[0];
        const int commitCount = 5000;
        var commits = Enumerable.Range(1, commitCount).Select(index => template with { Id = $"large-commit-{index:D4}" }).ToImmutableArray();
        var snapshot = new ReportSnapshot
        {
            Request = Request("gitCommits"), AsOf = Start.AddDays(10), Commits = commits
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var markdown = MarkdownReportWriter.Write(snapshot);
        Equal(commitCount, Regex.Matches(markdown, "\U0001F504 Commit: large-co").Count, "Every large-report commit is rendered through the parent's snapshot API.");
        Equal(commitCount, Regex.Matches(markdown, "file13").Count, "All 65,000 changed files survive the realistic 5,000-commit report.");
        Contains(markdown, "large-commit-5000", "Last record is present.");
        True(markdown.Length > 1_000_000, "Large fixture must exercise substantial output.");
        var html = HtmlSummaryReportWriter.Write(snapshot, "large.md");
        True(html.Length < markdown.Length / 10, "HTML remains a summary, not a second detail dump.");
        Equal(commitCount, document.OverallRows.Sum(row => row.Commits), "Large report exact counts.");
        True((long)Encoding.UTF8.GetByteCount(markdown) + Encoding.UTF8.GetByteCount(html) <= ReportArchiveBuilder.MaximumReportBytes,
            "The two real writer outputs fit the combined streamed-export UTF-8 budget.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => ReportDocumentBuilder.Build(snapshot, cancelled.Token), "Cancelled build.");
        Throws<OperationCanceledException>(() => MarkdownReportWriter.Write(document, cancelled.Token), "Cancelled Markdown.");
        Throws<OperationCanceledException>(() => HtmlSummaryReportWriter.Write(document, "large.md", cancelled.Token), "Cancelled HTML.");
        using var during = new CancellationTokenSource();
        IEnumerable<double> Durations()
        {
            yield return 1;
            during.Cancel();
            yield return 2;
        }
        Throws<OperationCanceledException>(() => ReportDocumentBuilder.DurationStatistics(Durations(), during.Token), "Cancellation is honored during duration enumeration.");
        var tooLarge = WorkItem(91, "org-a", "source-a", Alice, 2) with { Title = new string('x', ReportRenderingLimits.MaximumInputCharacters + 1) };
        Throws<ReportSizeLimitException>(() => MarkdownReportWriter.Write(new ReportSnapshot
        {
            Request = Request("workItems"), AsOf = Start.AddDays(10), WorkItems = [tooLarge]
        }), "Input resource bound must fail explicitly.");
        var expansion = tooLarge with { Title = new string('<', 100_000) };
        Throws<ReportSizeLimitException>(() => MarkdownReportWriter.Write(new ReportSnapshot
        {
            Request = Request("workItems"), AsOf = Start.AddDays(10),
            WorkItems = Enumerable.Range(1, 50).Select(index => expansion with { Id = index }).ToImmutableArray()
        }), "Escaping expansion must obey output limits without silent truncation.");
    }

    private static void BoundedConstructionAndUtf8Preflight()
    {
        var template = GitSnapshot().Commits[0] with { Files = [] };
        var tooMany = new ReportSnapshot
        {
            Request = Request("gitCommits"), AsOf = Start.AddDays(10),
            Commits = Enumerable.Repeat(template, ReportRenderingLimits.MaximumRecords + 1).ToImmutableArray()
        };
        var before = GC.GetAllocatedBytesForCurrentThread();
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(tooMany), "Reject entity count before building dictionaries/documents.");
        True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "Oversized entity arrays are rejected without copying them.");
        var matrix = tooMany with
        {
            Commits = [],
            Request = tooMany.Request with
            {
                Roster = Enumerable.Repeat(Zero, ReportRenderingLimits.MaximumSummaryRows / 4 + 1).ToImmutableArray()
            }
        };
        before = GC.GetAllocatedBytesForCurrentThread();
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(matrix), "Reject roster/source fan-out before allocating summary rows.");
        True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "Oversized roster matrix is rejected before construction.");

        var unicodeMessage = new string('\u4e00', 128 * 1024);
        var unicode = new ReportSnapshot
        {
            Request = Request("gitCommits"), AsOf = Start.AddDays(10),
            Commits = Enumerable.Range(1, 100).Select(index => template with
            {
                Id = $"unicode-{index}", Message = unicodeMessage
            }).ToImmutableArray()
        };
        before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Throws<ReportSizeLimitException>(() => MarkdownReportWriter.Write(unicode),
            "UTF-8 byte expansion must fail even when character count is below the old limit.");
        Contains(exception.Message, "32 MiB UTF-8", "Buffered string helpers retain their smaller quota; streamed exports have a separate larger budget.");
        True(GC.GetAllocatedBytesForCurrentThread() - before < 8L * 1024 * 1024,
            "Oversized Markdown is counted without allocating a giant output buffer or string.");

        var hostileLabel = new string('<', 5000);
        var summary = new ReportSnapshot
        {
            Request = Request("workItems") with
            {
                Sources = Enumerable.Range(1, 25).Select(index => SourceA with { Key = $"scope-{index}", Scope = hostileLabel }).ToImmutableArray(),
                Roster = Enumerable.Range(1, 50).Select(index => new ReportIdentity(
                    hostileLabel, $"member-{index}@example.test", $"member-{index}")).ToImmutableArray()
            },
            AsOf = Start.AddDays(10)
        };
        // Reference tables collapse empty combinations, so real areas must drive the escaping expansion.
        summary = summary with
        {
            WorkItems = summary.Request.Roster.SelectMany((member, memberIndex) =>
                summary.Request.Sources.Select((source, sourceIndex) =>
                    WorkItem(memberIndex * summary.Request.Sources.Length + sourceIndex + 1, "org-a", source.Key, member, 1)
                        with { AreaPath = hostileLabel + sourceIndex, History = [] })).ToImmutableArray()
        };
        before = GC.GetAllocatedBytesForCurrentThread();
        Throws<ReportSizeLimitException>(() => HtmlSummaryReportWriter.Write(summary, "summary.md"),
            "HTML escaping and repeated roster/source labels must be preflighted.");
        True(GC.GetAllocatedBytesForCurrentThread() - before < 32L * 1024 * 1024,
            "Oversized HTML fails before allocating its output buffer.");
        var record = template with { Message = new string('x', ReportRenderingLimits.MaximumRecordCharacters + 1) };
        before = GC.GetAllocatedBytesForCurrentThread();
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(unicode with { Commits = [record] }),
            "A giant scalar is rejected before identity/label/fence construction.");
        True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "Giant scalar rejection must not allocate another giant string.");

        var supplementary = template with { Message = string.Concat(Enumerable.Repeat("\U0001F642", 1000)) };
        var supplementarySnapshot = unicode with { Commits = [supplementary] };
        Contains(MarkdownReportWriter.Write(supplementarySnapshot), supplementary.Message,
            "Supplementary Unicode survives UTF-8 preflight without changed content.");
    }

    private static void EffectiveQueryBoundsMetadata()
    {
        var cases = new[]
        {
            (Analysis: "workItems", Start: "2026-07-01", End: "2026-07-31",
                Origin: "Done + StateChangeDate; date-only WIQL; captured defaults."),
            (Analysis: "gitCommits", Start: "2026-07-01T10:11:12.123Z", End: "2026-07-31T23:59:59.999Z",
                Origin: "Git millisecond literal-Z wall-clock bounds; UTC relabeling, not local-offset conversion."),
            (Analysis: "incidentManagement", Start: "1970-01-01T00:00:00.0000000Z", End: "2026-07-31T23:59:59.0000000Z",
                Origin: "ICM missing start uses UnixEpoch; end uses export capture anchor; UTC wall-clock relabeling.")
        };
        foreach (var item in cases)
        {
            var snapshot = new ReportSnapshot
            {
                Request = Request(item.Analysis) with
                {
                    Start = item.Analysis == "incidentManagement" ? DateTimeOffset.UnixEpoch : Start,
                    RequestedStart = null,
                    EffectiveStartLiteral = item.Start,
                    EffectiveEndLiteral = item.End,
                    EffectiveBoundsOrigin = item.Origin
                },
                AsOf = Start.AddDays(10)
            };
            var document = ReportDocumentBuilder.Build(snapshot);
            var markdown = MarkdownReportWriter.Write(document);
            var html = HtmlSummaryReportWriter.Write(document, "bounds.md");
            Contains(markdown, "Effective query start literal:** " + item.Start, "Exact start query literal is included without date conversion.");
            Contains(markdown, "Effective query end literal:** " + item.End, "Exact end query literal preserves date-only or fractional precision.");
            Contains(markdown, "Effective bounds origin:** " + item.Origin, "Captured query origin is included verbatim with Markdown escaping.");
            Contains(html, item.Start, "HTML includes the source's exact start literal.");
            Contains(html, item.End, "HTML includes the source's exact end literal.");
            Contains(html, item.Origin, "HTML includes the source-specific default/interpretation origin.");
            NotContains(html, "export default anchored at capture", "Missing dates must not mislabel ICM's UnixEpoch default.");
            AssertSummaryShape(html, item.Analysis);
            Equal(markdown, MarkdownReportWriter.Write(snapshot), "Build-once and snapshot Markdown APIs agree.");
            Equal(html, HtmlSummaryReportWriter.Write(snapshot, "bounds.md"), "Build-once and snapshot HTML APIs agree.");
        }

        var blank = new ReportSnapshot
        {
            Request = Request("workItems") with
            {
                EffectiveStartLiteral = "",
                EffectiveEndLiteral = " \r\n\t",
                EffectiveBoundsOrigin = null
            },
            AsOf = Start.AddDays(10)
        };
        foreach (var output in new[] { MarkdownReportWriter.Write(blank), HtmlSummaryReportWriter.Write(blank, "blank.md") })
            foreach (var label in new[] { "Effective query start literal", "Effective query end literal", "Effective bounds origin" })
                NotContains(output, label, "Blank optional metadata is omitted rather than displayed as a misleading value.");
        var partial = blank with { Request = blank.Request with { EffectiveEndLiteral = "2026-07-31" } };
        var partialMarkdown = MarkdownReportWriter.Write(partial);
        Contains(partialMarkdown, "Effective query end literal", "Each optional metadata property is independent.");
        NotContains(partialMarkdown, "Effective query start literal", "An absent start literal is not invented from the end literal.");

        var unsafeText = blank with
        {
            Request = blank.Request with { EffectiveBoundsOrigin = "<script>bad</script>\nStateChangeDate | `literal`" }
        };
        var safeHtml = HtmlSummaryReportWriter.Write(unsafeText, "encoded.md");
        Contains(safeHtml, "&lt;script&gt;bad&lt;/script&gt;", "New metadata uses HTML context encoding.");
        NotContains(safeHtml, "<script>", "Captured interpretation notes cannot introduce scripts.");
        var safeMarkdown = MarkdownReportWriter.Write(unsafeText);
        Contains(safeMarkdown, "\\| \\`literal\\`", "New metadata uses dedicated Markdown escaping.");
        var oversized = blank with
        {
            Request = blank.Request with { EffectiveBoundsOrigin = new string('x', ReportRenderingLimits.MaximumRecordCharacters + 1) }
        };
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(oversized), "New metadata participates in construction-size validation.");
    }

    private static void CultureIndependence()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var snapshot = WorkSnapshot();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var markdown = MarkdownReportWriter.Write(snapshot);
            var html = HtmlSummaryReportWriter.Write(snapshot, "work.md");
            foreach (var culture in new[] { "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Equal(markdown, MarkdownReportWriter.Write(snapshot), "Markdown output is independent of server culture.");
                Equal(html, HtmlSummaryReportWriter.Write(snapshot, "work.md"), "HTML output is independent of server culture.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static ReportRequest Request(string analysis) => new()
    {
        RunId = Guid.Parse("0b000000-0000-0000-0000-000000000001"),
        Analysis = analysis, View = "teams", ExecutedAt = Start, Start = Start, End = Start.AddMonths(1).AddTicks(-1),
        RequestedStart = Start.DateTime, RequestedEnd = Start.AddMonths(1).AddTicks(-1).DateTime,
        TimeZone = "UTC", Sources = [SourceA, SourceB, SourceC], Roster = [Alice, Bob, Zero], Manager = "Fixture Manager"
    };

    private static WorkItemReportRecord WorkItem(int id, string org, string source, ReportIdentity actor, int activeDays) => new()
    {
        SourceKey = source, Organization = org, Id = id, Title = $"Fixture item {id}", Type = "Task", State = "Done",
        Assignee = actor, Priority = 1, CreatedAt = Start, ChangedAt = Start.AddDays(activeDays),
        ClosedAt = Start.AddDays(activeDays), AreaPath = "Area\\Parent", IterationPath = "Iteration\\One",
        Url = $"https://example.test/{org}/items/{id}",
        History = [new("Active", Start, actor), new("Done", Start.AddDays(activeDays), actor)]
    };

    internal static ReportSnapshot WorkSnapshot()
    {
        var first = WorkItem(1, "org-a", "source-a", Alice, 5) with
        {
            Type = "Bug",
            History =
            [
                new("New", Start, Alice), new("Active", Start.AddDays(1), Alice),
                new("Active", Start.AddDays(2), Bob), new("In Progress", Start.AddDays(3), Bob),
                new("Committed", Start.AddDays(4), Alice), new("Done", Start.AddDays(5), Alice)
            ]
        };
        return new ReportSnapshot
        {
            Request = Request("workItems"), AsOf = Start.AddDays(10),
            CoverageNotes = ["All deterministic source pages and histories were supplied."],
            WorkItems =
            [
                first, first with { SourceKey = "source-b" },
                WorkItem(2, "org-a", "source-a", new("alice"), 2),
                WorkItem(1, "org-b", "source-c", Bob, 8) with { Type = "Product Backlog Item" },
                WorkItem(3, "org-a", "source-a", Unknown, 0) with { Type = "Feature", History = [], Priority = null }
            ]
        };
    }

    internal static ReportSnapshot GitSnapshot()
    {
        var files = Enumerable.Range(1, 13).Select(index => new GitFileReportRecord(
            index == 13 ? "tests\\file13-λ.cs" : $"src\\file{index}.cs", "edit", index, 1)).ToImmutableArray();
        var commit = new GitReportRecord
        {
            SourceKey = "source-a", Organization = "org-a", RepositoryId = "repo-one", Id = "same-sha",
            Author = Alice, Committer = Alice, AuthorDate = Start.AddDays(-1), CommitterDate = Start,
            Message = "Full message | Ω\n```fenced\n[not a link](javascript:alert(1))\nlast line",
            Url = "https://example.test/repo-one/commit/same-sha", ParentCount = 2, Files = files
        };
        var pr = new PullRequestReportRecord("source-a", "org-a", "repo-one", 7, "Completed fixture PR",
            "refs/heads/main", new("alice"), Start.AddHours(1), "https://example.test/pr/7");
        return new ReportSnapshot
        {
            Request = Request("gitCommits") with
            {
                Start = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
                Sources =
                [
                    SourceA with { Scope = "repo-one", Branch = "main" },
                    SourceB with { Scope = "repo-one", Branch = "release" },
                    SourceC with { Organization = "org-a", Scope = "repo-two", Branch = "main" }
                ]
            },
            AsOf = Start.AddDays(10),
            Commits =
            [
                commit, commit with { SourceKey = "source-b" },
                commit with { RepositoryId = "repo-two", SourceKey = "source-c", Committer = Bob, CommitterDate = Start.AddDays(1) },
                commit with { Id = "older-zero", Author = Bob, Committer = Bob, CommitterDate = new DateTimeOffset(2025, 12, 20, 0, 0, 0, TimeSpan.Zero), Files = [new("tests/zero.cs", "add", 0, 0)] }
            ],
            PullRequests = [pr, pr with { SourceKey = "source-b" }, pr with { Id = 8, CompletedBy = Unknown }]
        };
    }

    internal static ReportSnapshot IncidentSnapshot()
    {
        var first = new IncidentReportRecord
        {
            SourceKey = "source-a", Id = "1", SiloId = "silo-a", Title = "Active fixture", Severity = 2,
            Status = "Active", CreatedAt = Start, ModifiedAt = Start.AddHours(1), ImpactStart = Start.AddHours(-1),
            Owner = Alice, Active = true, Created = true, OwningTenant = "Tenant", OwningTeam = "Team A",
            Mitigation = "Exact multiline mitigation\nincluding | delimiters and ``` fences",
            Url = "https://example.test/incidents/1",
            Metadata = ImmutableDictionary<string, string>.Empty.Add("IsOutage", "True").Add("OriginalCreator", "NotTheOwner")
        };
        var second = first with
        {
            Id = "2", Title = "Resolved fixture", Status = "Resolved", Active = false, Created = false, Resolved = true, Mitigated = false,
            ImpactStart = Start.AddHours(-2), MitigatedAt = Start.AddHours(2), ResolvedAt = Start.AddHours(10), ResolvedBy = Bob, MitigatedBy = Alice
        };
        var third = second with
        {
            Id = "3", SourceKey = "source-c", SiloId = "silo-b", Owner = Bob, OwningTeam = "Team B", ResolvedBy = Alice,
            MitigatedBy = Bob, ImpactStart = Start.AddHours(-30), MitigatedAt = Start.AddHours(6), ResolvedAt = Start.AddHours(20)
        };
        var fourth = first with
        {
            SourceKey = "source-c", SiloId = "silo-b", Owner = Bob, OwningTeam = "Team B", Created = false,
            ImpactStart = Start.AddHours(1), Title = "Second silo same incident ID"
        };
        var actionOnly = first with
        {
            Id = "5", Severity = null, Active = false, Created = false, Title = "action-only",
            Owner = Unknown, ImpactStart = null, Metadata = ImmutableDictionary<string, string>.Empty
        };
        var mitigated = second with
        {
            Id = "6", Severity = 3, Title = "Currently mitigated fixture", Status = "Mitigated",
            Resolved = false, Mitigated = true, ResolvedAt = null, ResolvedBy = Unknown
        };
        var a1 = new IncidentActionReportRecord("source-a", "1", "silo-a", "action-1", new("alice"), Start, "Edit");
        return new ReportSnapshot
        {
            Request = Request("incidentManagement"), AsOf = Start.AddDays(10),
            Incidents = [first, first with { SourceKey = "source-b" }, second, third, fourth, actionOnly, mitigated],
            IncidentActions =
            [
                a1, a1 with { SourceKey = "source-b" },
                a1 with { ActionId = "action-2", Actor = new("alice@example.test"), Timestamp = Start.AddMinutes(1) },
                a1 with { ActionId = "action-3", Actor = Bob, Timestamp = Start.AddMinutes(2) },
                a1 with { ActionId = "action-4", IncidentId = "5", Actor = Alice },
                a1 with { ActionId = "action-5", IncidentId = "5", Actor = Unknown },
                a1 with { ActionId = "action-6", IncidentId = "5", Actor = new("alice") }
            ]
        };
    }

    private static void AssertSummaryShape(string html, string analysis)
    {
        Equal(2, Regex.Matches(html, "<table>").Count, "Exactly source and overall tables.");
        var headers = Regex.Matches(html, "<thead><tr>(.*?)</tr></thead>", RegexOptions.Singleline);
        var expected = analysis switch
        {
            "workItems" => new[]
            {
                new[] { "\U0001F464 Team Member", "\U0001F4C2 Area Path", "\U0001F4CB Total Items", "\U0001F41E Bugs",
                    "\U0001F527 Tasks", "\U0001F4E6 Product Backlog Items", "\U0001F9E9 Other" },
                new[] { "\U0001F464 Team Member", "\U0001F4CB Total Items", "\U0001F41E Bugs", "\U0001F527 Tasks",
                    "\U0001F4E6 Product Backlog Items", "\U0001F9E9 Other", "\U0001F465 Teams" }
            },
            "gitCommits" => new[]
            {
                new[] { "\U0001F464 Team Member", "\U0001F4C2 Repository", "\U0001F4BE Commits", "\U0001F500 PRs Merged", "\u2795 Lines", "\u2796 Lines",
                    "\U0001F4C4 Files Added", "\U0001F4DD Files Modified", "\U0001F5D1 Files Deleted" },
                new[] { "\U0001F464 Team Member", "\U0001F4BE Commits", "\U0001F500 PRs Merged", "\u2795 Lines", "\u2796 Lines",
                    "\U0001F4C4 Files Added", "\U0001F4DD Files Modified", "\U0001F5D1 Files Deleted", "\U0001F4C2 Repositories" }
            },
            _ => new[]
            {
                new[] { "\U0001F464 Team Member", "\U0001F4C2 ICM Path", "\U0001F7E2 Active", "\U0001F195 Created",
                    "\U0001F527 Mitigated", "\u2705 Resolved", "\U0001F4AC Contributed" },
                new[] { "\U0001F464 Team Member", "\U0001F7E2 Active", "\U0001F195 Created",
                    "\U0001F527 Mitigated", "\u2705 Resolved", "\U0001F4AC Contributed" }
            }
        };
        for (var i = 0; i < 2; i++)
        {
            var actual = Regex.Matches(headers[i].Groups[1].Value, "<th scope=\"col\">(.*?)</th>").Select(match => match.Groups[1].Value);
            True(expected[i].SequenceEqual(actual), "Prescribed column order for " + analysis);
        }
        Contains(html, "font-family:'Segoe UI'", "Reference font.");
        Contains(html, "background:#1e1e1e", "Reference dark background.");
        Contains(html, "tbody tr:nth-child(odd)", "Alternating rows.");
        Contains(html, "overflow-wrap:anywhere", "Long fields wrap.");
        NotContains(html, "<script", "Standalone HTML must not load scripts.");
        NotContains(html, "<link", "Standalone HTML must not depend on external CSS.");
        NotContains(html, "<img", "HTML must not render screenshots or external images.");
    }

    private static ReportSummaryRow Member(ReportDocument document, string email) =>
        document.OverallRows.Single(row => !row.Contributor.StartsWith("Author:", StringComparison.Ordinal)
            && row.Contributor.Contains(email, StringComparison.Ordinal));

    private static void Contains(string text, string fragment, string message) => True(text.Contains(fragment, StringComparison.Ordinal), message + " Missing: " + fragment);
    private static void NotContains(string text, string fragment, string message) => True(!text.Contains(fragment, StringComparison.Ordinal), message);
    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Report writer fixture: " + message);
    }
    private static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Expected {expected}; actual {actual}.");
    private static void Near(double expected, double actual, string message) =>
        True(Math.Abs(expected - actual) < 0.000001, $"{message} Expected {expected}; actual {actual}.");
    private static T Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Report writer fixture: {message} Expected {typeof(T).Name}.");
    }
}
