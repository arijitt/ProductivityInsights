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
        VerifyWorkItemBudgetConfiguration();
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

    private static void VerifyWorkItemBudgetConfiguration()
    {
        const string key = "ReportExport:WorkItems:MaxCollectionBytes";
        foreach (var maximum in new[] { 256L * 1024 * 1024, 3L * 1024 * 1024 * 1024 })
        {
            using var services = Build(new Dictionary<string, string?> { [key] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            using var scope = services.CreateScope();
            var limits = scope.ServiceProvider.GetRequiredKeyedService<AdoReportCollectionLimits>("workItems");
            CompatibilityTests.Require(limits.MaxCollectionBytes == maximum,
                "Work Items export must use the configured cumulative byte limit, including values larger than Int32.");
            _ = scope.ServiceProvider.GetRequiredKeyedService<AdoReportCollector>("workItems");
        }
        foreach (var invalid in new[] { "0", "-1", "invalid", "9223372036854775808" })
        {
            using var services = Build(new Dictionary<string, string?> { [key] = invalid });
            using var scope = services.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IReportCollector>();
            _ = scope.ServiceProvider.GetRequiredService<AdoReportCollector>();
            try
            {
                _ = scope.ServiceProvider.GetRequiredKeyedService<AdoReportCollector>("workItems");
                throw new Exception("Invalid Work Items export budget was accepted.");
            }
            catch (InvalidOperationException exception)
            {
                CompatibilityTests.Require(exception.Message.Contains(key),
                    "Invalid Work Items export budgets must identify their configuration key without blocking other collectors.");
            }
        }
    }

    internal static ServiceProvider Build(IEnumerable<KeyValuePair<string, string?>> values)
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
