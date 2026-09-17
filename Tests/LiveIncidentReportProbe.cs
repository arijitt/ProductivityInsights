using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Options;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests;

internal static class LiveIncidentReportProbe
{
    internal static async Task RunAsync(string[] args)
    {
        string Required(string option)
        {
            var index = Array.IndexOf(args, option);
            if (index < 0 || index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"Required live Incident probe argument: {option}");
            return args[index + 1];
        }
        var cluster = Required("--cluster");
        var database = Required("--database");
        var team = Required("--scope");
        var probeUnkeyed = args.Contains("--member-from-unkeyed-action");
        if (probeUnkeyed && args.Contains("--member"))
            throw new ArgumentException("Choose --member or --member-from-unkeyed-action, not both.");
        var member = probeUnkeyed ? "" : Required("--member");
        var start = DateTimeOffset.Parse(Required("--start"), CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(Required("--end"), CultureInfo.InvariantCulture);
        if (end < start || end - start > TimeSpan.FromDays(31))
            throw new ArgumentException("Live Incident probe requires a nonnegative window of no more than 31 days.");

        var services = new ServiceCollection();
        services.AddOptions<IncidentManagementKustoOptions>().Configure(options =>
        {
            options.ClusterUri = cluster;
            options.Database = database;
            options.QueryTimeoutSeconds = 180;
        });
        services.AddReportExports(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        if (probeUnkeyed)
        {
            var queryClient = scope.ServiceProvider.GetRequiredService<IIncidentReportQueryClient>();
            var parameters = new Dictionary<string, object>
            {
                ["owningTeam"] = team, ["startDate"] = start.UtcDateTime, ["endDate"] = end.UtcDateTime,
                ["asOf"] = DateTime.UtcNow, ["rowLimit"] = 1
            }.ToImmutableDictionary();
            var evidence = await queryClient.ExecuteAsync(new(IncidentReportCollector.QueryPrefix
                + "\nactions | where isnull(HistoryId) and isnotempty(ChangedBy) | project ChangedBy | take 1",
                false, parameters), timeout.Token);
            if (!evidence.IsComplete || evidence.Rows.IsDefault || evidence.Rows.Length != 1
                || !evidence.Rows[0].TryGetValue("ChangedBy", out var actor)
                || actor.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(actor.GetString()))
                throw new InvalidOperationException("No in-scope unkeyed action was found; the live regression cannot be verified.");
            member = actor.GetString()!;
        }
        var request = new ReportRequest
        {
            RunId = Guid.NewGuid(), Analysis = "incidentManagement", View = "teams",
            ExecutedAt = DateTimeOffset.UtcNow, RequestedStart = start.UtcDateTime, RequestedEnd = end.UtcDateTime,
            Start = start, End = end, TimeZone = "UTC",
            Sources = [new("live-incident-probe", "", "", team)], Roster = [new(member)]
        };
        var collector = scope.ServiceProvider.GetRequiredService<IReportCollector>();
        var snapshot = await collector.CollectAsync(request, null, timeout.Token);
        var missingIds = snapshot.IncidentActions.Count(action => action.ActionId.Length == 0);
        if (probeUnkeyed && missingIds == 0)
            throw new InvalidOperationException("The in-scope unkeyed action disappeared from the collected report.");
        using var content = ReportArchiveBuilder.Build(snapshot, timeout.Token).Content;
        using var zip = new ZipArchive(content, ZipArchiveMode.Read);
        if (zip.Entries.Count != 2 || zip.Entries.Count(entry => entry.Name.EndsWith("_Detailed.html", StringComparison.Ordinal)) != 1
            || zip.Entries.Count(entry => entry.Name.EndsWith(".html", StringComparison.Ordinal)) != 2)
            throw new InvalidOperationException("Live Incident export did not produce the detailed/summary HTML pair.");
        if (probeUnkeyed)
        {
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync(timeout.Token);
                if (!text.Contains("no HistoryId is invented", StringComparison.Ordinal)
                    || !text.Contains("Not provided", StringComparison.Ordinal))
                    throw new InvalidOperationException("The missing warehouse ID was not disclosed in both exported formats.");
            }
        }
        Console.WriteLine($"PASS live Teams Incident export: incidents={snapshot.Incidents.Length}, "
            + $"memberActions={snapshot.IncidentActions.Length}, actionsWithoutHistoryId={missingIds}, ZIP bytes={content.Length}. "
            + "Production registrations/transport/collector, no coverage configuration; no report data saved.");
    }
}
