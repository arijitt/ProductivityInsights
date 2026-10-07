using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ProductivityInsights.Models.FeatureRegistry;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;
using static ProductivityInsights.Services.Reports.AdoReportHttp;

namespace ProductivityInsights.Services.FeatureRegistry;

public interface IFeatureRegistryPullRequestCollector
{
    Task<FeatureRegistrySnapshot> CollectAsync(ReportRequest request, CancellationToken cancellationToken);
}

public sealed class FeatureRegistryPullRequestCollector(
    HttpClient client, Func<CancellationToken, Task<string>> tokenProvider,
    AdoReportCollectionLimits? limits = null) : IFeatureRegistryPullRequestCollector
{
    public async Task<FeatureRegistrySnapshot> CollectAsync(ReportRequest request, CancellationToken cancellationToken)
    {
        FeatureRegistrySource.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var inputBudget = ReportDocumentBuilder.Validate(new ReportSnapshot { Request = request, AsOf = started }, cancellationToken);
        if (request.Roster.Length > ReportRenderingLimits.MaximumIdentities)
            throw new ReportSizeLimitException("The captured roster exceeds the identity limit. No members were omitted.");
        var budget = limits ?? new AdoReportCollectionLimits();
        budget.Validate();
        var http = new AdoReportHttp(client, tokenProvider, budget);
        const string project = "https://dev.azure.com/msdata/A365";
        const string context = "Feature Registry pull requests";
        var repository = await http.JsonAsync(HttpMethod.Get,
            $"{project}/_apis/git/repositories/FeatureRegistry?api-version=7.1", null, context, cancellationToken);
        var repositoryId = Text(repository, "id", context);
        if (!Guid.TryParse(repositoryId, out _) || !FeatureRegistrySource.IsRepository("msdata",
            Text(Object(repository, "project", context), "name", context), Text(repository, "name", context)))
            throw Error(context, "Repository metadata does not identify msdata/A365/FeatureRegistry.");
        var endpoint = $"{project}/_apis/git/repositories/{Escape(repositoryId)}";
        var records = new Dictionary<int, FeatureRegistryPullRequest>();
        await CollectStatusAsync("active");
        await CollectStatusAsync("completed");
        var included = new List<FeatureRegistryPullRequest>();
        var unattributed = new List<FeatureRegistryPullRequest>();
        foreach (var record in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Status == "abandoned" || record.Status == "completed"
                && (record.CompletedAt < request.Start || record.CompletedAt > request.End))
                continue;
            var matches = ReportIdentityMatcher.Matches(record.Attribution, request);
            var unresolved = !HasStrongIdentity(record.Attribution) || HasRosterEvidence(record.Attribution, request.Roster);
            if (!matches && !unresolved) continue;
            var captured = request.IsTeams
                ? record with { FileChanges = await CollectFilesAsync(http, endpoint, record.Id, inputBudget, cancellationToken) }
                : record;
            if (matches) included.Add(captured);
            else unattributed.Add(captured);
        }
        var snapshot = new FeatureRegistrySnapshot(request, started, DateTimeOffset.UtcNow,
            included.Where(pr => pr.Status == "active").OrderByDescending(pr => pr.CreatedAt).ThenBy(pr => pr.Id).ToImmutableArray(),
            included.Where(pr => pr.Status == "completed").OrderByDescending(pr => pr.CompletedAt).ThenBy(pr => pr.Id).ToImmutableArray(),
            unattributed.OrderBy(pr => pr.Id).ToImmutableArray());
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;

        async Task CollectStatusAsync(string status)
        {
            var url = $"{endpoint}/pullrequests?api-version=7.1&searchCriteria.status={status}";
            if (status == "completed")
                url += "&searchCriteria.queryTimeRangeType=closed"
                    + $"&searchCriteria.minTime={Escape(request.Start.ToString("O", CultureInfo.InvariantCulture))}"
                    + $"&searchCriteria.maxTime={Escape(request.End.ToString("O", CultureInfo.InvariantCulture))}";
            var seen = new HashSet<int>();
            var skip = 0;
            while (true)
            {
                var pageContext = $"{context}, {status}, page {skip}";
                var page = await http.JsonAsync(HttpMethod.Get, $"{url}&$top=100&$skip={skip}", null, pageContext, cancellationToken);
                var values = Array(page, "value", pageContext);
                if (Integer(page, "count", pageContext) != values.GetArrayLength())
                    throw Error(pageContext, "The PR count does not match the response.");
                if (values.GetArrayLength() == 0) break;
                var added = 0;
                foreach (var value in values.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = Integer(value, "pullRequestId", pageContext, 1);
                    var first = seen.Add(id);
                    if (first) added++;
                    var pr = value;
                    if (!first || records.ContainsKey(id) || NeedsDetail(pr, status))
                        pr = await http.JsonAsync(HttpMethod.Get, $"{endpoint}/pullrequests/{id}?api-version=7.1",
                            null, $"{context}, PR {id}", cancellationToken);
                    var record = Parse(pr, id);
                    inputBudget.Record(record.Title, record.SourceBranch, record.TargetBranch, record.Url);
                    inputBudget.Identity(record.Author);
                    if (record.CompletedBy is not null) inputBudget.Identity(record.CompletedBy);
                    records[id] = record;
                }
                if (added == 0)
                    throw Error(pageContext, "Pagination repeated without progress; no complete PR result is available.");
                skip = checked(skip + values.GetArrayLength());
            }
        }

        FeatureRegistryPullRequest Parse(JsonElement pr, int id)
        {
            var prContext = $"{context}, PR {id}";
            if (Integer(pr, "pullRequestId", prContext, 1) != id
                || !string.Equals(Text(Object(pr, "repository", prContext), "id", prContext), repositoryId, StringComparison.OrdinalIgnoreCase))
                throw Error(prContext, "The PR belongs to a different repository or ID.");
            var status = Text(pr, "status", prContext).ToLowerInvariant();
            if (status is not ("active" or "completed" or "abandoned"))
                throw Error(prContext, "The PR has an unsupported status.");
            return new FeatureRegistryPullRequest(id, repositoryId, Text(pr, "title", prContext, allowEmpty: true),
                status, Boolean(pr, "isDraft", prContext) ?? throw Error(prContext, "The draft flag is missing."),
                Identity(pr, "createdBy"), Text(pr, "sourceRefName", prContext), Text(pr, "targetRefName", prContext),
                Date(pr, "creationDate", prContext),
                status == "completed" ? Identity(pr, "closedBy") : null,
                status == "completed" ? Date(pr, "closedDate", prContext) : null,
                $"{project}/_git/{Escape(repositoryId)}/pullrequest/{id}");
        }
    }

    private static async Task<FeatureRegistryFileChanges> CollectFilesAsync(AdoReportHttp http, string endpoint,
            int prId, ReportInputBudget inputBudget, CancellationToken token)
    {
        var context = $"Feature Registry, PR {prId}, changed files";
        var iterationsUrl = $"{endpoint}/pullrequests/{prId}/iterations";
        var response = await http.JsonAsync(HttpMethod.Get, $"{iterationsUrl}?includeCommits=false&api-version=7.1",
            null, context, token);
        CheckComplete(response, context);
        var iterations = Array(response, "value", context);
        if (Integer(response, "count", context) != iterations.GetArrayLength())
            throw Error(context, "The iteration count does not match the response.");
        var ids = new HashSet<int>();
        foreach (var iteration in iterations.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var id = Integer(iteration, "id", context, 1);
            if (!ids.Add(id)) throw Error(context, "The iteration list contains duplicate IDs.");
            inputBudget.Record();
        }
        if (ids.Count == 0)
            throw Error(context, "PR iterations are unavailable. No complete changed-file list can be captured; Run Analysis to retry.");
        var iterationId = ids.Max();
        var changes = new Dictionary<int, (FeatureRegistryFileChange File, bool Folder)>();
        var files = new HashSet<FeatureRegistryFileChange>();
        var skip = 0;
        var top = 1000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var pageContext = $"{context}, iteration {iterationId}, page {skip}";
            var page = await http.JsonAsync(HttpMethod.Get,
                $"{iterationsUrl}/{iterationId}/changes?$compareTo=0&$top={top}&$skip={skip}&api-version=7.1",
                null, pageContext, token);
            CheckComplete(page, pageContext);
            var entries = Array(page, "changeEntries", pageContext);
            if (entries.GetArrayLength() > top)
                throw Error(pageContext, "The response contains more changes than requested.");
            var added = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var changeId = Integer(entry, "changeId", pageContext, 1);
                var item = Object(entry, "item", pageContext);
                var path = OptionalText(item, "path", pageContext);
                var changeType = Text(entry, "changeType", pageContext);
                var originalPath = OptionalText(entry, "originalPath", pageContext);
                if (string.IsNullOrWhiteSpace(originalPath))
                    originalPath = OptionalText(entry, "sourceServerItem", pageContext);
                if (string.IsNullOrWhiteSpace(originalPath)) originalPath = null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    // Deletions can have no current item path; the API records the deleted name separately.
                    var types = changeType.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (originalPath is null || !types.Contains("delete", StringComparer.OrdinalIgnoreCase)
                        || types.Any(type => type.Equals("add", StringComparison.OrdinalIgnoreCase)
                            || type.Equals("rename", StringComparison.OrdinalIgnoreCase)
                            || type.Equals("targetRename", StringComparison.OrdinalIgnoreCase)))
                        throw Error(pageContext, $"Change {changeId} has no file path. Only deletions may use a recorded originalPath or sourceServerItem instead of item.path.");
                    path = originalPath;
                }
                var folder = Boolean(item, "isFolder", pageContext) == true
                    || string.Equals(OptionalText(item, "gitObjectType", pageContext), "tree", StringComparison.OrdinalIgnoreCase);
                var file = new FeatureRegistryFileChange(path, changeType, originalPath);
                inputBudget.Record(file.Path, file.ChangeType, file.OriginalPath);
                if (changes.TryGetValue(changeId, out var previous))
                {
                    if (previous != (file, folder))
                        throw Error(pageContext, "A repeated change ID has conflicting file metadata.");
                }
                else
                {
                    changes.Add(changeId, (file, folder));
                    added++;
                    if (!folder) files.Add(file);
                }
            }
            if (entries.GetArrayLength() != 0 && added == 0)
                throw Error(pageContext, "Changed-file pagination repeated without progress.");
            var hasSkip = page.TryGetProperty("nextSkip", out _);
            var hasTop = page.TryGetProperty("nextTop", out _);
            if (hasSkip != hasTop)
                throw Error(pageContext, "The changed-file continuation is incomplete.");
            if (hasSkip)
            {
                var nextSkip = Integer(page, "nextSkip", pageContext);
                var nextTop = Integer(page, "nextTop", pageContext);
                if (nextSkip == 0 && nextTop == 0) break;
                if (entries.GetArrayLength() == 0 || nextSkip <= skip
                    || nextTop is < 1 or > 2000)
                    throw Error(pageContext, "The changed-file continuation is invalid or does not advance.");
                skip = nextSkip;
                top = nextTop;
            }
            else
            {
                // Some API versions omit zero-valued cursors. Confirm completion with an empty page.
                if (entries.GetArrayLength() == 0) break;
                skip = checked(skip + entries.GetArrayLength());
            }
        }
        return new(iterationId, files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.OriginalPath, StringComparer.Ordinal).ThenBy(file => file.ChangeType, StringComparer.Ordinal).ToImmutableArray());

        static void CheckComplete(JsonElement value, string context)
        {
            if (Boolean(value, "isPartial", context) == true || Boolean(value, "isTruncated", context) == true)
                throw Error(context, "The service returned a partial changed-file response.");
        }
    }

    private static bool NeedsDetail(JsonElement value, string expectedStatus)
    {
        foreach (var name in new[] { "repository", "title", "status", "isDraft", "sourceRefName", "targetRefName", "creationDate" })
            if (!value.TryGetProperty(name, out var field) || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return true;
        var repository = value.GetProperty("repository");
        if (repository.ValueKind != JsonValueKind.Object || !repository.TryGetProperty("id", out var repositoryId)
            || repositoryId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(repositoryId.GetString()))
            return true;
        if (!string.Equals(OptionalText(value, "status", "Feature Registry"), expectedStatus, StringComparison.OrdinalIgnoreCase))
            return true;
        return !HasStrongIdentity(Identity(value, "createdBy"))
            || expectedStatus == "completed" && (!HasStrongIdentity(Identity(value, "closedBy"))
                || !value.TryGetProperty("closedDate", out var date) || date.ValueKind != JsonValueKind.String);
    }

    private static ReportIdentity Identity(JsonElement pr, string name)
    {
        if (!pr.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return new("Unavailable");
        if (value.ValueKind != JsonValueKind.Object)
            throw Error("Feature Registry", $"Invalid {name} identity.");
        return new(OptionalText(value, "displayName", name) ?? "Unavailable",
            OptionalText(value, "uniqueName", name) ?? OptionalText(value, "email", name), OptionalText(value, "id", name));
    }

    private static bool HasStrongIdentity(ReportIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Id) || !string.IsNullOrWhiteSpace(identity.Email);

    private static bool HasRosterEvidence(ReportIdentity identity, ImmutableArray<ReportIdentity> roster)
    {
        static bool Equal(string? left, string? right) => !string.IsNullOrWhiteSpace(left)
            && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        var addresses = new[] { identity.Email, identity.PrincipalName, identity.Name }
            .Where(value => value?.Contains('@') == true).ToArray();
        return roster.Any(member => Equal(identity.Id, member.Id) || addresses.Any(address =>
            Equal(address, member.Email) || Equal(address, member.PrincipalName) || Equal(address, member.Name)));
    }
}
