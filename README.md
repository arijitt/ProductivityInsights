# Productivity Insights

A comprehensive Blazor-based web application for analyzing team productivity metrics from Azure DevOps and ICM (Incident Management). This tool provides detailed insights into work item completion, git commit activity, and incident resolution patterns.

![Light Mode](LightMode.png)
![Dark Mode](DarkMode.png)

## Features

### 📊 Work Items Analysis
- Track completed work items by team and assignee
- View work item completion statistics and trends
- Analyze time spent in active work states (Committed, Active, In Progress)
- Calculate time-to-complete metrics
- Detailed work item history with state transitions

### 🔄 Git Commits Analysis
- Track code contributions by author/committer
- View commit statistics with line-level changes (+additions/-deletions)
- Analyze per-file changes with detailed diff information
- Support for multiple repositories and branches
- Distinguish between added, edited, and deleted files

### 🚨 Incident Resolutions Analysis
- Query incident data from Azure Data Explorer
- Monitor incident resolution and mitigation metrics
- Track time-to-detect (TTD), time-to-mitigate (TTM), and time-to-resolve (TTR)
- Analyze incidents by severity and status
- View active and created incidents by owner
- Support Details, Trends, and Teams manager filtering
- View detailed active, mitigated, and resolved incident information

### Report Export
- **Export Reports** opens Save As and saves one ZIP for the displayed completed analysis to the folder you choose.
- `<stem>_Detailed.html` replaces the Markdown file, retaining every contributor and detailed record, full work item state histories, every Git changed file, incident severity/time statistics, and supporting evidence. The same Markdown layout is rendered into readable headings, lists, tables and code blocks using Markdig, without re-collecting or changing the data.
- `<stem>.html` retains the existing dark, offline summary. Its filename, layout and visible content are unchanged; only its detail-link target points to the new `_Detailed.html` file. Both files are self-contained and require no scripts, screenshots or external assets. The button is labeled **Export Reports**; the summary link retains its legacy Markdown wording.

Detailed HTML uses the on-screen Montserrat typography and blue-accent, icon-led section styling in dark mode, with a light palette for printing. The variable font and its SIL Open Font License are embedded in the report, so no font download is needed when reading it offline. Generated console rulers become semantic headings and separators; recorded message text is left intact. Tables have tabular numerals and keyboard-accessible horizontal scrolling on narrow screens. Team Roster links jump to each member's first matching details (including shared author/committer sections and PR-only evidence). ICM paths link to their first source-qualified detail group. Both work offline and highlight/focus their destination; empty selections and members without collected details receive explicit destinations rather than broken links or invented activity. Identity/source keys, not display-name guesses, keep equal names and overlapping paths correctly linked. This detailed-only presentation/navigation layer does not change recorded content, calculations, filenames, the summary HTML or the application UI.
- Details and Teams are supported for all three analyses. Git Trends includes all period data; the Work Item/Incident placeholder trend views cannot be exported.
- Existing analysis APIs, source selections, on-screen report formatting, icons, and Git's first-ten-files preview are unchanged. Export uses separate collectors and renderers.
- Renamed Git files use `originalPath` or legacy `sourceServerItem` when supplied. Split `delete, sourceRename` and destination entries are matched by their original blob identifiers across all pages. Both paths are retained, with line changes counted once at the destination; unresolved or ambiguous mappings block export instead of guessing.

## Technology Stack

- **Framework**: .NET 8.0
- **UI**: Blazor Server with Interactive Rendering
- **Styling**: Bootstrap 5
- **Authentication**: Azure Identity with Interactive Browser Credential
- **APIs**: 
  - Azure DevOps REST API (v7.0)
  - Azure Data Explorer / Kusto

## Prerequisites

- .NET 8.0 SDK or later
- Azure DevOps account with appropriate permissions
- Access to the configured Azure Data Explorer incident warehouse
- Valid Azure AD credentials

## Installation

1. Clone the repository:
```bash
git clone <repository-url>
cd ProductivityInsights
```

2. Restore dependencies:
```bash
dotnet restore
```

3. Update configuration in `appsettings.json` if needed

4. Build the project:
```bash
dotnet build
```

## Running the Application

### Development Mode
```bash
dotnet run
```

The application will start on `https://localhost:<port>` (check console output for the exact URL).

