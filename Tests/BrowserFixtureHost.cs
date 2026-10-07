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
        var featureRegistry = new FeatureRegistryBrowserFixture(fixture);
        builder.Services.AddSingleton<ProductivityInsights.Services.FeatureRegistry.IFeatureRegistryPullRequestCollector>(featureRegistry);
        if (fixture is "incident-available-export" or "incident-unkeyed-export")
            IcmReports.IncidentTeamsExportTests.Register(builder.Services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                "dev@example.test", unkeyed: fixture == "incident-unkeyed-export", queryDelay: TimeSpan.FromMilliseconds(750));
        else
            builder.Services.AddScoped<IReportCollector>(_ => new FixtureCollector(fixture));
        builder.Services.AddSingleton<IComponentActivator>(new FixtureActivator(fixture, featureRegistry));
        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapGet("/feature-fixture-state", () => featureRegistry.State);
        app.MapGet("/feature-summary-reference", () => Microsoft.AspNetCore.Http.Results.Content(
            HtmlSummaryReportWriter.Write(ReportWriters.ReportWriterFixtures.GitSnapshot(), "Git_Detailed.html"), "text/html"));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        await app.RunAsync();
    }

    private sealed class FixtureActivator(string fixture, FeatureRegistryBrowserFixture featureRegistry) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType != typeof(AnalysisOptions))
                return (IComponent)(Activator.CreateInstance(componentType)
                    ?? throw new InvalidOperationException($"Cannot create {componentType.Name}"));
            if (fixture.StartsWith("feature-", StringComparison.Ordinal))
                return featureRegistry.Component();
            if (fixture == "teams-select-all")
                return new AnalysisOptions();
            var analysis = fixture is "incident-available-export" or "incident-unkeyed-export" or "incident-active-groups" ? "incidentManagement"
                : fixture is "workItems" or "incidentManagement" ? fixture : "gitCommits";
            var component = ExportIntegrationTests.PreparedComponent(analysis);
            if (fixture == "incident-active-groups")
                ExportIntegrationTests.Set(component, "selectedOwningTeams", new List<string>
                {
                    @"AZURESYNAPSEJOBSERVICE\JobPlatform", @"SYNTHETIC\SecondQueue"
                });
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
            if (request.Analysis == Models.FeatureRegistry.FeatureRegistrySource.Analysis)
                throw new InvalidOperationException("Feature Registry must not invoke the ordinary report collector.");
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
            if (fixture == "incident-active-groups")
            {
                var active = snapshot.Incidents[0] with
                {
                    Active = true, Created = true, Mitigated = true, Resolved = true, Status = "ACTIVE"
                };
                var owners = new[]
                {
                    active.Owner, new ReportIdentity("dev"),
                    request.Roster.Single(member => member.Email == "lead@example.test"),
                    request.Roster.Single(member => member.Email == "peer@example.test")
                };
                snapshot = snapshot with
                {
                    Incidents = snapshot.Incidents.AddRange(owners.Select((owner, index) => active with
                    {
                        Id = index == 3 ? "43" : (43 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        SiloId = index == 3 ? "2" : "1", Owner = owner,
                        SourceKey = request.Sources[index == 3 ? 1 : 0].Key,
                        OwningTeam = request.Sources[index == 3 ? 1 : 0].Scope,
                        Title = $"Active incident {index + 1} <safe> & [title]",
                        Url = $"https://example.test/incident/{43 + index}?fixture=active&index={index}"
                    }))
                };
                var contribution = snapshot.Incidents[0] with
                {
                    Id = "90", Active = false, Created = false, Mitigated = false, Resolved = false,
                    Title = "Contributed incident <safe> & [title]",
                    Owner = request.Roster.Single(member => member.Email == "manager@example.test"),
                    CreatedAt = request.Start.AddDays(-10), MitigatedAt = request.Start.AddDays(-5), ResolvedAt = request.Start.AddDays(-4),
                    Url = "https://example.test/incident/90?silo=1"
                };
                var secondContribution = contribution with
                {
                    SourceKey = request.Sources[1].Key, SiloId = "2", OwningTeam = request.Sources[1].Scope,
                    Title = "Contributed incident in second silo", Url = "https://example.test/incident/90?silo=2"
                };
                var action = snapshot.IncidentActions[0] with { IncidentId = "90", ActionId = "contributed-1" };
                snapshot = snapshot with
                {
                    Incidents = snapshot.Incidents.AddRange(new[] { contribution, secondContribution }),
                    IncidentActions = snapshot.IncidentActions.AddRange(new[]
                    {
                        action, action with { ActionId = "contributed-alias", Actor = new("dev") },
                        action with { ActionId = "", Actor = owners[2] },
                        action with { SourceKey = secondContribution.SourceKey, SiloId = "2" },
                        action with { SourceKey = secondContribution.SourceKey, SiloId = "2", ActionId = "", Actor = owners[3] }
                    })
                };
            }
            if (fixture == "gitCommits")
                snapshot = snapshot with
                {
                    Commits = snapshot.Commits.SetItem(0, snapshot.Commits[0] with { Message = "Merged PR 42: Original title\nFull recorded merge body" }),
                    PullRequests = [snapshot.PullRequests[0] with { Title = "Example pull request <safe> & [title]" }]
                };
            if (fixture == "git-role-icons")
                snapshot = snapshot with
                {
                    Commits = snapshot.Commits.Add(snapshot.Commits[0] with
                    {
                        Id = new string('f', 40),
                        Committer = request.Roster.Single(member => member.Email == "lead@example.test"),
                        Message = "Merged PR 73: Shared change <safe> & [title]\nRecorded shared merge body"
                    })
                };
            return snapshot;
        }
    }
}
