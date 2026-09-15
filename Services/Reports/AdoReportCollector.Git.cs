using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ProductivityInsights.Models.Reports;
using static ProductivityInsights.Services.Reports.AdoReportHttp;

namespace ProductivityInsights.Services.Reports;

public sealed partial class AdoReportCollector
{
    private async Task<ReportSnapshot> CollectGitAsync(
        AdoReportHttp http, ReportRequest request, DateTimeOffset asOf, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var commits = ImmutableArray.CreateBuilder<GitReportRecord>();
        var pullRequests = ImmutableArray.CreateBuilder<PullRequestReportRecord>();
        var commitCache = new Dictionary<string, GitReportRecord>(StringComparer.OrdinalIgnoreCase);
        var branchHeads = new Dictionary<(string Organization, string Repository, string Branch), string>();
        var branchNotes = ImmutableArray.CreateBuilder<string>();
        var skippedSources = ImmutableArray.CreateBuilder<ReportSkippedSource>();
        // The existing Git API formats the selected wall-clock values with a literal Z.
        var startText = request.Start.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var endText = request.End.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var effectiveStart = DateTimeOffset.Parse(startText, CultureInfo.InvariantCulture);
        var effectiveEnd = DateTimeOffset.Parse(endText, CultureInfo.InvariantCulture);
        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CollectSourceAsync(source);
            }
            catch (AdoReportRequestException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                skippedSources.Add(new(source.Key, exception.Message));
                progress?.Report($"Skipped {source.Label}: {exception.Message}");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ReportSnapshot
        {
            Request = request,
            AsOf = asOf,
            Commits = commits.ToImmutable(),
            PullRequests = pullRequests.ToImmutable(),
            SkippedSources = skippedSources.ToImmutable(),
            CoverageNotes =
            [
                $"Git collection completed for {request.Sources.Length - skippedSources.Count} of {request.Sources.Length} selected sources. Unavailable sources are explicitly listed as skipped, matching the online flow's continuation on failed repository reads. Totals include only successfully collected sources; unavailable activity is not zero.",
                $"Git commit selection retains the selected branch ancestry and wall-clock-as-UTC formatting: fromDate={startText}, toDate={endText}; requested timezone is {request.TimeZone}. Each available branch is resolved once and every list page uses its immutable commit SHA. The service controls time-filter inclusion; trend periods use committer timestamps.",
                "As-of marks collection start. Branch tips are pinned when resolved; completed PRs remain live reads filtered by the original target branch and completion window.",
                "Git contributor details retain the author/committer pair; Teams includes a commit when either identity resolves to the roster.",
                "Per-file edited line counts come from complete first-parent Azure DevOps file diffs. Added/deleted files use physical text lines at the correct commit version; an empty text file has zero lines. Only explicitly proven binary or Git submodule changes have non-text zero line counts.",
                "For included sources, every authoritative changed-file page is collected; changeCounts metadata is not a line count or a completeness contract.",
                "Split rename source and destination paths are retained. Their original blob identifiers establish the pairing across all pages; line deltas are counted once at the destination, not again as a full-file deletion at the source.",
                "PRs Merged counts completed REST 7.1 PRs by authoritative closedBy and closedDate, independently filtered to the inclusive effective completion window and target branch. Unknown completion identities remain explicit; creators, authors, parent counts, and autocomplete identities are not substitutes.",
                "Source memberships are preserved. Overall commit and PR totals must deduplicate organization/repository-qualified keys.",
                .. branchNotes
            ]
        };

        async Task CollectSourceAsync(ReportSource source)
        {
            var sourceCommits = ImmutableArray.CreateBuilder<GitReportRecord>();
            var context = $"Git {source.Label}";
            if (string.IsNullOrWhiteSpace(source.Scope) || string.IsNullOrWhiteSpace(source.Branch))
                throw Error(context, "Repository and captured source branch are required.");
            progress?.Report($"Collecting {context}");
            var projectUrl = $"https://dev.azure.com/{Escape(source.Organization)}/{Escape(source.Project)}";
            var repository = await http.JsonAsync(HttpMethod.Get,
                $"{projectUrl}/_apis/git/repositories/{Escape(source.Scope)}?api-version=7.1",
                null, $"{context}, repository details", cancellationToken);
            var repositoryId = Text(repository, "id", context);
            var repositoryUrl = $"{projectUrl}/_apis/git/repositories/{Escape(repositoryId)}";
            var branch = BranchName(source.Branch, context);
            var branchKey = (source.Organization.ToUpperInvariant(), repositoryId.ToUpperInvariant(), branch);
            if (!branchHeads.TryGetValue(branchKey, out var head))
            {
                head = await ResolveBranchAsync(http, repositoryUrl, branch, context, cancellationToken);
                branchHeads.Add(branchKey, head);
            }
            var branchNote = $"{source.Label}: refs/heads/{branch} pinned to commit {head} for all commit-list pages.";
            var commitUrl = $"{repositoryUrl}/commits?searchCriteria.itemVersion.version={Escape(head)}"
                + $"&searchCriteria.itemVersion.versionType=commit&searchCriteria.fromDate={Escape(startText)}"
                + $"&searchCriteria.toDate={Escape(endText)}&api-version=7.0";
            var skip = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var pageContext = $"{context}, commits page at {skip}";
                var page = await http.JsonAsync(HttpMethod.Get,
                    $"{commitUrl}&$top={PageSize}&$skip={skip}", null, pageContext, cancellationToken);
                var values = Array(page, "value", pageContext);
                if (values.GetArrayLength() == 0)
                    break;
                var added = 0;
                foreach (var summary in values.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = CommitId(summary, "commitId", pageContext);
                    if (!seen.Add(id))
                        continue;
                    added++;
                    var key = $"{source.Organization}:{repositoryId}:{id}";
                    if (!commitCache.TryGetValue(key, out var record))
                    {
                        var detailContext = $"{context}, commit {id}";
                        var detail = await http.JsonAsync(HttpMethod.Get,
                            $"{repositoryUrl}/commits/{Escape(id)}?api-version=7.0",
                            null, detailContext, cancellationToken);
                        if (!string.Equals(CommitId(detail, "commitId", detailContext), id, StringComparison.OrdinalIgnoreCase))
                            throw Error(detailContext, "The detail response belongs to a different commit.");
                        var author = Object(detail, "author", detailContext);
                        var committer = Object(detail, "committer", detailContext);
                        var parents = Array(detail, "parents", detailContext).EnumerateArray()
                            .Select(parent => ReadCommitId(parent, detailContext)).ToArray();
                        if (Boolean(detail, "commentTruncated", detailContext) == true)
                            throw Error(detailContext, "The required full commit message was truncated.");
                        record = new GitReportRecord
                        {
                            SourceKey = source.Key,
                            Organization = source.Organization,
                            RepositoryId = repositoryId,
                            Id = id,
                            Author = Identity(author, detailContext),
                            Committer = Identity(committer, detailContext),
                            AuthorDate = Date(author, "date", detailContext),
                            CommitterDate = Date(committer, "date", detailContext),
                            ParentCount = parents.Length,
                            Message = Text(detail, "comment", detailContext, allowEmpty: true),
                            Url = $"{projectUrl}/_git/{Escape(repositoryId)}/commit/{Escape(id)}",
                            Files = []
                        };
                        if (ReportIdentityMatcher.Matches(record.Author, request)
                            || ReportIdentityMatcher.Matches(record.Committer, request))
                        {
                            record = record with
                            {
                                Files = await CollectFilesAsync(http, repositoryUrl,
                                    id, parents.FirstOrDefault(), detailContext, cancellationToken)
                            };
                        }
                        commitCache.Add(key, record);
                    }
                    if (ReportIdentityMatcher.Matches(record.Author, request)
                        || ReportIdentityMatcher.Matches(record.Committer, request))
                        sourceCommits.Add(record with { SourceKey = source.Key });
                }
                if (added == 0)
                    throw Error(pageContext, "Commit pagination repeated a page without making progress.");
                skip = checked(skip + values.GetArrayLength());
            }
            var sourcePullRequests = await CollectPullRequestsAsync(
                http, request, source, repositoryUrl, repositoryId, projectUrl,
                effectiveStart, effectiveEnd, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Publish a source atomically: failed reads must not leave partially counted activity.
            commits.AddRange(sourceCommits);
            pullRequests.AddRange(sourcePullRequests);
            branchNotes.Add(branchNote);
        }
    }

