using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Models.Teams;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.Components.Pages;

public partial class AnalysisOptions
{
    [Inject] private IReportCollector ReportCollector { get; set; } = default!;
    [Inject] private ILogger<AnalysisOptions> ReportExportLogger { get; set; } = default!;
    [Inject] private IWebHostEnvironment ReportHostEnvironment { get; set; } = default!;

    private sealed class ExportRun(ReportRequest request, int version)
    {
        public ReportRequest Request { get; } = request;
        public int Version { get; } = version;
        public bool Completed { get; set; }
        public ReportSnapshot? Snapshot { get; set; }
    }

    private readonly Dictionary<string, ExportRun> cachedExportRuns = new();
    private readonly CancellationTokenSource reportLifetime = new();
    private readonly string reportBindingId = Guid.NewGuid().ToString("N");
    private ExportRun? displayedExportRun;
    private ElementReference reportExportButton;
    private DotNetObjectReference<AnalysisOptions>? reportCallback;
    private bool reportPickerReady;
    private bool reportPickerInitializationFailed;
    private CancellationTokenSource? reportCancellation;
    private Task<IJSObjectReference>? reportModuleTask;
    private IJSObjectReference? reportModule;
    private string? reportOperation;
    private bool reportDisposed;
    private bool isExportingReports;
    private string? reportExportError;
    private string reportExportProgress = "";

    private bool CanExportReports => !reportDisposed && !IsDisplayedAnalysisLoading && !isExportingReports
        && !string.IsNullOrEmpty(analysisResultHtml)
        && displayedExportRun is { Completed: true } run
        && run.Request.Analysis == selectedOption && run.Request.View == selectedView
        && (selectedView != "trends" || selectedOption == "gitCommits" || IsFeatureRegistrySelected);

