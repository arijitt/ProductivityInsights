using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ProductivityInsights.Models;
using ProductivityInsights.Models.CommitChanges;
using ProductivityInsights.Models.WorkItems;
using ProductivityInsights.Models.WorkItems.HistoryCollection;
using ProductivityInsights.Models.Incidents;
using ProductivityInsights.Services.Analysis;

namespace ProductivityInsights.Components.Pages;

public partial class AnalysisOptions
{
    [Inject] private SelectionAnalysisService SelectionAnalysis { get; set; } = default!;
    [Inject] private IncidentSelectionService IncidentSelection { get; set; } = default!;

    private readonly Dictionary<string, List<string>> workSourceSelections = new()
    {
        ["details"] = [teamCombinationOptions[0]],
        ["trends"] = [teamCombinationOptions[0]]
    };
    private readonly Dictionary<string, List<string>> gitSourceSelections = new()
    {
        ["details"] = [gitCommitCombinationOptions[0]],
        ["trends"] = [gitCommitCombinationOptions[0]]
    };
    private readonly Dictionary<string, SelectionAnalysisResult> selectionResults = new();
    private readonly Dictionary<string, List<string>> incidentSourceSelections = new()
    {
        ["details"] = [owningTeamOptions[0]],
        ["trends"] = [owningTeamOptions[0]]
    };
    private readonly Dictionary<string, (IncidentManagementRequest Request, Dictionary<string, IncidentCollection> Incidents)> cachedIncidentSources = new();
    private IncidentManagementRequest? displayedIncidentRequest;
    private string? incidentSelectionError;
    private sealed record TeamsSourceData(WorkItemDetailsCollection? WorkItems, CommitCollection? Commits,
        Dictionary<string, (string Org, string Project, string Repo, string Branch)> CommitSources);
    private readonly Dictionary<string, TeamsSourceData> cachedTeamsSourceData = new();
    private SelectionAnalysisRequest? displayedSelectionRequest;
    private SelectionAnalysisResult? displayedSelectionResult;
    private string? selectionAnalysisError;

    private bool UsesSourceSelection => selectedView != "teams" && selectedOption is "workItems" or "gitCommits";
    private bool HasSelectedSources => selectedOption switch
    {
        "workItems" => SelectedWorkItemSources.Count > 0,
        "gitCommits" => SelectedGitSources.Count > 0,
        "incidentManagement" => SelectedIncidentSources.Count > 0,
        _ => true
    };

    private List<string> SelectedWorkItemSources
    {
        get => selectedView == "teams" ? selectedTeamCombinations : workSourceSelections[selectedView];
        set
        {
            if (selectedView == "teams") selectedTeamCombinations = value ?? [];
            else workSourceSelections[selectedView] = value ?? [];
        }
    }

    private List<string> SelectedGitSources
    {
        get => selectedView == "teams" ? selectedGitCommitCombinations : gitSourceSelections[selectedView];
        set
        {
            if (selectedView == "teams") selectedGitCommitCombinations = value ?? [];
            else gitSourceSelections[selectedView] = value ?? [];
        }
    }

    private void OnWorkItemSourcesChanged(object value)
    {
        if (selectedView == "teams") OnSelectedTeamCombinationsChanged(value);
    }

    private List<string> SelectedIncidentSources
    {
        get => selectedView == "teams" ? selectedOwningTeams : incidentSourceSelections[selectedView];
        set
        {
            if (selectedView == "teams") selectedOwningTeams = value ?? [];
            else incidentSourceSelections[selectedView] = value ?? [];
        }
    }

    private void OnIncidentSourcesChanged(object value)
    {
        if (selectedView == "teams") OnSelectedOwningTeamsChanged(value);
    }

    private string IncidentSelectionSummary => displayedIncidentRequest is { } request
        ? $"{request.OwningTeams.Count} owning team(s): " + string.Join("; ", request.OwningTeams) : "";

    private void BeginIncidentSelection(IncidentManagementRequest request)
    {
        displayedIncidentRequest = request;
        incidentSelectionError = null;
        incidentManagementGroupedIncidentsByContributor = null;
        cachedIncidentSources.Remove(GetCacheKey("incidentManagement", selectedView));
    }

    private void OnGitSourcesChanged(object value)
    {
        if (selectedView == "teams") OnSelectedGitCommitCombinationsChanged(value);
    }

    private string SelectionSummary => displayedSelectionRequest is { } request
        ? $"{request.Sources.Length} selection(s): " + string.Join("; ", request.Sources.Select(source =>
            $"{source.Organization} / {source.Project} / {source.Scope}" + (source.Branch is null ? "" : $" / {source.Branch}")))
        : "";

    private string DisplayedTrendPeriod => displayedSelectionRequest?.TrendPeriod ?? selectedTrendPeriod;

    private void CacheTeamsSourceData()
    {
        if (selectedOption == "incidentManagement" && incidentManagementGroupedIncidentsByContributor is { } incidents)
            cachedIncidentSources[GetCacheKey(selectedOption, selectedView)] =
                (displayedIncidentRequest ?? CaptureIncidentManagementRequest(), incidents);
        if (selectedView == "teams" && selectedOption is "workItems" or "gitCommits")
            cachedTeamsSourceData[GetCacheKey(selectedOption, selectedView)] =
                new(workItemDetailsCollection, commitCollection, new(teamsCommitSourceMap, StringComparer.OrdinalIgnoreCase));
    }

