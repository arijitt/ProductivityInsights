using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using ProductivityInsights.Options;

namespace ProductivityInsights.Services.Reports;

/// <summary>Read-only, cancellation-aware export transport. It does not alter the ordinary metrics client.</summary>
public sealed class IncidentReportQueryClient : IIncidentReportQueryClient, IDisposable
{
    public const int MaximumResponseBytes = 32 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly TokenCredential credential;
    private readonly Uri cluster;
    private readonly string database;
    private readonly int timeoutSeconds;
    private readonly bool ownsHttp;
    private readonly IIncidentReportCoverageProvider? coverageProvider;

    public IncidentReportQueryClient(IncidentManagementKustoOptions options)
        : this(options, coverageProvider: null)
    {
    }

    public IncidentReportQueryClient(IncidentManagementKustoOptions options,
        IIncidentReportCoverageProvider? coverageProvider)
        : this(options, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, new DefaultAzureCredential(), coverageProvider)
    {
        ownsHttp = true;
    }

    public IncidentReportQueryClient(IncidentManagementKustoOptions options, HttpClient httpClient,
        TokenCredential credential, IIncidentReportCoverageProvider? coverageProvider = null)
    {
        if (!Uri.TryCreate(options.ClusterUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrWhiteSpace(options.Database)
            || options.QueryTimeoutSeconds is < 1 or > 3600)
            throw new IncidentReportExportException("Configure a valid HTTPS ICM cluster, database and 1-3600 second query timeout.");
        cluster = uri;
        database = options.Database;
        timeoutSeconds = options.QueryTimeoutSeconds;
        http = httpClient;
        this.credential = credential;
        this.coverageProvider = coverageProvider;
    }

    public async Task<IncidentReportCoverage?> GetCoverageAsync(
        IncidentReportCoverageRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = coverageProvider is null ? null
            : await coverageProvider.GetCoverageAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IncidentReportSourceVerification.ValidateCoverage(request, evidence);
        return evidence;
    }

    public async Task<IncidentReportQueryResult> ExecuteAsync(IncidentReportQuery query,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeout.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext([cluster.GetLeftPart(UriPartial.Authority) + "/.default"]), token);
            var parameters = query.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                DateTime date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset date => date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                _ => pair.Value.ToString()
            }, StringComparer.Ordinal);
            var properties = JsonSerializer.Serialize(new
            {
                Options = new Dictionary<string, object>
                {
                    ["request_readonly"] = true,
                    ["servertimeout"] = TimeSpan.FromSeconds(timeoutSeconds).ToString("c", CultureInfo.InvariantCulture),
                    ["queryconsistency"] = "strongconsistency",
                    ["results_progressive_enabled"] = false,
                    ["deferpartialqueryfailures"] = false,
                    ["truncationmaxrecords"] = IncidentReportCollector.MaximumRowsPerQuery + 1,
                    ["truncationmaxsize"] = MaximumResponseBytes
                },
                Parameters = parameters
            });
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(cluster, query.IsManagement ? "v1/rest/mgmt" : "v2/rest/query"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Headers.Add("x-ms-client-request-id", $"ProductivityInsights.IncidentExport;{Guid.NewGuid()}");
            request.Content = new StringContent(JsonSerializer.Serialize(new { db = database, csl = query.Text, properties }),
                Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new IncidentReportExportException(
                    $"ICM read-only {(query.IsManagement ? "schema/retention" : "data")} request failed "
                    + $"(HTTP {(int)response.StatusCode}). Verify existing user authentication and source permissions.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw ResponseLimit();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(bytes, token)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                    throw ResponseLimit();
                await buffer.WriteAsync(bytes.AsMemory(0, read), token);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            return ParseResponse(document.RootElement, query.IsManagement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IncidentReportExportException("ICM export query timed out. Reduce the selected scope/window or verify source availability.");
        }
        catch (Exception exception) when (exception is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new IncidentReportExportException("ICM export could not authenticate with the existing DefaultAzureCredential. Sign in with an authorized account.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            throw new IncidentReportExportException("ICM export did not receive a complete valid source response. Check connectivity/source health and retry.");
        }
    }

    private static IncidentReportExportException ResponseLimit() => new(
        "ICM query exceeded the 32 MiB export response limit. Select fewer teams/a shorter window; no partial report was generated.");

    public static IncidentReportQueryResult ParseResponse(JsonElement root, bool management)
    {
        try
        {
            JsonElement primary = default;
            bool complete = false;
            bool statusSeen = false;
            if (management)
            {
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
                    || (root.TryGetProperty("Exceptions", out var errors) && errors.GetArrayLength() > 0))
                    throw Incomplete();
                foreach (var table in root.GetProperty("Tables").EnumerateArray())
                {
                    if (table.GetProperty("TableName").GetString() == "Table_0")
                        primary = table;
                    var columns = ColumnNames(table);
                    if (columns.Contains("StatusCode", StringComparer.Ordinal)
                        && columns.Contains("Severity", StringComparer.Ordinal))
                    {
                        CheckStatus(table, "Severity");
                        statusSeen = true;
                    }
                }
                complete = statusSeen;
            }
            else
            {
                if (root.ValueKind != JsonValueKind.Array)
                    throw Incomplete();
                foreach (var frame in root.EnumerateArray())
                {
                    if (complete)
                        throw Incomplete();
                    var frameType = frame.GetProperty("FrameType").GetString();
                    if (frameType == "DataSetCompletion")
                    {
                        if (frame.GetProperty("Cancelled").GetBoolean())
                            throw Incomplete("the source reported a cancelled query");
                        if (frame.GetProperty("HasErrors").GetBoolean())
                            throw DataSetFailure(frame);
                        complete = true;
                    }
                    else if (frameType == "DataTable")
                    {
                        var kind = frame.GetProperty("TableKind").GetString();
                        if (kind == "PrimaryResult")
                        {
                            if (primary.ValueKind != JsonValueKind.Undefined)
                                throw Incomplete();
                            primary = frame;
                        }
                        else if (kind == "QueryCompletionInformation")
                        {
                            CheckStatus(frame, "Level");
                            statusSeen = true;
                        }
                        else if (kind != "QueryProperties")
                            throw Incomplete();
                    }
                    else if (frameType != "DataSetHeader")
                        throw Incomplete();
                }
            }
            if (!complete || !statusSeen || primary.ValueKind == JsonValueKind.Undefined)
                throw Incomplete();
            return new(ReadRows(primary));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or IndexOutOfRangeException
            && exception is not IncidentReportExportException)
        {
            throw Incomplete();
        }
    }

    private static void CheckStatus(JsonElement table, string severity)
    {
        var rows = ReadRows(table);
        if (rows.IsEmpty)
            throw Incomplete();
        bool success = false;
        foreach (var row in rows)
        {
            var statusCode = row["StatusCode"].GetInt32();
            var level = row[severity].GetInt32();
            if (statusCode != 0 || level < 4)
                throw Incomplete($"query status reported {severity} {level}, StatusCode {statusCode}");
            if (level == 4)
                success = true;
        }
        if (!success)
            throw Incomplete();
    }

    private static string[] ColumnNames(JsonElement table) => table.GetProperty("Columns").EnumerateArray()
        .Select(column => column.GetProperty("ColumnName").GetString()
            ?? throw Incomplete()).ToArray();

    private static ImmutableArray<ImmutableDictionary<string, JsonElement>> ReadRows(JsonElement table)
    {
        var names = ColumnNames(table);
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
            throw Incomplete();
        var result = ImmutableArray.CreateBuilder<ImmutableDictionary<string, JsonElement>>();
        foreach (var row in table.GetProperty("Rows").EnumerateArray())
        {
            if (row.GetArrayLength() != names.Length)
                throw Incomplete();
            var mapped = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
            for (var index = 0; index < names.Length; index++)
                mapped.Add(names[index], row[index].Clone());
            result.Add(mapped.ToImmutable());
            if (result.Count > IncidentReportCollector.MaximumRowsPerQuery)
                throw new IncidentReportExportException(
                    "ICM export exceeded the query row limit. Select fewer teams/a shorter window; no partial report was generated.");
        }
        return result.ToImmutable();
    }

    private static IncidentReportExportException DataSetFailure(JsonElement completion)
    {
        if (completion.TryGetProperty("OneApiErrors", out var failures) && failures.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failures.EnumerateArray())
            {
                if (!failure.TryGetProperty("error", out var error)
                    || !error.TryGetProperty("code", out var code) || code.GetString() != "LimitsExceeded")
                    continue;
                var runaway = error.TryGetProperty("@message", out var message) && message.ValueKind == JsonValueKind.String
                    && message.GetString()!.Contains("E_RUNAWAY_QUERY", StringComparison.Ordinal);
                return Incomplete(runaway
                    ? "the source exceeded its per-operator memory budget (E_RUNAWAY_QUERY). "
                        + "Narrow the source/date range or optimize the export query"
                    : "the source reported a query resource limit (LimitsExceeded). Narrow the source/date range");
            }
        }
        return Incomplete("the source reported query execution errors");
    }

    private static IncidentReportExportException Incomplete(
        string reason = "missing or malformed completion/status data, or an incomplete result stream") => new(
        $"ICM source response rejected: {reason}. No partial report was produced.");

    public void Dispose()
    {
        if (ownsHttp)
            http.Dispose();
    }
}
