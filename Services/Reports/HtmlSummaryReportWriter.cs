using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public static class HtmlSummaryReportWriter
{
    private static readonly string[] FileHeaders =
        ["\U0001F4C4 Files Added", "\U0001F4DD Files Modified", "\U0001F5D1 Files Deleted"];

    private static string[] WithFileColumns(string[] cells, string[] fileCells, bool beforeLast) => beforeLast
        ? [.. cells[..^1], .. fileCells, cells[^1]]
        : [.. cells, .. fileCells];

    private static string[] FileCells(long added, long modified, long deleted) =>
        [ReportFormatting.Number(added), ReportFormatting.Number(modified), ReportFormatting.Number(deleted)];

    private static string[] TrendCells(GitTrendPeriod period) => WithFileColumns(ReportTables.TrendCells(period),
        FileCells(period.FilesAdded, period.FilesModified, period.FilesDeleted), beforeLast: true);

    public static string Write(ReportSnapshot snapshot, string markdownFileName, CancellationToken cancellationToken = default) =>
        Write(ReportDocumentBuilder.Build(snapshot, cancellationToken), markdownFileName, cancellationToken);

    public static string Write(ReportDocument document, string markdownFileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Render(document, markdownFileName, cancellationToken, countOnly: true);
        return Render(document, markdownFileName, cancellationToken, countOnly: false).ToString();
    }

    public static long WriteTo(ReportDocument document, string markdownFileName, TextWriter destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Render(document, markdownFileName, cancellationToken, countOnly: false,
            destination, ReportRenderingLimits.MaximumExportFileBytes).ByteCount;
    }

    private static ReportOutput Render(ReportDocument document, string markdownFileName, CancellationToken cancellationToken, bool countOnly,
        TextWriter? destination = null, int maximumBytes = ReportRenderingLimits.MaximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var markdownUrl = ReportFormatting.MarkdownFileUrl(markdownFileName);
        var output = new ReportOutput(cancellationToken, countOnly, destination, maximumBytes);
        var snapshot = document.Snapshot;
        var request = snapshot.Request;
        var presentation = request.Analysis == "gitCommits" ? new GitReportPresentation(document, cancellationToken) : null;
        var workPresentation = request.Analysis == "workItems" && request.IsTeams
            ? new WorkItemReportPresentation(document, cancellationToken) : null;
        var incidentPresentation = request.Analysis == "incidentManagement" && request.IsTeams
            ? new IncidentReportPresentation(document, cancellationToken) : null;
        var referenceStyle = presentation is not null || workPresentation is not null || incidentPresentation is not null;
        output.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n<title>");
        output.Html(document.Title);
        output.Append("</title>\n<style>\n");
        output.Append("""
            :root { color-scheme: dark; }
            body { background:#1e1e1e; color:#e0e0e0; font-family:'Segoe UI',Tahoma,Arial,sans-serif; margin:24px; }
            h1 { color:#fff; border-bottom:2px solid #3e3e42; padding-bottom:8px; margin-top:0; }
            h2 { color:#9cdcfe; margin-top:32px; margin-bottom:8px; font-size:1.05em; }
            table { border-collapse:collapse; width:100%; table-layout:fixed; margin-top:8px; font-size:0.92em; }
            th, td { border:1px solid #3e3e42; padding:6px 10px; text-align:left; vertical-align:top; white-space:pre-wrap; }
            th { background:#2d2d30; color:#fff; font-weight:600; }
            tbody tr:nth-child(odd) { background:#252526; }
            tbody tr:nth-child(even) { background:#1e1e1e; }
            tbody tr:hover { background:#37373d; }
            tfoot { font-weight:600; background:#2d2d30; }
            .meta { color:#a0a0a0; font-size:0.85em; margin-bottom:24px; white-space:normal; }
            h1, h2, th, td, li, p, a, .meta { overflow-wrap:anywhere; word-break:break-word; }
            .value { white-space:pre-wrap; }
            a { color:#9cdcfe; }
            @media (max-width:700px) { body { margin:12px; } th,td { padding:4px; font-size:0.85em; } }
            """);
        if (referenceStyle)
            output.Append("""
                h1, th { color:#ffffff; }
                table { table-layout:auto; }
                th, td { white-space:normal; }
                .meta { color:#808080; }
                .report-context { margin-top:32px; }
                .report-context summary { color:#9cdcfe; cursor:pointer; }
                .report-warning { color:#ffd166; }
                """);
        output.Append("\n</style>\n</head>\n<body>\n<h1>");
        output.Html(document.Title);
        output.Append("</h1>\n");
        if (!referenceStyle)
            Context();
        else
        {
            output.Append("<div class=\"meta\">Generated ");
            output.Html(ReportFormatting.CompactTimestamp(snapshot.AsOf));
            output.Append("</div>\n");
            if (!snapshot.SkippedSources.IsEmpty)
                output.Append("<p class=\"report-warning\">Some sources were skipped and are excluded from totals. Activity for those sources is unavailable, not zero; see the export context below.</p>\n");
        }
        if (workPresentation is not null)
        {
            WorkSummary(document.SourceHeading, workPresentation, true);
            WorkSummary(document.OverallHeading, workPresentation, false);
        }
        else if (incidentPresentation is not null)
        {
            IncidentSummary(document.SourceHeading, incidentPresentation, true);
            IncidentSummary(document.OverallHeading, incidentPresentation, false);
        }
        else
        {
            Summary(document.SourceHeading, presentation?.SourceRows ?? document.SourceRows, true);
            Summary(document.OverallHeading, presentation?.OverallRows ?? document.OverallRows, false);
        }
        if (request.Analysis == "gitCommits" && request.View == "trends" && !snapshot.AllSourcesSkipped)
        {
            Heading("GIT COMMIT TRENDS - " + request.TrendPeriod);
            output.Append("<p class=\"meta\">All available periods. Commits are not PR completions; service/test classification uses the existing case-insensitive 'test' path rule.</p>\n");
            TableHeader(WithFileColumns(ReportTables.TrendHeaders, FileHeaders, beforeLast: true));
            foreach (var period in document.GitTrends) TableRow(TrendCells(period));
            output.Append("</tbody><tfoot>\n");
            TableRow(TrendCells(ReportTables.TrendTotal(document.GitTrends, cancellationToken)));
            output.Append("</tfoot></table>\n");
        }
        if (referenceStyle)
        {
            output.Append("<details class=\"report-context\"><summary>Captured inputs, sources and coverage</summary>\n");
            Context();
            output.Append("</details>\n");
        }
        output.Append("</body>\n</html>\n");
        cancellationToken.ThrowIfCancellationRequested();
        return output;

        void Context()
        {
            output.Append("<div class=\"meta\">\n");
            Metadata("Analysis / view", request.Analysis + " / " + request.View);
            Metadata("Run ID", request.RunId.ToString("D"));
            Metadata("Export capture anchor", ReportFormatting.Date(request.ExecutedAt));
            Metadata("Generated / as of", ReportFormatting.Date(snapshot.AsOf));
            Metadata("Requested start", ReportFormatting.RequestedDate(request.RequestedStart));
            Metadata("Requested end", ReportFormatting.RequestedDate(request.RequestedEnd));
            Metadata("Effective export start (inclusive)", ReportFormatting.Date(request.Start));
            Metadata("Effective export end (inclusive)", ReportFormatting.Date(request.End));
            foreach (var metadata in ReportFormatting.BoundsMetadata(request))
                Metadata(metadata.Label, metadata.Value);
            Metadata("Captured timezone / date semantics", request.TimeZone);
            Metadata("Manager", request.Manager);
            if (request.Analysis == "gitCommits") Metadata("Trend interval", request.TrendPeriod);
            output.Append("<p><strong>Selected sources:</strong></p><ul>\n");
            foreach (var source in request.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Append("<li class=\"value\">");
                output.Html($"{source.Label}; source key: {source.Key}; organization: {ReportFormatting.Label(source.Organization)}; project: {ReportFormatting.Label(source.Project)}; scope: {ReportFormatting.Label(source.Scope)}; branch: {ReportFormatting.Label(source.Branch)}; team: {ReportFormatting.Label(source.Team)}");
                output.Append("</li>\n");
            }
            output.Append("</ul>\n<p><strong>Captured roster:</strong></p>\n");
            if (request.Roster.IsEmpty) output.Append("<p>-</p>\n");
            else
            {
                output.Append("<ul>\n");
                foreach (var member in request.Roster)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Append("<li class=\"value\">");
                    output.Html(ReportFormatting.Identity(member));
                    output.Append("</li>\n");
                }
                output.Append("</ul>\n");
            }
            output.Append("<p><a href=\"");
            output.Html(markdownUrl);
            output.Append("\">Markdown report (all collected records, changed files and histories)</a></p>\n");
            if (!snapshot.SkippedSources.IsEmpty)
            {
                output.Append("<h2>Skipped sources - excluded from totals</h2><p>This report includes successfully collected sources only. Skipped-source activity is unavailable, not zero. The original selections and roster are preserved above.</p>\n");
                TableHeader(["Source", "Reason"]);
                foreach (var source in snapshot.SkippedSources)
                    TableRow([request.Sources.First(item => item.Key == source.SourceKey).Label, source.Reason]);
                output.Append("</tbody></table>\n");
            }
            output.Append("<ul>\n");
            if (presentation is not null)
                output.Append("<li>Files Added, Files Modified and Files Deleted count file-change occurrences per commit, not unique paths or lines. Renames and other changes to existing entries (including encoding, properties, binary files and submodules) count as modified; split-rename source entries are not counted again. Overall and trend counts deduplicate the same repository-qualified commit across selected sources.</li>\n");
            if (workPresentation is not null)
                output.Append("<li>Area rows group distinct organization-qualified work items by their recorded area path, not the selected query root. Overlapping selections do not duplicate area or overall counts. Ambiguous area, team and member names remain qualified; original selections and every item's source memberships remain in the export context and Markdown details.</li>\n");
            if (incidentPresentation is not null)
                output.Append("<li>TOTAL sums the existing overall contributor credits, not overlapping source rows. Contributed totals count actor/incident pairs, not distinct incidents across different actors. The recorded in-period action definition is unchanged; data availability and coverage are described below.</li>\n");
            foreach (var note in document.MetricNotes.Concat(snapshot.CoverageNotes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Append("<li>");
                output.Html(note);
                output.Append("</li>\n");
            }
            output.Append("</ul>\n</div>\n");
        }

        void Metadata(string label, string? value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append("<p><strong>");
            output.Html(label);
            output.Append(":</strong> <span class=\"value\">");
            output.Html(value);
            output.Append("</span></p>\n");
        }

        void Heading(string heading)
        {
            output.Append("<h2>");
            output.Html($"{heading} ({ReportFormatting.CalendarDate(request.Start)} to {ReportFormatting.CalendarDate(request.End)})");
            output.Append("</h2>\n");
        }

        void Summary(string heading, ImmutableArray<ReportSummaryRow> rows, bool bySource)
        {
            Heading(heading);
            TableHeader(presentation is null ? ReportTables.Headers(request.Analysis, bySource)
                : WithFileColumns(GitReportPresentation.Headers(bySource), FileHeaders, beforeLast: !bySource));
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TableRow(presentation is null ? ReportTables.Cells(row, request, cancellationToken)
                    : WithFileColumns(presentation.Cells(row, bySource),
                        FileCells(row.FilesAdded, row.FilesModified, row.FilesDeleted), beforeLast: !bySource));
            }
            output.Append("</tbody></table>\n");
            if (rows.IsEmpty) output.Append(snapshot.AllSourcesSkipped
                ? "<p class=\"meta\">No selected source could be collected. Activity is unavailable, not zero.</p>\n"
                : "<p class=\"meta\">No contributors in the completed snapshot.</p>\n");
        }

        void WorkSummary(string heading, WorkItemReportPresentation work, bool byArea)
        {
            Heading(heading);
            TableHeader(WorkItemReportPresentation.Headers(byArea));
            if (byArea)
                foreach (var row in work.AreaRows) TableRow(work.Cells(row));
            else
                foreach (var row in work.OverallRows) TableRow(work.Cells(row));
            output.Append("</tbody></table>\n");
            if (work.OverallRows.IsEmpty) output.Append(snapshot.AllSourcesSkipped
                ? "<p class=\"meta\">No selected source could be collected. Activity is unavailable, not zero.</p>\n"
                : "<p class=\"meta\">No contributors in the completed snapshot.</p>\n");
        }

        void IncidentSummary(string heading, IncidentReportPresentation incidents, bool bySource)
        {
            Heading(heading);
            TableHeader(IncidentReportPresentation.Headers(bySource));
            var rows = bySource ? incidents.SourceRows : incidents.OverallRows;
            foreach (var row in rows) TableRow(incidents.Cells(row, bySource));
            if (!bySource && !snapshot.AllSourcesSkipped) TableRow(incidents.TotalCells());
            output.Append("</tbody></table>\n");
            if (rows.IsEmpty) output.Append(snapshot.AllSourcesSkipped
                ? "<p class=\"meta\">No selected source could be collected. Activity is unavailable, not zero.</p>\n"
                : "<p class=\"meta\">No contributors in the completed snapshot.</p>\n");
        }

        void TableHeader(string[] headings)
        {
            output.Append("<table>\n<thead><tr>");
            foreach (var heading in headings)
            {
                output.Append("<th scope=\"col\">");
                output.Html(heading);
                output.Append("</th>");
            }
            output.Append("</tr></thead>\n<tbody>\n");
        }

        void TableRow(string[] cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append("<tr>");
            foreach (var cell in cells)
            {
                output.Append("<td>");
                if (!referenceStyle || cell.Length != 0) output.Html(cell);
                output.Append("</td>");
            }
            output.Append("</tr>\n");
        }
    }
}
