using System.Collections.Immutable;
using System.Globalization;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Utilities;

namespace ProductivityInsights.Services.Reports;

public static class MarkdownReportWriter
{
    public static string Write(ReportSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Write(ReportDocumentBuilder.Build(snapshot, cancellationToken), cancellationToken);

    public static string Write(ReportDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        new Writer(document, cancellationToken, countOnly: true).Render();
        return new Writer(document, cancellationToken, countOnly: false).Render().ToString();
    }

    internal static string WriteForDetailedHtml(ReportDocument document, DetailedReportNavigation navigation,
        DetailedReportCodeBlocks codeBlocks, CancellationToken token)
    {
        new Writer(document, token, countOnly: true, new DetailedReportNavigation(document, token), codeBlocks).Render();
        return new Writer(document, token, countOnly: false, navigation, codeBlocks).Render().ToString();
    }

    private sealed class Writer(ReportDocument document, CancellationToken token, bool countOnly,
        DetailedReportNavigation? navigation = null, DetailedReportCodeBlocks? codeBlocks = null)
    {
        private readonly ReportOutput output = new(token, countOnly, maximumBytes: codeBlocks is null
            ? ReportRenderingLimits.MaximumOutputBytes : ReportRenderingLimits.MaximumDetailedMarkupBytes);
        private ReportRequest Request => document.Snapshot.Request;
        private readonly HashSet<string> recordedIncidentEvidence = new(StringComparer.Ordinal);

        public ReportOutput Render()
        {
            Heading(1, document.Snapshot.FeatureRegistry is null
                ? document.Title : FeatureRegistryReportPresentation.DetailedTitle(Request));
            if (document.Snapshot.FeatureRegistry is not null)
            {
                FeatureRegistry();
            }
            else if (Request.Analysis == "gitCommits")
            {
                var presentation = new GitReportPresentation(document, token);
                GitDetails(presentation);
                GitSummary(document.SourceHeading, presentation.SourceRows, true, presentation);
                GitSummary(document.OverallHeading, presentation.OverallRows, false, presentation);
                if (navigation is null || !Request.IsTeams) Context();
                GitEvidence();
            }
            else if (Request.Analysis == "workItems" && Request.IsTeams)
            {
                var presentation = new WorkItemReportPresentation(document, token);
                TeamRoster();
                TeamWorkItemDetails(presentation);
                TeamWorkItemSummary(presentation, true);
                TeamWorkItemSummary(presentation, false);
                if (navigation is null) Context();
                WorkItems();
            }
            else if (Request.Analysis == "incidentManagement" && Request.IsTeams)
            {
                var presentation = new IncidentReportPresentation(document, token);
                TeamRoster();
                if (navigation is not null) TeamIncidentIndividualDetails(presentation);
                TeamIncidentDetails(presentation);
                TeamIncidentSummary(presentation, true);
                TeamIncidentSummary(presentation, false);
                if (navigation is null) Context();
                Incidents();
            }
            else
            {
                Context();
                Summary(document.SourceHeading, document.SourceRows, true);
                Summary(document.OverallHeading, document.OverallRows, false);
                if (Request.Analysis == "workItems") WorkItems();
                else Incidents();
            }
            if (navigation is not null) NavigationFallbacks();
            if (navigation is not null && Request.IsTeams
                && (Request.Analysis is "workItems" or "gitCommits" or "incidentManagement" || document.Snapshot.FeatureRegistry is not null))
                Context();
            token.ThrowIfCancellationRequested();
            return output;
        }

        private void FeatureRegistry()
        {
            var data = document.Snapshot.FeatureRegistry!;
            var groups = FeatureRegistryReportPresentation.Contributors(data, token);
            TeamRoster();
            BoxHeading($"\U0001F4CB {(Request.IsTeams ? "TEAM " : "")}FEATURE REGISTRY PULL REQUEST DETAILS BY PERSON:");
            Paragraph("Open PRs are grouped by author; completed PRs by their recorded completion actor (closedBy). Open PRs include drafts regardless of creation date; only completed PRs use the inclusive selected date range.");
            if (!data.Unattributed.IsEmpty)
                Paragraph("Zero counts mean no attributed PRs in the collected snapshot. Membership-unavailable PRs are listed separately and are not evidence of zero activity.");
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                var open = group.Records.Where(pr => pr.Status == "active").ToArray();
                var completed = group.Records.Where(pr => pr.Status == "completed").ToArray();
                navigation?.MemberSection(output.Length, group.Key, [group.Identity]);
                BoxHeading($"\U0001F464 Contributor: {group.Identity.DisplayName} - {open.Length} open PRs, {completed.Length} completed PRs");
                Line("  \U0001F4E6 msdata/A365/FeatureRegistry (all target branches)");
                output.Append("\n");
                PrRecords("Open PRs", open);
                if (Request.IsTeams && navigation is not null) Rule(bold: true);
                PrRecords("Completed PRs", completed);
                navigation?.PersonSection(sectionStart, output.Length);
            }
            if (data.Open.IsEmpty && data.Completed.IsEmpty)
                Paragraph(data.Unattributed.IsEmpty ? "No matching PRs in the collected snapshot."
                    : "No attributed PRs; membership-unavailable records are listed separately.");
            if (!data.Unattributed.IsEmpty)
            {
                BoxHeading("\u26A0 MEMBERSHIP UNAVAILABLE:");
                Paragraph("These PR identities could not be matched reliably. Records are excluded from roster counts, not evidence of zero activity.");
                PrRecords("Open PRs", data.Unattributed.Where(pr => pr.Status == "active").ToArray());
                if (Request.IsTeams && navigation is not null) Rule(bold: true);
                PrRecords("Completed PRs", data.Unattributed.Where(pr => pr.Status == "completed").ToArray());
            }
            PrSummary();

            void PrRecords(string title, IReadOnlyList<Models.FeatureRegistry.FeatureRegistryPullRequest> items)
            {
                Heading(4, $"{title} ({items.Count})");
                if (items.Count == 0)
                {
                    Paragraph("No matching PRs in these collected records.");
                    return;
                }
                foreach (var pr in items)
                {
                    token.ThrowIfCancellationRequested();
                    Rule();
                    var itemStart = ItemHeading($"\U0001F504 Pull Request: #{pr.Id}");
                    Line("\U0001F464 Author: " + ReportFormatting.Identity(pr.Author));
                    Line("\U0001F50E Status: " + pr.Status + (pr.IsDraft ? " (draft)" : ""));
                    var inlineTitle = navigation is not null && Request.IsTeams;
                    if (!inlineTitle) Line("\U0001F4AC Title: (Full PR title)");
                    CodeBlock(pr.Title, inlineTitle);
                    Line("\U0001F33F Source branch: " + pr.SourceBranch);
                    Line("\U0001F3AF Target branch: " + pr.TargetBranch);
                    Line("\U0001F4C5 Created: " + ReportFormatting.Date(pr.CreatedAt));
                    Line("\u270D\uFE0F Completed by: " + (pr.CompletedBy is null ? "-" : ReportFormatting.Identity(pr.CompletedBy)));
                    Line("\U0001F4C5 Completed: " + ReportFormatting.Date(pr.CompletedAt));
                    Line("\U0001F511 PR ID: " + pr.Id);
                    Line("\U0001F3E2 Organization: msdata");
                    Line("\U0001F5C2\uFE0F Repository ID: " + pr.RepositoryId);
                    Line("Source-qualified key: " + pr.EntityKey);
                    Line("Source memberships: " + SourceEvidence([Request.Sources[0].Key]));
                    output.Append("\n");
                    if (Request.IsTeams)
                    {
                        if (pr.FileChanges is { } changes)
                        {
                            Line($"\U0001F4C1 Changed Files ({changes.Files.Length}):");
                            Line($"Captured PR iteration {changes.IterationId}, compared with its source/target common commit (compareTo=0); complete PR changes, not just the last push.");
                            if (changes.Files.IsEmpty)
                                Line("No changed files in the captured PR comparison.");
                            foreach (var file in changes.Files)
                            {
                                var path = !string.IsNullOrEmpty(file.OriginalPath) && file.OriginalPath != file.Path
                                    ? $"{file.OriginalPath} \u2192 {file.Path}" : file.Path;
                                Line($"  \U0001F4C4 {path} [{file.ChangeType}]");
                            }
                        }
                        else
                        {
                            Line("\U0001F4C1 Changed Files: unavailable");
                            Line("This snapshot did not capture PR file lists. Run Analysis again to include them; unavailable is not zero changed files.");
                        }
                        output.Append("\n");
                    }
                    Link(pr.Url, "\U0001F310 View Pull Request", linkRecordedUrl: navigation is not null && Request.IsTeams);
                    Rule();
                    output.Append("\n");
                    navigation?.ItemSection(itemStart, output.Length);
                }
            }

            void PrSummary()
            {
                BoxHeading($"\U0001F4CA {(Request.IsTeams ? "TEAM " : "")}FEATURE REGISTRY PULL REQUEST SUMMARY BY REPOSITORY ({ReportFormatting.CalendarDate(Request.Start)} to {ReportFormatting.CalendarDate(Request.End)}):");
                var memberHeading = Request.IsTeams ? "\U0001F464 Team Member" : "\U0001F464 Contributor";
                Header(memberHeading, "\U0001F4C2 Repository", "\U0001F513 Open PRs", "\u2705 Completed PRs");
                foreach (var group in groups)
                {
                    var open = ReportFormatting.Number(group.Records.Count(pr => pr.Status == "active"));
                    var completed = ReportFormatting.Number(group.Records.Count(pr => pr.Status == "completed"));
                    Row(group.Identity.DisplayName, "msdata/A365/FeatureRegistry", open, completed);
                }
                var total = Request.IsTeams ? "TOTAL (attributed PRs)" : "TOTAL";
                Row(total, "msdata/A365/FeatureRegistry", ReportFormatting.Number(data.Open.Length), ReportFormatting.Number(data.Completed.Length));
                output.Append("\n");
                if (!data.Unattributed.IsEmpty)
                    Paragraph("Membership-unavailable PRs are excluded from these attributed counts, not evidence of zero activity.");
            }
        }

        private void Context()
        {
            var sectionStart = output.Length;
            Heading(2, "Captured inputs and export query context");
            Field("Analysis", Request.Analysis);
            Field("View", Request.View);
            Field("Run ID", Request.RunId.ToString("D"));
            Field("Export capture anchor", ReportFormatting.Date(Request.ExecutedAt));
            Field("Generated / as of", ReportFormatting.Date(document.Snapshot.AsOf));
            if (document.Snapshot.FeatureRegistry is { } featureRegistry)
            {
                Field("Collection started", ReportFormatting.Date(featureRegistry.StartedAt));
                Field("Collection finished", ReportFormatting.Date(featureRegistry.FinishedAt));
            }
            Field("Requested start", ReportFormatting.RequestedDate(Request.RequestedStart));
            Field("Requested end", ReportFormatting.RequestedDate(Request.RequestedEnd));
            Field("Effective export start (inclusive)", ReportFormatting.Date(Request.Start));
            Field("Effective export end (inclusive)", ReportFormatting.Date(Request.End));
            foreach (var metadata in ReportFormatting.BoundsMetadata(Request))
                Field(metadata.Label, metadata.Value);
            Field("Captured timezone / date semantics", Request.TimeZone);
            Field("Manager", Request.Manager);
            if (Request.Analysis == "gitCommits") Field("Trend interval", Request.TrendPeriod);
            output.Append("\n");
            Heading(3, "Selected sources");
            Header("Source key", "Organization", "Project", "Scope / path", "Branch", "Team");
            foreach (var source in Request.Sources)
                LinkedRow(3, navigation?.SourceLink(source.Key),
                    source.Key, source.Organization, source.Project, source.Scope, source.Branch, source.Team);
            output.Append("\n");
            if (navigation is null || Request.Analysis != "workItems" || !Request.IsTeams)
            {
                Heading(3, "Captured manager / report roster");
                if (Request.Roster.IsEmpty) Paragraph("No manager roster was captured for this view.");
                else
                {
                    Header("Name", "Email", "Principal name", "Identity ID");
                    foreach (var member in Request.Roster)
                        LinkedRow(0, navigation?.MemberLink(member), member.Name, member.Email, member.PrincipalName, member.Id);
                    output.Append("\n");
                }
            }
            if (!document.Snapshot.SkippedSources.IsEmpty)
            {
                Heading(3, "Skipped sources - excluded from totals");
                Paragraph("This report includes successfully collected sources only. Skipped-source activity is unavailable, not zero. The original selections and roster are preserved above.");
                Header("Source", "Reason");
                foreach (var source in document.Snapshot.SkippedSources)
                    LinkedRow(0, navigation?.SourceLink(source.SourceKey),
                        Request.Sources.First(item => item.Key == source.SourceKey).Label, source.Reason);
                output.Append("\n");
            }
            Heading(3, "Coverage and metric definitions");
            foreach (var note in document.MetricNotes.Concat(document.Snapshot.CoverageNotes))
            {
                token.ThrowIfCancellationRequested();
                output.Append("- ");
                output.Markdown(note);
                output.Append("\n");
            }
            if (Request.Analysis == "workItems" && Request.IsTeams)
                Paragraph("Area summaries group distinct work items by their recorded area path rather than selected query roots. Overlapping source memberships are preserved without duplicating items. " + (navigation is null
                    ? "The compact completion list is followed by the complete field, identity, history and duration evidence below."
                    : "Each person's completion section contains their summary and the complete field, identity, history and duration evidence for each work item."));
            if (Request.Analysis == "incidentManagement" && Request.IsTeams)
                Paragraph("TOTAL sums the existing overall contributor credits, not overlapping source rows. Contributed totals count actor/incident pairs, not distinct incidents across different actors. The recorded in-period action definition is unchanged; data availability and coverage are described above.");
            output.Append("\n");
            navigation?.ContextSection(sectionStart, output.Length);
        }

        private void Summary(string heading, ImmutableArray<ReportSummaryRow> rows, bool source)
        {
            Heading(2, heading);
            Header(ReportTables.Headers(Request.Analysis, source));
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                LinkedRow(1, navigation?.SourceLink(row.SourceKey), ReportTables.Cells(row, Request, token));
            }
            output.Append("\n");
            if (rows.IsEmpty) Paragraph(document.Snapshot.AllSourcesSkipped
                ? "No selected source could be collected. Activity is unavailable, not zero."
                : "No contributors in the completed snapshot.");
        }

