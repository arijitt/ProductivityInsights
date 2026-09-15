using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests;

public static class ExportConfigurationTests
{
    public static void Run()
    {
        using (var services = Build([]))
        using (var scope = services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IReportCollector>();
            ExpectCoverageFailure(() => scope.ServiceProvider.GetRequiredService<IOptions<IncidentReportCoverage>>(), "Teams");
        }

        var values = new Dictionary<string, string?>
        {
            ["IncidentReportCoverage:ClusterUri"] = "https://example.test",
            ["IncidentReportCoverage:Database"] = "Fixture",
            ["IncidentReportCoverage:SiloId"] = "1",
            ["IncidentReportCoverage:Teams:0"] = "FixtureTeam",
            ["IncidentReportCoverage:HistoryFrom"] = "2026-01-01T00:00:00Z",
            ["IncidentReportCoverage:HistoryThrough"] = "2026-12-31T23:59:59Z",
            ["IncidentReportCoverage:LatestIncidentsThrough"] = "2026-12-31T23:59:59Z",
            ["IncidentReportCoverage:PurgesThrough"] = "2026-12-31T23:59:59Z",
            ["IncidentReportCoverage:Evidence"] = "Synthetic fixture evidence only",
            ["IncidentReportCoverage:RetentionSemantics"] = "Synthetic fixture retention contract only"
        };
        foreach (var table in new[] { "Incidents", "IncidentHistory", "PurgedIncidents", "PurgedIncidentHistory" })
            values[$"IncidentReportCoverage:MinimumRetention:{table}"] = "740.00:00:00";
        using (var services = Build(values))
        using (var scope = services.CreateScope())
        {
            var coverage = scope.ServiceProvider.GetRequiredService<IOptions<IncidentReportCoverage>>().Value;
            CompatibilityTests.Require(coverage.Teams.Single() == "FixtureTeam"
                && coverage.MinimumRetention.Count == 4
                && coverage.HistoryFrom.Offset == TimeSpan.Zero, "Coverage configuration lost its typed evidence.");
        }

        values["IncidentReportCoverage:HistoryFrom"] = "2026-01-01T00:00:00";
        using (var services = Build(values))
        using (var scope = services.CreateScope())
            ExpectCoverageFailure(() => scope.ServiceProvider.GetRequiredService<IOptions<IncidentReportCoverage>>(), "explicit UTC offset");
        values["IncidentReportCoverage:HistoryFrom"] = "2026-01-01T00:00:00Z";
        values["IncidentReportCoverage:MinimumRetention:IncidentHistory"] = "-1.00:00:00";
        using (var services = Build(values))
        using (var scope = services.CreateScope())
            ExpectCoverageFailure(() => scope.ServiceProvider.GetRequiredService<IOptions<IncidentReportCoverage>>(), "positive");
    }

    private static ServiceProvider Build(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<IncidentManagementKustoOptions>().Configure(options => options.QueryTimeoutSeconds = 7200);
        services.AddReportExports(configuration);
        return services.BuildServiceProvider();
    }

    private static void ExpectCoverageFailure(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException("Invalid coverage configuration was accepted.");
        }
        catch (IncidentReportExportException exception)
        {
            CompatibilityTests.Require(exception.Message.Contains(message), $"Unexpected coverage failure: {exception.Message}");
        }
    }
}
