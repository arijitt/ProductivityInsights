using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public sealed record ReportDurationStatistics(
    int Count, double Total, double? Minimum, double? Average, double? Maximum, double? Median);

public sealed record ReportStatePeriod(
    string State, DateTimeOffset EnteredAt, DateTimeOffset? ExitedAt, ReportIdentity Actor, double Days);

public sealed record ReportEntity<T>(T Record, ImmutableArray<string> SourceKeys);

public sealed record WorkItemDocument(
    ReportEntity<WorkItemReportRecord> Entity, string ContributorKey, string Contributor,
    ImmutableArray<ReportStatePeriod> Periods, double? ActiveWorkDays);

public sealed record GitCommitDocument(
    ReportEntity<GitReportRecord> Entity, string ContributorKey, string Contributor,
    long Added, long Deleted, long ServiceAdded, long ServiceDeleted, long TestAdded, long TestDeleted)
{
    public long FilesAdded { get; init; }
    public long FilesModified { get; init; }
    public long FilesDeleted { get; init; }
}

public sealed record IncidentDocument(
    ReportEntity<IncidentReportRecord> Entity, double? DetectHours, double? MitigateHours, double? ResolveHours);

public sealed record ReportSummaryRow
{
    public required string ContributorKey { get; init; }
    public required string Contributor { get; init; }
    public string? SourceKey { get; init; }
    public ImmutableArray<string> SourceKeys { get; init; } = [];
    public int WorkItems { get; init; }
    public int Bugs { get; init; }
    public int Tasks { get; init; }
    public int ProductBacklogItems { get; init; }
    public int Other { get; init; }
    public int Commits { get; init; }
    public int PullRequests { get; init; }
    public long Added { get; init; }
    public long Deleted { get; init; }
    public long FilesChanged { get; init; }
    public long FilesAdded { get; init; }
    public long FilesModified { get; init; }
    public long FilesDeleted { get; init; }
    public int Active { get; init; }
    public int Created { get; init; }
    public int Mitigated { get; init; }
    public int Resolved { get; init; }
    public int Contributed { get; init; }
}

public sealed record GitTrendPeriod(
    string Period, int Commits, long ServiceAdded, long ServiceDeleted,
    long TestAdded, long TestDeleted, long Added, long Deleted)
{
    public long FilesAdded { get; init; }
    public long FilesModified { get; init; }
    public long FilesDeleted { get; init; }
}

public sealed record IncidentSeverityStatistics(
    int? Severity, int Count, ReportDurationStatistics Detect,
    ReportDurationStatistics Mitigate, ReportDurationStatistics Resolve);

public sealed record IncidentTeamStatistics(string Team, IncidentSeverityStatistics Statistics);

public sealed record IncidentContributorGroup(
    string Contributor, ImmutableArray<IncidentDocument> Incidents,
    ImmutableArray<IncidentSeverityStatistics> SeverityStatistics);

public sealed record ReportDocument
{
    public required ReportSnapshot Snapshot { get; init; }
    public required string Title { get; init; }
    public required string SourceHeading { get; init; }
    public required string OverallHeading { get; init; }
    public required ImmutableArray<string> MetricNotes { get; init; }
    public required ImmutableArray<ReportSummaryRow> SourceRows { get; init; }
    public required ImmutableArray<ReportSummaryRow> OverallRows { get; init; }
    public ImmutableArray<WorkItemDocument> WorkItems { get; init; } = [];
    public ImmutableArray<GitCommitDocument> Commits { get; init; } = [];
    public ImmutableArray<ReportEntity<PullRequestReportRecord>> PullRequests { get; init; } = [];
    public ImmutableArray<IncidentDocument> Incidents { get; init; } = [];
    public ImmutableArray<ReportEntity<IncidentActionReportRecord>> IncidentActions { get; init; } = [];
    public ImmutableArray<GitTrendPeriod> GitTrends { get; init; } = [];
    public ImmutableArray<IncidentContributorGroup> IncidentGroups { get; init; } = [];
    public ImmutableArray<IncidentSeverityStatistics> IncidentStatistics { get; init; } = [];
    public ImmutableArray<IncidentTeamStatistics> ActiveTeamStatistics { get; init; } = [];
}
