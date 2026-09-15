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
- **Export Reports (.md + .html)** opens Save As and saves one ZIP for the displayed completed analysis to the folder you choose.
- Markdown includes every contributor and detailed record, full work item state histories, every Git changed file, incident severity/time statistics, and evidence for the new summary metrics.
- The standalone HTML file contains dark, offline summary tables and links to its matching Markdown file. It requires no scripts, screenshots, or external assets.
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

History IDs are qualified by silo and incident, not assumed globally unique. Teams export preserves in-scope actions whose warehouse `HistoryId` is null: the Markdown action table shows **Not provided**, and both formats disclose the missing-ID policy. No history ID is invented. Identical unkeyed records are deduplicated using the recorded silo/incident, full actor identity, timestamp and action type; differences in any of those fields remain separate evidence. Contributed still counts distinct incident/member pairs, not action rows. Known-ID conflicts, missing columns, malformed non-null IDs, required incident/actor/time/type data, and the existing purge exclusions remain checked. Missing IDs do not establish independently verified history-purge completeness. Incident Details retains its stricter identity and coverage requirements.

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
1. Complete a supported analysis, then choose **Export Reports (.md + .html)**.
2. In the native **Save As** dialog, choose a folder and ZIP filename, then select **Save**.
3. Keep the analysis open while the export-only collectors retrieve report records and save the ZIP to that destination.
4. Extract the ZIP from your chosen folder and open its HTML summary or Markdown details.

Destination selection requires a browser supporting `showSaveFilePicker`, such as desktop Microsoft Edge or Google Chrome, on HTTPS or localhost. Unsupported browsers or insecure remote HTTP show an explicit export error; there is no silent fallback to Downloads. The dialog opens directly from the click, before collection, and suggests the existing run-specific ZIP name. Changing that name does not change the matching Markdown/HTML entry names. Cancelling the dialog does not collect report data.

The export module URL includes a SHA-256 fingerprint of the script supplied by the host's web-root file provider. This avoids reusing an older browser-cached module after an application update or circuit reconnect, including updates to JavaScript alone. If an already-open page reports that `bindExportButton` is undefined after upgrading from the unversioned loader, restart the updated app and reload that page once.

The browser commits the selected file only after a complete transfer and successful write; an earlier failure or cancellation aborts the write rather than replacing an existing file. Saving over an existing file follows the browser's overwrite confirmation. Selecting a new filename may leave an empty browser-created placeholder if export later fails; the application never deletes user files. Changing analyses or leaving the page invalidates the pending destination, although an already-open native dialog must still be dismissed manually. Cancellation cannot undo a file whose final commit has already started.

Git report downloads follow the reference Team Code Commit report presentation: Markdown has icon-rich contributor/repository groups, chronological commit details, full changed-file lists, boxed separators, and signed summary tables. HTML opens with a compact generated timestamp and the dark repository/overall tables, using the same icons and signed line counts. Zero-activity members appear once rather than once per repository; repository names and member labels stay qualified when needed to avoid ambiguity. Captured inputs, coverage, and supporting evidence remain available in the Markdown appendix and HTML's expandable context section. Skipped-source warnings remain visible. Console progress logs, reporting-hierarchy levels, and parent SHAs are not fabricated when absent from the snapshot; no API or online-renderer changes are involved.

Git HTML reports also include **Files Added**, **Files Modified**, and **Files Deleted** for each repository/contributor row, overall contributor row, and trend period/total. These count file-change occurrences per commit, not unique paths or lines; empty and binary files are included even with zero line changes. Renames count once as modified at the destination, without separately counting a split-rename source entry as deleted. Other changes to existing entries, including encoding, properties and submodules, count as modified. Overall/trend totals deduplicate repository-qualified commits across overlapping source selections. Markdown and on-screen report formats are unchanged.

**Teams + Work Items** exports follow the reference Team Work Item report layout. Markdown starts with the captured roster, boxed person/team completion sections, and chronological items with dated state transitions and their recorded actors, followed by icon-rich area/overall summary tables. HTML starts with a compact generated timestamp and the corresponding dark, automatically sized summary tables; its captured context is expandable below them. Area rows use each item's actual recorded area path, rather than the selected parent query path. Compact member, area and team labels stay qualified when ambiguous, and each zero-activity member has one placeholder row. Overlapping source selections retain their memberships without duplicating distinct items. Complete fields, identities, state histories, active-work statistics and source context remain in the Markdown appendix. Console progress logs, server output paths and uncaptured hierarchy levels are not fabricated. This presentation change does not alter Work Items Details, Git or Incident layouts, collection APIs, metric definitions, or on-screen reports.

