using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Xml.Linq;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.ReportTests.AdoReports;
using ProductivityInsights.ReportTests.ReportWriters;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.CompatibilityTests;
using static ProductivityInsights.ReportTests.FeatureRegistryTests;

namespace ProductivityInsights.ReportTests;

public static class FeatureRegistryFileTests
{
    public static async Task RunAsync()
    {
        await DeletedFilePaths();
        await CompleteFiles();
        await EmptyAndCursorlessPages();
        await FailuresAndCancellation();
    }

    private static async Task DeletedFilePaths()
    {
        foreach (var pathShape in new[] { "null", "absent", "empty", "blank" })
            foreach (var pathField in new[] { "originalPath", "sourceServerItem" })
            {
                using var handler = Http(message =>
                {
                    if (message.RequestUri!.AbsolutePath.EndsWith("/iterations"))
                        return AdoReportTestHttp.Json(new { count = 1, value = new[] { new { id = 1 } } });
                    if (!message.RequestUri.AbsolutePath.EndsWith("/iterations/1/changes")) return null;
                    var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
                    var entries = Enumerable.Range(skip + 1, Math.Min(3, Math.Max(0, 10 - skip))).Select(id =>
                    {
                        var item = new Dictionary<string, object?> { ["originalObjectId"] = new string('a', 40) };
                        if (pathShape != "absent") item["path"] = pathShape switch { "null" => null, "empty" => "", _ => " " };
                        return new Dictionary<string, object?>
                        {
                            ["changeId"] = id, ["changeTrackingId"] = id, ["item"] = item,
                            ["changeType"] = id == 10 ? "DELETE, sourceRename" : "delete",
                            [pathField] = $"/removed/file_{id:00} <old> & [x].md"
                        };
                    }).ToArray();
                    return AdoReportTestHttp.Json(new { changeEntries = entries });
                });
                using var client = new HttpClient(handler);
                var data = await Collector(client).CollectAsync(Request("teams"), default);
                foreach (var pr in data.Open.Concat(data.Completed))
                {
                    var files = pr.FileChanges!;
                    Require(files.IterationId == 1 && files.Files.Length == 10
                        && files.Files.Select(file => file.Path).SequenceEqual(Enumerable.Range(1, 10)
                            .Select(id => $"/removed/file_{id:00} <old> & [x].md"))
                        && files.Files.All(file => file.Path == file.OriginalPath && file.ChangeType.Contains("delete", StringComparison.OrdinalIgnoreCase)),
                        "Deleted PR files must retain all recorded original paths when the current item path is null or absent.");
                }
                var calls = handler.Requests.Count;
                var html = HtmlDetailedReportWriter.Write(Wrap(data));
                var folds = ReportFoldMarkup.Parse(html).Where(fold => fold.Kind == "item").ToArray();
                Require(folds.Length == 4 && folds.All(fold => fold.Content.Contains("Changed Files (10)")
                    && XElement.Parse("<section>" + fold.Content + "</section>").Value.Contains("/removed/file_10 <old> & [x].md [DELETE, sourceRename]"))
                    && !html.Contains("<old>") && !html.Contains("Changed Files: unavailable") && handler.Requests.Count == calls,
                    "Both open/completed PR reports must safely show every deletion, not hide it or requery during export.");
                Require(handler.Requests.Where(url => url.Contains("/changes?")).Count() == 20,
                    "Deleted-only short pages must paginate fully, including the confirming empty page.");
            }
    }

