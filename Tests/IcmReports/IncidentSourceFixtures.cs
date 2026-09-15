using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Azure.Core;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.IcmReports;

public static class IncidentSourceFixtures
{
    public static async Task RunAsync()
    {
        SchemaAndRetention();
        Coverage();
        Responses();
        await Transport();
    }

    private static void SchemaAndRetention()
    {
        var schema = """
            {"Name":"IncidentHistory","OrderedColumns":[
            {"Name":"HistoryId","Type":"System.Int64","CslType":"long"},
            {"Name":"IncidentId","Type":"System.Int64","CslType":"long"},
            {"Name":"ChangeDate","Type":"System.DateTime","CslType":"datetime"},
            {"Name":"ChangedBy","Type":"System.String","CslType":"string"},
            {"Name":"ChangeCategories","Type":"System.String","CslType":"string"}]}
            """;
        IncidentReportSourceVerification.ValidateSchema("IncidentHistory", schema);
        Fails(() => IncidentReportSourceVerification.ValidateSchema("IncidentHistory", schema.Replace("\"HistoryId\"", "\"ActionId\"")));
        Fails(() => IncidentReportSourceVerification.ValidateSchema("IncidentHistory", schema.Replace("\"datetime\"", "\"string\"")));
        Fails(() => IncidentReportSourceVerification.ValidateSchema("IncidentHistory", "{}"));
        var inherited = IncidentReportSourceVerification.ParseRetention("null",
            """{"SoftDeletePeriod":"365000.00:00:00","Recoverability":"Enabled"}""");
        Check(inherited.SoftDeletePeriod.TotalDays == 365000, "Inherited policy was confused with missing or unlimited retention.");
        var tablePolicy = IncidentReportSourceVerification.ParseRetention(
            """{"SoftDeletePeriod":"740.00:00:00","Recoverability":"Enabled"}""", "null");
        Check(tablePolicy.SoftDeletePeriod.TotalDays == 740, "Real Kusto retention format parsed incorrectly.");
        Fails(() => IncidentReportSourceVerification.ParseRetention("null", "null"));
        Fails(() => IncidentReportSourceVerification.ParseRetention("{}", null));
        Fails(() => IncidentReportSourceVerification.ParseRetention(
            """{"SoftDeletePeriod":"-1.00:00:00","Recoverability":"Enabled"}""", null));
        Fails(() => IncidentReportSourceVerification.ParseRetention(
            """{"SoftDeletePeriod":"740.00:00:00","Recoverability":"maybe"}""", null));
    }

    private static void Coverage()
    {
        var options = IncidentCollectorFixtures.Configuration;
        var request = new IncidentReportCoverageRequest(options.ClusterUri, options.Database, "1", ["Team"],
            IncidentCollectorFixtures.Start, IncidentCollectorFixtures.End, IncidentCollectorFixtures.AsOf,
            ImmutableDictionary<string, IncidentReportRetention>.Empty);
        var evidence = new IncidentReportCoverage(options.ClusterUri, options.Database, "1", ["Team"],
            request.Start, request.End, request.AsOf, request.AsOf,
            "Synthetic source-owner event-time coverage", "Synthetic ingestion and tombstone retention semantics");
        IncidentReportSourceVerification.ValidateCoverage(request, evidence);
        foreach (var invalid in new IncidentReportCoverage?[]
        {
            null, evidence with { ClusterUri = "https://other.kusto.windows.net" },
            evidence with { Database = "Other" }, evidence with { SiloId = "2" }, evidence with { Teams = ["Other"] },
            evidence with { HistoryFrom = request.Start.AddTicks(1) },
            evidence with { HistoryThrough = request.End.AddTicks(-1) },
            evidence with { LatestIncidentsThrough = request.AsOf.AddTicks(-1) },
            evidence with { PurgesThrough = request.AsOf.AddTicks(-1) },
            evidence with { Evidence = "" }, evidence with { RetentionSemantics = "" }
        })
            Fails(() => IncidentReportSourceVerification.ValidateCoverage(request, invalid));
    }

