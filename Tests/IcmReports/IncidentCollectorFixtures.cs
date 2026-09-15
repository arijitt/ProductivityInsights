using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.IcmReports;

public static class IncidentCollectorFixtures
{
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-01T00:00:00Z", CultureInfo.InvariantCulture);
    internal static readonly DateTimeOffset End = Start.AddDays(30);
    internal static readonly DateTimeOffset AsOf = End.AddDays(2);
    internal static readonly IncidentManagementKustoOptions Configuration = new()
    {
        ClusterUri = "https://synthetic.kusto.windows.net", Database = "SyntheticIcm", QueryTimeoutSeconds = 30
    };
    internal static readonly ImmutableArray<ReportIdentity> Roster =
        [new("Alice Example", "alice@example.test"), new("Bob Example", "bob@example.test")];

    public static async Task RunAsync()
    {
        var fake = new SourceFake();
        fake.Incidents.AddRange([
            Incident(1, "Active", Start.AddDays(-30)),
            Incident(2, "Resolved", Start, mitigated: Start.AddDays(1), resolved: End, owner: "charlie", resolver: "bob"),
            Incident(3, "Investigating", Start.AddDays(-3)),
            Incident(4, "Mitigated", Start.AddDays(-2), mitigated: End, owner: "bob", mitigator: "alice"),
            Incident(5, "Investigating", End),
            Incident(6, "Active", Start.AddDays(-1), team: "Other"),
            Incident(7, "Active", Start.AddDays(-1), silo: 2),
            Incident(8, "Active", Start.AddDays(-1)),
            Incident(9, "Active", Start.AddDays(-1)),
            Incident(10, "Active", Start.AddDays(-1), modified: Start),
            Incident(10, "Resolved", Start.AddDays(-1), team: "Other", modified: End),
            Incident(11, "Active", Start.AddDays(-1)).SetItem("IsPurged", JsonSerializer.SerializeToElement(true))
        ]);
        fake.PurgedIncidents.Add(8);
        fake.Actions.AddRange([
            Action(1, 100, "alice", Start),
            Action(1, 101, "alice", End),
            Action(1, 102, "bob", Start.AddDays(2)),
            Action(3, 103, "alice", Start),
            Action(3, 104, "alice", End.AddTicks(1)),
            Action(3, 105, "alice", Start.AddTicks(-1)),
            Action(8, 106, "alice", Start),
            Action(9, 107, "alice", Start),
            Action(7, 108, "alice", Start),
            Action(10, 109, "alice", Start),
            Action(3, 110, "charlie", Start)
        ]);
        fake.PurgedHistory.Add(107);
        var request = Request() with { Sources = [new("first", "", "", "Team"), new("overlap", "", "", " Team ")] };
        using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
        var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(snapshot.AsOf == AsOf && ReferenceEquals(snapshot.Request, request), "Captured context/as-of changed.");
        Check(snapshot.Incidents[0].Active, "Active group must be first.");
        Check(snapshot.Incidents.Where(record => record.Active).All(record => !record.Created), "Active must not imply created in window.");
        Check(snapshot.Incidents.Select(record => record.Id).Distinct().Order().SequenceEqual(new[] { "1", "2", "3", "4", "5", "9" }),
            "Scope, latest ownership, IsPurged or incident purge exclusion failed.");
        Check(snapshot.Incidents.Count(record => record.Id == "3") == 2, "Activity-only incident omitted or source membership lost.");
        var resolved = snapshot.Incidents.First(record => record.Id == "2");
        Check(resolved.Resolved && !resolved.Mitigated && resolved.Created, "Current-state or date boundary attribution changed.");
        Check(resolved.Owner.Name == "charlie" && resolved.ResolvedBy.Name == "bob", "Created must use owner; resolved must use resolution actor.");
        Check((resolved.MitigatedAt - resolved.CreatedAt)!.Value.TotalHours == 24
            && (resolved.ResolvedAt - resolved.CreatedAt)!.Value.TotalHours == 720,
            "Mitigation/resolution durations no longer preserve UI hours.");
        Check((resolved.CreatedAt - resolved.ImpactStart)!.Value.TotalHours == 2,
            "Time to detect must use impact-to-create hours.");
        Check(snapshot.Incidents.First(record => record.Id == "4").Mitigated, "Inclusive mitigation upper bound lost.");
        Check(snapshot.Incidents.First(record => record.Id == "5").Created, "Inclusive created upper bound lost.");
        Check(snapshot.Incidents.All(record => record.Metadata.ContainsKey("IsOutage")
            && record.Metadata.ContainsKey("IsNoise") && record.Metadata.ContainsKey("SourceOrigin")
            && record.OwningTenant == "Synthetic tenant" && record.Mitigation == "Synthetic mitigation\nSecond line"),
            "Visible incident metadata was omitted.");
        Check(snapshot.IncidentActions.Length == 10, "Actions lost, duplicated or incorrectly purged/windowed.");
        Check(snapshot.IncidentActions.Where(action => action.SourceKey == "first" && action.Actor.Name == "alice")
            .Select(action => action.IncidentKey).Distinct().Count() == 2, "Contributed must count incidents, not actions.");
        Check(snapshot.IncidentActions.All(action => action.ActionId is not ("104" or "105" or "106" or "107" or "108" or "109")),
            "Purge, team, silo or window exclusions failed.");

        var teams = await collector.CollectAsync(request with { View = "teams", Roster = Roster }, null, CancellationToken.None);
        Check(teams.IncidentActions.All(action => action.Actor.Name is "alice" or "bob"),
            "Recorded action aliases were replaced or outside actors were not excluded.");
        Check(teams.Incidents.First(record => record.Id == "4").MitigatedBy.Name == "alice",
            "Recorded mitigation actor was replaced by a roster display name.");
        Check(!teams.Incidents.First(record => record.Id == "2").Created
            && teams.Incidents.First(record => record.Id == "2").Resolved, "Independent metric actor filtering failed.");
        Check(teams.IncidentActions.Select(action =>
                (action.IncidentKey, Actor: ReportIdentityMatcher.Resolve(action.Actor, Roster))).Distinct().Count() == 3,
            "Several actors or overlapping selections changed distinct incident/member pairs.");

        await FailureCases();
        await UnkeyedOutsideRoster();
        await UnkeyedContributionEvidence();
        await IncidentQualifiedActionIds();
    }

