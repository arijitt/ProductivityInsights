using Azure.Core;
using ProductivityInsights.Models;
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
            string gitRepository,
            string branchName,
            DateTime fromDate,
            DateTime toDate)
        {
            try
            {
                var accessToken = await UserToken.GetToken();

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                // Format dates for Azure DevOps API (ISO 8601 format)
                string fromDateString = fromDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string toDateString = toDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                // Base URL without paging parameters
                string baseCommitsUrl = $"https://dev.azure.com/{organizationName}/{projectName}/_apis/git/repositories/{gitRepository}/commits" +
                                       $"?searchCriteria.itemVersion.version={branchName}" +
                                       "&searchCriteria.itemVersion.versionType=branch" +
                                       $"&searchCriteria.fromDate={fromDateString}" +
                                       $"&searchCriteria.toDate={toDateString}" +
                                       "&api-version=7.0";

                const int pageSize = 1000;
                string? continuationToken = null;
                List<CommitValue> allCommits = new List<CommitValue>();


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

                Console.WriteLine($"📋 Found {allCommits.Count} total commits in the specified range");

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
    }
}
