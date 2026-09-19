using System.Collections.Immutable;

namespace ProductivityInsights.Models.Reports;

public sealed record ReportIdentity(string Name, string? Email = null, string? Id = null, string? PrincipalName = null)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : !string.IsNullOrWhiteSpace(PrincipalName) ? PrincipalName
        : !string.IsNullOrWhiteSpace(Id) ? Id
        : "Unknown";
}

public sealed record ReportSource(
    string Key, string Organization, string Project, string Scope, string? Branch = null, string? Team = null)
{
    public string Label => string.IsNullOrEmpty(Organization)
        ? Scope
        : $"{Organization}/{Project}/{Scope}" + (string.IsNullOrEmpty(Branch) ? "" : $" ({Branch})");
}

public sealed record ReportRequest
{
    public required Guid RunId { get; init; }
    public required string Analysis { get; init; }
    public required string View { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public DateTime? RequestedStart { get; init; }
    public DateTime? RequestedEnd { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required string TimeZone { get; init; }
    public string? EffectiveStartLiteral { get; init; }
    public string? EffectiveEndLiteral { get; init; }
    public string? EffectiveBoundsOrigin { get; init; }
    public required ImmutableArray<ReportSource> Sources { get; init; }
    public ImmutableArray<ReportIdentity> Roster { get; init; } = [];
    public ImmutableArray<int?> RosterParentIndices { get; init; } = [];
    public string? Manager { get; init; }
    public string TrendPeriod { get; init; } = "daily";
    public bool IsTeams => View == "teams";
}

public sealed record WorkItemStateRecord(string State, DateTimeOffset EnteredAt, ReportIdentity Actor);

public sealed record WorkItemReportRecord
{
    public required string SourceKey { get; init; }
    public required string Organization { get; init; }
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }
    public required string State { get; init; }
    public required ReportIdentity Assignee { get; init; }
    public int? Priority { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ChangedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string AreaPath { get; init; } = "";
    public string IterationPath { get; init; } = "";
    public string Url { get; init; } = "";
    public ImmutableArray<WorkItemStateRecord> History { get; init; } = [];
    public string EntityKey => $"{Organization.ToUpperInvariant()}:{Id}";
}

public sealed record GitFileReportRecord(string Path, string ChangeType, int Added, int Deleted);

public sealed record GitReportRecord
{
    public required string SourceKey { get; init; }
    public required string Organization { get; init; }
    public required string RepositoryId { get; init; }
    public required string Id { get; init; }
    public required ReportIdentity Author { get; init; }
    public required ReportIdentity Committer { get; init; }
    public required DateTimeOffset AuthorDate { get; init; }
    public required DateTimeOffset CommitterDate { get; init; }
    public string Message { get; init; } = "";
    public string Url { get; init; } = "";
    public int ParentCount { get; init; }
    public required ImmutableArray<GitFileReportRecord> Files { get; init; }
    public int Added => Files.Sum(file => file.Added);
    public int Deleted => Files.Sum(file => file.Deleted);
    public string EntityKey => $"{Organization.ToUpperInvariant()}:{RepositoryId.ToUpperInvariant()}:{Id.ToUpperInvariant()}";
}

public sealed record PullRequestReportRecord(
    string SourceKey, string Organization, string RepositoryId, int Id, string Title,
    string TargetBranch, ReportIdentity CompletedBy, DateTimeOffset CompletedAt, string Url)
{
    public string EntityKey => $"{Organization.ToUpperInvariant()}:{RepositoryId.ToUpperInvariant()}:{Id}";
}

public sealed record IncidentReportRecord
{
    public required string SourceKey { get; init; }
    public required string Id { get; init; }
    public required string SiloId { get; init; }
    public string Title { get; init; } = "";
    public int? Severity { get; init; }
    public string Status { get; init; } = "";
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ModifiedAt { get; init; }
    public DateTimeOffset? ImpactStart { get; init; }
    public DateTimeOffset? MitigatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public ReportIdentity Owner { get; init; } = new("Unknown");
    public ReportIdentity MitigatedBy { get; init; } = new("Unknown");
    public ReportIdentity ResolvedBy { get; init; } = new("Unknown");
    public string OwningTenant { get; init; } = "";
    public string OwningTeam { get; init; } = "";
    public string Mitigation { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Active { get; init; }
    public bool Created { get; init; }
    public bool Mitigated { get; init; }
    public bool Resolved { get; init; }
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string EntityKey => $"{SiloId}:{Id}";
}

public sealed record IncidentActionReportRecord(
    string SourceKey, string IncidentId, string SiloId, string ActionId,
    ReportIdentity Actor, DateTimeOffset Timestamp, string ActionType)
{
    public string IncidentKey => $"{SiloId}:{IncidentId}";
}

public sealed record ReportSkippedSource(string SourceKey, string Reason);

public sealed record ReportSnapshot
{
    public required ReportRequest Request { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public ImmutableArray<WorkItemReportRecord> WorkItems { get; init; } = [];
    public ImmutableArray<GitReportRecord> Commits { get; init; } = [];
    public ImmutableArray<PullRequestReportRecord> PullRequests { get; init; } = [];
    public ImmutableArray<IncidentReportRecord> Incidents { get; init; } = [];
    public ImmutableArray<IncidentActionReportRecord> IncidentActions { get; init; } = [];
    public ImmutableArray<string> CoverageNotes { get; init; } = [];
    public ImmutableArray<ReportSkippedSource> SkippedSources { get; init; } = [];
    public bool AllSourcesSkipped => !SkippedSources.IsEmpty && SkippedSources.Length == Request.Sources.Length;
}
