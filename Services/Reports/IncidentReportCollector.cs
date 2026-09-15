using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;

namespace ProductivityInsights.Services.Reports;

public sealed class IncidentReportCollector : IReportCollector, IDisposable
{
    public const int MaximumRowsPerQuery = 50000;
    private const string SiloId = "1";
    private readonly IncidentManagementKustoOptions options;
    private readonly IIncidentReportQueryClient client;
    private readonly TimeProvider clock;
    private readonly bool ownsClient;

    public IncidentReportCollector(IOptions<IncidentManagementKustoOptions> options)
        : this(options, new IncidentReportQueryClient(options.Value))
    {
        ownsClient = true;
    }

    public IncidentReportCollector(IOptions<IncidentManagementKustoOptions> options,
        IIncidentReportQueryClient queryClient, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queryClient);
        this.options = new()
        {
            ClusterUri = options.Value.ClusterUri,
            Database = options.Value.Database,
            QueryTimeoutSeconds = options.Value.QueryTimeoutSeconds
        };
        client = queryClient;
        clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReportSnapshot> CollectAsync(ReportRequest request,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Analysis != "incidentManagement" || request.View is not ("details" or "teams"))
            throw new IncidentReportExportException("ICM export supports Incident Resolutions Details and Teams, not placeholder Trends.");
        if (request.Start > request.End || request.Sources.IsDefaultOrEmpty
            || request.Sources.Length > 64 || request.Sources.Any(source =>
                string.IsNullOrWhiteSpace(source.Key) || string.IsNullOrWhiteSpace(source.Scope))
            || request.Sources.Select(source => source.Key).Distinct(StringComparer.Ordinal).Count() != request.Sources.Length)
            throw new IncidentReportExportException("ICM export requires valid captured date bounds and 1-64 uniquely keyed owning-team sources.");
        if (request.IsTeams && request.Roster.IsDefaultOrEmpty)
            throw new IncidentReportExportException("ICM Teams export requires the successfully captured manager roster.");
        if (!Uri.TryCreate(options.ClusterUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(options.Database))
            throw new IncidentReportExportException("Configure IncidentManagementKusto ClusterUri (HTTPS) and Database for ICM export.");

        var asOf = clock.GetUtcNow();
        var teams = request.Sources.Select(source => source.Scope.Trim()).Distinct(StringComparer.Ordinal).ToImmutableArray();
        var coverageNote = await GetCoverageNoteAsync(request, teams, asOf, progress, cancellationToken);

        var incidents = ImmutableArray.CreateBuilder<IncidentReportRecord>();
        var actions = ImmutableArray.CreateBuilder<IncidentActionReportRecord>();
        foreach (var team in teams)
        {
            progress?.Report(request.IsTeams ? "Collecting available ICM incidents and recorded actions"
                : "Collecting complete scoped ICM incidents and recorded actions");
            var parameters = new Dictionary<string, object>
            {
                ["owningTeam"] = team,
                ["startDate"] = request.Start.UtcDateTime,
                ["endDate"] = request.End.UtcDateTime,
                ["asOf"] = asOf.UtcDateTime,
                ["rowLimit"] = MaximumRowsPerQuery + 1
            }.ToImmutableDictionary();
            var incidentResult = await ExecuteAsync(new(IncidentQuery, false, parameters), cancellationToken);
            var actionResult = await ExecuteAsync(new(ActionQuery, false, parameters), cancellationToken);
            var scoped = new Dictionary<string, IncidentReportRecord>(StringComparer.Ordinal);
            foreach (var row in incidentResult.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IncidentReportSourceVerification.Text(row, "SiloId") != SiloId
                    || IncidentReportSourceVerification.Text(row, "OwningTeamName") != team)
                    throw new IncidentReportExportException("ICM query returned an incident outside the selected silo/current team.");
                var incident = MapIncident(row, request);
                if (incident.ModifiedAt > asOf || !scoped.TryAdd(incident.Id, incident))
                    throw new IncidentReportExportException("ICM latest-incident query returned inconsistent or duplicate versions.");
            }
            var teamActions = new Dictionary<IncidentActionKey, IncidentActionReportRecord>();
            foreach (var row in actionResult.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = IncidentReportSourceVerification.Id(row, "IncidentId");
                var silo = IncidentReportSourceVerification.Text(row, "SiloId");
                var timestamp = IncidentReportSourceVerification.Date(row, "ChangeDate", true)!.Value;
                var actorText = IncidentReportSourceVerification.Text(row, "ChangedBy");
                var type = IncidentReportSourceVerification.Text(row, "ChangeCategories");
                if (silo != SiloId || !scoped.ContainsKey(id) || timestamp < request.Start
                    || timestamp > request.End || string.IsNullOrWhiteSpace(actorText) || string.IsNullOrWhiteSpace(type))
                    throw new IncidentReportExportException("ICM action evidence is incomplete or outside the eligible source/window.");
                var actor = Identity(actorText);
                var actionId = request.IsTeams && row.TryGetValue("HistoryId", out var historyId) && historyId.ValueKind == JsonValueKind.Null
                    ? "" : IncidentReportSourceVerification.Id(row, "HistoryId");
                var action = new IncidentActionReportRecord("", id, silo, actionId, actor, timestamp, type);
                var actionKey = IncidentActionKey.From(action);
                if (teamActions.TryGetValue(actionKey, out var previous) && previous != action)
                    throw new IncidentReportExportException("ICM history contains conflicting records for a silo/incident-qualified action identifier.");
                teamActions[actionKey] = action;
            }
            var visibleActions = teamActions.Values.Where(action => ReportIdentityMatcher.Matches(action.Actor, request))
                .OrderBy(action => action.Timestamp).ThenBy(action => action.ActionId, StringComparer.Ordinal).ToArray();
            var actionIncidents = visibleActions.Select(action => action.IncidentId).ToHashSet(StringComparer.Ordinal);
            foreach (var source in request.Sources.Where(source => source.Scope.Trim() == team))
            {
                foreach (var incident in scoped.Values.Where(incident => incident.Active || incident.Created
                    || incident.Mitigated || incident.Resolved || actionIncidents.Contains(incident.Id)))
                    incidents.Add(incident with { SourceKey = source.Key });
                foreach (var action in visibleActions)
                    actions.Add(action with { SourceKey = source.Key });
                if (incidents.Count + actions.Count > 200000)
                    throw new IncidentReportExportException("ICM export exceeds the 200000-record resource limit. Select fewer teams or a shorter window.");
            }
        }

