namespace ProductivityInsights.Metrics
{
    using ProductivityInsights.Models.Incidents;
    using ProductivityInsights.Utilities;
    using System.Net.Http.Headers;
    using System.Security.Cryptography.X509Certificates;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class IncidentAttendanceMetrics
    {

        /// <summary>
        /// Asynchronously retrieves all active incidents from the ICM system that are owned by the specified team.
        /// </summary>
        /// <remarks>This method queries the ICM OData API and automatically handles paging to return all
        /// matching active incidents. The method returns null if the API request fails or if deserialization of the
        /// response is unsuccessful.</remarks>
        /// <param name="icmCertificate">The X.509 certificate used to authenticate the request to the ICM API. Must be valid and trusted by the ICM
        /// service.</param>
        /// <param name="owningTeamName">The name or ID of the team whose active incidents are to be retrieved. If null or empty, no team filter is
        /// applied.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an IncidentCollection with all
        /// active incidents matching the specified team, or null if the request fails or no incidents are found.</returns>
        public static async Task<IncidentCollection?> SearchAllActiveIncidentsAsync(
            X509Certificate2 icmCertificate,
            string owningTeamName)
        {
            try
            {
                string queryBaseURL = string.Format("{0}{1}", ICM_API_BASE_URL, ICM_ODATA_API);

                // Build OData filter for active incidents
                var queryOptions = new List<string>();

                if (!string.IsNullOrEmpty(owningTeamName))
                {
                    if (long.TryParse(owningTeamName, out var owningTeamId))
                    {
                        queryOptions.Add($"OwningTeamId eq {owningTeamId}");
                    }
                    else
                    {
                        // escape single quotes
                        var escapedTeamName = owningTeamName.Replace("'", "''");
                        queryOptions.Add($"OwningTeamId eq '{escapedTeamName}'");
                    }
                }

                // Filter for active incidents (Status = 0 typically means active)
                queryOptions.Add("Status eq 'Active'");

                // Compose final query  
                string queryFilter = "?$filter=" + string.Join(" and ", queryOptions);
                string queryURL = queryBaseURL + queryFilter;

#if DEBUG
                Console.WriteLine($"🔍 Querying active ICM incidents from URL: {queryURL}");
#endif

                // Create HttpClientHandler with the certificate
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(icmCertificate);

                // Create HttpClient with the handler
                using var httpClient = new HttpClient(handler);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Collect all incidents across pages
                var allIncidents = new List<IncidentValue>();
                string? nextLink = queryURL;
                int pageCount = 0;

                while (!string.IsNullOrEmpty(nextLink))
                {
                    pageCount++;

#if DEBUG
                    Console.WriteLine($"  📄 Fetching page {pageCount}...");
#endif

                    // Submit the request
                    var httpResponse = await httpClient.GetAsync(nextLink);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error querying incidents: {httpResponse.StatusCode}");
                        string errorContent = await httpResponse.Content.ReadAsStringAsync();
                        PrintUtilities.PrintFormattedJson(errorContent);
                        return null;
                    }

                    string? jsonResponse = await httpResponse.Content.ReadAsStringAsync();

                    // Deserialize the current page
                    IncidentCollection? pageResult = JsonSerializer.Deserialize<IncidentCollection>(jsonResponse, CachedJsonSerializerOptions);

                    if (pageResult == null)
                    {
                        Console.WriteLine("❌ Deserialization returned null Incident object.");
                        return null;
                    }

                    // Add incidents from this page to the collection
                    if (pageResult.value != null && pageResult.value.Count > 0)
                    {
                        allIncidents.AddRange(pageResult.value);

#if DEBUG
                        Console.WriteLine($"    Retrieved {pageResult.value.Count} incidents from page {pageCount} (Total so far: {allIncidents.Count})");
#endif
                    }

                    // Check for next link
                    nextLink = pageResult.odatanextLink;
                }

#if DEBUG
                Console.WriteLine($"✓ Successfully retrieved {allIncidents.Count} active incident details across {pageCount} page(s).");
#endif

                // Return combined results
                var combinedResult = new IncidentCollection
                {
                    value = allIncidents
                };

                return combinedResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error searching active incidents: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Asynchronously searches for resolved incidents in ICM that match the specified team and date range.
        /// </summary>
        /// <remarks>This method queries the ICM OData API for incidents with a status of 'Resolved' and
        /// filters results by the specified team and resolution date range. The method handles paginated results and
        /// combines all incidents into a single collection. If the API request fails or deserialization is
        /// unsuccessful, the method returns null.</remarks>
        /// <param name="icmCertificate">The X.509 certificate used to authenticate the request to the ICM API. Cannot be null.</param>
        /// <param name="owningTeamName">The name or ID of the owning team to filter incidents by. If null or empty, no team filter is applied.</param>
        /// <param name="startDate">The start of the date range (inclusive) for the incident resolution date. If null, no lower bound is
        /// applied.</param>
        /// <param name="endDate">The end of the date range (inclusive) for the incident resolution date. If null, no upper bound is applied.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an IncidentCollection with the
        /// resolved incidents that match the criteria, or null if the query fails or no incidents are found.</returns>
        public static async Task<IncidentCollection?> SearchResolvedIncidentsAsync(
            X509Certificate2 icmCertificate,
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate)
        {
            try
            {
                string queryBaseURL = string.Format("{0}{1}", ICM_API_BASE_URL, ICM_ODATA_API);

                // Build OData filter for resolved incidents
                var queryOptions = new List<string>();

                if (!string.IsNullOrEmpty(owningTeamName))
                {
                    if (long.TryParse(owningTeamName, out var owningTeamId))
                    {
                        queryOptions.Add($"OwningTeamId eq {owningTeamId}");
                    }
                    else
                    {
                        // escape single quotes
                        var escapedTeamName = owningTeamName.Replace("'", "''");
                        queryOptions.Add($"OwningTeamId eq '{escapedTeamName}'");
                    }
                }

                // Filter for resolved incidents
                queryOptions.Add("Status eq 'Resolved'");

                // Date filters (use OData datetime literal for resolution date)
                // ResolutionData is a complex object with Date property
                queryOptions.Add($"ResolutionData/Date ge datetime'{startDate:yyyy-MM-ddTHH:mm:ssZ}'");
                queryOptions.Add($"ResolutionData/Date le datetime'{endDate:yyyy-MM-ddTHH:mm:ssZ}'");

                // Compose final query  
                string queryFilter = "?$filter=" + string.Join(" and ", queryOptions);
                string queryURL = queryBaseURL + queryFilter;

#if DEBUG
                Console.WriteLine($"🔍 Querying resolved ICM incidents from URL: {queryURL}");
#endif

                // Create HttpClientHandler with the certificate
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(icmCertificate);

                // Create HttpClient with the handler
                using var httpClient = new HttpClient(handler);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Collect all incidents across pages
                var allIncidents = new List<IncidentValue>();
                string? nextLink = queryURL;
                int pageCount = 0;

                while (!string.IsNullOrEmpty(nextLink))
                {
                    pageCount++;

#if DEBUG
                    Console.WriteLine($"  📄 Fetching page {pageCount}...");
#endif

                    // Submit the request
                    var httpResponse = await httpClient.GetAsync(nextLink);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error querying incidents: {httpResponse.StatusCode}");
                        string errorContent = await httpResponse.Content.ReadAsStringAsync();
                        PrintUtilities.PrintFormattedJson(errorContent);
                        return null;
                    }

                    string? jsonResponse = await httpResponse.Content.ReadAsStringAsync();

                    // Deserialize the current page
                    IncidentCollection? pageResult = JsonSerializer.Deserialize<IncidentCollection>(jsonResponse, CachedJsonSerializerOptions);

                    if (pageResult == null)
                    {
                        Console.WriteLine("❌ Deserialization returned null Incident object.");
                        return null;
                    }

                    // Add incidents from this page to the collection
                    if (pageResult.value != null && pageResult.value.Count > 0)
                    {
                        allIncidents.AddRange(pageResult.value);

#if DEBUG
                        Console.WriteLine($"    Retrieved {pageResult.value.Count} incidents from page {pageCount} (Total so far: {allIncidents.Count})");
#endif
                    }

                    // Check for next link
                    nextLink = pageResult.odatanextLink;
                }

#if DEBUG
                Console.WriteLine($"✓ Successfully retrieved {allIncidents.Count} resolved incident details across {pageCount} page(s).");
#endif

                // Return combined results
                var combinedResult = new IncidentCollection
                {
                    value = allIncidents
                };

                return combinedResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error searching resolved incidents: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Asynchronously searches for incidents that have been mitigated but not resolved, filtered by owning team and
        /// mitigation date range.
        /// </summary>
        /// <remarks>This method queries the ICM OData API for incidents with a status of 'Mitigated' and
        /// returns all matching results, handling pagination automatically. The search is limited to incidents whose
        /// mitigation date falls within the specified range. The method returns null if the API request fails or if
        /// deserialization of the response is unsuccessful.</remarks>
        /// <param name="icmCertificate">The X.509 certificate used to authenticate the request to the ICM API. Cannot be null.</param>
        /// <param name="owningTeamName">The name or ID of the team that owns the incidents to search for. If null or empty, no team filter is
        /// applied.</param>
        /// <param name="startDate">The start of the mitigation date range. Only incidents mitigated on or after this date are included.</param>
        /// <param name="endDate">The end of the mitigation date range. Only incidents mitigated on or before this date are included.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an IncidentCollection with all
        /// mitigated but not resolved incidents matching the specified criteria, or null if the request fails or no
        /// incidents are found.</returns>
        public static async Task<IncidentCollection?> SearchMitigatedIncidentsAsync(
            X509Certificate2 icmCertificate,
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate)
        {
            try
            {
                string? jsonResponse = null;
                string queryBaseURL = string.Format("{0}{1}", ICM_API_BASE_URL, ICM_ODATA_API);

                // Build OData filter for mitigated but not resolved incidents
                var queryOptions = new List<string>();

                if (!string.IsNullOrEmpty(owningTeamName))
                {
                    if (long.TryParse(owningTeamName, out var owningTeamId))
                    {
                        queryOptions.Add($"OwningTeamId eq {owningTeamId}");
                    }
                    else
                    {
                        // escape single quotes
                        var escapedTeamName = owningTeamName.Replace("'", "''");
                        queryOptions.Add($"OwningTeamId eq '{escapedTeamName}'");
                    }
                }

                // Filter for mitigated incidents (Status = 'Mitigated')
                queryOptions.Add("Status eq 'Mitigated'");

                // Date filters (use OData datetime literal for mitigation date)
                // MitigationData is a complex object with Date property
                queryOptions.Add($"MitigationData/Date ge datetime'{startDate:yyyy-MM-ddTHH:mm:ssZ}'");
                queryOptions.Add($"MitigationData/Date le datetime'{endDate:yyyy-MM-ddTHH:mm:ssZ}'");

                // Compose final query  
                string queryFilter = "?$filter=" + string.Join(" and ", queryOptions);
                string queryURL = queryBaseURL + queryFilter;

#if DEBUG
                Console.WriteLine($"🔍 Querying mitigated ICM incidents from URL: {queryURL}");
#endif

                // Create HttpClientHandler with the certificate
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(icmCertificate);

                // Create HttpClient with the handler
                using var httpClient = new HttpClient(handler);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Collect all incidents across pages
                var allIncidents = new List<IncidentValue>();
                string? nextLink = queryURL;
                int pageCount = 0;

                while (!string.IsNullOrEmpty(nextLink))
                {
                    pageCount++;

#if DEBUG
                    Console.WriteLine($"  📄 Fetching page {pageCount}...");
#endif

                    // Submit the request
                    var httpResponse = await httpClient.GetAsync(nextLink);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error querying incidents: {httpResponse.StatusCode}");
                        string errorContent = await httpResponse.Content.ReadAsStringAsync();
                        PrintUtilities.PrintFormattedJson(errorContent);
                        return null;
                    }

                    jsonResponse = await httpResponse.Content.ReadAsStringAsync();

                    // Deserialize the current page
                    IncidentCollection? pageResult = JsonSerializer.Deserialize<IncidentCollection>(jsonResponse, CachedJsonSerializerOptions);

                    if (pageResult == null)
                    {
                        Console.WriteLine("❌ Deserialization returned null Incident object.");
                        return null;
                    }

                    // Add incidents from this page to the collection
                    if (pageResult.value != null && pageResult.value.Count > 0)
                    {
                        allIncidents.AddRange(pageResult.value);

#if DEBUG
                        Console.WriteLine($"    Retrieved {pageResult.value.Count} incidents from page {pageCount} (Total so far: {allIncidents.Count})");
#endif
                    }

                    // Check for next link
                    nextLink = pageResult.odatanextLink;
                }

#if DEBUG

                Console.WriteLine($"✓ Successfully retrieved {allIncidents.Count} mitigated but not resolved incident details across {pageCount} page(s).");
#endif

                // Return combined results
                var combinedResult = new IncidentCollection
                {
                    value = allIncidents
                };

                return combinedResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error searching mitigated but not resolved incidents: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Searches for incidents created within a specified date range and owned by a given team using the ICM OData
        /// API.
        /// </summary>
        /// <remarks>The method retrieves all matching incidents, handling pagination automatically. The
        /// search uses the ICM OData API and requires a valid client certificate for authentication. If the API request
        /// fails or the response cannot be deserialized, the method returns null.</remarks>
        /// <param name="icmCertificate">The X.509 certificate used to authenticate the request to the ICM API. Cannot be null.</param>
        /// <param name="owningTeamName">The name or ID of the team that owns the incidents to search for. If null or empty, no team filter is
        /// applied.</param>
        /// <param name="startDate">The start of the date range for incident creation. Only incidents created on or after this date are
        /// included. Must not be null.</param>
        /// <param name="endDate">The end of the date range for incident creation. Only incidents created on or before this date are included.
        /// Must not be null.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an IncidentCollection with the
        /// matching incidents, or null if the request fails or no incidents are found.</returns>
        public static async Task<IncidentCollection?> SearchCreatedIncidentsAsync(
            X509Certificate2 icmCertificate,
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate)
        {
            try
            {
                string? jsonResponse = null;
                string queryBaseURL = string.Format("{0}{1}", ICM_API_BASE_URL, ICM_ODATA_API);

                // Build OData filter for created incidents
                var queryOptions = new List<string>();

                if (!string.IsNullOrEmpty(owningTeamName))
                {
                    if (long.TryParse(owningTeamName, out var owningTeamId))
                    {
                        queryOptions.Add($"OwningTeamId eq {owningTeamId}");
                    }
                    else
                    {
                        // escape single quotes
                        var escapedTeamName = owningTeamName.Replace("'", "''");
                        queryOptions.Add($"OwningTeamId eq '{escapedTeamName}'");
                    }
                }

                // Date filters (use OData datetime literal for creation date)
                queryOptions.Add($"CreateDate ge datetime'{startDate:yyyy-MM-ddTHH:mm:ssZ}'");
                queryOptions.Add($"CreateDate le datetime'{endDate:yyyy-MM-ddTHH:mm:ssZ}'");

                // Compose final query  
                string queryFilter = "?$filter=" + string.Join(" and ", queryOptions);
                string queryURL = queryBaseURL + queryFilter;

#if DEBUG
                Console.WriteLine($"🔍 Querying created ICM incidents from URL: {queryURL}");
#endif

                // Create HttpClientHandler with the certificate
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(icmCertificate);

                // Create HttpClient with the handler
                using var httpClient = new HttpClient(handler);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Collect all incidents across pages
                var allIncidents = new List<IncidentValue>();
                string? nextLink = queryURL;
                int pageCount = 0;

                while (!string.IsNullOrEmpty(nextLink))
                {
                    pageCount++;

#if DEBUG
                    Console.WriteLine($"  📄 Fetching page {pageCount}...");
#endif

                    // Submit the request
                    var httpResponse = await httpClient.GetAsync(nextLink);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error querying incidents: {httpResponse.StatusCode}");
                        string errorContent = await httpResponse.Content.ReadAsStringAsync();
                        PrintUtilities.PrintFormattedJson(errorContent);
                        return null;
                    }

                    jsonResponse = await httpResponse.Content.ReadAsStringAsync();

                    // Deserialize the current page
                    IncidentCollection? pageResult = JsonSerializer.Deserialize<IncidentCollection>(jsonResponse, CachedJsonSerializerOptions);

                    if (pageResult == null)
                    {
                        Console.WriteLine("❌ Deserialization returned null Incident object.");
                        return null;
                    }

                    // Add incidents from this page to the collection
                    if (pageResult.value != null && pageResult.value.Count > 0)
                    {
                        allIncidents.AddRange(pageResult.value);

#if DEBUG
                        Console.WriteLine($"    Retrieved {pageResult.value.Count} incidents from page {pageCount} (Total so far: {allIncidents.Count})");
#endif
                    }

                    // Check for next link
                    nextLink = pageResult.odatanextLink;
                }

#if DEBUG
                Console.WriteLine($"✓ Successfully retrieved {allIncidents.Count} created incident details across {pageCount} page(s).");
#endif

                // Return combined results
                var combinedResult = new IncidentCollection
                {
                    value = allIncidents
                };

                return combinedResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error searching created incidents: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }

       
        /// <summary>
        /// 
        /// </summary>
        /// <param name="incidentCollection"></param>
        public static void PrintIncidents(IncidentCollection incidentCollection)
        {
            if (incidentCollection?.value == null)
            {
                Console.WriteLine("No incidents to print.");
                return;
            }

            foreach (var incident in incidentCollection.value)
            {
                PrintUtilities.PrintSingleDashSeparator();
                Console.WriteLine("Incident Details:");
                Console.WriteLine($"\tID: {incident?.Id}");
                Console.WriteLine($"\tURL: https://portal.microsofticm.com/imp/v5/incidents/details/{incident?.Id}/summary");
                Console.WriteLine($"\tTitle: {incident?.Title}");
                Console.WriteLine($"\tStatus: {incident?.Status}");
                Console.WriteLine($"\tSeverity: {incident?.Severity}");
                Console.WriteLine($"\tIs Outage: {incident?.IsOutage}");
                Console.WriteLine($"\tCreated Date: {incident?.CreateDate}");
                Console.WriteLine($"\tModified Date: {incident?.ModifiedDate}");
                Console.WriteLine($"\tOwning Team Name: {incident?.OwningTeamId}");
                Console.WriteLine($"\tOwning Contact Alias: {incident?.OwningContactAlias}");

                if (incident?.MitigationData != null)
                {
                    Console.WriteLine($"\tMitigation Date: {incident.MitigationData.Date}");
                    Console.WriteLine($"\tMitigated By: {incident.MitigationData.ChangedBy}");
                }

                if (incident?.ResolutionData != null)
                {
                    Console.WriteLine($"\tResolution Date: {incident.ResolutionData.Date}");
                    Console.WriteLine($"\tResolved By: {incident.ResolutionData.ChangedBy}");
                }

                PrintUtilities.PrintSingleDashSeparator();
                // Add more fields as necessary
            }

            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"Found {incidentCollection.value.Count} incidents in total.");
            PrintUtilities.PrintDoubleDashSeparator();
        }

        /// <summary>
        /// Provides cached JSON serializer options configured for case-insensitive property name matching and to ignore
        /// null values when writing JSON.
        /// </summary>
        /// <remarks>This field is intended for internal reuse to ensure consistent JSON serialization
        /// behavior across the application. The options specify that property name matching is case-insensitive and
        /// that properties with null values are omitted from the serialized output.</remarks>
        private static readonly JsonSerializerOptions CachedJsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        //ICM documentation: https://eng.ms/docs/products/icm/developers/gettingstarted
        private const string ICM_API_BASE_URL = "https://prod.microsofticm.com/api/cert";
        private const string ICM_ODATA_API = "/incidents";
    }
}