### Production Mode
```bash
dotnet run --configuration Release
```

## Configuration

### Azure DevOps Settings
The application connects to Azure DevOps organizations and projects. Configure available options in `AnalysisOptions.razor`:

- **Organizations**: msdata, powerbi
- **Projects**: A365, HDInsight, MWC
- **Teams**: Cosmos on Azure Job Service, FastSpark, JS Reliability, etc.

### Incident Resolutions Settings
For incident analysis, configure:
- **Kusto Cluster**: Azure Data Explorer cluster URI in `appsettings.json`
- **Kusto Database**: Incident warehouse database in `appsettings.json`
- **Owning Teams**: Team paths in ICM

### Incident Report Coverage

**Teams + Incident Resolutions does not require `IncidentReportCoverage`.** It exports the currently available incident and in-period action records without extra schema/retention management queries or source-owner attestation. Missing, malformed, expired, or mismatched coverage configuration does not block Teams export. Both formats disclose that historical completeness is not independently verified; zero means no matching available records, not proof that the warehouse retains every historical action.

**Incident Details retains the stricter coverage prerequisite.** The export-only `IncidentReportCoverage` section is intentionally unconfigured. Add it only after obtaining an approved source contract for the configured cluster, database, silo (currently `"1"`), and owning teams. This unconfigured template does **not** enable Incident Details export:

```json
{
  "IncidentReportCoverage": {
    "ClusterUri": null,
    "Database": null,
    "SiloId": null,
    "Teams": [],
    "HistoryFrom": null,
    "HistoryThrough": null,
    "LatestIncidentsThrough": null,
    "PurgesThrough": null,
    "Evidence": null,
    "RetentionSemantics": null,
    "MinimumRetention": {
      "Incidents": null,
      "IncidentHistory": null,
      "PurgedIncidents": null,
      "PurgedIncidentHistory": null
    }
  }
}
```

`HistoryFrom`/`HistoryThrough` must establish the retention-safe `IncidentHistory.ChangeDate` window. `LatestIncidentsThrough` and `PurgesThrough` are applicability limits for the retained views and must cover the report's as-of instant. `Evidence` and `RetentionSemantics` must identify the source-owner contract, retention clock/policy history, and incident/history purge semantics. Do not substitute the current time, a maximum timestamp, earliest observed row, or assumed rolling window for missing evidence.

Use explicit-offset timestamps and positive .NET constant-format `TimeSpan` values for the four minimum retentions. The provider compares the source/silo/teams, bounds, applicability, schemas, and live retention policies with that contract. Configuration validation checks consistency; it does not authenticate or independently prove the truth of the attestation. Coverage concerns retained, published warehouse records, not a guarantee that every upstream action has already ingested.

ICM transports are resolved lazily for Incident exports; coverage configuration is read only when attestation is requested by Incident Details. Missing, expired, or inconsistent evidence blocks Details export without affecting Teams export, application startup, or ordinary analysis. Teams still requires successful data queries, including incident history and purge data: query/permission errors, partial responses, malformed required data, cancellation, and resource limits are not converted into zero activity.

The export-only history query broadcasts the small team-scoped incident key set into the date-filtered history join, rather than building a join hash table from warehouse-wide period history. Ordinary online analysis does not execute this history query. `E_RUNAWAY_QUERY` identifies a server-side operator memory limit, not ZIP size or missing coverage configuration. Export identifies the failing incident/history stage and reports known resource-limit codes without exposing raw server text. Keep incomplete-result safeguards enabled; optimize the query or reduce scope/date range if a genuine resource limit remains.

History IDs are qualified by silo and incident, not assumed globally unique. Teams export preserves in-scope actions whose warehouse `HistoryId` is null: the detailed action table shows **Not provided**, and both files disclose the missing-ID policy. No history ID is invented. Identical unkeyed records are deduplicated using the recorded silo/incident, full actor identity, timestamp and action type; differences in any of those fields remain separate evidence. Contributed still counts distinct incident/member pairs, not action rows. Known-ID conflicts, missing columns, malformed non-null IDs, required incident/actor/time/type data, and the existing purge exclusions remain checked. Missing IDs do not establish independently verified history-purge completeness. Incident Details retains its stricter identity and coverage requirements.