        return new ReportSnapshot
        {
            Request = request,
            AsOf = asOf,
            Incidents = incidents.OrderByDescending(incident => incident.Active)
                .ThenBy(incident => incident.Severity ?? int.MaxValue)
                .ThenBy(incident => incident.Status == "Resolved" ? 0 : 1)
                .ThenByDescending(incident => incident.ResolvedAt ?? incident.MitigatedAt ?? incident.ModifiedAt)
                .ThenBy(incident => incident.EntityKey, StringComparer.Ordinal)
                .ThenBy(incident => incident.SourceKey, StringComparer.Ordinal).ToImmutableArray(),
            IncidentActions = actions.ToImmutable(),
            CoverageNotes =
            [
                "Active is the latest eligible state as of collection, independent of the selected period. "
                    + "Created is attributed to the current owning contact. Mitigated/Resolved require the respective latest state and in-period date/actor.",
                "Contributed counts distinct silo-qualified incident/member pairs from in-period IncidentHistory "
                    + "actions (HistoryId, ChangedBy, ChangeDate, ChangeCategories), independently of displayed state groups.",
                "Current owning-team and silo 1 scope; PurgedIncidents, IsPurged and PurgedIncidentHistory exclusions applied. "
                    + "All selected source memberships are retained. Date bounds are inclusive; TTD/TTM/TTR use hours.",
                coverageNote
            ]
        };
    }

    private async Task<string> GetCoverageNoteAsync(ReportRequest request, ImmutableArray<string> teams,
        DateTimeOffset asOf, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.IsTeams)
            return "Available warehouse data: Teams export reports returned incident and in-period action records "
                + "without requiring source-owner history or retention attestation. Historical completeness is not "
                + "independently verified; records may be absent because of retention or publication lag. "
                + "No coverage dates are inferred. Query failures and incomplete responses still block export.";

        progress?.Report("Verifying ICM source schema, effective retention and history/purge coverage");
        var retention = ImmutableDictionary.CreateBuilder<string, IncidentReportRetention>(StringComparer.Ordinal);
        foreach (var table in IncidentReportSourceVerification.RequiredSchema.Keys.Order(StringComparer.Ordinal))
        {
            var schema = await ExecuteAsync(IncidentReportQuery.Management(
                $".show table {table} schema as json"), cancellationToken);
            if (schema.Rows.Length != 1)
                throw new IncidentReportExportException($"ICM {table} schema was not returned. Verify read-only metadata permissions.");
            IncidentReportSourceVerification.ValidateSchema(table,
                IncidentReportSourceVerification.Text(schema.Rows[0], "Schema"));
            var policy = await ExecuteAsync(IncidentReportQuery.Management(
                $".show table {table} policy retention | project Policy, EffectivePolicy"), cancellationToken);
            if (policy.Rows.Length != 1)
                throw new IncidentReportExportException($"ICM {table} effective retention is unavailable.");
            retention[table] = IncidentReportSourceVerification.ParseRetention(
                IncidentReportSourceVerification.Text(policy.Rows[0], "Policy"),
                IncidentReportSourceVerification.Text(policy.Rows[0], "EffectivePolicy"));
        }
        var coverageRequest = new IncidentReportCoverageRequest(options.ClusterUri, options.Database,
            SiloId, teams, request.Start, request.End, asOf, retention.ToImmutable());
        var coverage = await client.GetCoverageAsync(coverageRequest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IncidentReportSourceVerification.ValidateCoverage(coverageRequest, coverage);
        return $"Verified history window: {coverage!.HistoryFrom:O} through {coverage.HistoryThrough:O}. "
            + "Source-owner retained-view coverage and retention semantics verified by the configured provider. "
            + "This is the currently published warehouse view, not a guarantee that all upstream actions have ingested.";
    }

    private async Task<IncidentReportQueryResult> ExecuteAsync(IncidentReportQuery query, CancellationToken token)
    {
        try
        {
            var result = await client.ExecuteAsync(query, token);
            token.ThrowIfCancellationRequested();
            if (!result.IsComplete || result.Rows.IsDefault || result.Rows.Length > MaximumRowsPerQuery)
                throw new IncidentReportExportException(
                    $"ICM query was partial, truncated or exceeded the {MaximumRowsPerQuery}-row resource limit. "
                    + "Select fewer teams/a shorter window; no partial report was generated.");
            return result;
        }
        catch (IncidentReportExportException exception) when (!query.IsManagement)
        {
            var stage = query.Text == ActionQuery ? "recorded-action history" : "incident";
            throw new IncidentReportExportException($"ICM {stage} query: {exception.Message}");
        }
    }

    private static IncidentReportRecord MapIncident(ImmutableDictionary<string, JsonElement> row, ReportRequest request)
    {
        string Text(string column) => IncidentReportSourceVerification.Text(row, column);
        DateTimeOffset? Date(string column, bool required = false) => IncidentReportSourceVerification.Date(row, column, required);
        bool InWindow(DateTimeOffset? date) => date >= request.Start && date <= request.End;
        var created = Date("CreateDate", true)!.Value;
        var mitigated = Date("MitigateDate");
        var resolved = Date("ResolveDate");
        var status = Text("Status").ToUpperInvariant() switch
        {
            "ACTIVE" => "Active", "MITIGATED" => "Mitigated", "RESOLVED" => "Resolved", _ => Text("Status")
        };
        var owner = Identity(Text("OwningContactAlias"));
        var mitigator = Identity(Text("MitigatedBy"));
        var resolver = Identity(Text("ResolvedBy"));
        var id = IncidentReportSourceVerification.Id(row, "IncidentId");
        var severityText = Text("Severity");
        int? severity = null;
        if (!string.IsNullOrEmpty(severityText))
        {
            if (!int.TryParse(severityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new IncidentReportExportException("ICM export contains an invalid Severity.");
            severity = number;
        }
        return new IncidentReportRecord
        {
            SourceKey = "", Id = id, SiloId = SiloId, Title = Text("Title"), Severity = severity, Status = status,
            CreatedAt = created, ModifiedAt = Date("ModifiedDate", true)!.Value, ImpactStart = Date("ImpactStartDate"),
            MitigatedAt = mitigated, ResolvedAt = resolved, Owner = owner, MitigatedBy = mitigator, ResolvedBy = resolver,
            OwningTenant = Text("OwningTenantName"), OwningTeam = Text("OwningTeamName"), Mitigation = Text("Mitigation"),
            Url = $"https://portal.microsofticm.com/imp/v5/incidents/details/{id}/summary",
            Active = status == "Active" && ReportIdentityMatcher.Matches(owner, request),
            Created = InWindow(created) && ReportIdentityMatcher.Matches(owner, request),
            Mitigated = status == "Mitigated" && InWindow(mitigated) && ReportIdentityMatcher.Matches(mitigator, request),
            Resolved = status == "Resolved" && InWindow(resolved) && ReportIdentityMatcher.Matches(resolver, request),
            Metadata = new Dictionary<string, string>
            {
                ["IsOutage"] = Text("IsOutage"), ["IsNoise"] = Text("IsNoise"), ["SourceOrigin"] = Text("SourceOrigin"),
                ["OwningContactAlias"] = Text("OwningContactAlias"),
                ["MitigatedBy"] = Text("MitigatedBy"), ["ResolvedBy"] = Text("ResolvedBy")
            }.ToImmutableDictionary()
        };
    }

    private static ReportIdentity Identity(string value) =>
        new(string.IsNullOrWhiteSpace(value) ? "Unknown" : value);

    public void Dispose()
    {
        if (ownsClient && client is IDisposable disposable)
            disposable.Dispose();
    }

    public const string QueryPrefix = """
        declare query_parameters(owningTeam:string, startDate:datetime, endDate:datetime, asOf:datetime, rowLimit:long);
        let scopedIncidents = Incidents
        | where tostring(SiloId) == "1"
        | where ModifiedDate <= asOf
        | project IncidentId, SiloId, ModifiedDate, CreateDate, ImpactStartDate, MitigateDate, ResolveDate,
            Severity, Status, Title, OwningContactAlias, OwningTenantName, OwningTeamName,
            MitigatedBy, ResolvedBy, Mitigation, IsOutage, IsNoise, SourceOrigin, IsPurged;
        let candidateIds = scopedIncidents | where OwningTeamName == owningTeam | distinct IncidentId;
        let eligible = candidateIds
        | join kind=inner hint.strategy=broadcast (scopedIncidents) on IncidentId
        | summarize arg_max(ModifiedDate, SiloId, CreateDate, ImpactStartDate, MitigateDate, ResolveDate,
            Severity, Status, Title, OwningContactAlias, OwningTenantName, OwningTeamName,
            MitigatedBy, ResolvedBy, Mitigation, IsOutage, IsNoise, SourceOrigin, IsPurged) by IncidentId
        | where OwningTeamName == owningTeam
        | where coalesce(IsPurged, false) == false
        | join kind=leftanti (PurgedIncidents | distinct IncidentId) on IncidentId;
        let actions = eligible | project IncidentId, SiloId
        | join kind=inner hint.strategy=broadcast (
            IncidentHistory
            | where ChangeDate between (startDate .. endDate)
            | project HistoryId, IncidentId, ChangeDate, ChangedBy, ChangeCategories
        ) on IncidentId
        | join kind=leftanti (PurgedIncidentHistory | distinct HistoryId) on HistoryId
        | project HistoryId, IncidentId, SiloId, ChangeDate, ChangedBy, ChangeCategories;
        """;

    public const string IncidentQuery = QueryPrefix + """

        eligible
        | where Status =~ "ACTIVE" or CreateDate between (startDate .. endDate)
            or (Status =~ "MITIGATED" and MitigateDate between (startDate .. endDate))
            or (Status =~ "RESOLVED" and ResolveDate between (startDate .. endDate))
            or IncidentId in (actions | distinct IncidentId)
        | project IncidentId, SiloId, ModifiedDate, CreateDate, ImpactStartDate, MitigateDate, ResolveDate,
            Severity, Status, Title, OwningContactAlias, OwningTenantName, OwningTeamName,
            MitigatedBy, ResolvedBy, Mitigation, IsOutage, IsNoise, SourceOrigin
        | order by IncidentId asc
        | take rowLimit
        """;

    public const string ActionQuery = QueryPrefix + """

        actions
        | distinct HistoryId, IncidentId, SiloId, ChangeDate, ChangedBy, ChangeCategories
        | order by HistoryId asc, IncidentId asc
        | take rowLimit
        """;
}
