using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProductivityInsights.Components;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Metrics;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;
using Radzen;
using System.Collections.Immutable;

namespace ProductivityInsights.ReportTests;

internal static class BrowserFixtureHost
{
    public static async Task RunAsync(string[] args)
    {
        var fixtureIndex = Array.IndexOf(args, "--fixture");
        var fixture = fixtureIndex >= 0 ? args[fixtureIndex + 1] : "gitCommits";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = CompatibilityTests.RepositoryRoot(),
            ApplicationName = typeof(App).Assembly.GetName().Name,
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddRadzenComponents();
        builder.Services.AddScoped<ProductivityInsights.Services.ThemeService>();
        builder.Services.AddOptions<IncidentManagementKustoOptions>()
            .Bind(builder.Configuration.GetSection(IncidentManagementKustoOptions.SectionName));
        builder.Services.AddSingleton<IncidentManagementMetrics>();
        if (fixture is "incident-available-export" or "incident-unkeyed-export")
            IcmReports.IncidentTeamsExportTests.Register(builder.Services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                "dev@example.test", unkeyed: fixture == "incident-unkeyed-export");
        else
            builder.Services.AddScoped<IReportCollector>(_ => new FixtureCollector(fixture));
        builder.Services.AddSingleton<IComponentActivator>(new FixtureActivator(fixture));
        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        await app.RunAsync();
    }

    private sealed class FixtureActivator(string fixture) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType != typeof(AnalysisOptions))
                return (IComponent)(Activator.CreateInstance(componentType)
                    ?? throw new InvalidOperationException($"Cannot create {componentType.Name}"));
            var analysis = fixture is "incident-available-export" or "incident-unkeyed-export" ? "incidentManagement"
                : fixture is "workItems" or "incidentManagement" ? fixture : "gitCommits";
            return ExportIntegrationTests.PreparedComponent(analysis);
        }
    }

    private sealed class FixtureCollector(string fixture) : IReportCollector
    {
        private int collections;

        public async Task<ReportSnapshot> CollectAsync(
            ReportRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report("Collecting synthetic export fixture...");
            await Task.Delay(fixture == "slow-export" ? 5000 : 50, cancellationToken);
            if (fixture == "failed-export")
                throw new InvalidOperationException("Fixture: incomplete changed files in a required export source.");
            if (fixture == "skipped-export")
            {
                collections++;
                if (collections == 1)
                    return new ReportSnapshot
                    {
                        Request = request, AsOf = DateTimeOffset.UtcNow,
                        SkippedSources = request.Sources.Select(source =>
                            new ReportSkippedSource(source.Key, "Fixture: repository unavailable (HTTP 404).")).ToImmutableArray()
                    };
                if (collections > 2)
                    throw new InvalidOperationException("A fully collected export should reuse its snapshot.");
            }
            return ExportIntegrationTests.Snapshot(request, fixture == "large" ? 5000 : 2);
        }
    }
}