### Authentication
The application uses Azure Interactive Browser Credential for authentication. On first run:
1. A browser window will open for Azure AD sign-in
2. Sign in with your Microsoft credentials
3. Grant necessary permissions for Azure DevOps and Azure Data Explorer access

## Usage

### Analyzing Work Items
1. Navigate to the **Work Items** tab
2. Select organization, project, and team
3. Choose the area path
4. Set start and end date range
5. Click **Run Analysis**
6. View summary statistics and expand rows for detailed information

### Analyzing Git Commits
1. Navigate to the **Git Commits** tab
2. Select organization, project, and repository
3. Choose target branch (main/master)
4. Set date range
5. Click **Run Analysis**
6. View commit statistics with line changes per contributor
7. Expand rows to see individual commits with file-level details

### Analyzing Incident Resolutions
1. Navigate to the **Incident Resolutions** tab
2. Select an owning team
3. Set the date range
4. Click **Run Analysis**
5. View active, created, mitigated, and resolved incident metrics, including TTD, TTM, and TTR
6. Expand rows for detailed incident information

### Exporting Reports
1. Complete a supported analysis, then choose **Export Reports**.
2. In the native **Save As** dialog, choose a folder and ZIP filename, then select **Save**.
3. Keep the analysis open while the export-only collectors retrieve report records and save the ZIP to that destination.
4. Extract the ZIP from your chosen folder and open `<stem>.html` for the existing summary or `<stem>_Detailed.html` for the complete detailed report. No `.md` entry is included.

Destination selection requires a browser supporting `showSaveFilePicker`, such as desktop Microsoft Edge or Google Chrome, on HTTPS or localhost. Unsupported browsers or insecure remote HTTP show an explicit export error; there is no silent fallback to Downloads. The dialog opens directly from the click, before collection, and suggests the existing run-specific ZIP name. Changing that name does not change the matching detailed/summary HTML entry names. Cancelling the dialog does not collect report data.

The export module URL includes a SHA-256 fingerprint of the script supplied by the host's web-root file provider. This avoids reusing an older browser-cached module after an application update or circuit reconnect, including updates to JavaScript alone. If an already-open page reports that `bindExportButton` is undefined after upgrading from the unversioned loader, restart the updated app and reload that page once.

The browser commits the selected file only after a complete transfer and successful write; an earlier failure or cancellation aborts the write rather than replacing an existing file. Saving over an existing file follows the browser's overwrite confirmation. Selecting a new filename may leave an empty browser-created placeholder if export later fails; the application never deletes user files. Changing analyses or leaving the page invalidates the pending destination, although an already-open native dialog must still be dismissed manually. Cancellation cannot undo a file whose final commit has already started.

Git report downloads follow the reference Team Code Commit presentation. Detailed HTML renders the same icon-rich contributor/repository groups, chronological commit details, full changed-file lists, boxed separators, and signed summary tables as the former Markdown. Summary HTML opens with the unchanged compact generated timestamp and dark repository/overall tables, using the same icons and signed line counts. Zero-activity members appear once rather than once per repository; repository names and member labels stay qualified when needed to avoid ambiguity. Captured inputs, coverage, and supporting evidence remain available in the detailed appendix and the summary's expandable context section. Skipped-source warnings remain visible. Console progress logs, reporting-hierarchy levels, and parent SHAs are not fabricated when absent from the snapshot; no API or online-renderer changes are involved.

For **Teams + Git Commits**, the detailed HTML's TEAM ROSTER preserves the actual reporting hierarchy and sibling order captured from the loaded online tree, including collapsed branches. Member hyperlinks still target their existing details, including shared author/committer sections and PR-only evidence. Later form edits or cache restores do not change the captured relationships; no additional directory queries are made. Older snapshots without hierarchy remain flat and disclose that the analysis must be rerun to capture it. All other report formatting and the summary HTML remain unchanged.

Both Teams + Git Commits HTML exports distinguish authors with 👤 and committers with ✍️, matching the on-screen display. Summary tables show icons and identity-qualified names without the `Author:` and `Committer:` labels; detailed HTML retains those labels. The detailed commit fields and committer headings use the corresponding icons. Icons do not change grouping, ordering, counts, or member navigation.

In the Teams + Git Commits detailed HTML, each commit's **Recorded URL** value is hyperlinked; the separate **URL** link is omitted. Missing or unsafe values retain the existing text/warning behavior. PR evidence links, summary HTML, and other views remain unchanged.