    private static void Responses()
    {
        var query = QueryResponse();
        Check(IncidentReportQueryClient.ParseResponse(query, false).Rows.Length == 1, "Complete query result rejected.");
        var management = JsonSerializer.SerializeToElement(new
        {
            Tables = new object[]
            {
                new { TableName = "Table_0", Columns = new[] { new { ColumnName = "Schema" } }, Rows = new[] { new object[] { "synthetic" } } },
                new { TableName = "Table_2", Columns = new[] { new { ColumnName = "StatusCode" }, new { ColumnName = "Severity" } },
                    Rows = new[] { new object[] { 0, 4 } } }
            }
        });
        Check(IncidentReportQueryClient.ParseResponse(management, true).Rows.Length == 1, "Complete management result rejected.");
        foreach (var invalid in new[]
        {
            QueryResponse(hasErrors: true), QueryResponse(cancelled: true), QueryResponse(status: 1),
            QueryResponse(level: 3), QueryResponse(completion: false), QueryResponse(statusTable: false),
            JsonSerializer.SerializeToElement(new { error = "synthetic" }),
            JsonSerializer.SerializeToElement(Array.Empty<object>())
        })
            Fails(() => IncidentReportQueryClient.ParseResponse(invalid, false));
        Fails(() => IncidentReportQueryClient.ParseResponse(JsonSerializer.SerializeToElement(new
        {
            Tables = new[] { new { TableName = "Table_0", Columns = Array.Empty<object>(), Rows = Array.Empty<object>() } }
        }), true));
        Fails(() => IncidentReportQueryClient.ParseResponse(QueryResponse(rowCount: IncidentReportCollector.MaximumRowsPerQuery + 1), false));
        try
        {
            IncidentReportQueryClient.ParseResponse(QueryResponse(hasErrors: true, statusTable: false,
                failureCode: "LimitsExceeded",
                failureMessage: "DO_NOT_SURFACE_SYNTHETIC_SOURCE_TEXT: join memory exhausted (E_RUNAWAY_QUERY)"), false);
            throw new InvalidOperationException("Server-side memory failure was accepted.");
        }
        catch (IncidentReportExportException exception)
        {
            Check(exception.Message.Contains("E_RUNAWAY_QUERY", StringComparison.Ordinal)
                && exception.Message.Contains("memory", StringComparison.OrdinalIgnoreCase)
                && !exception.Message.Contains("DO_NOT_SURFACE", StringComparison.Ordinal),
                "The resource-limit diagnostic must identify the cause without exposing raw source text.");
        }
    }

