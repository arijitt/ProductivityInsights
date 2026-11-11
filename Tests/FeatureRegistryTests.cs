using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.JSInterop;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.ReportTests.AdoReports;
using ProductivityInsights.ReportTests.ReportWriters;
using ProductivityInsights.Services.FeatureRegistry;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.CompatibilityTests;
using static ProductivityInsights.ReportTests.ExportIntegrationTests;

namespace ProductivityInsights.ReportTests;

public static class FeatureRegistryTests
{
    internal const string RepositoryId = "10000000-0000-0000-0000-000000000001";
    internal static readonly ReportIdentity Alice = new("Alice Author", "alice@example.test", "alice");
    internal static readonly ReportIdentity Bob = new("Bob Closer", "bob@example.test", "bob");
    internal static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset End = Start.AddDays(30);

    internal static ReportRequest Request(string view = "details") => new()
    {
        RunId = Guid.NewGuid(), Analysis = FeatureRegistrySource.Analysis, View = view, ExecutedAt = End,
        Start = Start, End = End, TimeZone = "UTC wall-clock",
        Sources = [new("feature", "msdata", "A365", FeatureRegistrySource.Repository)],
        Roster = view == "teams" ? [Alice, Bob, new("No activity", "zero@example.test", "zero")] : [],
        RosterParentIndices = view == "teams" ? [null, 0, 0] : [], Manager = view == "teams" ? Alice.Name : null
    };

    internal static Dictionary<string, object?> Pr(int id, string status, ReportIdentity? author = null,
        ReportIdentity? closer = null, DateTimeOffset? closed = null, bool draft = false)
    {
        static object Identity(ReportIdentity value) => new { id = value.Id, displayName = value.Name, uniqueName = value.Email };
        return new()
        {
            ["pullRequestId"] = id, ["repository"] = new { id = RepositoryId },
            ["title"] = $"PR {id} <script>unsafe()</script> | [text]",
            ["status"] = status, ["isDraft"] = draft,
            ["sourceRefName"] = "refs/heads/topic", ["targetRefName"] = id % 2 == 0 ? "refs/heads/main" : "refs/heads/other",
            ["createdBy"] = Identity(author ?? Alice), ["creationDate"] = Start.AddYears(-1),
            ["closedBy"] = status == "completed" ? Identity(closer ?? Bob) : null,
            ["closedDate"] = status == "completed" ? closed ?? Start : null,
            ["url"] = "https://untrusted.example/steal"
        };
    }

