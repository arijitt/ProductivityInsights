using System.Net;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.AdoReports.AdoReportAssert;
using static ProductivityInsights.ReportTests.AdoReports.AdoWorkItemCollectorTests;

namespace ProductivityInsights.ReportTests.AdoReports;

public static class AdoGitCollectorTests
{
    private const string Sha = "5f11fd650d972803118b6c24c617bc32c113288c";
    private const string SecondSha = "2222222222222222222222222222222222222222";
    private const string Parent = "1111111111111111111111111111111111111111";
    private const string Repo = "e023c38c-8dd3-450b-8c1a-a9362656748d";

    private static JsonElement DiffParameters(HttpRequestMessage message)
    {
        That(message.Method == HttpMethod.Post && AdoReportTestHttp.Query(message, "api-version") == "7.1",
            "Line counts must use the supported count-only POST API.");
        using var json = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var criteria = json.RootElement;
        That(criteria.GetProperty("baseVersionCommit").GetString() == Parent
            && criteria.GetProperty("targetVersionCommit").GetString() is Sha or SecondSha,
            "Line counts must compare immutable commit IDs against the first parent.");
        var files = criteria.GetProperty("fileDiffParams");
        That(files.GetArrayLength() == 1, "Each comparison must request exactly its intended file.");
        return files[0].Clone();
    }

    private static object FileDiff(string path, string? originalPath, params object[] blocks) => new
    {
        count = 1, value = new[] { new { path, originalPath, lineDiffBlocks = blocks } }
    };

    public static async Task RunAsync()
    {
        await ChangedSidesOnlyAsync();
        await RenameMetadataAsync();
        await SplitRenameMetadataAsync();
        await CompleteFilesAndPrAttributionAsync();
        await MutableBranchPaginationAsync();
        await SourceAndRosterIsolationAsync();
        await RequiredFailuresAsync();
        await TransientAndCancellationAsync();
    }

