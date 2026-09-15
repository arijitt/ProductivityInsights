using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public sealed class ReportCollectionService(
    Func<IReportCollector> ado, Func<IReportCollector> incidents) : IReportCollector
{
    public Task<ReportSnapshot> CollectAsync(
        ReportRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Sources.IsDefaultOrEmpty || request.Sources.Any(source => string.IsNullOrWhiteSpace(source.Scope)))
            throw new InvalidOperationException("No valid sources were captured. Select sources and run the analysis again.");
        if (request.Start > request.End)
            throw new InvalidOperationException("The report start must not be after its end.");
        if (request.IsTeams && request.Roster.IsDefaultOrEmpty)
            throw new InvalidOperationException("The captured manager roster is unavailable. Fetch the roster and rerun the analysis.");
        if (request.View is not ("details" or "teams" or "trends")
            || request.View == "trends" && request.Analysis != "gitCommits")
            throw new InvalidOperationException("This view has no supported report data.");

        return request.Analysis switch
        {
            "workItems" or "gitCommits" => ado().CollectAsync(request, progress, cancellationToken),
            "incidentManagement" => incidents().CollectAsync(request, progress, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported report analysis.")
        };
    }
}
