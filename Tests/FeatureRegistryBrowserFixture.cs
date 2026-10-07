using Microsoft.AspNetCore.Components;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Models.Teams;
using ProductivityInsights.ReportTests.AdoReports;
using ProductivityInsights.Services.FeatureRegistry;
using static ProductivityInsights.ReportTests.ExportIntegrationTests;

namespace ProductivityInsights.ReportTests;

internal sealed class FeatureRegistryBrowserFixture(string fixture) : IFeatureRegistryPullRequestCollector
{
    private int calls;
    private int runs;
    private AnalysisOptions? component;
    private string gitConfirmationHtml = "";
    public object State => new { calls, runs, gitConfirmationHtml };

    public async Task<FeatureRegistrySnapshot> CollectAsync(ReportRequest request, CancellationToken token)
    {
        Interlocked.Increment(ref runs);
        if (!fixture.StartsWith("feature-", StringComparison.Ordinal))
            throw new InvalidOperationException("Feature Registry should not run for an ordinary browser fixture.");
        await (Task)Call(component!, "PublishOrdinaryAnalysisAsync", 1,
            (Action)(() => throw new InvalidOperationException("A stale ordinary completion overwrote the PR run.")))!;
        if (fixture == "feature-slow") await Task.Delay(2000, token);
        if (fixture == "feature-late") await Task.Delay(2000);
        using var handler = FeatureRegistryTests.Http(message =>
        {
            Interlocked.Increment(ref calls);
            if (fixture == "feature-files-failed" && runs == 1
                && message.RequestUri!.AbsolutePath.EndsWith("/pullrequests/3/iterations/3/changes", StringComparison.Ordinal))
                return AdoReportTestHttp.Json(new { }, System.Net.HttpStatusCode.Forbidden);
            if (message.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal))
            {
                if (fixture == "feature-empty") return AdoReportTestHttp.Json(new { count = 0, value = Array.Empty<object>() });
                if (fixture == "feature-failed" && runs == 1 && AdoReportTestHttp.Query(message, "searchCriteria.status") == "completed")
                    return AdoReportTestHttp.Json(new { }, System.Net.HttpStatusCode.Forbidden);
            }
            return null;
        });
        using var client = new HttpClient(handler);
        return await FeatureRegistryTests.Collector(client).CollectAsync(request, fixture == "feature-late" ? default : token);
    }

    internal IComponent Component()
    {
        component = PreparedComponent();
        Set(component, "selectedManager", new GraphUser { id = "alice", displayName = "Alice Author", mail = "alice@example.test" });
        Set(component, "orgTreeRoot", new OrgMember
        {
            Id = "alice", DisplayName = "Alice Author", Mail = "alice@example.test",
            Reports =
            [
                new() { Id = "bob", DisplayName = "Bob Closer", Mail = "bob@example.test" },
                new() { Id = "zero", DisplayName = "No activity", Mail = "zero@example.test" }
            ]
        });
        Set(component, "featureRegistryStart", FeatureRegistryTests.Start.UtcDateTime);
        Set(component, "featureRegistryEnd", FeatureRegistryTests.End.UtcDateTime);
        Call(component, "BeginReportExportContext", 1);
        Call(component, "CompleteReportExportContext", 1, true);
        gitConfirmationHtml = (string)Call(component, "BuildAnalysisParametersHtml")!;
        return component;
    }
}
