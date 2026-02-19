using Azure.Core;
using ProductivityInsights.Models;
using ProductivityInsights.Models.CommitChanges;
using ProductivityInsights.Utilities;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ProductivityInsights.Metrics
{
    public class GitCommitMetrics
    {
        /// <summary>
        /// Retrieves a collection of commits that were merged into the specified branch within the given date range
        /// from an Azure DevOps Git repository.
        /// </summary>
        /// <remarks>This method queries the Azure DevOps REST API for commits in the specified repository
        /// and branch within the provided date range. The returned collection may be empty if no commits match the
        /// criteria. The method returns null if the API request fails or an exception is encountered.</remarks>
        /// <param name="accessToken">The access token used to authenticate with the Azure DevOps REST API. Must have sufficient permissions to
        /// read repository commits.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization containing the target repository.</param>
        /// <param name="projectName">The name of the Azure DevOps project that contains the repository.</param>
        /// <param name="gitRepository">The name or ID of the Git repository from which to retrieve merged commits.</param>
        /// <param name="branchName">The name of the branch into which commits were merged. This should be the target branch (for example, 'main'
        /// or 'develop').</param>
        /// <param name="fromDate">The start of the date range for which to retrieve merged commits. Only commits merged on or after this date
        /// are included.</param>
        /// <param name="toDate">The end of the date range for which to retrieve merged commits. Only commits merged on or before this date
        /// are included.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a CommitCollection with the
        /// merged commits if the operation succeeds; otherwise, null if the request fails or an error occurs.</returns>
        public static async Task<CommitCollection?> GetCommitsAsync(
            string organizationName,
            string projectName,
            string gitRepositoryName,
            string branchName,
            DateTime fromDate,
            DateTime toDate)
        {
            try
            {
                AccessToken accessToken = await UserToken.GetToken();

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                // Format dates for Azure DevOps API (ISO 8601 format)
                string fromDateString = fromDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string toDateString = toDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                // Base URL without paging parameters
                string baseCommitsUrl = $"https://dev.azure.com/{organizationName}/{projectName}/_apis/git/repositories/{gitRepositoryName}/commits" +
                                       $"?searchCriteria.itemVersion.version={branchName}" +
                                       "&searchCriteria.itemVersion.versionType=branch" +
                                       $"&searchCriteria.fromDate={fromDateString}" +
                                       $"&searchCriteria.toDate={toDateString}" +
                                       "&api-version=7.0";

                const int pageSize = 1000;
                string? continuationToken = null;
                List<CommitValue> allCommits = new List<CommitValue>();

#if DEBUG
                PrintUtilities.PrintSingleDashSeparator();
#endif

                do
                {
                    string pageUrl = baseCommitsUrl + $"&$top={pageSize}";
                    if (!string.IsNullOrEmpty(continuationToken))
                    {
                        pageUrl += "&continuationToken=" + Uri.EscapeDataString(continuationToken);
                    }

                    var httpResponse = await httpClient.GetAsync(pageUrl);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Error getting commits: {httpResponse.StatusCode}");

                        string errorContent = await httpResponse.Content.ReadAsStringAsync();

                        PrintUtilities.PrintFormattedJson(errorContent);

#if DEBUG
                        PrintUtilities.PrintSingleDashSeparator();
#endif

                        return null;
                    }

                    string responseContent = await httpResponse.Content.ReadAsStringAsync();
                    CommitCollection? commitCollectionPage = JsonSerializer.Deserialize<CommitCollection>(responseContent);

                    if (commitCollectionPage?.value != null && commitCollectionPage.value.Any())
                    {
                        allCommits.AddRange(commitCollectionPage.value);
                    }

                    continuationToken = null;
                    if (httpResponse.Headers.TryGetValues("x-ms-continuationtoken", out var continuationValues))
                    {
                        continuationToken = continuationValues.FirstOrDefault();
                    }

                    // Safety: if fewer results than page size are returned, assume no more pages
                    if (commitCollectionPage?.value == null || commitCollectionPage.value.Count < pageSize)
                    {
                        continuationToken = null;
                    }

                } while (!string.IsNullOrEmpty(continuationToken));

#if DEBUG
                Console.WriteLine($"📋 Found {allCommits.Count} total commits in the specified range");
                PrintUtilities.PrintSingleDashSeparator();
#endif

                return new CommitCollection
                {
                    count = allCommits.Count,
                    value = allCommits
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting commits: {ex.Message}");
                return null;
            }
        }

        public static async Task<GitCommitCollection?> GetCommitLineDetailsAsync(
            string? organizationName,
            string? projectName,
            string? gitRepositoryName,
            string? branchName,
            CommitCollection? commitCollection,
            Func<string, int, int, Task>? progressCallback = null)
        {
            Dictionary<string, CommitDetailsCollection>? commitDetailsCollectionMap = new Dictionary<string, CommitDetailsCollection>();

            Dictionary<string, CommitChangeCollection>? commitChangeDetailsCollectionMap = new Dictionary<string, CommitChangeCollection>();

            GitCommitCollection? gitCommitCollection = null;

            if (commitCollection != null)
            {
                AccessToken accessToken = await UserToken.GetToken();

                GitRepository? gitRepository = await GetRepositoryDetailsAsync(
                    accessToken,
                    organizationName,
                    projectName,
                    gitRepositoryName);

                commitDetailsCollectionMap = await GitCommitMetrics.GetCommitDetailsAsync(
                   accessToken,
                   organizationName!,
                   gitRepository!,
                   branchName!,
                   commitCollection,
                   progressCallback);

                commitChangeDetailsCollectionMap = await GitCommitMetrics.GetCommitChangeDetailsAsync(
                    accessToken,
                    organizationName!,
                    gitRepository!,
                    branchName!,
                    commitCollection,
                    progressCallback);

                gitCommitCollection = await GitCommitMetrics.GetCommitLineChangeDetailsAsyc(
                    accessToken,
                    organizationName!,
                    gitRepository!,
                    branchName!,
                    commitCollection,
                    commitDetailsCollectionMap,
                    commitChangeDetailsCollectionMap,
                    progressCallback);
            }

            return gitCommitCollection;
        }


        /// <summary>
        /// Retrieves details for a specified Azure DevOps Git repository asynchronously.
        /// </summary>
        /// <remarks>This method sends an authenticated HTTP request to the Azure DevOps REST API to
        /// retrieve repository information. Returns null if the repository is not found or if an error occurs during
        /// the request.</remarks>
        /// <param name="accessToken">The access token used to authenticate the request to the Azure DevOps REST API. Cannot be null.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization containing the repository. Cannot be null or empty.</param>
        /// <param name="projectName">The name of the Azure DevOps project containing the repository. Cannot be null or empty.</param>
        /// <param name="gitRepositoryName">The name of the Git repository to retrieve. Cannot be null or empty.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a GitRepository object with
        /// repository details if found; otherwise, null if the repository does not exist or the request fails.</returns>
        private static async Task<GitRepository?> GetRepositoryDetailsAsync(
            AccessToken accessToken,
            string? organizationName,
            string? projectName,
            string? gitRepositoryName)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                // Get repository information
                string repositoryUrl = $"https://dev.azure.com/{organizationName}/{projectName}/_apis/git/repositories/{gitRepositoryName}?api-version=7.0";

#if DEBUG
                Console.WriteLine($"🔗 Connecting to Git repository '{gitRepositoryName}' in project '{projectName}'...");
#endif

                var httpResponse = await httpClient.GetAsync(repositoryUrl);

#if DEBUG
                Console.WriteLine($"Git Repository API Response Status: {httpResponse.StatusCode}");
#endif

                if (!httpResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Error connecting to repository: {httpResponse.StatusCode}");
                    string errorContent = await httpResponse.Content.ReadAsStringAsync();
                    return null;
                }

                string responseContent = await httpResponse.Content.ReadAsStringAsync();

                GitRepository? gitRepositoryDetails = JsonSerializer.Deserialize<GitRepository>(responseContent);

#if DEBUG
                Console.WriteLine($"✓ Successfully connected to repository: {gitRepositoryDetails?.name}");
#endif

                return gitRepositoryDetails;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error connecting to Git repository: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Retrieves detailed information for each commit in the specified collection from an Azure DevOps Git
        /// repository.
        /// </summary>
        /// <remarks>This method performs network requests to the Azure DevOps REST API for each commit in
        /// the provided collection. A small delay is introduced between requests to help avoid rate limiting. Only
        /// commits present in the input collection are processed.</remarks>
        /// <param name="accessToken">The access token used to authenticate requests to the Azure DevOps REST API.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization that contains the target repository.</param>
        /// <param name="projectName">The name of the Azure DevOps project that contains the target repository.</param>
        /// <param name="gitRepository">The name or ID of the Git repository from which to retrieve commit details.</param>
        /// <param name="commitCollection">A collection of commit summaries for which to retrieve detailed information. Cannot be null.</param>
        /// <returns>A dictionary mapping commit IDs to their corresponding detailed commit information. The dictionary is empty
        /// if the commit collection is null or contains no commits.</returns>
        private static async Task<Dictionary<string, CommitDetailsCollection>> GetCommitDetailsAsync(
            AccessToken accessToken,
            string organizationName,
            GitRepository gitRepository,
            string branchName,
            CommitCollection commitCollection,
            Func<string, int, int, Task>? progressCallback = null)
        {
            Dictionary<string, CommitDetailsCollection> commitDetailsMap = new Dictionary<string, CommitDetailsCollection>();

            if (commitCollection == null || commitCollection.value == null)
            {
                return commitDetailsMap;
            }

            int commitCounter = 0;
            int totalCommits = commitCollection.value.Count;

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

#if DEBUG
            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"🔍 Retrieving details for {totalCommits} commits in '{organizationName}\\{gitRepository.project!.name}\\{gitRepository.name}\\{branchName}'");
            PrintUtilities.PrintDoubleDashSeparator();

            PrintUtilities.PrintSingleDashSeparator();
#endif

            foreach (var commitSummary in commitCollection.value)
            {
                commitCounter++;

                if (progressCallback != null)
                {
                    await progressCallback("Retrieving commit details", commitCounter, totalCommits);
                }

#if DEBUG
                Console.WriteLine($"[{commitCounter}/{totalCommits}] Retrieving details for commit {commitSummary.commitId?[..8]} [Author: {commitSummary.author?.name}, Committer: {commitSummary.committer?.name}]");
#endif

                try
                {
                    List<string> parentCommitIdList = new List<string>();

                    // Get detailed commit information including parents
                    string commitUrl = $"https://dev.azure.com/{organizationName}/{gitRepository.project!.name}/_apis/git/repositories/{gitRepository.name}/commits/{commitSummary.commitId}?api-version=7.0";

                    var commitResponse = await httpClient.GetAsync(commitUrl);
                    if (commitResponse.IsSuccessStatusCode)
                    {
                        string commitDetailsContent = await commitResponse.Content.ReadAsStringAsync();
                        var commitDetails = JsonSerializer.Deserialize<CommitDetailsCollection>(commitDetailsContent);

                        commitDetailsMap.Add(commitSummary.commitId!, commitDetails!);

                        // Determine if this is a merge commit (has multiple parents)
                        //commit.IsMergeCommit = commit.ParentCommitIds.Count > 1;
                    }

#if DEBUG
                    PrintUtilities.PrintSingleDashSeparator();
#endif

                    // Small delay to avoid rate limiting
                    await Task.Delay(50);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"⚠️  Warning: Could not get parent IDs for commit {commitSummary.commitId?[..8]}: {e.Message}");
                }
            }

            return commitDetailsMap;
        }

        /// <summary>
        /// Retrieves detailed change information for each commit in the specified collection from an Azure DevOps Git
        /// repository.
        /// </summary>
        /// <remarks>Only file-level changes are included; folder items are excluded from the results. The
        /// method performs network requests for each commit and may be subject to rate limiting by the Azure DevOps
        /// API.</remarks>
        /// <param name="accessToken">The access token used to authenticate requests to the Azure DevOps REST API. Must have sufficient
        /// permissions to read repository data.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization that contains the target repository.</param>
        /// <param name="projectName">The name of the Azure DevOps project that contains the target repository.</param>
        /// <param name="gitRepository">The name or ID of the Git repository from which to retrieve commit change details.</param>
        /// <param name="commitCollection">A collection of commits for which to retrieve change details. Cannot be null and must contain valid commit
        /// entries.</param>
        /// <returns>A dictionary mapping commit IDs to their corresponding change details. The dictionary is empty if the commit
        /// collection is null or contains no commits.</returns>
        private static async Task<Dictionary<string, CommitChangeCollection>> GetCommitChangeDetailsAsync(
            AccessToken accessToken,
            string organizationName,
            GitRepository gitRepository,
            string branchName,
            CommitCollection commitCollection,
            Func<string, int, int, Task>? progressCallback = null)
        {
            Dictionary<string, CommitChangeCollection> commitChangeMap = new Dictionary<string, CommitChangeCollection>();

            if (commitCollection == null || commitCollection.value == null)
            {
                return commitChangeMap;
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            int commitCounter = 0;
            int totalCommits = commitCollection.value.Count;

#if DEBUG
            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"🔍 Retrieving change details for {totalCommits} commits in '{organizationName}\\{gitRepository.project!.name}\\{gitRepository.name}\\{branchName}'");
            PrintUtilities.PrintDoubleDashSeparator();

            PrintUtilities.PrintSingleDashSeparator();
#endif

            foreach (var commitSummary in commitCollection.value)
            {
                try
                {
                    commitCounter++;

                    if (progressCallback != null)
                    {
                        await progressCallback("Retrieving file change details", commitCounter, totalCommits);
                    }

#if DEBUG
                    Console.WriteLine($"[{commitCounter}/{totalCommits}] Retrieving change details for commit {commitSummary.commitId?[..8]} [Author: {commitSummary.author?.name}, Committer: {commitSummary.committer?.name}]");
#endif

                    // Get commit changes with file-level statistics
                    string changesUrl = $"https://dev.azure.com/{organizationName}/{gitRepository.project.name}/_apis/git/repositories/{gitRepository.name}/commits/{commitSummary.commitId}/changes?api-version=7.0";

                    var changesResponse = await httpClient.GetAsync(changesUrl);
                    if (changesResponse.IsSuccessStatusCode)
                    {
                        string changesContent = await changesResponse.Content.ReadAsStringAsync();
                        var commitChanges = JsonSerializer.Deserialize<CommitChangeCollection>(changesContent);

                        // Filter out folder items from the changes collection
                        if (commitChanges?.changes != null)
                        {
                            commitChanges.changes = commitChanges.changes
                                .Where(change => !(change?.item?.isFolder ?? false))
                                .ToList();

                            commitChangeMap.Add(commitSummary.commitId!, commitChanges);
                        }
                    }

#if DEBUG
                    PrintUtilities.PrintSingleDashSeparator();
#endif

                    // Small delay to avoid rate limiting
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  Warning: Could not get details for commit {commitSummary.commitId}: {ex.Message}");
                }
            }

            return commitChangeMap;
        }

        /// <summary>
        /// Retrieves detailed line change information for each commit in the specified collection, returning a list of
        /// enriched Git commit objects.
        /// </summary>
        /// <remarks>This method processes each commit in the provided collection, retrieving line-level
        /// change statistics by comparing each commit to its parent. Only commits with available details and change
        /// information are included in the result. The method may skip commits if required data is missing. Network
        /// requests are made to the Azure DevOps REST API for each commit, and small delays are introduced to avoid
        /// rate limiting.</remarks>
        /// <param name="accessToken">The access token used to authenticate requests to the Azure DevOps REST API. Cannot be null.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization containing the repository. Cannot be null or empty.</param>
        /// <param name="gitRepository">The Git repository for which commit line change details are retrieved. Cannot be null.</param>
        /// <param name="commitCollection">The collection of commits to process. If null or empty, the method returns an empty list.</param>
        /// <param name="commitDetailsCollectionMap">A mapping of commit IDs to their detailed commit information. Must contain entries for all commits in the
        /// collection.</param>
        /// <param name="commitChangeCollectionMap">A mapping of commit IDs to their associated file change collections. Must contain entries for all commits in
        /// the collection.</param>
        /// <returns>A list of GitCommit objects containing detailed line change information for each processed commit. The list
        /// is empty if no commits are provided or if none can be processed.</returns>
        private static async Task<GitCommitCollection> GetCommitLineChangeDetailsAsyc(
            AccessToken accessToken,
            string organizationName,
            GitRepository gitRepository,
            string branchName,
            CommitCollection commitCollection,
            Dictionary<string, CommitDetailsCollection> commitDetailsCollectionMap,
            Dictionary<string, CommitChangeCollection> commitChangeCollectionMap,
            Func<string, int, int, Task>? progressCallback = null)
        {
            Dictionary<string, Dictionary<LineCountTypes, int>> commitLineChangeDetailsMap = new Dictionary<string, Dictionary<LineCountTypes, int>>();

            GitCommitCollection gitCommitCollection = new GitCommitCollection();

            if (commitCollection == null || commitCollection.value == null)
            {
                return gitCommitCollection;
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            int commitCounter = 0;
            int totalCommits = commitCollection.value.Count;

#if DEBUG
            PrintUtilities.PrintDoubleDashSeparator();
            Console.WriteLine($"🔍 Retrieving line change details for {totalCommits} commits in '{organizationName}\\{gitRepository.project!.name}\\{gitRepository.name}\\{branchName}'");
            PrintUtilities.PrintDoubleDashSeparator();

            PrintUtilities.PrintSingleDashSeparator();
#endif

            foreach (var commitSummary in commitCollection.value)
            {
                commitCounter++;

                if (progressCallback != null)
                {
                    await progressCallback("Calculating line changes", commitCounter, totalCommits);
                }

#if DEBUG
                Console.WriteLine($"[{commitCounter}/{totalCommits}] Retrieving line change details for commit {commitSummary.commitId?[..8]} [Author: {commitSummary.author?.name}, Committer: {commitSummary.committer?.name}]");
#endif

                if (!commitDetailsCollectionMap.TryGetValue(commitSummary.commitId!, out var commitDetails))
                {
#if DEBUG
                    Console.WriteLine($"⚠️  Warning: Could not find details for commit {commitSummary.commitId?[..8]}");
#endif

                    continue;
                }

                if (!commitChangeCollectionMap.TryGetValue(commitSummary.commitId!, out var commitChanges))
                {

#if DEBUG
                    Console.WriteLine($"⚠️  Warning: Could not find changes for commit {commitSummary.commitId?[..8]}");
#endif

                    continue;
                }

                try
                {
                    // Use the first parent for comparison (for merge commits, this compares against the main branch)
                    string parentCommitId = commitDetails.parents?.FirstOrDefault() ?? string.Empty;

                    if (string.IsNullOrEmpty(parentCommitId))
                    {

#if DEBUG
                        Console.WriteLine($"⚠️  Warning: No parent commit found for {commitSummary.commitId?[..8]}, skipping line count calculation");
#endif
                        continue;
                    }

                    string? repositoryId = gitRepository.id;

                    // Store per-file line change details
                    Dictionary<string, CommitLineChangeDetails> fileLineChangeDetailsMap = new Dictionary<string, CommitLineChangeDetails>();

                    // Process each changed file to calculate line changes
                    if (commitChanges.changes != null)
                    {
                        foreach (var changeSummary in commitChanges.changes)
                        {
                            try
                            {
                                var filePath = changeSummary?.item?.path;
                                var commitType = changeSummary?.changeType;

                                if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(commitType))
                                {
                                    continue;
                                }

                                // Create the diff parameters JSON
                                var diffParameters = new
                                {
                                    originalPath = filePath,
                                    originalVersion = parentCommitId,
                                    modifiedPath = filePath,
                                    modifiedVersion = commitSummary.commitId,
                                    partialDiff = true,
                                    includeCharDiffs = true
                                };

                                string diffParametersJson = JsonSerializer.Serialize(diffParameters);

                                // Create the file diff API URL
                                string fileDiffUrl = $"https://dev.azure.com/{organizationName}/{gitRepository.project?.name}/_api/_versioncontrol/fileDiff?__v=5&diffParameters={Uri.EscapeDataString(diffParametersJson)}&repositoryId={repositoryId}";

                                var diffResponse = await httpClient.PostAsync(fileDiffUrl, null);

                                if (diffResponse.IsSuccessStatusCode)
                                {
                                    string? diffContent = await diffResponse.Content.ReadAsStringAsync();
                                    var commitLineChangeDetails = JsonSerializer.Deserialize<CommitLineChangeDetails>(diffContent);

                                    // Store the line change details for this specific file
                                    if (commitLineChangeDetails?.blocks != null)
                                    {
                                        fileLineChangeDetailsMap[filePath] = commitLineChangeDetails;
                                    }
                                }
                                else
                                {
#if DEBUG
                                    Console.WriteLine($"⚠️  Warning: Could not get diff for file {filePath} in commit {commitSummary.commitId?[..8]}");
#endif
                                }

                                // Small delay to avoid rate limiting
                                await Task.Delay(10);
                            }
                            catch (Exception fileEx)
                            {
                                Console.WriteLine($"⚠️Warning: Error processing file {changeSummary?.item?.path} for line count: {fileEx.Message}");
                            }
                        }
                    }

                    commitLineChangeDetailsMap = await GetFileLineStatisticsAsync(
                        accessToken,
                        organizationName,
                        gitRepository,
                        commitChangeCollectionMap[commitSummary.commitId!]);

                    var gitCommit = ToGitCommit(
                        accessToken,
                        organizationName,
                        gitRepository,
                        commitSummary,
                        commitDetails,
                        commitChanges,
                        fileLineChangeDetailsMap,
                        commitLineChangeDetailsMap!);

                    gitCommitCollection.Value.Add(gitCommit);

#if DEBUG
                    PrintUtilities.PrintSingleDashSeparator();
#endif

                    // Small delay to avoid rate limiting
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  Warning: Could not process commit {commitSummary.commitId}: {ex.Message}");
                }
            }

            return gitCommitCollection;
        }

        /// <summary>
        /// Aggregates line count statistics for each file affected by the specified commit changes in a Git repository.
        /// </summary>
        /// <remarks>Effective line count is calculated as the total line count minus the empty line count
        /// for each file.</remarks>
        /// <param name="accessToken">The access token used to authenticate requests to the repository.</param>
        /// <param name="organizationName">The name of the organization that owns the Git repository.</param>
        /// <param name="gitRepository">The Git repository containing the files and commits to analyze.</param>
        /// <param name="commitChangeCollection">A collection of commit changes representing the files and their associated changes to process. Cannot be
        /// null.</param>
        /// <returns>A dictionary mapping each file path to a dictionary of line count types and their corresponding counts. The
        /// inner dictionary contains total, empty, and effective line counts for each file. If no changes are found,
        /// the dictionary is empty.</returns>
        private static async Task<Dictionary<string, Dictionary<LineCountTypes, int>>> GetFileLineStatisticsAsync(
             AccessToken accessToken,
            string organizationName,
            GitRepository gitRepository,
            CommitChangeCollection commitChangeCollection)
        {
            Dictionary<string, Dictionary<LineCountTypes, int>> commitLineChangeDetailsMap = new Dictionary<string, Dictionary<LineCountTypes, int>>();

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            if (commitChangeCollection?.changes != null)
            {
                foreach (var commitChange in commitChangeCollection.changes)
                {
                    if (commitChange?.item?.path != null && commitChange.changeType != null)
                    {
                        var filePath = commitChange.item.path;

                        /*
                        var (totalLineCount, emptyLineCount) = await GetFileLineStatisticsAsync(
                          accessToken,
                          organizationName,
                          gitRepository.project!.name!,
                          gitRepository.id!,
                          filePath,
                          commitChange.item.commitId!);
                        */

                        int totalLineCount = 0;
                        int emptyLineCount = 0;

                        try
                        {
                            // API URL to get file content at specific commit
                            string fileContentUrl = $"https://dev.azure.com/{organizationName}/{gitRepository!.project!.name}/_apis/git/repositories/{gitRepository.id!}/items?path={Uri.EscapeDataString(filePath)}&versionDescriptor.version={commitChange.item.commitId!}&versionDescriptor.versionType=commit&api-version=7.1";

                            var response = await httpClient.GetAsync(fileContentUrl);

                            if (response.IsSuccessStatusCode)
                            {
                                string content = await response.Content.ReadAsStringAsync();

                                // Count non-empty lines
                                var totalLines = content.Split('\n');

                                totalLineCount = totalLines.Count();
                                emptyLineCount = totalLines.Where(line => string.IsNullOrWhiteSpace(line)).Count();
                            }
                            else
                            {

#if DEBUG
                                Console.WriteLine($"⚠️  Warning: Could not get content for file {filePath} at commit {commitChange.item.commitId![..8]}: {response.StatusCode}");
#endif
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️  Warning: Error getting line count for file {filePath}: {ex.Message}");

                        }

                        if (commitLineChangeDetailsMap.ContainsKey(filePath!))
                        {
                            commitLineChangeDetailsMap[filePath!][LineCountTypes.Total] += totalLineCount;
                            commitLineChangeDetailsMap[filePath!][LineCountTypes.Empty] += emptyLineCount;
                            commitLineChangeDetailsMap[filePath!][LineCountTypes.Effective] += (totalLineCount - emptyLineCount);
                        }
                        else
                        {
                            Dictionary<LineCountTypes, int> fileLineChangeMap = new Dictionary<LineCountTypes, int>() {
                                                { LineCountTypes.Total, totalLineCount },
                                                { LineCountTypes.Empty, emptyLineCount },
                                                { LineCountTypes.Effective, totalLineCount - emptyLineCount }
                                                };

                            commitLineChangeDetailsMap.Add(filePath!, fileLineChangeMap);
                        }
                    }
                }
            }

            return commitLineChangeDetailsMap;
        }


        /// <summary>
        /// Creates a new GitCommit instance populated with metadata, change statistics, and line change details for a
        /// specific commit in a Git repository.
        /// </summary>
        /// <remarks>The returned GitCommit aggregates both high-level commit metadata and granular
        /// file-level statistics, including line counts and change types. If certain details are unavailable (such as
        /// line-level diffs), some statistics may be incomplete. All required input objects must be provided and
        /// populated with the relevant commit data.</remarks>
        /// <param name="accessToken">The access token used to authenticate requests for retrieving commit and repository information.</param>
        /// <param name="organizationName">The name of the Azure DevOps or Git organization that owns the repository.</param>
        /// <param name="gitRepository">The GitRepository object representing the repository containing the commit. Must not be null.</param>
        /// <param name="commitValue">The CommitValue object containing basic information about the commit, such as its ID, comment, and URLs.
        /// Must not be null.</param>
        /// <param name="commitDetailsCollection">The CommitDetailsCollection containing detailed metadata about the commit, including author, committer, and
        /// parent commit information. Must not be null.</param>
        /// <param name="commitChangeCollection">The CommitChangeCollection representing the set of file changes included in the commit. May be null if no
        /// file changes are available.</param>
        /// <param name="fileLineChangeDetailsMap">A dictionary mapping file paths to their CommitLineChangeDetails objects providing detailed line-by-line change 
        /// information for each file. May be empty if line-level details are not available.</param>
        /// <param name="commitLineChangeDetailsMap">A dictionary mapping file paths to dictionaries of line count types and their corresponding counts, used to
        /// provide per-file line statistics. Must not be null and must contain entries for all changed files.</param>
        /// <returns>A GitCommit object containing the commit's metadata, file change types, and line change statistics. The
        /// returned object includes detailed information about added, deleted, and modified lines for each changed
        /// file.</returns>
        private static GitCommit ToGitCommit(
            AccessToken accessToken,
            string organizationName,
            GitRepository gitRepository,
            CommitValue commitValue,
            CommitDetailsCollection commitDetailsCollection,
            CommitChangeCollection commitChangeCollection,
            Dictionary<string, CommitLineChangeDetails> fileLineChangeDetailsMap,
            Dictionary<string, Dictionary<LineCountTypes, int>> commitLineChangeDetailsMap)
        {
            var gitCommit = new GitCommit
            {
                Organization = organizationName,
                ProjectName = gitRepository?.project?.name,
                RepositoryId = gitRepository?.id,
                CommitId = commitValue.commitId,
                Comment = commitValue.comment,
                Url = commitValue.url,
                RemoteUrl = commitValue.remoteUrl,
                AuthorName = commitDetailsCollection.author?.name,
                AuthorEmail = commitDetailsCollection.author?.email,
                AuthorDate = commitDetailsCollection.author?.date ?? DateTime.MinValue,
                CommitterName = commitDetailsCollection.committer?.name,
                CommitterEmail = commitDetailsCollection.committer?.email,
                CommitterDate = commitDetailsCollection.committer?.date ?? DateTime.MinValue,
                IsMergeCommit = (commitDetailsCollection.parents?.Count ?? 0) > 1,
                ParentCommitIds = commitDetailsCollection.parents ?? new List<string>()
            };

            // Map change counts from CommitValue
            if (commitValue.changeCounts != null)
            {
                gitCommit.ChangeCounts[CommitTypes.Add.ToString()] = commitValue.changeCounts.Add;
                gitCommit.ChangeCounts[CommitTypes.Edit.ToString()] = commitValue.changeCounts.Edit;
                gitCommit.ChangeCounts[CommitTypes.Delete.ToString()] = commitValue.changeCounts.Delete;
            }

            // Process changed files from CommitChangeCollection
            if (commitChangeCollection?.changes != null)
            {
                foreach (var commitChange in commitChangeCollection.changes)
                {
                    if (commitChange?.item?.path != null && commitChange.changeType != null)
                    {
                        var filePath = commitChange.item.path;

                        // Map change type
                        if (Enum.TryParse<CommitTypes>(commitChange.changeType, true, out var commitType))
                        {
                            gitCommit.ChangedFilesCommitTypes[filePath] = commitType;
                        }
                        else
                        {
                            gitCommit.ChangedFilesCommitTypes[filePath] = CommitTypes.Unknown;
                        }

                        if (commitLineChangeDetailsMap.TryGetValue(filePath, out var fileLineCounts))
                        {
                            gitCommit.ChangedFilesTotalLineCounts.Add(filePath, fileLineCounts.GetValueOrDefault(LineCountTypes.Total, 0));
                            gitCommit.ChangedFilesEmptyLineCounts.Add(filePath, fileLineCounts.GetValueOrDefault(LineCountTypes.Empty, 0));
                            gitCommit.ChangedFilesEffectiveLineCounts.Add(filePath, fileLineCounts.GetValueOrDefault(LineCountTypes.Effective, 0));
                        }
                        else
                        {
                            gitCommit.ChangedFilesTotalLineCounts.Add(filePath, 0);
                            gitCommit.ChangedFilesEmptyLineCounts.Add(filePath, 0);
                            gitCommit.ChangedFilesEffectiveLineCounts.Add(filePath, 0);
                        }

                        /*
                        var (totalLineCount, emptyLineCount) = await GetFileLineStatisticsAsync(
                            accessToken,
                            gitCommit.Organization,
                            gitCommit.ProjectName!,
                            gitCommit.RepositoryId!,
                            filePath,
                            gitCommit.CommitId!);

                        gitCommit.ChangedFilesTotalLineCounts.Add(filePath, totalLineCount);
                        gitCommit.ChangedFilesEmptyLineCounts.Add(filePath, emptyLineCount);
                        */



                    }
                }
            }

            foreach (var changedFile in gitCommit.ChangedFilesCommitTypes)
            {
                int fileLinesAdded = 0;
                int fileLinesDeleted = 0;

                try
                {
                    var filePath = changedFile.Key;
                    var commitType = changedFile.Value;

                    // Skip added and deleted files as they don't have diff content in the new version
                    if (commitType == CommitTypes.Add)
                    {
                        fileLinesAdded += gitCommit.ChangedFilesTotalLineCounts.ContainsKey(filePath) ?
                            gitCommit.ChangedFilesTotalLineCounts[filePath] : 0;

                        gitCommit.AddedLines += fileLinesAdded;
                        gitCommit.ChangedFilesAddedLineCounts[changedFile.Key] = fileLinesAdded;

                        continue;
                    }

                    if (commitType == CommitTypes.Delete)
                    {
                        fileLinesDeleted += gitCommit.ChangedFilesTotalLineCounts.ContainsKey(filePath) ?
                            gitCommit.ChangedFilesTotalLineCounts[filePath] : 0;

                        gitCommit.DeletedLines += fileLinesDeleted;
                        gitCommit.ChangedFilesDeletedLineCounts[changedFile.Key] = fileLinesDeleted;
                        continue;
                    }

                    // Calculate line changes using the per-file line change details
                    if (fileLineChangeDetailsMap.TryGetValue(filePath, out var fileLineChangeDetails) && 
                        fileLineChangeDetails?.blocks != null)
                    {
                        foreach (var block in fileLineChangeDetails.blocks)
                        {
                            // Use the ChangeTypes enum values:
                            // 0 = Unchanged, 1 = Addition, 2 = Deletion, 3 = Modification
                            switch (block.changeType)
                            {
                                case (int)ChangeTypes.Addition:
                                    if (block.mLines != null)
                                    {
                                        fileLinesAdded += block.mLines.Count;
                                    }
                                    else
                                    {
                                        fileLinesAdded += block.mLinesCount;
                                    }
                                    break;
                                case (int)ChangeTypes.Deletion:
                                    if (block.oLines != null)
                                    {
                                        fileLinesDeleted += block.oLines.Count;
                                    }
                                    else
                                    {
                                        fileLinesDeleted += block.oLinesCount;
                                    }
                                    break;
                                case (int)ChangeTypes.Modification:

                                    if (block.mLines != null && block.oLines != null)
                                    {
                                        fileLinesAdded += block.mLines.Count;
                                        fileLinesDeleted += block.oLines.Count;
                                    }
                                    else
                                    {
                                        fileLinesAdded += block.mLinesCount;
                                        fileLinesDeleted += block.oLinesCount;
                                    }
                                    break;
                            }
                        }
                    }
                    else
                    {

#if DEBUG
                        Console.WriteLine($"⚠️  Warning: Could not get diff for file {filePath} in commit {gitCommit.CommitId?[..8]}");
#endif
                    }
                }
                catch (Exception fileEx)
                {
                    Console.WriteLine($"⚠️  Warning: Error processing file {changedFile.Key} for line count: {fileEx.Message}");
                }

                gitCommit.ChangedFilesAddedLineCounts[changedFile.Key] = fileLinesAdded;
                gitCommit.ChangedFilesDeletedLineCounts[changedFile.Key] = fileLinesDeleted;

                gitCommit.AddedLines += fileLinesAdded;
                gitCommit.DeletedLines += fileLinesDeleted;
            }

#if DEBUG
            Console.WriteLine($"     📊 Commit {gitCommit.CommitId?[..8]} found with +{gitCommit.AddedLines}/-{gitCommit.DeletedLines} lines");
#endif

            return gitCommit;
        }
    }
}
