namespace ProductivityInsights.Metrics
{
    using Azure.Core;
    using ProductivityInsights.Models.Teams;
    using ProductivityInsights.Utilities;
    using System.Net.Http.Headers;
    using System.Text.Json;

    /// <summary>
    /// Retrieves manager / reporting-hierarchy information from the Microsoft Graph API.
    /// All calls use a Graph-scoped <see cref="GraphToken"/> bearer token and raw HttpClient,
    /// consistent with the other Metrics classes. Errors are logged to console and the
    /// methods return empty/partial results on failure rather than throwing.
    /// </summary>
    public class TeamHierarchyMetrics
    {
        private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

        // Defensive limits to prevent runaway recursion / excessive Graph calls.
        private const int MaxDepth = 10;
        private const string SelectFields = "id,displayName,mail,userPrincipalName,jobTitle,department";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Searches Azure AD / Entra ID for users whose display name starts with the supplied
        /// text so the caller can disambiguate and pick the intended manager.
        /// </summary>
        /// <param name="nameQuery">Partial or full display name typed by the user.</param>
        /// <returns>A list of matching <see cref="GraphUser"/> (may be empty).</returns>
        public static async Task<List<GraphUser>> SearchManagersAsync(string nameQuery)
        {
            var results = new List<GraphUser>();

            if (string.IsNullOrWhiteSpace(nameQuery))
            {
                return results;
            }

            try
            {
                AccessToken accessToken = await GraphToken.GetToken();

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken.Token);

                // Escape single quotes for the OData filter string literal.
                string escaped = nameQuery.Trim().Replace("'", "''");
                string filter = Uri.EscapeDataString($"startswith(displayName,'{escaped}')");
                string select = Uri.EscapeDataString(SelectFields);

                string url = $"{GraphBaseUrl}/users?$filter={filter}&$select={select}&$top=15";

#if DEBUG
                Console.WriteLine($"🔍 Searching Graph users from URL: {url}");
#endif

                string? nextUrl = url;

                while (!string.IsNullOrEmpty(nextUrl))
                {
                    var httpResponse = await httpClient.GetAsync(nextUrl);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        string errorContent = await httpResponse.Content.ReadAsStringAsync();
                        Console.WriteLine($"Error searching managers: {httpResponse.StatusCode} - {errorContent}");
                        break;
                    }

                    string content = await httpResponse.Content.ReadAsStringAsync();
                    var collection = JsonSerializer.Deserialize<GraphUserCollection>(content, JsonOptions);

                    if (collection?.value != null)
                    {
                        results.AddRange(collection.value);
                    }

                    // Follow pagination if present (search results are capped at $top above,
                    // but the API may still paginate).
                    nextUrl = collection?.odatanextLink;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while searching managers: {ex.Message}");
            }

            return results
                .OrderBy(u => u.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Builds the full reporting hierarchy (direct and indirect reports) under the
        /// specified manager by recursively following the Graph <c>directReports</c> relationship.
        /// Includes cycle and depth guards plus pagination handling at every level.
        /// </summary>
        /// <param name="manager">The manager to use as the root of the tree.</param>
        /// <returns>An <see cref="OrgMember"/> root populated with nested reports, or null on failure.</returns>
        public static async Task<OrgMember?> GetReportsHierarchyAsync(GraphUser manager)
        {
            if (manager == null || string.IsNullOrEmpty(manager.id))
            {
                return null;
            }

            try
            {
                AccessToken accessToken = await GraphToken.GetToken();

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken.Token);

                var root = OrgMember.FromGraphUser(manager);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manager.id! };

                await PopulateReportsAsync(httpClient, root, visited, depth: 0);

                return root;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while building reports hierarchy: {ex.Message}");
                return null;
            }
        }

        private static async Task PopulateReportsAsync(
            HttpClient httpClient,
            OrgMember node,
            HashSet<string> visited,
            int depth)
        {
            if (depth >= MaxDepth || string.IsNullOrEmpty(node.Id))
            {
                return;
            }

            string select = Uri.EscapeDataString(SelectFields);
            string? url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(node.Id!)}/directReports?$select={select}&$top=100";

            var directReports = new List<GraphUser>();

            while (!string.IsNullOrEmpty(url))
            {
                var httpResponse = await httpClient.GetAsync(url);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    string errorContent = await httpResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error fetching direct reports for {node.DisplayName}: {httpResponse.StatusCode} - {errorContent}");
                    return;
                }

                string content = await httpResponse.Content.ReadAsStringAsync();
                var collection = JsonSerializer.Deserialize<GraphUserCollection>(content, JsonOptions);

                if (collection?.value != null)
                {
                    directReports.AddRange(collection.value);
                }

                url = collection?.odatanextLink;
            }

            foreach (var report in directReports)
            {
                if (string.IsNullOrEmpty(report.id) || !visited.Add(report.id))
                {
                    // Skip nodes without an id or already-visited nodes (cycle guard).
                    continue;
                }

                var childNode = OrgMember.FromGraphUser(report);
                node.Reports.Add(childNode);

                await PopulateReportsAsync(httpClient, childNode, visited, depth + 1);
            }
        }
    }
}
