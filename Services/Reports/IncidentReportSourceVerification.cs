using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ProductivityInsights.Services.Reports;

public sealed class IncidentReportExportException(string message) : InvalidOperationException(message);

public sealed record IncidentReportQuery(
    string Text, bool IsManagement, ImmutableDictionary<string, object> Parameters)
{
    public static IncidentReportQuery Management(string text) => new(text, true,
        ImmutableDictionary<string, object>.Empty);
}

public sealed record IncidentReportQueryResult(
    ImmutableArray<ImmutableDictionary<string, JsonElement>> Rows, bool IsComplete = true);

/// <summary>
/// Coverage of the retained, published warehouse view, not a guarantee about unpublished upstream actions.
/// HistoryFrom must come from a retention-safe event-time contract, not a minimum observed timestamp or TTL alone.
/// Through timestamps delimit that contract's applicability to the selected view. Teams and silo qualify every assertion.
/// </summary>
public sealed record IncidentReportCoverage(
    string ClusterUri, string Database, string SiloId, ImmutableArray<string> Teams,
    DateTimeOffset HistoryFrom, DateTimeOffset HistoryThrough,
    DateTimeOffset LatestIncidentsThrough, DateTimeOffset PurgesThrough,
    string Evidence, string RetentionSemantics)
{
    public ImmutableDictionary<string, TimeSpan> MinimumRetention { get; init; } =
        ImmutableDictionary<string, TimeSpan>.Empty;
}

public sealed record IncidentReportCoverageRequest(
    string ClusterUri, string Database, string SiloId, ImmutableArray<string> Teams,
    DateTimeOffset Start, DateTimeOffset End, DateTimeOffset AsOf,
    ImmutableDictionary<string, IncidentReportRetention> Retention);

public interface IIncidentReportQueryClient
{
    Task<IncidentReportQueryResult> ExecuteAsync(
        IncidentReportQuery query, CancellationToken cancellationToken);

    // Null means completeness cannot be attested. Details requires attestation; Teams uses available data.
    Task<IncidentReportCoverage?> GetCoverageAsync(
        IncidentReportCoverageRequest request, CancellationToken cancellationToken);
}

public interface IIncidentReportCoverageProvider
{
    Task<IncidentReportCoverage?> GetCoverageAsync(
        IncidentReportCoverageRequest request, CancellationToken cancellationToken);
}

/// <summary>Accepts externally supplied source-owner evidence; never invents a rolling window from the observed TTL.</summary>
public sealed class IncidentReportConfiguredCoverageProvider : IIncidentReportCoverageProvider
{
    private readonly Func<IncidentReportCoverage> readCoverage;

    public IncidentReportConfiguredCoverageProvider(IOptions<IncidentReportCoverage> options)
        : this(() => options.Value)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public IncidentReportConfiguredCoverageProvider(Func<IncidentReportCoverage> readCoverage)
    {
        ArgumentNullException.ThrowIfNull(readCoverage);
        this.readCoverage = readCoverage;
    }

    public Task<IncidentReportCoverage?> GetCoverageAsync(
        IncidentReportCoverageRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = readCoverage();
        IncidentReportSourceVerification.ValidateCoverage(request, evidence);
        foreach (var table in IncidentReportSourceVerification.RequiredSchema.Keys)
        {
            if (!evidence.MinimumRetention.TryGetValue(table, out var minimum) || minimum <= TimeSpan.Zero
                || !request.Retention.TryGetValue(table, out var actual) || actual.SoftDeletePeriod < minimum)
                throw new IncidentReportExportException(
                    $"ICM {table} retention does not satisfy the configured source coverage contract. "
                    + "Verify the policy and obtain current retained-window evidence.");
        }
        return Task.FromResult<IncidentReportCoverage?>(evidence);
    }
}

public sealed record IncidentReportRetention(TimeSpan SoftDeletePeriod, string Recoverability);

