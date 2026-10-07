using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.FeatureRegistry;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.Components.Pages;

public partial class AnalysisOptions
{
    [Inject] private IFeatureRegistryPullRequestCollector FeatureRegistryCollector { get; set; } = default!;
    private DateTime? featureRegistryStart;
    private DateTime? featureRegistryEnd;
    private ReportRequest? featureRegistryRequest;
    private FeatureRegistrySnapshot? featureRegistrySnapshot;
    private string? featureRegistryError;
    private bool featureRegistryLoading;
    private Guid? featureRegistryScrollRunId;
    private CancellationTokenSource? featureRegistryCancellation;
    private int featureRegistryTransitionVersion;
    private readonly Dictionary<string, FeatureRegistrySnapshot> featureRegistryCache = new();
    private IReadOnlyList<FeatureRegistryContributor> featureRegistryRows = [];
    private readonly HashSet<string> expandedFeatureRegistryRows = new(StringComparer.Ordinal);

    private sealed record FeatureRegistryContributor(string Key, string Name,
        IReadOnlyList<FeatureRegistryPullRequest> Open, IReadOnlyList<FeatureRegistryPullRequest> Completed,
        bool MembershipUnavailable = false);

    private IReadOnlyList<FeatureRegistryContributor> BuildFeatureRegistryRows(FeatureRegistrySnapshot data)
    {
        var records = data.Open.Concat(data.Completed).ToArray();
        var identities = records.Select(pr => pr.Attribution).Distinct().ToDictionary(identity => identity,
            identity => data.Request.IsTeams
                ? ReportIdentityMatcher.Resolve(identity, data.Request.Roster)
                    ?? throw new InvalidOperationException("PR attribution does not match the captured Feature Registry roster.")
                : identity);
        var rows = records.GroupBy(pr => Key(identities[pr.Attribution], pr))
            .Select(group => new FeatureRegistryContributor(group.Key, identities[group.First().Attribution].DisplayName,
                group.Where(pr => pr.Status == "active").ToArray(), group.Where(pr => pr.Status == "completed").ToArray()))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal).ToList();
        if (!data.Unattributed.IsEmpty)
            rows.Add(new("membership-unavailable", "Membership unavailable",
                data.Unattributed.Where(pr => pr.Status == "active").ToArray(),
                data.Unattributed.Where(pr => pr.Status == "completed").ToArray(), true));
        return rows;

