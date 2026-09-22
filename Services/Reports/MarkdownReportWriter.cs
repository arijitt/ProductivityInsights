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

        public ReportOutput Render()
        {
            Heading(1, document.Title);
            if (Request.Analysis == "gitCommits")
            {
                var presentation = new GitReportPresentation(document, token);
                GitDetails(presentation);
                GitSummary(document.SourceHeading, presentation.SourceRows, true, presentation);
                GitSummary(document.OverallHeading, presentation.OverallRows, false, presentation);
                Context();
                GitEvidence();
            }
            else if (Request.Analysis == "workItems" && Request.IsTeams)
            {
                var presentation = new WorkItemReportPresentation(document, token);
                TeamRoster();
                TeamWorkItemDetails(presentation);
                TeamWorkItemSummary(presentation, true);
                TeamWorkItemSummary(presentation, false);
                Context();
                WorkItems();
            }
            else if (Request.Analysis == "incidentManagement" && Request.IsTeams)
            {
                var presentation = new IncidentReportPresentation(document, token);
                TeamRoster();
                TeamIncidentDetails(presentation);
                TeamIncidentSummary(presentation, true);
                TeamIncidentSummary(presentation, false);
                Context();
                Incidents(presentation);
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
            token.ThrowIfCancellationRequested();
            return output;
        }

        private void Context()
        {
            Heading(2, "Captured inputs and export query context");
            Field("Analysis", Request.Analysis);
            Field("View", Request.View);
            Field("Run ID", Request.RunId.ToString("D"));
            Field("Export capture anchor", ReportFormatting.Date(Request.ExecutedAt));
            Field("Generated / as of", ReportFormatting.Date(document.Snapshot.AsOf));
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
            Heading(3, "Captured manager / report roster");
            if (Request.Roster.IsEmpty) Paragraph("No manager roster was captured for this view.");
            else
            {
                Header("Name", "Email", "Principal name", "Identity ID");
                foreach (var member in Request.Roster)
                    LinkedRow(0, navigation?.MemberLink(member), member.Name, member.Email, member.PrincipalName, member.Id);
                output.Append("\n");
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
                Paragraph("Area summaries group distinct work items by their recorded area path rather than selected query roots. Overlapping source memberships are preserved without duplicating items. The compact completion list is followed by the complete field, identity, history and duration evidence below.");
            if (Request.Analysis == "incidentManagement" && Request.IsTeams)
                Paragraph("TOTAL sums the existing overall contributor credits, not overlapping source rows. Contributed totals count actor/incident pairs, not distinct incidents across different actors. The recorded in-period action definition is unchanged; data availability and coverage are described above.");
            output.Append("\n");
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
                    navigation?.ItemSection(itemStart, output.Length);
                }
                navigation?.PersonSection(sectionStart, output.Length);
            }
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
            foreach (var member in presentation.OverallRows.Where(row => row.Commits != 0)
                .OrderByDescending(row => row.Commits).ThenByDescending(row => row.Added)
                .ThenBy(presentation.Contributor, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                var role = member.Contributor.StartsWith("Author:", StringComparison.Ordinal) ? "Contributor" : "Committer";
                var roleIcon = navigation is not null && Request.IsTeams && role == "Committer"
                    ? GitReportPresentation.CommitterIcon : GitReportPresentation.AuthorIcon;
                navigation?.MemberSection(output.Length, member.ContributorKey,
                    groups[member.ContributorKey].SelectMany(commit => new[] { commit.Entity.Record.Author, commit.Entity.Record.Committer }));
                var contributor = navigation is null ? presentation.Contributor(member) : presentation.ContributorForHtml(member);
                BoxHeading($"{roleIcon} {role}: {contributor} - {member.Commits} commits (+{member.Added}/-{member.Deleted} lines)");
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
                        var itemStart = ItemHeading($"\U0001F504 Commit: {record.Id[..Math.Min(8, record.Id.Length)]}...");
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
                        navigation?.ItemSection(itemStart, output.Length);
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
                var supportsHierarchy = navigation is not null && Request.Analysis is "incidentManagement" or "gitCommits" or "workItems";
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
                var contributor = member.WorkItems == 0 ? member.Contributor : presentation.Contributor(member);
                BoxHeading($"\U0001F464 {contributor}: {ReportFormatting.Number(member.WorkItems)} work item(s) completed");
                if (navigation is not null)
                    TeamWorkItemPersonSummary(presentation.Contributor(member), presentation.Items(member).ToImmutableArray());
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
            Line("Total Work Items Completed: " + ReportFormatting.Number(items.Length));
            foreach (var group in items.GroupBy(item => item.Entity.Record.Type)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
                Line(group.Key + ": " + ReportFormatting.Number(group.Count()));
            Line("\u23F1\uFE0F Time Analysis (Committed/Active/In Progress states only):");
            var stats = ReportDocumentBuilder.DurationStatistics(
                items.Where(item => item.ActiveWorkDays > 0).Select(item => item.ActiveWorkDays!.Value), token);
            if (stats.Count > 0)
            {
                Line("Total Days: " + ReportFormatting.Decimal(stats.Total));
                Line("Maximum Days: " + ReportFormatting.Decimal(stats.Maximum));
                Line("Minimum Days: " + ReportFormatting.Decimal(stats.Minimum));
                Line("Average Days: " + ReportFormatting.Decimal(stats.Average));
                Line("Median Days: " + ReportFormatting.Decimal(stats.Median));
            }
            else
                Line("No recorded positive active-work duration is available.");
            Line($"Work Items with Active Work Time: {ReportFormatting.Number(stats.Count)} of {ReportFormatting.Number(items.Length)}");
            var unavailable = items.Count(item => item.ActiveWorkDays is null);
            if (unavailable > 0)
                Line($"Work Items with Unavailable Active Work Time: {ReportFormatting.Number(unavailable)} of {ReportFormatting.Number(items.Length)}");
            if (items.IsEmpty) Line("No work items for this contributor.");
            output.Append("\n");
        }

        private void TeamWorkItemRecordDetails(WorkItemDocument item)
        {
            var record = item.Entity.Record;
            var itemStart = ItemHeading($"\U0001F4CB Work Item #{ReportFormatting.Number(record.Id)} - {record.Type}");
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
            Link(record.Url, "\U0001F310 View Work Item", linkRecordedUrl: true);
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

        private void Rule() => output.Append(new string('\u2500', 120) + "\n");

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
                Category("\U0001F7E2 Active", record => record.Active);
                Category("\U0001F195 Created", record => record.Created);
                Category("\U0001F527 Mitigated", record => record.Mitigated);
                Category("\u2705 Resolved", record => record.Resolved);
                Category("Other selected (including action-only)", record => !record.Active && !record.Created && !record.Mitigated && !record.Resolved);

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

        private void CompactIncident(IncidentDocument incident, IncidentReportPresentation presentation)
        {
            var record = incident.Entity.Record;
            Rule();
            Line("Incident Details:");
            Detail("ID", record.Id);
            Detail("Silo", record.SiloId);
            Detail("URL", record.Url);
            Detail("Title", record.Title);
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
            Rule();

            void Detail(string label, string? value) => Line($"  {label}: {ReportFormatting.Label(value)}");
        }

        private void TeamIncidentSummary(IncidentReportPresentation presentation, bool bySource)
        {
            var heading = bySource ? document.SourceHeading : document.OverallHeading;
            BoxHeading($"\U0001F4CA {heading} ({ReportFormatting.CalendarDate(Request.Start)} to {ReportFormatting.CalendarDate(Request.End)}):");
            Header(IncidentReportPresentation.Headers(bySource));
            foreach (var row in bySource ? presentation.SourceRows : presentation.OverallRows)
                ReferenceRow(presentation.Cells(row, bySource), bySource ? navigation?.SourceLink(row.SourceKey) : null);
            if (!bySource && !document.Snapshot.AllSourcesSkipped) ReferenceRow(presentation.TotalCells());
            output.Append("\n");
        }

        private void Incidents(IncidentReportPresentation? presentation = null)
        {
            Heading(2, "Incident details");
            var memberDetails = navigation is not null && Request.IsTeams
                ? (presentation ?? throw new InvalidOperationException("Teams incident detail presentation is required.")).MemberActivityDetailsByKey()
                : null;
            if (memberDetails is not null)
                Paragraph("Created and Contributed sections use the existing per-member credits. Closed-incident statistics are unchanged. Repeated silo-qualified incidents across sections do not increase totals.");
            var rendered = new HashSet<string>(StringComparer.Ordinal);
            var expandedMembers = new HashSet<string>(StringComparer.Ordinal);
            for (var groupIndex = 0; groupIndex < document.IncidentGroups.Length; groupIndex++)
            {
                var group = document.IncidentGroups[groupIndex];
                token.ThrowIfCancellationRequested();
                var sectionStart = output.Length;
                string? memberKey = null;
                if (groupIndex != 0 && !group.Incidents.IsEmpty)
                {
                    var record = group.Incidents[0].Entity.Record;
                    var actor = record.ResolvedAt is not null ? record.ResolvedBy : record.MitigatedBy;
                    navigation?.MemberSection(output.Length, actors: [actor]);
                    memberKey = presentation?.MemberKey(actor);
                }
                Heading(3, group.Contributor);
                Field(memberDetails is not null && groupIndex != 0 ? "Distinct closed incidents in group" : "Distinct incidents in group",
                    ReportFormatting.Number(group.Incidents.Length));
                Field("Currently resolved", ReportFormatting.Number(group.Incidents.Count(item => item.Entity.Record.Status == "Resolved")));
                Field("Currently mitigated", ReportFormatting.Number(group.Incidents.Count(item => item.Entity.Record.Status == "Mitigated")));
                output.Append("\n");
                IncidentStatistics(group.SeverityStatistics);
                if (group.Contributor == "Active" && Request.IsTeams
                    && Request.Sources.Select(source => source.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    ActiveTeamStatistics();
                if (group.Incidents.IsEmpty) Paragraph("0 incidents in this group.");
                foreach (var incident in group.Incidents)
                {
                    var itemStart = output.Length;
                    Incident(incident);
                    if (groupIndex != 0) navigation?.ItemSection(itemStart, output.Length);
                    rendered.Add(incident.Entity.Record.EntityKey);
                }
                if (memberDetails is not null && memberKey is not null
                    && memberDetails.TryGetValue(memberKey, out var details) && expandedMembers.Add(memberKey))
                    MemberIncidentActivity(details);
                if (groupIndex != 0) navigation?.PersonSection(sectionStart, output.Length);
            }
            foreach (var member in document.OverallRows.Where(member =>
                memberDetails is not null ? !expandedMembers.Contains(member.ContributorKey)
                    : !document.IncidentGroups.Any(group => group.Contributor == member.Contributor)))
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
                if (memberDetails is not null) MemberIncidentActivity(memberDetails[member.ContributorKey]);
                navigation?.PersonSection(sectionStart, output.Length);
            }
            var other = document.Incidents.Where(item => !rendered.Contains(item.Entity.Record.EntityKey)).ToImmutableArray();
            if (!other.IsEmpty)
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
            Heading(2, "Recorded incident action history and contribution evidence");
            Paragraph("Several recorded actions on one incident count as one Contributed incident for that actor. Recorded incident, actor, timestamp, action type and source memberships below are the evidence; warehouse action IDs are included when provided. Latest incident modification is not substituted.");
            IncidentActionTable(document.IncidentActions);
        }

        private void MemberIncidentActivity(IncidentReportPresentation.MemberActivityDetails details)
        {
            token.ThrowIfCancellationRequested();
            Heading(4, $"Created ICMs ({ReportFormatting.Number(details.Created.Length)})");
            Paragraph("Created credits the current owner of incidents created in the selected period, not their original creator.");
            if (details.Created.IsEmpty) Paragraph("No Created incidents for this member in the collected snapshot.");
            foreach (var incident in details.Created)
            {
                var itemStart = output.Length;
                Incident(incident, headingLevel: 5);
                navigation?.ItemSection(itemStart, output.Length);
            }
            Heading(4, $"Contributed ICMs ({ReportFormatting.Number(details.Contributed.Length)})");
            Paragraph("Contributed counts distinct incidents with this member's recorded in-period actions. Only this member's actions appear below; repeated actions or source memberships do not add incidents.");
            if (details.Contributed.IsEmpty) Paragraph("No Contributed incidents for this member in the collected snapshot.");
            foreach (var contribution in details.Contributed)
            {
                var itemStart = output.Length;
                Incident(contribution.Incident, headingLevel: 5);
                Heading(6, "Recorded in-period actions by this member");
                IncidentActionTable(contribution.Actions);
                navigation?.ItemSection(itemStart, output.Length);
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
            Field("Owning tenant", record.OwningTenant);
            Field("Owning team", record.OwningTeam, navigation?.PathLink(record.OwningTeam, incident.Entity.SourceKeys));
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

        private void IncidentStatistics(ImmutableArray<IncidentSeverityStatistics> statistics)
        {
            Heading(4, "Severity counts");
            Header("Severity", "Incident count");
            foreach (var severity in statistics)
                Row(Severity(severity.Severity), ReportFormatting.Number(severity.Count));
            output.Append("\n");
            foreach (var metric in new[] { "TTD", "TTM", "TTR" })
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
            Heading(4, "Active severity by owning team");
            Header("Owning team", "Severity", "Incident count");
            foreach (var team in document.ActiveTeamStatistics)
                Row(team.Team, Severity(team.Statistics.Severity), ReportFormatting.Number(team.Statistics.Count));
            output.Append("\n");
            Heading(4, "Active TTD by owning team and severity");
            Header("Owning team", "Severity", "Incident count with duration", "Minimum", "Average", "Maximum");
            foreach (var team in document.ActiveTeamStatistics)
            {
                var stats = team.Statistics.Detect;
                LinkedRow(0, navigation?.PathLink(team.Team),
                    team.Team, Severity(team.Statistics.Severity), ReportFormatting.Number(stats.Count),
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
            var safe = ReportFormatting.SafeUrl(url);
            if (safe is not null)
                safe = safe.Replace("|", "%7C", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal)
                    .Replace(">", "%3E", StringComparison.Ordinal).Replace("`", "%60", StringComparison.Ordinal);
            Field("Recorded URL", url, linkRecordedUrl && safe is not null ? "<" + safe + ">" : null);
            if (safe is null)
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
            output.Append("](<" + safe + ">)\n\n");
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

        private void CodeBlock(string text)
        {
            if (codeBlocks is null) output.CodeBlock(text);
            else
            {
                if (!countOnly) codeBlocks.Add(output.Length, text);
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