public static class IncidentReportSourceVerification
{
    public static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> RequiredSchema =
        new Dictionary<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["Incidents"] = Columns(
                "IncidentId:long", "SiloId:long", "ModifiedDate:datetime", "CreateDate:datetime",
                "ImpactStartDate:datetime", "MitigateDate:datetime", "ResolveDate:datetime",
                "Severity:int", "Status:string", "Title:string", "OwningContactAlias:string",
                "OwningTenantName:string", "OwningTeamName:string", "MitigatedBy:string",
                "ResolvedBy:string", "Mitigation:string", "IsOutage:bool", "IsNoise:bool",
                "SourceOrigin:string", "IsPurged:bool"),
            ["IncidentHistory"] = Columns(
                "HistoryId:long", "IncidentId:long", "ChangeDate:datetime",
                "ChangedBy:string", "ChangeCategories:string"),
            ["PurgedIncidents"] = Columns("IncidentId:long", "SiloId:long"),
            ["PurgedIncidentHistory"] = Columns("HistoryId:long")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableDictionary<string, string> Columns(params string[] columns) =>
        columns.Select(value => value.Split(':')).ToImmutableDictionary(
            parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    public static void ValidateSchema(string table, string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;
            if (root.GetProperty("Name").GetString() != table)
                throw new IncidentReportExportException("ICM schema response identifies a different table.");
            var columns = root.GetProperty("OrderedColumns").EnumerateArray().ToDictionary(
                column => column.GetProperty("Name").GetString()!,
                column => column.GetProperty("CslType").GetString(), StringComparer.Ordinal);
            foreach (var (column, type) in RequiredSchema[table])
                if (!columns.TryGetValue(column, out var actual) || actual != type)
                    throw new IncidentReportExportException(
                        $"ICM export requires {table}.{column} with Kusto type {type}. Verify source schema and access.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException
            && exception is not IncidentReportExportException)
        {
            throw new IncidentReportExportException($"ICM {table} schema response is malformed; export was blocked.");
        }
    }

    public static IncidentReportRetention ParseRetention(string? policy, string? effectivePolicy)
    {
        // EffectivePolicy includes database inheritance. A null table Policy is not unlimited retention.
        var json = string.IsNullOrWhiteSpace(effectivePolicy) || effectivePolicy.Trim() == "null"
            ? policy : effectivePolicy;
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SoftDeletePeriod", out var duration)
                || !TimeSpan.TryParseExact(duration.GetString(), "c", CultureInfo.InvariantCulture, out var ttl)
                || ttl <= TimeSpan.Zero
                || !root.TryGetProperty("Recoverability", out var recoverability)
                || recoverability.GetString() is not ("Enabled" or "Disabled"))
                throw new IncidentReportExportException(
                    "ICM effective retention policy is missing or invalid. Verify inherited database policy.");
            return new(ttl, recoverability.GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            && exception is not IncidentReportExportException)
        {
            throw new IncidentReportExportException("ICM effective retention policy could not be parsed.");
        }
    }

    public static void ValidateCoverage(IncidentReportCoverageRequest request, IncidentReportCoverage? evidence)
    {
        if (evidence is null)
            throw new IncidentReportExportException(
                "ICM export is blocked: the requested IncidentHistory event-time window has no verified "
                + "retention-safe source contract. Configure an IIncidentReportCoverageProvider with "
                + "source-owner evidence for the retained history window, latest-incident view and incident/history "
                + "purge semantics. The documented two-year horizon and ingestion-based TTL do not alone establish "
                + "a ChangeDate cutoff. This check concerns published warehouse data, not unpublished upstream actions.");

        if (!Uri.TryCreate(evidence.ClusterUri, UriKind.Absolute, out var actualCluster)
            || !Uri.TryCreate(request.ClusterUri, UriKind.Absolute, out var expectedCluster)
            || actualCluster != expectedCluster || evidence.Database != request.Database
            || evidence.SiloId != request.SiloId
            || evidence.Teams.IsDefault
            || request.Teams.Any(team => !evidence.Teams.Contains(team, StringComparer.Ordinal)))
            throw new IncidentReportExportException("ICM completeness evidence does not cover the selected source/silo/teams.");

        if (string.IsNullOrWhiteSpace(evidence.Evidence) || string.IsNullOrWhiteSpace(evidence.RetentionSemantics)
            || evidence.HistoryFrom > request.Start || evidence.HistoryThrough < request.End
            || evidence.LatestIncidentsThrough < request.AsOf || evidence.PurgesThrough < request.AsOf)
            throw new IncidentReportExportException(
                "ICM completeness evidence does not cover the requested window/as-of time, retention semantics or purge feeds. "
                + "Select a covered window or obtain updated source-owner coverage evidence.");
    }

    internal static string Text(ImmutableDictionary<string, JsonElement> row, string column)
    {
        if (!row.TryGetValue(column, out var value))
            throw new IncidentReportExportException($"ICM export response is missing required column {column}.");
        return value.ValueKind is JsonValueKind.Null ? "" : value.ToString();
    }

    internal static DateTimeOffset? Date(ImmutableDictionary<string, JsonElement> row, string column, bool required = false)
    {
        var value = Text(row, column);
        if (string.IsNullOrEmpty(value) && !required)
            return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            || (required && date == DateTimeOffset.MinValue))
            throw new IncidentReportExportException($"ICM export contains a missing or invalid {column}; no partial report was generated.");
        return date == DateTimeOffset.MinValue ? null : date;
    }

    internal static string Id(ImmutableDictionary<string, JsonElement> row, string column)
    {
        if (!long.TryParse(Text(row, column), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new IncidentReportExportException($"ICM export requires a stable positive {column}; the source contains an invalid action/incident identifier.");
        return id.ToString(CultureInfo.InvariantCulture);
    }
}