The same detailed report omits **Source-qualified key**, **Source memberships**, **Author identity**, and **Committer identity** only from each commit's footer. The author/committer entries at the top, remaining commit fields, captured input context, PR evidence, and source-qualified calculations are retained.

Its commit footers use 🔑 **Commit ID**, 🏢 **Organization**, 🗂️ **Repository ID**, 🗓️ **Author date**, and 📅 **Committer date**. The icons are detailed-Teams-Git-only decorations; recorded values and timestamp precision are unchanged.

Git summary HTML also includes **Files Added**, **Files Modified**, and **Files Deleted** for each repository/contributor row, overall contributor row, and trend period/total. These count file-change occurrences per commit, not unique paths or lines; empty and binary files are included even with zero line changes. Renames count once as modified at the destination, without separately counting a split-rename source entry as deleted. Other changes to existing entries, including encoding, properties and submodules, count as modified. Overall/trend totals deduplicate repository-qualified commits across overlapping source selections. The former Markdown's detailed content and on-screen report formats are unchanged.

**Teams + Work Items** exports follow the reference Team Work Item layout. Detailed HTML renders the captured roster, boxed person/team completion sections, chronological items with dated state transitions and their recorded actors, and icon-rich area/overall tables from the shared report snapshot. Summary HTML retains its compact generated timestamp and dark, automatically sized tables; its captured context is expandable below them. Area rows use each item's actual recorded area path, rather than the selected parent query path. Compact member, area and team labels stay qualified when ambiguous, and each zero-activity member has one placeholder row. Overlapping source selections retain their memberships without duplicating distinct items. Complete fields, identities, state histories, active-work statistics and source context remain in the detailed appendix. Console progress logs, server output paths and uncaptured hierarchy levels are not fabricated. Collection APIs, metric definitions and on-screen reports are unchanged.

The Teams + Work Items detailed HTML also shows the actual captured reporting hierarchy in **TEAM ROSTER**, retaining member hyperlinks and the online sibling order, including collapsed branches. Older flat snapshots require a new analysis to capture those relationships. Each member's completion section includes icon-led work-item entries matching the on-screen fields: title, state, priority, creation/closure or last-change dates, completion time, area/iteration, active-work time, state-change actors and durations, and a work-item link. Assignees retain identity qualification. Current-state durations end at the captured report as-of time, not the reader's current time; missing history remains unknown, not zero. These additions reuse collected records without extra API calls and retain the complete audit appendix, summary HTML and legacy Markdown layout.

In both the member details and audit appendix, each work item's **Recorded URL** value is hyperlinked instead of displaying a separate **View Work Item** link. Missing or unsafe URLs retain their existing text/warning behavior. Summary HTML and non-Teams views are unchanged.

**State Change History** actors in both locations show names and email addresses without Principal or ID annotations. Full recorded identities remain available outside history; other views and legacy Markdown retain their existing identity format.

Each person's detailed section starts with a work-item type breakdown and **Time Analysis (Committed/Active/In Progress states only)**: total, maximum, minimum, average and median days, plus **Work Items with Active Work Time: N of M**. These reuse deduplicated work items and the existing positive active-work durations; zero and unavailable durations do not enter the statistics, and missing/single-revision histories are disclosed separately. Zero-activity members receive explicit empty summaries. Summary HTML, the audit appendix and online calculations are unchanged.

**Teams + Incident Resolutions** exports follow the incident references: detailed HTML renders the same boxed ICM-path/category sections, compact `Incident Details` fields, and icon-rich source/overall tables as the former Markdown. Its Team Roster preserves the actual manager/report hierarchy and sibling order captured from the online tree, including collapsed branches, with each member still linked to their details. Parent references are captured with the immutable roster, not inferred from names, indentation or current form values. Older snapshots without hierarchy retain a flat list and disclose that the analysis must be rerun to capture it. Only the TEAM ROSTER section changes; other report sections and captured input context retain their existing format. Summary HTML retains its compact dark layout, generated timestamp and expandable context, with a meaningful title rather than the reference's blank title. Members and their active source rows in summary tables are ordered by total metric activity, with one placeholder for each zero-activity member. The overall `TOTAL` sums contributor credits after source deduplication; Contributed totals are actor/incident pairs, not unique incidents across different actors. Category memberships may overlap without changing overall counts. Contributors come from the existing recorded in-period action evidence, not an added comments API. Full incident metadata, mitigation text, duration statistics and action histories remain in the detailed appendix. Authentication/API progress logs and uncaptured hierarchy levels are not fabricated. Teams uses available warehouse data without the additional coverage prerequisite; data-query APIs, metric definitions and on-screen reports are unchanged.

