using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class ContextFoldingTests
{
    public static void Run()
    {
        foreach (var baseline in new[]
        {
            ReportWriterFixtures.WorkSnapshot(), ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.IncidentSnapshot()
        })
        {
            foreach (var view in baseline.Request.Analysis == "gitCommits" ? new[] { "teams", "details", "trends" } : ["teams", "details"])
            {
                var snapshot = baseline with { Request = baseline.Request with { View = view } };
                Check(snapshot);
                Check(snapshot with { WorkItems = [], Commits = [], PullRequests = [], Incidents = [], IncidentActions = [] });
            }
        }
        var git = ReportWriterFixtures.GitSnapshot();
        Check(git with { Commits = [] });
        Check(git with
        {
            Commits = [], PullRequests = [],
            SkippedSources = [new(git.Request.Sources[0].Key, "HTTP 404 <details>")]
        });
        Check(git with
        {
            Commits = [], PullRequests = [],
            SkippedSources = [.. git.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "HTTP 404"))]
        });
        var incidents = ReportWriterFixtures.IncidentSnapshot();
        Check(incidents with
        {
            Incidents = [], IncidentActions = [],
            SkippedSources = [.. incidents.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "Unavailable"))]
        });
    }

    private static void Check(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var folds = ReportFoldMarkup.Parse(html);
        var contexts = folds.Where(fold => fold.Kind == "context").ToArray();
        Require(contexts.Length == 1, "Exactly one context section must start collapsed.");
        var context = contexts[0];
        Require(context.ParentStart is null
            && Regex.IsMatch(context.Content, @"^\s*<summary>\s*<h2>Captured inputs and export query context</h2>\s*</summary>\s*<div class=""report-context-content"">")
            && !context.Content.Contains("<details", StringComparison.Ordinal)
            && !context.Content.Contains("TEAM ROSTER", StringComparison.Ordinal)
            && Regex.Matches(context.Content, "<h2>").Count == 1,
            "The independent context fold must contain only its own heading and complete body, never adjacent report sections.");
        Require(context.Content.Contains(snapshot.Request.RunId.ToString("D"), StringComparison.Ordinal)
            && context.Content.Contains("Selected sources", StringComparison.Ordinal)
            && context.Content.Contains("Coverage and metric definitions", StringComparison.Ordinal)
            && context.Content.Contains("Captured timezone / date semantics", StringComparison.Ordinal),
            "Collapsing context must preserve all captured inputs, sources and metric definitions.");
        Require(context.Content.Contains("Skipped sources - excluded from totals", StringComparison.Ordinal)
                == !snapshot.SkippedSources.IsEmpty
            && context.Content.Contains("Captured manager / report roster", StringComparison.Ordinal)
                == !(snapshot.Request.IsTeams && snapshot.Request.Analysis == "workItems"),
            "Existing source-availability disclosures and scoped roster omission must remain unchanged.");
        var outside = html.Remove(context.Start, context.Length);
        Require(!outside.Contains("<h2>Captured inputs and export query context</h2>", StringComparison.Ordinal)
            && (!snapshot.Request.IsTeams || outside.Contains("TEAM ROSTER", StringComparison.Ordinal))
            && outside.Contains(snapshot.Request.Analysis switch
            {
                "workItems" => "Distinct work item totals and active-work time",
                "gitCommits" => "Distinct Git totals",
                _ => "Distinct incident totals and weighted duration statistics"
            }, StringComparison.Ordinal),
            "Roster navigation, global totals and record evidence must stay outside the context fold.");
        var markdown = MarkdownReportWriter.Write(document);
        if (snapshot.Request.IsTeams && snapshot.Request.Analysis is "workItems" or "gitCommits" or "incidentManagement")
        {
            var totalsHeading = snapshot.Request.Analysis switch
            {
                "gitCommits" => "Distinct Git totals",
                "workItems" => "Distinct work item totals and active-work time",
                _ => "Distinct incident totals and weighted duration statistics"
            };
            var totals = html.IndexOf("<h2>" + totalsHeading + "</h2>", StringComparison.Ordinal);
            Require(totals >= 0 && totals < context.Start
                && Regex.IsMatch(html[(context.Start + context.Length)..], @"^\s*</main>\s*</body>\s*</html>\s*$"),
                "Teams context must be the final section, after all statistics, trends and navigation fallbacks.");
            Require(markdown.IndexOf("Captured inputs and export query context", StringComparison.Ordinal)
                    < markdown.IndexOf(totalsHeading, StringComparison.Ordinal),
                "Reordering the detailed HTML footer must preserve legacy Markdown ordering.");
        }
        if (!snapshot.Request.IsTeams && snapshot.Request.Analysis == "gitCommits")
            Require(context.Start < html.IndexOf("<h2>Distinct Git totals</h2>", StringComparison.Ordinal),
                "Git Details and Trends must retain their existing context order.");
        if (!snapshot.Request.IsTeams && snapshot.Request.Analysis == "incidentManagement")
            Require(context.Start < html.IndexOf("<h2>Incident details</h2>", StringComparison.Ordinal),
                "Incident Details must retain context before the incident appendix.");
        Require(!markdown.Contains("<details", StringComparison.Ordinal)
            && !HtmlSummaryReportWriter.Write(document, "report_Detailed.html").Contains("report-context-details", StringComparison.Ordinal),
            "Detailed context folding must not alter legacy Markdown or the summary report.");
        using var stream = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, stream);
        Require(stream.ToString() == html, "Streamed downloads must preserve the same context disclosure and full content.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Context folding: " + message);
    }
}
