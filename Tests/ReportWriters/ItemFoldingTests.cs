using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class ItemFoldingTests
{
    public static void Run()
    {
        foreach (var snapshot in new[]
        {
            ReportWriterFixtures.WorkSnapshot(), ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.IncidentSnapshot()
        })
        {
            Check(snapshot);
            Check(snapshot with { WorkItems = [], Commits = [], PullRequests = [], Incidents = [], IncidentActions = [] });
            Require(!HtmlSummaryReportWriter.Write(snapshot, "report_Detailed.html").Contains("report-item-details", StringComparison.Ordinal)
                && !MarkdownReportWriter.Write(snapshot).Contains("<details", StringComparison.Ordinal),
                "Item folding must not alter summary HTML or legacy Markdown.");
            foreach (var view in snapshot.Request.Analysis == "gitCommits" ? new[] { "details", "trends" } : ["details"])
                Require(!HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = view } })
                    .Contains("report-item-details", StringComparison.Ordinal), "Non-Teams reports must not gain item folds.");
        }
        const string hostile = "</details><summary>Other item</summary><script>bad</script>\n& [fake](javascript:alert(1))";
        var work = ReportWriterFixtures.WorkSnapshot();
        Check(work with { WorkItems = work.WorkItems.Select(item => item with { Title = hostile, Type = "Task\n<details>" }).ToImmutableArray() });
        var git = ReportWriterFixtures.GitSnapshot();
        Check(git with { Commits = git.Commits.Select(commit => commit with { Message = hostile }).ToImmutableArray() });
        Check(git with { Commits = [] });
        Check(git with
        {
            Commits = [], PullRequests = [],
            SkippedSources = git.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "HTTP 404")).ToImmutableArray()
        });
        var incident = ReportWriterFixtures.IncidentSnapshot();
        Check(incident with { Incidents = incident.Incidents.Select(item => item with { Title = hostile, Mitigation = hostile }).ToImmutableArray() });
    }

    private static void Check(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var folds = ReportFoldMarkup.Parse(html);
        var persons = folds.Where(fold => fold.Kind == "person").ToDictionary(fold => fold.Start);
        var items = folds.Where(fold => fold.Kind == "item").ToArray();
        var expected = snapshot.Request.Analysis switch
        {
            "workItems" => document.WorkItems.Length,
            "gitCommits" => document.Commits.Length,
            _ => 0
        };
        Require(items.Length == expected, "Every personal record occurrence must have exactly one fold, without duplicate source memberships.");
        foreach (var item in items)
        {
            Require(item.ParentStart is { } parent && persons.ContainsKey(parent)
                && !item.Content.Contains("<details", StringComparison.Ordinal)
                && Regex.IsMatch(item.Content, @"^\s*<summary>\s*<h[45]\b[^>]*>.+?</h[45]>\s*</summary>\s*<div class=""report-item-content"">", RegexOptions.Singleline)
                && !Regex.IsMatch(item.Content, @"<h[123]\b"),
                "Each item needs a complete heading/body disclosure directly inside its own person, never adjacent people or global sections.");
            Require(item.Content.Contains(snapshot.Request.Analysis == "incidentManagement" ? "report-incident-link" : "Recorded URL:", StringComparison.Ordinal),
                "Each fold retains a working reference to its incident record or the original recorded URL for other analyses.");
        }
        Require(!ReportFoldMarkup.OutsidePersons(html).Contains("<details class=\"report-item-details\"", StringComparison.Ordinal)
            && !Regex.IsMatch(html, @"<(?:script|iframe|img)\b", RegexOptions.IgnoreCase),
            "Item folds must be limited to personal details and keep recorded text safe.");
        if (snapshot.Request.Analysis == "workItems")
        {
            Require(items.All(item => Text(item.Content).Contains("State Change History", StringComparison.OrdinalIgnoreCase)),
                "Each work-item fold must contain its complete history.");
            foreach (var record in document.WorkItems.Select(item => item.Entity.Record))
                Require(items.Any(item => Text(item.Content).Contains("Source-qualified key: " + record.EntityKey, StringComparison.Ordinal)
                    && Text(item.Content).Contains(record.Title, StringComparison.Ordinal)),
                    "Consolidated work-item folds must retain their source-qualified audit identity and exact title.");
        }
        else if (snapshot.Request.Analysis == "gitCommits")
        {
            foreach (var record in document.Commits.Select(item => item.Entity.Record))
                Require(items.Any(item => Text(item.Content).Contains(record.Id, StringComparison.Ordinal)
                    && Text(item.Content).Contains(record.Message, StringComparison.Ordinal)
                    && record.Files.All(file => Text(item.Content).Contains(file.Path, StringComparison.Ordinal))),
                    "Each commit fold must include its full message, metadata and every changed file.");
        }
        else
            Require(!html.Contains("Recorded in-period actions by this member", StringComparison.Ordinal),
                "The removed later incident sections must not leave orphaned action-evidence folds.");
        using var output = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, output);
        Require(output.ToString() == html, "Streaming must preserve every nested fold and record.");
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Item folding: " + message);
    }
}
