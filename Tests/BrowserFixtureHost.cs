using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProductivityInsights.Components;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Metrics;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Models.Teams;
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
            var component = ExportIntegrationTests.PreparedComponent(analysis);
            if (analysis is "incidentManagement" or "gitCommits" or "workItems")
            {
                var root = (OrgMember)ExportIntegrationTests.Get(component, "orgTreeRoot")!;
                var contributor = root.Reports[0];
                root.Reports =
                [
                    new OrgMember
                    {
                        Id = "example-lead", DisplayName = "Zoe Lead", Mail = "lead@example.test", Reports = [contributor]
                    },
                    new OrgMember { Id = "example-peer", DisplayName = "Aaron Peer", Mail = "peer@example.test" }
                ];
                ExportIntegrationTests.Set(component, "orgTreeMemberCount", 3);
                ExportIntegrationTests.Set(component, "expandedOrgNodes", new HashSet<string> { root.Id!, "example-lead" });
                ExportIntegrationTests.Call(component, "BeginReportExportContext", 1);
                ExportIntegrationTests.Call(component, "CompleteReportExportContext", 1, true);
            }
            if (analysis == "workItems")
                ExportIntegrationTests.Set(component, "contributorDetails", new Dictionary<string, string>
                {
                    ["Example Contributor"] = """
                        <div class='contributor-summary'>
                        <h6>📊 Summary for Example Contributor</h6>
                        <div>Total Work Items Completed: 1</div>
                        <div>Task: 1</div>
                        <div>⏱️ Time Analysis (Committed/Active/In Progress states only):</div>
                        <div>Total Days: 1.0</div>
                        <div>Maximum Days: 1.0</div>
                        <div>Minimum Days: 1.0</div>
                        <div>Average Days: 1.0</div>
                        <div>Median Days: 1.0</div>
                        <div>Work Items with Active Work Time: 1 of 1</div>
                        </div>
                        <div class='work-item-entry'>
                        <div class='work-item-header'>📋 Work Item #42 - Task</div>
                        <div class='work-item-info'><strong>📝 Title:</strong> Example item</div>
                        <div class='work-item-info'><strong>🔄 State:</strong> Done</div>
                        <div class='work-item-info'><strong>⚡ Priority:</strong> 2</div>
                        <div class='work-item-info'><strong>📅 Created Date:</strong> 2026-07-08 12:00:00</div>
                        <div class='work-item-info'><strong>✅ Closed Date:</strong> 2026-07-10 12:00:00</div>
                        <div class='work-item-info'><strong>⏱️ Time to Complete:</strong> 2.0 days</div>
                        <div class='work-item-info'><strong>🔁 Iteration:</strong> Sprint 1</div>
                        <div class='work-item-info'><strong>⏱️ Time in Active Work:</strong> 1.0 days</div>
                        </div>
                        """
                });
            return component;
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
            var snapshot = ExportIntegrationTests.Snapshot(request, fixture == "large" ? 5000 : 2);
            if (fixture == "git-role-icons")
                snapshot = snapshot with
                {
                    Commits = snapshot.Commits.Add(snapshot.Commits[0] with
                    {
                        Id = new string('f', 40),
                        Committer = request.Roster.Single(member => member.Email == "lead@example.test")
                    })
                };
            return snapshot;
        }
    }
}
