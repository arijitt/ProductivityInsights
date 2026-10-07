using System.Collections.Immutable;
using System.Globalization;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal static class FeatureRegistryReportPresentation
{
    internal static string DetailedTitle(ReportRequest request) =>
        $"{(request.IsTeams ? "Team " : "")}Feature Registry Pull Request Analysis ({ReportFormatting.CalendarDate(request.Start)} to {ReportFormatting.CalendarDate(request.End)})";

    internal static readonly string[] Headers =
        ["PR", "Title", "Status", "Author", "Source branch", "Target branch", "Created (UTC)", "Completed by", "Completed (UTC)"];

    internal static string[] Cells(FeatureRegistryPullRequest pr) =>
        [pr.Id.ToString(CultureInfo.InvariantCulture), pr.Title, pr.Status + (pr.IsDraft ? " (draft)" : ""),
            ReportFormatting.Identity(pr.Author), pr.SourceBranch, pr.TargetBranch, ReportFormatting.Date(pr.CreatedAt),
            pr.CompletedBy is null ? "-" : ReportFormatting.Identity(pr.CompletedBy), ReportFormatting.Date(pr.CompletedAt)];

    internal static ReportSnapshot Snapshot(FeatureRegistrySnapshot data) =>
        new() { Request = data.Request, AsOf = data.FinishedAt, FeatureRegistry = data };

    internal sealed record Contributor(string Key, ReportIdentity Identity, ImmutableArray<FeatureRegistryPullRequest> Records);

    internal static Contributor[] Contributors(FeatureRegistrySnapshot data, CancellationToken token)
    {
        var request = data.Request;
        var records = data.Open.Concat(data.Completed);
        var identities = new ReportIdentityCatalog(request, records.Select(pr => pr.Attribution), token);
        var groups = records.GroupBy(pr =>
            !request.IsTeams && string.IsNullOrWhiteSpace(pr.Attribution.Id)
                && !new[] { pr.Attribution.Email, pr.Attribution.PrincipalName, pr.Attribution.Name }
                    .Any(value => value?.Contains('@') == true)
                ? "pr:" + pr.EntityKey : identities.Get(pr.Attribution).Key)
            .Select(group => new Contributor(group.Key, group.First().Attribution, group.ToImmutableArray()))
            .ToDictionary(group => group.Key, StringComparer.Ordinal);
        if (request.IsTeams)
            foreach (var member in request.Roster)
            {
                token.ThrowIfCancellationRequested();
                var key = identities.Get(member).Key;
                groups[key] = new(key, member, groups.GetValueOrDefault(key)?.Records ?? []);
            }
        return groups.Values.OrderByDescending(group => group.Records.Length)
            .ThenBy(group => group.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
    }

    internal static ReportDocument Build(ReportSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FeatureRegistrySource.Validate(snapshot.Request);
        var budget = ReportDocumentBuilder.Validate(snapshot, token);
        var data = snapshot.FeatureRegistry
            ?? throw new InvalidOperationException("Run Feature Registry analysis again to collect its PR snapshot; export cannot requery it.");
        if (data.Request != snapshot.Request || snapshot.AsOf != data.FinishedAt || data.FinishedAt < data.StartedAt
            || data.Open.IsDefault || data.Completed.IsDefault || data.Unattributed.IsDefault
            || !snapshot.Commits.IsEmpty || !snapshot.PullRequests.IsEmpty || !snapshot.WorkItems.IsEmpty
            || !snapshot.Incidents.IsEmpty || !snapshot.IncidentActions.IsEmpty || !snapshot.SkippedSources.IsEmpty)
            throw new InvalidOperationException("The Feature Registry export snapshot does not match the executed PR-only run.");
        foreach (var pr in data.Open.Concat(data.Completed).Concat(data.Unattributed))
        {
            budget.Record(pr.Title, pr.SourceBranch, pr.TargetBranch, pr.Url);
            budget.Identity(pr.Author);
            if (pr.CompletedBy is not null) budget.Identity(pr.CompletedBy);
            if (pr.FileChanges is { } changes)
            {
                if (changes.IterationId <= 0 || changes.Files.IsDefault)
                    throw new InvalidOperationException("Feature Registry changed-file metadata is incomplete. Run Analysis again.");
                foreach (var file in changes.Files)
                {
                    if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.ChangeType))
                        throw new InvalidOperationException("Feature Registry changed-file metadata has a missing path or change type. Run Analysis again.");
                    budget.Record(file.Path, file.OriginalPath, file.ChangeType);
                }
            }
        }
        return new ReportDocument
        {
            Snapshot = snapshot,
            Title = $"Feature Registry Pull Requests ({snapshot.Request.View})",
            SourceHeading = "Open PRs",
            OverallHeading = "Completed PRs",
            SourceRows = [],
            OverallRows = [],
            MetricNotes =
            [
                "Exact successful Run Analysis snapshot; exporting does not refresh PR state.",
                "Open PRs include drafts across all target branches, regardless of creation date. Completed PRs use the inclusive completion-time window. Abandoned PRs are excluded.",
                "Teams includes open PRs authored by a roster member and completed PRs completed by a roster member (closedBy). Unknown, ambiguous or conflicting membership is disclosed separately, not credited as roster activity.",
                "PRs are live paginated reads during the recorded collection interval, not an atomic historical snapshot. Run Analysis again to refresh."
            ]
        };
    }

    internal static IEnumerable<(string Label, string Value)> Metadata(FeatureRegistrySnapshot data)
    {
        yield return ("Source", "msdata/A365/FeatureRegistry (all branches; PRs only)");
        yield return ("Analysis", "Feature Registry");
        yield return ("View", data.Request.View);
        yield return ("Run ID", data.Request.RunId.ToString());
        yield return ("Completed range start (inclusive)", ReportFormatting.Date(data.Request.Start));
        yield return ("Completed range end (inclusive)", ReportFormatting.Date(data.Request.End));
        yield return ("Collection started", ReportFormatting.Date(data.StartedAt));
        yield return ("Collection finished", ReportFormatting.Date(data.FinishedAt));
        yield return ("Manager", data.Request.Manager ?? "-");
    }

    internal static ReportOutput Summary(ReportDocument document, string detailedName, CancellationToken token,
        bool countOnly, TextWriter? destination, int maximumBytes)
    {
        var data = document.Snapshot.FeatureRegistry!;
        var teams = data.Request.IsTeams;
        var output = new ReportOutput(token, countOnly, destination, maximumBytes);
        if (teams)
        {
            output.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n<title>");
            output.Html(DetailedTitle(data.Request));
            output.Append("</title>\n<style>\n");
            HtmlSummaryReportWriter.WriteStyles(output, referenceStyle: true);
            output.Append("\n</style>\n</head>\n<body>\n<h1>");
            output.Html(DetailedTitle(data.Request));
            output.Append("</h1>\n<div class=\"meta\">Generated ");
            output.Html(ReportFormatting.CompactTimestamp(document.Snapshot.AsOf));
            output.Append("</div>\n");
            if (!data.Unattributed.IsEmpty)
                output.Append("<p class=\"report-warning\">Membership-unavailable PRs are excluded from roster counts, not evidence of zero activity. See the captured PR evidence below.</p>\n");
            var rows = Contributors(data, token).Select(group => new
            {
                group.Key,
                Name = group.Identity.DisplayName,
                Open = group.Records.Count(pr => pr.Status == "active"),
                Completed = group.Records.Count(pr => pr.Status == "completed")
            }).OrderByDescending(row => row.Completed).ThenByDescending(row => row.Open)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
            foreach (var byRepository in new[] { true, false })
            {
                output.Append("<h2>");
                output.Html($"TEAM FEATURE REGISTRY PULL REQUEST SUMMARY{(byRepository ? " BY REPOSITORY" : "")} ({ReportFormatting.CalendarDate(data.Request.Start)} to {ReportFormatting.CalendarDate(data.Request.End)})");
                output.Append("</h2>\n<table>\n<thead><tr>");
                var headers = byRepository
                    ? new[] { "\U0001F464 Team Member", "\U0001F4C2 Repository", "\U0001F513 Open PRs", "\u2705 Completed PRs" }
                    : new[] { "\U0001F464 Team Member", "\U0001F513 Open PRs", "\u2705 Completed PRs", "\U0001F4C2 Repositories" };
                foreach (var header in headers)
                {
                    output.Append("<th scope=\"col\">"); output.Html(header); output.Append("</th>");
                }
                output.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    var active = row.Open != 0 || row.Completed != 0;
                    var cells = byRepository
                        ? new[] { row.Name, active ? "msdata/A365/FeatureRegistry" : "\u2014",
                            ReportFormatting.Number(row.Open), ReportFormatting.Number(row.Completed) }
                        : new[] { row.Name, ReportFormatting.Number(row.Open), ReportFormatting.Number(row.Completed),
                            active ? "FeatureRegistry" : "" };
                    output.Append("<tr>");
                    foreach (var cell in cells)
                    {
                        output.Append("<td>");
                        if (cell.Length != 0) output.Html(cell);
                        output.Append("</td>");
                    }
                    output.Append("</tr>\n");
                }
                output.Append("</tbody></table>\n");
            }
            output.Append("<details class=\"report-context\"><summary>Captured inputs, sources and PR evidence</summary>\n<div class=\"meta\">\n");
        }
        else
        {
            output.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>");
            output.Html(document.Title);
            output.Append("""
            </title><style>
            :root{color-scheme:dark}body{background:#1e1e1e;color:#e0e0e0;font-family:'Segoe UI',Arial,sans-serif;margin:24px}
            h1,h2,a{color:#9cdcfe}table{border-collapse:collapse;width:100%;table-layout:fixed}
            th,td{border:1px solid #3e3e42;padding:6px;vertical-align:top;white-space:pre-wrap;overflow-wrap:anywhere}th{background:#2d2d30}
            p,li,h1,h2{overflow-wrap:anywhere}@media print{:root{color-scheme:light}body{background:white;color:black}h1,h2,a{color:black}th{background:#eee}thead{display:table-header-group}}
            </style></head><body><h1>
            """);
            output.Html(document.Title);
            output.Append("</h1>");
        }
        foreach (var (label, value) in Metadata(data))
        {
            output.Append("<p><strong>"); output.Html(label); output.Append(":</strong> ");
            output.Html(value); output.Append("</p>");
        }
        output.Append("<ul>");
        foreach (var note in document.MetricNotes) { output.Append("<li>"); output.Html(note); output.Append("</li>"); }
        output.Append("</ul>");
        if (teams) output.Append("</div>\n");
        Table("Open PRs", data.Open);
        Table("Completed PRs", data.Completed);
        if (!data.Unattributed.IsEmpty)
            Table("Membership unavailable - excluded from roster counts, not evidence of zero activity", data.Unattributed);
        output.Append("<p><a href=\""); output.Html(ReportFormatting.MarkdownFileUrl(detailedName));
        output.Append("\">Read the detailed HTML report</a></p>");
        output.Append(teams ? "</details>\n</body>\n</html>\n" : "</body></html>");
        return output;

        void Table(string label, IReadOnlyList<FeatureRegistryPullRequest> records)
        {
            output.Append("<h2>"); output.Html($"{label} ({records.Count})"); output.Append("</h2>");
            if (records.Count == 0)
            {
                output.Append(data.Unattributed.IsEmpty ? "<p>No matching PRs in the collected snapshot.</p>"
                    : "<p>No attributed PRs; membership-unavailable records are listed separately.</p>");
                return;
            }
            output.Append("<table><thead><tr>");
            foreach (var header in Headers) { output.Append("<th>"); output.Html(header); output.Append("</th>"); }
            output.Append("</tr></thead><tbody>");
            foreach (var pr in records)
            {
                token.ThrowIfCancellationRequested();
                output.Append("<tr>");
                var cells = Cells(pr);
                for (var index = 0; index < cells.Length; index++)
                {
                    output.Append("<td>");
                    if (index < 2) { output.Append("<a href=\""); output.Html(pr.Url); output.Append("\">"); }
                    output.Html(cells[index]);
                    if (index < 2) output.Append("</a>");
                    output.Append("</td>");
                }
                output.Append("</tr>");
            }
            output.Append("</tbody></table>");
        }
    }
}
