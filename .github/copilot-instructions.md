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

This project has no test suite configured.

## Architecture

This is a .NET 8.0 Blazor Server application for analyzing team productivity metrics from Azure DevOps and ICM (Incident Management).

### Core Flow

1. **Components/Pages/AnalysisOptions.razor** - Main UI page with four analysis tabs (Work Items, Git Commits, ICM Resolutions, Incident Management)
2. **Metrics/** - Static classes that call external APIs and return typed collections:
   - `GitCommitMetrics` → Azure DevOps Git API
   - `WorkItemCompletionMetrics` → Azure DevOps Work Item Tracking API (WIQL)
   - `IncidentAttendanceMetrics` → ICM OData API
   - `IncidentManagementMetrics` → Azure Data Explorer (`IcmDataWarehouse`)
3. **Models/** - DTOs for deserializing API responses (use lowercase property names matching JSON)
4. **Options/** - Query parameter classes passed from UI to metrics classes

### Authentication

- **Azure DevOps**: Uses `DefaultAzureCredential` via `Utilities/UserToken.cs` with token caching (15-min buffer)
- **ICM**: Certificate-based auth via `Utilities/KeyVaultUtilities.cs`, retrieves X.509 certs from Azure Key Vault
- **Incident Management Kusto**: Uses `DefaultAzureCredential`; cluster and database come from the `IncidentManagementKusto` configuration section

### Services

- `ThemeService` - Scoped service for dark/light mode toggle, injected via DI

## Conventions

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
