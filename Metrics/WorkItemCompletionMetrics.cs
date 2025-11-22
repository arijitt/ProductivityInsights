namespace ProductivityInsights.Metrics
{
    using Azure.Core;
    using ProductivityInsights.Models.WorkItems;
    using ProductivityInsights.Models.WorkItems.HistoryCollection;
    using ProductivityInsights.Utilities;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;

    public class WorkItemCompletionMetrics
    {
        public static async Task<WorkItemDetailsCollection> QueryWorkItemDetailsAsync(
           string organizationName,
           string projectName,
           string teamName,
           string? areaPath,
           string targetState,
           DateTime startDate,
           DateTime endDate)
        {
            var queryRequest = new
            {
                query = GetWorkItemQuery(
                    organizationName,
                    projectName,
                    teamName,
                    areaPath,
                    targetState,
                    startDate: startDate,
                    endDate: endDate)
            };

            WorkItemDetailsCollection workItemDetailsCollection = new WorkItemDetailsCollection()
            {
                count = 0,
                value = new List<WorkItemValue>(),
            };

            AccessToken accessToken = await UserToken.GetToken();

            string wiqlUrl = $"https://dev.azure.com/{organizationName}/{projectName}/{teamName}/_apis/wit/wiql?api-version=7.1";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            var json = JsonSerializer.Serialize(queryRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpResponse = await httpClient.PostAsync(wiqlUrl, content);

            if (!httpResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {httpResponse.StatusCode}");
                string errorContent = await httpResponse.Content.ReadAsStringAsync();
                PrintUtilities.PrintFormattedJson(errorContent);
                return workItemDetailsCollection;
            }

            string queryResponseContent = await httpResponse.Content.ReadAsStringAsync();
            WIQLQueryResult? queryResponse = JsonSerializer.Deserialize<WIQLQueryResult>(queryResponseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (queryResponse?.WorkItems == null || !queryResponse.WorkItems.Any())
            {
                Console.WriteLine($"No work items found moved to {targetState} state.");
                return workItemDetailsCollection;
            }

#if DEBUG
            PrintUtilities.PrintSingleDashSeparator();
            Console.WriteLine($"📋 Found {queryResponse?.WorkItems.Count} work items moved to {targetState} state.");
            PrintUtilities.PrintSingleDashSeparator();
#endif

            // Get detailed work item information
            workItemDetailsCollection = await GetWorkItemDetailsCollection(accessToken, organizationName, queryResponse);

            return workItemDetailsCollection;
        }

        public static async Task<Dictionary<int, WorkItemHistoryCollection>> GetWorkItemHistoryCollectionAsync(
            string organizationName,
            WorkItemDetailsCollection workItemDetailsCollection)
        {
            const int batchSize = 200;

            Dictionary<int, WorkItemHistoryCollection> workItemHistoryCollectionMap = new Dictionary<int, WorkItemHistoryCollection>();

            if (workItemDetailsCollection.value == null)
            {
                return workItemHistoryCollectionMap;
            }

            AccessToken accessToken = await UserToken.GetToken();

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            for (int i = 0; i < workItemDetailsCollection.count; ++i)
            {
                int workItemId = workItemDetailsCollection.value[i].id;

                var workItemRevisionCollection = new WorkItemHistoryCollection()
                {
                    count = 0,
                    value = new List<Value>(),
                };

                int skipCount = 0;
                bool hasMoreRevisions = true;

#if DEBUG
                Console.WriteLine($"[{i + 1}/{workItemDetailsCollection.count}] Getting history for work item {workItemId}:");
#endif

                while (hasMoreRevisions)
                {
                    string historyUrl = $"https://dev.azure.com/{organizationName}/_apis/wit/workitems/{workItemId}/revisions?$skip={skipCount}&$top={batchSize}&api-version=7.1";
                    var httpResponse = await httpClient.GetAsync(historyUrl);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error getting history for work item {workItemId} (batch starting at {skipCount}): {httpResponse.StatusCode}");

                        string errorContent = await httpResponse.Content.ReadAsStringAsync();

                        Console.WriteLine($"Error details: {errorContent}");

                        hasMoreRevisions = false;

                        break;
                    }

                    string responseContent = await httpResponse.Content.ReadAsStringAsync();

                    WorkItemHistoryCollection? batchedWorkItemHistoryCollection
                        = JsonSerializer.Deserialize<WorkItemHistoryCollection>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (batchedWorkItemHistoryCollection?.value?.Count != 0)
                    {
                        if (batchedWorkItemHistoryCollection != null && batchedWorkItemHistoryCollection.value != null)
                        {
                            workItemRevisionCollection.count += batchedWorkItemHistoryCollection.count;
                            workItemRevisionCollection.value.AddRange(batchedWorkItemHistoryCollection.value);
                        }

                        hasMoreRevisions = batchedWorkItemHistoryCollection?.count == batchSize;

                        if (hasMoreRevisions)
                        {
                            skipCount += batchSize;

#if DEBUG
                            Console.WriteLine($"     Retrieved {batchedWorkItemHistoryCollection?.count} revisions for work item {workItemId} (total so far: {batchedWorkItemHistoryCollection?.value?.Count}). Getting next batch...");
#endif
                        }
                        else
                        {
#if DEBUG
                            Console.WriteLine($"     Retrieved {batchedWorkItemHistoryCollection?.count} revisions for work item {workItemId}.");
#endif
                        }
                    }
                    else
                    {
                        hasMoreRevisions = false;
                    }
                }

                workItemHistoryCollectionMap.Add(workItemId, workItemRevisionCollection);

#if DEBUG
                PrintUtilities.PrintSingleDashSeparator();
#endif

                await Task.Delay(100);
            }

#if DEBUG
            Console.WriteLine($"✓ Retrieved all work item histories for {workItemDetailsCollection.count} work items.");
            PrintUtilities.PrintSingleDashSeparator();
#endif

            return workItemHistoryCollectionMap;
        }

        public static void PrintWorkItemAttributions(
            string teamName,
            string targetState,
            WorkItemDetailsCollection workItemDetailsCollection,
            Dictionary<int, WorkItemHistoryCollection> workItemHistoryCollectionMap)
        {
            if (workItemDetailsCollection == null || workItemDetailsCollection.value == null || !workItemDetailsCollection.value.Any())
            {
                Console.WriteLine($"\nNo work items available for attribution analysis for team '{teamName}'");
                return;
            }

            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"🏆 COMPLETION DETAILS BY PERSON:");
            PrintUtilities.PrintDoubleDashSeparator();

            // Group work items by who moved them to target state
            var workAttributions = new Dictionary<string, List<(WorkItemValue WorkItem, DateTime CompletionDate)>>();

            foreach (var workItem in workItemDetailsCollection.value)
            {
                if (!workItemHistoryCollectionMap.TryGetValue(workItem.id, out var historyCollection) ||
                    historyCollection?.value == null || !historyCollection.value.Any())
                    continue;

                // Find all actual transitions to target state
                var transitionsToTargetState = new List<(int RevisionIndex, DateTime Date, string ChangedBy)>();

                for (int i = 1; i < historyCollection.value.Count; i++)
                {
                    var currentRevision = historyCollection.value[i];
                    var previousRevision = historyCollection.value[i - 1];

                    var currentState = currentRevision.fields?.SystemState;
                    var previousState = previousRevision.fields?.SystemState;

                    // Check if this is a transition TO the target state
                    if (currentState == targetState && previousState != targetState && currentRevision.fields != null)
                    {
                        var changedBy = currentRevision.fields.SystemChangedBy?.displayName ??
                                      currentRevision.fields.SystemChangedBy?.uniqueName ??
                                      "Unknown";
                        DateTime changedDate = currentRevision.fields.SystemChangedDate;

                        transitionsToTargetState.Add((i, changedDate, changedBy));
                    }
                }

                if (!transitionsToTargetState.Any()) continue;

                // Find the last transition to target state that was not followed by a transition away
                (int RevisionIndex, DateTime Date, string ChangedBy)? finalMoveToTargetState = null;

                foreach (var transition in transitionsToTargetState.OrderByDescending(t => t.Date))
                {
                    int revisionIndex = transition.RevisionIndex;

                    // Check if there are any subsequent state changes away from target state
                    bool hasSubsequentStateChange = false;
                    for (int i = revisionIndex + 1; i < historyCollection.value.Count; i++)
                    {
                        if (historyCollection.value[i].fields?.SystemState != targetState)
                        {
                            hasSubsequentStateChange = true;
                            break;
                        }
                    }

                    // If no subsequent state changes away from target state, this is our final move
                    if (!hasSubsequentStateChange)
                    {
                        finalMoveToTargetState = transition;
                        break;
                    }
                }

                if (finalMoveToTargetState != null)
                {
                    string completedBy = finalMoveToTargetState.Value.ChangedBy;
                    if (!workAttributions.ContainsKey(completedBy))
                    {
                        workAttributions[completedBy] = new List<(WorkItemValue, DateTime)>();
                    }
                    workAttributions[completedBy].Add((workItem, finalMoveToTargetState.Value.Date));
                }
            }

            if (!workAttributions.Any())
            {
                Console.WriteLine($"No work items were found with transitions to {targetState} state.");
                return;
            }

            var daysInTargetStatesByContributors = new Dictionary<string, List<double>>();

            // Sort contributors by number of items completed
            var sortedAttributions = workAttributions
                .OrderByDescending(a => a.Value.Count)
                .ThenBy(a => a.Key);

            // Print summary for each contributor
            foreach (var attribution in sortedAttributions)
            {
                var contributor = attribution.Key;
                var items = attribution.Value;

                PrintUtilities.PrintDoubleDashSeparator();
                Console.WriteLine($"👤 {contributor} - {items.Count} work item(s) completed");

                // Calculate days spent in Committed or Active states for this contributor
                var daysInTargetStates = new List<double>();

                foreach (var (workItem, completionDate) in items)
                {
                    if (!workItemHistoryCollectionMap.TryGetValue(workItem.id, out var historyCollection) ||
                        historyCollection?.value == null || historyCollection.value.Count < 2)
                        continue;

                    var stateDurations = new Dictionary<string, TimeSpan>();
                    TimeSpan totalTargetStateDuration = TimeSpan.Zero;

                    // Calculate time spent in each state
                    for (int i = 1; i < historyCollection.value.Count; i++)
                    {
                        var currentRevision = historyCollection.value[i];
                        var previousRevision = historyCollection.value[i - 1];

                        var previousState = previousRevision.fields?.SystemState ?? "New";
                        var currentDate = currentRevision.fields?.SystemChangedDate ?? DateTime.Now;
                        var previousDate = previousRevision.fields?.SystemChangedDate ?? DateTime.Now;

                        var duration = currentDate - previousDate;

                        if (!stateDurations.ContainsKey(previousState))
                            stateDurations[previousState] = TimeSpan.Zero;
                        stateDurations[previousState] = stateDurations[previousState].Add(duration);
                    }

                    // Add time in current state if it's the target state
                    var lastRevision = historyCollection.value.Last();
                    var currentState = lastRevision.fields?.SystemState;
                    if (currentState == targetState)
                    {
                        var lastDate = lastRevision.fields?.SystemChangedDate ?? DateTime.Now;
                        var timeInCurrentState = DateTime.Now - lastDate;

                        if (!stateDurations.ContainsKey(currentState))
                            stateDurations[currentState] = TimeSpan.Zero;
                        stateDurations[currentState] = stateDurations[currentState].Add(timeInCurrentState);
                    }

                    // Sum up time spent in Committed, Active, or In Progress states
                    foreach (var stateDuration in stateDurations)
                    {
                        if (stateDuration.Key.Equals("Committed", StringComparison.OrdinalIgnoreCase) ||
                            stateDuration.Key.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                            stateDuration.Key.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
                        {
                            totalTargetStateDuration = totalTargetStateDuration.Add(stateDuration.Value);
                        }
                    }

                    if (totalTargetStateDuration.TotalDays > 0)
                    {
                        daysInTargetStates.Add(totalTargetStateDuration.TotalDays);
                    }
                }

                daysInTargetStatesByContributors[contributor] = daysInTargetStates;

                // Group items by work item type
                var typeGroups = items
                    .GroupBy(i => i.WorkItem.fields?.SystemWorkItemType ?? "Unknown")
                    .OrderByDescending(g => g.Count());

                Console.WriteLine($"\n📋 Work Item Types:");
                foreach (var typeGroup in typeGroups)
                {
                    Console.WriteLine($"    {typeGroup.Key}: {typeGroup.Count()} items");
                }
                PrintUtilities.PrintDoubleDashSeparator();

                // Show detailed list of items completed
                foreach (var (workItem, completionDate) in items.OrderBy(i => i.CompletionDate))
                {
                    PrintUtilities.PrintSingleDashSeparator();

                    Console.WriteLine($"  [{completionDate:yyyy-MM-dd HH:mm:ss}] {workItem.id}: {workItem.fields?.SystemTitle ?? "N/A"}");
                    Console.WriteLine($"    Type: {workItem.fields?.SystemWorkItemType ?? "N/A"}");
                    Console.WriteLine($"    State: {workItem.fields?.SystemState ?? "N/A"}");
                    Console.WriteLine($"    Assigned To: {workItem.fields?.SystemAssignedTo?.displayName ?? "Unassigned"}");
                    Console.WriteLine($"    URL: {workItem.url ?? "N/A"}");

                    // Print state change history for this work item
                    if (workItemHistoryCollectionMap.TryGetValue(workItem.id, out var historyCollection) &&
                        historyCollection?.value != null && historyCollection.value.Count > 1)
                    {
                        Console.WriteLine("\n    State Change History:");

                        var stateChanges = new List<(string FromState, string ToState, DateTime Date, string ChangedBy, TimeSpan Duration)>();

                        for (int i = 1; i < historyCollection.value.Count; i++)
                        {
                            var current = historyCollection.value[i];
                            var previous = historyCollection.value[i - 1];

                            var currentState = current.fields?.SystemState;
                            var previousState = previous.fields?.SystemState;

                            if (currentState != previousState)
                            {
                                var date = current.fields?.SystemChangedDate ?? DateTime.Now;
                                var changedBy = current.fields?.SystemChangedBy?.displayName ?? "Unknown";
                                var previousDate = previous.fields?.SystemChangedDate ?? DateTime.Now;
                                var duration = date - previousDate;

                                stateChanges.Add((previousState ?? "New", currentState ?? "Unknown", date, changedBy, duration));
                            }
                        }

                        foreach (var change in stateChanges)
                        {
                            var durationStr = FormatUtilities.ToStringValue(change.Duration);
                            Console.WriteLine($"      {change.Date:yyyy-MM-dd HH:mm:ss} - {change.ChangedBy}: {change.FromState} → {change.ToState} (spent {durationStr} in '{change.FromState}')");
                        }
                    }

                    PrintUtilities.PrintSingleDashSeparator();
                }
            }

            // Print overall statistics
            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine("📊 Overall Statistics");
            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"Total Contributors: {workAttributions.Count}");
            Console.WriteLine($"Total Items Completed: {workItemDetailsCollection.count}");
            Console.WriteLine($"Average Items per Contributor: {(double)workItemDetailsCollection.count / workAttributions.Count:F1}");

            // Print work items completed by each contributor
            foreach (var contribution in workAttributions.OrderByDescending(a => a.Value.Count))
            {
                var typeGroups = contribution.Value
                    .GroupBy(i => i.WorkItem.fields?.SystemWorkItemType ?? "Unknown")
                    .OrderByDescending(g => g.Count());

                Console.WriteLine();
                Console.WriteLine($"👤 {contribution.Key} ({contribution.Value.Count} items):");
                foreach (var typeGroup in typeGroups)
                {
                    Console.WriteLine($"     {typeGroup.Key}: {typeGroup.Count()} items");
                }

                // Calculate statistics for days spent in Committed/Active/In Progress states
                if (daysInTargetStatesByContributors.TryGetValue(contribution.Key, out var daysInTargetStates) &&
                    daysInTargetStates.Any())
                {
                    var totalDays = daysInTargetStates.Sum();
                    var maxDays = daysInTargetStates.Max();
                    var minDays = daysInTargetStates.Min();
                    var averageDays = daysInTargetStates.Average();
                    var sortedDays = daysInTargetStates.OrderBy(d => d).ToList();
                    var medianDays = sortedDays.Count % 2 == 0
                        ? (sortedDays[sortedDays.Count / 2 - 1] + sortedDays[sortedDays.Count / 2]) / 2
                        : sortedDays[sortedDays.Count / 2];

                    Console.WriteLine($"     📊 Time Analysis (Committed/Active/In Progress states only):");
                    Console.WriteLine($"          Total Days: {totalDays:F1}");
                    Console.WriteLine($"          Maximum Days: {maxDays:F1}");
                    Console.WriteLine($"          Minimum Days: {minDays:F1}");
                    Console.WriteLine($"          Average Days: {averageDays:F1}");
                    Console.WriteLine($"          Median Days: {medianDays:F1}");
                    Console.WriteLine($"          Work Items with Committed/Active/In Progress time: {daysInTargetStates.Count} of {contribution.Value.Count}");
                }
                else
                {
                    Console.WriteLine($"     📊 Time Analysis: No time spent in Committed/Active/In Progress states found for work items.");
                }
            }
        }

        private static async Task<WorkItemDetailsCollection> GetWorkItemDetailsCollection(
            AccessToken accessToken,
            string organizationName,
            WIQLQueryResult? queryResponse)
        {
            WorkItemDetailsCollection workItemDetailsCollection = new WorkItemDetailsCollection()
            {
                count = 0,
                value = new List<WorkItemValue>()
            };

            // Get work item IDs
            var workItemIds = queryResponse?.WorkItems?.Select(wi => wi.Id).ToList();

            if (workItemIds != null && !workItemIds.Any())
            {
                return workItemDetailsCollection;
            }

            const int batchSize = 200; // Process work items in batches to avoid URL length limitations
          
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            // Process work items in batches
            for (int i = 0; i < workItemIds?.Count; i += batchSize)
            {
                var batchIds = workItemIds.Skip(i).Take(batchSize).ToList();
                string ids = string.Join(",", batchIds);
                string workItemsUrl = $"https://dev.azure.com/{organizationName}/_apis/wit/workitems?ids={ids}&$expand=All&api-version=7.0";

                int batchNumber = (i / batchSize) + 1;
                int totalBatches = (workItemIds.Count + batchSize - 1) / batchSize;

                var httpResponse = await httpClient.GetAsync(workItemsUrl);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Error in batch {batchNumber}: {httpResponse.StatusCode}");
                    string errorContent = await httpResponse.Content.ReadAsStringAsync();
                    PrintUtilities.PrintFormattedJson(errorContent);
                    continue; // Skip this batch but continue with others
                }

                string responseContent = await httpResponse.Content.ReadAsStringAsync();

                WorkItemDetailsCollection batchedWorkItemCollection
                    = JsonSerializer.Deserialize<WorkItemDetailsCollection>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new WorkItemDetailsCollection
                    {
                        count = 0,
                        value = new List<WorkItemValue>()
                    };

                workItemDetailsCollection.count += batchedWorkItemCollection.count;
                workItemDetailsCollection.value!.AddRange(batchedWorkItemCollection.value!);

            }

            return workItemDetailsCollection;
        }

        private static string GetWorkItemQuery(
           string organizationName,
           string projectName,
           string teamName,
           string? areaPath,
           string targetState,
           DateTime startDate,
           DateTime endDate)
        {
            string startDateFormat = startDate.ToString("yyyy-MM-dd");
            string endDateFormat = endDate.ToString("yyyy-MM-dd");

            if (string.IsNullOrEmpty(areaPath))
            {
                areaPath = $"{projectName}\\{teamName}";
            }

            string wiqlQuery = $@"
            SELECT 
            [System.Id], 
            [System.Title], 
            [System.State], 
            [System.WorkItemType], 
            [System.AssignedTo], 
            [System.AreaPath], 
            [System.ChangedDate], 
            [System.ChangedBy]
            FROM workitems
            WHERE [System.TeamProject] = '{projectName}'
            AND [System.AreaPath] UNDER '{areaPath}'
            AND [System.State] = '{targetState}'
            AND [Microsoft.VSTS.Common.StateChangeDate] >= '{startDateFormat}'
            AND [Microsoft.VSTS.Common.StateChangeDate] <= '{endDateFormat}'
            ORDER BY [Microsoft.VSTS.Common.StateChangeDate] ASC";

            return wiqlQuery;
        }
    }
}