    private static async Task ChangedSidesOnlyAsync()
    {
        var scenarios = new (string Name, object[] Blocks, int Added, int Deleted)[]
        {
            ("live unchanged block lengths",
            [
                new { changeType = "none", originalLinesCount = 97, modifiedLinesCount = 97 },
                new { changeType = "edit", originalLinesCount = 1, modifiedLinesCount = 2 },
                new { changeType = "none", originalLinesCount = 97, modifiedLinesCount = 98 }
            ], 2, 1),
            ("numeric unchanged block lengths",
            [
                new { changeType = 0, originalLinesCount = 97, modifiedLinesCount = 97 },
                new { changeType = 3, originalLinesCount = 1, modifiedLinesCount = 2 },
                new { changeType = 0, originalLinesCount = 97, modifiedLinesCount = 98 }
            ], 2, 1),
            ("only added side contributes",
                [new { changeType = "add", originalLinesCount = 7, modifiedLinesCount = 3 }], 3, 0),
            ("only deleted side contributes",
                [new { changeType = "delete", originalLinesCount = 4, modifiedLinesCount = 8 }], 0, 4),
            ("unchanged counts never contribute",
                [new { changeType = "none", originalLinesCount = 1, modifiedLinesCount = 2 }], 0, 0),
            ("unused counts are not required",
            [
                new { changeType = "none" },
                new { changeType = "add", modifiedLinesCount = 3 },
                new { changeType = "delete", originalLinesCount = 2 },
                new { changeType = "edit", originalLinesCount = 4, modifiedLinesCount = 1 }
            ], 4, 6),
            ("numeric changed sides",
            [
                new { changeType = 1, originalLinesCount = 7, modifiedLinesCount = 3 },
                new { changeType = 2, originalLinesCount = 4, modifiedLinesCount = 8 }
            ], 3, 4)
        };
        foreach (var scenario in scenarios)
        {
            using var handler = new AdoReportTestHttp((message, _) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                object payload;
                if (path.EndsWith("/repositories/test", StringComparison.Ordinal))
                    payload = new { id = Repo };
                else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                    payload = new { name = "main", commit = new { commitId = Sha } };
                else if (path.EndsWith("/commits", StringComparison.Ordinal))
                    payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
                else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                    payload = Commit(Sha);
                else if (path.EndsWith("/changes", StringComparison.Ordinal))
                    payload = new { changes = AdoReportTestHttp.Query(message, "skip") == "0"
                        ? new[] { new { item = new { path = "/edit.cs", gitObjectType = "blob" }, changeType = "edit" } } : [] };
                else if (path.EndsWith("/items", StringComparison.Ordinal))
                    payload = new { path = "/edit.cs", contentMetadata = new { isBinary = false } };
                else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
                {
                    var parameters = DiffParameters(message);
                    That(parameters.GetProperty("path").GetString() == "/edit.cs"
                        && parameters.GetProperty("originalPath").GetString() == "/edit.cs",
                        "Block attribution must retain the selected file and immutable first-parent comparison.");
                    payload = FileDiff("/edit.cs", "/edit.cs", scenario.Blocks);
                }
                else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                    payload = new { value = System.Array.Empty<object>() };
                else
                    throw new InvalidOperationException($"Unexpected block-attribution request {message.RequestUri}");
                return Task.FromResult(AdoReportTestHttp.Json(payload));
            });
            using var client = new HttpClient(handler);
            var snapshot = await Collector(client).CollectAsync(
                Request("gitCommits", new ReportSource("blocks", "org", "Project", "test", "main")), null, CancellationToken.None);
            var commit = snapshot.Commits.Single();
            var file = commit.Files.Single();
            That(file.Added == scenario.Added && file.Deleted == scenario.Deleted
                && commit.Added == scenario.Added && commit.Deleted == scenario.Deleted,
                $"{scenario.Name}: only the change type's contributing sides may affect file or commit totals.");
        }
    }

    private static async Task RenameMetadataAsync()
    {
        var scenarios = new (string Name, string ChangeType, string? OriginalPath, string? ServerPath, string? ExpectedPath)[]
        {
            ("documented pure rename", "rename", "/old.cs", null, "/old.cs"),
            ("documented rename and edit", "rename, edit", "/old.cs", null, "/old.cs"),
            ("documented path wins", "rename, edit", "/old.cs", "/new.cs", "/old.cs"),
            ("legacy path", "rename, edit", null, "/legacy.cs", "/legacy.cs"),
            ("blank documented path", "rename", " ", "/legacy.cs", "/legacy.cs"),
            ("missing paths", "rename", null, null, null),
            ("blank paths", "rename, edit", "", " ", null)
        };
        foreach (var scenario in scenarios)
        {
            var diffs = 0;
            using var handler = new AdoReportTestHttp((message, _) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                object payload;
                if (path.EndsWith("/repositories/test", StringComparison.Ordinal))
                    payload = new { id = Repo };
                else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                    payload = new { name = "main", commit = new { commitId = Sha } };
                else if (path.EndsWith("/commits", StringComparison.Ordinal))
                    payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
                else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                    payload = Commit(Sha);
                else if (path.EndsWith("/changes", StringComparison.Ordinal))
                {
                    var change = new Dictionary<string, object?>
                    {
                        ["item"] = new { path = "/new.cs", gitObjectType = "blob" },
                        ["changeType"] = scenario.ChangeType
                    };
                    if (scenario.OriginalPath is not null)
                        change["originalPath"] = scenario.OriginalPath;
                    if (scenario.ServerPath is not null)
                        change["sourceServerItem"] = scenario.ServerPath;
                    payload = new { changes = AdoReportTestHttp.Query(message, "skip") == "0"
                        ? new object[] { change } : [] };
                }
                else if (path.EndsWith("/items", StringComparison.Ordinal))
                {
                    That(AdoReportTestHttp.Query(message, "path") == "/new.cs"
                        && AdoReportTestHttp.Query(message, "versionDescriptor.version") == Sha,
                        "Rename metadata must be read at the destination in the new commit.");
                    payload = new { path = "/new.cs", contentMetadata = new { isBinary = false } };
                }
                else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
                {
                    diffs++;
                    var diff = DiffParameters(message);
                    That(diff.GetProperty("originalPath").GetString() == scenario.ExpectedPath
                        && diff.GetProperty("path").GetString() == "/new.cs",
                        $"{scenario.Name}: a rename must compare the authoritative old path against the new path.");
                    payload = FileDiff("/new.cs", scenario.ExpectedPath, scenario.ChangeType.Contains("edit")
                        ? [new { changeType = "edit", modifiedLinesCount = 4, originalLinesCount = 2 }] : []);
                }
                else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                    payload = new { value = System.Array.Empty<object>() };
                else
                    throw new InvalidOperationException($"Unexpected rename-fixture request {message.RequestUri}");
                return Task.FromResult(AdoReportTestHttp.Json(payload));
            });
            using var client = new HttpClient(handler);
            var collector = Collector(client);
            var request = Request("gitCommits", new ReportSource("rename", "org", "Project", "test", "main"));
            if (scenario.ExpectedPath is null)
            {
                await FailsAsync(() => collector.CollectAsync(request, null, CancellationToken.None),
                    "rename", "original path");
                That(diffs == 0, "Missing rename metadata must not become a guessed same-path diff.");
            }
            else
            {
                var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
                var file = snapshot.Commits.Single().Files.Single();
                That(file.Path == "/new.cs" && file.ChangeType == scenario.ChangeType && diffs == 1,
                    $"{scenario.Name}: renamed-file evidence was lost.");
                That(file.Added == (scenario.ChangeType.Contains("edit") ? 4 : 0)
                    && file.Deleted == (scenario.ChangeType.Contains("edit") ? 2 : 0),
                    $"{scenario.Name}: renamed-file line counts were incorrect.");
            }
        }
    }

    private static async Task SplitRenameMetadataAsync()
    {
        foreach (var mode in new[]
        {
            "edited", "pure", "add-target", "ambiguous", "orphan", "missing-source",
            "conflicting-ids", "reused-source", "explicit-pairs", "zero-id"
        })
        {
            var diffCalls = 0;
            var pageSkips = new List<int>();
            var invalid = mode is "ambiguous" or "orphan" or "missing-source" or "conflicting-ids" or "reused-source" or "zero-id";
            var blob = mode == "zero-id" ? new string('0', 40) : new string('a', 40);
            var targetBlob = mode == "conflicting-ids" ? new string('b', 40) : blob;
            var type = mode == "pure" ? "rename" : mode == "add-target" ? "add, targetRename" : "edit, rename";
            object Target(string path, string? originalPath = null) => new
            {
                item = new { path, gitObjectType = "blob", originalObjectId = targetBlob },
                changeType = type, originalPath
            };
            object Source(string path) => new
            {
                item = new { path, gitObjectType = "blob", objectId = "", originalObjectId = blob },
                changeType = "delete, sourceRename"
            };
            object[] targets = mode == "orphan" ? []
                : mode == "explicit-pairs" ? [Target("/new.cs", "/old.cs"), Target("/other-new.cs", "/other.cs")]
                : mode == "reused-source" ? [Target("/new.cs"), Target("/other-new.cs")]
                : [Target("/new.cs", mode == "conflicting-ids" ? "/old.cs" : null)];
            object[] sources = mode == "missing-source" ? []
                : mode is "ambiguous" or "explicit-pairs" ? [Source("/old.cs"), Source("/other.cs")]
                : [Source("/old.cs")];
            using var handler = new AdoReportTestHttp((message, _) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                object payload;
                if (path.EndsWith("/repositories/test", StringComparison.Ordinal))
                    payload = new { id = Repo };
                else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                    payload = new { name = "main", commit = new { commitId = Sha } };
                else if (path.EndsWith("/commits", StringComparison.Ordinal))
                    payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
                else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                    payload = Commit(Sha);
                else if (path.EndsWith("/changes", StringComparison.Ordinal))
                {
                    var skip = int.Parse(AdoReportTestHttp.Query(message, "skip"));
                    pageSkips.Add(skip);
                    // The source follows its destination on another page, as ordering cannot be assumed.
                    payload = new { changes = skip == 0 && targets.Length != 0 ? targets
                        : skip == targets.Length ? sources : [] };
                }
                else if (path.EndsWith("/items", StringComparison.Ordinal))
                {
                    var file = AdoReportTestHttp.Query(message, "path");
                    That(file is "/new.cs" or "/other-new.cs", "A paired rename source must not be fetched as a full deletion.");
                    payload = new { path = file, contentMetadata = new { isBinary = false } };
                }
                else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
                {
                    diffCalls++;
                    var diff = DiffParameters(message);
                    var destination = diff.GetProperty("path").GetString()!;
                    var original = destination == "/new.cs" ? "/old.cs" : "/other.cs";
                    That(diff.GetProperty("originalPath").GetString() == original,
                        "Split rename diff must use its verified old path and first-parent version.");
                    payload = FileDiff(destination, original, mode == "pure" ? []
                        : [new { changeType = "edit", modifiedLinesCount = 4, originalLinesCount = 2 }]);
                }
                else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                    payload = new { value = System.Array.Empty<object>() };
                else
                    throw new InvalidOperationException($"Unexpected split-rename request {message.RequestUri}");
                return Task.FromResult(AdoReportTestHttp.Json(payload));
            });
            using var client = new HttpClient(handler);
            var collect = () => Collector(client).CollectAsync(
                Request("gitCommits", new ReportSource("split-rename", "org", "Project", "test", "main")), null, CancellationToken.None);
            if (invalid)
            {
                await FailsAsync(collect, "rename");
                That(diffCalls == 0, $"{mode}: invalid pairing must fail before assigning line counts.");
            }
            else
            {
                var commit = (await collect()).Commits.Single();
                That(commit.Files.Length == sources.Length + targets.Length && diffCalls == targets.Length,
                    $"{mode}: retain both paths but count each renamed file's delta only once.");
                That(commit.Files.Where(file => file.ChangeType.Contains("sourceRename"))
                    .All(file => file.Added == 0 && file.Deleted == 0), "Source entries must not inflate line counts.");
                That(commit.Added == (mode == "pure" ? 0 : targets.Length * 4)
                    && commit.Deleted == (mode == "pure" ? 0 : targets.Length * 2),
                    $"{mode}: rename totals must come from destination comparisons, never full delete/add counts.");
                That(pageSkips.SequenceEqual(new[] { 0, targets.Length, targets.Length + sources.Length }),
                    "Rename matching must wait for all changed-file pages.");
            }
        }
    }

    private static object Commit(string sha, object? author = null) => new
    {
        commitId = sha, comment = "Full message\nSecond line | <encoded by writer>", commentTruncated = false,
        parents = new[] { Parent, SecondSha },
        author = author ?? new { name = "Member", email = "member@example.com", date = "2020-01-10T01:00:00Z" },
        committer = new { name = "Member", email = "member@example.com", date = "2020-01-10T02:00:00Z" }
    };

    private static object Pr(int id, object? closedBy, string date = "2020-01-20T12:00:00Z",
        string branch = "refs/heads/main") => new
    {
        pullRequestId = id, title = $"PR {id}", status = "completed", targetRefName = branch,
        closedDate = date, closedBy, createdBy = new { displayName = "Wrong Creator", uniqueName = "wrong@example.com" }
    };

    private static async Task CompleteFilesAndPrAttributionAsync()
    {
        var changePages = new List<string>();
        var prPages = new List<string>();
        var detailFallbacks = new List<int>();
        using var handler = new AdoReportTestHttp((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            HttpResponseMessage result;
            if (path.EndsWith("/repositories/workload-sparkcore", StringComparison.Ordinal))
                result = AdoReportTestHttp.Json(new { id = Repo });
            else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                result = AdoReportTestHttp.Json(new { name = "main", commit = new { commitId = Sha } });
            else if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                That(AdoReportTestHttp.Query(message, "api-version") == "7.0"
                    && AdoReportTestHttp.Query(message, "searchCriteria.itemVersion.version") == Sha
                    && AdoReportTestHttp.Query(message, "searchCriteria.itemVersion.versionType") == "commit",
                    "Commit pages must use the selected branch's immutable resolved tip.");
                That(AdoReportTestHttp.Query(message, "searchCriteria.fromDate") == "2020-01-01T12:34:56.000Z",
                    "The original Git wall-clock-as-UTC date formatting must be retained.");
                var skip = AdoReportTestHttp.Query(message, "$skip");
                result = AdoReportTestHttp.Json(new
                {
                    value = skip == "0" ? new[] { new { commitId = Sha } }
                        : skip == "1" ? new[] { new { commitId = SecondSha } } : []
                });
            }
            else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal)
                || path.EndsWith($"/commits/{SecondSha}", StringComparison.Ordinal))
                result = AdoReportTestHttp.Json(Commit(path.Split('/')[^1]));
            else if (path.EndsWith("/changes", StringComparison.Ordinal))
            {
                var skip = int.Parse(AdoReportTestHttp.Query(message, "skip"));
                changePages.Add($"{path}:{skip}");
                That(AdoReportTestHttp.Query(message, "top") == "200", "Changes must use documented top/skip pagination.");
                var first = skip == 0;
                object[] changes = skip switch
                {
                    0 => Enumerable.Range(0, 11).Select(index => (object)new
                    {
                        item = new { path = $"/file{index}.cs", gitObjectType = "blob", commitId = Parent },
                        changeType = "add"
                    }).ToArray(),
                    11 =>
                    [
                        new { item = new { path = "/edited.cs", gitObjectType = "blob" }, changeType = "edit" },
                        new { item = new { path = "/deleted.cs", gitObjectType = "blob" }, changeType = "delete" },
                        new { item = new { path = "/renamed.cs", gitObjectType = "blob" }, changeType = "rename, edit", sourceServerItem = "/old.cs" },
                        new { item = new { path = "/binary.png", gitObjectType = "blob" }, changeType = "add" },
                        new { item = new { path = "/directory", isFolder = true }, changeType = "add" }
                    ],
                    _ => []
                };
                // Deliberately inconsistent metadata must not invalidate authoritative changed-file pages.
                result = AdoReportTestHttp.Json(new { changeCounts = new { Add = first ? 9999 : 1, Edit = 0 }, changes });
            }
            else if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                var file = AdoReportTestHttp.Query(message, "path");
                if (file == "/deleted.cs")
                    That(AdoReportTestHttp.Query(message, "versionDescriptor.version") == Parent,
                        "Deleted-file content must be read at the first parent, never at the deleted version.");
                var binary = file == "/binary.png";
                result = AdoReportTestHttp.Json(new
                {
                    path = file, contentMetadata = new { isBinary = binary },
                    content = binary ? null : file == "/deleted.cs" ? "removed\n" : "first\nsecond\n"
                });
            }
            else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
            {
                var parameters = DiffParameters(message);
                var file = parameters.GetProperty("path").GetString()!;
                var original = parameters.GetProperty("originalPath").GetString();
                if (file == "/renamed.cs")
                    That(original == "/old.cs",
                        "Rename diff must compare the original path.");
                result = AdoReportTestHttp.Json(FileDiff(file, original,
                    new { changeType = 3, modifiedLinesCount = 4, originalLinesCount = 2 }));
            }
            else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
            {
                That(AdoReportTestHttp.Query(message, "api-version") == "7.1"
                    && AdoReportTestHttp.Query(message, "searchCriteria.status") == "completed"
                    && AdoReportTestHttp.Query(message, "searchCriteria.targetRefName") == "refs/heads/main"
                    && AdoReportTestHttp.Query(message, "searchCriteria.queryTimeRangeType") == "closed",
                    "PR selection must request authoritative completion and target-branch semantics.");
                That(AdoReportTestHttp.Query(message, "searchCriteria.minTime") != ""
                    && AdoReportTestHttp.Query(message, "searchCriteria.maxTime") != "", "PR bounds must be sent.");
                var skip = AdoReportTestHttp.Query(message, "$skip");
                prPages.Add(skip);
                result = AdoReportTestHttp.Json(new
                {
                    value = skip switch
                    {
                        "0" => new[] { Pr(1, null) },
                        "1" => new[]
                        {
                            Pr(2, new { displayName = "Member", uniqueName = "member@example.com" }, "2019-01-01T00:00:00Z"),
                            Pr(3, new { displayName = "Member", uniqueName = "member@example.com" }, branch: "refs/heads/other"),
                            Pr(4, null)
                        },
                        _ => []
                    }
                });
            }
            else if (path.Contains("/pullrequests/", StringComparison.Ordinal))
            {
                var id = int.Parse(path.Split('/')[^1]);
                detailFallbacks.Add(id);
                result = AdoReportTestHttp.Json(Pr(id,
                    id == 1 ? new { displayName = "Actual Closer", uniqueName = "closer@example.com" } : null));
            }
            else
                throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
            return Task.FromResult(result);
        });
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(Request("gitCommits",
            new ReportSource("spark", "powerbi", "MWC", "workload-sparkcore", "main")), null, CancellationToken.None);
        That(snapshot.Commits.Length == 2, "Commit pagination must continue after short pages.");
        That(snapshot.Commits.All(commit => commit.Files.Length == 15 && commit.ParentCount == 2
            && commit.Message.Contains('\n')), "All files, parent count, and complete messages must survive.");
        var firstCommit = snapshot.Commits[0];
        That(firstCommit.Added == 30 && firstCommit.Deleted == 5, "Per-file counts must come from content/diffs, not changeCounts.");
        That(firstCommit.Files.Single(file => file.Path == "/binary.png").Added == 0,
            "Explicit binary metadata establishes legitimate non-text zero semantics.");
        That(changePages.Count == 6 && prPages.SequenceEqual(new[] { "0", "1", "4" }),
            "Changes and PRs must exhaust independent pagination.");
        That(detailFallbacks.SequenceEqual(new[] { 1, 4 }), "Omitted completion identities must trigger PR detail reads.");
        That(snapshot.PullRequests.Length == 2 && snapshot.PullRequests[0].CompletedBy.Name == "Actual Closer",
            "Completion window/branch must be enforced and creator must not be credited.");
        That(snapshot.PullRequests[1].CompletedBy.Name == "Unknown completion identity",
            "Missing authoritative identity must remain explicit, never guessed.");
    }

    private static async Task SourceAndRosterIsolationAsync()
    {
        var branchResolutions = 0;
        using var handler = new AdoReportTestHttp((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            var otherRepo = path.Contains("second", StringComparison.Ordinal);
            object payload;
            if (path.EndsWith("/repositories/first", StringComparison.Ordinal)
                || path.EndsWith("/repositories/second", StringComparison.Ordinal))
                payload = new { id = otherRepo ? "second-id" : "first-id" };
            else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
            {
                branchResolutions++;
                payload = new { name = AdoReportTestHttp.Query(message, "name"), commit = new { commitId = Sha } };
            }
            else if (path.EndsWith("/commits", StringComparison.Ordinal))
                payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
            else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                payload = Commit(Sha, new { name = "Outside", email = "outside@example.com", date = "2020-01-10T00:00:00Z" });
            else if (path.EndsWith("/changes", StringComparison.Ordinal))
                payload = new { changes = System.Array.Empty<object>() };
            else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0"
                    ? new[] { Pr(1, new { displayName = "Member", uniqueName = "member@example.com" }) } : [] };
            else
                throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
            return Task.FromResult(AdoReportTestHttp.Json(payload));
        });
        using var client = new HttpClient(handler);
        var request = Request("gitCommits",
            new("s1", "org", "Project", "first", "main"), new("s2", "org", "Project", "second", "main"),
            new("s3", "other-org", "Project", "first", "main"), new("s4", "org", "Project", "first", "release"),
            new("s5", "org", "Project", "first", "main"))
            with { View = "teams", Roster = [new("Member", "member@example.com"), new("Zero", "zero@example.com")] };
        var snapshot = await Collector(client).CollectAsync(request, null, CancellationToken.None);
        That(snapshot.Commits.Length == 5 && snapshot.Commits.Select(commit => commit.EntityKey).Distinct().Count() == 3,
            "Repository and organization-qualified SHAs must preserve scope; overlapping branch memberships remain.");
        That(branchResolutions == 4, "Overlapping memberships must reuse a selected branch's single pinned tip.");
        That(snapshot.PullRequests.Select(pr => pr.EntityKey).Distinct().Count() == 3,
            "PR IDs must be repository and organization-qualified.");
        That(snapshot.Request.Roster.Length == 2 && snapshot.Commits.All(commit => commit.Author.Name == "Outside"),
            "Teams includes either roster author or committer, without rewriting the actual author.");
    }

    private static async Task RequiredFailuresAsync()
    {
        foreach (var mode in new[]
        {
            "invalid-commits", "missing-path", "truncated-diff",
            "missing-lines", "repeat-page", "invalid-number", "unproven-empty-diff",
            "wrong-branch", "invalid-tip", "partial-diff", "missing-diff", "extra-diff", "wrong-diff-path",
            "wrong-original-path", "wrong-diff-count", "unknown-kind", "negative-lines",
            "negative-original-lines", "missing-original-lines", "missing-edit-original-lines", "missing-edit-modified-lines",
            "invalid-original-number", "fractional-lines", "invalid-kind", "unknown-numeric-kind",
            "truncated-none", "missing-blocks", "partial-file-diff"
        })
        {
            using var handler = new AdoReportTestHttp((message, _) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                object payload;
                if (path.EndsWith("/repositories/nebula", StringComparison.Ordinal))
                    payload = new { id = Repo };
                else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                    payload = new
                    {
                        name = mode == "wrong-branch" ? "other" : "main",
                        commit = new { commitId = mode == "invalid-tip" ? "not-a-sha" : Sha }
                    };
                else if (path.EndsWith("/commits", StringComparison.Ordinal))
                {
                    if (mode == "invalid-commits") payload = new { value = (object?)null };
                    else payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" || mode == "repeat-page"
                        ? new[] { new { commitId = Sha } } : [] };
                }
                else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                    payload = Commit(Sha);
                else if (path.EndsWith("/changes", StringComparison.Ordinal))
                    payload = new
                    {
                        changes = AdoReportTestHttp.Query(message, "skip") != "0" || mode == "repeat-page"
                            ? System.Array.Empty<object>()
                            : new object[] { new { item = new { path = mode == "missing-path" ? null : "/edit.cs" }, changeType = "edit" } }
                    };
                else if (path.EndsWith("/items", StringComparison.Ordinal))
                    {
                        payload = mode == "unproven-empty-diff" ? new { path = "/edit.cs" }
                            : new { path = "/edit.cs", contentMetadata = new { isBinary = false } };
                    }
                else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
                {
                    DiffParameters(message);
                    object[] blocks = mode switch
                    {
                        "truncated-diff" => [new { changeType = "add", modifiedLinesCount = 2, originalLinesCount = 0, truncatedAfter = true }],
                        "invalid-number" => [new { changeType = "add", modifiedLinesCount = "bad", originalLinesCount = 0 }],
                        "negative-lines" => [new { changeType = "edit", modifiedLinesCount = -1, originalLinesCount = 0 }],
                        "negative-original-lines" => [new { changeType = "delete", originalLinesCount = -1 }],
                        "missing-original-lines" => [new { changeType = "delete", modifiedLinesCount = 0 }],
                        "missing-edit-original-lines" => [new { changeType = "edit", modifiedLinesCount = 2 }],
                        "missing-edit-modified-lines" => [new { changeType = "edit", originalLinesCount = 1 }],
                        "invalid-original-number" => [new { changeType = "delete", originalLinesCount = "bad" }],
                        "fractional-lines" => [new { changeType = "add", modifiedLinesCount = 1.5 }],
                        "unknown-kind" => [new { changeType = "future" }],
                        "invalid-kind" => [new { changeType = true }],
                        "unknown-numeric-kind" => [new { changeType = 4 }],
                        "truncated-none" => [new { changeType = "none", modifiedLinesCount = 1, originalLinesCount = 1, truncatedBefore = true }],
                        "unproven-empty-diff" => [],
                        _ => [new { changeType = "add" }]
                    };
                    payload = mode switch
                    {
                        "partial-diff" => new { isPartial = true, value = System.Array.Empty<object>() },
                        "partial-file-diff" => new { value = new[]
                        {
                            new { path = "/edit.cs", originalPath = "/edit.cs", isPartial = true, lineDiffBlocks = System.Array.Empty<object>() }
                        } },
                        "missing-diff" => new { value = System.Array.Empty<object>() },
                        "extra-diff" => new { value = new object[] { new { path = "/edit.cs" }, new { path = "/extra.cs" } } },
                        "wrong-diff-count" => new { count = 2, value = new[] { new { path = "/edit.cs" } } },
                        "wrong-diff-path" => FileDiff("/different.cs", "/edit.cs", blocks),
                        "wrong-original-path" => FileDiff("/edit.cs", "/different.cs", blocks),
                        "missing-blocks" => new { value = new[] { new { path = "/edit.cs", originalPath = "/edit.cs" } } },
                        _ => FileDiff("/edit.cs", "/edit.cs", blocks)
                    };
                }
                else
                    throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
                return Task.FromResult(AdoReportTestHttp.Json(payload));
            });
            using var client = new HttpClient(handler);
            await FailsAsync(() => Collector(client).CollectAsync(Request("gitCommits",
                    new ReportSource("nebula", "msdata", "A365", "nebula", "main")), null, CancellationToken.None),
                "msdata/A365/nebula", mode switch
                {
                    "invalid-commits" => "value", "missing-path" => "path",
                    "truncated-diff" => "truncated", "repeat-page" => "pagination",
                    "unproven-empty-diff" => "zero line counts",
                    "wrong-branch" => "different branch", "invalid-tip" => "invalid commit SHA",
                    "invalid-number" or "negative-lines" or "negative-original-lines" or "invalid-original-number" or "fractional-lines" => "integer",
                    "partial-diff" or "partial-file-diff" => "partial", "missing-diff" or "extra-diff" or "wrong-diff-count" => "exactly",
                    "wrong-diff-path" => "different path", "wrong-original-path" => "different original path",
                    "unknown-kind" or "unknown-numeric-kind" => "Unknown diff", "invalid-kind" => "Invalid diff",
                    "missing-original-lines" or "missing-edit-original-lines" => "originalLinesCount",
                    "truncated-none" => "truncated", "missing-blocks" => "lineDiffBlocks", _ => "modifiedLinesCount"
                });
        }
    }

    private static async Task MutableBranchPaginationAsync()
    {
        var resolutions = 0;
        var forcePushed = false;
        var skips = new List<int>();
        using var handler = new AdoReportTestHttp((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            object payload;
            if (path.EndsWith("/repositories/repo", StringComparison.Ordinal))
                payload = new { id = Repo };
            else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
            {
                resolutions++;
                payload = new { name = "main", commit = new { commitId = forcePushed ? SecondSha : Sha } };
            }
            else if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                var version = AdoReportTestHttp.Query(message, "searchCriteria.itemVersion.version");
                var pinned = AdoReportTestHttp.Query(message, "searchCriteria.itemVersion.versionType") == "commit";
                if (pinned)
                    That(version == Sha, "Every page must retain the originally resolved SHA.");
                var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
                skips.Add(skip);
                var visible = pinned || !forcePushed ? new[] { Sha, SecondSha } : new[] { SecondSha };
                payload = new { value = visible.Skip(skip).Take(1).Select(commitId => new { commitId }) };
            }
            else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
            {
                forcePushed = true;
                payload = Commit(Sha);
            }
            else if (path.EndsWith($"/commits/{SecondSha}", StringComparison.Ordinal))
                payload = Commit(SecondSha);
            else if (path.EndsWith("/changes", StringComparison.Ordinal))
                payload = new { changes = System.Array.Empty<object>() };
            else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
            {
                That(AdoReportTestHttp.Query(message, "searchCriteria.targetRefName") == "refs/heads/main",
                    "PR target filtering must retain the original branch, not the resolved SHA.");
                payload = new { value = System.Array.Empty<object>() };
            }
            else
                throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
            return Task.FromResult(AdoReportTestHttp.Json(payload));
        });
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(Request("gitCommits",
            new ReportSource("s", "org", "Project", "repo", "main")), null, CancellationToken.None);
        That(forcePushed && resolutions == 1 && skips.SequenceEqual(new[] { 0, 1, 2 }),
            "The fixture must force-push between pages, without resolving the branch again.");
        That(snapshot.Commits.Select(commit => commit.Id).SequenceEqual(new[] { Sha, SecondSha }),
            "A force-push dropping the seen commit must not skip a never-collected commit.");
        That(snapshot.Request.Sources.Single().Branch == "main"
            && snapshot.CoverageNotes.Any(note => note.Contains($"pinned to commit {Sha}", StringComparison.Ordinal)),
            "Source metadata must retain the branch and disclose its pinned SHA.");
    }

    private static async Task TransientAndCancellationAsync()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var handler = new AdoReportTestHttp((_, token) =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = AdoReportTestHttp.Json(new { }, HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(throttled);
            }
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not propagated into the request.");
        });
        using var client = new HttpClient(handler);
        try
        {
            await Collector(client).CollectAsync(Request("gitCommits",
                new ReportSource("s", "org", "Project", "repo", "main")), null, cancellation.Token);
            throw new InvalidOperationException("Cancellation must abort retry collection.");
        }
        catch (OperationCanceledException) { }
        That(calls == 2, "A transient throttling response may retry; cancellation must stop immediately.");
    }
}
