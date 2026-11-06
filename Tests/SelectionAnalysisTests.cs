using System.Collections.Immutable;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Models;
using ProductivityInsights.Models.CommitChanges;
using ProductivityInsights.Models.WorkItems;
using ProductivityInsights.Models.WorkItems.HistoryCollection;
using ProductivityInsights.Services.Analysis;
using static ProductivityInsights.ReportTests.CompatibilityTests;
using static ProductivityInsights.ReportTests.ExportIntegrationTests;

namespace ProductivityInsights.ReportTests;

public static class SelectionAnalysisTests
{
    public static async Task RunAsync()
    {
        var first = new AnalysisSource("msdata", "A365", "Area A", Team: "Team A");
        var second = new AnalysisSource("msdata", "HDInsight", @"HDInsight\HDInsight on K8s\Hilo Platform", Team: "HDInsight on K8s");
        var hdInsightRoot = new AnalysisSource("msdata", "HDInsight", "HDInsight", Team: "HDInsight Team");
        var otherOrganization = first with { Organization = "other" };
        var client = new FixtureClient();
        var service = new SelectionAnalysisService(client);
        var work = await service.CollectAsync(RequestFor("workItems", first, first with { Scope = "Overlapping area" }, second, otherOrganization, hdInsightRoot));
        Require(work.WorkSources.Count == 3 && client.Calls.Count == 5,
            "Collect every selected work source, deduplicate parent/child area overlap within an organization, and retain same IDs in other organizations.");
        var histories = await service.HistoryAsync(work, work.WorkSources.Keys);
        Require(histories.Count == 3 && client.Calls.Count(call => call.StartsWith("history:")) == 2,
            "Histories must use each captured organization rather than the current form.");
        Require(histories.All(pair => pair.Value.value![0].fields!.SystemAreaPath == work.WorkSources[pair.Key].Organization),
            "History results with the same numeric ID must not cross organizations.");

        first = new("msdata", "A365", "BBC", "develop");
        second = new("msdata", "HDInsight", "HDInsight-Main", "master");
        var git = await service.CollectAsync(RequestFor("gitCommits", first, first with { Branch = "main" }, second,
            first with { Organization = "other" }) with { View = "trends" });
        Require(git.CommitSources.Count == 3 && git.TrendCommits?.Value.Count == 3,
            "Git must deduplicate branch overlap but retain same-SHA commits from different repositories and organizations.");
        var component = new AnalysisOptions();
        var trends = (List<AnalysisOptions.CommitTrendDataPoint>)Call(component, "BuildTrendData", git.TrendCommits!, "daily")!;
        var commits = trends.Sum(point => point.TotalCommits);
        Require(commits == 3, "Trend aggregation must preserve source-qualified commit counts.");
        var details = await service.CommitDetailsAsync(git, git.CommitSources.Keys.Take(2));
        Require(details.Value.Select(commit => commit.RepositoryId).Distinct().Count() == 2,
            "Contributor details must query the recorded repository for each commit.");

        foreach (var failure in new[] { "work", "commits", "details" })
        {
            client.Failure = failure;
            client.FailureScope = second.Scope;
            try
            {
                await service.CollectAsync(RequestFor(failure == "work" ? "workItems" : "gitCommits", first, second)
                    with { View = failure == "details" ? "trends" : "details" });
                throw new Exception("Unavailable source was published as a successful analysis.");
            }
            catch (InvalidOperationException error)
            {
                Require(error.Message.Contains("unavailable"), "Failed sources must surface an actionable error.");
            }
        }
        VerifySelections();
        VerifyTeamsSourceRestore();
    }

    private static SelectionAnalysisRequest RequestFor(string analysis, params AnalysisSource[] sources) =>
        new(analysis, "details", "daily", new(2026, 7, 1), new(2026, 7, 31), [.. sources]);

