using Microsoft.Extensions.DependencyInjection;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests;

public static class ExportDependencyTests
{
    public static async Task RunAsync()
    {
        var incidentInitializations = 0;
        var adoInitializations = 0;
        var services = new ServiceCollection();
        services.AddOptions<IncidentManagementKustoOptions>().Configure(options => options.QueryTimeoutSeconds = 7200);
        services.AddScoped<IncidentReportCollector>();
        services.AddScoped<IReportCollector>(provider => new ReportCollectionService(
            () => { adoInitializations++; return new EmptyCollector(); },
            () =>
            {
                incidentInitializations++;
                return provider.GetRequiredService<IncidentReportCollector>();
            }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IReportCollector>();
        CompatibilityTests.Require(adoInitializations == 0 && incidentInitializations == 0,
            "Page dependency activation initialized an export-only transport.");
        var request = ExportIntegrationTests.Request(ExportIntegrationTests.PreparedComponent());
        var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
        CompatibilityTests.Require(snapshot.Request.RunId == request.RunId && adoInitializations == 1
            && incidentInitializations == 0, "ADO export eagerly initialized ICM dependencies.");
        var failingIncidentExport = new ReportCollectionService(() => new EmptyCollector(),
            () => throw new ArgumentOutOfRangeException("QueryTimeoutSeconds", 7200, "Export transport restriction."));
        try
        {
            await failingIncidentExport.CollectAsync(request with { Analysis = "incidentManagement" }, null, CancellationToken.None);
            throw new InvalidOperationException("Expected the selected incident export transport to fail.");
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "QueryTimeoutSeconds")
        {
            CompatibilityTests.Require(incidentInitializations == 0, "Unselected ICM transport was initialized.");
        }
    }

    private sealed class EmptyCollector : IReportCollector
    {
        public Task<ReportSnapshot> CollectAsync(
            ReportRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ReportSnapshot { Request = request, AsOf = DateTimeOffset.UtcNow });
    }
}
