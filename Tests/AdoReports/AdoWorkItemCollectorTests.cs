using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.ReportTests.AdoReports.AdoReportAssert;

namespace ProductivityInsights.ReportTests.AdoReports;

public static class AdoWorkItemCollectorTests
{
    public static async Task RunAsync()
    {
        await BatchKeysetAndSourceIsolationAsync();
        await ClosedDateMetadataAndCapturedLiteralsAsync();
        await RevisionPaginationAsync();
        await InvalidAndMissingPayloadsAsync();
        await CancellationAsync();
    }

    internal static ReportRequest Request(string analysis, params ReportSource[] sources) => new()
    {
        RunId = Guid.Parse("2dd49c61-71c5-4383-a3fa-00850684c21e"),
        Analysis = analysis, View = "details", ExecutedAt = DateTimeOffset.Parse("2020-02-01T00:00:00Z"),
        Start = DateTimeOffset.Parse("2020-01-01T12:34:56-08:00"),
        End = DateTimeOffset.Parse("2020-01-31T23:59:59-08:00"),
        TimeZone = "Pacific Standard Time", Sources = sources.ToImmutableArray()
    };

    internal static AdoReportCollector Collector(HttpClient client) => new(client, token =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult("fixture-token");
    });

    private static object WorkItem(int id, int revision = 1, string? closedAt = "2020-01-10T00:00:00Z")
    {
        var fields = new Dictionary<string, object>
        {
            ["System.Title"] = $"Item {id}", ["System.WorkItemType"] = "Task", ["System.State"] = "Done",
            ["System.AssignedTo"] = new { displayName = "Member", uniqueName = "member@example.com" },
            ["System.CreatedDate"] = "2020-01-01T00:00:00Z", ["System.ChangedDate"] = "2020-01-10T00:00:00Z",
            ["System.AreaPath"] = "Project\\Team", ["System.IterationPath"] = "Project\\Iteration"
        };
        if (closedAt is not null)
            fields["Microsoft.VSTS.Common.ClosedDate"] = closedAt;
        return new { id, rev = revision, fields };
    }

    private static object Revision(int id, int revision, string state = "Done") => new
    {
        id, rev = revision,
        fields = new Dictionary<string, object>
        {
            ["System.State"] = state,
            ["System.ChangedDate"] = DateTimeOffset.Parse("2020-01-01T00:00:00Z").AddMinutes(revision),
            ["System.ChangedBy"] = new { displayName = $"Actor {revision}", uniqueName = $"actor{revision}@example.com" }
        }
    };

    private static async Task BatchKeysetAndSourceIsolationAsync()
    {
        var batchSizes = new List<int>();
        var wiqlQueries = new List<string>();
        using var handler = new AdoReportTestHttp(async (message, token) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/wiql", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                var query = body.RootElement.GetProperty("query").GetString()!;
                wiqlQueries.Add(query);
                That(AdoReportTestHttp.Query(message, "$top") == "2000", "WIQL must use a bounded top.");
                That(query.Contains("[System.State] = 'Done'") && query.Contains("StateChangeDate") && !query.Contains("ClosedDate")
                    && query.Contains("UNDER 'Project\\Team'") && query.Contains("ASOF '"), "WIQL semantics or as-of missing.");
                That(query.Contains(">= '2020-01-01'") && query.Contains("<= '2020-01-31'"),
                    "Work-item effective bounds must remain date-only.");
                var values = query.Contains("[System.Id] > 0")
                    ? Enumerable.Range(1, 201).Select(id => new { id }).ToArray() : [];
                return AdoReportTestHttp.Json(new { workItems = values });
            }
            if (path.EndsWith("/workitemsbatch", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                var ids = body.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()).ToArray();
                batchSizes.Add(ids.Length);
                That(ids.Length <= 200 && body.RootElement.GetProperty("errorPolicy").GetString() == "Fail",
                    "Details must be strict batches of at most 200 IDs.");
                That(body.RootElement.GetProperty("fields").GetArrayLength() <= 12
                    && body.RootElement.TryGetProperty("asOf", out _), "Batch fields must be narrow and pinned as-of.");
                return AdoReportTestHttp.Json(new { value = ids.Select(id => WorkItem(id)).Reverse() });
            }
            if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                var id = int.Parse(path.Split('/')[^2]);
                return AdoReportTestHttp.Json(new { value = new[] { Revision(id, 1) } });
            }
            throw new InvalidOperationException($"Unexpected request {message.RequestUri}");
        });
        using var client = new HttpClient(handler);
        var request = Request("workItems",
            new("first", "org-a", "Project", "Project\\Team", Team: "Team"),
            new("overlap", "org-a", "Project", "Project\\Team", Team: "Team"),
            new("other-org", "org-b", "Project", "Project\\Team", Team: "Team"));
        var snapshot = await Collector(client).CollectAsync(request, null, CancellationToken.None);
        That(snapshot.WorkItems.Length == 603, "Overlapping source memberships must be retained.");
        That(snapshot.WorkItems.Select(item => item.EntityKey).Distinct().Count() == 402,
            "Work item IDs from different organizations must not collide.");
        That(batchSizes.SequenceEqual(new[] { 200, 1, 200, 1 }), "Overlapping details should reuse source-qualified data.");
        That(wiqlQueries.Any(query => query.Contains("[System.Id] > 201")), "WIQL must advance its ID keyset.");
        That(snapshot.CoverageNotes.Any(note => note.Contains("date-only")), "Effective date semantics must be reported.");
    }

    private static async Task ClosedDateMetadataAndCapturedLiteralsAsync()
    {
        const string origin = "Explicit literals captured at confirmation; no default timestamps inferred.";
        using var handler = new AdoReportTestHttp(async (message, token) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/wiql", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                var query = body.RootElement.GetProperty("query").GetString()!;
                That(query.Contains("[Microsoft.VSTS.Common.StateChangeDate] >= '2020-01-03'")
                    && query.Contains("[Microsoft.VSTS.Common.StateChangeDate] <= '2020-01-25'")
                    && !query.Contains("ClosedDate"), "Captured state-change date literals must control selection exactly.");
                That(query.Contains("UNDER 'Project\\Team'"), "An omitted area must retain the legacy project/team fallback.");
                return AdoReportTestHttp.Json(new { workItems = query.Contains("[System.Id] > 0")
                    ? new[] { new { id = 1 }, new { id = 2 } } : [] });
            }
            if (path.EndsWith("/workitemsbatch", StringComparison.Ordinal))
                return AdoReportTestHttp.Json(new { value = new[]
                {
                    WorkItem(1, closedAt: null),
                    WorkItem(2, closedAt: "2020-02-01T00:00:00Z")
                } });
            var id = int.Parse(path.Split('/')[^2]);
            return AdoReportTestHttp.Json(new { value = new[] { Revision(id, 1) } });
        });
        using var client = new HttpClient(handler);
        var request = Request("workItems", new ReportSource("s", "org", "Project", "", Team: "Team")) with
        {
            EffectiveStartLiteral = "2020-01-03",
            EffectiveEndLiteral = "2020-01-25",
            EffectiveBoundsOrigin = origin
        };
        var snapshot = await Collector(client).CollectAsync(request, null, CancellationToken.None);
        That(snapshot.WorkItems.Length == 2 && snapshot.WorkItems.Single(item => item.Id == 1).ClosedAt is null
            && snapshot.WorkItems.Single(item => item.Id == 2).ClosedAt == DateTimeOffset.Parse("2020-02-01T00:00:00Z"),
            "Missing or out-of-window ClosedDate must not exclude Done items selected by StateChangeDate.");
        That(snapshot.CoverageNotes.Contains(origin)
            && !snapshot.CoverageNotes.Any(note => note.Contains("unchanged UI query", StringComparison.Ordinal)),
            "Coverage must preserve bound origin without claiming intentional selection divergence.");
        var calls = handler.Requests.Count;
        await FailsAsync(() => Collector(client).CollectAsync(request with
        {
            EffectiveStartLiteral = "2020-01-03T12:00:00Z"
        }, null, CancellationToken.None), "date-only", "start");
        That(handler.Requests.Count == calls, "Malformed captured literals must fail before issuing WIQL.");
    }

    private static async Task RevisionPaginationAsync()
    {
        var pages = new List<string>();
        using var handler = new AdoReportTestHttp(async (message, token) =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/wiql", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                return AdoReportTestHttp.Json(new { workItems = body.RootElement.GetProperty("query").GetString()!.Contains("[System.Id] > 0")
                    ? new[] { new { id = 7 } } : [] });
            }
            if (message.RequestUri.AbsolutePath.EndsWith("/workitemsbatch", StringComparison.Ordinal))
                return AdoReportTestHttp.Json(new { value = new[] { WorkItem(7, 201) } });
            var skip = int.Parse(AdoReportTestHttp.Query(message, "$skip"));
            pages.Add(skip.ToString());
            var values = Enumerable.Range(skip + 1, Math.Min(200, 201 - skip))
                .Select(rev => Revision(7, rev, rev < 201 ? "Active" : "Done"));
            return AdoReportTestHttp.Json(new { value = values });
        });
        using var client = new HttpClient(handler);
        var snapshot = await Collector(client).CollectAsync(Request("workItems", new ReportSource("s", "org", "Project", "Project\\Team")),
            null, CancellationToken.None);
        That(pages.SequenceEqual(new[] { "0", "200" }), "Full histories must follow revision pagination.");
        var history = snapshot.WorkItems.Single().History;
        That(history.Length == 201 && history[^1].Actor.Name == "Actor 201"
            && history.Take(200).All(revision => revision.State == "Active")
            && history[0].EnteredAt < history[^1].EnteredAt,
            "Every revision, including unchanged states, must retain its actual timestamp/actor for duration-statistic parity.");
    }

    private static async Task InvalidAndMissingPayloadsAsync()
    {
        foreach (var mode in new[] { "invalid", "missing-id", "revision-gap", "forbidden" })
        {
            using var handler = new AdoReportTestHttp(async (message, token) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                if (mode == "forbidden")
                    return AdoReportTestHttp.Json(new { message = "denied" }, HttpStatusCode.Forbidden);
                if (path.EndsWith("/wiql", StringComparison.Ordinal))
                {
                    if (mode == "invalid") return AdoReportTestHttp.Json(new { count = 0 });
                    using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                    return AdoReportTestHttp.Json(new { workItems = body.RootElement.GetProperty("query").GetString()!.Contains("[System.Id] > 0")
                        ? new[] { new { id = 7 } } : [] });
                }
                if (path.EndsWith("/workitemsbatch", StringComparison.Ordinal))
                    return AdoReportTestHttp.Json(new { value = mode == "missing-id" ? System.Array.Empty<object>() : new[] { WorkItem(7, 2) } });
                return AdoReportTestHttp.Json(new { value = new[] { Revision(7, 2) } });
            });
            using var client = new HttpClient(handler);
            await FailsAsync(() => Collector(client).CollectAsync(
                Request("workItems", new ReportSource("s", "org", "Project", "Project\\Team")), null, CancellationToken.None),
                "org/Project", mode switch { "invalid" => "workItems", "missing-id" => "omitted", "revision-gap" => "contiguous", _ => "403" });
        }
    }

    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new AdoReportTestHttp((_, _) => throw new InvalidOperationException("HTTP must not run after cancellation."));
        using var client = new HttpClient(handler);
        cancellation.Cancel();
        try
        {
            await Collector(client).CollectAsync(
                Request("workItems", new ReportSource("s", "org", "Project", "Project\\Team")), null, cancellation.Token);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        That(handler.Requests.Count == 0, "Cancelled work must not acquire data.");
    }
}