    private static void VerifySelections()
    {
        var component = PreparedComponent();
        foreach (var analysis in new[] { "workItems", "gitCommits" })
        {
            var property = analysis == "workItems" ? "SelectedWorkItemSources" : "SelectedGitSources";
            var teamField = analysis == "workItems" ? "selectedTeamCombinations" : "selectedGitCommitCombinations";
            var optionsField = analysis == "workItems" ? "teamCombinationOptions" : "gitCommitCombinationOptions";
            var options = (string[])typeof(AnalysisOptions).GetField(optionsField,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var teams = ((List<string>)Get(component, teamField)!).ToArray();
            Set(component, "selectedOption", analysis);
            foreach (var view in new[] { "details", "trends" })
            {
                Set(component, "selectedView", view);
                Require(((List<string>)Get(component, property)!).SequenceEqual(options.Take(1)), "Each view must retain its original default selection.");
                Set(component, property, new List<string> { options[0], options[^1] });
                var confirmation = (string)Call(component, "BuildAnalysisParametersHtml")!;
                Require(confirmation.Contains("Selections (2)") && !confirmation.Contains("Manager Filter")
                    && confirmation.Contains(System.Net.WebUtility.HtmlEncode(options[^1])),
                    "Details and Trends confirmations must list every selected source without a manager filter.");
                Call(component, "BeginReportExportContext", 2);
                var captured = Request(component);
                Require(captured.Sources.Length == 2 && captured.Roster.IsEmpty && captured.View == view,
                    "Both selected sources must enter the captured export context in the correct non-Teams view.");
                if (analysis == "workItems")
                    Require(captured.Sources[1].Project == "A365"
                        && captured.Sources[1].Team == "One Infra - Foundations"
                        && captured.Sources[1].Scope == @"A365\One Infra\One Infra Foundations",
                        "The second Foundations area must retain its actual team and exact scope for analysis and export.");
                Set(component, property, new List<string>());
                Require(!(bool)Get(component, "CanRunAnalysis")! && Request(component) == captured,
                    "Clear must disable analysis without mutating executed export inputs.");
            }
            Require(((List<string>)Get(component, teamField)!).SequenceEqual(teams), "Non-Teams edits must not affect Teams selections.");
            Set(component, "selectedView", "details");
            Require(((List<string>)Get(component, property)!).Count == 0, "A cleared Details selection must survive Trends edits.");
        }
    }

    private static void VerifyTeamsSourceRestore()
    {
        foreach (var analysis in new[] { "workItems", "gitCommits" })
        {
            var component = PreparedComponent(analysis);
            var originalWork = new WorkItemDetailsCollection { count = 1, value = [new() { id = 42 }] };
            var originalCommits = new CommitCollection { count = 1, value = [new() { commitId = "original" }] };
            Set(component, "workItemDetailsCollection", originalWork);
            Set(component, "commitCollection", originalCommits);
            var sources = new Dictionary<string, (string Org, string Project, string Repo, string Branch)>
            {
                ["original"] = ("msdata", "A365", "BBC", "develop")
            };
            Set(component, "teamsCommitSourceMap", sources);
            Call(component, "CloseAnalysisResults");
            Set(component, "workItemDetailsCollection", new WorkItemDetailsCollection());
            Set(component, "commitCollection", new CommitCollection());
            sources.Clear();
            Call(component, "RestoreCachedResults", analysis, "teams");
            Require(analysis == "workItems"
                    ? ReferenceEquals(Get(component, "workItemDetailsCollection"), originalWork)
                    : ReferenceEquals(Get(component, "commitCollection"), originalCommits)
                        && ((Dictionary<string, (string, string, string, string)>)Get(component, "teamsCommitSourceMap")!).ContainsKey("original"),
                "A Details/Trends run must not replace the data or repository map behind cached Teams drill-downs.");
        }
    }

    internal sealed class FixtureClient : IAnalysisDataClient
    {
        public List<string> Calls { get; } = [];
        public string? Failure { get; set; }
        public string? FailureScope { get; set; }
        public int DelayMilliseconds { get; set; }

        public async Task<WorkItemDetailsCollection?> WorkItemsAsync(AnalysisSource source, DateTime start, DateTime end)
        {
            Calls.Add($"work:{source.Organization}/{source.Project}/{source.Scope}");
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds);
            if (Failure == "work" && source.Scope == FailureScope) return null;
            if (source.Project == "HDInsight")
                Require(source.Team == (source.Scope switch
                {
                    "HDInsight" or @"HDInsight\Supportability" => "HDInsight Team",
                    @"HDInsight\HDI Product Management" => "HDI Product Management",
                    _ => "HDInsight on K8s"
                }),
                    "HDInsight selections must route each area through its actual Azure DevOps team.");
            if (source.Scope == @"A365\One Infra\Cluster Service")
                Require(source.Project == "A365" && source.Team == "One Infra - CS",
                    "Cluster Service selections must use the actual One Infra - CS team.");
            if (source.Scope is @"A365\Synapse Jobs Service\BBC\Foundations" or @"A365\One Infra\One Infra Foundations")
                Require(source.Project == "A365" && source.Team == "One Infra - Foundations",
                    "Foundations selections must use the actual One Infra - Foundations team.");
            if (source.Scope == @"A365\One Infra\Reliability")
                Require(source.Project == "A365" && source.Team == "One Infra - Reliability",
                    "Reliability selections must use the actual One Infra - Reliability team.");
            var id = source.Scope switch
            {
                @"A365\One Infra\Cluster Service" => 3,
                @"A365\Synapse Jobs Service\BBC\Foundations" => 4,
                @"A365\One Infra\One Infra Foundations" => 5,
                @"A365\One Infra\Reliability" => 6,
                _ => source.Project == "HDInsight" ? 2 : 1
            };
            return new() { count = 1, value = [new()
            {
                id = id, url = $"https://example.test/{source.Organization}/work/{id}",
                fields = new()
                {
                    SystemId = id, SystemTitle = "Captured item " + source.Scope, SystemWorkItemType = "Task",
                    SystemState = "Done", SystemAssignedTo = new() { displayName = "Selection Contributor", uniqueName = "selection@example.test" },
                    SystemChangedDate = start.AddDays(2), SystemCreatedDate = start, MicrosoftVSTSCommonClosedDate = start.AddDays(2)
                }
            }] };
        }

        public async Task<CommitCollection?> CommitsAsync(AnalysisSource source, DateTime start, DateTime end)
        {
            Calls.Add($"commits:{source.Organization}/{source.Project}/{source.Scope}/{source.Branch}");
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds);
            if (Failure == "commits" && source.Scope == FailureScope) return null;
            return new() { count = 1, value = [new()
            {
                commitId = new string('a', 40), author = new() { name = "Selection Contributor", email = "selection@example.test", date = start },
                committer = new() { name = "Selection Contributor", email = "selection@example.test", date = start },
                comment = "Captured commit " + source.Scope, changeCounts = new() { Add = 1 }
            }] };
        }

