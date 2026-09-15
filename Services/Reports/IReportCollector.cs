using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public interface IReportCollector
{
    Task<ReportSnapshot> CollectAsync(
        ReportRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}
