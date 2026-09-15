using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal static class ReportTables
{
    public static string[] Headers(string analysis, bool bySource) => (analysis, bySource) switch
    {
        ("workItems", true) => ["Team Member", "Area Path", "Total Items", "Bugs", "Tasks", "Product Backlog Items", "Other"],
        ("workItems", false) => ["Team Member", "Total Items", "Bugs", "Tasks", "Product Backlog Items", "Other", "Teams"],
        ("gitCommits", true) => ["Team Member", "Repository", "Commits", "PRs Merged", "Lines +", "Lines -"],
        ("gitCommits", false) => ["Team Member", "Commits", "PRs Merged", "Lines +", "Lines -", "Repositories"],
        ("incidentManagement", true) => ["Team Member", "ICM Path", "Active", "Created", "Mitigated", "Resolved", "Contributed"],
        ("incidentManagement", false) => ["Team Member", "Active", "Created", "Mitigated", "Resolved", "Contributed"],
        _ => throw new InvalidOperationException("Unsupported report summary analysis.")
    };

    public static string[] Cells(ReportSummaryRow row, ReportRequest request, CancellationToken token = default)
    {
        var cells = new List<string> { row.Contributor };
        if (row.SourceKey is not null)
            cells.Add(request.Sources.First(source => source.Key == row.SourceKey).Label);
        if (request.Analysis == "workItems")
        {
            cells.AddRange(new[] { row.WorkItems, row.Bugs, row.Tasks, row.ProductBacklogItems, row.Other }.Select(value => ReportFormatting.Number(value)));
            if (row.SourceKey is null)
            {
                cells.Add(ReportFormatting.Join("; ", SelectedSources(row, request)
                    .Where(source => !string.IsNullOrWhiteSpace(source.Team))
                    .Select(source => $"{source.Organization}/{source.Project}/{source.Team}")
                    .Distinct(StringComparer.OrdinalIgnoreCase), token));
            }
        }
        else if (request.Analysis == "gitCommits")
        {
            cells.AddRange(new[] { (long)row.Commits, row.PullRequests, row.Added, row.Deleted }.Select(ReportFormatting.Number));
            if (row.SourceKey is null)
                cells.Add(ReportFormatting.Join("; ", SelectedSources(row, request)
                    .Select(source => $"{source.Organization}/{source.Project}/{source.Scope}")
                    .Distinct(StringComparer.OrdinalIgnoreCase), token));
        }
        else
            cells.AddRange(new[] { row.Active, row.Created, row.Mitigated, row.Resolved, row.Contributed }
                .Select(value => ReportFormatting.Number(value)));
        return cells.ToArray();
    }

    public static readonly string[] TrendHeaders =
    [
        "Period", "Commits", "Service Lines Added", "Service Lines Deleted", "Test Lines Added",
        "Test Lines Deleted", "Total Lines Added", "Total Lines Deleted", "Service/Test Ratio"
    ];

    public static string[] TrendCells(GitTrendPeriod period) =>
    [
        period.Period, ReportFormatting.Number(period.Commits), ReportFormatting.Number(period.ServiceAdded),
        ReportFormatting.Number(period.ServiceDeleted), ReportFormatting.Number(period.TestAdded),
        ReportFormatting.Number(period.TestDeleted), ReportFormatting.Number(period.Added),
        ReportFormatting.Number(period.Deleted),
        ReportFormatting.Ratio(period.ServiceAdded + period.ServiceDeleted, period.TestAdded + period.TestDeleted)
    ];

    public static GitTrendPeriod TrendTotal(ImmutableArray<GitTrendPeriod> periods, CancellationToken token)
    {
        var total = new GitTrendPeriod("Total", 0, 0, 0, 0, 0, 0, 0);
        foreach (var period in periods)
        {
            token.ThrowIfCancellationRequested();
            total = total with
            {
                Commits = total.Commits + period.Commits, ServiceAdded = total.ServiceAdded + period.ServiceAdded,
                ServiceDeleted = total.ServiceDeleted + period.ServiceDeleted, TestAdded = total.TestAdded + period.TestAdded,
                TestDeleted = total.TestDeleted + period.TestDeleted, Added = total.Added + period.Added, Deleted = total.Deleted + period.Deleted,
                FilesAdded = total.FilesAdded + period.FilesAdded, FilesModified = total.FilesModified + period.FilesModified,
                FilesDeleted = total.FilesDeleted + period.FilesDeleted
            };
        }
        return total;
    }

    private static IEnumerable<ReportSource> SelectedSources(ReportSummaryRow row, ReportRequest request) =>
        row.SourceKeys.Select(key => request.Sources.First(source => source.Key == key));
}