In the Teams + Incident Resolutions detailed HTML, **Recorded URL** values link directly to their incidents; the separate **View Incident** link is omitted. Missing or unsafe URLs remain text with the existing safety handling. Other report views and the summary HTML are unchanged.

Each member's detailed section also includes **Created ICMs** and **Contributed ICMs**, with counts matching the existing summary. Created uses the current owner of incidents created in the selected period, not the original creator. Contributed lists each silo-qualified incident once per member and includes that member's recorded in-period actions, timestamps, action IDs (or **Not provided**) and source memberships. Both sections retain full incident fields, metadata, mitigation text and linked URLs, including for members without closed incidents; zero-activity members get explicit empty sections. Existing active/closed details, statistics and the complete action-evidence appendix remain intact. An incident appearing in multiple sections does not add to totals. These detailed-only additions make no new API calls and retain the existing cancellation and output-size limits.

Export uses the inputs and manager roster captured for the executed run, not subsequently edited dropdowns. Both files use one immutable, source-qualified report snapshot with its run ID, effective query bounds, time zone, and as-of timestamp. Source data is collected for that saved selection at export time; repeat exports reuse a fully collected snapshot. Exports with skipped sources are not cached, so another export retries unavailable sources without changing the captured selection.

Filter metadata preserves the existing APIs' semantics: Work Items uses inclusive, date-only `StateChangeDate` filters; Git and ICM retain UTC-labeled wall-clock inputs without converting the host offset. Missing ICM start dates use the fixed UnixEpoch default. Other missing-date defaults are frozen at confirmation and explicitly identified; the unchanged analysis APIs evaluate time-dependent defaults independently.

**Metric definitions:** Git additions/deletions follow each diff block's change type; unchanged blocks never contribute, even when ADO reports different original/modified range lengths. PRs Merged counts distinct completed pull requests by the completion identity (`closedBy`) and completion time (`closedDate`), not the author or merge-commit count. Incident Contributed counts distinct incidents with recorded, in-period actions by each member. Incident Created retains the existing owning-contact attribution. Active is an as-of state, distinct from activity within the selected period. Teams summaries include the captured roster, including members with no matching activity in the collected data.

Git export follows the online flow's best-effort source availability behavior: a missing repository or branch, denied read, failed HTTP request, or transport failure skips that Git source and continues with other selections. Both downloaded formats list every skipped source and its error; original selections and roster remain visible. Source results are published atomically, so a late read failure cannot leave partially counted commits or PRs. Totals and zero-activity rows cover successfully collected sources only; an all-unavailable selection produces an explicitly unavailable report, not zero activity. Cancellation, resource limits, malformed required data, incomplete query responses, and unverifiable Incident Details history coverage remain errors rather than guessed data. Teams Incident export does not require independent historical coverage attestation. The existing analysis and contributor details remain usable. Changing the displayed analysis or leaving the page cancels the pending download.

**Incident Details export prerequisite:** A source-owner-supported earliest retained action time (or retention-clock mapping/policy history) and history-purge semantics are required for Details. A 740-day ingestion-retention policy alone does not prove coverage of an arbitrary `IncidentHistory.ChangeDate` window. Details export fails explicitly until valid coverage evidence is configured; Teams Incident export, ordinary incident analysis and Work Item/Git exports do not depend on that evidence. Synthetic incident fixtures verify the implementation, not production coverage.

Downloaded HTML now supports **256 MiB UTF-8 per file (512 MiB uncompressed for the pair)**. Writers stream directly into ZIP entries rather than allocating complete HTML strings. Detailed report preparation keeps recorded commit messages and mitigation texts separate from structural Markdown, parsing each distinct text once while retaining every full occurrence in the final report. This prevents repeated Active/Created/Contributed sections from multiplying those text allocations. Structural Markdown has a separate 64 MiB bound; buffered string-returning writer helpers retain their 32 MiB safety limit and are not used to build downloads.

