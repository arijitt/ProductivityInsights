using System.Collections.Immutable;
using ProductivityInsights.Metrics;
using ProductivityInsights.Models;
using ProductivityInsights.Models.CommitChanges;
using ProductivityInsights.Models.WorkItems;
using ProductivityInsights.Models.WorkItems.HistoryCollection;

namespace ProductivityInsights.Services.Analysis;

public sealed record AnalysisSource(string Organization, string Project, string Scope, string? Branch = null, string? Team = null);

public sealed record SelectionAnalysisRequest(string Analysis, string View, string TrendPeriod,
    DateTime Start, DateTime End, ImmutableArray<AnalysisSource> Sources);

public sealed record SelectionAnalysisResult(
    SelectionAnalysisRequest Request,
    Dictionary<WorkItemValue, AnalysisSource> WorkSources,
    Dictionary<CommitValue, AnalysisSource> CommitSources,
    GitCommitCollection? TrendCommits)
{
    public WorkItemDetailsCollection WorkItems => new() { count = WorkSources.Count, value = WorkSources.Keys.ToList() };
    public CommitCollection Commits => new() { count = CommitSources.Count, value = CommitSources.Keys.ToList() };
}

public interface IAnalysisDataClient
{
    Task<WorkItemDetailsCollection?> WorkItemsAsync(AnalysisSource source, DateTime start, DateTime end);
    Task<CommitCollection?> CommitsAsync(AnalysisSource source, DateTime start, DateTime end);
    Task<Dictionary<int, WorkItemHistoryCollection>> HistoryAsync(string organization, WorkItemDetailsCollection items);
    Task<GitCommitCollection?> CommitDetailsAsync(AnalysisSource source, CommitCollection commits);
}

public sealed class AnalysisDataClient : IAnalysisDataClient
{
    public async Task<WorkItemDetailsCollection?> WorkItemsAsync(AnalysisSource source, DateTime start, DateTime end) =>
        await WorkItemCompletionMetrics.QueryWorkItemDetailsAsync(source.Organization, source.Project, source.Team!,
            source.Scope, "Done", start, end);

    public Task<CommitCollection?> CommitsAsync(AnalysisSource source, DateTime start, DateTime end) =>
        GitCommitMetrics.GetCommitsAsync(source.Organization, source.Project, source.Scope, source.Branch!, start, end);

    public Task<Dictionary<int, WorkItemHistoryCollection>> HistoryAsync(string organization, WorkItemDetailsCollection items) =>
        WorkItemCompletionMetrics.GetWorkItemHistoryCollectionAsync(organization, items);

    public Task<GitCommitCollection?> CommitDetailsAsync(AnalysisSource source, CommitCollection commits) =>
        GitCommitMetrics.GetCommitLineDetailsAsync(source.Organization, source.Project, source.Scope, source.Branch!, commits);
}

public sealed class SelectionAnalysisService(IAnalysisDataClient client)
{
    public async Task<SelectionAnalysisResult> CollectAsync(SelectionAnalysisRequest request)
    {
        if (request.Sources.IsEmpty) throw new InvalidOperationException("Select at least one source before running analysis.");
        var work = new Dictionary<WorkItemValue, AnalysisSource>();
        var commits = new Dictionary<CommitValue, AnalysisSource>();
        var workKeys = new HashSet<(string Organization, int Id)>();
        var commitKeys = new HashSet<(string Organization, string Project, string Repository, string Id)>();
        foreach (var source in request.Sources)
        {
            if (request.Analysis == "workItems")
            {
                var collection = await client.WorkItemsAsync(source, request.Start, request.End);
                if (collection?.value is null) throw Unavailable(source);
                foreach (var item in collection.value)
                {
                    if (item.id <= 0) throw new InvalidOperationException($"Missing work item ID in {source.Scope}.");
                    if (workKeys.Add((source.Organization.ToUpperInvariant(), item.id))) work.Add(item, source);
                }
            }
            else if (request.Analysis == "gitCommits")
            {
                var collection = await client.CommitsAsync(source, request.Start, request.End);
                if (collection?.value is null) throw Unavailable(source);
                foreach (var commit in collection.value)
                {
                    if (string.IsNullOrWhiteSpace(commit.commitId))
                        throw new InvalidOperationException($"Missing commit ID in {source.Scope}.");
                    if (commitKeys.Add((source.Organization.ToUpperInvariant(), source.Project.ToUpperInvariant(),
                        source.Scope.ToUpperInvariant(), commit.commitId.ToUpperInvariant())))
                        commits.Add(commit, source);
                }
            }
            else throw new InvalidOperationException("Unsupported multi-source analysis.");
        }
        var result = new SelectionAnalysisResult(request, work, commits, null);
        return request is { Analysis: "gitCommits", View: "trends" }
            ? result with { TrendCommits = await CommitDetailsAsync(result, commits.Keys) } : result;
    }

    public async Task<Dictionary<WorkItemValue, WorkItemHistoryCollection>> HistoryAsync(
        SelectionAnalysisResult result, IEnumerable<WorkItemValue> items)
    {
        var histories = new Dictionary<WorkItemValue, WorkItemHistoryCollection>();
        foreach (var group in items.GroupBy(item => result.WorkSources[item].Organization, StringComparer.OrdinalIgnoreCase))
        {
            var history = await client.HistoryAsync(group.Key, new() { count = group.Count(), value = group.ToList() });
            foreach (var item in group)
            {
                if (!history.TryGetValue(item.id, out var value) || value?.value is null)
                    throw new InvalidOperationException($"Work item history is unavailable for {group.Key}/{item.id}.");
                histories.Add(item, value);
            }
        }
        return histories;
    }

    public async Task<GitCommitCollection> CommitDetailsAsync(
        SelectionAnalysisResult result, IEnumerable<CommitValue> commits)
    {
        var combined = new GitCommitCollection();
        foreach (var group in commits.GroupBy(commit => result.CommitSources[commit]))
        {
            var details = await client.CommitDetailsAsync(group.Key, new() { count = group.Count(), value = group.ToList() });
            if (details?.Value is null || details.Value.Count != group.Count()
                || !details.Value.Select(commit => commit.CommitId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(group.Select(commit => commit.commitId)))
                throw Unavailable(group.Key);
            combined.Value.AddRange(details.Value);
        }
        combined.Count = combined.Value.Count;
        return combined;
    }

    private static InvalidOperationException Unavailable(AnalysisSource source) =>
        new($"Analysis data is unavailable or incomplete for {source.Organization} / {source.Project} / {source.Scope}. Retry the analysis.");
}