    private static async Task CompleteFiles()
    {
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            using var handler = Http();
            using var client = new HttpClient(handler);
            var data = await Collector(client).CollectAsync(Request(view), default);
            var records = data.Open.Concat(data.Completed).ToArray();
            var fileCalls = handler.Requests.Where(url => url.Contains("/iterations")).ToArray();
            if (view != "teams")
            {
                Require(fileCalls.Length == 0 && records.All(pr => pr.FileChanges is null),
                    "Details/Trends must not add file queries or change their snapshot shape.");
                Require(!HtmlDetailedReportWriter.Write(Wrap(data)).Contains("Changed Files"),
                    "File lists are Teams-only.");
                continue;
            }
            foreach (var pr in records)
            {
                var changes = pr.FileChanges!;
                Require(changes.IterationId == 3 && changes.Files.Length == 14
                    && changes.Files.Any(file => file.Path == $"/pr-{pr.Id}/file_12.cs")
                    && changes.Files.Any(file => file.Path == "/shared.cs" && file.ChangeType == "delete")
                    && changes.Files.Any(file => file.OriginalPath == $"/pr-{pr.Id}/old <file> & [x].cs"),
                    "Each open/completed PR needs its complete latest-iteration list with deletes and both rename paths.");
                Require(changes.Files.Select(file => file.Path).SequenceEqual(changes.Files.Select(file => file.Path).Order(StringComparer.Ordinal)),
                    "File lists must be deterministic and case-sensitive.");
                Require(fileCalls.Count(url => url.Contains($"/pullrequests/{pr.Id}/iterations?")) == 1,
                    "Each PR must capture one iteration, not move the comparison between pages.");
            }
            Require(fileCalls.All(url => !url.Contains("/pullrequests/5/") && !url.Contains("/pullrequests/6/") && !url.Contains("/pullrequests/7/")),
                "Out-of-window and abandoned PRs must not trigger file reads.");
            var report = Wrap(data);
            var html = HtmlDetailedReportWriter.Write(report);
            var before = handler.Requests.Count;
            var folds = ReportFoldMarkup.Parse(html).Where(fold => fold.Kind == "item").ToArray();
            Require(folds.Length == 4 && folds.All(fold => fold.Content.Contains("Changed Files (14)")
                && fold.Content.Contains("iteration 3") && fold.Content.Contains("compareTo=0")),
                "Every PR file list must be inside its own item disclosure with comparison provenance.");
            foreach (var (pr, index) in records.Select((pr, index) => (pr, index)))
            {
                var element = XElement.Parse("<section>" + folds[index].Content + "</section>");
                var lines = element.Descendants("div").Where(node => (string?)node.Attribute("class") == "report-line report-file").ToArray();
                Require(lines.Length == 14 && lines.Any(line => line.Value.Contains($"/pr-{pr.Id}/file_12.cs"))
                    && lines.Any(line => line.Value.Contains($"/pr-{pr.Id}/old <file> & [x].cs \u2192 /pr-{pr.Id}/new <file> & [x].cs [rename, edit]"))
                    && !element.Descendants("file").Any(), "Lists must not truncate, cross PR boundaries or parse file names as HTML.");
            }
            using var output = new StringWriter();
            HtmlDetailedReportWriter.WriteTo(ReportDocumentBuilder.Build(report), output);
            Require(html == output.ToString() && handler.Requests.Count == before, "Repeated detailed exports must reuse captured file lists.");
            var withoutFiles = Copy(data, records.Select(pr => pr with { FileChanges = null }));
            Require(HtmlSummaryReportWriter.Write(report, "details.html") == HtmlSummaryReportWriter.Write(Wrap(withoutFiles), "details.html"),
                "The standalone summary must remain byte-for-byte unchanged by file metadata.");
            var legacy = HtmlDetailedReportWriter.Write(Wrap(withoutFiles));
            var legacyText = XElement.Parse(legacy[legacy.IndexOf("<main>", StringComparison.Ordinal)..(legacy.IndexOf("</main>", StringComparison.Ordinal) + 7)]).Value;
            Require(legacyText.Contains("Changed Files: unavailable") && legacy.Contains("Run Analysis again")
                && !legacy.Contains("Changed Files (0)"), "Uncaptured older snapshots must not claim zero files or requery.");
            var bad = Copy(data, records.Select(pr => pr with { FileChanges = new(0, []) }));
            await AdoReportAssert.FailsAsync(() => Task.FromResult(ReportDocumentBuilder.Build(Wrap(bad))), "incomplete");
        }
        using var outsiderHandler = Http();
        using var outsiderClient = new HttpClient(outsiderHandler);
        var outsider = await Collector(outsiderClient).CollectAsync(Request("teams") with
        { Roster = [new("Outsider", "outside@example.test", "outside")], RosterParentIndices = [null] }, default);
        Require(outsider.Open.IsEmpty && outsider.Completed.IsEmpty
            && outsiderHandler.Requests.All(url => !url.Contains("/iterations")), "Do not collect file lists for known outside-roster PRs.");
        using var unknownHandler = Http();
        using var unknownClient = new HttpClient(unknownHandler);
        var unknown = await Collector(unknownClient).CollectAsync(Request("teams") with
        { Roster = [Alice with { Id = "one" }, Alice with { Id = "two" }], RosterParentIndices = [null, 0] }, default);
        Require(unknown.Unattributed.Length == 2 && unknown.Unattributed.All(pr => pr.FileChanges?.Files.Length == 14)
            && HtmlDetailedReportWriter.Write(Wrap(unknown)).Contains("Changed Files (14)"),
            "Membership-unavailable PRs must retain their file lists without gaining roster credit.");
    }

    private static async Task EmptyAndCursorlessPages()
    {
        foreach (var mode in new[] { "empty", "cursorless", "server-cursor", "deduplicate", "legacy-rename" })
        {
            using var handler = Http(message =>
            {
                if (!message.RequestUri!.AbsolutePath.EndsWith("/changes")) return null;
                var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
                if (mode == "empty")
                    return AdoReportTestHttp.Json(new { changeEntries = Array.Empty<object>(), nextSkip = 0, nextTop = 0 });
                if (mode == "cursorless")
                    return AdoReportTestHttp.Json(new { changeEntries = skip < 2 ? new[] { FileEntry(skip + 1, $"/file{skip}.cs", "edit") } : [] });
                if (mode == "server-cursor")
                    return AdoReportTestHttp.Json(new
                    {
                        changeEntries = new[] { FileEntry(skip + 1, $"/file{skip}.cs", "edit") },
                        nextSkip = skip == 0 ? 8 : 0, nextTop = skip == 0 ? 5 : 0
                    });
                if (mode == "legacy-rename")
                {
                    var entry = FileEntry(1, "/new.cs", "rename");
                    entry["sourceServerItem"] = "/old.cs";
                    return AdoReportTestHttp.Json(new { changeEntries = new[] { entry }, nextSkip = 0, nextTop = 0 });
                }
                return AdoReportTestHttp.Json(new
                {
                    changeEntries = new[] { FileEntry(1, "/File.cs", "edit"), FileEntry(1, "/File.cs", "edit"),
                        FileEntry(2, "/File.cs", "edit"), FileEntry(3, "/file.cs", "edit") }, nextSkip = 0, nextTop = 0
                });
            });
            using var client = new HttpClient(handler);
            var data = await Collector(client).CollectAsync(Request("teams"), default);
            var changes = data.Open[0].FileChanges!;
            Require(changes.Files.Length == (mode == "empty" ? 0 : mode == "legacy-rename" ? 1 : 2),
                "Empty/short cursorless pages and exact deduplication must preserve full case-sensitive evidence.");
            if (mode == "empty")
                Require(HtmlDetailedReportWriter.Write(Wrap(data)).Contains("No changed files in the captured PR comparison."),
                    "A successfully collected empty file list must be explicit.");
            if (mode == "legacy-rename")
                Require(changes.Files[0].OriginalPath == "/old.cs", "Retain the legacy rename source path.");
            if (mode == "cursorless")
                Require(handler.Requests.Count(url => url.Contains("/changes?")) == 12,
                    "Short cursorless pages must continue until an empty page for every PR.");
            if (mode == "server-cursor")
                Require(changes.Files.Any(file => file.Path == "/file8.cs")
                    && handler.Requests.Where(url => url.Contains("/changes?")).Count() == 8,
                    "Follow the server's authoritative continuation rather than guessing cursors from page length.");
        }
    }

    private static async Task FailuresAndCancellation()
    {
        foreach (var mode in new[]
        {
            "forbidden", "bad-json", "missing-entries", "missing-path", "missing-type", "missing-id",
            "partial", "truncated", "bad-next", "half-cursor", "repeat", "conflict", "empty-more",
            "no-iterations", "bad-count", "duplicate-iteration", "too-large",
            "delete-without-path", "edit-original-only", "add-original-only", "rename-original-only",
            "undelete-original-only", "delete-add-original-only", "delete-target-rename-original-only", "invalid-delete-path"
        })
        {
            using var handler = Http(message =>
            {
                var uri = message.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/iterations"))
                {
                    if (mode == "no-iterations") return AdoReportTestHttp.Json(new { count = 0, value = Array.Empty<object>() });
                    if (mode == "bad-count") return AdoReportTestHttp.Json(new { count = 2, value = new[] { new { id = 1 } } });
                    if (mode == "duplicate-iteration") return AdoReportTestHttp.Json(new { count = 2, value = new[] { new { id = 1 }, new { id = 1 } } });
                }
                if (!uri.AbsolutePath.EndsWith("/changes")) return null;
                if (mode == "forbidden") return AdoReportTestHttp.Json(new { }, HttpStatusCode.Forbidden);
                if (mode == "bad-json") return new(HttpStatusCode.OK) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
                if (mode == "missing-entries") return AdoReportTestHttp.Json(new { nextSkip = 0, nextTop = 0 });
                var entry = FileEntry(1, "/file.cs", "edit");
                if (mode.EndsWith("-original-only", StringComparison.Ordinal))
                {
                    entry["item"] = new { path = (string?)null };
                    entry["originalPath"] = "/old.cs";
                    entry["changeType"] = mode switch
                    {
                        "delete-add-original-only" => "delete, add",
                        "delete-target-rename-original-only" => "delete, targetRename",
                        _ => mode[..mode.IndexOf('-')]
                    };
                }
                if (mode == "delete-without-path") { entry["item"] = new { path = (string?)null }; entry["changeType"] = "delete"; }
                if (mode == "invalid-delete-path")
                { entry["item"] = new { path = 123 }; entry["originalPath"] = "/old.cs"; entry["changeType"] = "delete"; }
                if (mode == "missing-path") entry["item"] = new { };
                if (mode == "missing-type") entry.Remove("changeType");
                if (mode == "missing-id") entry.Remove("changeId");
                if (mode == "too-large") entry["item"] = new { path = "/" + new string('x', ReportRenderingLimits.MaximumRecordCharacters) };
                var page = new Dictionary<string, object?> { ["changeEntries"] = new[] { entry }, ["nextSkip"] = 0, ["nextTop"] = 0 };
                if (mode == "partial") page["isPartial"] = true;
                if (mode == "truncated") page["isTruncated"] = true;
                if (mode == "bad-next") { page["nextSkip"] = 0; page["nextTop"] = 1000; }
                if (mode == "half-cursor") page.Remove("nextSkip");
                if (mode == "repeat") { page.Remove("nextSkip"); page.Remove("nextTop"); }
                if (mode == "conflict") page["changeEntries"] = new[] { entry, FileEntry(1, "/different.cs", "edit") };
                if (mode == "empty-more") { page["changeEntries"] = Array.Empty<object>(); page["nextSkip"] = 1; page["nextTop"] = 1000; }
                return AdoReportTestHttp.Json(page);
            });
            using var client = new HttpClient(handler);
            await AdoReportAssert.FailsAsync(() => Collector(client).CollectAsync(Request("teams"), default),
                mode == "too-large" ? "limit" : "changed files");
        }
        using var cancellation = new CancellationTokenSource();
        using var cancelledHandler = Http(message =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/changes")) cancellation.Cancel();
            return null;
        });
        using var cancelledClient = new HttpClient(cancelledHandler);
        try
        {
            await Collector(cancelledClient).CollectAsync(Request("teams"), cancellation.Token);
            throw new InvalidOperationException("File collection ignored cancellation.");
        }
        catch (OperationCanceledException) { }
        using var oversizedHandler = Http(message => message.RequestUri!.AbsolutePath.EndsWith("/changes")
            ? AdoReportTestHttp.Json(new { changeEntries = new[] { FileEntry(1, "/" + new string('x', 5000), "edit") } })
            : null);
        using var oversizedClient = new HttpClient(oversizedHandler);
        await AdoReportAssert.FailsAsync(() => Collector(oversizedClient, new() { MaxResponseBytes = 4096 })
            .CollectAsync(Request("teams"), default), "changed files", "response limit");
    }

    private static ReportSnapshot Wrap(FeatureRegistrySnapshot data) =>
        new() { Request = data.Request, AsOf = data.FinishedAt, FeatureRegistry = data };

    private static FeatureRegistrySnapshot Copy(FeatureRegistrySnapshot data, IEnumerable<FeatureRegistryPullRequest> records) =>
        (FeatureRegistrySnapshot)typeof(FeatureRegistrySnapshot)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single()
            .Invoke([data.Request, data.StartedAt, data.FinishedAt,
                records.Where(pr => pr.Status == "active").ToImmutableArray(),
                records.Where(pr => pr.Status == "completed").ToImmutableArray(), data.Unattributed]);
}
