using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.IcmReports;

public static class IncidentTeamsExportTests
{
    public static async Task RunAsync()
    {
        foreach (var values in new Dictionary<string, string?>[]
        {
            [],
            new() { ["IncidentReportCoverage:Teams:0"] = " " },
            new() { ["IncidentReportCoverage:Teams:0"] = "Other", ["IncidentReportCoverage:HistoryFrom"] = "invalid" }
        })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            Register(services, configuration, "alice");
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var collector = scope.ServiceProvider.GetRequiredService<IReportCollector>();
            var source = scope.ServiceProvider.GetRequiredService<AvailableSource>();
            var request = IncidentCollectorFixtures.Request() with { View = "teams", Roster = IncidentCollectorFixtures.Roster };
            var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
            IncidentCollectorFixtures.Check(source.Queries == 2 && source.VerificationRequests == 0,
                "Teams export must use data queries only, without schema/retention/coverage prerequisites.");
            IncidentCollectorFixtures.Check(snapshot.Incidents.Length == 1 && snapshot.IncidentActions.Length == 1
                && snapshot.Request == request, "Available data and captured context were lost.");
            using var archive = ReportArchiveBuilder.Build(snapshot).Content;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
            IncidentCollectorFixtures.Check(zip.Entries.Count == 2, "Unconfigured Teams export did not produce the complete report pair.");
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                IncidentCollectorFixtures.Check(text.Contains("Available warehouse data", StringComparison.Ordinal)
                    && !text.Contains("Verified history window", StringComparison.Ordinal),
                    "Available-data export must disclose its coverage without claiming source-owner verification.");
            }
            foreach (var scenario in new[] { "failure", "action-failure", "partial", "missing-column", "limit" })
            {
                source.Scenario = scenario;
                await IncidentCollectorFixtures.ExpectFailure(() => collector.CollectAsync(request, null, CancellationToken.None),
                    scenario, scenario switch
                    {
                        "action-failure" => "ICM recorded-action history query:",
                        "missing-column" => "missing required column Title",
                        _ => "ICM incident query:"
                    });
            }
            source.Scenario = "empty";
            var empty = await collector.CollectAsync(request, null, CancellationToken.None);
            IncidentCollectorFixtures.Check(empty.Incidents.IsEmpty && empty.IncidentActions.IsEmpty
                && empty.CoverageNotes.Any(note => note.Contains("Available warehouse data", StringComparison.Ordinal)),
                "Complete empty responses must export as available-data zeros, not fail coverage validation.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await IncidentCollectorFixtures.ExpectCancellation(() => collector.CollectAsync(request, null, cancellation.Token));
            source.Scenario = null;
            await IncidentCollectorFixtures.ExpectFailure(
                () => collector.CollectAsync(request with { View = "details" }, null, CancellationToken.None),
                "Details retains its explicit verification policy.");
        }
    }

    internal static void Register(IServiceCollection services, IConfiguration configuration, string actor, bool unkeyed = false)
    {
        services.AddOptions<IncidentManagementKustoOptions>().Configure(options =>
        {
            options.ClusterUri = IncidentCollectorFixtures.Configuration.ClusterUri;
            options.Database = IncidentCollectorFixtures.Configuration.Database;
            options.QueryTimeoutSeconds = 30;
        });
        services.AddReportExports(configuration);
        services.AddScoped(provider => new AvailableSource(provider.GetRequiredService<IIncidentReportCoverageProvider>(), actor)
            { Scenario = unkeyed ? "unkeyed" : null });
        services.AddScoped<IIncidentReportQueryClient>(provider => provider.GetRequiredService<AvailableSource>());
    }

    internal sealed class AvailableSource(IIncidentReportCoverageProvider coverage, string actor) : IIncidentReportQueryClient
    {
        public int Queries { get; private set; }
        public int VerificationRequests { get; private set; }
        public string? Scenario { get; set; }

        public Task<IncidentReportCoverage?> GetCoverageAsync(IncidentReportCoverageRequest request, CancellationToken token)
        {
            VerificationRequests++;
            return coverage.GetCoverageAsync(request, token);
        }

        public Task<IncidentReportQueryResult> ExecuteAsync(IncidentReportQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (query.IsManagement)
            {
                VerificationRequests++;
                throw new IncidentReportExportException("Fixture: metadata permissions are unavailable.");
            }
            Queries++;
            if (Scenario == "failure" || Scenario == "action-failure" && query.Text == IncidentReportCollector.ActionQuery)
                throw new IncidentReportExportException("Fixture: required data query failed.");
            if (Scenario == "partial") return Task.FromResult(new IncidentReportQueryResult([], false));
            if (Scenario == "empty") return Task.FromResult(new IncidentReportQueryResult([]));
            var start = new DateTimeOffset((DateTime)query.Parameters["startDate"]);
            var when = start.AddHours(1);
            var team = (string)query.Parameters["owningTeam"];
            if (query.Text == IncidentReportCollector.IncidentQuery)
            {
                var row = IncidentCollectorFixtures.Incident(42, "Resolved", start, team,
                    modified: when, mitigated: when.AddMinutes(-30), resolved: when, owner: actor, mitigator: actor, resolver: actor);
                if (Scenario == "missing-column") row = row.Remove("Title");
                return Task.FromResult(new IncidentReportQueryResult(Scenario == "limit"
                    ? Enumerable.Repeat(row, IncidentReportCollector.MaximumRowsPerQuery + 1).ToImmutableArray() : [row]));
            }
            IncidentCollectorFixtures.Check(query.Text == IncidentReportCollector.ActionQuery, "Unexpected data query.");
            return Task.FromResult(new IncidentReportQueryResult(
                [IncidentCollectorFixtures.Action(42, Scenario == "unkeyed" ? null : 100, actor, when.AddMinutes(-10))]));
        }
    }
}
