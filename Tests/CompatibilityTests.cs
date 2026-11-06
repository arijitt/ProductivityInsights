using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProductivityInsights.ReportTests;

public static class CompatibilityTests
{
    private const string Baseline = "1379858";

    public static void Run()
    {
        var root = RepositoryRoot();
        var protectedFiles = Git(root, "ls-tree", "-r", "--name-only", Baseline,
                "--", "Metrics", "Models", "Options", "Utilities")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var changes = Git(root, ["diff", "--name-only", Baseline, "--", .. protectedFiles]);
        Require(string.IsNullOrWhiteSpace(changes), $"Existing API/DTO/auth files changed:\n{changes}");
        var collectors = Git(root, "diff", "--name-only", "4720086", "--",
            "Services/Reports/AdoReportCollector.cs", "Services/Reports/AdoReportCollector.Git.cs",
            "Services/Reports/AdoReportHttp.cs", "Services/Reports/ReportIdentityMatcher.cs");
        Require(string.IsNullOrWhiteSpace(collectors), "Feature Registry changed an existing Git collector/transport/identity implementation.");

        var baselinePage = Git(root, "show", $"{Baseline}:Components/Pages/AnalysisOptions.razor");
        var currentPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "AnalysisOptions.razor"));
        Require(NormalizePage(baselinePage) == NormalizePage(currentPage),
            "An existing analysis API call, report renderer, form, or source mapping changed outside permitted export hooks.");

        var baselineCss = Git(root, "show", $"{Baseline}:Components/Pages/AnalysisOptions.razor.css");
        var currentCss = File.ReadAllText(Path.Combine(root, "Components", "Pages", "AnalysisOptions.razor.css"))
            .Replace(".analysis-export-btn", ".analysis-pdf-btn");
        currentCss = Regex.Replace(currentCss, @"\.report-export-status\s*\{[^}]*\}", "");
        Require(NormalizeLines(baselineCss) == NormalizeLines(currentCss),
            "Existing UX report styling changed.");

        var baselineTheme = Git(root, "show", $"{Baseline}:wwwroot/theme.js");
        baselineTheme = baselineTheme[..baselineTheme.IndexOf("window._loadScript", StringComparison.Ordinal)];
        var currentTheme = File.ReadAllText(Path.Combine(root, "wwwroot", "theme.js"));
        Require(NormalizeLines(baselineTheme) == NormalizeLines(currentTheme),
            "Non-export theme/modal/scrolling functions changed.");
    }

    private static string NormalizePage(string value)
    {
        value = value.Replace("\r\n", "\n");
        value = NormalizeFeatureRegistryHooks(value);
        value = NormalizeTeamsSelectionDefaults(value);
        value = NormalizeWorkItemSourceSelections(value);
        value = RemoveRange(value, "    private string? TeamsManagerWarning =>",
            "    // ---- Teams view (manager org-tree) UI + handlers ----");
        value = value.Replace("        @if (TeamsManagerWarning is { } managerWarning)\n"
            + "        {\n"
            + "            <div class=\"alert alert-warning teams-manager-warning\" role=\"alert\">@managerWarning</div>\n"
            + "        }\n", "");
        value = RemoveRange(value, "                    <button class=\"btn btn-sm analysis-pdf-btn\"",
            "                    @if (selectedView == \"details\"");
        value = RemoveRange(value, "                    <button class=\"btn btn-sm analysis-export-btn\"",
            "                    @if (selectedView == \"details\"");
        value = RemoveRange(value, "    private IJSObjectReference? _jsModule;", "    public async ValueTask DisposeAsync()");
        value = RemoveRange(value, "    public async ValueTask DisposeAsync()", "    private RenderFragment RenderAnalysisForm()");
        value = RemoveRange(value, "    private async Task DownloadAsPdf()", "    private IncidentManagementRequest CaptureIncidentManagementRequest()");
        value = Regex.Replace(value,
            @"(?m)^[ \t]*(?:(?:Begin|Complete|Hide|Restore)ReportExportContext\([^\r\n]*\);|private bool isGeneratingPdf = false;)[ \t]*\n", "");
        value = NormalizeMultiSourceSelection(value);
        return NormalizeLines(value);
    }

    private static string NormalizeFeatureRegistryHooks(string value)
    {
        value = RemoveRange(value, "                <li class=\"nav-item feature-registry-tab\"", "            </ul>");
        value = RemoveRange(value, "                <div class=\"tab-pane feature-registry-panel", "            </div>\n\n            @if");
        value = RemoveRange(value, "    private RenderFragment RenderFeatureRegistryForm()", "    private RenderFragment RenderGitCommitsForm()");
        value = RemoveRange(value, "                    @if (featureRegistryRequest is not null)",
            "                    else if (selectedView == \"details\" || selectedView == \"teams\")");
        value = value.Replace("                    else if (selectedView == \"details\" || selectedView == \"teams\")",
            "                    @if (selectedView == \"details\" || selectedView == \"teams\")");
        foreach (var handler in new[] { "ShowFeatureRegistryConfirmationAsync", "ConfirmFeatureRegistryAnalysisAsync" })
            value = value.Replace("        if (IsFeatureRegistrySelected)\n        {\n"
                + $"            await {handler}();\n            return;\n        }}\n", "");
        foreach (var hook in new[]
        {
            "            EnterFeatureRegistryTab(option);\n",
            "        if (RestoreFeatureRegistryResults(option, view)) return;\n",
            "        if (CloseFeatureRegistryResults()) return;\n",
            "            \"featureRegistry\" => \"Feature Registry Analysis\",\n",
            "                    if (SupersededByFeatureRegistry(operationVersion)) return;\n"
        }) value = value.Replace(hook, "");
        return value.Replace("await PublishOrdinaryAnalysisAsync(operationVersion, () =>", "await InvokeAsync(() =>");
    }

    private static string NormalizeTeamsSelectionDefaults(string value) =>
        value.Replace(
            "private HashSet<string> selectedTeams = new HashSet<string>(teamOptions, StringComparer.OrdinalIgnoreCase);",
            "private HashSet<string> selectedTeams = new HashSet<string>(new[] { \"Cosmos on Azure Job Service\" }, StringComparer.OrdinalIgnoreCase);")
        .Replace("    private List<string> selectedTeamCombinations = new List<string>(teamCombinationOptions);",
            """
                private List<string> selectedTeamCombinations = new List<string>
                {
                    BuildTeamCombination(
                        "msdata",
                        "A365",
                        "Cosmos on Azure Job Service",
                        GetAreaPathForTeam("Cosmos on Azure Job Service"))
                };
            """)
        .Replace("private List<string> selectedGitCommitCombinations = new List<string>(gitCommitCombinationOptions);",
            "private List<string> selectedGitCommitCombinations = new List<string>();")
        .Replace("private List<string> selectedOwningTeams = new List<string>(owningTeamOptions);",
            "private List<string> selectedOwningTeams = new List<string>();")
        .Replace("    private void SyncSelectedTeamCombinations()\n    {\n        if (selectedView != \"teams\") return;\n",
            "    private void SyncSelectedTeamCombinations()\n    {\n");

    private static string NormalizeWorkItemSourceSelections(string value) =>
        value.Replace("            \"One Infra - CS\" => \"A365\\\\One Infra\\\\Cluster Service\",\n", "")
        .Replace("        \"One Infra - CS\",\n", "")
        .Replace("            \"One Infra - Foundations\" => \"A365\\\\Synapse Jobs Service\\\\BBC\\\\Foundations\",\n", "")
        .Replace("        \"One Infra - Foundations\",\n", "")
        .Replace("            \"One Infra - Reliability\" => \"A365\\\\One Infra\\\\Reliability\",\n", "")
        .Replace("        \"One Infra - Reliability\",\n", "")
        .Replace("        .Append(BuildTeamCombination(\"msdata\", \"A365\", \"One Infra - Foundations\", \"A365\\\\One Infra\\\\One Infra Foundations\"))\n", "")
        .Replace("                                <option value=\"HDInsight\">HDInsight</option>",
            "                                <!--<option value=\"HDInsight\">HDInsight</option>-->")
        .Replace("@if (selectedProject == \"A365\" || selectedProject == \"HDInsight\")",
            "@if (selectedProject == \"A365\")")
        .Replace("@foreach (var team in teamOptions.Where(team => GetProjectForTeam(team) == selectedProject))",
            "@foreach (var team in teamOptions)")
        .Replace("                    <option value=\"HDInsight\\HDInsight on K8s\\Hilo Platform\" disabled=\"@(!IsAreaPathApplicable(\"HDInsight\\\\HDInsight on K8s\\\\Hilo Platform\"))\">HDInsight\\HDInsight on K8s\\Hilo Platform</option>\n", "")
        .Replace("            selectedTeam = \"HDInsight on K8s\";\n            selectedAreaPath = GetAreaPathForTeam();\n", "")
        .Replace("            \"HDInsight on K8s\" => \"HDInsight\\\\HDInsight on K8s\\\\Hilo Platform\",\n", "")
        .Replace("            \"HDI Product Management\" => \"HDInsight\\\\HDI Product Management\",\n", "")
        .Replace("            \"HDInsight Team\" => \"HDInsight\\\\Supportability\",\n", "")
        .Replace("    private static string GetProjectForTeam(string team) =>\n"
            + "        team is \"HDInsight on K8s\" or \"HDI Product Management\" or \"HDInsight Team\" ? \"HDInsight\" : \"A365\";\n", "")
        .Replace("        \"Lakehouse Job Execution and Capacities\",\n        \"HDInsight on K8s\",\n        \"HDI Product Management\",\n        \"HDInsight Team\"\n",
            "        \"Lakehouse Job Execution and Capacities\"\n")
        .Replace(".Select(team => BuildTeamCombination(\"msdata\", GetProjectForTeam(team), team, GetAreaPathForTeam(team)))",
            ".Select(team => BuildTeamCombination(\"msdata\", \"A365\", team, GetAreaPathForTeam(team)))");

    private static string NormalizeMultiSourceSelection(string value)
    {
        foreach (var form in new[] { "RenderAnalysisForm", "RenderGitCommitsForm", "RenderIncidentResolutionsForm" })
            value = RemoveRange(value, $"    private RenderFragment {form}()", "        <div class=\"datetime-group mb-4\">");
        value = value.Replace("        RestoreSelectionResults(option, view);\n", "")
            .Replace("            CacheTeamsSourceData();\n", "")
            .Replace("            BeginIncidentSelection(incidentManagementRequest);\n", "")
            .Replace("                        incidentSelectionError = incidentManagementResult.ErrorMessage;\n", "")
            .Replace("                        incidentSelectionError = ex.Message;\n", "")
            .Replace("            if (UsesSourceSelection) isLoadingResults = false;\n", "")
            .Replace("HasSelectedSources && (selectedView != \"teams\" || (selectedManager != null && orgTreeRoot != null))",
                "selectedView != \"teams\" || (selectedManager != null && orgTreeRoot != null)")
            .Replace("bool teamsWorkItems = selectedOption == \"workItems\";", "bool teamsWorkItems = selectedView == \"teams\" && selectedOption == \"workItems\";")
            .Replace("bool teamsGitCommits = selectedOption == \"gitCommits\";", "bool teamsGitCommits = selectedView == \"teams\" && selectedOption == \"gitCommits\";")
            .Replace("if (selectedView == \"teams\") html.Append", "html.Append")
            .Replace("(contributors.Any() || UsesSourceSelection && displayedSelectionResult is not null)", "contributors.Any()")
            .Replace("        if (UsesSourceSelection)\n        {\n            await ConfirmSourceSelectionAsync();\n            return;\n        }\n", "")
            .Replace("<text>Work Items Completion Summary across @SelectionSummary</text>",
                "<text>Work Items Completion Summary for '@selectedOrganization\\@selectedProject\\@selectedAreaPath'</text>")
            .Replace("<text>Code Contribution Summary across @SelectionSummary</text>",
                "<text>Code Contribution Summary for '@selectedOrganization\\@selectedProject\\@selectedGitRepository\\@selectedTargetBranch' Branch</text>")
            .Replace("<text>ICM Incidents Summary across @IncidentSelectionSummary</text>",
                "<text>ICM Incidents Summary for '@selectedOwningTeam'</text>")
            .Replace("Code Commit Trends — @(char.ToUpper(DisplayedTrendPeriod[0]) + DisplayedTrendPeriod.Substring(1)) across @SelectionSummary",
                "Code Commit Trends — @(char.ToUpper(selectedTrendPeriod[0]) + selectedTrendPeriod.Substring(1)) for '@selectedOrganization\\@selectedProject\\@selectedGitRepository\\@selectedTargetBranch' Branch")
            .Replace("                var detailVersion = analysisOperationVersion;\n", "")
            .Replace("if (detailVersion == analysisOperationVersion) contributorDetails", "contributorDetails")
            .Replace("                    if (detailVersion == analysisOperationVersion)\n                        contributorDetails", "                    contributorDetails")
            .Replace("foreach (var combination in SelectedWorkItemSources)", "foreach (var combination in selectedTeamCombinations ?? Enumerable.Empty<string>())")
            .Replace("foreach (var combination in SelectedGitSources)", "foreach (var combination in selectedGitCommitCombinations ?? Enumerable.Empty<string>())")
            .Replace("workItemHistoryCollectionMap.TryGetValue(workItem,", "workItemHistoryCollectionMap.TryGetValue(workItemId,")
            .Replace("GitCommitCollection gitCommitCollection, string? capturedPeriod = null", "GitCommitCollection gitCommitCollection")
            .Replace("(capturedPeriod ?? selectedTrendPeriod)", "selectedTrendPeriod");
        value = value.Replace("        var workItemHistoryCollectionMap = await GetSelectedWorkItemHistoryAsync(contributorWorkItems);",
            """
                    WorkItemQueryOptions workItemQueryOptions = GetWorkItemQueryOptions();

                    Dictionary<int, WorkItemHistoryCollection> workItemHistoryCollectionMap =  await WorkItemCompletionMetrics.GetWorkItemHistoryCollectionAsync(
                        workItemQueryOptions.OrganizationName,
                        new WorkItemDetailsCollection() { count = contributorWorkItems.Count, value = contributorWorkItems});
            """);
        value = value.Replace("        if (UsesSourceSelection && displayedSelectionResult is { } selectionResult)\n"
            + "        {\n            gitCommitCollection = await SelectionAnalysis.CommitDetailsAsync(selectionResult, contributorCommits);\n"
            + "        }\n        else if (selectedView == \"teams\")", "        if (selectedView == \"teams\")");
        value = RemoveRange(value, "        // Incident analysis specific", "            var kustoOptions = IncidentManagementKustoSettings.Value;");
        value = RemoveRange(value, "    private IncidentManagementRequest CaptureIncidentManagementRequest()",
            "    private async Task<IncidentManagementAnalysisResult> GetTeamsIncidentManagementCollection(");
        return RemoveRange(value, "        var uniqueCommits = gitCommitCollection.Value", "#if DEBUG");
    }

    private static string RemoveRange(string value, string start, string end)
    {
        var from = value.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
            return value;
        var to = value.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Require(to >= 0, $"Missing compatibility boundary {end}");
        return value.Remove(from, to - from);
    }

    private static string NormalizeLines(string value) =>
        string.Join('\n', value.Replace("\r\n", "\n").TrimStart('\uFEFF').Split('\n')
            .Select(line => line.TrimEnd()).Where(line => !string.IsNullOrWhiteSpace(line)));

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProductivityInsights.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate the application repository.");
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, $"Compatibility baseline lookup failed: {error}");
        return output;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