    private void RestoreSelectionResults(string option, string view)
    {
        selectionAnalysisError = null;
        incidentSelectionError = null;
        displayedIncidentRequest = null;
        if (option == "incidentManagement" && cachedIncidentSources.TryGetValue(GetCacheKey(option, view), out var incidentData))
        {
            displayedIncidentRequest = incidentData.Request;
            incidentManagementGroupedIncidentsByContributor = incidentData.Incidents;
        }
        if (view == "teams" && cachedTeamsSourceData.TryGetValue(GetCacheKey(option, view), out var teams))
        {
            if (option == "workItems") workItemDetailsCollection = teams.WorkItems;
            else
            {
                commitCollection = teams.Commits;
                teamsCommitSourceMap = new(teams.CommitSources, StringComparer.OrdinalIgnoreCase);
            }
        }
        displayedSelectionResult = selectionResults.GetValueOrDefault(GetCacheKey(option, view));
        displayedSelectionRequest = displayedSelectionResult?.Request;
        if (displayedSelectionResult is { } result)
        {
            if (option == "workItems") workItemDetailsCollection = result.WorkItems;
            else
            {
                commitCollection = result.Commits;
                trendData = result.TrendCommits is null ? null : BuildTrendData(result.TrendCommits, result.Request.TrendPeriod);
            }
            if (view == "trends") analysisResultHtml = option;
        }
    }

    private async Task ConfirmSourceSelectionAsync()
    {
        if (!HasSelectedSources)
        {
            selectionAnalysisError = "Select at least one source before running analysis.";
            return;
        }
        var version = ++analysisOperationVersion;
        var analysis = selectedOption;
        var view = selectedView;
        bool IsCurrentRun() => !reportDisposed && version == analysisOperationVersion && selectedOption == analysis && selectedView == view;
        BeginReportExportContext(version);
        if (displayedExportRun is not { } exportRun) return;
        var captured = exportRun.Request;
        var request = new SelectionAnalysisRequest(analysis, view, selectedTrendPeriod,
            captured.Start.UtcDateTime, captured.End.UtcDateTime,
            [.. captured.Sources.Select(source => new AnalysisSource(source.Organization, source.Project, source.Scope, source.Branch, source.Team))]);
        var key = GetCacheKey(analysis, view);
        selectionResults.Remove(key);
        cachedResults.Remove(key);
        displayedSelectionRequest = request;
        displayedSelectionResult = null;
        selectionAnalysisError = null;
        contributors = [];
        contributorDetails.Clear();
        expandedRows.Clear();
        trendData = null;
        analysisResultHtml = analysis;
        isLoadingResults = true;
        try
        {
            await JSRuntime.InvokeVoidAsync("hideModal", "analysisModal");
            if (!IsCurrentRun()) return;
            var result = await SelectionAnalysis.CollectAsync(request);
            if (!IsCurrentRun()) return;
            displayedSelectionResult = result;
            selectionResults[key] = result;
            if (analysis == "workItems")
            {
                workItemDetailsCollection = result.WorkItems;
                contributors = result.WorkSources.Keys
                    .GroupBy(item => item.fields?.SystemAssignedTo?.displayName ?? "Unassigned")
                    .OrderByDescending(group => group.Count())
                    .Select(group => (group.Key, $"{group.Count()} work item{(group.Count() != 1 ? "s" : "")} completed")).ToList();
            }
            else
            {
                commitCollection = result.Commits;
                contributors = result.CommitSources.Keys.GroupBy(commit =>
                {
                    var author = commit.author?.name ?? "Unknown";
                    var committer = commit.committer?.name ?? "Unknown";
                    return author == committer ? author : $"\U0001F464 {author} / \u270D\uFE0F {committer}";
                }).Select(group => new
                {
                    Name = group.Key, Count = group.Count(),
                    Add = group.Sum(commit => commit.changeCounts?.Add ?? 0),
                    Delete = group.Sum(commit => commit.changeCounts?.Delete ?? 0),
                    Edit = group.Sum(commit => commit.changeCounts?.Edit ?? 0)
                }).OrderByDescending(group => group.Count).ThenByDescending(group => group.Add + group.Delete + group.Edit)
                    .Select(group => (group.Name, $"{group.Count} commit{(group.Count != 1 ? "s" : "")} (+{group.Add}/-{group.Delete}/{group.Edit} files)")).ToList();
                if (result.TrendCommits is { } details) trendData = BuildTrendData(details, request.TrendPeriod);
            }
            CompleteReportExportContext(version);
        }
        catch (Exception exception)
        {
            ReportExportLogger.LogError(exception, "Unable to complete multi-source {Analysis} analysis", analysis);
            if (IsCurrentRun())
            {
                selectionAnalysisError = exception.Message;
                CompleteReportExportContext(version, false);
            }
        }
        finally
        {
            if (IsCurrentRun())
                isLoadingResults = false;
        }
    }

    private async Task<Dictionary<WorkItemValue, WorkItemHistoryCollection>> GetSelectedWorkItemHistoryAsync(List<WorkItemValue> items)
    {
        if (UsesSourceSelection && displayedSelectionResult is { } result)
            return await SelectionAnalysis.HistoryAsync(result, items);
        var query = GetWorkItemQueryOptions();
        var legacy = await Metrics.WorkItemCompletionMetrics.GetWorkItemHistoryCollectionAsync(query.OrganizationName,
            new WorkItemDetailsCollection { count = items.Count, value = items });
        return items.Where(item => legacy.ContainsKey(item.fields?.SystemId ?? 0))
            .ToDictionary(item => item, item => legacy[item.fields?.SystemId ?? 0]);
    }
}