The ZIP is built in an exclusively opened, randomly named OS temporary file and becomes available to the browser only after both entries are complete. It is deleted when its stream closes after download, cancellation or failure; it is never served from `wwwroot` or kept as a persistent server report. Preparation still bounds input records (including nested history/files), summary rows, identities, and individual text values. ADO collection rejects responses over 16 MiB and cumulative response data over 128 MiB per collection, before parsing unbounded data. Edited/renamed Git files use the supported Git `fileDiffs` API's first-parent line counts, not the UI endpoint's full source/diff text; resolved rename paths and exact additions/deletions are preserved. These collection limits are separate from rendered-file and ZIP sizes and are unchanged. Requests exceeding any remaining bound fail explicitly, never by truncating records.

## Project Structure

```
ProductivityInsights/
├── Components/
│   ├── Layout/           # Layout components (MainLayout, NavMenu)
│   └── Pages/            # Razor pages (AnalysisOptions, Home, etc.)
├── Metrics/              # Business logic for metrics calculation
│   ├── GitCommitMetrics.cs
│   ├── IncidentManagementMetrics.cs
│   └── WorkItemCompletionMetrics.cs
├── Models/               # Data models and DTOs
│   ├── CommitCollection.cs
│   ├── IncidentCollection.cs
│   ├── WorkItemDetailsCollection.cs
│   └── ...
├── Options/              # Query options classes
│   ├── CommitQueryOptions.cs
│   ├── IncidentManagementKustoOptions.cs
│   └── WorkItemQueryOptions.cs
├── Services/             # Application services
│   └── ThemeService.cs
├── Utilities/            # Helper utilities
│   ├── FormatUtilities.cs
│   ├── PrintUtilities.cs
│   └── UserToken.cs
├── wwwroot/              # Static files (CSS, JS)
├── Program.cs            # Application entry point
└── appsettings.json      # Configuration
```

## Key Features Implementation

### Caching
Analysis results are cached per tab to improve performance when switching between tabs. Results are refreshed when running a new analysis.
Export context and validated snapshots are maintained separately; export-only collection never replaces the existing analysis cache or detail-loading APIs.

### Interactive UI
- Expandable rows for detailed information
- Loading indicators during API calls
- Modal confirmations for analysis parameters
- Responsive design with Bootstrap

### Error Handling
- Graceful handling of API failures
- Warning messages for missing data
- Debug logging in development mode

## API Integration

### Azure DevOps REST API
The application uses the following endpoints:
- Commits: `/git/repositories/{repo}/commits`
- Commit Details: `/git/repositories/{repo}/commits/{id}`
- Commit Changes: `/git/repositories/{repo}/commits/{id}/changes`
- Work Items: `/wit/wiql` and `/wit/workitems`
- File Diffs: `/_versioncontrol/fileDiff`

### Azure Data Explorer
- Queries active, created, mitigated, and resolved incidents from `IcmDataWarehouse`
- Uses `DefaultAzureCredential` and configured Kusto cluster/database settings

## Performance Considerations

- **Pagination**: Handles large datasets with continuation tokens
- **Rate Limiting**: Includes delays between API calls to avoid throttling
- **Async Operations**: All API calls are asynchronous
- **Parallel Processing**: Results are loaded in background tasks

## Troubleshooting

### Authentication Issues
- Ensure you have appropriate permissions in Azure DevOps
- Check Azure AD credentials and consent
- Verify Azure Data Explorer access for Incident Resolutions analysis

### API Errors
- Check network connectivity
- Verify organization/project names are correct
- Ensure date ranges are valid
- Review console output for detailed error messages

### Performance Issues
- Reduce date range for faster results
- Check for rate limiting errors in console
- Consider analyzing smaller teams/repositories

## Development

Run focused report and compatibility fixtures separately from the web project:

```powershell
dotnet run --project Tests\ProductivityInsights.ReportTests.csproj
```

The compatibility fixtures compare existing API/DTO/auth files, analysis paths, renderers, and report CSS against the restored `1379858` baseline. They permit only the export control, export-context hooks, and removal of PDF-only code. Rebaseline these checks only for an explicitly approved change to existing analysis behavior.