    private string SuggestedReportFileName => CanExportReports && displayedExportRun is { } run
        ? ReportArchiveBuilder.GetFileName(run.Request) : "";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await ScrollFeatureRegistryResultsAsync();
        if (reportDisposed || reportPickerInitializationFailed || string.IsNullOrEmpty(analysisResultHtml))
            return;
        try
        {
            reportModuleTask ??= ImportReportModuleAsync();
            reportModule = await reportModuleTask;
            if (reportDisposed || string.IsNullOrEmpty(analysisResultHtml))
                return;
            reportCallback ??= DotNetObjectReference.Create(this);
            var bound = await reportModule.InvokeAsync<bool>("bindExportButton", reportLifetime.Token,
                reportBindingId, reportExportButton, reportCallback);
            if (!reportDisposed && bound && !reportPickerReady)
            {
                reportPickerReady = true;
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (reportLifetime.IsCancellationRequested)
        {
            ReportExportLogger.LogDebug("Report save dialog initialization cancelled");
        }
        catch (JSDisconnectedException)
        {
            ReportExportLogger.LogDebug("Report save dialog initialization stopped after circuit disconnect");
        }
        catch (Exception exception)
        {
            ReportExportLogger.LogError(exception, "Report save dialog initialization failed");
            if (!reportDisposed)
            {
                reportPickerInitializationFailed = true;
                reportExportError = $"The report save dialog could not be initialized: {exception.Message} Reload the page to retry.";
                StateHasChanged();
            }
        }
    }

    private async Task<IJSObjectReference> ImportReportModuleAsync()
    {
        var file = ReportHostEnvironment.WebRootFileProvider.GetFileInfo("reportExport.module.js");
        if (!file.Exists || file.IsDirectory)
            throw new InvalidOperationException("The report export script is missing from the deployed web assets.");

        await using var stream = file.CreateReadStream();
        var hash = await SHA256.HashDataAsync(stream, reportLifetime.Token);
        // ES modules stay cached for the document lifetime, even across Blazor circuit reconnects.
        var moduleUrl = $"./reportExport.module.js?v={Convert.ToHexString(hash)}";
        return await JSRuntime.InvokeAsync<IJSObjectReference>("import", reportLifetime.Token, moduleUrl);
    }

    private void BeginReportExportContext(int version)
    {
        CancelReportExport();
        reportPickerReady = false;
        cachedExportRuns.Remove(GetCacheKey(selectedOption, selectedView));
        displayedExportRun = null;
        reportExportError = null;
        try
        {
            displayedExportRun = new ExportRun(CaptureReportRequest(), version);
        }
        catch (Exception exception)
        {
            ReportExportLogger.LogError(exception, "Unable to capture report export context");
            reportExportError = $"Export context could not be captured: {exception.Message}";
        }
    }

    private ReportRequest CaptureReportRequest()
    {
        if (selectedView == "teams" && (orgTreeRoot is null || isLoadingOrgTree))
            throw new InvalidOperationException("Fetch the complete manager roster before confirming an analysis for export.");
        var now = DateTimeOffset.UtcNow;
        var sources = ImmutableArray.CreateBuilder<ReportSource>();
        void AddSource(string organization, string project, string scope, string? branch = null, string? team = null)
        {
            var key = JsonSerializer.Serialize(new[] { organization, project, scope, branch, team });
            if (!sources.Any(source => string.Equals(source.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new ReportSource(key, organization, project, scope, branch, team));
            }
        }

        if (selectedOption == "workItems")
        {
            foreach (var (org, project, team, area) in ParseSelectedTeamCombinations())
                AddSource(org, project, area, team: team);
        }
        else if (selectedOption == "gitCommits")
        {
            foreach (var (org, project, repo, branch) in ParseSelectedGitCommitCombinations())
                AddSource(org, project, repo, branch);
        }
        else if (IsFeatureRegistrySelected)
        {
            AddSource("msdata", "A365", FeatureRegistrySource.Repository);
        }
        else
        {
            foreach (var team in SelectedIncidentSources)
                AddSource("", "", team);
        }

        var roster = ImmutableArray.CreateBuilder<ReportIdentity>();
        var rosterParents = ImmutableArray.CreateBuilder<int?>();
        if (selectedView == "teams" && orgTreeRoot is not null)
        {
            var pending = new Stack<(OrgMember Node, int? Parent, int Depth)>();
            var visited = new HashSet<OrgMember>(ReferenceEqualityComparer.Instance);
            pending.Push((orgTreeRoot, null, 0));
            while (pending.TryPop(out var entry))
            {
                reportLifetime.Token.ThrowIfCancellationRequested();
                if (!visited.Add(entry.Node))
                    throw new InvalidOperationException("The displayed reporting hierarchy contains a repeated node or cycle.");
                if (roster.Count >= ReportRenderingLimits.MaximumIdentities
                    || entry.Depth > ReportRenderingLimits.MaximumRosterDepth
                    || (long)roster.Count + pending.Count + entry.Node.Reports.Count + 1 > ReportRenderingLimits.MaximumIdentities)
                    throw new ReportSizeLimitException("The displayed reporting hierarchy exceeds the export identity/depth limits. No members were omitted.");
                var index = roster.Count;
                roster.Add(new ReportIdentity(entry.Node.DisplayName ?? "", entry.Node.Mail, entry.Node.Id, entry.Node.UserPrincipalName));
                rosterParents.Add(entry.Parent);
                for (var child = entry.Node.Reports.Count - 1; child >= 0; child--)
                    pending.Push((entry.Node.Reports[child], index, entry.Depth + 1));
            }
        }

        var requestedStart = IsFeatureRegistrySelected ? featureRegistryStart : startDateTime;
        var requestedEnd = IsFeatureRegistrySelected ? featureRegistryEnd : endDateTime;
        var start = requestedStart ?? (selectedOption == "incidentManagement" ? DateTime.UnixEpoch : now.UtcDateTime.AddDays(-30));
        var end = requestedEnd ?? now.UtcDateTime;
        if (selectedOption == "gitCommits" || IsFeatureRegistrySelected)
        {
            start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerMillisecond));
            end = end.AddTicks(-(end.Ticks % TimeSpan.TicksPerMillisecond));
        }
        var dateFormat = selectedOption == "workItems" ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        var startLiteral = selectedOption == "incidentManagement"
            ? DateTime.SpecifyKind(start, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
            : start.ToString(dateFormat, CultureInfo.CurrentCulture);
        var endLiteral = selectedOption == "incidentManagement"
            ? DateTime.SpecifyKind(end, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
            : end.ToString(dateFormat, CultureInfo.CurrentCulture);
        var boundsOrigin = "Explicit inputs captured at confirmation; original wall-clock fields are retained without time-zone conversion.";
        if (!requestedStart.HasValue || !requestedEnd.HasValue)
        {
            var defaults = new List<string>();
            if (!requestedStart.HasValue)
                defaults.Add(selectedOption == "incidentManagement"
                    ? "Missing start uses the fixed UnixEpoch default."
                    : "Missing start uses the 30-day export default anchored at confirmation.");
            if (!requestedEnd.HasValue)
                defaults.Add("Missing end uses the export capture timestamp.");
            defaults.Add(IsFeatureRegistrySelected
                ? "Feature Registry uses these captured defaults for both analysis and export."
                : "Unchanged analysis APIs evaluate time-dependent defaults independently; capture does not observe their earlier calls.");
            boundsOrigin = string.Join(" ", defaults);
        }
        return new ReportRequest
        {
            RunId = Guid.NewGuid(),
            Analysis = selectedOption,
            View = selectedView,
            ExecutedAt = now,
            RequestedStart = requestedStart,
            RequestedEnd = requestedEnd,
            Start = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)),
            End = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)),
            TimeZone = selectedOption == "workItems" ? "Date-only WIQL literals (no time-zone conversion)" : "UTC wall-clock (legacy API semantics)",
            EffectiveStartLiteral = startLiteral,
            EffectiveEndLiteral = endLiteral,
            EffectiveBoundsOrigin = boundsOrigin,
            Sources = sources.ToImmutable(),
            Roster = roster.ToImmutable(),
            RosterParentIndices = selectedOption is "incidentManagement" or "gitCommits" or "workItems" or "featureRegistry" ? rosterParents.ToImmutable() : [],
            Manager = selectedView == "teams" ? selectedManager?.displayName : null,
            TrendPeriod = IsFeatureRegistrySelected ? "daily" : selectedTrendPeriod
        };
    }

    private void CompleteReportExportContext(int version, bool succeeded = true)
    {
        if (reportDisposed || displayedExportRun is not { } run || run.Version != version
            || analysisOperationVersion != version
            || run.Request.Analysis != selectedOption || run.Request.View != selectedView)
            return;

        run.Completed = succeeded;
        if (!succeeded)
            reportExportError = "Export requires a completed analysis. Resolve the analysis error and run it again.";
    }

    private void HideReportExportContext(bool cache)
    {
        CancelReportExport();
        reportPickerReady = false;
        if (cache && displayedExportRun is { } run)
            cachedExportRuns[GetCacheKey(run.Request.Analysis, run.Request.View)] = run;
        displayedExportRun = null;
        reportExportError = null;
    }

    private void RestoreReportExportContext(string cacheKey)
    {
        cachedExportRuns.TryGetValue(cacheKey, out displayedExportRun);
        reportExportError = displayedExportRun is null
            ? "Run this analysis again to capture its export context."
            : null;
    }

    private void CancelReportExport()
    {
        var operation = reportOperation;
        reportOperation = null;
        reportCancellation?.Cancel();
        reportCancellation = null;
        isExportingReports = false;
        if (operation is not null && reportModule is not null)
            _ = CancelReportDownloadAsync(reportModule, operation);
    }

    private async Task CancelReportDownloadAsync(IJSObjectReference module, string operation)
    {
        try
        {
            await module.InvokeVoidAsync("cancelDownload", CancellationToken.None, operation);
        }
        catch (JSDisconnectedException)
        {
            ReportExportLogger.LogDebug("Report download cancellation after circuit disconnect");
        }
        catch (JSException exception)
        {
            ReportExportLogger.LogWarning(exception, "Unable to invalidate report download");
        }
        catch (ObjectDisposedException)
        {
            ReportExportLogger.LogDebug("Report download was already disposed");
        }
    }

    private bool IsCurrentExport(ExportRun run, string operation) =>
        !reportDisposed && ReferenceEquals(displayedExportRun, run) && reportOperation == operation;

    [JSInvokable]
    public async Task ExportReportsToFileAsync(string runId, string operation)
    {
        if (!CanExportReports || displayedExportRun is not { } run
            || run.Request.RunId.ToString("N") != runId || !Guid.TryParseExact(operation, "D", out _))
        {
            ReportExportLogger.LogDebug("Ignoring a stale or invalid report save request");
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(reportLifetime.Token);
        var token = cancellation.Token;
        reportCancellation = cancellation;
        reportOperation = operation;
        isExportingReports = true;
        reportExportError = null;
        reportExportProgress = "Choose the folder and ZIP filename in the Save As dialog...";
        StateHasChanged();

        var progress = new Progress<string>(message =>
        {
            if (!IsCurrentExport(run, operation))
                return;
            reportExportProgress = message;
            StateHasChanged();
        });

        try
        {
            if (reportModule is null)
                throw new InvalidOperationException("The report save dialog is not initialized. Reload the page and retry.");
            var selected = await reportModule.InvokeAsync<bool>("waitForDestination", token, operation);
            token.ThrowIfCancellationRequested();
            if (!selected || !IsCurrentExport(run, operation))
                return;

            reportExportProgress = run.Request.Analysis == "incidentManagement" && run.Request.IsTeams
                ? "Collecting available incident report data..." : "Collecting complete report data...";
            if (run.Request.Analysis == FeatureRegistrySource.Analysis)
            {
                reportExportProgress = "Preparing the captured Feature Registry PR snapshot...";
                if (run.Snapshot is null)
                    throw new InvalidOperationException("Run Feature Registry analysis again; its PR snapshot is unavailable and export will not requery.");
            }
            StateHasChanged();
            var snapshot = run.Snapshot ?? await Task.Run(
                () => ReportCollector.CollectAsync(run.Request, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentExport(run, operation))
                return;
            if (snapshot.Request.RunId != run.Request.RunId)
                throw new InvalidOperationException("Report collection returned a different analysis run.");
            foreach (var source in snapshot.SkippedSources)
                ReportExportLogger.LogWarning("Report export skipped source {SourceKey}: {Reason}", source.SourceKey, source.Reason);

            reportExportProgress = "Preparing Markdown, HTML, and ZIP...";
            StateHasChanged();
            var archive = await Task.Run(() => ReportArchiveBuilder.Build(snapshot, token), token);
            using var content = archive.Content;
            token.ThrowIfCancellationRequested();
            if (!IsCurrentExport(run, operation))
                return;

            run.Snapshot = snapshot.SkippedSources.IsEmpty ? snapshot : null;
            reportExportProgress = "Saving report ZIP to the selected file...";
            StateHasChanged();
            using var streamReference = new DotNetStreamReference(content, leaveOpen: true);
            // Keep the stream alive until JS settles, including cancellation during transfer or writing.
            var saved = await reportModule.InvokeAsync<bool>("saveReport", CancellationToken.None,
                streamReference, operation);
            if (!saved && IsCurrentExport(run, operation) && !token.IsCancellationRequested)
                throw new InvalidOperationException("The browser invalidated the selected report destination before saving.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || reportLifetime.IsCancellationRequested)
        {
            ReportExportLogger.LogDebug("Report export cancelled");
        }
        catch (JSDisconnectedException)
        {
            ReportExportLogger.LogDebug("Report export stopped after circuit disconnect");
        }
        catch (Exception exception)
        {
            if (reportModuleTask is { IsFaulted: true } or { IsCanceled: true })
                reportModuleTask = null;
            ReportExportLogger.LogError(exception, "Report export failed for run {RunId}", run.Request.RunId);
            if (IsCurrentExport(run, operation))
                reportExportError = $"Export failed: {exception.Message} The on-screen analysis is unchanged.";
        }
        finally
        {
            if (reportModule is not null)
                await CancelReportDownloadAsync(reportModule, operation);
            if (IsCurrentExport(run, operation))
            {
                reportOperation = null;
                reportCancellation = null;
                isExportingReports = false;
                StateHasChanged();
            }
        }
    }

    private async ValueTask DisposeReportExportAsync()
    {
        reportDisposed = true;
        reportLifetime.Cancel();
        CancelReportExport();
        cachedExportRuns.Clear();
        displayedExportRun = null;
        if (reportModuleTask is not null)
        {
            try
            {
                var module = await reportModuleTask;
                await module.InvokeVoidAsync("disposeBinding", CancellationToken.None, reportBindingId);
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                ReportExportLogger.LogDebug("Report module disposal after circuit disconnect");
            }
            catch (OperationCanceledException) when (reportLifetime.IsCancellationRequested)
            {
                ReportExportLogger.LogDebug("Report module import cancelled during disposal");
            }
            catch (JSException exception)
            {
                ReportExportLogger.LogWarning(exception, "Report module disposal failed");
            }
        }
        reportCallback?.Dispose();
        reportLifetime.Dispose();
    }
}
