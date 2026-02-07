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

1. **Components/Pages/AnalysisOptions.razor** - Main UI page with three analysis tabs (Work Items, Git Commits, ICM Resolutions)
2. **Metrics/** - Static classes that call external APIs and return typed collections:
   - `GitCommitMetrics` → Azure DevOps Git API
   - `WorkItemCompletionMetrics` → Azure DevOps Work Item Tracking API (WIQL)
   - `IncidentAttendanceMetrics` → ICM OData API
3. **Models/** - DTOs for deserializing API responses (use lowercase property names matching JSON)
4. **Options/** - Query parameter classes passed from UI to metrics classes

### Authentication

- **Azure DevOps**: Uses `DefaultAzureCredential` via `Utilities/UserToken.cs` with token caching (15-min buffer)
- **ICM**: Certificate-based auth via `Utilities/KeyVaultUtilities.cs`, retrieves X.509 certs from Azure Key Vault

### Services

- `ThemeService` - Scoped service for dark/light mode toggle, injected via DI

## Conventions

### API Calls

- All external API calls are `async` static methods in `Metrics/` classes
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
