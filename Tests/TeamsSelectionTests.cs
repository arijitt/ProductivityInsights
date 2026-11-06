using System.Reflection;
using Microsoft.AspNetCore.Components;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Options;
using static ProductivityInsights.ReportTests.CompatibilityTests;
using static ProductivityInsights.ReportTests.ExportIntegrationTests;

namespace ProductivityInsights.ReportTests;

public static class TeamsSelectionTests
{
    private static readonly (string Analysis, string Selection, string Options, string Handler)[] Selectors =
    [
        ("workItems", "selectedTeamCombinations", "teamCombinationOptions", "OnSelectedTeamCombinationsChanged"),
        ("gitCommits", "selectedGitCommitCombinations", "gitCommitCombinationOptions", "OnSelectedGitCommitCombinationsChanged"),
        ("incidentManagement", "selectedOwningTeams", "owningTeamOptions", "OnSelectedOwningTeamsChanged")
    ];

    public static void Run()
    {
        VerifyWorkItemSourceSelections();
        var component = new AnalysisOptions();
        VerifyAll(component);
        Require((string)Get(component, "selectedView")! == "details"
            && (string)Get(component, "selectedOrganization")! == "msdata"
            && (string)Get(component, "selectedProject")! == "A365"
            && (string)Get(component, "selectedTeam")! == "Cosmos on Azure Job Service"
            && (string)Get(component, "selectedGitRepository")! == "BBC-VHD"
            && (string)Get(component, "selectedTargetBranch")! == "master"
            && (string)Get(component, "selectedOwningTeam")! == "AZURESYNAPSEJOBSERVICE\\JobPlatform",
            "Selecting all Teams sources must not change the initial view or single-source defaults.");
        EditSingleSourceInputs(component);
        Call(component, "OnViewSelected", "teams");
        VerifyAll(component);
        Require(!(bool)Get(component, "CanRunAnalysis")!,
            "Selecting all sources must not bypass the manager/Fetch Reports prerequisite.");

        foreach (var (analysis, selection, options, handler) in Selectors)
        {
            Call(component, "OnOptionSelected", analysis);
            var chosen = new List<string> { Options(options)[^1] };
            Set(component, selection, chosen);
            Call(component, handler, chosen);
        }
        var selected = Selectors.ToDictionary(selector => selector.Selection,
            selector => ((List<string>)Get(component, selector.Selection)!).ToArray());
        foreach (var view in new[] { "details", "trends", "teams" })
        {
            Call(component, "OnViewSelected", view);
            if (view != "teams") EditSingleSourceInputs(component);
            foreach (var option in new[] { "workItems", "gitCommits", "incidentManagement", "featureRegistry" })
                Call(component, "OnOptionSelected", option);
            foreach (var selector in Selectors)
                Require(((List<string>)Get(component, selector.Selection)!).SequenceEqual(selected[selector.Selection]),
                    "Tab/view changes and single-source edits must preserve explicit Teams choices: " + selector.Analysis);
        }
        foreach (var (analysis, selection, _, handler) in Selectors)
        {
            Call(component, "OnOptionSelected", analysis);
            Set(component, selection, new List<string>());
            Call(component, handler, new List<string>());
        }
        Call(component, "OnViewSelected", "details");
        EditSingleSourceInputs(component);
        Call(component, "OnViewSelected", "teams");
        foreach (var selector in Selectors)
            Require(((List<string>)Get(component, selector.Selection)!).Count == 0,
                "Explicitly clearing a Teams selection must not silently restore Select All.");
        VerifyAll(new AnalysisOptions());

        foreach (var (analysis, selection, options, _) in Selectors)
        {
            var prepared = PreparedComponent(analysis);
            Set(prepared, selection, Options(options).ToList());
            Call(prepared, "BeginReportExportContext", 2);
            var request = Request(prepared);
            Require(request.Sources.Length == Options(options).Length,
                "Select All must capture every available source in the executed request: " + analysis);
            Set(prepared, selection, new List<string>());
            Require(Request(prepared) == request,
                "Editing Select All after execution must not change the captured report sources.");
        }
    }

