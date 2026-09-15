using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class IncidentReferenceFormatTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var alex = new ReportIdentity("Alex Example", "alex@example.test", "alex");
        var blair = new ReportIdentity("Blair Example", "blair@example.test", "blair");
        var casey = new ReportIdentity("Casey Example", "casey@example.test", "casey");
        var zero = new ReportIdentity("Zero Example", "zero@example.test", "zero");
        var active = new IncidentReportRecord
        {
            SourceKey = "alpha", Id = "1", SiloId = "silo", Title = "Active fixture", Severity = 2, Status = "Active",
            CreatedAt = start, ModifiedAt = start.AddDays(1), ImpactStart = start.AddHours(-1),
            Owner = alex, Active = true, Created = true, OwningTeam = @"SERVICE\Alpha", OwningTenant = "Example Tenant",
            Url = "https://example.test/incidents/1", Metadata = ImmutableDictionary<string, string>.Empty.Add("IsOutage", "True")
        };
        var resolved = active with
        {
            Id = "2", Title = "Resolved fixture", Status = "Resolved", Active = false, Created = false, Resolved = true,
            MitigatedAt = start.AddHours(2), ResolvedAt = start.AddHours(26), MitigatedBy = alex, ResolvedBy = blair,
            Mitigation = "Complete mitigation\nwith ``` fences and <unsafe> content"
        };
        var mitigated = resolved with
        {
            Id = "3", SourceKey = "beta", Title = "Mitigated fixture", OwningTeam = @"SERVICE\Beta",
            Status = "Mitigated", Resolved = false, Mitigated = true, ResolvedAt = null, ResolvedBy = new("Unknown")
        };
        var actionOnly = active with
        {
            Id = "4", SourceKey = "beta", Active = false, Created = false, Title = "Action-only fixture",
            OwningTeam = @"SERVICE\Beta", Severity = null, ImpactStart = null
        };
        var action = new IncidentActionReportRecord("alpha", "1", "silo", "action-1", new("alex"), start.AddMinutes(1), "Updated");
        var snapshot = new ReportSnapshot
        {
            Request = new ReportRequest
            {
                RunId = Guid.NewGuid(), Analysis = "incidentManagement", View = "teams", Start = start,
                End = start.AddMonths(1).AddTicks(-1), ExecutedAt = start.AddMonths(1), TimeZone = "UTC",
                Roster = [alex, blair, casey, zero], Manager = "Example Manager",
                Sources = [new("alpha", "", "", @"SERVICE\Alpha"), new("beta", "", "", @"SERVICE\Beta")]
            },
            AsOf = start.AddMonths(1),
            Incidents = [active, active with { SourceKey = "beta" }, resolved, mitigated, actionOnly],
            IncidentActions =
            [
                action, action with { SourceKey = "beta" },
                action with { ActionId = "action-2", Actor = alex },
                action with { ActionId = "action-3", Actor = blair },
                action with { IncidentId = "2", ActionId = "action-4", Actor = alex },
                action with { SourceKey = "beta", IncidentId = "4", ActionId = "action-5", Actor = casey }
            ]
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlSummaryReportWriter.Write(document, "incidents.md");
        var markdown = MarkdownReportWriter.Write(document);
        var tables = Bodies(html);
        Require(tables.Length == 2 && tables[0].Length == 5 && tables[1].Length == 5,
            "Reference HTML has activity-only source rows, one zero placeholder, and an overall total.");
        Require(tables[0][0].SequenceEqual(new[] { "Alex Example", @"SERVICE\Alpha", "1", "1", "0", "0", "2" })
            && tables[0][1].SequenceEqual(new[] { "Alex Example", @"SERVICE\Beta", "1", "1", "1", "0", "1" }),
            "Rows use readable members, exact ICM paths and existing source credits, ordered by activity.");
        Require(tables[0][^1].SequenceEqual(new[] { "Zero Example", "\u2014", "0", "0", "0", "0", "0" }),
            "Inactive member/source matrices collapse into one zero row.");
        Require(tables[1][0].SequenceEqual(new[] { "Alex Example", "1", "1", "1", "0", "2" }),
            "Overall credit deduplicates overlapping source membership and actor aliases.");
        Require(tables[1][^1].SequenceEqual(new[] { "\U0001F3C1 TOTAL", "1", "1", "1", "1", "4" }),
            "TOTAL sums contributor credits, not source rows or unique incidents across different actors.");
        Require(tables[0].Any(row => row[0] == "Casey Example" && row[^1] == "1"),
            "Action-only contributor rows remain visible.");
        Require(html.Contains("<h1>Team Incident Resolutions Analysis", StringComparison.Ordinal)
            && !html.Contains("<h1></h1>", StringComparison.Ordinal), "Do not reproduce the reference's empty-title defect.");
        Require(html.Contains("<div class=\"meta\">Generated 2026-08-01 00:00:00</div>", StringComparison.Ordinal)
            && html.Contains("table-layout:auto", StringComparison.Ordinal)
            && html.Contains("color:#808080", StringComparison.Ordinal), "Reference timestamp, dark style and automatic table layout are preserved.");
        Require(html.Contains("ACROSS ALL 2 ICM GROUP(S)", StringComparison.Ordinal)
            && html.IndexOf("<table>", StringComparison.Ordinal) < html.IndexOf("<details class=\"report-context\">", StringComparison.Ordinal),
            "ICM group count is dynamic and context does not displace the main summaries.");
        foreach (var icon in new[] { "\U0001F464", "\U0001F4C2", "\U0001F7E2", "\U0001F195", "\U0001F527", "\u2705", "\U0001F4AC", "\U0001F3C1" })
            Require(html.Contains(icon, StringComparison.Ordinal) && markdown.Contains(icon, StringComparison.Ordinal),
                "Both formats retain all reference summary icons.");
        Require(markdown.Contains("Incident Details:", StringComparison.Ordinal)
            && markdown.Contains("Owning Team Name:", StringComparison.Ordinal)
            && markdown.Contains("Contributors with recorded in-period actions", StringComparison.Ordinal)
            && markdown.Contains("TTR: 1d 2h", StringComparison.Ordinal), "Markdown uses compact incident field blocks with evidence-based contributors and durations.");
        var details = markdown.IndexOf("TEAM INCIDENT DETAILS BY ICM PATH", StringComparison.Ordinal);
        var summary = markdown.IndexOf("\U0001F4CA TEAM INCIDENT SUMMARY BY ICM PATH", StringComparison.Ordinal);
        var context = markdown.IndexOf("Captured inputs and export query context", StringComparison.Ordinal);
        Require(details >= 0 && summary > details && context > summary, "Details and boxed reference summaries precede the evidence appendix.");
        Require(markdown.Contains("TTD by severity", StringComparison.Ordinal) && markdown.Contains("TTR by severity", StringComparison.Ordinal)
            && markdown.Contains("action-5", StringComparison.Ordinal) && markdown.Contains(resolved.Mitigation, StringComparison.Ordinal)
            && markdown.Contains("Action-only fixture", StringComparison.Ordinal), "Full durations, all incidents, mitigation and action records remain in the appendix.");
        Require(!markdown.Contains("Retrieving certificate", StringComparison.Ordinal) && !markdown.Contains("Fetching page", StringComparison.Ordinal)
            && !markdown.Contains("TEAM HIERARCHY", StringComparison.Ordinal), "Do not fabricate authentication logs, API progress or hierarchy levels.");
        Require(document.Incidents.Length == 4 && document.IncidentActions.Length == 5
            && document.OverallRows.Sum(row => row.Contributed) == 4, "Formatting does not change incident or contribution metrics.");

        var empty = snapshot with { Incidents = [], IncidentActions = [] };
        var emptyTables = Bodies(HtmlSummaryReportWriter.Write(empty, "empty.md"));
        Require(emptyTables[0].Length == 4 && emptyTables[1].Length == 5
            && emptyTables[0].All(row => row[1] == "\u2014") && emptyTables[1][^1].Skip(1).All(cell => cell == "0"),
            "Complete empty snapshots retain roster zeros and a zero total.");
        var unavailable = empty with
        {
            SkippedSources = empty.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "Unavailable")).ToImmutableArray()
        };
        var unavailableTables = Bodies(HtmlSummaryReportWriter.Write(unavailable, "unavailable.md"));
        Require(unavailableTables[0].Length == 0 && unavailableTables[1].Length == 0,
            "Unavailable data must not acquire fabricated zero totals.");
        var duplicateName = snapshot with
        {
            Request = snapshot.Request with { Roster = [alex, blair with { Name = alex.Name }, casey, zero] }
        };
        var names = Bodies(HtmlSummaryReportWriter.Write(duplicateName, "names.md"))[1].Select(row => row[0]).ToArray();
        Require(names.Any(name => name.Contains("alex@example.test", StringComparison.Ordinal))
            && names.Any(name => name.Contains("blair@example.test", StringComparison.Ordinal)),
            "Compact labels must retain identity qualification for equal names.");
        var unresolved = snapshot with
        {
            IncidentActions = snapshot.IncidentActions.Add(action with { ActionId = "unknown", Actor = new("Unmatched Alias") })
        };
        Require(Bodies(HtmlSummaryReportWriter.Write(unresolved, "unknown.md"))[1]
            .Any(row => row[0].Contains("unresolved identity", StringComparison.Ordinal)), "Unresolved action actors cannot be guessed.");
        var hostile = snapshot with
        {
            Request = snapshot.Request with { Sources = [new("alpha", "", "", @"<script>\Team|A"), snapshot.Request.Sources[1]] },
            Incidents = snapshot.Incidents.Select(item => item with { Title = "<img src=x> | title", Url = "javascript:alert(1)" }).ToImmutableArray()
        };
        Require(HtmlSummaryReportWriter.Write(hostile, "safe.md").Contains("&lt;script&gt;", StringComparison.Ordinal)
            && !HtmlSummaryReportWriter.Write(hostile, "safe.md").Contains("<script>", StringComparison.Ordinal)
            && !MarkdownReportWriter.Write(hostile).Contains("](<javascript:", StringComparison.Ordinal), "Reference layouts retain encoded text and safe links.");
        var detailsOnly = snapshot with { Request = snapshot.Request with { View = "details", Roster = [] } };
        Require(!HtmlSummaryReportWriter.Write(detailsOnly, "details.md").Contains("<details class=\"report-context\">", StringComparison.Ordinal)
            && !MarkdownReportWriter.Write(detailsOnly).Contains("TEAM INCIDENT DETAILS BY ICM PATH", StringComparison.Ordinal),
            "Incident Details layout remains unchanged.");
    }

    private static string[][][] Bodies(string html) => Regex.Matches(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline)
        .Select(body => Regex.Matches(body.Groups[1].Value, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
            .Select(row => Regex.Matches(row.Groups[1].Value, @"<td>(.*?)</td>", RegexOptions.Singleline)
                .Select(cell => WebUtility.HtmlDecode(cell.Groups[1].Value)).ToArray()).ToArray()).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Incident reference format: " + message);
    }
}
