using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Models.FeatureRegistry;

public static class FeatureRegistrySource
{
    public const string Analysis = "featureRegistry";
    public const string Repository = "FeatureRegistry";

    public static bool IsRepository(string organization, string project, string repository) =>
        string.Equals(organization, "msdata", StringComparison.OrdinalIgnoreCase)
        && string.Equals(project, "A365", StringComparison.OrdinalIgnoreCase)
        && string.Equals(repository, Repository, StringComparison.OrdinalIgnoreCase);

    public static void Validate(ReportRequest request)
    {
        if (request.Analysis != Analysis || request.Sources.IsDefaultOrEmpty || request.Sources.Length != 1
            || request.Sources[0].Branch is not null
            || !IsRepository(request.Sources[0].Organization, request.Sources[0].Project, request.Sources[0].Scope))
            throw new InvalidOperationException("Feature Registry requires only msdata/A365/FeatureRegistry, across all branches.");
        if (request.View is not ("details" or "trends" or "teams") || request.Start > request.End)
            throw new InvalidOperationException("Select a supported view and a start time not later than the end time.");
        if (request.IsTeams && request.Roster.IsDefaultOrEmpty)
            throw new InvalidOperationException("Fetch the complete manager roster before running Feature Registry.");
    }
}

public sealed record FeatureRegistryPullRequest(
    int Id, string RepositoryId, string Title, string Status, bool IsDraft,
    ReportIdentity Author, string SourceBranch, string TargetBranch, DateTimeOffset CreatedAt,
    ReportIdentity? CompletedBy, DateTimeOffset? CompletedAt, string Url)
{
    public FeatureRegistryFileChanges? FileChanges { get; init; }
    public string EntityKey => $"MSDATA:{RepositoryId.ToUpperInvariant()}:{Id}";
    public ReportIdentity Attribution => Status == "active" ? Author : CompletedBy ?? new("Unavailable");
}

public sealed record FeatureRegistryFileChange(string Path, string ChangeType, string? OriginalPath = null);

public sealed record FeatureRegistryFileChanges(int IterationId, ImmutableArray<FeatureRegistryFileChange> Files);

public sealed class FeatureRegistrySnapshot
{
    internal FeatureRegistrySnapshot(ReportRequest request, DateTimeOffset started, DateTimeOffset finished,
        ImmutableArray<FeatureRegistryPullRequest> open, ImmutableArray<FeatureRegistryPullRequest> completed,
        ImmutableArray<FeatureRegistryPullRequest> unattributed)
    {
        Request = request;
        StartedAt = started;
        FinishedAt = finished;
        Open = open;
        Completed = completed;
        Unattributed = unattributed;
    }

    public ReportRequest Request { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset FinishedAt { get; }
    public ImmutableArray<FeatureRegistryPullRequest> Open { get; }
    public ImmutableArray<FeatureRegistryPullRequest> Completed { get; }
    public ImmutableArray<FeatureRegistryPullRequest> Unattributed { get; }
}
