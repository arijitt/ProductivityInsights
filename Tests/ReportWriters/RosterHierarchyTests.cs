using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Models.Teams;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class RosterHierarchyTests
{
    public static void Run()
    {
        VerifyHierarchy("incidentManagement");
        VerifyHierarchy("gitCommits");
        VerifyHierarchy("workItems");
    }

    private static void VerifyHierarchy(string analysis)
    {
        var component = ExportIntegrationTests.PreparedComponent(analysis);
        var root = (OrgMember)ExportIntegrationTests.Get(component, "orgTreeRoot")!;
        var contributor = root.Reports[0];
        var lead = new OrgMember
        {
            Id = "lead-id", DisplayName = "Same <Name> & Lead", Mail = "lead@example.test", Reports = [contributor]
        };
        var peer = new OrgMember
        {
            Id = "peer-id", DisplayName = "Same <Name> & Lead", Mail = "peer@example.test"
        };
        root.Reports = [lead, peer];
        ExportIntegrationTests.Set(component, "analysisOperationVersion", 2);
        ExportIntegrationTests.Call(component, "BeginReportExportContext", 2);
        ExportIntegrationTests.Call(component, "CompleteReportExportContext", 2, true);
        var request = ExportIntegrationTests.Request(component);
        Require(request.RosterParentIndices.SequenceEqual(new int?[] { null, 0, 1, 0 })
            && request.Roster.Select(member => member.Email).SequenceEqual(
                new[] { "manager@example.test", "lead@example.test", "dev@example.test", "peer@example.test" }),
            "Capture must preserve the actual tree and sibling order, including collapsed branches and duplicate names.");

        lead.DisplayName = "Changed after capture";
        contributor.Mail = "changed@example.test";
        root.Reports.Clear();
        ExportIntegrationTests.Set(component, "orgTreeRoot", null);
        ExportIntegrationTests.Set(component, "selectedManager", null);
        ExportIntegrationTests.Call(component, "HideReportExportContext", true);
        ExportIntegrationTests.Call(component, "RestoreReportExportContext", "teams:" + analysis);
        Require(ReferenceEquals(request, ExportIntegrationTests.Request(component))
            && request.Roster[1].Name == "Same <Name> & Lead" && request.Roster[2].Email == "dev@example.test"
            && request.RosterParentIndices.SequenceEqual(new int?[] { null, 0, 1, 0 }),
            "Live tree edits, manager changes and tab cache restores must not mutate captured relationships.");
        var restored = JsonSerializer.Deserialize<ReportRequest>(JsonSerializer.Serialize(request))!;
        Require(restored.Roster.SequenceEqual(request.Roster)
            && restored.RosterParentIndices.SequenceEqual(request.RosterParentIndices),
            "Snapshot serialization must retain identities and parent references.");

        var snapshot = ExportIntegrationTests.Snapshot(request);
        if (analysis == "gitCommits")
            snapshot = snapshot with
            {
                Commits = snapshot.Commits.Select(commit => commit with { Committer = request.Roster[1] }).ToImmutableArray(),
                PullRequests = [snapshot.PullRequests[0] with { CompletedBy = request.Roster[3] }]
            };
        var html = HtmlDetailedReportWriter.Write(snapshot);
        var treeStart = html.IndexOf("<ul class=\"report-roster-hierarchy\"", StringComparison.Ordinal);
        Require(treeStart >= 0, "The TEAM ROSTER must contain a reporting tree.");
        var treeEnd = html.IndexOf("<hr class=\"report-rule\" /></div>", treeStart, StringComparison.Ordinal);
        Require(treeStart >= 0 && treeEnd > treeStart, "The TEAM ROSTER must contain a complete reporting tree.");
        var tree = XElement.Parse(html[treeStart..treeEnd]);
        var nodes = tree.Descendants("li").ToArray();
        Require(nodes.Length == request.Roster.Length, "Hierarchy rendering must not omit or duplicate members.");
        for (var index = 0; index < nodes.Length; index++)
        {
            var parent = nodes[index].Parent?.Parent;
            int? parentIndex = parent?.Name == "li" ? Array.IndexOf(nodes, parent) : null;
            var link = nodes[index].Element("span")?.Element("a");
            Require(parentIndex == request.RosterParentIndices[index]
                && link is not null && link.Value.Contains(request.Roster[index].Email!, StringComparison.Ordinal),
                "Rendered parent/report edges and member identities must match the captured tree exactly.");
            var target = (string?)link!.Attribute("href") ?? "";
            var targetTag = analysis == "gitCommits" && index == 3 ? "tr" : "h3";
            Require(Regex.IsMatch(target, @"^#report-member-[0-9]+$")
                && Regex.IsMatch(html, "<" + targetTag + @"\b[^>]*\bid=""" + Regex.Escape(target[1..]) + @"""[^>]*>"),
                "Every member must keep its existing, resolved detail hyperlink.");
        }
        if (analysis == "gitCommits")
            Require((string?)nodes[1].Element("span")!.Element("a")!.Attribute("href")
                == (string?)nodes[2].Element("span")!.Element("a")!.Attribute("href"),
                "The hierarchy must preserve shared author/committer detail destinations.");
        Require(!html.Contains("<Name>", StringComparison.Ordinal), "Recorded names must remain encoded in the tree.");

        var flat = snapshot with { Request = request with { RosterParentIndices = [] } };
        Require(WithoutRoster(html) == WithoutRoster(HtmlDetailedReportWriter.Write(flat)),
            "Hierarchy must change only TEAM ROSTER, not styles, detail ordering, IDs, tables or other report content.");
        Require(HtmlSummaryReportWriter.Write(snapshot, "report.html") == HtmlSummaryReportWriter.Write(flat, "report.html")
            && MarkdownReportWriter.Write(snapshot) == MarkdownReportWriter.Write(flat),
            "Summary HTML and legacy Markdown must remain unchanged.");
        Require(HtmlDetailedReportWriter.Write(flat).Contains("reporting hierarchy was not captured for this run", StringComparison.Ordinal),
            "Older flat snapshots must disclose missing hierarchy, never infer reporting relationships from name/order.");
        if (analysis == "gitCommits") VerifySkippedSources(snapshot);

        foreach (var parents in new ImmutableArray<int?>[]
        {
            default, [null, 0], [0, 0, 1, 0], [null, null, 1, 0], [null, -1, 1, 0],
            [null, 2, 1, 0], [null, 0, 2, 0], [null, 0, 0, 1]
        })
            Throws<InvalidOperationException>(() => ReportDocumentBuilder.Build(snapshot with
                { Request = request with { RosterParentIndices = parents } }));

        var deepRoster = Enumerable.Range(0, ReportRenderingLimits.MaximumRosterDepth + 2)
            .Select(index => new ReportIdentity($"Depth {index}", $"depth{index}@example.test")).ToImmutableArray();
        Throws<ReportSizeLimitException>(() => ReportDocumentBuilder.Build(snapshot with
        {
            Request = request with
            {
                Roster = deepRoster,
                RosterParentIndices = Enumerable.Range(0, deepRoster.Length).Select(index => index == 0 ? (int?)null : index - 1).ToImmutableArray()
            }
        }));
        var cyclic = ExportIntegrationTests.PreparedComponent(analysis);
        var cycleRoot = (OrgMember)ExportIntegrationTests.Get(cyclic, "orgTreeRoot")!;
        cycleRoot.Reports.Add(cycleRoot);
        ExportIntegrationTests.Call(cyclic, "BeginReportExportContext", 2);
        Require(ExportIntegrationTests.Get(cyclic, "displayedExportRun") is null
            && ((string)ExportIntegrationTests.Get(cyclic, "reportExportError")!).Contains("cycle", StringComparison.Ordinal)
            && ((List<(string Name, string Summary)>)ExportIntegrationTests.Get(cyclic, "contributors")!).Count == 1,
            "Invalid hierarchy must fail only export capture without altering or truncating the online analysis.");
    }

    private static void VerifySkippedSources(ReportSnapshot snapshot)
    {
        var missingSource = snapshot.Request.Sources[0] with { Key = "missing", Scope = "Unavailable <repo> & path" };
        var allSkipped = snapshot with
        {
            Commits = [], PullRequests = [],
            SkippedSources = snapshot.Request.Sources.Select(source =>
                new ReportSkippedSource(source.Key, "Source unavailable (HTTP 404).")).ToImmutableArray()
        };
        var partiallySkipped = snapshot with
        {
            Request = snapshot.Request with { Sources = snapshot.Request.Sources.Add(missingSource) },
            SkippedSources = [new(missingSource.Key, "Source unavailable (HTTP 403).")]
        };
        foreach (var skipped in new[] { allSkipped, partiallySkipped })
        {
            var html = HtmlDetailedReportWriter.Write(skipped);
            var flat = skipped with { Request = skipped.Request with { RosterParentIndices = [] } };
            var flatHtml = HtmlDetailedReportWriter.Write(flat);
            Require(html.Contains("class=\"report-roster-hierarchy\"", StringComparison.Ordinal)
                && Regex.Matches(html, "class=\"report-line report-roster-member\"").Count == skipped.Request.Roster.Length,
                "All and partially skipped Git sources must preserve the complete linked reporting hierarchy.");
            Require(RosterTail(html) == RosterTail(flatHtml) && WithoutRoster(html) == WithoutRoster(flatHtml)
                && HtmlSummaryReportWriter.Write(skipped, "report.html") == HtmlSummaryReportWriter.Write(flat, "report.html"),
                "Skipped-source warnings, their formatting and all non-roster content must remain unchanged.");
        }
    }

    private static string RosterTail(string html)
    {
        var label = html.IndexOf("TEAM ROSTER (", StringComparison.Ordinal);
        var rule = html.IndexOf("<hr class=\"report-rule\" />", label, StringComparison.Ordinal);
        var end = html.IndexOf("<h2", label, StringComparison.Ordinal);
        Require(rule >= 0 && end > rule, "The roster separator and following section must remain intact.");
        return html[rule..end];
    }

    private static string WithoutRoster(string html)
    {
        var label = html.IndexOf("TEAM ROSTER (", StringComparison.Ordinal);
        var start = html.LastIndexOf("<h2", label, StringComparison.Ordinal);
        var end = html.IndexOf("<h2", label, StringComparison.Ordinal);
        Require(start >= 0 && end > start, "The roster section must retain its original heading boundaries.");
        return html[..start] + html[end..];
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
