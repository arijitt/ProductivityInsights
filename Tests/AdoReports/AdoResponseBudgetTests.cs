using System.Net;
using System.Text;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.AdoReports.AdoReportAssert;
using static ProductivityInsights.ReportTests.AdoReports.AdoWorkItemCollectorTests;

namespace ProductivityInsights.ReportTests.AdoReports;

public static class AdoResponseBudgetTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Parent = "2222222222222222222222222222222222222222";

    public static async Task RunAsync()
    {
        await UndeclaredResponseLimitAsync();
        await DeclaredResponseLimitAsync();
        await CumulativeBudgetAsync();
        await OverlappingCancelledCollectionsAsync();
        await FileContentResponseLimitAsync();
        await CountOnlyDiffBudgetAsync();
        await ExactLimitAsync();
        await StreamingCancellationAsync();
    }

    private static AdoReportCollector Limited(HttpClient client, int response, long collection) =>
        new(client, _ => Task.FromResult("fixture-token"), new AdoReportCollectionLimits
        {
            MaxResponseBytes = response,
            MaxCollectionBytes = collection
        });

    private static ReportRequest WorkRequest(params ReportSource[] sources) => Request("workItems", sources);

    private static HttpResponseMessage StreamResponse(GeneratedResponseStream stream, bool chunked = false,
        long? declaredLength = null)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        result.Content.Headers.ContentType = new("application/json");
        if (declaredLength.HasValue)
            result.Content.Headers.ContentLength = declaredLength.Value;
        else
            That(result.Content.Headers.ContentLength is null, "Fixture must not advertise a Content-Length.");
        if (chunked)
            result.Headers.TransferEncodingChunked = true;
        return result;
    }

    private static GeneratedResponseStream EmptyWorkResponse(long length, Action<int>? onRead = null,
        Func<int, CancellationToken, Task>? beforeRead = null)
    {
        const string prefix = "{\"workItems\":[]}";
        return new GeneratedResponseStream(prefix, length - Encoding.UTF8.GetByteCount(prefix), (byte)' ', "",
            onRead, beforeRead);
    }

    private static async Task UndeclaredResponseLimitAsync()
    {
        foreach (var chunked in new[] { false, true })
        {
            using var stream = EmptyWorkResponse(10_000_000);
            using var handler = new AdoReportTestHttp((_, _) => Task.FromResult(StreamResponse(stream, chunked)));
            using var client = new HttpClient(handler);
            await FailsAsync(() => Limited(client, 128, 1024).CollectAsync(
                WorkRequest(new ReportSource("s", "org", "Project", "Area")), null, CancellationToken.None),
                "org/Project/Area", "response limit", "no partial report");
            That(stream.BytesRead == 129 && stream.WasDisposed && handler.Requests.Count == 1,
                "Unknown-length/chunked responses must abort at limit+1, dispose, and never parse a truncated success.");
        }
    }

    private static async Task DeclaredResponseLimitAsync()
    {
        using var stream = EmptyWorkResponse(10_000_000);
        using var handler = new AdoReportTestHttp((_, _) =>
            Task.FromResult(StreamResponse(stream, declaredLength: 10_000_000)));
        using var client = new HttpClient(handler);
        await FailsAsync(() => Limited(client, 128, 1024).CollectAsync(
            WorkRequest(new ReportSource("s", "org", "Project", "Area")), null, CancellationToken.None),
            "response limit");
        That(stream.BytesRead == 0 && stream.WasDisposed, "Oversized declared bodies must fail before reading content.");
    }

    private static async Task CumulativeBudgetAsync()
    {
        var streams = new List<GeneratedResponseStream>();
        using var handler = new AdoReportTestHttp((_, _) =>
        {
            var stream = EmptyWorkResponse(100);
            streams.Add(stream);
            return Task.FromResult(StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        var collector = Limited(client, 128, 150);
        await FailsAsync(() => collector.CollectAsync(WorkRequest(
            new("first", "org-a", "Project", "Area"),
            new("second", "org-b", "Project", "Area"),
            new("third", "org-c", "Project", "Area")), null, CancellationToken.None),
            "org-b/Project/Area", "cumulative collection budget", "no partial report");
        That(streams.Count == 2 && streams[0].BytesRead == 100 && streams[1].BytesRead == 51
            && streams.All(stream => stream.WasDisposed),
            "Individually small responses must share one collection budget, with early abort and no third source.");

        var snapshot = await collector.CollectAsync(
            WorkRequest(new ReportSource("next-run", "org-next", "Project", "Area")), null, CancellationToken.None);
        That(snapshot.WorkItems.IsEmpty && streams.Count == 3 && streams[^1].BytesRead == 100,
            "A later collection on the same collector must get a fresh independent budget after failure.");
    }

    private static async Task FileContentResponseLimitAsync()
    {
        foreach (var changeType in new[] { "add", "delete" })
        {
            using var stream = new GeneratedResponseStream(
                "{\"path\":\"/large.txt\",\"contentMetadata\":{\"isBinary\":false},\"content\":\"",
                10_000_000, (byte)'x', "\"}");
            using var handler = new AdoReportTestHttp((message, _) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                object payload;
                if (path.EndsWith("/repositories/repo", StringComparison.Ordinal))
                    payload = new { id = "repo-id" };
                else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                    payload = new { name = "main", commit = new { commitId = Sha } };
                else if (path.EndsWith("/commits", StringComparison.Ordinal))
                    payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0"
                        ? new[] { new { commitId = Sha } } : [] };
                else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                    payload = new
                    {
                        commitId = Sha, comment = "Small commit", parents = new[] { Parent },
                        author = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" },
                        committer = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" }
                    };
                else if (path.EndsWith("/changes", StringComparison.Ordinal))
                    payload = new
                    {
                        changes = AdoReportTestHttp.Query(message, "skip") == "0"
                            ? new[] { new { item = new { path = "/large.txt", gitObjectType = "blob" }, changeType } } : []
                    };
                else if (path.EndsWith("/items", StringComparison.Ordinal))
                {
                    That(AdoReportTestHttp.Query(message, "versionDescriptor.version") == (changeType == "add" ? Sha : Parent),
                        "Added/deleted bounded file reads must retain the correct version.");
                    return Task.FromResult(StreamResponse(stream, chunked: true));
                }
                else
                    throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
                return Task.FromResult(AdoReportTestHttp.Json(payload));
            });
            using var client = new HttpClient(handler);
            await FailsAsync(() => Limited(client, 1024, 8192).CollectAsync(
                Request("gitCommits", new ReportSource("s", "org", "Project", "repo", "main")),
                null, CancellationToken.None), Sha, "/large.txt", "response limit");
            That(stream.BytesRead == 1025 && stream.WasDisposed,
                "Added/deleted file bodies must fail before full JSON allocation or line counting.");
        }
    }

    private static async Task CountOnlyDiffBudgetAsync()
    {
        const int fileCount = 64;
        var diffCalls = 0;
        long responseBytes = 0;
        using var handler = new AdoReportTestHttp((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            object payload;
            if (path.EndsWith("/repositories/repo", StringComparison.Ordinal))
                payload = new { id = "repo-id" };
            else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                payload = new { name = "main", commit = new { commitId = Sha } };
            else if (path.EndsWith("/commits", StringComparison.Ordinal))
                payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
            else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                payload = new
                {
                    commitId = Sha, comment = "Large files, small deltas", parents = new[] { Parent },
                    author = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" },
                    committer = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" }
                };
            else if (path.EndsWith("/changes", StringComparison.Ordinal))
                payload = new
                {
                    changes = AdoReportTestHttp.Query(message, "skip") == "0"
                        ? Enumerable.Range(0, fileCount).Select(index => new
                        {
                            item = new { path = $"/large-{index}.cs", gitObjectType = "blob" }, changeType = "edit"
                        }).ToArray() : []
                };
            else if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                That(AdoReportTestHttp.Query(message, "includeContent") == "false",
                    "Edited files require metadata, not full source text.");
                payload = new { path = AdoReportTestHttp.Query(message, "path"), contentMetadata = new { isBinary = false } };
            }
            else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
            {
                diffCalls++;
                That(message.Method == HttpMethod.Post && AdoReportTestHttp.Query(message, "api-version") == "7.1",
                    "Diff collection must use the count-only API, never the full-text UI endpoint.");
                using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var criteria = body.RootElement;
                That(criteria.GetProperty("baseVersionCommit").GetString() == Parent
                    && criteria.GetProperty("targetVersionCommit").GetString() == Sha,
                    "Compact comparisons must retain immutable first-parent semantics.");
                var files = criteria.GetProperty("fileDiffParams");
                That(files.GetArrayLength() == 1, "The response must be scoped to exactly one file.");
                var file = files[0].GetProperty("path").GetString()!;
                That(files[0].GetProperty("originalPath").GetString() == file,
                    "An ordinary edit must compare the same path at both versions.");
                payload = new
                {
                    count = 1,
                    value = new[]
                    {
                        new
                        {
                            path = file,
                            lineDiffBlocks = new[]
                            {
                                new { changeType = "none", originalLinesCount = 100_000, modifiedLinesCount = 100_000 },
                                new { changeType = "add", originalLinesCount = 0, modifiedLinesCount = 5 },
                                new { changeType = "delete", originalLinesCount = 7, modifiedLinesCount = 0 },
                                new { changeType = "edit", originalLinesCount = 2, modifiedLinesCount = 3 }
                            }
                        }
                    }
                };
            }
            else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                payload = new { value = Array.Empty<object>() };
            else
                throw new InvalidOperationException($"Unexpected compact-diff request {message.RequestUri}");
            var response = AdoReportTestHttp.Json(payload);
            responseBytes += response.Content.Headers.ContentLength
                ?? throw new InvalidOperationException("The fixture must advertise its exact response size.");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var request = Request("gitCommits", new ReportSource("s", "org", "Project", "repo", "main"));
        var snapshot = await Limited(client, 16 * 1024, 64 * 1024).CollectAsync(request, null, CancellationToken.None);
        var commit = snapshot.Commits.Single();
        That(commit.Files.Length == fileCount && diffCalls == fileCount
            && commit.Files.All(file => file.Added == 8 && file.Deleted == 9)
            && commit.Added == fileCount * 8 && commit.Deleted == fileCount * 9,
            "Every large file must retain exact add/delete/edit counts while ignoring unchanged ranges.");
        That(responseBytes < 64 * 1024,
            "All metadata and line counts for 64 hundred-thousand-line files must fit in a 64 KiB collection.");
        var exactBytes = responseBytes;
        await FailsAsync(() => Limited(client, 16 * 1024, exactBytes - 1).CollectAsync(request, null, CancellationToken.None),
            "cumulative collection budget", "no partial report");
    }

    private static async Task OverlappingCancelledCollectionsAsync()
    {
        var oldPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAdvance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAdvanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var oldStream = EmptyWorkResponse(128, beforeRead: async (read, _) =>
        {
            if (read == 3)
            {
                oldPaused.TrySetResult();
                await oldAdvance.Task;
            }
            if (read == 4)
            {
                oldAdvanced.TrySetResult();
                await releaseOld.Task;
            }
        });
        using var replacementStream = EmptyWorkResponse(128, beforeRead: async (read, _) =>
        {
            if (read == 2)
            {
                replacementPaused.TrySetResult();
                await releaseReplacement.Task;
            }
        });
        using var handler = new AdoReportTestHttp((message, _) => Task.FromResult(StreamResponse(
            message.RequestUri!.AbsolutePath.StartsWith("/old-org/", StringComparison.Ordinal)
                ? oldStream : replacementStream)));
        using var client = new HttpClient(handler);
        var collector = Limited(client, 128, 128);
        var oldTask = collector.CollectAsync(
            WorkRequest(new ReportSource("old", "old-org", "Project", "Area")), null, cancellation.Token);
        Task<ReportSnapshot>? replacementTask = null;
        var oldCancelled = false;
        try
        {
            await oldPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            replacementTask = collector.CollectAsync(
                WorkRequest(new ReportSource("new", "new-org", "Project", "Area")), null, CancellationToken.None);
            await replacementPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The old run consumes another chunk after the replacement has started its own budget.
            oldAdvance.TrySetResult();
            await oldAdvanced.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            That(!oldTask.IsCompleted, "The cancelled run must remain pending while its replacement completes.");
            releaseReplacement.TrySetResult();
            var snapshot = await replacementTask.WaitAsync(TimeSpan.FromSeconds(10));
            That(snapshot.WorkItems.IsEmpty && replacementStream.BytesRead == 128,
                "A replacement must retain its complete independent budget during overlap and old-run cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            oldAdvance.TrySetResult();
            releaseOld.TrySetResult();
            releaseReplacement.TrySetResult();
            try
            {
                await oldTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                oldCancelled = true;
            }
            if (replacementTask is not null && !replacementTask.IsCompleted)
                await replacementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        That(oldCancelled && oldStream.BytesRead == 111 && oldStream.WasDisposed && replacementStream.WasDisposed,
            "The old run must cancel and dispose independently without consuming or resetting its replacement's budget.");
    }

    private static async Task ExactLimitAsync()
    {
        using var stream = EmptyWorkResponse(128);
        using var handler = new AdoReportTestHttp((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler);
        var snapshot = await Limited(client, 128, 128).CollectAsync(
            WorkRequest(new ReportSource("s", "org", "Project", "Area")), null, CancellationToken.None);
        That(snapshot.WorkItems.IsEmpty && stream.BytesRead == 128 && stream.WasDisposed,
            "A complete response exactly at both limits must succeed without truncation or an off-by-one rejection.");
    }

    private static async Task StreamingCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = EmptyWorkResponse(10_000_000, read =>
        {
            if (read == 2) cancellation.Cancel();
        });
        using var handler = new AdoReportTestHttp((_, _) => Task.FromResult(StreamResponse(stream, chunked: true)));
        using var client = new HttpClient(handler);
        try
        {
            await Limited(client, 1024, 4096).CollectAsync(
                WorkRequest(new ReportSource("s", "org", "Project", "Area")), null, cancellation.Token);
            throw new InvalidOperationException("Streaming cancellation was ignored.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        That(stream.ReadCalls == 2 && stream.BytesRead <= 74 && stream.WasDisposed,
            "Cancellation while reading must abort promptly and dispose the response stream.");
    }

    private sealed class GeneratedResponseStream : Stream
    {
        private readonly byte[] prefix;
        private readonly byte[] suffix;
        private readonly long repeatedBytes;
        private readonly byte repeatedValue;
        private readonly Action<int>? onRead;
        private readonly Func<int, CancellationToken, Task>? beforeRead;

        public GeneratedResponseStream(string prefix, long repeatedBytes, byte repeatedValue, string suffix,
            Action<int>? onRead = null, Func<int, CancellationToken, Task>? beforeRead = null)
        {
            this.prefix = Encoding.UTF8.GetBytes(prefix);
            this.suffix = Encoding.UTF8.GetBytes(suffix);
            this.repeatedBytes = repeatedBytes;
            this.repeatedValue = repeatedValue;
            this.onRead = onRead;
            this.beforeRead = beforeRead;
        }

        public long BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeRead is not null)
                await beforeRead(ReadCalls + 1, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(buffer.Span);
            cancellationToken.ThrowIfCancellationRequested();
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var remaining = prefix.Length + repeatedBytes + suffix.Length - BytesRead;
            var read = (int)Math.Min(Math.Min(remaining, buffer.Length), 37);
            for (var index = 0; index < read; index++)
            {
                var position = BytesRead + index;
                buffer[index] = position < prefix.Length ? prefix[(int)position]
                    : position < prefix.Length + repeatedBytes ? repeatedValue
                    : suffix[(int)(position - prefix.Length - repeatedBytes)];
            }
            BytesRead += read;
            onRead?.Invoke(ReadCalls);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