        public Task<Dictionary<int, WorkItemHistoryCollection>> HistoryAsync(string organization, WorkItemDetailsCollection items)
        {
            Calls.Add("history:" + organization);
            return Task.FromResult(items.value!.ToDictionary(item => item.id, item => new WorkItemHistoryCollection
            {
                count = 2, value =
                [
                    new() { fields = new() { SystemState = "Active", SystemAreaPath = organization, SystemChangedDate = new(2026, 7, 1) } },
                    new() { fields = new() { SystemState = "Done", SystemAreaPath = organization, SystemChangedDate = new(2026, 7, 2) } }
                ]
            }));
        }

        public Task<GitCommitCollection?> CommitDetailsAsync(AnalysisSource source, CommitCollection commits)
        {
            Calls.Add($"details:{source.Organization}/{source.Project}/{source.Scope}/{source.Branch}");
            return Task.FromResult<GitCommitCollection?>(Failure == "details" && source.Scope == FailureScope ? null : new()
            {
                Count = commits.count, Value = commits.value!.Select(commit => new GitCommit
                {
                    Organization = source.Organization, ProjectName = source.Project, RepositoryId = source.Scope,
                    CommitId = commit.commitId, AuthorName = commit.author!.name, CommitterName = commit.committer!.name,
                    CommitterDate = commit.committer.date, AuthorDate = commit.author.date,
                    Comment = "Captured commit " + source.Scope, AddedLines = 3, DeletedLines = 1,
                    RemoteUrl = $"https://example.test/{source.Scope}/commit/{commit.commitId}"
                }).ToList()
            });
        }
    }
}
