using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using ProductivityInsights.Utilities;

namespace ProductivityInsights.ReportTests;

internal static class LiveReportProbe
{
    internal static async Task RunAsync(string[] args)
    {
        string Required(string option)
        {
            var index = Array.IndexOf(args, option);
            if (index < 0 || index + 1 == args.Length)
                throw new ArgumentException($"Required live probe argument: {option}");
            return args[index + 1];
        }
        var analysis = Required("--analysis");
        if (analysis is not ("gitCommits" or "workItems"))
            throw new ArgumentException("This read-only probe supports gitCommits or workItems.");
        var organization = Required("--organization");
        var project = Required("--project");
        var scope = Required("--scope");
        var branch = analysis == "gitCommits" ? Required("--branch") : null;
        var additionalScope = args.Contains("--additional-scope") ? Required("--additional-scope") : null;
        if (additionalScope is not null && analysis != "gitCommits")
            throw new ArgumentException("--additional-scope is supported only for Git availability probes.");
        var additionalBranch = additionalScope is not null ? Required("--additional-branch") : null;
        var commitId = args.Contains("--commit") ? Required("--commit") : null;
        if (commitId is not null && (analysis != "gitCommits" || commitId.Length != 40 || !commitId.All(Uri.IsHexDigit)))
            throw new ArgumentException("--commit requires a full 40-character Git commit SHA.");
        if (commitId is not null && args.Contains("--latest"))
            throw new ArgumentException("Choose --commit or --latest, not both.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        DateTimeOffset start;
        DateTimeOffset end;
        if (args.Contains("--latest") || commitId is not null)
        {
            if (analysis != "gitCommits")
                throw new ArgumentException("--latest is only supported for Git probes.");
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}"
                + $"/_apis/git/repositories/{Uri.EscapeDataString(scope)}/commits?api-version=7.1"
                + $"&searchCriteria.itemVersion.versionType=branch&searchCriteria.itemVersion.version={Uri.EscapeDataString(branch!)}"
                + "&searchCriteria.$top=1";
            if (commitId is not null)
                url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}"
                    + $"/_apis/git/repositories/{Uri.EscapeDataString(scope)}/commits/{commitId}?api-version=7.0";
            using var query = new HttpRequestMessage(HttpMethod.Get, url);
            query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await UserToken.GetToken().WaitAsync(timeout.Token)).Token);
            using var response = await client.SendAsync(query, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
            var commit = commitId is null ? json.RootElement.GetProperty("value")[0] : json.RootElement;
            var timestamp = commit.GetProperty("committer").GetProperty("date").GetDateTimeOffset();
            start = timestamp.AddMinutes(-1);
            end = timestamp.AddMinutes(1);
        }
        else
        {
            start = DateTimeOffset.Parse(Required("--start"), CultureInfo.InvariantCulture);
            end = DateTimeOffset.Parse(Required("--end"), CultureInfo.InvariantCulture);
        }
        if (end < start || end - start > TimeSpan.FromDays(7))
            throw new ArgumentException("Live probe requires a nonnegative window of no more than seven days.");
        var request = new ReportRequest
        {
            RunId = Guid.NewGuid(), Analysis = analysis, View = "details",
            ExecutedAt = DateTimeOffset.UtcNow, RequestedStart = start.UtcDateTime, RequestedEnd = end.UtcDateTime,
            Start = start, End = end, TimeZone = "UTC",
            Sources = additionalScope is null
                ? [new("live-probe", organization, project, scope, branch)]
                : [new("live-probe", organization, project, scope, branch),
                    new("additional-probe", organization, project, additionalScope, additionalBranch)]
        };
        var collector = new AdoReportCollector(client, async token => (await UserToken.GetToken().WaitAsync(token)).Token);
        var snapshot = await collector.CollectAsync(request, null, timeout.Token);
        if (args.Contains("--latest") && snapshot.Commits.IsEmpty)
            throw new InvalidOperationException("The latest-commit probe unexpectedly returned no commits.");
        if (commitId is not null && !snapshot.Commits.Any(commit => string.Equals(commit.Id, commitId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The requested commit was not collected from the selected branch/window.");
        var archive = ReportArchiveBuilder.Build(snapshot, timeout.Token);
        using (archive.Content)
        using (var zip = new ZipArchive(archive.Content, ZipArchiveMode.Read))
        {
            if (zip.Entries.Count != 2)
                throw new InvalidOperationException("Live export did not produce exactly two files.");
            Console.WriteLine($"PASS live {analysis}: sources={snapshot.Request.Sources.Length}, skipped={snapshot.SkippedSources.Length}, workItems={snapshot.WorkItems.Length}, commits={snapshot.Commits.Length}, PRs={snapshot.PullRequests.Length}, files={snapshot.Commits.Sum(commit => commit.Files.Length)}, ZIP bytes={archive.Content.Length}.");
            foreach (var source in snapshot.SkippedSources)
                Console.WriteLine($"Skipped {source.SourceKey}: {source.Reason}");
        }
    }
}
