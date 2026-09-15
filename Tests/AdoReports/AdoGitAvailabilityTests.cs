using System.Net;
using System.IO.Compression;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.AdoReports.AdoReportAssert;
using static ProductivityInsights.ReportTests.AdoReports.AdoWorkItemCollectorTests;

namespace ProductivityInsights.ReportTests.AdoReports;

public static class AdoGitAvailabilityTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Parent = "2222222222222222222222222222222222222222";

    public static async Task RunAsync()
    {
        foreach (var view in new[] { "details", "teams", "trends" })
        {
            foreach (var failure in new[]
            {
                "repository", "branch", "commits", "later-page", "detail", "changes", "items", "diff", "prs",
                "transport", "timeout", "permission", "server", "read-failure", "retries-exhausted"
            })
                await AvailableSourcesSurviveAsync(view, failure);
            await AllUnavailableAsync(view);
        }
        await CancellationIsNotSkippedAsync();
        await CachedCommitSurvivesFailedMembershipAsync();
    }

    private static AdoReportTestHttp Handler(string failure, CancellationTokenSource? cancellation = null) =>
        new((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            var parts = path.Split('/');
            var repository = parts[Array.IndexOf(parts, "repositories") + 1];
            var missing = repository.StartsWith("missing", StringComparison.Ordinal);
            if (missing)
            {
                if (failure == "transport")
                    throw new HttpRequestException("private transport detail");
                if (failure == "timeout")
                    throw new TaskCanceledException("private timeout detail");
                if (failure == "read-failure")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FailingResponseStream())
                    });
                if (failure == "retries-exhausted")
                {
                    var retry = AdoReportTestHttp.Json(new { }, HttpStatusCode.ServiceUnavailable);
                    retry.Headers.RetryAfter = new(TimeSpan.Zero);
                    return Task.FromResult(retry);
                }
                var fail = failure switch
                {
                    "repository" or "permission" or "server" or "cancel" => repository == "missing",
                    "branch" => path.EndsWith("/stats/branches", StringComparison.Ordinal),
                    "commits" => path.EndsWith("/commits", StringComparison.Ordinal),
                    "later-page" => path.EndsWith("/commits", StringComparison.Ordinal)
                        && AdoReportTestHttp.Query(message, "$skip") == "1",
                    "detail" => path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal),
                    "changes" => path.EndsWith("/changes", StringComparison.Ordinal),
                    "items" => path.EndsWith("/items", StringComparison.Ordinal),
                    "diff" => path.EndsWith("/fileDiffs", StringComparison.Ordinal),
                    "prs" => path.EndsWith("/pullrequests", StringComparison.Ordinal),
                    _ => false
                };
                if (fail)
                {
                    cancellation?.Cancel();
                    return Task.FromResult(AdoReportTestHttp.Json(new { },
                        failure == "permission" ? HttpStatusCode.Forbidden
                        : failure == "server" ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound));
                }
            }
            object payload;
            if (path.EndsWith("/repositories/" + repository, StringComparison.Ordinal))
                payload = new { id = repository + "-id" };
            else if (path.EndsWith("/stats/branches", StringComparison.Ordinal))
                payload = new { name = "main", commit = new { commitId = Sha } };
            else if (path.EndsWith("/commits", StringComparison.Ordinal))
                payload = new { value = AdoReportTestHttp.Query(message, "$skip") == "0" ? new[] { new { commitId = Sha } } : [] };
            else if (path.EndsWith($"/commits/{Sha}", StringComparison.Ordinal))
                payload = new
                {
                    commitId = Sha, comment = "Complete included commit", parents = new[] { Parent },
                    author = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" },
                    committer = new { name = "Member", email = "member@example.com", date = "2020-01-10T00:00:00Z" }
                };
            else if (path.EndsWith("/changes", StringComparison.Ordinal))
                payload = new { changes = AdoReportTestHttp.Query(message, "skip") == "0"
                    ? new[] { new { item = new { path = "/edit.cs", gitObjectType = "blob" }, changeType = "edit" } } : [] };
            else if (path.EndsWith("/items", StringComparison.Ordinal))
                payload = new { path = "/edit.cs", contentMetadata = new { isBinary = false } };
            else if (path.EndsWith("/fileDiffs", StringComparison.Ordinal))
                payload = new { count = 1, value = new[] { new
                {
                    path = "/edit.cs", originalPath = "/edit.cs",
                    lineDiffBlocks = new[] { new { changeType = "edit", originalLinesCount = 1, modifiedLinesCount = 2 } }
                } } };
            else if (path.EndsWith("/pullrequests", StringComparison.Ordinal))
                payload = new { value = Array.Empty<object>() };
            else
                throw new InvalidOperationException($"Unexpected availability request {message.RequestUri}");
            return Task.FromResult(AdoReportTestHttp.Json(payload));
        });

    private static ReportRequest GitRequest(string view, params ReportSource[] sources) =>
        Request("gitCommits", sources) with { View = view, Roster = [new("Member", "member@example.com"), new("Zero", "zero@example.com")] };

    private static async Task AvailableSourcesSurviveAsync(string view, string failure)
    {
        using var handler = Handler(failure);
        using var client = new HttpClient(handler);
        var request = GitRequest(view,
            new("first", "org", "Project", "first", "main"),
            new("missing-source", "org", "Project", "missing", "main"),
            new("last", "other-org", "Project", "last", "main"));
        var messages = new List<string>();
        var snapshot = await Collector(client).CollectAsync(request, new SynchronousProgress(messages), CancellationToken.None);
        That(snapshot.Request == request && snapshot.SkippedSources.Length == 1 && !snapshot.AllSourcesSkipped,
            $"{failure}: original selections and roster must survive a skipped source.");
        var skipped = snapshot.SkippedSources.Single();
        That(skipped.SourceKey == "missing-source" && skipped.Reason.Contains("Git org/Project/missing", StringComparison.Ordinal)
            && !skipped.Reason.Contains("private ", StringComparison.Ordinal),
            "Skipped failures must identify the source without exposing transport internals.");
        That(messages.Any(message => message.StartsWith("Skipped ", StringComparison.Ordinal)),
            "Skipped sources must be reported as export progress, not silently ignored.");
        That(snapshot.Commits.Length == 2 && snapshot.Commits.All(commit => commit.SourceKey != "missing-source")
            && snapshot.Commits.Sum(commit => commit.Added) == 4 && snapshot.Commits.Sum(commit => commit.Deleted) == 2,
            $"{failure}: failed sources must not leak partial commits; earlier and later sources retain exact counts.");
        var document = ReportDocumentBuilder.Build(snapshot);
        That(document.SourceRows.All(row => row.SourceKey != "missing-source")
            && document.OverallRows.Sum(row => row.Commits) == 2
            && document.OverallRows.All(row => !row.SourceKeys.Contains("missing-source")),
            "Missing repositories must not create zero-activity Teams rows or counted source memberships.");
        var archive = ReportArchiveBuilder.Build(snapshot);
        using (archive.Content)
        using (var zip = new ZipArchive(archive.Content, ZipArchiveMode.Read))
        {
            That(zip.Entries.Count == 2, "Best-effort export must still contain exactly matching Markdown and HTML.");
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var content = await reader.ReadToEndAsync();
                That(content.Contains("Skipped sources - excluded from totals", StringComparison.Ordinal)
                    && content.Contains("org/Project/missing", StringComparison.Ordinal)
                    && content.Contains("unavailable, not zero", StringComparison.Ordinal),
                    "Both formats must disclose the unavailable source and limited coverage.");
            }
        }
        if (failure == "repository")
            That(handler.Requests.Count(url => url.Contains("/repositories/missing", StringComparison.Ordinal)) == 1,
                "A missing repository must be skipped immediately without retrying or requesting its details.");
        if (failure == "retries-exhausted")
            That(handler.Requests.Count(url => url.Contains("/repositories/missing", StringComparison.Ordinal)) == 4,
                "Transient failures must exhaust bounded retries before skipping a source.");
    }

    private static async Task AllUnavailableAsync(string view)
    {
        using var handler = Handler("repository");
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(
            GitRequest(view, new ReportSource("missing-source", "org", "Project", "missing", "main")), null, CancellationToken.None);
        var document = ReportDocumentBuilder.Build(snapshot);
        That(snapshot.AllSourcesSkipped && document.SourceRows.IsEmpty && document.OverallRows.IsEmpty,
            "All unavailable sources must not fabricate zero-activity contributor rows.");
        var markdown = MarkdownReportWriter.Write(document);
        var html = HtmlSummaryReportWriter.Write(document, "report.md");
        That(markdown.Contains("totals are unavailable, not zero", StringComparison.Ordinal)
            && html.Contains("Activity is unavailable, not zero", StringComparison.Ordinal)
            && !markdown.Contains("**Commits:** 0", StringComparison.Ordinal)
            && !html.Contains("<tfoot>", StringComparison.Ordinal),
            "Unavailable-only reports must disclose unknown activity, not numeric zero totals or empty trend totals.");
    }

    private static async Task CancellationIsNotSkippedAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = Handler("cancel", cancellation);
        using var client = new HttpClient(handler);
        try
        {
            await Collector(client).CollectAsync(GitRequest("details",
                new ReportSource("missing-source", "org", "Project", "missing", "main")), null, cancellation.Token);
            throw new InvalidOperationException("Cancellation must not become a successful skipped-source snapshot.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private static async Task CachedCommitSurvivesFailedMembershipAsync()
    {
        var calls = 0;
        using var successful = Handler("none");
        using var invoker = new HttpMessageInvoker(successful);
        using var handler = new AdoReportTestHttp((message, token) =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal) && calls++ == 0)
                return Task.FromResult(AdoReportTestHttp.Json(new { }, HttpStatusCode.Forbidden));
            return invoker.SendAsync(message, token);
        });
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(GitRequest("teams",
            new("failed-membership", "org", "Project", "same", "main"),
            new("included-membership", "org", "Project", "same", "main")), null, CancellationToken.None);
        That(snapshot.SkippedSources.Single().SourceKey == "failed-membership"
            && snapshot.Commits.Single().SourceKey == "included-membership",
            "A cached complete commit must be reusable without reviving its failed source membership.");
        That(successful.Requests.Count(url => url.Contains("/fileDiffs?", StringComparison.Ordinal)) == 1,
            "Best-effort source isolation must preserve complete-commit caching.");
        That(ReportDocumentBuilder.Build(snapshot).SourceRows.All(row => row.SourceKey == "included-membership"),
            "Failed source membership must not appear in export activity rows.");
    }

    private sealed class SynchronousProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }

    private sealed class FailingResponseStream() : MemoryStream([0])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private stream detail"));
    }
}