        static string Key(ReportIdentity identity, FeatureRegistryPullRequest pr)
        {
            if (!string.IsNullOrWhiteSpace(identity.Id)) return "id:" + identity.Id.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(identity.Email)) return "email:" + identity.Email.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(identity.PrincipalName)) return "principal:" + identity.PrincipalName.Trim().ToUpperInvariant();
            return "pr:" + pr.EntityKey;
        }
    }

    private void ToggleFeatureRegistryRow(string key)
    {
        if (!expandedFeatureRegistryRows.Add(key)) expandedFeatureRegistryRows.Remove(key);
    }

    private static string FeatureRegistryDate(DateTimeOffset? date) =>
        date?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-";

    public AnalysisOptions()
    {
        featureRegistryStart = startDateTime;
        featureRegistryEnd = endDateTime;
    }

    private bool IsFeatureRegistrySelected => selectedOption == FeatureRegistrySource.Analysis;
    private bool IsDisplayedAnalysisLoading => IsFeatureRegistrySelected ? featureRegistryLoading : isLoadingResults;

    private string? FeatureRegistryValidationError
    {
        get
        {
            if (TeamsManagerWarning is { } warning)
                return warning;
            var now = DateTime.UtcNow;
            return (featureRegistryStart ?? now.AddDays(-30)) > (featureRegistryEnd ?? now)
                ? "The completed-PR start time must not be later than the end time." : null;
        }
    }

    private async Task ShowFeatureRegistryConfirmationAsync()
    {
        if (FeatureRegistryValidationError is not null) return;
        analysisParameters = BuildFeatureRegistryParametersHtml();
        await JSRuntime.InvokeVoidAsync("showModal", "analysisModal");
    }

    private string BuildFeatureRegistryParametersHtml()
    {
        var html = new System.Text.StringBuilder();
        html.Append("<div class='parameter-section'>");
        html.Append("<h6 class='fw-bold text-primary mb-3'>Analysis Type: Feature Registry Analysis</h6>");
        html.Append("<table class='table table-sm table-bordered'><tbody>");
        var firstRow = true;
        if (selectedView == "teams") Row("Manager Filter", selectedManager?.displayName ?? "None selected");
        Row("Organization", "msdata");
        Row("Project", "A365");
        Row("Git Repository", FeatureRegistrySource.Repository);
        Row("Target Branch", "All target branches");
        Row("Open PRs", "All open PRs, including drafts, regardless of creation date.");
        Row("Completed PRs", "Completed within the selected date range (inclusive, UTC wall-clock).");
        if (selectedView == "teams")
            Row("Attribution", "Open PRs: roster authors. Completed PRs: recorded completion actors (closedBy).");
        Row("Start Date Time", featureRegistryStart?.ToString("yyyy-MM-dd HH:mm") ?? "Not set");
        Row("End Date Time", featureRegistryEnd?.ToString("yyyy-MM-dd HH:mm") ?? "Not set");
        if (!featureRegistryStart.HasValue || !featureRegistryEnd.HasValue)
            Row("Date Defaults", "Blank start: 30 days before execution. Blank end: execution time.");
        html.Append("</tbody></table></div>");
        return html.ToString();

        void Row(string label, string value)
        {
            html.Append(firstRow ? "<tr><td class='fw-bold' style='width: 200px;'>" : "<tr><td class='fw-bold'>");
            firstRow = false;
            html.Append(WebUtility.HtmlEncode(label));
            html.Append("</td><td>");
            html.Append(WebUtility.HtmlEncode(value));
            html.Append("</td></tr>");
        }
    }

    private async Task ConfirmFeatureRegistryAnalysisAsync()
    {
        var view = selectedView;
        var version = analysisOperationVersion;
        await JSRuntime.InvokeVoidAsync("hideModal", "analysisModal");
        if (reportDisposed || !IsFeatureRegistrySelected || selectedView != view || analysisOperationVersion != version) return;
        await RunFeatureRegistryAnalysisAsync();
    }

    private async Task ScrollFeatureRegistryResultsAsync()
    {
        var runId = featureRegistryScrollRunId;
        featureRegistryScrollRunId = null;
        if (runId is null || reportDisposed || featureRegistryRequest is not { } request
            || request.RunId != runId || !IsFeatureRegistrySelected || selectedView != request.View)
            return;
        try
        {
            await JSRuntime.InvokeVoidAsync("scrollToBottom", reportLifetime.Token, true);
        }
        catch (JSDisconnectedException)
        {
            ReportExportLogger.LogDebug("Feature Registry result scrolling stopped after circuit disconnect");
        }
        catch (OperationCanceledException exception)
        {
            if (reportLifetime.IsCancellationRequested)
                ReportExportLogger.LogDebug("Feature Registry result scrolling cancelled");
            else
                ReportExportLogger.LogWarning(exception, "Feature Registry result scrolling timed out or was cancelled");
        }
        catch (JSException exception)
        {
            ReportExportLogger.LogWarning(exception, "Unable to scroll to Feature Registry results");
        }
    }

    private async Task RunFeatureRegistryAnalysisAsync()
    {
        if (FeatureRegistryValidationError is not null) return;
        ReportRequest request;
        try
        {
            request = CaptureReportRequest();
            FeatureRegistrySource.Validate(request);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            ReportExportLogger.LogWarning(exception, "Invalid Feature Registry request");
            CloseAnalysisResults();
            var failedKey = GetCacheKey(FeatureRegistrySource.Analysis, selectedView);
            featureRegistryCache.Remove(failedKey);
            cachedExportRuns.Remove(failedKey);
            featureRegistryError = exception.Message;
            return;
        }
        CloseAnalysisResults();
        var version = ++analysisOperationVersion;
        featureRegistryTransitionVersion = version;
        var key = GetCacheKey(request.Analysis, request.View);
        featureRegistryCache.Remove(key);
        cachedExportRuns.Remove(key);
        featureRegistryRequest = request;
        featureRegistrySnapshot = null;
        featureRegistryError = null;
        reportExportError = null;
        reportPickerReady = false;
        var run = new ExportRun(request, version);
        displayedExportRun = run;
        analysisResultHtml = FeatureRegistrySource.Analysis;
        featureRegistryLoading = true;
        featureRegistryScrollRunId = request.RunId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(reportLifetime.Token);
        featureRegistryCancellation = cancellation;
        StateHasChanged();
        try
        {
            var data = await Task.Run(() => FeatureRegistryCollector.CollectAsync(request, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (data.Request != request)
                throw new InvalidOperationException("Feature Registry returned a snapshot for a different run.");
            featureRegistryRows = BuildFeatureRegistryRows(data);
            featureRegistrySnapshot = data;
            run.Snapshot = FeatureRegistryReportPresentation.Snapshot(data);
            run.Completed = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ReportExportLogger.LogDebug("Feature Registry analysis cancelled");
        }
        catch (Exception exception)
        {
            ReportExportLogger.LogError(exception, "Feature Registry PR collection failed");
            if (Current()) featureRegistryError = $"Feature Registry PR collection failed: {exception.Message} No partial PR snapshot was published. Run Analysis to retry.";
        }
        finally
        {
            if (ReferenceEquals(featureRegistryCancellation, cancellation)) featureRegistryCancellation = null;
            if (Current())
            {
                featureRegistryLoading = false;
                StateHasChanged();
            }
        }

        bool Current() => !reportDisposed && !cancellation.IsCancellationRequested && analysisOperationVersion == version
            && ReferenceEquals(featureRegistryRequest, request) && ReferenceEquals(displayedExportRun, run)
            && IsFeatureRegistrySelected && selectedView == request.View;
    }

    private bool CloseFeatureRegistryResults()
    {
        if (featureRegistryRequest is not { } request) return false;
        featureRegistryCancellation?.Cancel();
        var successful = featureRegistrySnapshot is not null;
        if (successful) featureRegistryCache[GetCacheKey(request.Analysis, request.View)] = featureRegistrySnapshot!;
        HideReportExportContext(successful);
        featureRegistryRequest = null;
        featureRegistryScrollRunId = null;
        featureRegistrySnapshot = null;
        featureRegistryRows = [];
        expandedFeatureRegistryRows.Clear();
        featureRegistryError = null;
        featureRegistryLoading = false;
        analysisResultHtml = "";
        return true;
    }

    private bool RestoreFeatureRegistryResults(string option, string view)
    {
        if (option != FeatureRegistrySource.Analysis) return false;
        featureRegistryError = null;
        if (featureRegistryCache.TryGetValue(GetCacheKey(option, view), out var snapshot))
        {
            featureRegistryRequest = snapshot.Request;
            featureRegistrySnapshot = snapshot;
            featureRegistryRows = BuildFeatureRegistryRows(snapshot);
            analysisResultHtml = option;
            RestoreReportExportContext(GetCacheKey(option, view));
        }
        return true;
    }

    private void EnterFeatureRegistryTab(string option)
    {
        if (option == FeatureRegistrySource.Analysis)
        {
            featureRegistryTransitionVersion = analysisOperationVersion;
            isLoadingResults = false;
        }
    }

    private bool SupersededByFeatureRegistry(int version) => version < featureRegistryTransitionVersion;

    private Task PublishOrdinaryAnalysisAsync(int version, Action publish) => InvokeAsync(() =>
    {
        if (!SupersededByFeatureRegistry(version)) publish();
    });
}