    private static async Task IncidentQualifiedActionIds()
    {
        var fake = new SourceFake();
        fake.Incidents.AddRange([Incident(1, "Active", Start), Incident(2, "Active", Start)]);
        fake.Actions.AddRange([Action(1, 100, "alice", Start), Action(2, 100, "alice", End)]);
        using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
        var request = Request() with { View = "teams", Roster = Roster };
        var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(snapshot.IncidentActions.Length == 2
            && snapshot.IncidentActions.Select(action => action.IncidentKey).Distinct().Count() == 2,
            "HistoryId reuse across different incidents must not conflict or collapse contributor credits.");
        using var archive = ReportArchiveBuilder.Build(snapshot).Content;
        Check(archive.Length > 0, "Incident-qualified actions did not survive report generation.");
        fake.PurgedHistory.Add(100);
        var purged = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(purged.IncidentActions.IsEmpty, "The existing HistoryId-based purge feed must still exclude matching rows.");
    }

    private static async Task UnkeyedOutsideRoster()
    {
        var fake = new SourceFake { Scenario = "unkeyed-outsider" };
        fake.Incidents.Add(Incident(1, "Active", Start));
        fake.Actions.Add(Action(1, 1, "alice", Start));
        using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
        var request = Request() with { View = "teams", Roster = Roster };
        var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(snapshot.IncidentActions.Length == 1 && snapshot.IncidentActions[0].ActionId == "1",
            "Unkeyed non-roster actions must not block or add contribution credit to a Teams report.");
        var included = await collector.CollectAsync(request with { Roster = [new("charlie")] }, null, CancellationToken.None);
        Check(included.IncidentActions.Length == 1 && included.IncidentActions[0].ActionId == "",
            "An in-scope action must retain its evidence without inventing a missing HistoryId.");
        await ExpectFailure(() => collector.CollectAsync(Request(), null, CancellationToken.None),
            "Details must not bypass its history identity requirement.");
    }

