using ProductivityInsights.Components.Pages;
using ProductivityInsights.Models.Incidents;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Analysis;
using static ProductivityInsights.ReportTests.CompatibilityTests;
using static ProductivityInsights.ReportTests.ExportIntegrationTests;

namespace ProductivityInsights.ReportTests;

public static class IncidentSelectionTests
{
    private const string First = @"AZURESYNAPSEJOBSERVICE\JobPlatform";
    private const string Second = @"HDINSIGHT\ControlPlane";

    public static async Task RunAsync()
    {
        var client = new FixtureClient { SameIdDifferentSilo = true };
        var service = new IncidentSelectionService(client);
        var start = new DateTime(2026, 7, 1);
        var end = start.AddDays(30);
        var collected = await service.CollectAsync([First, First.ToLowerInvariant(), Second, "overlapping-path"], start, end, default);
        Require(client.Calls.Count == 12 && collected.Resolved.value!.Count == 2 && collected.Active.value!.Count == 2,
            "Collect all four categories for each distinct selected path, deduplicate overlap, and preserve equal incident IDs in different silos.");
        Require(client.Requests.All(request => request.Start == start && request.End == end),
            "Every source/category must use the same captured date bounds.");
        foreach (var kind in Enum.GetValues<IncidentSelectionKind>())
        {
            client.Failure = kind;
            try
            {
                await service.CollectAsync([First, Second], start, end, default);
                throw new Exception("Incomplete incident data was published.");
            }
            catch (InvalidOperationException error)
            {
                Require(error.Message.Contains(Second) && error.Message.Contains(kind.ToString()),
                    "Any unavailable category must fail the run explicitly instead of claiming zero incidents.");
            }
        }
        client.Failure = null;
        client.Empty = true;
        var empty = await service.CollectAsync([First, Second], start, end, default);
        Require(empty.Resolved.value!.Count == 0 && empty.Mitigated.value!.Count == 0
            && empty.Active.value!.Count == 0 && empty.Created.value!.Count == 0, "Available empty responses must remain valid.");
        client.Empty = false;
        client.DelayMilliseconds = 100;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(20);
        try
        {
            await service.CollectAsync([First, Second], start, end, cancellation.Token);
            throw new Exception("Incident selection ignored cancellation.");
        }
        catch (OperationCanceledException) { }
        await VerifyViews();
    }

    private static async Task VerifyViews()
    {
        var component = PreparedComponent("incidentManagement");
        Set(component, "IncidentManagementKustoSettings", Microsoft.Extensions.Options.Options.Create(new IncidentManagementKustoOptions()));
        Set(component, "IncidentSelection", new IncidentSelectionService(new FixtureClient()));
        var teams = ((List<string>)Get(component, "selectedOwningTeams")!).ToArray();
        foreach (var view in new[] { "details", "trends" })
        {
            Set(component, "selectedView", view);
            Require(((List<string>)Get(component, "SelectedIncidentSources")!).SequenceEqual([First]),
                "Details and Trends must retain independent default owning teams.");
            Set(component, "SelectedIncidentSources", new List<string> { First, Second });
            var confirmation = (string)Call(component, "BuildAnalysisParametersHtml")!;
            Require(confirmation.Contains("Selections (2)") && confirmation.Contains(First) && confirmation.Contains(Second)
                && !confirmation.Contains("Manager Filter"), "Confirmation must include all selected teams without manager filtering.");
            Call(component, "BeginReportExportContext", 2);
            var export = Request(component);
            var captured = Call(component, "CaptureIncidentManagementRequest")!;
            Require(((IReadOnlyList<string>)Property(captured, "OwningTeams")!).SequenceEqual([First, Second])
                && Property(captured, "ManagerIdentities") is null && export.Sources.Select(source => source.Scope).SequenceEqual([First, Second])
                && export.Roster.IsEmpty, "Captured API and export inputs must agree on both owning teams without a roster filter.");
            Set(component, "SelectedIncidentSources", new List<string>());
            Require(!(bool)Get(component, "CanRunAnalysis")! && Request(component) == export
                && ((IReadOnlyList<string>)Property(captured, "OwningTeams")!).Count == 2,
                "Clear must disable analysis without mutating the captured selection.");
            var query = (Task)Call(component, "GetIncidentManagementCollection", captured, CancellationToken.None)!;
            await query;
            var result = Property(query, "Result")!;
            var statistics = (List<(string Name, string Summary)>)Property(result, "Contributors")!;
            Require(statistics.Any(row => row.Name == "Selection Contributor" && row.Summary.StartsWith("2 incidents closed")),
                "Details and Trends must keep contributors outside the fetched manager's roster.");
            Call(component, "BeginIncidentSelection", captured);
            Set(component, "contributors", statistics);
            Set(component, "incidentManagementGroupedIncidentsByContributor", Property(result, "GroupedIncidentsByContributor"));
            Set(component, "analysisResultHtml", "incidentManagement");
            Call(component, "CloseAnalysisResults");
            Set(component, "incidentManagementGroupedIncidentsByContributor", null);
            Call(component, "RestoreCachedResults", "incidentManagement", view);
            Require(ReferenceEquals(Get(component, "incidentManagementGroupedIncidentsByContributor"), Property(result, "GroupedIncidentsByContributor"))
                && ((string)Get(component, "IncidentSelectionSummary")!).Contains(Second),
                "Cache restoration must recover the incident records and executed owning teams after input edits.");
        }
        Require(((List<string>)Get(component, "selectedOwningTeams")!).SequenceEqual(teams),
            "Details/Trends selections must not change Teams selections.");
        Set(component, "selectedView", "details");
        Require(((List<string>)Get(component, "SelectedIncidentSources")!).Count == 0,
            "A cleared Details selection must survive switching to Trends and back.");
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    internal sealed class FixtureClient(List<string>? sharedCalls = null) : IIncidentSelectionClient
    {
        public List<string> Calls { get; } = sharedCalls ?? [];
        public List<(IncidentSelectionKind Kind, string Team, DateTime? Start, DateTime? End)> Requests { get; } = [];
        public IncidentSelectionKind? Failure { get; set; }
        public bool Empty { get; set; }
        public bool SameIdDifferentSilo { get; set; }
        public int DelayMilliseconds { get; set; }
        private int completed;
        public int Completed => Volatile.Read(ref completed);

        public async Task<IncidentCollection?> SearchAsync(IncidentSelectionKind kind, string team,
            DateTime? start, DateTime? end, CancellationToken cancellationToken)
        {
            Calls.Add($"incidents:{kind}:{team}");
            Requests.Add((kind, team, start, end));
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref completed);
            if (Failure == kind && team == Second) return null;
            if (Empty || kind is IncidentSelectionKind.Mitigated or IncidentSelectionKind.Created)
                return new() { value = [] };
            var number = team == Second && !SameIdDifferentSilo ? 2 : 1;
            var created = new DateTime(2026, 7, 1);
            return new() { value = [new()
            {
                Id = (kind == IncidentSelectionKind.Active ? number + 100 : number).ToString(),
                SiloId = team == Second && SameIdDifferentSilo ? "2" : "1", Severity = 2,
                Status = kind == IncidentSelectionKind.Resolved ? "Resolved" : "Active",
                Title = "Captured incident " + team, OwningTeamName = team, OwningContactAlias = "Selection Contributor",
                CreateDate = created, ModifiedDate = created.AddDays(1), ImpactStartDate = created.AddHours(-1),
                ResolutionData = kind == IncidentSelectionKind.Resolved
                    ? new() { ChangedBy = "Selection Contributor", Date = created.AddDays(1) } : null
            }] };
        }
    }
}