Browser fixtures use synthetic data over a real Blazor circuit, not live external services. They exercise ZIP streaming, full content, unchanged displayed reports, failures, cancellation, and a 5,000-commit export. Git, Teams Work Items and Teams Incident fixtures also open the exported HTML and verify its rendered reference layout, columns, counts and collapsed context. `incident-available-export` and `incident-unkeyed-export` use production export registrations and the real Incident collector with a synthetic data-query client, absent coverage configuration, and denied metadata access. The unkeyed scenario requires an in-scope null-`HistoryId` action to contribute and appear as **Not provided** in the downloaded history. With Chrome or Edge installed:

```powershell
dotnet run --project Tests\ProductivityInsights.ReportTests.csproj -- --web --fixture gitCommits --urls http://127.0.0.1:5187
node Tests\reportBrowserTests.mjs http://127.0.0.1:5187/ gitCommits
```

Other fixture names are `workItems`, `incidentManagement`, `incident-available-export`, `incident-unkeyed-export`, `large`, `failed-export`, `slow-export`, `skipped-export`, `picker-stale`, and `stale-module`. The skipped-source scenario verifies that cancelled/unsupported pickers do not collect data, then checks a successful warning-only save, recovery retry, and complete-snapshot caching. The Git scenario also exercises disk-write failure and retry; `picker-stale` invalidates a destination while its dialog is pending. `stale-module` preloads a cached legacy export module without `bindExportButton` and verifies that the content-fingerprinted import still saves the ZIP. Set `REPORT_TEST_BROWSER` if the browser executable is outside its standard Windows installation directory.
Run all browser scenarios with `.\Tests\Run-ReportBrowserTests.ps1`; the script starts and stops only its own fixture hosts. Headless tests use trusted browser clicks and a picker/writer shim, not an automated native OS dialog. The shim preserves the real Blazor stream and captures ZIP bytes in the isolated test profile only on a simulated commit. Run `node Tests\reportDownloadTests.mjs` for focused picker, transactional-write, cancellation, error, and lifecycle checks.

For a bounded, read-only live ADO check, the fixture runner also accepts `--live --analysis gitCommits --organization <org> --project <project> --scope <repository> --branch <branch> --latest`. This checks a two-minute window around the latest commit and builds the ZIP in memory, printing counts and any skipped-source errors. Replace `--latest` with `--commit <40-character-sha>` to reproduce a specific commit and require its presence in the collected report. Add `--additional-scope <repository> --additional-branch <branch>` to check a mixed available/unavailable selection in the same organization/project. Alternatively supply explicit `--start`/`--end` UTC timestamps spanning at most seven days; use `--analysis workItems` with an area path as `--scope` for Work Items. Live probes use existing credentials and do not persist source records or tokens.

For a bounded live Teams Incident ZIP check, use `dotnet run --project Tests\ProductivityInsights.ReportTests.csproj -- --live-incident --cluster <HTTPS-cluster> --database <database> --scope <owning-team> --member <recorded-alias-or-email> --start <UTC-start> --end <UTC-end>`. This uses production registrations, credentials, transport, collector and writers without coverage configuration. It permits one owning team, a single-member probe roster, and at most 31 days; it prints only counts and ZIP size, and never saves source records or the ZIP.

Replace `--member <recorded-alias-or-email>` with `--member-from-unkeyed-action` to reproduce the missing-ID case. The probe selects one recorded actor from an in-period null-`HistoryId` action without printing that identity, then requires the real export to retain unkeyed evidence and disclose unavailable IDs in both ZIP entries. It fails rather than claiming success if no affected action is found.

### Debug Mode
Debug mode provides additional logging:
```csharp
#if DEBUG
    Console.WriteLine("Debug information...");
#endif
```

### Adding New Metrics
1. Create a new metrics class in `Metrics/`
2. Add corresponding models in `Models/`
3. Update `AnalysisOptions.razor` with new UI tab
4. Implement rendering methods for results display

## Contributing

When contributing to this project:
1. Follow existing code structure and naming conventions
2. Include XML documentation for public methods
3. Test with multiple organizations and date ranges
4. Update README for new features

## License

[Specify your license here]

## Support

For issues or questions:
- Check console output for error details
- Review Azure DevOps API documentation
- Verify authentication and permissions

## Version History

- **Current**: Initial release with work items, git commits, and ICM analysis

---

**Note**: This application requires appropriate permissions and credentials for Azure DevOps and ICM access. Ensure you have the necessary access before running analyses.