        private void WorkItems()
        {
            Heading(2, "Distinct work item totals and active-work time");
            WorkStatistics(document.WorkItems);
            if (navigation is not null && Request.IsTeams) return;
            var groups = document.WorkItems.ToLookup(item => item.ContributorKey, StringComparer.Ordinal);
            Heading(2, "All contributor work item details");
            foreach (var member in document.OverallRows)
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                navigation?.MemberSection(output.Length, member.ContributorKey);
                Heading(3, member.Contributor);
                var items = groups[member.ContributorKey].ToImmutableArray();
                WorkStatistics(items);
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    var itemStart = output.Length;
                    var record = item.Entity.Record;
                    Heading(4, $"Work item {record.Organization}:{record.Id} - {record.Title}");
                    WorkItemEvidence(item);
                    navigation?.ItemSection(itemStart, output.Length);
                }
                navigation?.PersonSection(sectionStart, output.Length);
            }
        }

        private void WorkItemEvidence(WorkItemDocument item)
        {
            var record = item.Entity.Record;
            Field("Source-qualified key", record.EntityKey);
            Memberships(item.Entity.SourceKeys);
            Field("ID", ReportFormatting.Number(record.Id));
            Field("Title", record.Title);
            Field("Type", record.Type);
            Field("State", record.State);
            Field("Assignee", ReportFormatting.Identity(record.Assignee));
            Field("Priority", record.Priority?.ToString(CultureInfo.InvariantCulture));
            Field("Created date", ReportFormatting.Date(record.CreatedAt));
            Field("Closed date", ReportFormatting.Date(record.ClosedAt));
            Field("Last changed", ReportFormatting.Date(record.ChangedAt));
            Field("Time to complete (days)", ReportFormatting.Decimal(record.ClosedAt is { } closed ? (closed - record.CreatedAt).TotalDays : null));
            Field("Area path", record.AreaPath);
            Field("Iteration path", record.IterationPath);
            Field("Time in active work (days)", ReportFormatting.Decimal(item.ActiveWorkDays));
            output.Append("\n");
            Heading(5, "State change history");
            if (item.Periods.IsEmpty) Paragraph("No state history was supplied. Active-work duration is unknown, not zero.");
            else
            {
                Header("State", "Entered at", "Entered by", "Exited at", "Next state", "Transition actor", "Duration", "Days");
                for (var index = 0; index < item.Periods.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var period = item.Periods[index];
                    var next = index + 1 < item.Periods.Length ? item.Periods[index + 1] : null;
                    Row(period.State, ReportFormatting.Date(period.EnteredAt), WorkItemHistoryActor(period.Actor),
                        period.ExitedAt.HasValue ? ReportFormatting.Date(period.ExitedAt) : "Ongoing at report as-of",
                        next?.State, next is null ? "-" : WorkItemHistoryActor(next.Actor),
                        TimeSpan.FromDays(period.Days).ToString("c", CultureInfo.InvariantCulture),
                        period.Days.ToString("F6", CultureInfo.InvariantCulture));
                }
                output.Append("\n");
                Heading(5, "Total time by state");
                Header("State", "Duration", "Days");
                foreach (var state in item.Periods.GroupBy(period => period.State, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var days = state.Sum(period => period.Days);
                    Row(state.Key, TimeSpan.FromDays(days).ToString("c", CultureInfo.InvariantCulture), days.ToString("F6", CultureInfo.InvariantCulture));
                }
                output.Append("\n");
            }
            Link(record.Url, "View Work Item", linkRecordedUrl: navigation is not null && Request.IsTeams);
        }

        private string WorkItemHistoryActor(ReportIdentity actor) =>
            ReportFormatting.Identity(actor, includePrincipalAndId: navigation is null || !Request.IsTeams);

        private void WorkStatistics(ImmutableArray<WorkItemDocument> items)
        {
            Field("Total work items completed", ReportFormatting.Number(items.Length));
            Header("Work item type", "Count");
            foreach (var group in items.GroupBy(item => item.Entity.Record.Type)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
                Row(group.Key, ReportFormatting.Number(group.Count()));
            output.Append("\n");
            var values = items.Where(item => item.ActiveWorkDays > 0).Select(item => item.ActiveWorkDays!.Value);
            var stats = ReportDocumentBuilder.DurationStatistics(values, token);
            Header("Active-work measure (days)", "Value");
            Row("Total (known positive durations)", ReportFormatting.Decimal(stats.Total));
            Row("Minimum", ReportFormatting.Decimal(stats.Minimum));
            Row("Average", ReportFormatting.Decimal(stats.Average));
            Row("Maximum", ReportFormatting.Decimal(stats.Maximum));
            Row("Median", ReportFormatting.Decimal(stats.Median));
            Row("Work items with positive active-work time", $"{stats.Count} of {items.Length}");
            Row("Work items with unavailable / single-revision active-work duration", ReportFormatting.Number(items.Count(item => item.ActiveWorkDays is null)));
            output.Append("\n");
            if (items.IsEmpty) Paragraph("No work items for this contributor.");
        }

        private void GitDetails(GitReportPresentation presentation)
        {
            TeamRoster();
            foreach (var source in document.Snapshot.SkippedSources)
                Line($"\u26a0 Skipped {Request.Sources.First(item => item.Key == source.SourceKey).Label}: {source.Reason}");
            BoxHeading($"\U0001F4CB {(Request.IsTeams ? "TEAM " : "")}CODE COMMIT DETAILS BY PERSON:");
            if (document.Snapshot.AllSourcesSkipped)
                Line("No selected source could be collected. Activity is unavailable, not zero.");
            var groups = document.Commits.ToLookup(commit => commit.ContributorKey, StringComparer.Ordinal);
            var teamsHtml = navigation is not null && Request.IsTeams;
            if (teamsHtml && !document.Snapshot.SkippedSources.IsEmpty)
                Paragraph("Person headings count collected commits only. Activity from skipped sources is unavailable, not zero.");
            var members = teamsHtml
                ? presentation.TeamsDetailRows()
                : presentation.OverallRows.Where(row => row.Commits != 0)
                    .OrderByDescending(row => row.Commits).ThenByDescending(row => row.Added)
                    .ThenBy(presentation.Contributor, StringComparer.Ordinal);
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                var role = member.Contributor.StartsWith("Author:", StringComparison.Ordinal) ? "Contributor" : "Committer";
                navigation?.MemberSection(output.Length, member.ContributorKey,
                    groups[member.ContributorKey].SelectMany(commit => new[] { commit.Entity.Record.Author, commit.Entity.Record.Committer }));
                var contributor = navigation is null ? presentation.Contributor(member) : presentation.ContributorForHtml(member);
                var unavailable = teamsHtml && member.Commits == 0 && !document.Snapshot.SkippedSources.IsEmpty;
                BoxHeading(teamsHtml ? presentation.PersonHeading(member)
                    : $"{GitReportPresentation.AuthorIcon} {role}: {contributor} - {member.Commits} commits (+{member.Added}/-{member.Deleted} lines)");
                if (unavailable)
                    Paragraph("No commits were collected for this person. Data is incomplete because one or more selected sources were skipped; this is not evidence of zero activity.");
                foreach (var repository in groups[member.ContributorKey].GroupBy(
                    commit => (commit.Entity.Record.Organization, commit.Entity.Record.RepositoryId)))
                {
                    var memberships = repository.SelectMany(commit => commit.Entity.SourceKeys)
                        .Distinct(StringComparer.Ordinal).ToImmutableArray();
                    Line($"  \U0001F4E6 {presentation.CommitRepositories(memberships)}: {repository.Count()} commits");
                    foreach (var commit in repository.OrderBy(commit => commit.Entity.Record.CommitterDate)
                        .ThenBy(commit => commit.Entity.Record.Id, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        var record = commit.Entity.Record;
                        Rule();
                        var pr = navigation is not null && Request.IsTeams ? presentation.PullRequestForHeading(record) : null;
                        var itemStart = ItemHeading(pr is { } heading
                            ? $"\U0001F504 PR #{ReportFormatting.Number(heading.Id)}:"
                            : $"\U0001F504 Commit: {record.Id[..Math.Min(8, record.Id.Length)]}...");
                        Line("Author: " + GitReportPresentation.Identity(record.Author));
                        Line((navigation is not null && Request.IsTeams ? GitReportPresentation.CommitterIcon + " " : "")
                            + "Committer: " + GitReportPresentation.Identity(record.Committer));
                        Line("Date: " + GitReportPresentation.Timestamp(record.CommitterDate));
                        Line("Message: (Full commit message)");
                        CodeBlock(record.Message);
                        Line($"\U0001F4CA Line Changes: +{commit.Added} additions, -{commit.Deleted} deletions");
                        Line($"\U0001F4C1 Changed Files ({record.Files.Length}):");
                        foreach (var file in record.Files)
                            Line($"  \U0001F4C4 {file.Path} [{GitReportPresentation.ChangeType(file.ChangeType)}] ({GitReportPresentation.FileDelta(file)})");
                        Line($"\U0001F517 Parent count (not PR evidence): {record.ParentCount}");
                        GitDetailField("\U0001F511", "Commit ID", record.Id);
                        GitDetailField("\U0001F3E2", "Organization", record.Organization);
                        GitDetailField("\U0001F5C2\uFE0F", "Repository ID", record.RepositoryId);
                        if (navigation is null || !Request.IsTeams)
                        {
                            Line("Source-qualified key: " + record.EntityKey);
                            Line("Source memberships: " + SourceEvidence(commit.Entity.SourceKeys));
                            Line("Author identity: " + ReportFormatting.Identity(record.Author));
                            Line("Committer identity: " + ReportFormatting.Identity(record.Committer));
                        }
                        GitDetailField("\U0001F5D3\uFE0F", "Author date", ReportFormatting.Date(record.AuthorDate));
                        GitDetailField("\U0001F4C5", "Committer date", ReportFormatting.Date(record.CommitterDate));
                        Link(record.Url, "\U0001F310 URL", linkRecordedUrl: navigation is not null && Request.IsTeams);
                        Rule();
                        if (navigation is not null && Request.IsTeams) output.Append("\n");
                        navigation?.ItemSection(itemStart, output.Length, pr?.Title);
                    }
                }
                navigation?.PersonSection(sectionStart, output.Length);
            }
        }

        private void GitDetailField(string icon, string label, string value) =>
            Line((navigation is not null && Request.IsTeams ? icon + " " : "") + label + ": " + value);

        private void TeamRoster()
        {
            if (Request.IsTeams)
            {
                BoxHeading($"\U0001F465 TEAM ROSTER ({Request.Roster.Length} members):");
                var supportsHierarchy = navigation is not null && Request.Analysis is "incidentManagement" or "gitCommits" or "workItems" or "featureRegistry";
                var showHierarchy = supportsHierarchy && !Request.RosterParentIndices.IsEmpty;
                if (showHierarchy) navigation!.RosterHierarchy(output.Length);
                Line(showHierarchy
                    ? "Captured reporting hierarchy; manager/report relationships and sibling order match the online display."
                    : supportsHierarchy
                        ? "Captured roster; reporting hierarchy was not captured for this run. Run the analysis again to include it."
                        : "Captured roster; reporting levels are not recorded in the export snapshot.");
                var roster = Request.Roster.AsEnumerable();
                if (navigation is not null && Request.Analysis == "incidentManagement" && !showHierarchy)
                    roster = roster.OrderBy(member => member.DisplayName.Split((char[]?)null, 2,
                            StringSplitOptions.RemoveEmptyEntries)[0], StringComparer.OrdinalIgnoreCase)
                        .ThenBy(member => member.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase);
                foreach (var member in roster)
                    Line("\u2022 " + GitReportPresentation.Identity(member), navigation?.MemberLink(member));
                Rule();
            }
        }

        private void TeamWorkItemDetails(WorkItemReportPresentation presentation)
        {
            BoxHeading("\U0001F3C6 TEAM WORK ITEM COMPLETION DETAILS BY PERSON (Target State: Done):");
            Line("Overlapping selections are grouped together; each distinct work item appears once in this completion list.");
            foreach (var member in presentation.OverallRows.Where(row => navigation is not null || row.WorkItems != 0))
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                navigation?.MemberSection(output.Length, member.ContributorKey);
                var contributor = presentation.Contributor(member);
                BoxHeading($"\U0001F464 {contributor}: {ReportFormatting.Number(member.WorkItems)} work item(s) completed");
                if (navigation is not null && member.WorkItems != 0)
                    TeamWorkItemPersonSummary(contributor, presentation.Items(member).ToImmutableArray());
                foreach (var group in presentation.Groups(member))
                {
                    Rule();
                    Line($"  \U0001F4E6 {presentation.SourceCaption(group.SourceKeys)}: {ReportFormatting.Number(group.Items.Length)} item(s)");
                    foreach (var item in group.Items)
                    {
                        token.ThrowIfCancellationRequested();
                        var record = item.Entity.Record;
                        Rule();
                        if (navigation is not null)
                        {
                            TeamWorkItemRecordDetails(item);
                            continue;
                        }
                        Line($"    \u2022 [{record.Type}] #{ReportFormatting.Number(record.Id)} (P{record.Priority?.ToString(CultureInfo.InvariantCulture) ?? "?"}): {record.Title}");
                        if (item.Periods.IsEmpty)
                            Line("      No state history was supplied. Active-work duration is unknown, not zero.");
                        else if (item.Periods.Length == 1)
                        {
                            var period = item.Periods[0];
                            Line($"      {period.State} entered by {period.Actor.DisplayName} on {ReportFormatting.CalendarDate(period.EnteredAt)}; no state transition recorded.");
                        }
                        else
                            for (var index = 1; index < item.Periods.Length; index++)
                            {
                                var previous = item.Periods[index - 1];
                                var next = item.Periods[index];
                                Line($"      {previous.State} \u2192 {next.State} by {next.Actor.DisplayName} on {ReportFormatting.CalendarDate(next.EnteredAt)}");
                            }
                    }
                }
                navigation?.PersonSection(sectionStart, output.Length);
            }
            Rule();
            if (document.WorkItems.IsEmpty) Paragraph("No work items in the completed snapshot.");
        }

        private void TeamWorkItemPersonSummary(string contributor, ImmutableArray<WorkItemDocument> items)
        {
            Line("\U0001F4CA Summary for " + contributor);
            output.Append("\n");
            WorkStatistics(items);
        }

        private void TeamWorkItemRecordDetails(WorkItemDocument item)
        {
            var record = item.Entity.Record;
            var itemStart = ItemHeading($"\U0001F4CB Work Item #{ReportFormatting.Number(record.Id)} - {record.Title} ({record.Type})");
            Line("\U0001F4DD Title: " + record.Title);
            Line("\U0001F504 State: " + record.State);
            Line("\U0001F464 Assignee: " + ReportFormatting.Identity(record.Assignee));
            if (record.Priority is { } priority)
                Line("\u26A1 Priority: " + ReportFormatting.Number(priority));
            Line("\U0001F4C5 Created Date: " + ReportFormatting.CompactTimestamp(record.CreatedAt));
            if (record.ClosedAt is { } closed)
            {
                Line("\u2705 Closed Date: " + ReportFormatting.CompactTimestamp(closed));
                Line("\u23F1\uFE0F Time to Complete: " + ReportFormatting.Decimal((closed - record.CreatedAt).TotalDays) + " days");
            }
            else
                Line("\U0001F552 Last Changed: " + ReportFormatting.CompactTimestamp(record.ChangedAt));
            if (!string.IsNullOrEmpty(record.AreaPath)) Line("\U0001F4C2 Area Path: " + record.AreaPath);
            if (!string.IsNullOrEmpty(record.IterationPath)) Line("\U0001F501 Iteration: " + record.IterationPath);
            Line("\u23F1\uFE0F Time in Active Work: " + (item.ActiveWorkDays is { } active
                ? ReportFormatting.Decimal(active) + " days" : "Unknown (insufficient state history)"));

            Heading(5, "\U0001F4DC State Change History");
            if (item.Periods.IsEmpty)
                Paragraph("No state history was supplied. Active-work duration is unknown, not zero.");
            else
                for (var index = 0; index < item.Periods.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var period = item.Periods[index];
                    var duration = FormatUtilities.ToStringValue(TimeSpan.FromDays(period.Days));
                    if (index + 1 < item.Periods.Length)
                    {
                        var next = item.Periods[index + 1];
                        Line($"\U0001F552 {ReportFormatting.CompactTimestamp(next.EnteredAt)} - {WorkItemHistoryActor(next.Actor)}: {period.State} \u2192 {next.State} (spent {duration} in '{period.State}')");
                    }
                    else
                        Line($"\U0001F552 Currently in {period.State} since {ReportFormatting.CompactTimestamp(period.EnteredAt)}; entered by {WorkItemHistoryActor(period.Actor)} ({duration}; ongoing at report as-of {ReportFormatting.CompactTimestamp(document.Snapshot.AsOf)}).");
                }
            Heading(5, "Recorded field evidence");
            WorkItemEvidence(item);
            navigation?.ItemSection(itemStart, output.Length);
        }

        private int ItemHeading(string text)
        {
            var folding = navigation is not null && Request.IsTeams;
            if (folding) output.Append("\n");
            var start = output.Length;
            if (folding)
            {
                output.Markdown(text, table: true);
                output.Append("  \n\n");
            }
            else Line(text);
            return start;
        }

        private void TeamWorkItemSummary(WorkItemReportPresentation presentation, bool byArea)
        {
            var heading = byArea ? document.SourceHeading : document.OverallHeading;
            BoxHeading($"\U0001F4CA {heading} ({ReportFormatting.CalendarDate(Request.Start)} to {ReportFormatting.CalendarDate(Request.End)}):");
            Header(WorkItemReportPresentation.Headers(byArea));
            if (byArea)
                foreach (var row in presentation.AreaRows) ReferenceRow(presentation.Cells(row));
            else
                foreach (var row in presentation.OverallRows) ReferenceRow(presentation.Cells(row));
            output.Append("\n");
        }

        private void ReferenceRow(string[] cells, string? pathLink = null)
        {
            output.Append("|");
            for (var index = 0; index < cells.Length; index++)
            {
                var cell = cells[index];
                output.Append(" ");
                if (cell.Length != 0) LinkedText(cell, index == 1 ? pathLink : null, table: true);
                output.Append(" |");
            }
            output.Append("\n");
        }

        private void GitSummary(string heading, ImmutableArray<ReportSummaryRow> rows, bool bySource, GitReportPresentation presentation)
        {
            BoxHeading($"\U0001F4CA {heading} ({ReportFormatting.CalendarDate(Request.Start)} to {ReportFormatting.CalendarDate(Request.End)}):");
            Header(GitReportPresentation.Headers(bySource));
            foreach (var row in rows)
                Row(presentation.Cells(row, bySource, roleIcons: navigation is not null));
            output.Append("\n");
            if (rows.IsEmpty) Paragraph(document.Snapshot.AllSourcesSkipped
                ? "No selected source could be collected. Activity is unavailable, not zero."
                : "No contributors in the completed snapshot.");
        }

        private void Line(string text, string? href = null)
        {
            LinkedText(text, href);
            output.Append("  \n");
        }

        private void Rule(bool bold = false) => output.Append(new string(bold ? '\u2501' : '\u2500', 120) + "\n");

        private void BoxHeading(string text, Action? inlineContent = null)
        {
            output.Append("\n" + new string('\u2550', 120) + "\n");
            if (inlineContent is null) output.Markdown(text, table: navigation is not null);
            else inlineContent();
            output.Append("  \n");
            output.Append(new string('\u2550', 120) + "\n\n");
        }

        private void GitEvidence()
        {
            Heading(2, "Distinct Git totals");
            if (document.Snapshot.AllSourcesSkipped)
            {
                Paragraph("No selected source could be collected. Commit, PR, line, and file totals are unavailable, not zero.");
                return;
            }
            Field("Commits", ReportFormatting.Number(document.Commits.Length));
            Field("Completed PRs", ReportFormatting.Number(document.PullRequests.Length));
            Field("Lines added", ReportFormatting.Number(document.Commits.Sum(commit => commit.Added)));
            Field("Lines deleted", ReportFormatting.Number(document.Commits.Sum(commit => commit.Deleted)));
            Field("Files changed", ReportFormatting.Number(document.Commits.Sum(commit => (long)commit.Entity.Record.Files.Length)));
            output.Append("\n");
            Heading(2, $"Git commit trends - {Request.TrendPeriod}");
            Paragraph("All available periods and detailed commits are included. Commits are not authoritative PR completions.");
            Header(ReportTables.TrendHeaders);
            foreach (var period in document.GitTrends) Row(ReportTables.TrendCells(period));
            Row(ReportTables.TrendCells(ReportTables.TrendTotal(document.GitTrends, token)));
            output.Append("\n");
            if (navigation is not null && Request.IsTeams) return;
            Heading(2, "Completed PR evidence");
            Paragraph("Only the recorded closedBy completion actor and closedDate establish PRs Merged credit; commit messages and commit parents are not evidence.");
            Header("Source-qualified PR", "ID", "Title", "Organization", "Repository ID", "Target branch", "closedBy", "closedDate", "Source memberships", "Recorded URL");
            foreach (var pr in document.PullRequests)
            {
                token.ThrowIfCancellationRequested();
                navigation?.MemberSection(output.Length, actors: [pr.Record.CompletedBy]);
                Row(pr.Record.EntityKey, ReportFormatting.Number(pr.Record.Id), pr.Record.Title, pr.Record.Organization,
                    pr.Record.RepositoryId, pr.Record.TargetBranch, ReportFormatting.Identity(pr.Record.CompletedBy),
                    ReportFormatting.Date(pr.Record.CompletedAt), SourceEvidence(pr.SourceKeys), pr.Record.Url);
            }
            output.Append("\n");
            foreach (var pr in document.PullRequests) Link(pr.Record.Url, $"View PR {pr.Record.EntityKey}");
            if (document.PullRequests.IsEmpty) Paragraph("0 completed PRs in the completed snapshot.");
        }

        private void TeamIncidentIndividualDetails(IncidentReportPresentation presentation)
        {
            BoxHeading("\U0001F4CB TEAM INCIDENT DETAILS BY INDIVIDUALS:");
            Paragraph("Incidents are grouped by current owning contact across all selected ICM paths. Category memberships can overlap; counts use distinct silo-qualified incidents, not repeated source selections. This ownership view does not change mitigation, resolution or recorded-action contribution credits.");
            Paragraph("Contributed Incidents retains the ICM-path category of incidents outside Active, Created, Mitigated and Resolved. Records without in-period contributor evidence remain explicit, not credited to their owners. Full recorded evidence remains in the ICM-path sections below.");
            Paragraph("Each individual's closed-incident summary retains the recorded resolution/mitigation-actor attribution and duration calculations. It may cover different incidents than the current-ownership categories below. Counts and missing durations describe the collected snapshot only.");
            var closedGroups = presentation.ClosedGroupsByMemberKey();
            foreach (var owner in presentation.OwnerGroups(document.Incidents))
            {
                token.ThrowIfCancellationRequested();
                var ownerStart = output.Length;
                var memberKey = presentation.MemberKey(owner.Incidents[0].Entity.Record.Owner);
                navigation!.MemberSection(output.Length, memberKey);
                Heading(3, $"\U0001F464 {owner.OwningContact} ({ReportFormatting.Number(owner.Incidents.Length)} incident(s))");
                closedGroups.TryGetValue(memberKey, out var closed);
                IncidentGroupStatistics(closed?.Incidents ?? [], closed?.SeverityStatistics ?? [], closed: true);
                if (closed is null)
                    Paragraph("No closed incidents are attributed to this individual in the collected snapshot. Empty duration tables do not imply zero-duration incidents.");
                Category("\U0001F7E2 Active incidents", record => record.Active);
                Category("\U0001F195 Created incidents", record => record.Created);
                Category("\U0001F527 Mitigated incidents", record => record.Mitigated);
                Category("\u2705 Resolved incidents", record => record.Resolved);
                Category("\U0001F4AC Contributed Incidents", record => !record.Active && !record.Created && !record.Mitigated && !record.Resolved);
                navigation!.OwnerSection(ownerStart, output.Length);

                void Category(string label, Func<IncidentReportRecord, bool> selected)
                {
                    var items = owner.Incidents.Where(item => selected(item.Entity.Record)).ToImmutableArray();
                    var categoryStart = output.Length;
                    Heading(4, $"{label}: {ReportFormatting.Number(items.Length)} incident(s)");
                    if (items.IsEmpty) Paragraph("No incidents in this category for this owning contact in the collected snapshot.");
                    foreach (var incident in items)
                        CompactIncident(incident, presentation, headingLevel: 5, referenceTitle: true, includeEvidence: false);
                    output.Append("\n");
                    navigation!.QueueSection(categoryStart, output.Length);
                }
            }
            if (document.Snapshot.AllSourcesSkipped)
            {
                navigation!.MemberSection(output.Length, actors: Request.Roster);
                Heading(3, "Individual incident data unavailable");
                Paragraph("No selected source could be collected. Activity is unavailable, not zero.");
            }
            else if (document.Incidents.IsEmpty) Paragraph("No incidents in the completed snapshot.");
        }

        private void TeamIncidentDetails(IncidentReportPresentation presentation)
        {
            BoxHeading("\U0001F4CB TEAM INCIDENT DETAILS BY ICM PATH:");
            Line("Category memberships can overlap. Repeated IDs refer to the same silo-qualified record, not extra incidents in overall totals.");
            foreach (var group in presentation.Groups())
            {
                navigation?.SourceSection(output.Length, group.SourceKeys);
                BoxHeading("\U0001F4E6 ICM group: " + presentation.SourceCaption(group.SourceKeys),
                    navigation is null ? null : () =>
                    {
                        output.Markdown("\U0001F4E6 ICM group: ");
                        WriteSources(Request.Sources.Where(source => group.SourceKeys.Contains(source.Key))
                            .Select(source => source.Key), table: true, evidence: false);
                    });
                if (navigation is not null)
                {
                    IncidentCategoryByOwner(group, presentation, "\U0001F7E2 Active incidents", record => record.Active,
                        "No active incidents in this queue in the collected snapshot.");
                    IncidentCategoryByOwner(group, presentation, "\U0001F195 Created incidents", record => record.Created,
                        "No created incidents in this queue in the collected snapshot.");
                    IncidentCategoryByOwner(group, presentation, "\U0001F527 Mitigated incidents", record => record.Mitigated,
                        "No mitigated incidents in this queue in the collected snapshot.");
                    IncidentCategoryByOwner(group, presentation, "\u2705 Resolved incidents", record => record.Resolved,
                        "No resolved incidents in this queue in the collected snapshot.");
                    IncidentCategoryByContributor(group, presentation);
                }
                else
                {
                    Category("\U0001F7E2 Active", record => record.Active);
                    Category("\U0001F195 Created", record => record.Created);
                    Category("\U0001F527 Mitigated", record => record.Mitigated);
                    Category("\u2705 Resolved", record => record.Resolved);
                    Category("Other selected (including action-only)", record => !record.Active && !record.Created && !record.Mitigated && !record.Resolved);
                }

                void Category(string label, Func<IncidentReportRecord, bool> selected)
                {
                    var items = group.Incidents.Where(item => selected(item.Entity.Record)).ToImmutableArray();
                    Rule();
                    Line($"  {label}: {ReportFormatting.Number(items.Length)} incident(s)");
                    foreach (var item in items) CompactIncident(item, presentation);
                }
            }
            if (document.Incidents.IsEmpty) Paragraph(document.Snapshot.AllSourcesSkipped
                ? "No selected source could be collected. Activity is unavailable, not zero."
                : "No incidents in the completed snapshot.");
        }

        private void IncidentCategoryByOwner(IncidentReportPresentation.SourceGroup group, IncidentReportPresentation presentation,
            string label, Func<IncidentReportRecord, bool> selected, string emptyMessage)
        {
            var items = group.Incidents.Where(item => selected(item.Entity.Record)).ToImmutableArray();
            var queueStart = output.Length;
            Heading(4, $"{label}: {ReportFormatting.Number(items.Length)}");
            if (items.IsEmpty) Paragraph(emptyMessage);
            foreach (var owner in presentation.OwnerGroups(items))
            {
                token.ThrowIfCancellationRequested();
                var ownerStart = output.Length;
                Heading(5, $"\U0001F464 Owning Contact: {owner.OwningContact} ({ReportFormatting.Number(owner.Incidents.Length)} incident(s))");
                foreach (var incident in owner.Incidents) CompactIncident(incident, presentation, headingLevel: 6);
                output.Append("\n");
                navigation!.OwnerSection(ownerStart, output.Length);
            }
            navigation!.QueueSection(queueStart, output.Length);
        }

        private void IncidentCategoryByContributor(IncidentReportPresentation.SourceGroup group, IncidentReportPresentation presentation)
        {
            var items = group.Incidents.Where(item => !item.Entity.Record.Active && !item.Entity.Record.Created
                && !item.Entity.Record.Mitigated && !item.Entity.Record.Resolved).ToImmutableArray();
            var categoryStart = output.Length;
            Heading(4, $"\U0001F4AC Contributed Incidents: {ReportFormatting.Number(items.Length)} incident(s)");
            Paragraph("This category contains incidents outside Active, Created, Mitigated and Resolved. Its count uses distinct silo-qualified incidents. Incidents repeat across recorded in-period contributor groups without adding activity to report totals.");
            if (items.IsEmpty) Paragraph("No incidents in this category in the collected snapshot.");
            foreach (var contributor in presentation.ContributionGroups(items))
            {
                token.ThrowIfCancellationRequested();
                var contributorStart = output.Length;
                Heading(5, (contributor.ContributorKey is null ? "\u26A0\uFE0F " : "\U0001F464 Contributor: ")
                    + $"{contributor.Contributor} ({ReportFormatting.Number(contributor.Incidents.Length)} incident(s))");
                if (contributor.ContributorKey is null)
                    Paragraph("These selected incidents have no recorded in-period contributor evidence. They are retained for completeness, not attributed to their owners or counted as contributions.");
                foreach (var incident in contributor.Incidents) CompactIncident(incident, presentation, headingLevel: 6, referenceTitle: true);
                output.Append("\n");
                navigation!.OwnerSection(contributorStart, output.Length);
            }
            navigation!.QueueSection(categoryStart, output.Length);
        }

        private void CompactIncident(IncidentDocument incident, IncidentReportPresentation presentation, int headingLevel = 4,
            bool referenceTitle = false, bool includeEvidence = true)
        {
            token.ThrowIfCancellationRequested();
            var record = incident.Entity.Record;
            Rule();
            if (navigation is not null)
            {
                var headingStart = ItemHeading("Incident Details:");
                navigation.RecordHeading(headingStart, record.Title.AsMemory(), headingLevel);
                if (includeEvidence) navigation.IncidentSection(headingStart, incident);
            }
            else Line("Incident Details:");
            Detail("ID", record.Id);
            Detail("Silo", record.SiloId);
            if (navigation is not null)
            {
                var destination = LinkDestination(record.Url);
                output.Markdown("  URL: ");
                LinkedText(ReportFormatting.Label(record.Url), destination);
                output.Append("  \n");
                if (destination is null && !string.IsNullOrEmpty(record.Url))
                    Line("  Unsafe or unsupported URL: retained as text, not linked.");
            }
            else Detail("URL", record.Url);
            if (referenceTitle && codeBlocks is not null)
            {
                output.Markdown("  Title: ");
                if (!countOnly) codeBlocks.AddInline(output.Length, ReportFormatting.Label(record.Title));
                output.Append("`recorded-title`  \n");
            }
            else Detail("Title", record.Title);
            Detail("Status", record.Status);
            Detail("Severity", record.Severity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown");
            Detail("Is Outage", record.Metadata.GetValueOrDefault("IsOutage"));
            Detail("Created Date", IncidentReportPresentation.Date(record.CreatedAt));
            Detail("Modified Date", IncidentReportPresentation.Date(record.ModifiedAt));
            Line($"  Owning Team Name: {ReportFormatting.Label(record.OwningTeam)}",
                navigation?.PathLink(record.OwningTeam, incident.Entity.SourceKeys));
            Detail("Owning Contact", record.Owner.DisplayName);
            if (record.MitigatedAt.HasValue)
            {
                Detail("Mitigation Date", IncidentReportPresentation.Date(record.MitigatedAt));
                Detail("Mitigated By", record.MitigatedBy.DisplayName);
                Detail("TTM", IncidentReportPresentation.Duration(incident.MitigateHours));
            }
            if (record.ResolvedAt.HasValue)
            {
                Detail("Resolution Date", IncidentReportPresentation.Date(record.ResolvedAt));
                Detail("Resolved By", record.ResolvedBy.DisplayName);
                Detail("TTR", IncidentReportPresentation.Duration(incident.ResolveHours));
            }
            var contributors = presentation.Contributors(incident);
            Detail($"Contributors with recorded in-period actions ({ReportFormatting.Number(contributors.Length)})",
                contributors.IsEmpty ? "None" : ReportFormatting.Join(", ", contributors, token));
            if (!includeEvidence && navigation is not null)
                Line("  Full recorded evidence", navigation.IncidentLink(incident));
            if (includeEvidence && navigation is not null && recordedIncidentEvidence.Add(record.EntityKey))
            {
                Rule();
                output.Append("\n");
                Field("Source-qualified key", record.EntityKey);
                Memberships(incident.Entity.SourceKeys);
                IncidentEvidence(incident, headingLevel: 5, compact: true);
            }
            Rule();

            void Detail(string label, string? value) => Line($"  {label}: {ReportFormatting.Label(value)}");
        }

        private void TeamIncidentSummary(IncidentReportPresentation presentation, bool bySource)
        {
            var heading = bySource ? document.SourceHeading : document.OverallHeading;
            BoxHeading($"\U0001F4CA {heading} ({ReportFormatting.CalendarDate(Request.Start)} to {ReportFormatting.CalendarDate(Request.End)}):");
            Header(IncidentReportPresentation.Headers(bySource));
            foreach (var row in bySource ? presentation.SourceRows : presentation.OverallRows)
            {
                if (!bySource) navigation?.MemberSection(output.Length, row.ContributorKey);
                ReferenceRow(presentation.Cells(row, bySource), bySource ? navigation?.SourceLink(row.SourceKey) : null);
            }
            if (!bySource && !document.Snapshot.AllSourcesSkipped) ReferenceRow(presentation.TotalCells());
            output.Append("\n");
        }

        private void Incidents()
        {
            var referencesOnly = navigation is not null && Request.IsTeams;
            Heading(2, referencesOnly ? "Incident statistics" : "Incident details");
            if (referencesOnly)
                Paragraph("Individual summaries and incident records appear above. The global Active statistics below are followed directly by distinct incident totals and weighted duration statistics.");
            var rendered = new HashSet<string>(StringComparer.Ordinal);
            for (var groupIndex = 0; groupIndex < document.IncidentGroups.Length; groupIndex++)
            {
                if (referencesOnly && groupIndex != 0) break;
                var group = document.IncidentGroups[groupIndex];
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                if (groupIndex != 0 && !group.Incidents.IsEmpty)
                {
                    var record = group.Incidents[0].Entity.Record;
                    var actor = record.ResolvedAt is not null ? record.ResolvedBy : record.MitigatedBy;
                    navigation?.MemberSection(output.Length, actors: [actor]);
                }
                Heading(3, group.Contributor);
                IncidentGroupStatistics(group.Incidents, group.SeverityStatistics, closed: false,
                    detectionOnly: referencesOnly && groupIndex == 0);
                if (group.Contributor == "Active" && Request.IsTeams
                    && Request.Sources.Select(source => source.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    ActiveTeamStatistics();
                if (group.Incidents.IsEmpty) Paragraph("0 incidents in this group.");
                if (!referencesOnly)
                {
                    foreach (var incident in group.Incidents)
                    {
                        var itemStart = output.Length;
                        Incident(incident);
                        if (groupIndex != 0) navigation?.ItemSection(itemStart, output.Length);
                        rendered.Add(incident.Entity.Record.EntityKey);
                    }
                }
                if (groupIndex != 0) navigation?.PersonSection(sectionStart, output.Length);
            }
            foreach (var member in document.OverallRows.Where(member =>
                !referencesOnly && !document.IncidentGroups.Any(group => group.Contributor == member.Contributor)))
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                navigation?.MemberSection(output.Length, member.ContributorKey);
                Heading(3, member.Contributor);
                Header("Active", "Created", "Mitigated", "Resolved", "Contributed");
                Row(ReportFormatting.Number(member.Active), ReportFormatting.Number(member.Created),
                    ReportFormatting.Number(member.Mitigated), ReportFormatting.Number(member.Resolved), ReportFormatting.Number(member.Contributed));
                output.Append("\n");
                Paragraph("This contributor has no separate closed-incident detail group. Active, created-only, and action-only incidents and evidence remain fully included.");
                navigation?.PersonSection(sectionStart, output.Length);
            }
            var other = referencesOnly ? [] : document.Incidents.Where(item => !rendered.Contains(item.Entity.Record.EntityKey)).ToImmutableArray();
            if (!referencesOnly && !other.IsEmpty)
            {
                Heading(3, "Other selected incidents (including created-only and action-only)");
                foreach (var incident in other) Incident(incident);
            }
            Heading(2, "Distinct incident totals and weighted duration statistics");
            Field("Distinct incidents", ReportFormatting.Number(document.Incidents.Length));
            Field("Active as of collection", ReportFormatting.Number(document.Incidents.Count(item => item.Entity.Record.Active)));
            Field("Created in period", ReportFormatting.Number(document.Incidents.Count(item => item.Entity.Record.Created)));
            Field("Mitigated in period", ReportFormatting.Number(document.Incidents.Count(item => item.Entity.Record.Mitigated)));
            Field("Resolved in period", ReportFormatting.Number(document.Incidents.Count(item => item.Entity.Record.Resolved)));
            Field("Distinct incidents with recorded in-period actions", ReportFormatting.Number(document.IncidentActions.Select(action => action.Record.IncidentKey).Distinct(StringComparer.Ordinal).Count()));
            output.Append("\n");
            IncidentStatistics(document.IncidentStatistics);
            if (!referencesOnly)
            {
                Heading(2, "Recorded incident action history and contribution evidence");
                Paragraph("Several recorded actions on one incident count as one Contributed incident for that actor. Recorded incident, actor, timestamp, action type and source memberships below are the evidence; warehouse action IDs are included when provided. Latest incident modification is not substituted.");
                IncidentActionTable(document.IncidentActions);
            }
        }

        private void IncidentActionTable(ImmutableArray<ReportEntity<IncidentActionReportRecord>> actions)
        {
            Header("Silo-qualified incident", "Silo", "Incident ID", "Action ID", "Actor", "Timestamp", "Action / record type", "Source memberships");
            foreach (var action in actions)
            {
                token.ThrowIfCancellationRequested();
                RowCells(-1, null, action.Record.IncidentKey, action.Record.SiloId, action.Record.IncidentId,
                    action.Record.ActionId.Length == 0 ? "Not provided" : action.Record.ActionId,
                    ReportFormatting.Identity(action.Record.Actor), ReportFormatting.Date(action.Record.Timestamp),
                    action.Record.ActionType);
                output.Append(" ");
                if (navigation is null) output.Markdown(SourceEvidence(action.SourceKeys), table: true);
                else WriteSources(action.SourceKeys, table: true);
                output.Append(" |\n");
            }
            output.Append("\n");
            if (actions.IsEmpty) Paragraph("0 recorded in-period actions in the completed snapshot.");
        }

        private void Incident(IncidentDocument incident, int headingLevel = 4)
        {
            token.ThrowIfCancellationRequested();
            var record = incident.Entity.Record;
            navigation?.SourceSection(output.Length, incident.Entity.SourceKeys);
            Heading(headingLevel, $"Incident {record.SiloId}:{record.Id} - {record.Title}");
            Field("Source-qualified key", record.EntityKey);
            Memberships(incident.Entity.SourceKeys);
            Field("Incident ID", record.Id);
            Field("Silo", record.SiloId);
            Field("Title", record.Title);
            Field("Status", record.Status);
            Field("Severity", record.Severity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown");
            IncidentEvidence(incident, headingLevel);
        }

        private void IncidentEvidence(IncidentDocument incident, int headingLevel, bool compact = false)
        {
            var record = incident.Entity.Record;
            Field("Owning tenant", record.OwningTenant);
            if (!compact) Field("Owning team", record.OwningTeam, navigation?.PathLink(record.OwningTeam, incident.Entity.SourceKeys));
            Field("Owning contact", ReportFormatting.Identity(record.Owner));
            Field("Created date", ReportFormatting.Date(record.CreatedAt));
            Field("Modified date", ReportFormatting.Date(record.ModifiedAt));
            Field("Impact start", ReportFormatting.Date(record.ImpactStart));
            Field("Mitigation date", ReportFormatting.Date(record.MitigatedAt));
            Field("Mitigated by", ReportFormatting.Identity(record.MitigatedBy));
            Field("Resolution date", ReportFormatting.Date(record.ResolvedAt));
            Field("Resolved by", ReportFormatting.Identity(record.ResolvedBy));
            Field("Active as of collection", record.Active ? "Yes" : "No");
            Field("Created in period", record.Created ? "Yes" : "No");
            Field("Mitigated in period", record.Mitigated ? "Yes" : "No");
            Field("Resolved in period", record.Resolved ? "Yes" : "No");
            Field("Time to detect (TTD)", ReportFormatting.Hours(incident.DetectHours));
            Field("Time to mitigate (TTM, hours)", ReportFormatting.Decimal(record.MitigatedAt is { } mitigated ? (mitigated - record.CreatedAt).TotalHours : null));
            Field("Time to resolve (TTR, hours)", ReportFormatting.Decimal(record.ResolvedAt is { } resolved ? (resolved - record.CreatedAt).TotalHours : null));
            output.Append("\n");
            Heading(headingLevel + 1, "Visible incident metadata");
            Header("Field", "Value");
            foreach (var field in record.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Row(field.Key, field.Value);
            output.Append("\n");
            Heading(headingLevel + 1, "Mitigation text");
            if (string.IsNullOrEmpty(record.Mitigation)) Paragraph("-");
            else CodeBlock(record.Mitigation);
            Link(record.Url, "View Incident", linkRecordedUrl: navigation is not null && Request.IsTeams);
        }

        private void IncidentGroupStatistics(ImmutableArray<IncidentDocument> incidents,
            ImmutableArray<IncidentSeverityStatistics> statistics, bool closed, bool detectionOnly = false)
        {
            Field(closed ? "Distinct closed incidents in group" : "Distinct incidents in group",
                ReportFormatting.Number(incidents.Length));
            Field("Currently resolved", ReportFormatting.Number(incidents.Count(item => item.Entity.Record.Status == "Resolved")));
            Field("Currently mitigated", ReportFormatting.Number(incidents.Count(item => item.Entity.Record.Status == "Mitigated")));
            output.Append("\n");
            IncidentStatistics(statistics, detectionOnly);
        }

        private void IncidentStatistics(ImmutableArray<IncidentSeverityStatistics> statistics, bool detectionOnly = false)
        {
            Heading(4, "Severity counts");
            Header("Severity", "Incident count");
            foreach (var severity in statistics)
                Row(Severity(severity.Severity), ReportFormatting.Number(severity.Count));
            output.Append("\n");
            foreach (var metric in detectionOnly ? new[] { "TTD" } : new[] { "TTD", "TTM", "TTR" })
            {
                Heading(4, $"{metric} by severity");
                Header("Severity", "Incident count with duration", "Minimum", "Average", "Maximum", "Minimum hours", "Average hours", "Maximum hours");
                foreach (var severity in statistics)
                {
                    var stats = metric == "TTD" ? severity.Detect : metric == "TTM" ? severity.Mitigate : severity.Resolve;
                    Row(Severity(severity.Severity), ReportFormatting.Number(stats.Count),
                        ReportFormatting.Hours(stats.Minimum), ReportFormatting.Hours(stats.Average), ReportFormatting.Hours(stats.Maximum),
                        ReportFormatting.Decimal(stats.Minimum), ReportFormatting.Decimal(stats.Average), ReportFormatting.Decimal(stats.Maximum));
                }
                output.Append("\n");
            }
        }

        private void ActiveTeamStatistics()
        {
            Dictionary<string, string>? teamLabels = null;
            if (navigation is not null && Request.IsTeams)
            {
                teamLabels = new(StringComparer.OrdinalIgnoreCase);
                foreach (var incident in document.Incidents)
                {
                    token.ThrowIfCancellationRequested();
                    var record = incident.Entity.Record;
                    if (record.Active)
                        teamLabels.TryAdd(ReportDocumentBuilder.TeamKey(record),
                            $"{ReportFormatting.Label(record.OwningTenant)}\\{ReportFormatting.Label(record.OwningTeam)}");
                }
            }
            string TeamLabel(IncidentTeamStatistics team) => teamLabels is null ? team.Team : teamLabels[team.Team];

            Heading(4, "Active severity by owning team");
            Header("Owning team", "Severity", "Incident count");
            foreach (var team in document.ActiveTeamStatistics)
                LinkedRow(0, navigation?.PathLink(team.Team),
                    TeamLabel(team), Severity(team.Statistics.Severity), ReportFormatting.Number(team.Statistics.Count));
            output.Append("\n");
            Heading(4, "Active TTD by owning team and severity");
            Header("Owning team", "Severity", "Incident count with duration", "Minimum", "Average", "Maximum");
            foreach (var team in document.ActiveTeamStatistics)
            {
                var stats = team.Statistics.Detect;
                LinkedRow(0, navigation?.PathLink(team.Team),
                    TeamLabel(team), Severity(team.Statistics.Severity), ReportFormatting.Number(stats.Count),
                    ReportFormatting.Hours(stats.Minimum), ReportFormatting.Hours(stats.Average), ReportFormatting.Hours(stats.Maximum));
            }
            output.Append("\n");
        }

        private static string Severity(int? severity) =>
            severity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";

        private string SourceEvidence(ImmutableArray<string> keys) =>
            ReportFormatting.Join("; ", keys.Select(key => $"{Request.Sources.First(source => source.Key == key).Label} [source key: {key}]"), token);

        private void Memberships(ImmutableArray<string> keys)
        {
            if (navigation is null || Request.Analysis != "incidentManagement")
            {
                Field("Source memberships", SourceEvidence(keys));
                return;
            }
            output.Append("- **Source memberships:** ");
            WriteSources(keys);
            output.Append("\n");
        }

        private void WriteSources(IEnumerable<string> keys, bool table = false, bool evidence = true)
        {
            var first = true;
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                if (!first) output.Append("; ");
                first = false;
                var label = Request.Sources.First(source => source.Key == key).Label;
                if (label.Length != 0) LinkedText(label, navigation?.SourceLink(key), table);
                if (evidence) output.Markdown($" [source key: {key}]", table);
            }
        }

        private void Link(string? url, string label, bool linkRecordedUrl = false)
        {
            token.ThrowIfCancellationRequested();
            var destination = LinkDestination(url);
            Field("Recorded URL", url, linkRecordedUrl ? destination : null);
            if (destination is null)
            {
                if (!string.IsNullOrEmpty(url)) Paragraph("Unsafe or unsupported URL: retained as text, not linked.");
                output.Append("\n");
                return;
            }
            if (linkRecordedUrl)
            {
                output.Append("\n");
                return;
            }
            output.Append("\n[");
            output.Markdown(label);
            output.Append("](" + destination + ")\n\n");
        }

        private static string? LinkDestination(string? url)
        {
            var safe = ReportFormatting.SafeUrl(url);
            if (safe is not null)
                safe = safe.Replace("|", "%7C", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal)
                    .Replace(">", "%3E", StringComparison.Ordinal).Replace("`", "%60", StringComparison.Ordinal);
            return safe is null ? null : "<" + safe + ">";
        }

        private void Heading(int level, string text)
        {
            token.ThrowIfCancellationRequested();
            output.Append(new string('#', level) + " ");
            output.Markdown(text, table: true);
            output.Append("\n\n");
        }

        private void Field(string label, string? value, string? href = null)
        {
            token.ThrowIfCancellationRequested();
            output.Append("- **" + label + ":** ");
            LinkedText(value, href);
            output.Append("\n");
        }

        private void Paragraph(string text)
        {
            output.Markdown(text);
            output.Append("\n\n");
        }

        private void CodeBlock(string text, bool inlineTitle = false)
        {
            if (codeBlocks is null) output.CodeBlock(text);
            else
            {
                if (!countOnly) codeBlocks.Add(output.Length, text, inlineTitle);
                output.CodeBlock("");
            }
        }

        private void Header(params string[] headings)
        {
            Row(headings);
            output.Append("|");
            foreach (var _ in headings) output.Append(" --- |");
            output.Append("\n");
        }

        private void Row(params string?[] cells) => LinkedRow(-1, null, cells);

        private void LinkedRow(int column, string? href, params string?[] cells)
        {
            RowCells(column, href, cells);
            output.Append("\n");
        }

        private void RowCells(int column, string? href, params string?[] cells)
        {
            token.ThrowIfCancellationRequested();
            output.Append("|");
            for (var index = 0; index < cells.Length; index++)
            {
                output.Append(" ");
                LinkedText(cells[index], index == column ? href : null, table: true);
                output.Append(" |");
            }
        }

        private void LinkedText(string? text, string? href, bool table = false)
        {
            if (href is not null) output.Append("[");
            output.Markdown(text, table || href is not null);
            if (href is not null) output.Append("](" + href + ")");
        }

        private void NavigationFallbacks()
        {
            foreach (var member in navigation!.UnlinkedMembers())
            {
                var sectionStart = output.Length;
                navigation.MemberSection(output.Length, actors: [member]);
                Heading(3, "\U0001F464 " + ReportFormatting.Identity(member));
                Paragraph("No collected detail section is attributed to this roster member. Skipped or unavailable source data is not evidence of zero activity.");
                navigation.PersonSection(sectionStart, output.Length);
            }
            foreach (var source in navigation.UnlinkedSources())
            {
                navigation.SourceSection(output.Length, [source.Key]);
                Heading(3, "\U0001F4E6 ICM path: " + source.Label);
                Paragraph("No collected incident details for this ICM path. Skipped or unavailable source data is not evidence of zero activity.");
            }
        }
    }
}