    internal static AdoReportTestHttp Http(Func<HttpRequestMessage, HttpResponseMessage?>? custom = null) => new((message, _) =>
    {
        Require(message.Method == HttpMethod.Get, "Feature Registry must make GET requests only.");
        var uri = message.RequestUri!;
        Require(uri.Host == "dev.azure.com" && uri.AbsolutePath.StartsWith("/msdata/A365/_apis/git/repositories/", StringComparison.Ordinal),
            "Unexpected source.");
        var response = custom?.Invoke(message);
        if (response is not null) return Task.FromResult(response);
        if (uri.AbsolutePath.EndsWith("/FeatureRegistry", StringComparison.Ordinal))
            return Task.FromResult(AdoReportTestHttp.Json(new { id = RepositoryId, name = "FeatureRegistry", project = new { name = "A365" } }));
        if (uri.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal))
        {
            var status = AdoReportTestHttp.Query(message, "searchCriteria.status");
            Require(AdoReportTestHttp.Query(message, "searchCriteria.targetRefName") == "", "All branches must be included.");
            if (status == "active")
                Require(AdoReportTestHttp.Query(message, "searchCriteria.minTime") == ""
                    && AdoReportTestHttp.Query(message, "searchCriteria.maxTime") == "", "Open PRs must have no date filters.");
            else Require(status == "completed" && AdoReportTestHttp.Query(message, "searchCriteria.queryTimeRangeType") == "closed",
                "Completed selection must use closed time.");
            var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
            var values = status == "active" ? new[] { Pr(1, "active", draft: true), Pr(2, "active") }
                : new[] { Pr(3, "completed", closed: Start), Pr(4, "completed", closed: End),
                    Pr(5, "completed", closed: Start.AddTicks(-1)), Pr(6, "completed", closed: End.AddTicks(1)), Pr(7, "abandoned") };
            // Short non-final pages must not terminate pagination.
            var page = values.Skip(skip).Take(1).ToArray();
            return Task.FromResult(AdoReportTestHttp.Json(new { count = page.Length, value = page }));
        }
        if (uri.AbsolutePath.EndsWith("/pullrequests/7", StringComparison.Ordinal))
            return Task.FromResult(AdoReportTestHttp.Json(Pr(7, "abandoned")));
        if (uri.AbsolutePath.EndsWith("/iterations", StringComparison.Ordinal))
        {
            Require(AdoReportTestHttp.Query(message, "includeCommits") == "false", "Iteration lookup must not enumerate commits.");
            return Task.FromResult(AdoReportTestHttp.Json(new { count = 3, value = new[] { new { id = 3 }, new { id = 1 }, new { id = 2 } } }));
        }
        if (uri.AbsolutePath.EndsWith("/iterations/3/changes", StringComparison.Ordinal))
        {
            Require(AdoReportTestHttp.Query(message, "$compareTo") == "0", "Files must cover the complete PR, not just the latest push.");
            var parts = uri.AbsolutePath.Split('/');
            var prId = int.Parse(parts[Array.IndexOf(parts, "pullrequests") + 1]);
            var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
            var changes = FileEntries(prId);
            var page = changes.Skip(skip).Take(3).ToArray();
            var next = skip + page.Length;
            return Task.FromResult(AdoReportTestHttp.Json(new
            {
                changeEntries = page, nextSkip = next < changes.Length ? next : 0, nextTop = next < changes.Length ? 3 : 0
            }));
        }
        throw new InvalidOperationException("Unexpected API, including prohibited commit or file-content calls: " + uri.AbsolutePath);
    });

    internal static Dictionary<string, object?>[] FileEntries(int prId) =>
    [
        .. Enumerable.Range(1, 12).Select(index => FileEntry(index, $"/pr-{prId}/file_{index:00}.cs", index % 2 == 0 ? "edit" : "add")),
        FileEntry(13, $"/pr-{prId}/new <file> & [x].cs", "rename, edit", $"/pr-{prId}/old <file> & [x].cs"),
        new() { ["changeId"] = 14, ["changeType"] = "delete", ["originalPath"] = "/shared.cs",
            ["item"] = new { originalObjectId = new string('a', 40), path = (string?)null } },
        new() { ["changeId"] = 15, ["changeType"] = "add", ["item"] = new { path = "/directory", isFolder = true } }
    ];

    internal static Dictionary<string, object?> FileEntry(int id, string path, string type, string? originalPath = null) =>
        new() { ["changeId"] = id, ["changeType"] = type, ["originalPath"] = originalPath, ["item"] = new { path } };

    internal static FeatureRegistryPullRequestCollector Collector(HttpClient client, AdoReportCollectionLimits? limits = null) =>
        new(client, _ => Task.FromResult("fixture-token"), limits);

    public static async Task RunAsync()
    {
        await SelectionAndExport();
        await RosterAttribution();
        await DetailFallbackAndRaces();
        await Failures();
        await IsolatedState();
        ConfirmationMarkup();
        await ContributorRows();
        await DetailedReportFormat();
        await SummaryReportFormat();
        await ResultScrolling();
        TeamsManagerWarnings();
    }

    private static void TeamsManagerWarnings()
    {
        var component = new AnalysisOptions();
        foreach (var (option, name) in new[]
        {
            ("workItems", "Work Items"), ("gitCommits", "Git Commits"),
            ("incidentManagement", "Incident Resolutions"), ("featureRegistry", "Feature Registry")
        })
        {
            Set(component, "selectedOption", option);
            Set(component, "selectedView", "teams");
            Call(component, "ClearManagerSelection");
            var expected = $"Select a manager and finish Fetch Team before running {name}.";
            Require((string?)Get(component, "TeamsManagerWarning") == expected, "Missing-manager banner must name the selected Teams analysis.");
            Set(component, "selectedManager", new ProductivityInsights.Models.Teams.GraphUser { displayName = "Fixture manager" });
            Require((string?)Get(component, "TeamsManagerWarning") == expected, "Selecting a manager without fetching the roster must retain the warning.");
            Set(component, "orgTreeRoot", new ProductivityInsights.Models.Teams.OrgMember { DisplayName = "Fixture manager" });
            Set(component, "isLoadingOrgTree", true);
            Require((string?)Get(component, "TeamsManagerWarning") == expected, "An unfinished fetch must retain the warning, even if a prior roster exists.");
            Set(component, "isLoadingOrgTree", false);
            Require(Get(component, "TeamsManagerWarning") is null, "A completed roster must clear the warning.");
            Call(component, "ClearManagerSelection");
            Require((string?)Get(component, "TeamsManagerWarning") == expected, "Clearing the manager must restore the warning.");
            foreach (var view in new[] { "details", "trends" })
            {
                Set(component, "selectedView", view);
                Require(Get(component, "TeamsManagerWarning") is null, "Non-Teams views must not display manager prerequisites.");
            }
        }
        Set(component, "selectedView", "teams");
        Require((string?)Get(component, "FeatureRegistryValidationError") == (string?)Get(component, "TeamsManagerWarning"),
            "Feature Registry validation must use the shared manager prerequisite.");
        Set(component, "selectedView", "details");
        Set(component, "featureRegistryStart", End.AddDays(1).UtcDateTime);
        Set(component, "featureRegistryEnd", End.UtcDateTime);
        Require(((string?)Get(component, "FeatureRegistryValidationError"))?.Contains("start time must not be later") == true,
            "Sharing manager warnings must preserve Feature Registry date validation.");
    }

    private static async Task ResultScrolling()
    {
        var component = PreparedComponent();
        var runtime = new ScrollRuntime();
        var request = Request("teams");
        Set(component, "JSRuntime", runtime);
        Set(component, "selectedOption", FeatureRegistrySource.Analysis);
        Set(component, "featureRegistryRequest", request);
        Set(component, "featureRegistryScrollRunId", request.RunId);
        await Scroll();
        await Scroll();
        Require(runtime.Calls == 1, "A confirmed PR run must scroll once, not on every render.");
        Set(component, "featureRegistryScrollRunId", Guid.NewGuid());
        await Scroll();
        Set(component, "featureRegistryScrollRunId", request.RunId);
        Set(component, "selectedOption", "gitCommits");
        await Scroll();
        Set(component, "selectedOption", FeatureRegistrySource.Analysis);
        Set(component, "selectedView", "details");
        Set(component, "featureRegistryScrollRunId", request.RunId);
        await Scroll();
        Set(component, "selectedView", "teams");
        Set(component, "reportDisposed", true);
        Set(component, "featureRegistryScrollRunId", request.RunId);
        await Scroll();
        Require(runtime.Calls == 1, "Stale runs, tab/view changes and disposal must suppress PR scrolling.");
        Set(component, "reportDisposed", false);
        foreach (var error in new Exception[]
        {
            new JSException("Fixture scroll failure"), new JSDisconnectedException("Fixture disconnect"),
            new OperationCanceledException("Fixture interop timeout")
        })
        {
            runtime.Error = error;
            Set(component, "featureRegistryScrollRunId", request.RunId);
            await Scroll();
            Require(Get(component, "featureRegistryScrollRunId") is null && Get(component, "featureRegistryError") is null,
                "A logged scrolling failure must not fail PR collection or retry on every render.");
        }
        runtime.Error = null;
        Set(component, "featureRegistryScrollRunId", request.RunId);
        ((CancellationTokenSource)Get(component, "reportLifetime")!).Cancel();
        await Scroll();

        Task Scroll() => (Task)Call(component, "ScrollFeatureRegistryResultsAsync")!;
    }

    private sealed class ScrollRuntime : IJSRuntime
    {
        internal int Calls { get; private set; }
        internal Exception? Error { get; set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Require(identifier == "scrollToBottom" && args is [true], "Feature Registry must reuse smooth ordinary-analysis scrolling.");
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Error is null ? ValueTask.FromResult(default(TValue)!) : ValueTask.FromException<TValue>(Error);
        }
    }

    private static async Task DetailedReportFormat()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            var data = await Collector(client).CollectAsync(Request(view), default);
            var report = Wrap(data);
            var summary = HtmlSummaryReportWriter.Write(report, "Feature_Registry_Detailed.html");
            var html = HtmlDetailedReportWriter.Write(report);
            using var streamed = new StringWriter();
            HtmlDetailedReportWriter.WriteTo(ReportDocumentBuilder.Build(report), streamed);
            Require(html == streamed.ToString(), "Streamed and buffered PR details must have identical content.");
            var main = Main(html);
            Require(main.Element("h1")!.Value == $"{(view == "teams" ? "Team " : "")}Feature Registry Pull Request Analysis (2026-07-01 to 2026-07-31)",
                "Detailed PR title must follow the dated Git reference heading.");
            var sections = main.Elements("h2").Select(element => element.Value).ToArray();
            Require(sections.Count(title => title.Contains("SUMMARY")) == 1
                && !sections.Any(title => title.Contains("FEATURE REGISTRY PULL REQUEST SUMMARY ("))
                && Array.FindIndex(sections, title => title.Contains("DETAILS BY PERSON"))
                    < Array.FindIndex(sections, title => title.Contains("SUMMARY BY REPOSITORY")),
                "PR details and repository summary must retain their order without a redundant overall summary.");
            if (view != "teams")
                Require(!html.Contains("Captured inputs and export query context")
                    && !html.Contains("Collection started") && !html.Contains("Collection finished")
                    && !html.Contains("Run Analysis snapshot") && !html.Contains(data.Request.RunId.ToString()),
                    "Details and Trends must continue to omit captured-input context.");
            var markdown = MarkdownReportWriter.Write(report);
            Require(!markdown.Contains("Captured inputs and export query context")
                && markdown.Contains("\U0001F464 Contributor: Alice Author - 2 open PRs, 0 completed PRs")
                && new[] { 1, 2, 4, 3 }.All(id => markdown.Contains($"\U0001F504 Pull Request: \\#{id}")),
                "Teams HTML formatting must not change legacy Feature Registry Markdown context, contributor headers or numbered PR headings.");
            var personHeadings = main.Descendants("h3").Where(element => Class(element, "report-person-heading")).ToArray();
            Require(personHeadings.Length == (view == "teams" ? 3 : 2),
                "Teams must include every roster member; other views must retain their recorded contributors.");
            if (view != "teams")
                Require(personHeadings.Select(element => element.Value).SequenceEqual(new[]
                {
                    "\U0001F464 Contributor: Alice Author - 2 open PRs, 0 completed PRs",
                    "\U0001F464 Contributor: Bob Closer - 0 open PRs, 2 completed PRs"
                }), "Details and Trends must retain their existing contributor headers.");
            var records = main.Descendants("h4").Where(element => Class(element, "report-record-heading")).ToArray();
            if (view == "teams")
                CheckTeamPrRecords(main, data.Open.Concat(data.Completed));
            else
                Require(records.Select(element => element.Value).SequenceEqual(new[]
                    { "\U0001F504 Pull Request: #1", "\U0001F504 Pull Request: #2", "\U0001F504 Pull Request: #4", "\U0001F504 Pull Request: #3" }),
                    "Details and Trends must retain their original numbered PR headings and ordering.");
            var titles = view == "teams"
                ? main.Descendants("span").Where(element => Class(element, "report-pr-title-value")).Select(element => element.Value)
                : main.Descendants("code").Select(element => element.Value.TrimEnd('\n'));
            Require(titles.SequenceEqual(data.Open.Concat(data.Completed).Select(pr => pr.Title))
                && !main.Descendants("script").Any(), "Full PR titles must remain encoded, not parsed as markup.");
            if (view == "teams")
                Require(!html.Contains("(Full PR title)") && !main.Descendants("pre").Any()
                    && main.Descendants("div").Count(element => Class(element, "report-pr-title")
                        && element.Elements("span").Any(span => Class(span, "report-label") && span.Value == "Title:")
                        && element.Elements("span").Any(span => Class(span, "report-pr-title-value"))) == 4,
                    "Teams PR titles must appear beside their Title label, without a placeholder or separate code block.");
            var labels = main.Descendants("span").Where(element => Class(element, "report-label")).Select(element => element.Value).ToArray();
            foreach (var label in new[] { "Author:", "Status:", "Title:", "Source branch:", "Target branch:", "Created:", "Completed by:", "Completed:", "PR ID:" })
                Require(labels.Count(value => value == label) == 4, "Missing icon-led PR field label: " + label);
            var tables = main.Descendants("table").Where(table =>
                !table.Ancestors("details").Any(element => Class(element, "report-context-details"))).ToArray();
            Require(tables.Length == 1 && tables.All(table => table.Element("thead")!.Descendants("th").Count() == 4),
                "PR content outside captured context must retain only the compact repository summary table.");
            Require(tables.All(table => table.Element("tbody")!.Elements("tr").Last().Elements("td")
                .Select(cell => cell.Value).Count(value => value == "2") == 2), "Summary totals must preserve two open and two completed PRs.");
            var folds = ReportFoldMarkup.Parse(html);
            if (view == "teams")
            {
                Require(folds.Count(fold => fold.Kind == "person") == 3 && folds.Count(fold => fold.Kind == "item") == 4
                    && folds.Where(fold => fold.Kind == "item").All(fold => fold.ParentStart is not null),
                    "Every Teams member and each recorded PR must retain independent folding.");
                var roster = main.Descendants("ul").Single(element => Class(element, "report-roster-hierarchy"));
                var nodes = roster.Descendants("li").ToArray();
                Require(nodes.Length == 3 && nodes[1].Parent!.Parent == nodes[0] && nodes[2].Parent!.Parent == nodes[0],
                    "Feature Registry roster must preserve its captured manager/report hierarchy.");
                CheckTeamMembers(data, main);
                var flatData = SnapshotForTest(data.Request with { RosterParentIndices = [] }, data.Open, data.Completed, []);
                var flat = HtmlDetailedReportWriter.Write(Wrap(flatData));
                Require(!flat.Contains("<ul class=\"report-roster-hierarchy\"")
                    && flat.Contains("reporting hierarchy was not captured"), "Flat snapshots must not invent reporting relationships.");
                CheckTeamMembers(flatData, Main(flat));
                var outside = ReportFoldMarkup.OutsidePersons(html);
                Require(outside.Contains("SUMMARY BY REPOSITORY"),
                    "Repository summary must stay outside collapsed person sections.");
            }
            else Require(folds.Length == 0 && !html.Contains("report-pr-divider"),
                "Details and Trends must not gain Teams-only folding or PR list dividers.");
            Require(summary == HtmlSummaryReportWriter.Write(report, "Feature_Registry_Detailed.html")
                && summary.Contains("<th>Source branch</th>") && !summary.Contains("report-person-details")
                && !summary.Contains("report-pr-divider"),
                "Detailed formatting must not alter the separate summary report.");
        }

        var baseline = await Collector(client).CollectAsync(Request(), default);
        var unavailable = SnapshotForTest(Request("teams"), [], [],
            [baseline.Open[0] with { Author = new("Unknown") }, baseline.Completed[0] with { CompletedBy = null }]);
        var unavailableHtml = HtmlDetailedReportWriter.Write(Wrap(unavailable));
        Require(Main(unavailableHtml).Descendants("h4").Count(element => Class(element, "report-record-heading")) == 2
            && unavailableHtml.Contains("MEMBERSHIP UNAVAILABLE") && unavailableHtml.Contains("excluded from roster counts")
            && ReportFoldMarkup.Parse(unavailableHtml).Where(fold => fold.Kind == "person")
                .All(fold => !fold.Content.Contains("Pull Request:")),
            "Unavailable membership records must remain complete, separate, and never receive roster credit.");
        Require(unavailableHtml.Contains("Zero counts mean no attributed PRs"),
            "Zero recorded counts must not hide unresolved membership evidence.");
        CheckTeamPrRecords(Main(unavailableHtml), unavailable.Unattributed);
        CheckTeamMembers(unavailable, Main(unavailableHtml));
        CheckPrDivider(Main(unavailableHtml));
        var bothGroups = SnapshotForTest(Request("teams"), baseline.Open,
            baseline.Completed.Select(pr => pr with { CompletedBy = Alice }).ToImmutableArray(), []);
        CheckTeamMembers(bothGroups, Main(HtmlDetailedReportWriter.Write(Wrap(bothGroups))));
        var duplicateNames = Request("teams") with
        {
            Roster = [Alice, Bob with { Name = Alice.Name }, new(Alice.Name, "zero@example.test", "zero", "zero@example.test")]
        };
        var sameNames = SnapshotForTest(duplicateNames, baseline.Open,
            baseline.Completed.Select(pr => pr with { CompletedBy = Bob with { Name = Alice.Name } }).ToImmutableArray(), []);
        CheckTeamMembers(sameNames, Main(HtmlDetailedReportWriter.Write(Wrap(sameNames))));
        var emptyData = SnapshotForTest(Request("teams"), [], [], []);
        var emptyHtml = HtmlDetailedReportWriter.Write(Wrap(emptyData));
        Require(emptyHtml.Contains("No matching PRs in the collected snapshot")
            && ReportFoldMarkup.Parse(emptyHtml).All(fold => fold.Kind is "person" or "context"), "Empty PR snapshots must stay explicit without fabricated records.");
        CheckTeamPrRecords(Main(emptyHtml), []);
        CheckTeamMembers(emptyData, Main(emptyHtml));
        var duplicateTitles = SnapshotForTest(Request("teams"),
            baseline.Open.Select(pr => pr with { Title = "Shared <title> & [link](javascript:bad)" }).ToImmutableArray(),
            baseline.Completed.Select(pr => pr with { Title = "Shared <title> & [link](javascript:bad)" }).ToImmutableArray(), []);
        CheckTeamPrRecords(Main(HtmlDetailedReportWriter.Write(Wrap(duplicateTitles))),
            duplicateTitles.Open.Concat(duplicateTitles.Completed));
        var encodedRequest = Request("teams") with
        {
            Roster = Request("teams").Roster.SetItem(2, new("No <PR> & member", "zero@example.test", "zero", "zero@example.test"))
        };
        var encodedData = SnapshotForTest(encodedRequest, baseline.Open, baseline.Completed, []);
        var encodedMain = Main(HtmlDetailedReportWriter.Write(Wrap(encodedData)));
        CheckTeamMembers(encodedData, encodedMain);
        Require(!encodedMain.Descendants("PR").Any(), "Zero-PR names must remain encoded, not rendered as HTML.");
        var multiline = "Full title\n```text\n<h3>untrusted</h3>\n" + new string('x', 8192);
        var longTitle = SnapshotForTest(Request(), [baseline.Open[0] with { Title = multiline }], [], []);
        Require(Main(HtmlDetailedReportWriter.Write(Wrap(longTitle))).Descendants("code").Single().Value.TrimEnd('\n') == multiline,
            "Multiline PR text must survive bounded code-block rendering without truncation or structural injection.");
        var teamTitle = SnapshotForTest(Request("teams"), [baseline.Open[0] with { Title = multiline }], [], []);
        var titleHtml = HtmlDetailedReportWriter.Write(Wrap(teamTitle));
        CheckTeamPrRecords(Main(titleHtml), teamTitle.Open);
        Require(Main(titleHtml).Descendants("span").Single(element => Class(element, "report-pr-title-value")).Value == multiline
            && !titleHtml.Contains("(Full PR title)") && !Main(titleHtml).Descendants("code").Any(),
            "Inline Teams titles must preserve full multiline text and escaping without code-block presentation.");
        var largeTitle = new string('&', 900_000);
        var large = SnapshotForTest(Request("teams"), Enumerable.Range(1, 15)
            .Select(id => baseline.Open[0] with { Id = id, Title = largeTitle }).ToImmutableArray(), [], []);
        var largeDocument = ReportDocumentBuilder.Build(Wrap(large));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var bytes = HtmlDetailedReportWriter.WriteTo(largeDocument, TextWriter.Null);
        Require(bytes > 2L * largeTitle.Length * large.Open.Length * "&amp;".Length
            && bytes < ReportRenderingLimits.MaximumExportFileBytes
            && GC.GetAllocatedBytesForCurrentThread() - before < ReportRenderingLimits.MaximumInputCharacters,
            "Full PR titles in both headings and fields must stream from captured references without inflating the structural Markdown buffer.");

        static ReportSnapshot Wrap(FeatureRegistrySnapshot data) => new() { Request = data.Request, AsOf = data.FinishedAt, FeatureRegistry = data };
        static XElement Main(string html) => XElement.Parse(html[html.IndexOf("<main>", StringComparison.Ordinal)..(html.IndexOf("</main>", StringComparison.Ordinal) + 7)]);
        static bool Class(XElement element, string value) => ((string?)element.Attribute("class"))?.Split(' ').Contains(value) == true;
        static void CheckTeamPrRecords(XElement main, IEnumerable<FeatureRegistryPullRequest> expected)
        {
            var records = expected.ToArray();
            var items = main.Descendants("details").Where(element => Class(element, "report-item-details")).ToArray();
            Require(items.Length == records.Length, "Every recorded PR must retain its own disclosure, even when titles match.");
            foreach (var (item, pr) in items.Zip(records))
            {
                var heading = item.Element("summary")!.Element("h4")!;
                Require(Class(heading, "report-record-heading") && heading.Value == "\U0001F504 Pull Request: " + pr.Title
                    && !heading.Descendants("a").Any() && !heading.Descendants("script").Any()
                    && !heading.Descendants("h3").Any() && !heading.Descendants("title").Any(),
                    "Teams PR headings must show the complete captured title as encoded text instead of the PR number.");
                var idField = item.Descendants("span").Single(element => Class(element, "report-label") && element.Value == "PR ID:")
                    .NextNode as XText;
                Require(idField?.Value.Trim() == $"{pr.Id}",
                    $"Title-based headings must preserve PR ID {pr.Id}; actual field: {idField?.Value}");
                var links = item.Descendants("a").Select(link => (string?)link.Attribute("href")).ToArray();
                Require(links.Contains(pr.Url),
                    $"Title-based headings must preserve URL {pr.Url}; actual links: {string.Join(", ", links)}");
            }
        }
        static void CheckPrDivider(XElement container)
        {
            var completed = container.Elements("h4").Single(element => element.Value.StartsWith("Completed PRs ("));
            var preceding = completed.ElementsBeforeSelf().ToArray();
            var dividerBlock = preceding.Last();
            var divider = dividerBlock.Elements("hr").Single();
            Require(Class(divider, "report-rule") && Class(divider, "report-pr-divider")
                && (string?)divider.Attribute("style") == "border-top:3px solid currentColor;color:inherit;opacity:1"
                && preceding.Any(element => element.Name == "h4" && element.Value.StartsWith("Open PRs ("))
                && container.Elements("div").SelectMany(element => element.Elements("hr"))
                    .Count(element => Class(element, "report-pr-divider")) == 1
                && !divider.Ancestors("details").Any(element => Class(element, "report-item-details")),
                "A single bold divider must separate complete open and completed PR lists outside individual PR folds, including empty lists.");
        }
        static void CheckTeamMembers(FeatureRegistrySnapshot data, XElement main)
        {
            var headings = main.Descendants("h3").Where(element => Class(element, "report-person-heading")).ToArray();
            var links = main.Descendants("a").Where(element => Class(element, "report-member-link")
                && element.Ancestors().Any(parent => Class(parent, "report-roster-member"))).ToArray();
            var summary = main.Elements("h2").Single(element => element.Value.Contains("SUMMARY BY REPOSITORY"));
            var beforeSummary = main.Elements().TakeWhile(element => element != summary).SelectMany(element => element.DescendantsAndSelf()).ToHashSet();
            var rows = main.Descendants("tbody").Single(table =>
                !table.Ancestors("details").Any(element => Class(element, "report-context-details"))).Elements("tr").ToArray();
            Require(headings.Length == data.Request.Roster.Length && rows.Length == headings.Length + 1
                && links.Length == headings.Length && links.Select(link => link.Attribute("href")!.Value).Distinct().Count() == headings.Length,
                "Every captured member needs exactly one person section, summary row and identity-qualified roster destination.");
            for (var index = 0; index < data.Request.Roster.Length; index++)
            {
                var member = data.Request.Roster[index];
                var open = data.Open.Count(pr => ReportIdentityMatcher.Resolve(pr.Attribution, data.Request.Roster) == member);
                var completed = data.Completed.Count(pr => ReportIdentityMatcher.Resolve(pr.Attribution, data.Request.Roster) == member);
                var target = links[index].Attribute("href")!.Value[1..];
                var heading = headings.Single(element => (string?)element.Attribute("id") == target);
                CheckPrDivider(heading.Parent!.Parent!.Elements("div").Single(element => Class(element, "report-person-content")));
                Require(beforeSummary.Contains(heading)
                    && heading.Value == $"\U0001F464 {member.DisplayName} - {open} open PRs, {completed} completed PRs",
                    "Roster links must target the correct counted person heading without a Contributor: prefix, including zero-PR members.");
                var row = rows[Array.IndexOf(headings, heading)].Elements("td").Select(cell => cell.Value).ToArray();
                Require(row.SequenceEqual([member.DisplayName, "msdata/A365/FeatureRegistry", open.ToString(), completed.ToString()]),
                    "Summary must include every member's name and correct counts without email/principal/ID annotations.");
            }
            Require(!main.Value.Contains("No collected detail section is attributed to this roster member"),
                "Zero-PR members must no longer appear in a separate metadata-heavy fallback section.");
            var context = main.Elements("details").Single(element => Class(element, "report-context-details"));
            Require(main.Elements().Last() == context && context.Attribute("open") is null
                && context.Element("summary")!.Element("h2")!.Value == "Captured inputs and export query context"
                && !context.Descendants("details").Any()
                && context.Value.Contains(data.Request.RunId.ToString("D"))
                && context.Value.Contains(data.Request.Start.ToString("O")) && context.Value.Contains(data.Request.End.ToString("O"))
                && context.Value.Contains(data.StartedAt.ToString("O")) && context.Value.Contains(data.FinishedAt.ToString("O"))
                && context.Value.Contains("Selected sources") && context.Value.Contains("FeatureRegistry")
                && context.Value.Contains("Exact successful Run Analysis snapshot"),
                "Teams captured inputs, sources and collection timestamps must be the final independent, initially collapsed section.");
        }
    }

    private static async Task SummaryReportFormat()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        var data = await Collector(client).CollectAsync(Request("teams"), default);
        var before = handler.Requests.Count;
        var summary = Render(data);
        var body = Body(summary);
        var title = "Team Feature Registry Pull Request Analysis (2026-07-01 to 2026-07-31)";
        Require(body.Element("h1")!.Value == title && summary.Contains("<title>" + title + "</title>")
            && body.Elements("div").Single().Value == "Generated " + data.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            "Teams summary must use the reference's dated title and compact generated timestamp.");
        var headings = body.Elements("h2").Select(element => element.Value).ToArray();
        Require(headings.SequenceEqual([
            "TEAM FEATURE REGISTRY PULL REQUEST SUMMARY BY REPOSITORY (2026-07-01 to 2026-07-31)",
            "TEAM FEATURE REGISTRY PULL REQUEST SUMMARY (2026-07-01 to 2026-07-31)"]),
            "Compact by-repository and overall summaries must precede the expandable context.");
        var tables = body.Elements("table").ToArray();
        Require(tables.Length == 2 && Rows(tables[0]).Select(row => string.Join("|", row)).SequenceEqual([
            "Bob Closer|msdata/A365/FeatureRegistry|0|2", "Alice Author|msdata/A365/FeatureRegistry|2|0", "No activity|\u2014|0|0"])
            && Rows(tables[1]).Select(row => string.Join("|", row)).SequenceEqual([
            "Bob Closer|0|2|FeatureRegistry", "Alice Author|2|0|FeatureRegistry", "No activity|0|0|"]),
            "Reference summaries need completion-first ordering, correct author/closer credit and one zero-member row.");
        Require(tables[0].Descendants("th").Select(element => element.Value).SequenceEqual([
            "\U0001F464 Team Member", "\U0001F4C2 Repository", "\U0001F513 Open PRs", "\u2705 Completed PRs"])
            && tables[1].Descendants("th").Select(element => element.Value).SequenceEqual([
                "\U0001F464 Team Member", "\U0001F513 Open PRs", "\u2705 Completed PRs", "\U0001F4C2 Repositories"])
            && tables.SelectMany(table => table.Descendants("th")).All(element => (string?)element.Attribute("scope") == "col"),
            "Summary columns must retain reference icons/accessibility without inventing commit or line metrics.");
        var context = body.Element("details")!;
        Require((string?)context.Attribute("class") == "report-context" && context.Attribute("open") is null
            && context.Element("summary")!.Value == "Captured inputs, sources and PR evidence"
            && context.ElementsBeforeSelf().Count(element => element.Name == "table") == 2
            && context.Value.Contains("Run Analysis snapshot") && context.Value.Contains("Collection started")
            && context.Value.Contains("all target branches") && context.Value.Contains(data.Request.RunId.ToString())
            && context.Value.Contains("closedBy")
            && context.Descendants("table").Count() == 2
            && context.Descendants("a").Select(element => (string?)element.Attribute("href"))
                .Contains("./Feature_Registry_Detailed.html"),
            "Existing PR records, all-branch scope, attribution and snapshot context must remain available below compact summaries.");
        foreach (var pr in data.Open.Concat(data.Completed))
            Require(context.Descendants("a").Any(element => (string?)element.Attribute("href") == pr.Url && element.Value == pr.Title),
                "Preserve every full linked PR title in the collapsed evidence, safely encoded.");
        Require(!body.Descendants("script").Any() && !summary.Contains("Files Added") && !summary.Contains("Lines")
            && !summary.Contains("Changed Files"), "Summary formatting cannot collect or invent Git commit/file/line statistics.");
        var git = HtmlSummaryReportWriter.Write(ReportWriterFixtures.GitSnapshot(), "Git_Detailed.html");
        Require(Style(summary) == Style(git)
            && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Style(summary))))
                == "DB1A310EE98AF05C549238D9AEF20EFE6268F2E196360DAE4EB1083AABB32679",
            "Teams PR summary styles must exactly match the supplied August Git summary stylesheet, not an approximation.");
        using var streamed = new StringWriter();
        var report = new ReportSnapshot { Request = data.Request, AsOf = data.FinishedAt, FeatureRegistry = data };
        HtmlSummaryReportWriter.WriteTo(ReportDocumentBuilder.Build(report), "Feature_Registry_Detailed.html", streamed);
        Require(summary == streamed.ToString() && handler.Requests.Count == before,
            "Buffered/streamed summaries must remain identical and must not recollect PRs.");
        var empty = Body(Render(SnapshotForTest(data.Request, [], [], [])));
        Require(empty.Elements("table").All(table => Rows(table).Length == 3)
            && Rows(empty.Elements("table").First()).All(row => row[1] == "\u2014" && row[2] == "0" && row[3] == "0")
            && empty.Value.Contains("No matching PRs in the collected snapshot."), "Successful empty collections keep the entire zero-PR roster.");
        var unavailable = Body(Render(SnapshotForTest(data.Request, [], [], [data.Open[0], data.Completed[0]])));
        Require(unavailable.Elements("p").Single().Value.Contains("not evidence of zero activity")
            && unavailable.Element("details")!.Value.Contains("Membership unavailable")
            && Rows(unavailable.Elements("table").First()).All(row => row[2] == "0" && row[3] == "0"),
            "Unknown membership must be disclosed outside the collapsed context, never assigned or counted as roster activity.");
        var duplicateNames = data.Request with
        {
            Roster = [Alice, Bob with { Name = Alice.Name }, new("No <PR> & activity", "zero@example.test", "zero")]
        };
        var collision = Body(Render(SnapshotForTest(duplicateNames, data.Open, data.Completed, [])));
        var collisionRows = Rows(collision.Elements("table").First());
        Require(collisionRows.Length == 3 && collisionRows[0][0] == Alice.Name && collisionRows[1][0] == Alice.Name
            && collisionRows[0][3] == "2" && collisionRows[1][2] == "2"
            && collisionRows[2][0] == "No <PR> & activity" && !collision.Descendants("PR").Any(),
            "Name collisions must not merge roster identities or reinterpret recorded names as HTML.");
        foreach (var view in new[] { "details", "trends" })
        {
            var other = Render(SnapshotForTest(Request(view), data.Open, data.Completed, []));
            Require(other.Contains("<h1>Feature Registry Pull Requests (" + view + ")</h1>")
                && !other.Contains("report-context") && !other.Contains("SUMMARY BY REPOSITORY")
                && other.Contains("<th>Source branch</th>"), "Other views must retain their existing PR summary layout.");
        }

        static string Render(FeatureRegistrySnapshot snapshot) => HtmlSummaryReportWriter.Write(
            new ReportSnapshot { Request = snapshot.Request, AsOf = snapshot.FinishedAt, FeatureRegistry = snapshot },
            "Feature_Registry_Detailed.html");
        static XElement Body(string html) => XElement.Parse(html[html.IndexOf("<body>", StringComparison.Ordinal)..(html.IndexOf("</body>", StringComparison.Ordinal) + 7)]);
        static string Style(string html) => html[(html.IndexOf("<style>", StringComparison.Ordinal) + 7)..html.IndexOf("</style>", StringComparison.Ordinal)]
            .ReplaceLineEndings("\n").Trim();
        static string[][] Rows(XElement table) => table.Element("tbody")!.Elements("tr")
            .Select(row => row.Elements("td").Select(cell => cell.Value).ToArray()).ToArray();
    }

    private static async Task ContributorRows()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        var component = new AnalysisOptions();
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            var data = await Collector(client).CollectAsync(Request(view), default);
            var rows = Rows(data);
            Require(rows.Length == 2 && rows[0].Name == Alice.Name && rows[1].Name == Bob.Name,
                "PR results must provide contributor summaries in every view.");
            Require(rows[0].Open.SequenceEqual([1, 2]) && rows[0].Completed.Length == 0
                && rows[1].Open.Length == 0 && rows[1].Completed.SequenceEqual([4, 3]),
                "Contributor details must preserve PR order and author/completion-actor attribution.");
            var rowKeys = rows.Select(row => row.Key).ToArray();
            Call(component, "ToggleFeatureRegistryRow", rowKeys[0]);
            var expanded = (HashSet<string>)Get(component, "expandedFeatureRegistryRows")!;
            Require(expanded.SetEquals([rowKeys[0]]), "Expanding one PR contributor must not expand another.");
            Call(component, "ToggleFeatureRegistryRow", rowKeys[0]);
            Require(expanded.Count == 0 && ((HashSet<string>)Get(component, "expandedRows")!).Count == 0,
                "PR expansion must be independent of ordinary Git expansion.");
        }
        var snapshot = await Collector(client).CollectAsync(Request(), default);
        var sameNames = SnapshotForTest(snapshot.Request,
            [snapshot.Open[0], snapshot.Open[1] with { Author = Bob with { Name = Alice.Name } }], [], []);
        Require(Rows(sameNames).Length == 2, "Equal display names must never merge distinct identities.");
        var unknownNames = SnapshotForTest(snapshot.Request,
            snapshot.Open.Select(pr => pr with { Author = new ReportIdentity("Unknown") }).ToImmutableArray(), [], []);
        Require(Rows(unknownNames).Length == 2, "Missing identity evidence must not group unrelated PR authors.");
        var unavailable = SnapshotForTest(Request("teams"), [], [], [snapshot.Open[0], snapshot.Completed[0]]);
        var unknown = Rows(unavailable).Single();
        Require(unknown.Unavailable && unknown.Name == "Membership unavailable"
            && unknown.Open.SequenceEqual([1]) && unknown.Completed.SequenceEqual([4]),
            "Unattributed evidence must remain accessible without receiving roster credit.");
        Require(Rows(SnapshotForTest(Request(), [], [], [])).Length == 0,
            "Empty snapshots must not invent zero-activity contributor rows.");
        Set(component, "featureRegistryRequest", snapshot.Request);
        Set(component, "featureRegistrySnapshot", snapshot);
        Set(component, "featureRegistryScrollRunId", snapshot.Request.RunId);
        Call(component, "ToggleFeatureRegistryRow", "id:ALICE");
        Call(component, "CloseFeatureRegistryResults");
        Require(((HashSet<string>)Get(component, "expandedFeatureRegistryRows")!).Count == 0
            && Get(component, "featureRegistryScrollRunId") is null,
            "Closing PR results must reset expansion and pending-scroll state.");

        (string Key, string Name, int[] Open, int[] Completed, bool Unavailable)[] Rows(FeatureRegistrySnapshot data) =>
            ((System.Collections.IEnumerable)Call(component, "BuildFeatureRegistryRows", data)!).Cast<object>()
                .Select(row => (
                    (string)Value(row, "Key"), (string)Value(row, "Name"),
                    ((IReadOnlyList<FeatureRegistryPullRequest>)Value(row, "Open")).Select(pr => pr.Id).ToArray(),
                    ((IReadOnlyList<FeatureRegistryPullRequest>)Value(row, "Completed")).Select(pr => pr.Id).ToArray(),
                    (bool)Value(row, "MembershipUnavailable"))).ToArray();
        static object Value(object row, string property) => row.GetType().GetProperty(property)!.GetValue(row)!;
    }

    private static FeatureRegistrySnapshot SnapshotForTest(ReportRequest request, ImmutableArray<FeatureRegistryPullRequest> open,
        ImmutableArray<FeatureRegistryPullRequest> completed, ImmutableArray<FeatureRegistryPullRequest> unattributed) =>
        (FeatureRegistrySnapshot)typeof(FeatureRegistrySnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single().Invoke([request, Start, End, open, completed, unattributed]);

    private static async Task SelectionAndExport()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            var data = await Collector(client).CollectAsync(Request(view), default);
            Require(data.Open.Select(pr => pr.Id).SequenceEqual([1, 2]), "Old active PRs and drafts must be included.");
            Require(data.Completed.Select(pr => pr.Id).SequenceEqual([4, 3]), "Both inclusive completion boundaries must be included; outside/abandoned excluded.");
            Require(data.Open[0].IsDraft && data.Open.All(pr => pr.CompletedAt is null), "No invented completion time for open PRs.");
            var snapshot = new ReportSnapshot { Request = data.Request, AsOf = data.FinishedAt, FeatureRegistry = data };
            var document = ReportDocumentBuilder.Build(snapshot);
            Require(document.Commits.IsEmpty && document.GitTrends.IsEmpty, "PR-only reports cannot fabricate commit/trend metrics.");
            var before = handler.Requests.Count;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var archive = ReportArchiveBuilder.Build(snapshot);
                var stem = $"Feature_Registry_{view}_20260701_to_20260731";
                Require(archive.FileName == $"{stem}_{data.Request.RunId:N}.zip"
                    && ReportArchiveBuilder.GetFileName(data.Request) == archive.FileName,
                    "Feature Registry archive and Save As suggestion must retain the Feature_Registry prefix and run GUID.");
                using var stream = archive.Content;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                Require(zip.Entries.Count == 2 && zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal)
                    .SetEquals([stem + ".html", stem + "_Detailed.html"]), "Both HTML entries must use the Feature_Registry prefix without the run GUID.");
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var content = await reader.ReadToEndAsync();
                    Require(content.Contains("Feature Registry") && content.Contains("Open PRs") && content.Contains("Completed PRs")
                        && content.Contains("PR 1") && content.Contains("PR 4") && !content.Contains("PR 5"), "Exact PR snapshot records required.");
                    Require(!content.Contains("<script>") && !content.Contains("untrusted.example"), "Encode fields and construct trusted PR links.");
                    Require(!content.Contains("GIT COMMIT TRENDS") && !content.Contains("Files Added"), "No commit format leakage.");
                    if (entry.Name == stem + ".html")
                    {
                        Require(content.Contains("Run Analysis snapshot") && content.Contains(data.Request.RunId.ToString()), "Summary snapshot provenance must remain unchanged.");
                        Require(content.Contains($"href=\"./{stem}_Detailed.html\""), "Summary must link to the GUID-free detailed report.");
                        var previous = HtmlSummaryReportWriter.Write(document, $"{stem}_{data.Request.RunId:N}_Detailed.html");
                        Require(content == previous.Replace($"./{stem}_{data.Request.RunId:N}_Detailed.html", $"./{stem}_Detailed.html", StringComparison.Ordinal),
                            "Entry renaming must not alter Feature Registry summary content beyond its detail link.");
                    }
                    else
                        Require(content.Contains("Captured inputs and export query context") == (view == "teams")
                            && content.Contains("Collection started") == (view == "teams")
                            && content.Contains("Run Analysis snapshot") == (view == "teams"),
                            "Only Teams detailed exports must include the final captured-input context.");
                }
            }
            Require(handler.Requests.Count == before, "Export must never requery PRs.");
            await AdoReportAssert.FailsAsync(() => Task.FromResult(ReportDocumentBuilder.Build(snapshot with
            { Request = data.Request with { Start = data.Request.Start.AddDays(1) } })), "does not match");
            await AdoReportAssert.FailsAsync(() => Task.FromResult(ReportDocumentBuilder.Build(snapshot with { FeatureRegistry = null })), "cannot requery");
        }
        var service = new ReportCollectionService(() => throw new Exception("Ordinary ADO invoked"), () => throw new Exception("ICM invoked"));
        await AdoReportAssert.FailsAsync(() => service.CollectAsync(Request("trends"), null, default), "snapshot");
    }

    private static async Task RosterAttribution()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        var request = Request("teams") with { Roster = [Alice], RosterParentIndices = [null] };
        var authored = await Collector(client).CollectAsync(request, default);
        Require(authored.Open.Length == 2 && authored.Completed.IsEmpty, "Author must not receive completion credit.");
        var closed = await Collector(client).CollectAsync(request with { Roster = [Bob] }, default);
        Require(closed.Open.IsEmpty && closed.Completed.Length == 2, "Closer must not receive open authorship credit.");
        var ambiguous = await Collector(client).CollectAsync(request with
        {
            Roster = [Alice with { Id = "directory-one" }, Alice with { Id = "directory-two" }],
            RosterParentIndices = [null, 0]
        }, default);
        Require(ambiguous.Open.IsEmpty && ambiguous.Unattributed.Length == 2, "Ambiguous membership must be disclosed.");
        var conflict = await Collector(client).CollectAsync(request with { Roster = [Alice with { Email = "different@example.test" }] }, default);
        Require(conflict.Open.IsEmpty && conflict.Unattributed.Length == 2, "Conflicting ID/email must not grant credit or disappear.");
        var outsider = await Collector(client).CollectAsync(request with { Roster = [Alice with { Id = "outsider", Email = "outsider@example.test" }] }, default);
        Require(outsider.Open.IsEmpty && outsider.Unattributed.IsEmpty, "Equal names must not include known outside-roster identities.");
        using var unknownHandler = Http(message =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/pullrequests/3", StringComparison.Ordinal)
                || message.RequestUri.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal)
                    && AdoReportTestHttp.Query(message, "searchCriteria.status") == "completed"
                    && AdoReportTestHttp.Query(message, "$skip") == "0")
            {
                var pr = Pr(3, "completed"); pr["closedBy"] = null;
                return message.RequestUri.AbsolutePath.EndsWith("/3", StringComparison.Ordinal) ? AdoReportTestHttp.Json(pr)
                    : AdoReportTestHttp.Json(new { count = 1, value = new[] { pr } });
            }
            return null;
        });
        using var unknownClient = new HttpClient(unknownHandler);
        var unknown = await Collector(unknownClient).CollectAsync(request, default);
        Require(unknown.Unattributed.Length == 1 && unknown.Completed.IsEmpty, "Unknown actor must not be assigned to author/roster.");
    }

    private static async Task DetailFallbackAndRaces()
    {
        var detailCalls = 0;
        using var handler = Http(message =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/pullrequests/3", StringComparison.Ordinal))
            { detailCalls++; return AdoReportTestHttp.Json(Pr(3, "completed")); }
            if (message.RequestUri.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal)
                && AdoReportTestHttp.Query(message, "searchCriteria.status") == "completed"
                && AdoReportTestHttp.Query(message, "$skip") == "0")
            {
                var pr = Pr(3, "completed"); pr.Remove("closedBy"); pr["repository"] = new { };
                return AdoReportTestHttp.Json(new { count = 1, value = new[] { pr } });
            }
            return null;
        });
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(Request(), default);
        Require(detailCalls == 1 && snapshot.Completed.Single(pr => pr.Id == 3).CompletedBy == Bob, "Authoritative detail fallback required.");
        using var raceHandler = Http(message =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/pullrequests/1", StringComparison.Ordinal))
                return AdoReportTestHttp.Json(Pr(1, "completed"));
            if (message.RequestUri.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal)
                && AdoReportTestHttp.Query(message, "searchCriteria.status") == "completed"
                && AdoReportTestHttp.Query(message, "$skip") == "0")
                return AdoReportTestHttp.Json(new { count = 1, value = new[] { Pr(1, "completed") } });
            return null;
        });
        using var raceClient = new HttpClient(raceHandler);
        var race = await Collector(raceClient).CollectAsync(Request(), default);
        Require(race.Open.All(pr => pr.Id != 1) && race.Completed.Count(pr => pr.Id == 1) == 1, "Status changes must not double count.");
    }

    private static async Task Failures()
    {
        using var handler = Http();
        using var client = new HttpClient(handler);
        var collector = Collector(client);
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request() with
        { Sources = [.. Request().Sources, new("ordinary", "msdata", "A365", "BBC", "develop")] }, default), "only");
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request() with { Analysis = "gitCommits" }, default), "only");
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request() with { Start = End.AddDays(1) }, default), "start");
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request("teams") with { RosterParentIndices = [null, 2, 0] }, default), "hierarchy");
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request("teams") with
        { Roster = Enumerable.Repeat(Alice, ReportRenderingLimits.MaximumIdentities + 1).ToImmutableArray(), RosterParentIndices = [] }, default), "identity limit");
        await AdoReportAssert.FailsAsync(() => collector.CollectAsync(Request() with { Manager = new string('x', ReportRenderingLimits.MaximumRecordCharacters + 1) }, default), "limit");
        Require(handler.Requests.Count == 0, "Invalid requests must fail before HTTP.");
        foreach (var mode in new[] { "forbidden", "partial", "repeat", "wrong-repository", "bad-json" })
        {
            using var failed = Http(message =>
            {
                if (mode == "repeat" && message.RequestUri!.AbsolutePath.EndsWith("/pullrequests/1", StringComparison.Ordinal))
                    return AdoReportTestHttp.Json(Pr(1, "active"));
                if (!message.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal)) return null;
                if (mode == "forbidden") return AdoReportTestHttp.Json(new { }, HttpStatusCode.Forbidden);
                if (mode == "partial") return AdoReportTestHttp.Json(new { count = 2, value = new[] { Pr(1, "active") } });
                if (mode == "bad-json") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
                var pr = Pr(1, "active");
                if (mode == "wrong-repository") pr["repository"] = new { id = Guid.NewGuid() };
                return AdoReportTestHttp.Json(new { count = 1, value = new[] { pr } });
            });
            using var failedClient = new HttpClient(failed);
            var expected = mode switch { "forbidden" => "403", "partial" => "count", "repeat" => "without progress", "wrong-repository" => "different repository", _ => "JSON" };
            await AdoReportAssert.FailsAsync(() => Collector(failedClient).CollectAsync(Request(), default), expected);
        }
        await AdoReportAssert.FailsAsync(() => Collector(client, new() { MaxResponseBytes = 1 }).CollectAsync(Request(), default), "limit");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await collector.CollectAsync(Request(), cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var retryCancellation = new CancellationTokenSource();
        using var retry = new AdoReportTestHttp((_, _) =>
        {
            retryCancellation.CancelAfter(20);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        using var retryClient = new HttpClient(retry);
        try { await Collector(retryClient).CollectAsync(Request(), retryCancellation.Token); throw new Exception("Retry cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    private static void ConfirmationMarkup()
    {
        var component = PreparedComponent();
        Set(component, "selectedOption", FeatureRegistrySource.Analysis);
        Set(component, "featureRegistryStart", Start.UtcDateTime);
        Set(component, "featureRegistryEnd", End.UtcDateTime);
        Set(component, "selectedManager", new ProductivityInsights.Models.Teams.GraphUser { displayName = "Manager <img src=x> & reports" });
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            Set(component, "selectedView", view);
            var html = (string)Call(component, "BuildFeatureRegistryParametersHtml")!;
            Require(html.StartsWith("<div class='parameter-section'><h6 class='fw-bold text-primary mb-3'>Analysis Type: Feature Registry Analysis</h6><table class='table table-sm table-bordered'><tbody>", StringComparison.Ordinal)
                && html.EndsWith("</tbody></table></div>", StringComparison.Ordinal)
                && html.Contains("<td class='fw-bold' style='width: 200px;'>"), "Confirmation must use the existing heading/table/label layout.");
            Require(html.Contains("msdata") && html.Contains("A365") && html.Contains("All target branches")
                && html.Contains("including drafts") && html.Contains("inclusive, UTC wall-clock")
                && html.Contains("Start Date Time</td><td>2026-07-01 00:00")
                && html.Contains("End Date Time</td><td>2026-07-31 00:00"), "PR scope or standard date formatting missing.");
            Require(!html.Contains("<p>") && !html.Contains("Trend Interval") && !html.Contains("<img"), "Unexpected paragraph, chart interval, or unencoded identity.");
            Require(html.Contains("Manager Filter") == (view == "teams")
                && html.Contains("closedBy") == (view == "teams"), "Team-only confirmation parameters have the wrong scope.");
            if (view == "teams") Require(html.Contains("Manager &lt;img src=x&gt; &amp; reports"), "Manager text must remain encoded.");
        }
        Set(component, "featureRegistryStart", null);
        Set(component, "featureRegistryEnd", null);
        var defaults = (string)Call(component, "BuildFeatureRegistryParametersHtml")!;
        Require(defaults.Contains("Start Date Time</td><td>Not set") && defaults.Contains("End Date Time</td><td>Not set")
            && defaults.Contains("Date Defaults") && defaults.Contains("30 days before execution"), "Blank date defaults must remain disclosed.");
    }

    private static async Task IsolatedState()
    {
        var defaults = new AnalysisOptions();
        Require(Get(defaults, "featureRegistryStart") is DateTime start
            && Get(defaults, "featureRegistryEnd") is DateTime end
            && start == (DateTime)Get(defaults, "startDateTime")!
            && end == (DateTime)Get(defaults, "endDateTime")!
            && Math.Abs((end - start).TotalDays - 30) < 0.001,
            "Feature Registry must initialize with the exact same populated date defaults as Git Commits.");
        Set(defaults, "startDateTime", new DateTime(2030, 1, 1));
        Require(!Equals(Get(defaults, "featureRegistryStart"), Get(defaults, "startDateTime")),
            "Matching initial defaults must not link date edits between tabs.");
        var component = PreparedComponent();
        var ordinaryRun = Get(component, "displayedExportRun");
        var originalStart = Get(component, "startDateTime");
        var originalEnd = Get(component, "endDateTime");
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var options = (string[])typeof(AnalysisOptions).GetField("gitCommitCombinationOptions", flags)!.GetValue(null)!;
        Require(options.All(value => !value.Contains("FeatureRegistry")) && (string)Get(component, "selectedGitRepository")! == "BBC-VHD",
            "The new tab must not alter Git sources/defaults.");
        Set(component, "isLoadingResults", true);
        Call(component, "OnOptionSelected", FeatureRegistrySource.Analysis);
        Require(!(bool)Get(component, "isLoadingResults")!, "A superseded ordinary loading state must not leave restored caches spinning.");
        Require((string)Get(component, "selectedGitRepository")! == "BBC-VHD", "Selecting the PR tab changed Git inputs.");
        Set(component, "featureRegistryStart", Start.UtcDateTime);
        Set(component, "featureRegistryEnd", End.UtcDateTime);
        var captured = (ReportRequest)Call(component, "CaptureReportRequest")!;
        Require(captured.Analysis == FeatureRegistrySource.Analysis && captured.Sources.Single().Scope == "FeatureRegistry"
            && captured.Sources.Single().Branch is null && captured.Start == Start && captured.End == End
            && captured.RosterParentIndices.SequenceEqual(new int?[] { null, 0 }), "New tab request capture used ordinary inputs.");
        Require(Equals(originalStart, Get(component, "startDateTime")) && Equals(originalEnd, Get(component, "endDateTime")), "PR dates changed other tabs.");
        Set(component, "isLoadingOrgTree", true);
        Require(Get(component, "FeatureRegistryValidationError") is not null, "Incomplete roster must block the PR tab.");
        Set(component, "isLoadingOrgTree", false);
        using var handler = Http();
        using var client = new HttpClient(handler);
        var data = await Collector(client).CollectAsync(captured, default);
        Set(component, "featureRegistryRequest", captured);
        Set(component, "featureRegistrySnapshot", data);
        Set(component, "analysisResultHtml", FeatureRegistrySource.Analysis);
        var runType = ordinaryRun!.GetType();
        var specialRun = Activator.CreateInstance(runType, captured, 3)!;
        runType.GetProperty("Completed")!.SetValue(specialRun, true);
        runType.GetProperty("Snapshot")!.SetValue(specialRun, new ReportSnapshot { Request = captured, AsOf = data.FinishedAt, FeatureRegistry = data });
        Set(component, "displayedExportRun", specialRun);
        Call(component, "OnOptionSelected", "gitCommits");
        Require(ReferenceEquals(Get(component, "displayedExportRun"), ordinaryRun)
            && ((List<(string, string)>)Get(component, "contributors")!).Count == 1, "PR run lost the ordinary result/export cache.");
        Call(component, "OnOptionSelected", FeatureRegistrySource.Analysis);
        Require(ReferenceEquals(Get(component, "displayedExportRun"), specialRun) && ReferenceEquals(Get(component, "featureRegistrySnapshot"), data),
            "PR snapshot/cache restore lost its own export context.");
        Call(component, "CompleteReportExportContext", 1, false);
        Require((bool)Get(component, "CanExportReports")!, "Stale ordinary completion changed PR export state.");
        using var cancellation = new CancellationTokenSource();
        Set(component, "ThemeService", new ProductivityInsights.Services.ThemeService());
        Set(component, "featureRegistryCancellation", cancellation);
        await component.DisposeAsync();
        Require(cancellation.IsCancellationRequested && !(bool)Get(component, "CanExportReports")!, "Disposal must cancel collection and export.");
    }
}