    private static void VerifyWorkItemSourceSelections()
    {
        const string areaPath = @"HDInsight\HDInsight on K8s\Hilo Platform";
        foreach (var view in new[] { "details", "trends" })
        {
            var component = new AnalysisOptions();
            Call(component, "OnViewSelected", view);
            Call(component, "OnProjectChanged", new ChangeEventArgs { Value = "HDInsight" });
            var query = (WorkItemQueryOptions)Call(component, "GetWorkItemQueryOptions")!;
            Require(query.OrganizationName == "msdata" && query.ProjectName == "HDInsight"
                && query.TeamName == "HDInsight on K8s" && query.AreaPath == areaPath
                && (bool)Call(component, "IsAreaPathApplicable", areaPath)!,
                $"Selecting HDInsight in {view} must query the actual HDInsight on K8s team and Hilo Platform area path.");

            Call(component, "OnProjectChanged", new ChangeEventArgs { Value = "A365" });
            query = (WorkItemQueryOptions)Call(component, "GetWorkItemQueryOptions")!;
            Require(query.ProjectName == "A365" && query.TeamName == "Cosmos on Azure Job Service"
                && query.AreaPath == @"A365\Cosmos Analytics\Platforms\Cosmos on Azure"
                && !(bool)Call(component, "IsAreaPathApplicable", areaPath)!,
                "Switching back to A365 must restore its defaults and disable the HDInsight area path.");
        }

        Require(!Options("teamCombinationOptions").Contains("msdata : HDInsight : HDInsight Team : HDInsight"),
            "The Supportability selection must not leave the broader HDInsight root area in the dropdown.");
        foreach (var view in new[] { "details", "trends", "teams" })
        foreach (var (project, team, area) in new[]
        {
            ("HDInsight", "HDInsight on K8s", areaPath),
            ("HDInsight", "HDI Product Management", @"HDInsight\HDI Product Management"),
            ("HDInsight", "HDInsight Team", @"HDInsight\Supportability"),
            ("A365", "One Infra - CS", @"A365\One Infra\Cluster Service"),
            ("A365", "One Infra - Foundations", @"A365\Synapse Jobs Service\BBC\Foundations"),
            ("A365", "One Infra - Foundations", @"A365\One Infra\One Infra Foundations"),
            ("A365", "One Infra - Reliability", @"A365\One Infra\Reliability")
        })
        {
            var component = PreparedComponent("workItems");
            Set(component, "selectedView", view);
            var combination = $"msdata : {project} : {team} : {area}";
            Require(Options("teamCombinationOptions").Contains(combination),
                $"{view} must offer the source-qualified {team} option.");
            var first = area == @"A365\One Infra\One Infra Foundations"
                ? @"msdata : A365 : One Infra - Foundations : A365\Synapse Jobs Service\BBC\Foundations"
                : Options("teamCombinationOptions")[0];
            var chosen = new List<string> { first, combination };
            Set(component, "SelectedWorkItemSources", chosen);
            Call(component, "OnWorkItemSourcesChanged", chosen);
            var sources = (List<(string Org, string Project, string Team, string AreaPath)>)
                Call(component, "ParseSelectedTeamCombinations")!;
            Require(sources.Count == 2 && sources[0].Project == "A365"
                && sources[1] == ("msdata", project, team, area),
                "Selections must retain distinct area paths even when they share the same project and team.");
            Call(component, "BeginReportExportContext", 2);
            var captured = Request(component).Sources;
            Require(captured.Length == 2 && captured[1].Organization == "msdata"
                && captured[1].Project == project && captured[1].Team == team
                && captured[1].Scope == area,
                "Analysis and export must capture each selected source without relabeling its project or area.");
        }
    }

    private static void EditSingleSourceInputs(AnalysisOptions component)
    {
        Call(component, "OnOrganizationChanged", new ChangeEventArgs { Value = "powerbi" });
        Call(component, "OnOrganizationChanged", new ChangeEventArgs { Value = "msdata" });
        Call(component, "OnProjectChanged", new ChangeEventArgs { Value = "A365" });
        Call(component, "OnTeamChanged", new ChangeEventArgs { Value = "FastSpark" });
        Call(component, "OnGitRepositoryChanged", new ChangeEventArgs { Value = "BBC" });
        Set(component, "selectedOwningTeam", "HDINSIGHT\\ControlPlane");
    }

    private static void VerifyAll(AnalysisOptions component)
    {
        foreach (var (_, selection, options, _) in Selectors)
        {
            var values = (List<string>)Get(component, selection)!;
            Require(values.SequenceEqual(Options(options)) && values.Count > 0,
                "A new Teams selection must contain all available options in their existing order: " + selection);
        }
        Require(((HashSet<string>)Get(component, "selectedTeams")!).SetEquals(Options("teamOptions")),
            "The selected Work Items teams must match the Select All combinations.");
    }

    private static string[] Options(string field) =>
        (string[])typeof(AnalysisOptions).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
