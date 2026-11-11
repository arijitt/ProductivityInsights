# Copilot Instructions for ProductivityInsights

## Build & Run

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run in development mode
dotnet run

# Run in production mode
dotnet run --configuration Release
```

Report fixtures run separately from the web project:
`dotnet run --project Tests\ProductivityInsights.ReportTests.csproj`.

## Architecture

This is a .NET 8.0 Blazor Server application for analyzing team productivity metrics from Azure DevOps and ICM (Incident Management).

### Core Flow

1. **Components/Pages/AnalysisOptions.razor** - Main UI page with three analysis tabs (Work Items, Git Commits, Incident Resolutions)
2. **Metrics/** - Static classes that call external APIs and return typed collections:
   - `GitCommitMetrics` → Azure DevOps Git API
   - `WorkItemCompletionMetrics` → Azure DevOps Work Item Tracking API (WIQL)
   - `IncidentManagementMetrics` → Azure Data Explorer (`IcmDataWarehouse`) for Incident Resolutions
3. **Models/** - DTOs for deserializing API responses (use lowercase property names matching JSON)
4. **Options/** - Query parameter classes passed from UI to metrics classes

### Authentication

- **Azure DevOps**: Uses `DefaultAzureCredential` via `Utilities/UserToken.cs` with token caching (15-min buffer)
- **Incident Resolutions**: Uses `DefaultAzureCredential`; cluster and database come from the `IncidentManagementKusto` configuration section

### Services

- `ThemeService` - Scoped service for dark/light mode toggle, injected via DI

### Report Export Compatibility

- Existing `Metrics/`, API DTOs, authentication, queries, and ordinary analysis/detail callers must not be modified for export.
- Preserve all existing UX report markup, icons, styles, labels, ordering, source mappings, and compact Git file previews. Export builders must never feed the on-screen report renderers.
- New export-only collectors, immutable records, builders, and writers live under `Services/Reports/` and `Models/Reports/`. Share calculations only between the downloaded formats.
- Capture executed input/source/manager context separately at run and cache boundaries. Editing form inputs must not relabel or redirect an export.
- Deliver exactly two standalone HTML files inside the unchanged ZIP name: `<stem>.html` (existing summary) and `<stem>_Detailed.html` (all former Markdown content). The HTML stem includes analysis/view/dates but no GUID; retain the run GUID only in the ZIP filename (`<stem>_<runid>.zip`) and report metadata. Render the unchanged Markdown layout with the bounded HTML writer; never scrape HTML/DOM, expand rows, or take screenshots. Preserve summary HTML byte-for-byte except its detail-link target, and leave online presentation unchanged.
- Detailed HTML uses dark screen/light print styling and offline roster/ICM-path navigation. Record destinations alongside their source-qualified writer sections; never infer identity from display names. Keep empty/unavailable selections explicit rather than creating broken links or claiming zero activity.
- Teams Git, Work Items and Incident TEAM ROSTER use parent indices captured from the already-loaded online org tree, preserving its actual relationships/order and hyperlinks. Confine hierarchy markup to that section; never change the online tree, refetch directory data, or invent relationships for older flat snapshots.
- Teams Work Items detailed HTML consolidates per-person summary tables and all recorded item evidence into each member's completion section, without a separate All contributor work item details section. Keep icon-led fields, timelines, precise history/state-total tables and source-qualified evidence; reuse existing calculations and preserve source deduplication, summary HTML, legacy Markdown and the online renderer. Zero-item members show only their counted heading.
- Source-qualified work item, repository/commit, PR, and silo/incident keys prevent collisions and double counting across overlapping selections.
- PR completion attribution uses `closedBy`/`closedDate`. Incident contribution uses recorded in-period actions with purge and scope checks. Teams Incident export uses available warehouse data, not independently attested historical coverage.
- Start export history joins from the small team-scoped incident key set with broadcast, not warehouse-wide period history. Qualify action IDs by silo/incident/history ID; do not assume HistoryId is globally unique or bypass true partial-query failures.
- Teams must retain in-scope actions with null HistoryId, display the ID as unavailable, and deduplicate only identical recorded incident/actor/time/type evidence using the shared IncidentActionKey. Never invent IDs or drop those contribution records; keep known-ID conflict, required-evidence and purge checks.
- Resolve export transports lazily by analysis type and coverage configuration only when Details requests attestation. Teams Incident export must not require `IncidentReportCoverage` or schema/retention management queries. Details coverage must come from source-owner evidence; never invent coverage dates from retention duration alone.
- Git source HTTP/transport failures are best-effort: continue with other sources and disclose omissions in both files. Publish each source atomically; skipped sources must not create zero-activity rows or enter totals. Do not cache snapshots with skipped sources, so retrying export retries availability.
- Cancellation, resource limits, required data-query failures, malformed/partial responses, and Incident Details coverage failures remain export-only errors. Disclose Teams Incident data availability and zero semantics; never change legacy analysis handling or treat UI caches as complete export evidence.
- Use safe encoded text/links and `DotNetStreamReference`; retain stream lifetime, circuit-safe cleanup, cancellation, and stale-download guards. Never silently truncate oversized reports.
- Stream downloaded HTML directly into a private delete-on-close temporary ZIP (256 MiB per HTML file, 512 MiB uncompressed pair). Keep repeated recorded text out of the structural Markdown buffer; do not switch downloads back to the smaller buffered string writers or persist server reports.
- Compatibility tests preserve the restored `1379858` API/UX baseline; an API or report-format change requires explicit approval.

## Conventions

### Feature Registry Tab

- `featureRegistry` is an independent analysis key for the horizontal **Feature Registry** tab in Details, Trends and Teams. Keep the fixed `msdata/A365/FeatureRegistry` source out of every existing Git dropdown; preserve other tabs and defaults.
- New PR records/collector live under `Models/FeatureRegistry/` and `Services/FeatureRegistry/`. New reads are REST 7.1 repository metadata, PR list/detail, and (Teams only) PR iteration metadata plus paginated iteration changes. Existing Git APIs, authentication and ordinary collectors remain unchanged; do not fetch file contents, line diffs or commit lists for PR file lists.
- Include all active PRs (including drafts) across all branches plus completed PRs inside the inclusive captured `closedDate` window. Teams requires Fetch Team and attributes open PRs to authors, completed PRs to `closedBy`; disclose unknown/ambiguous/conflicting membership.
- Both status queries must complete before publishing. Dedicated online rendering uses immutable source records, never export builders. Keep new form/loading/error/snapshot state and per-view caches separate; suppress stale cross-tab publications.
- Both HTML downloads use the exact successful Run Analysis snapshot without recollection. Preserve archive limits, offline roster links and ordinary formats. Feature Registry ZIP and HTML filenames use `Feature_Registry_`; ordinary Git exports retain `Git_Commits_`. Analysis/cache identity remains `featureRegistry`.
- Teams Feature Registry summary HTML reuses the Git summary styles and compact by-repository/overall tables with open/completed PR counts and all roster members. Sort by completions then opens; retain full PR records and snapshot metadata in collapsed context, with a visible unavailable-membership warning. Keep Details/Trends summaries and ordinary reports unchanged.
- Feature Registry detailed exports reuse the Git report presentation: captured roster hierarchy, per-person and PR sections, icon-led fields and a repository summary. Omit the overall summary and captured-input/query-context sections from detailed HTML; retain snapshot metadata in summary HTML. Teams folds remain offline/print-safe. Stream full PR titles outside the structural Markdown buffer; in Teams, show the actual title inline after its label instead of a placeholder/code block. Preserve attribution, unavailable membership, summary HTML and online rendering.
- Teams Feature Registry detailed reports include every captured roster member in the main person sections and repository summary. Use name-only headings/rows and explicit zero PR counts for members without attributed records; keep hierarchy links identity-qualified and unavailable membership separate.
- Capture full Teams PR changed-file lists during analysis, including membership-unavailable PRs, pinned to the latest captured iteration with compareTo=0. Retain paths/change types/recorded rename paths, exclude folders, and finish all pages before publishing. Keep file lists inside detailed PR sections only; empty and uncaptured lists must remain distinct, and exports never requery.
- PR deletion entries can have null `item.path`; retain their recorded `originalPath` or legacy `sourceServerItem` as the deleted filename. Do not drop them, guess paths, or use original paths for missing add/edit/rename destinations. Preserve atomic failures for genuinely missing file evidence.

### API Calls

- External data calls are asynchronous methods in `Metrics/` classes; reusable configured clients may be registered through DI
- Use `HttpClient` with Bearer token (Azure DevOps) or client certificate (ICM)
- Handle pagination with continuation tokens; collect all pages into a single collection
- Return `null` on API failure after logging error to console

### Debug Logging

Wrap debug output in preprocessor directives:
```csharp
#if DEBUG
    Console.WriteLine("Debug information...");
#endif
```

### Model Classes

- Property names match API JSON responses (typically camelCase)
- Use `JsonPropertyName` attribute when JSON key contains special characters (e.g., `odata.nextLink`)
- Nullable types (`?`) for optional properties

### Blazor Components

- Use `@rendermode InteractiveServer` for interactive pages
- Results are cached per tab; cleared when running new analysis
- Use `@((MarkupString)htmlContent)` for rendering HTML strings

- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.