    private async Task<string> ResolveBranchAsync(
        AdoReportHttp http, string repositoryUrl, string branch, string context, CancellationToken cancellationToken)
    {
        var branchContext = $"{context}, resolving immutable tip of refs/heads/{branch}";
        var result = await http.JsonAsync(HttpMethod.Get,
            $"{repositoryUrl}/stats/branches?name={Escape(branch)}&api-version=7.1",
            null, branchContext, cancellationToken);
        if (!string.Equals(BranchName(Text(result, "name", branchContext), branchContext), branch, StringComparison.Ordinal))
            throw Error(branchContext, "The branch response identifies a different branch.");
        return CommitId(Object(result, "commit", branchContext), "commitId", branchContext);
    }

    private static string BranchName(string value, string context)
    {
        if (value.StartsWith("refs/heads/", StringComparison.Ordinal))
            value = value["refs/heads/".Length..];
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("refs/", StringComparison.Ordinal))
            throw Error(context, "A branch name or refs/heads/ reference is required.");
        return value;
    }

    private async Task<ImmutableArray<GitFileReportRecord>> CollectFilesAsync(
        AdoReportHttp http, string repositoryUrl, string commitId, string? parent,
        string context, CancellationToken cancellationToken)
    {
        var changes = await CollectFileChangesAsync(http, repositoryUrl, commitId, context, cancellationToken);
        var renamePaths = ResolveRenamePaths(changes, context, cancellationToken);
        var files = ImmutableArray.CreateBuilder<GitFileReportRecord>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = change.Path;
            var fileContext = $"{context}, file {path}";
            if (!change.IsSubmodule && (!change.IsAdd || change.IsRename) && parent is null)
                throw Error(fileContext, "A non-add change has no first parent for comparison.");

            // Split rename source entries are path evidence; the destination diff owns the line delta.
            if (change.IsSubmodule || change.IsSourceRename)
            {
                files.Add(new GitFileReportRecord(path, change.ChangeType, 0, 0));
                continue;
            }
            var originalPath = change.IsRename ? renamePaths[path] : path;
            int added;
            int deleted;
            if (!change.IsRename && (change.IsAdd || change.IsDelete))
            {
                var lineCount = await FileLineCountAsync(http, repositoryUrl, change.IsDelete ? originalPath : path,
                    change.IsDelete ? parent! : commitId, fileContext, cancellationToken);
                added = change.IsAdd ? lineCount : 0;
                deleted = change.IsDelete ? lineCount : 0;
            }
            else
            {
                var metadata = await GitItemAsync(http, repositoryUrl, path, commitId, false, fileContext, cancellationToken);
                if (IsProvenBinary(metadata, fileContext))
                {
                    added = deleted = 0;
                }
                else
                {
                    var comparison = await http.JsonAsync(HttpMethod.Post,
                        $"{repositoryUrl}/fileDiffs?api-version=7.1", new
                    {
                        baseVersionCommit = parent,
                        targetVersionCommit = commitId,
                        fileDiffParams = new[] { new { originalPath, path } }
                    }, $"{fileContext}, first-parent line counts", cancellationToken);
                    if (Boolean(comparison, "isTruncated", fileContext) == true || Boolean(comparison, "isPartial", fileContext) == true)
                        throw Error(fileContext, "The line diff is partial or truncated.");
                    var comparisons = Array(comparison, "value", fileContext);
                    if (comparisons.GetArrayLength() != 1
                        || (HasValue(comparison, "count") && Integer(comparison, "count", fileContext) != 1))
                        throw Error(fileContext, "The line-diff response must contain exactly the requested file.");
                    var diff = comparisons[0];
                    if (!string.Equals(Text(diff, "path", fileContext), path, StringComparison.Ordinal))
                        throw Error(fileContext, "The line-diff response belongs to a different path.");
                    var returnedOriginal = OptionalText(diff, "originalPath", fileContext);
                    if (!string.Equals(returnedOriginal ?? path, originalPath, StringComparison.Ordinal))
                        throw Error(fileContext, "The line-diff response belongs to a different original path.");
                    if (Array(diff, "lineDiffBlocks", fileContext).GetArrayLength() == 0
                        && (!metadata.TryGetProperty("contentMetadata", out var contentMetadata)
                            || contentMetadata.ValueKind != JsonValueKind.Object
                            || Boolean(contentMetadata, "isBinary", fileContext) != false))
                        throw Error(fileContext, "An empty diff lacks authoritative text or binary metadata; zero line counts cannot be established.");
                    (added, deleted) = ReadDiff(diff, fileContext, cancellationToken);
                }
            }
            files.Add(new GitFileReportRecord(path, change.ChangeType, added, deleted));
        }
        return files.ToImmutable();
    }

    private sealed record GitFileChange(
        string Path, string ChangeType, bool IsAdd, bool IsDelete, bool IsRename,
        bool IsSourceRename, bool IsSubmodule, string? OriginalPath, string? OriginalObjectId);

    private async Task<List<GitFileChange>> CollectFileChangesAsync(
        AdoReportHttp http, string repositoryUrl, string commitId, string context, CancellationToken cancellationToken)
    {
        var result = new List<GitFileChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skip = 0;
        while (true)
        {
            var pageContext = $"{context}, changes page at {skip}";
            var page = await http.JsonAsync(HttpMethod.Get,
                $"{repositoryUrl}/commits/{Escape(commitId)}/changes?top={PageSize}&skip={skip}&api-version=7.0",
                null, pageContext, cancellationToken);
            var changes = Array(page, "changes", pageContext);
            if (changes.GetArrayLength() == 0)
                break;
            var progress = 0;
            foreach (var change in changes.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = Object(change, "item", pageContext);
                var path = Text(item, "path", pageContext);
                if (!seen.Add(path))
                    throw Error(pageContext, $"Changed path '{path}' was repeated; complete pagination cannot be established.");
                progress++;
                if (Boolean(item, "isFolder", pageContext) == true
                    || string.Equals(OptionalText(item, "gitObjectType", pageContext), "tree", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fileContext = $"{context}, file {path}";
                var changeType = Text(change, "changeType", fileContext);
                if (string.Equals(OptionalText(item, "gitObjectType", fileContext), "commit", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new(path, changeType, false, false, false, false, true, null, null));
                    continue;
                }
                var types = changeType.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var isAdd = types.Contains("add", StringComparer.OrdinalIgnoreCase);
                var isDelete = types.Contains("delete", StringComparer.OrdinalIgnoreCase);
                var isSourceRename = types.Contains("sourceRename", StringComparer.OrdinalIgnoreCase);
                var isRename = types.Contains("rename", StringComparer.OrdinalIgnoreCase)
                    || types.Contains("targetRename", StringComparer.OrdinalIgnoreCase);
                if (types.Length == 0 || types.Any(type => !KnownChangeTypes.Contains(type)))
                    throw Error(fileContext, $"Unsupported change type '{changeType}'; line counts cannot be established.");
                if (isAdd && isDelete)
                    throw Error(fileContext, "A change was marked both added and deleted.");
                if ((isSourceRename && (!isDelete || isRename)) || (isRename && isDelete))
                    throw Error(fileContext, $"Inconsistent rename change type '{changeType}'.");
                var originalPath = isRename ? OptionalText(change, "originalPath", fileContext) : null;
                if (isRename && string.IsNullOrWhiteSpace(originalPath))
                    originalPath = OptionalText(change, "sourceServerItem", fileContext);
                var originalObjectId = isRename || isSourceRename ? OptionalText(item, "originalObjectId", fileContext) : null;
                if (originalObjectId == "0000000000000000000000000000000000000000")
                    originalObjectId = null;
                if (!string.IsNullOrWhiteSpace(originalObjectId)
                    && (originalObjectId.Length != 40 || !originalObjectId.All(Uri.IsHexDigit)))
                    throw Error(fileContext, "Invalid original blob identifier for a rename.");
                result.Add(new(path, changeType, isAdd, isDelete, isRename, isSourceRename, false, originalPath, originalObjectId));
            }
            if (progress == 0)
                throw Error(pageContext, "Changed-file pagination did not advance.");
            skip = checked(skip + changes.GetArrayLength());
        }
        return result;
    }

    private static Dictionary<string, string> ResolveRenamePaths(
        List<GitFileChange> changes, string context, CancellationToken cancellationToken)
    {
        var sources = changes.Where(change => change.IsSourceRename)
            .ToDictionary(change => change.Path, StringComparer.Ordinal);
        var byBlob = sources.Values.Where(change => !string.IsNullOrWhiteSpace(change.OriginalObjectId))
            .GroupBy(change => change.OriginalObjectId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        var pairedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in changes.Where(change => change.IsRename))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileContext = $"{context}, file {target.Path}";
            var originalPath = target.OriginalPath;
            GitFileChange? source = null;
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                if (string.IsNullOrWhiteSpace(target.OriginalObjectId)
                    || !byBlob.TryGetValue(target.OriginalObjectId, out var candidates) || candidates.Length == 0)
                    throw Error(fileContext, "A rename omitted its original path and has no matching sourceRename entry.");
                if (candidates.Length != 1)
                    throw Error(fileContext, "The original path of a rename is ambiguous: multiple sourceRename entries share its originalObjectId.");
                source = candidates[0];
                originalPath = source.Path;
            }
            else
            {
                sources.TryGetValue(originalPath, out source);
            }
            if (source is not null)
            {
                if (!string.IsNullOrWhiteSpace(source.OriginalObjectId) && !string.IsNullOrWhiteSpace(target.OriginalObjectId)
                    && !string.Equals(source.OriginalObjectId, target.OriginalObjectId, StringComparison.OrdinalIgnoreCase))
                    throw Error(fileContext, "Rename path and original blob identifiers disagree.");
                if (!pairedSources.Add(source.Path))
                    throw Error(fileContext, "A sourceRename entry was matched to multiple destinations.");
            }
            destinations.Add(target.Path, originalPath);
        }
        foreach (var source in sources.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pairedSources.Contains(source.Path))
                throw Error($"{context}, file {source.Path}", "A sourceRename entry has no verified destination; line counts cannot be established.");
        }
        return destinations;
    }

    private static readonly HashSet<string> KnownChangeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "delete", "edit", "rename", "sourceRename", "targetRename", "encoding", "property"
    };

    private async Task<JsonElement> GitItemAsync(
        AdoReportHttp http, string repositoryUrl, string path, string version, bool includeContent,
        string context, CancellationToken cancellationToken)
    {
        var item = await http.JsonAsync(HttpMethod.Get,
            $"{repositoryUrl}/items?path={Escape(path)}&versionDescriptor.version={Escape(version)}"
            + $"&versionDescriptor.versionType=commit&includeContentMetadata=true&includeContent={includeContent.ToString().ToLowerInvariant()}&$format=json&api-version=7.1",
            null, $"{context}, item at {version}", cancellationToken);
        if (!string.Equals(Text(item, "path", context), path, StringComparison.Ordinal))
            throw Error(context, "The item response belongs to a different path.");
        return item;
    }

    private async Task<int> FileLineCountAsync(
        AdoReportHttp http, string repositoryUrl, string path, string version, string context, CancellationToken cancellationToken)
    {
        var item = await GitItemAsync(http, repositoryUrl, path, version, true, context, cancellationToken);
        if (IsProvenBinary(item, context))
            return 0;
        var content = Text(item, "content", context, allowEmpty: true);
        if (Boolean(item, "isTruncated", context) == true || Boolean(item, "contentTruncated", context) == true)
            throw Error(context, "The service returned truncated file content.");
        if (content.Contains('\0'))
            throw Error(context, "File content contains NUL without authoritative binary metadata.");
        var count = 0;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count = checked(count + 1);
        }
        return count;
    }

    private static bool IsProvenBinary(JsonElement item, string context) =>
        item.TryGetProperty("contentMetadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object && Boolean(metadata, "isBinary", context) == true;

    private static (int Added, int Deleted) ReadDiff(JsonElement diff, string context, CancellationToken cancellationToken)
    {
        if (Boolean(diff, "isTruncated", context) == true || Boolean(diff, "isPartial", context) == true)
            throw Error(context, "The line diff is partial or truncated.");
        var blocks = Array(diff, "lineDiffBlocks", context);
        var added = 0;
        var deleted = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changeType = Required(block, "changeType", context);
            var kind = changeType.ValueKind switch
            {
                JsonValueKind.Number => Integer(block, "changeType", context),
                JsonValueKind.String => changeType.GetString()!.ToLowerInvariant() switch
                {
                    "none" => 0,
                    "add" => 1,
                    "delete" => 2,
                    "edit" => 3,
                    _ => throw Error(context, "Unknown diff block change type.")
                },
                _ => throw Error(context, "Invalid diff block change type.")
            };
            if (kind > 3)
                throw Error(context, $"Unknown diff block change type {kind}.");
            if (Boolean(block, "truncatedBefore", context) == true || Boolean(block, "truncatedAfter", context) == true)
                throw Error(context, "A line-diff block was truncated.");
            // ADO can return unequal unchanged ranges; only the change type's contributing sides define deltas.
            if (kind is 1 or 3)
                added = checked(added + Integer(block, "modifiedLinesCount", context));
            if (kind is 2 or 3)
                deleted = checked(deleted + Integer(block, "originalLinesCount", context));
        }
        return (added, deleted);
    }

    private async Task<ImmutableArray<PullRequestReportRecord>> CollectPullRequestsAsync(
        AdoReportHttp http, ReportRequest request, ReportSource source, string repositoryUrl, string repositoryId, string projectUrl,
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var context = $"Git {source.Label}, completed pull requests";
        var target = source.Branch!.StartsWith("refs/", StringComparison.Ordinal)
            ? source.Branch : $"refs/heads/{source.Branch}";
        var url = $"{repositoryUrl}/pullrequests?api-version=7.1&searchCriteria.status=completed"
            + $"&searchCriteria.targetRefName={Escape(target)}&searchCriteria.queryTimeRangeType=closed"
            + $"&searchCriteria.minTime={Escape(start.ToString("O", CultureInfo.InvariantCulture))}"
            + $"&searchCriteria.maxTime={Escape(end.ToString("O", CultureInfo.InvariantCulture))}";
        var records = ImmutableArray.CreateBuilder<PullRequestReportRecord>();
        var seen = new HashSet<int>();
        var skip = 0;
        while (true)
        {
            var pageContext = $"{context}, page at {skip}";
            var page = await http.JsonAsync(HttpMethod.Get,
                $"{url}&$top={PageSize}&$skip={skip}", null, pageContext, cancellationToken);
            var values = Array(page, "value", pageContext);
            if (values.GetArrayLength() == 0)
                break;
            var progress = 0;
            foreach (var value in values.EnumerateArray())
            {
                var id = Integer(value, "pullRequestId", pageContext, 1);
                if (!seen.Add(id))
                    continue;
                progress++;
                var prContext = $"{context}, PR {id}";
                var pr = value;
                if (!HasCompletionIdentity(pr) || !HasValue(pr, "closedDate")
                    || !HasValue(pr, "targetRefName") || !HasValue(pr, "status"))
                {
                    pr = await http.JsonAsync(HttpMethod.Get,
                        $"{repositoryUrl}/pullrequests/{id}?api-version=7.1",
                        null, $"{prContext}, authoritative detail", cancellationToken);
                    if (Integer(pr, "pullRequestId", prContext, 1) != id)
                        throw Error(prContext, "Detail belongs to a different pull request.");
                }
                var status = Text(pr, "status", prContext);
                var branch = Text(pr, "targetRefName", prContext);
                var date = Date(pr, "closedDate", prContext);
                if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(branch, target, StringComparison.Ordinal)
                    || date < start || date > end)
                    continue;
                var completedBy = pr.TryGetProperty("closedBy", out var identity)
                    ? Identity(identity, prContext, "Unknown completion identity")
                    : new ReportIdentity("Unknown completion identity");
                if (ReportIdentityMatcher.Matches(completedBy, request)
                    || !HasCompletionIdentity(pr))
                {
                    records.Add(new PullRequestReportRecord(source.Key, source.Organization, repositoryId,
                        id, Text(pr, "title", prContext, allowEmpty: true), branch, completedBy, date,
                        $"{projectUrl}/_git/{Escape(repositoryId)}/pullrequest/{id}"));
                }
            }
            if (progress == 0)
                throw Error(pageContext, "PR pagination repeated a page without making progress.");
            skip = checked(skip + values.GetArrayLength());
        }
        return records.ToImmutable();
    }

    private static bool HasValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool HasCompletionIdentity(JsonElement element)
    {
        if (!element.TryGetProperty("closedBy", out var identity) || identity.ValueKind != JsonValueKind.Object)
            return false;
        return new[] { "id", "uniqueName", "email", "displayName" }.Any(name =>
            identity.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()));
    }

    private static string CommitId(JsonElement element, string name, string context) =>
        ReadCommitId(Required(element, name, context), context);

    private static string ReadCommitId(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 40 } id
            || id.Any(character => !Uri.IsHexDigit(character)))
            throw Error(context, "The service returned an invalid commit SHA.");
        return id;
    }
}
