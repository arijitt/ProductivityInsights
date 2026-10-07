using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class PersonFoldingTests
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
            foreach (var view in new[] { "details", "trends" }.Take(snapshot.Request.Analysis == "gitCommits" ? 2 : 1))
                Require(!HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = view } })
                    .Contains("report-person-details", StringComparison.Ordinal), "Other report views must not gain person folds.");
            Require(!HtmlSummaryReportWriter.Write(snapshot, "report_Detailed.html").Contains("report-person-details", StringComparison.Ordinal)
                && !MarkdownReportWriter.Write(snapshot).Contains("<details", StringComparison.Ordinal),
                "Folding must not alter summary HTML or legacy Markdown.");
        }

        var work = ReportWriterFixtures.WorkSnapshot();
        var workSections = Sections(HtmlDetailedReportWriter.Write(work));
        var workRows = ReportDocumentBuilder.Build(work).OverallRows;
        Require(workSections.Length == workRows.Length,
            "Every Work Items member needs exactly one completion section, without separate audit folds.");
        Require(workSections.Any(section => section.Contains("State change history", StringComparison.Ordinal))
            && workSections.Any(section => section.Contains("Work items with positive active-work time", StringComparison.Ordinal)),
            "Work-item histories and the relocated person-summary tables must be inside the completion folds.");

        var git = ReportWriterFixtures.GitSnapshot();
        var pair = git with { Commits = [git.Commits[0] with { Committer = git.Request.Roster[1] }], PullRequests = [] };
        var pairSections = Sections(HtmlDetailedReportWriter.Write(pair));
        Require(pairSections.Length == 2 && pairSections.Any(section =>
            section.Contains(git.Request.Roster[0].Email!, StringComparison.Ordinal)
            && section.Contains(git.Request.Roster[1].Email!, StringComparison.Ordinal)
            && section.Contains(git.Commits[0].Id, StringComparison.Ordinal)),
            "Shared author/committer details must stay together in one fold, with a separate zero-member fold.");
        var prOnly = git with { Commits = [] };
        Check(prOnly);
        Require(Sections(HtmlDetailedReportWriter.Write(prOnly)).All(section =>
            !section.Contains("closedBy", StringComparison.Ordinal)), "PR-only person folds must not gain repeated PR audit fields.");
        Check(git with
        {
            Commits = [], PullRequests = [],
            SkippedSources = git.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "HTTP 404")).ToImmutableArray()
        });

        var incident = ReportWriterFixtures.IncidentSnapshot();
        var incidentSections = Sections(HtmlDetailedReportWriter.Write(incident));
        Require(incidentSections.Length == 0,
            "Teams Incident must omit the later personal evidence folds.");
        var namedActive = incident with
        {
            Request = incident.Request with
            {
                Roster = incident.Request.Roster.SetItem(0, incident.Request.Roster[0] with { Name = "Active" })
            }
        };
        Require(ReportFoldMarkup.Parse(HtmlDetailedReportWriter.Write(namedActive))
            .Any(fold => fold.Kind == "owner" && fold.ParentStart is null
                && Text(fold.Content).Contains("Active", StringComparison.Ordinal)
                && fold.Content.Contains("Distinct closed incidents in group", StringComparison.Ordinal)),
            "A person named Active must retain their earlier individual fold, independent of the global Active statistics.");
    }

    private static void Check(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var sections = Sections(html);
        Require((snapshot.Request.Analysis == "incidentManagement" ? sections.Length == 0 : sections.Length > 0) && ReportFoldMarkup.Parse(html)
            .Where(fold => fold.Kind == "person").All(fold => fold.ParentStart is null),
            "Every person fold must be complete, independent, initially collapsed and non-nested.");
        foreach (var section in sections)
        {
            Require(Regex.IsMatch(section, @"^\s*<summary>\s*<h3\b[^>]*>.+?</h3>\s*</summary>\s*<div class=""report-person-content"">", RegexOptions.Singleline)
                && !Regex.IsMatch(section, @"<h[12]\b")
                && !section.Contains("<details class=\"report-person-details\"", StringComparison.Ordinal)
                && !section.Contains("TEAM ROSTER", StringComparison.Ordinal)
                && !section.Contains("Recorded incident action history and contribution evidence", StringComparison.Ordinal)
                && !section.Contains("Other selected incidents (including created-only and action-only)", StringComparison.Ordinal),
                "Only a complete person section belongs inside each native disclosure, never following global sections.");
        }
        Require(!Regex.IsMatch(html, @"<(?:script|iframe|img)\b", RegexOptions.IgnoreCase)
            && html.Contains("default-src 'none'", StringComparison.Ordinal)
            && html.Contains("::details-content { display:block; content-visibility:visible; }", StringComparison.Ordinal),
            "Offline folding must preserve the no-script policy and reveal closed contents for printing.");
        var outside = ReportFoldMarkup.OutsidePersons(html);
        Require(outside.Contains("Captured inputs and export query context", StringComparison.Ordinal)
            && outside.Contains("TEAM ROSTER", StringComparison.Ordinal),
            "Roster and captured input context must remain outside the folds.");
        if (snapshot.Request.Analysis == "incidentManagement")
            Require(outside.Contains("<h3>Active</h3>", StringComparison.Ordinal)
                && outside.Contains("Distinct incident totals and weighted duration statistics", StringComparison.Ordinal)
                && !Regex.IsMatch(outside, @"<th\b[^>]*>\s*Action ID\s*</th>"),
                "Global Active and overall statistics remain outside personal folds; action tables belong only to member evidence.");
        if (snapshot.Request.Analysis == "gitCommits")
            Require(outside.Contains("Distinct Git totals", StringComparison.Ordinal)
                && outside.Contains(snapshot.AllSourcesSkipped ? "No selected source could be collected" : "Git commit trends", StringComparison.Ordinal)
                && !html.Contains("Completed PR evidence", StringComparison.Ordinal),
                "Git totals and trends must remain global without the redundant completed-PR evidence section.");
        using var stream = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, stream);
        Require(stream.ToString() == html, "Streamed and buffered exports must preserve identical folding and content.");
    }

    private static string[] Sections(string html) =>
        ReportFoldMarkup.Parse(html).Where(fold => fold.Kind == "person").Select(fold => fold.Content).ToArray();

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Person folding: " + message);
    }
}
