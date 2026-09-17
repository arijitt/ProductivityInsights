using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ProductivityInsights.Components.Pages;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Models.Teams;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests;

public static class ExportIntegrationTests
{
    public static void Run()
    {
        CompatibilityTests.Require(new ReportIdentity("", " ", "", "").DisplayName == "Unknown",
            "Blank identity fields must remain explicitly unknown.");
        var loadingRoster = PreparedComponent();
        Set(loadingRoster, "isLoadingOrgTree", true);
        Call(loadingRoster, "BeginReportExportContext", 2);
        CompatibilityTests.Require(Get(loadingRoster, "displayedExportRun") is null
            && ((List<(string Name, string Summary)>)Get(loadingRoster, "contributors")!).Count == 1,
            "A partial roster must block only export context capture, not clear existing analysis.");
        var component = PreparedComponent();
        var captured = Request(component);
        CompatibilityTests.Require(captured.Sources.Length == 1 && captured.Manager == "Example Manager",
            "Teams export must capture the actual manager and sources.");
        Set(component, "selectedGitCommitCombinations", new List<string>());
        Set(component, "selectedManager", null);
        Set(component, "startDateTime", new DateTime(2030, 1, 1));
        CompatibilityTests.Require(Request(component) == captured, "Form edits mutated executed export context.");

        Call(component, "HideReportExportContext", true);
        Call(component, "RestoreReportExportContext", "teams:gitCommits");
        CompatibilityTests.Require(Request(component) == captured, "Cache restore lost the executed context.");
        Set(component, "analysisOperationVersion", 2);
        Call(component, "BeginReportExportContext", 2);
        Call(component, "CompleteReportExportContext", 1, true);
        CompatibilityTests.Require(!Ready(component), "A stale completion enabled export for another run.");
        Call(component, "CompleteReportExportContext", 2, false);
        CompatibilityTests.Require(!Ready(component), "Failed analysis enabled export.");
        CheckDateSemantics();

        var snapshot = Snapshot(captured, 2);
        var archive = ReportArchiveBuilder.Build(snapshot);
        CompatibilityTests.Require(ReportArchiveBuilder.GetFileName(captured) == archive.FileName,
            "The pre-collection Save As suggestion must match the generated ZIP name.");
        using (archive.Content)
        using (var zip = new ZipArchive(archive.Content, ZipArchiveMode.Read))
        {
            CompatibilityTests.Require(zip.Entries.Count == 2, "Export must contain exactly two files.");
            CompatibilityTests.Require(zip.Entries.All(entry => entry.Name.EndsWith(".html")), "Both reports must be HTML.");
            var detailedEntry = zip.Entries.Single(entry => entry.Name.EndsWith("_Detailed.html"));
            var htmlEntry = zip.Entries.Single(entry => !entry.Name.EndsWith("_Detailed.html"));
            var stem = Path.GetFileNameWithoutExtension(htmlEntry.Name);
            CompatibilityTests.Require(detailedEntry.Name == stem + "_Detailed.html", "Detailed HTML naming changed the report stem.");
            using var detailedReader = new StreamReader(detailedEntry.Open());
            using var htmlReader = new StreamReader(htmlEntry.Open());
            var detailed = detailedReader.ReadToEnd();
            var html = htmlReader.ReadToEnd();
            CompatibilityTests.Require(detailed.Contains("file_11.cs"), "Detailed HTML truncated Git's file preview.");
            CompatibilityTests.Require(detailed.Contains(captured.RunId.ToString())
                || detailed.Contains(captured.RunId.ToString("N")), "Detailed HTML omitted run identity.");
            var expectedSummary = HtmlSummaryReportWriter.Write(snapshot, stem + ".md")
                .Replace("./" + Uri.EscapeDataString(stem + ".md"), "./" + Uri.EscapeDataString(detailedEntry.Name), StringComparison.Ordinal);
            CompatibilityTests.Require(html == expectedSummary, "Summary HTML changed beyond the required detail-link target.");
            CompatibilityTests.Require(!html.Contains("<script", StringComparison.OrdinalIgnoreCase)
                && !detailed.Contains("<script", StringComparison.OrdinalIgnoreCase),
                "Standalone HTML must not contain scripts.");
        }

        static void CheckDateSemantics()
        {
            var localClock = new DateTime(2026, 7, 1, 9, 30, 15, DateTimeKind.Local).AddTicks(12345);
            var git = PreparedComponent();
            Set(git, "startDateTime", localClock);
            Call(git, "BeginReportExportContext", 2);
            var gitRequest = Request(git);
            CompatibilityTests.Require(gitRequest.Start.Offset == TimeSpan.Zero
                && gitRequest.Start.Hour == localClock.Hour
                && gitRequest.Start.Ticks % TimeSpan.TicksPerMillisecond == 0,
                "Git export must preserve the legacy millisecond UTC-labeled wall clock, not convert the host offset.");
            CompatibilityTests.Require(gitRequest.RequestedStart == localClock
                && gitRequest.EffectiveStartLiteral!.Contains("T09:30:15.001Z"),
                "Git export lost requested input precision or effective filter text.");

            var incidents = PreparedComponent("incidentManagement");
            Set(incidents, "startDateTime", null);
            Call(incidents, "BeginReportExportContext", 2);
            CompatibilityTests.Require(Request(incidents).Start == DateTimeOffset.UnixEpoch,
                "ICM missing start must use the legacy UnixEpoch default.");
            CompatibilityTests.Require(Request(incidents).EffectiveBoundsOrigin!.Contains("fixed UnixEpoch")
                && !Request(incidents).EffectiveBoundsOrigin!.Contains("anchored at confirmation"),
                "A fixed ICM default must not be mislabeled as a capture-time default.");

            var work = PreparedComponent("workItems");
            Set(work, "startDateTime", localClock);
            Call(work, "BeginReportExportContext", 2);
            CompatibilityTests.Require(Request(work).EffectiveStartLiteral == "2026-07-01",
                "WIQL effective date literal must not imply timestamp precision.");
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            ReportArchiveBuilder.Build(snapshot, cancelled.Token);
            throw new InvalidOperationException("Cancelled archive generation succeeded.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static AnalysisOptions PreparedComponent(string analysis = "gitCommits")
    {
        var component = new AnalysisOptions();
        Set(component, "ReportExportLogger", NullLogger<AnalysisOptions>.Instance);
        Set(component, "selectedOption", analysis);
        Set(component, "selectedView", "teams");
        Set(component, "analysisResultHtml", analysis);
        Set(component, "analysisOperationVersion", 1);
        Set(component, "selectedManager", new GraphUser { displayName = "Example Manager" });
        Set(component, "orgTreeRoot", new OrgMember
        {
            Id = "example-manager", DisplayName = "Example Manager", Mail = "manager@example.test",
            Reports = [new OrgMember { Id = "example-member", DisplayName = "Example Contributor", Mail = "dev@example.test" }]
        });
        Set(component, "selectedGitCommitCombinations", new List<string> { "msdata : A365 : BBC-VHD : master" });
        Set(component, "selectedOwningTeams", new List<string> { "AZURESYNAPSEJOBSERVICE\\JobPlatform" });
        Set(component, "startDateTime", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        Set(component, "endDateTime", new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc));
        Set(component, "contributors", new List<(string Name, string Summary)> { ("Example Contributor", "2 contributions") });
        Set(component, "contributorDetails", new Dictionary<string, string>
        {
            ["Example Contributor"] = "<div class='commit-summary-card'><h6>📊 Commit Summary</h6></div><div class='commit-entry'><div>👤 Author: Example Contributor</div><div>✍️ Committer: Example Contributor</div><div>📅 Date: 2026-07-10</div><div>💬 Message: Example change</div><div>📁 Files Changed (12)</div><div>📝 src/file_00.cs</div><div>📄 ... and 2 more files</div><a href='https://example.test/commit'>🌐 View Commit</a></div>"
        });
        Call(component, "BeginReportExportContext", 1);
        Call(component, "CompleteReportExportContext", 1, true);
        return component;
    }

    internal static ReportSnapshot Snapshot(ReportRequest request, int count = 2)
    {
        var identity = new ReportIdentity("Example Contributor", "dev@example.test", "example-member");
        var source = request.Sources[0];
        var when = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new ReportSnapshot { Request = request, AsOf = when.AddDays(1) };
        if (request.Analysis == "gitCommits")
        {
            return snapshot with
            {
                Commits = Enumerable.Range(0, count).Select(index => new GitReportRecord
                {
                    SourceKey = source.Key, Organization = source.Organization, RepositoryId = "example-repository",
                    Id = index.ToString("x40"), Author = identity, Committer = identity,
                    AuthorDate = when, CommitterDate = when, Message = $"Example change {index}\nSecond line <unsafe>",
                    Url = $"https://example.test/commits/{index}", ParentCount = 1,
                    Files = Enumerable.Range(0, 12).Select(file => new GitFileReportRecord(
                        $"src/file_{file:00}.cs", "edit", file + 1, 1)).ToImmutableArray()
                }).ToImmutableArray(),
                PullRequests = [new(source.Key, source.Organization, "example-repository", 42, "Example pull request",
                    "refs/heads/master", identity, when, "https://example.test/pr/42")]
            };
        }
        if (request.Analysis == "workItems")
        {
            return snapshot with
            {
                WorkItems = [new()
                {
                    SourceKey = source.Key, Organization = source.Organization, Id = 42,
                    Title = "Example item", Type = "Task", State = "Done", Assignee = identity,
                    CreatedAt = when.AddDays(-2), ChangedAt = when, ClosedAt = when,
                    Priority = 2, AreaPath = source.Scope, IterationPath = "Sprint 1", Url = "https://example.test/item/42",
                    History = [new("New", when.AddDays(-2), identity), new("Active", when.AddDays(-1), identity), new("Done", when, identity)]
                }]
            };
        }
        return snapshot with
        {
            Incidents = [new()
            {
                SourceKey = source.Key, Id = "42", SiloId = "1", Title = "Example incident", Severity = 2, Status = "RESOLVED",
                CreatedAt = when.AddHours(-2), ModifiedAt = when, ImpactStart = when.AddHours(-3),
                MitigatedAt = when.AddHours(-1), ResolvedAt = when, Owner = identity, MitigatedBy = identity, ResolvedBy = identity,
                Created = true, Mitigated = true, Resolved = true, OwningTeam = source.Scope,
                Mitigation = "Example mitigation", Url = "https://example.test/incident/42"
            }],
            IncidentActions = [new(source.Key, "42", "1", "action-1", identity, when.AddMinutes(-10), "Updated")]
        };
    }

    internal static ReportRequest Request(AnalysisOptions component)
    {
        var run = Get(component, "displayedExportRun") ?? throw new InvalidOperationException("No export context.");
        return (ReportRequest)run.GetType().GetProperty("Request")!.GetValue(run)!;
    }

    private static bool Ready(AnalysisOptions component) => (bool)Get(component, "CanExportReports")!;

    internal static object? Get(object target, string member) =>
        target.GetType().GetField(member, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? target.GetType().GetProperty(member, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    internal static void Set(object target, string member, object? value)
    {
        var field = target.GetType().GetField(member, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
            field.SetValue(target, value);
        else
            (target.GetType().GetProperty(member, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing fixture member {member}")).SetValue(target, value);
    }

    internal static object? Call(object target, string method, params object?[] args) =>
        (target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing fixture method {method}")).Invoke(target, args);
}