    private static async Task UnkeyedContributionEvidence()
    {
        var fake = new SourceFake();
        fake.Incidents.AddRange([
            Incident(1, "Investigating", Start.AddDays(-2)),
            Incident(2, "Investigating", Start.AddDays(-2))
        ]);
        var unkeyed = Action(1, null, "alice", Start);
        fake.Actions.AddRange([
            unkeyed, unkeyed,
            unkeyed.SetItem("ChangeCategories", JsonSerializer.SerializeToElement("Status update")),
            Action(1, null, "alice", End), Action(1, null, "bob", Start),
            Action(2, null, "alice", Start), Action(1, 7, "alice", Start)
        ]);
        using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
        var request = Request() with
        {
            View = "teams", Roster = Roster,
            Sources = [new("first", "", "", "Team"), new("overlap", "", "", " Team ")]
        };
        var snapshot = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(snapshot.IncidentActions.Length == 12 && snapshot.IncidentActions.Count(action => action.ActionId == "") == 10,
            "Deduplicate only identical unkeyed evidence; preserve actor/time/type/incident differences and known IDs.");
        var document = ReportDocumentBuilder.Build(snapshot);
        Check(document.IncidentActions.Length == 6 && document.IncidentActions.All(action => action.SourceKeys.Length == 2)
            && document.IncidentActions.Count(action => action.Record.ActionId == "") == 5
            && document.OverallRows.Select(row => row.Contributed).Order().SequenceEqual(new[] { 1, 2 }),
            "Unkeyed actions must retain source membership and distinct incident/member contribution credits.");
        var markdown = MarkdownReportWriter.Write(document);
        var html = HtmlSummaryReportWriter.Write(document, "unkeyed.md");
        Check(markdown.Contains("Not provided", StringComparison.Ordinal)
            && markdown.Contains("no HistoryId is invented", StringComparison.Ordinal)
            && html.Contains("no HistoryId is invented", StringComparison.Ordinal),
            "Both reports must disclose missing IDs without fabricating identifiers.");
        using var archive = ReportArchiveBuilder.Build(snapshot).Content;
        Check(archive.Length > 0, "Unkeyed contribution evidence did not export.");
        fake.PurgedIncidents.Add(1);
        var purged = await collector.CollectAsync(request, null, CancellationToken.None);
        Check(purged.IncidentActions.All(action => action.IncidentId == "2"),
            "Unkeyed history must still respect incident purge exclusions.");
    }