**Teams + Incident Resolutions** exports follow the incident references: Markdown has boxed ICM-path/category sections, compact `Incident Details` fields, and icon-rich source/overall tables. HTML uses the same compact dark summary layout, generated timestamp and expandable context; it retains a meaningful title rather than reproducing the reference's blank title. Members and their active source rows are ordered by total metric activity, with one placeholder for each zero-activity member. The overall `TOTAL` sums contributor credits after source deduplication; Contributed totals are actor/incident pairs, not unique incidents across different actors. Category memberships may overlap without changing overall counts. Contributors come from the existing recorded in-period action evidence, not an added comments API. Full incident metadata, mitigation text, duration statistics and action histories remain in the Markdown appendix. Authentication/API progress logs and uncaptured hierarchy levels are not fabricated. Teams uses available warehouse data without the additional coverage prerequisite; Incident Details, data-query APIs, metric definitions and on-screen reports are unchanged.

Export uses the inputs and manager roster captured for the executed run, not subsequently edited dropdowns. Both files use one immutable, source-qualified report snapshot with its run ID, effective query bounds, time zone, and as-of timestamp. Source data is collected for that saved selection at export time; repeat exports reuse a fully collected snapshot. Exports with skipped sources are not cached, so another export retries unavailable sources without changing the captured selection.

Filter metadata preserves the existing APIs' semantics: Work Items uses inclusive, date-only `StateChangeDate` filters; Git and ICM retain UTC-labeled wall-clock inputs without converting the host offset. Missing ICM start dates use the fixed UnixEpoch default. Other missing-date defaults are frozen at confirmation and explicitly identified; the unchanged analysis APIs evaluate time-dependent defaults independently.

**Metric definitions:** Git additions/deletions follow each diff block's change type; unchanged blocks never contribute, even when ADO reports different original/modified range lengths. PRs Merged counts distinct completed pull requests by the completion identity (`closedBy`) and completion time (`closedDate`), not the author or merge-commit count. Incident Contributed counts distinct incidents with recorded, in-period actions by each member. Incident Created retains the existing owning-contact attribution. Active is an as-of state, distinct from activity within the selected period. Teams summaries include the captured roster, including members with no matching activity in the collected data.

Git export follows the online flow's best-effort source availability behavior: a missing repository or branch, denied read, failed HTTP request, or transport failure skips that Git source and continues with other selections. Both downloaded formats list every skipped source and its error; original selections and roster remain visible. Source results are published atomically, so a late read failure cannot leave partially counted commits or PRs. Totals and zero-activity rows cover successfully collected sources only; an all-unavailable selection produces an explicitly unavailable report, not zero activity. Cancellation, resource limits, malformed required data, incomplete query responses, and unverifiable Incident Details history coverage remain errors rather than guessed data. Teams Incident export does not require independent historical coverage attestation. The existing analysis and contributor details remain usable. Changing the displayed analysis or leaving the page cancels the pending download.

**Incident Details export prerequisite:** A source-owner-supported earliest retained action time (or retention-clock mapping/policy history) and history-purge semantics are required for Details. A 740-day ingestion-retention policy alone does not prove coverage of an arbitrary `IncidentHistory.ChangeDate` window. Details export fails explicitly until valid coverage evidence is configured; Teams Incident export, ordinary incident analysis and Work Item/Git exports do not depend on that evidence. Synthetic incident fixtures verify the implementation, not production coverage.

Each generated file is limited to 32 MiB UTF-8 and the pair to 64 MiB. Preparation also bounds input records (including nested history/files), summary rows, identities, and individual text values. ADO collection rejects responses over 16 MiB and cumulative response data over 128 MiB per collection, before parsing unbounded data. Edited/renamed Git files use the supported Git `fileDiffs` API's first-parent line counts, not the UI endpoint's full source/diff text; resolved rename paths and exact additions/deletions are preserved. The collection budget measures cumulative response bytes, not the final ZIP size. Oversized requests fail explicitly; select a smaller scope/date range rather than relying on truncation. Reports are generated in memory and streamed to the browser, not stored on the server or under `wwwroot`.

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