    private static async Task Transport()
    {
        foreach (var management in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancellationReachedTransport = false;
            using var handler = new Handler(async (request, token) =>
            {
                Check(request.RequestUri!.AbsolutePath == (management ? "/v1/rest/mgmt" : "/v2/rest/query"), "Incorrect Kusto endpoint.");
                using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                using var properties = JsonDocument.Parse(content.RootElement.GetProperty("properties").GetString()!);
                Check(properties.RootElement.GetProperty("Options").GetProperty("request_readonly").GetBoolean(),
                    "Transport must set request_readonly.");
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancellationReachedTransport = token.IsCancellationRequested; }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var http = new HttpClient(handler);
            using var client = new IncidentReportQueryClient(IncidentCollectorFixtures.Configuration, http, new Credential());
            using var cancellation = new CancellationTokenSource();
            var pending = client.ExecuteAsync(new("synthetic", management, ImmutableDictionary<string, object>.Empty), cancellation.Token);
            await entered.Task;
            cancellation.Cancel();
            await IncidentCollectorFixtures.ExpectCancellation(() => pending);
            Check(cancellationReachedTransport, "Cancellation abandoned an in-flight query/admin request.");
        }
        using var forbiddenHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("DO_NOT_SURFACE_SYNTHETIC_INCIDENT_TEXT")
        }));
        using var forbiddenHttp = new HttpClient(forbiddenHandler);
        using var forbidden = new IncidentReportQueryClient(IncidentCollectorFixtures.Configuration, forbiddenHttp, new Credential());
        try
        {
            await forbidden.ExecuteAsync(new("synthetic", false, ImmutableDictionary<string, object>.Empty), CancellationToken.None);
            throw new InvalidOperationException("Required source permission failure was swallowed.");
        }
        catch (IncidentReportExportException exception)
        {
            Check(exception.Message.Contains("403") && !exception.Message.Contains("DO_NOT_SURFACE"), "Unsafe or unactionable source error.");
        }
        var coverageRequest = new IncidentReportCoverageRequest("https://synthetic.kusto.windows.net", "SyntheticIcm", "1", ["Team"],
            IncidentCollectorFixtures.Start, IncidentCollectorFixtures.End, IncidentCollectorFixtures.AsOf,
            IncidentReportSourceVerification.RequiredSchema.Keys.ToImmutableDictionary(
                table => table, _ => new IncidentReportRetention(TimeSpan.FromDays(740), "Enabled")));
        await IncidentCollectorFixtures.ExpectFailure(
            () => forbidden.GetCoverageAsync(coverageRequest, CancellationToken.None),
            "Default source must not invent coverage from TTL.");

        var evidence = new IncidentReportCoverage(coverageRequest.ClusterUri, coverageRequest.Database, "1", ["Team"],
            coverageRequest.Start, coverageRequest.End, coverageRequest.AsOf, coverageRequest.AsOf,
            "Synthetic retained-warehouse contract", "Synthetic event-time retention and history purge contract")
        {
            MinimumRetention = IncidentReportSourceVerification.RequiredSchema.Keys.ToImmutableDictionary(
                table => table, _ => TimeSpan.FromDays(740))
        };
        var provider = new IncidentReportConfiguredCoverageProvider(
            Microsoft.Extensions.Options.Options.Create(evidence));
        using var configured = new IncidentReportQueryClient(IncidentCollectorFixtures.Configuration,
            forbiddenHttp, new Credential(), provider);
        Check(await configured.GetCoverageAsync(coverageRequest, CancellationToken.None) == evidence,
            "Production transport must support configured, validated coverage without replacing the query client.");
        await IncidentCollectorFixtures.ExpectFailure(
            () => configured.GetCoverageAsync(coverageRequest with { Database = "Other" }, CancellationToken.None),
            "Configured evidence must not cross source boundaries.");
        await IncidentCollectorFixtures.ExpectFailure(
            () => configured.GetCoverageAsync(coverageRequest with { Start = coverageRequest.Start.AddTicks(-1) }, CancellationToken.None),
            "Configured evidence must not cover an earlier unverified window.");
        await IncidentCollectorFixtures.ExpectFailure(
            () => configured.GetCoverageAsync(coverageRequest with { AsOf = coverageRequest.AsOf.AddTicks(1) }, CancellationToken.None),
            "Expired source-view evidence must fail.");
        await IncidentCollectorFixtures.ExpectFailure(
            () => configured.GetCoverageAsync(coverageRequest with
            {
                Retention = coverageRequest.Retention.SetItem("IncidentHistory",
                    new IncidentReportRetention(TimeSpan.FromDays(1), "Enabled"))
            }, CancellationToken.None),
            "Reduced effective retention must invalidate the configured source contract.");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await IncidentCollectorFixtures.ExpectCancellation(
            () => configured.GetCoverageAsync(coverageRequest, cancellationSource.Token));
    }

    private static JsonElement QueryResponse(bool hasErrors = false, bool cancelled = false, int status = 0,
        int level = 4, bool completion = true, bool statusTable = true, int rowCount = 1,
        string? failureCode = null, string? failureMessage = null)
    {
        var frames = new List<object>
        {
            new { FrameType = "DataSetHeader" },
            new { FrameType = "DataTable", TableKind = "PrimaryResult",
                Columns = new[] { new { ColumnName = "IncidentId" } },
                Rows = Enumerable.Range(0, rowCount).Select(index => new object[] { index + 1 }).ToArray() }
        };
        if (statusTable)
            frames.Add(new { FrameType = "DataTable", TableKind = "QueryCompletionInformation",
                Columns = new[] { new { ColumnName = "StatusCode" }, new { ColumnName = "Level" } },
                Rows = new[] { new object[] { status, level } } });
        if (completion)
            frames.Add(new
            {
                FrameType = "DataSetCompletion", HasErrors = hasErrors, Cancelled = cancelled,
                OneApiErrors = failureCode is null ? Array.Empty<object>() :
                    [new { error = new Dictionary<string, string?> { ["code"] = failureCode, ["@message"] = failureMessage } }]
            });
        return JsonSerializer.SerializeToElement(frames);
    }

    private static void Fails(System.Action action)
    {
        try { action(); }
        catch (IncidentReportExportException) { return; }
        throw new InvalidOperationException("Unverified source response/coverage was accepted.");
    }

    private static void Check(bool condition, string message) => IncidentCollectorFixtures.Check(condition, message);

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-test-token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