    private static async Task FailureCases()
    {
        foreach (var scenario in new[] { "coverage", "partial", "failure", "silo", "id", "id-column", "actor", "type", "conflict", "outsider-conflict" })
        {
            var fake = new SourceFake { Scenario = scenario };
            fake.Incidents.Add(Incident(1, "Active", Start));
            fake.Actions.Add(Action(1, 1, "alice", Start));
            using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
            await ExpectFailure(() => collector.CollectAsync(Request(), null, CancellationToken.None), scenario);
            var teamRequest = Request() with { View = "teams", Roster = Roster };
            if (scenario == "coverage")
            {
                var available = await collector.CollectAsync(teamRequest, null, CancellationToken.None);
                Check(available.Incidents.Length == 1
                    && available.CoverageNotes.Any(note => note.Contains("Available warehouse data", StringComparison.Ordinal)),
                    "Missing attestation must not block Teams or imply verified historical coverage.");
            }
            else
                await ExpectFailure(() => collector.CollectAsync(teamRequest, null, CancellationToken.None), "Teams " + scenario);
        }
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), new SourceFake(), new FixedClock());
        await ExpectCancellation(() => client.CollectAsync(Request(), null, cancelled.Token));
        foreach (var stage in new[] { "schema", "retention", "coverage", "data" })
        {
            using var source = new CancellationTokenSource();
            var fake = new SourceFake { CancellationStage = stage, Cancel = source.Cancel };
            using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
            await ExpectCancellation(() => collector.CollectAsync(Request(), null, source.Token));
        }
        using (var source = new CancellationTokenSource())
        {
            var fake = new SourceFake { CancellationStage = "data", Cancel = source.Cancel };
            using var collector = new IncidentReportCollector(Microsoft.Extensions.Options.Options.Create(Configuration), fake, new FixedClock());
            await ExpectCancellation(() => collector.CollectAsync(
                Request() with { View = "teams", Roster = Roster }, null, source.Token));
        }
        await ExpectFailure(() => client.CollectAsync(Request() with { View = "trends" }, null, CancellationToken.None), "trends");
        await ExpectFailure(() => client.CollectAsync(Request() with { View = "teams" }, null, CancellationToken.None), "missing roster");
        await ExpectFailure(() => client.CollectAsync(Request() with { Start = End.AddDays(1) }, null, CancellationToken.None), "reversed window");
    }

    internal static ReportRequest Request() => new()
    {
        RunId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        Analysis = "incidentManagement", View = "details", ExecutedAt = AsOf,
        Start = Start, End = End, TimeZone = "UTC", Sources = [new("source", "", "", "Team")]
    };

    internal static ImmutableDictionary<string, JsonElement> Row(params (string Key, object? Value)[] values) =>
        values.ToImmutableDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));

    internal static ImmutableDictionary<string, JsonElement> Incident(long id, string status, DateTimeOffset created,
        string team = "Team", long silo = 1, DateTimeOffset? modified = null, DateTimeOffset? mitigated = null,
        DateTimeOffset? resolved = null, string owner = "alice", string mitigator = "alice", string resolver = "alice") =>
        Row(("IncidentId", id), ("SiloId", silo), ("Status", status), ("CreateDate", created),
            ("ModifiedDate", modified ?? End), ("ImpactStartDate", created.AddHours(-2)), ("MitigateDate", mitigated),
            ("ResolveDate", resolved), ("Severity", 2), ("Title", "Synthetic incident"), ("OwningContactAlias", owner),
            ("OwningTenantName", "Synthetic tenant"), ("OwningTeamName", team), ("MitigatedBy", mitigator),
            ("ResolvedBy", resolver), ("Mitigation", "Synthetic mitigation\nSecond line"),
            ("IsOutage", true), ("IsNoise", false), ("SourceOrigin", "Synthetic"), ("IsPurged", false));

    internal static ImmutableDictionary<string, JsonElement> Action(long incidentId, long? id, string actor, DateTimeOffset date) =>
        Row(("IncidentId", incidentId), ("HistoryId", id), ("SiloId", 1), ("ChangeDate", date),
            ("ChangedBy", actor), ("ChangeCategories", "Synthetic update"));

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task ExpectFailure(Func<Task> action, string scenario, string? expectedMessage = null)
    {
        try { await action(); }
        catch (IncidentReportExportException exception)
        {
            Check(expectedMessage is null || exception.Message.Contains(expectedMessage, StringComparison.Ordinal),
                "Export failure did not identify its query stage: " + scenario);
            return;
        }
        throw new InvalidOperationException("Expected export-only failure: " + scenario);
    }

    internal static async Task ExpectCancellation(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancellation was swallowed.");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => AsOf;
    }

    private sealed class SourceFake : IIncidentReportQueryClient
    {
        public List<ImmutableDictionary<string, JsonElement>> Incidents { get; } = [];
        public List<ImmutableDictionary<string, JsonElement>> Actions { get; } = [];
        public HashSet<long> PurgedIncidents { get; } = [];
        public HashSet<long> PurgedHistory { get; } = [];
        public string? Scenario { get; init; }
        public string? CancellationStage { get; init; }
        public System.Action? Cancel { get; init; }

        public Task<IncidentReportCoverage?> GetCoverageAsync(IncidentReportCoverageRequest request, CancellationToken token)
        {
            CheckCancellation("coverage", token);
            return Task.FromResult<IncidentReportCoverage?>(Scenario == "coverage" ? null :
                new(request.ClusterUri, request.Database, request.SiloId, request.Teams,
                    Start, End, AsOf, AsOf, "Synthetic complete source fixture", "Synthetic event-time retention and purge watermark contract"));
        }

        public Task<IncidentReportQueryResult> ExecuteAsync(IncidentReportQuery query, CancellationToken token)
        {
            var stage = query.IsManagement ? query.Text.Contains("schema") ? "schema" : "retention" : "data";
            CheckCancellation(stage, token);
            if (query.IsManagement)
            {
                if (stage == "schema")
                {
                    var table = query.Text.Split(' ')[2];
                    var schema = JsonSerializer.Serialize(new
                    {
                        Name = table,
                        OrderedColumns = IncidentReportSourceVerification.RequiredSchema[table]
                            .Select(column => new { Name = column.Key, CslType = column.Value }).ToArray()
                    });
                    return Task.FromResult(new IncidentReportQueryResult([Row(("Schema", schema))]));
                }
                return Task.FromResult(new IncidentReportQueryResult([Row(("Policy", "null"),
                    ("EffectivePolicy", """{"SoftDeletePeriod":"740.00:00:00","Recoverability":"Enabled"}"""))]));
            }
            if (Scenario == "failure") throw new IncidentReportExportException("Synthetic source failure");
            if (Scenario == "partial") return Task.FromResult(new IncidentReportQueryResult([], false));
            Check(query.Parameters["owningTeam"].ToString() == "Team", "Team parameter was not captured.");
            Check(query.Parameters["startDate"] is DateTime lower && lower == Start.UtcDateTime
                && query.Parameters["endDate"] is DateTime upper && upper == End.UtcDateTime,
                "Captured bounds were not transmitted as UTC parameters.");
            Check(!query.Text.Contains("\"Team\"") && !query.Text.Contains("ChangeDescription"),
                "Query interpolates source text or requests unnecessary incident history text.");
            Check(query.Text.Contains("where tostring(SiloId) == \"1\"")
                && query.Text.Contains("summarize arg_max(ModifiedDate,")
                && query.Text.Contains("where OwningTeamName == owningTeam")
                && query.Text.Contains("join kind=leftanti (PurgedIncidents")
                && query.Text.Contains("join kind=leftanti (PurgedIncidentHistory")
                && query.Text.Contains("coalesce(IsPurged, false)")
                && query.Text.Contains("eligible | project IncidentId, SiloId"),
                "Required silo/latest/current team/purge query contract changed.");
            Check(!query.Text.Contains("arg_max(ModifiedDate, *)"), "Broad latest projection is forbidden.");
            var historyJoin = query.Text[query.Text.IndexOf("let actions =", StringComparison.Ordinal)..];
            Check(historyJoin.StartsWith("let actions = eligible | project IncidentId, SiloId", StringComparison.Ordinal)
                && historyJoin.Contains("| join kind=inner hint.strategy=broadcast (", StringComparison.Ordinal),
                "History must join from the small team-scoped key set, not build a hash table of global period history.");

            // A small deterministic source fake, not a KQL parser. Predicate presence above is checked
            // independently; the exact production queries also receive live aggregate-only compilation.
            var latest = Incidents.Where(row => row["SiloId"].GetInt64() == 1
                    && row["ModifiedDate"].GetDateTimeOffset() <= AsOf)
                .GroupBy(row => row["IncidentId"].GetInt64())
                .Select(group => group.MaxBy(row => row["ModifiedDate"].GetDateTimeOffset())!)
                .Where(row => row["OwningTeamName"].GetString() == "Team" && !row["IsPurged"].GetBoolean()
                    && !PurgedIncidents.Contains(row["IncidentId"].GetInt64())).ToArray();
            var ids = latest.Select(row => row["IncidentId"].GetInt64()).ToHashSet();
            var selectedActions = Actions.Where(row => ids.Contains(row["IncidentId"].GetInt64())
                && row["ChangeDate"].GetDateTimeOffset() >= Start && row["ChangeDate"].GetDateTimeOffset() <= End
                && (row["HistoryId"].ValueKind == JsonValueKind.Null
                    || !PurgedHistory.Contains(row["HistoryId"].GetInt64()))).ToImmutableArray();
            if (query.Text == IncidentReportCollector.IncidentQuery)
            {
                var rows = latest.ToImmutableArray();
                if (Scenario == "silo") rows = [rows[0].SetItem("SiloId", JsonSerializer.SerializeToElement(2))];
                return Task.FromResult(new IncidentReportQueryResult(rows));
            }
            if (Scenario == "id") selectedActions = [selectedActions[0].SetItem("HistoryId", JsonSerializer.SerializeToElement("invalid"))];
            if (Scenario == "id-column") selectedActions = [selectedActions[0].Remove("HistoryId")];
            if (Scenario == "actor") selectedActions = [selectedActions[0].SetItem("ChangedBy", JsonSerializer.SerializeToElement(""))];
            if (Scenario == "type") selectedActions = [selectedActions[0].SetItem("ChangeCategories", JsonSerializer.SerializeToElement(""))];
            if (Scenario == "conflict") selectedActions = selectedActions.Add(
                selectedActions[0].SetItem("ChangedBy", JsonSerializer.SerializeToElement("bob")));
            if (Scenario == "outsider-conflict") selectedActions = selectedActions.Add(
                selectedActions[0].SetItem("ChangedBy", JsonSerializer.SerializeToElement("charlie")));
            if (Scenario == "unkeyed-outsider") selectedActions = selectedActions.Add(
                selectedActions[0].SetItem("HistoryId", JsonSerializer.SerializeToElement<object?>(null))
                    .SetItem("ChangedBy", JsonSerializer.SerializeToElement("charlie")));
            return Task.FromResult(new IncidentReportQueryResult(selectedActions));
        }

        private void CheckCancellation(string stage, CancellationToken token)
        {
            Check(token.CanBeCanceled || CancellationStage is null, "Source was not passed cancellation.");
            if (CancellationStage == stage) Cancel!();
            token.ThrowIfCancellationRequested();
        }
    }
}
