using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class DetailedHtmlReportTests
{
    public static void Run()
    {
        foreach (var original in new[]
        {
            ReportWriterFixtures.WorkSnapshot(), ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.IncidentSnapshot()
        })
        {
            foreach (var view in original.Request.Analysis == "gitCommits" ? new[] { "teams", "details", "trends" } : ["teams", "details"])
                CheckArchive(original with { Request = original.Request with { View = view } });
        }
        Safety();
        ExecutivePresentation();
        Navigation();
        IncidentRosterOrdering();
        IncidentRecordedUrls();
        GitRecordedUrls();
        WorkItemRecordedUrls();
        GitRedundantIdentityFields();
        GitDetailMetadataIcons();
        IncidentMemberNavigation();
        IncidentDetailConsolidation();
        IncidentActiveTeamLabels();
        LimitsAndCancellation();
    }

    private static void ExecutivePresentation()
    {
        foreach (var snapshot in new[]
        {
            ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.WorkSnapshot(), ReportWriterFixtures.IncidentSnapshot()
        })
        {
            var html = HtmlDetailedReportWriter.Write(snapshot);
            Require(html.Contains("font-family:'Montserrat'", StringComparison.Ordinal)
                && html.Contains("data:font/woff2;base64,", StringComparison.Ordinal)
                && html.Contains("SIL OPEN FONT LICENSE", StringComparison.Ordinal),
                "Detailed reports must carry the licensed on-screen font without network dependencies.");
            Require(html.Contains("class=\"report-section-heading\"", StringComparison.Ordinal)
                && html.Contains("class=\"report-icon\"", StringComparison.Ordinal)
                && html.Contains("class=\"report-table-scroll\"", StringComparison.Ordinal)
                && html.Contains("font-variant-numeric:tabular-nums", StringComparison.Ordinal),
                "Executive details need a clear section hierarchy, consistent icons and readable numeric tables.");
            Require(!html.Contains(new string('\u2550', 120), StringComparison.Ordinal),
                "Generated console-style heading rulers must become semantic report headings.");
            Require(html.Contains(":root { color-scheme:dark;", StringComparison.Ordinal)
                && html.Contains("@media print { :root { color-scheme:light;", StringComparison.Ordinal),
                "Detailed HTML must default to dark mode and retain a readable light print palette.");
        }
        var baseline = ReportWriterFixtures.GitSnapshot();
        var message = new string('\u2550', 120) + "\nAn actual recorded message\n" + new string('\u2500', 120);
        var sourceText = baseline with { Commits = [baseline.Commits[0] with { Message = message }], PullRequests = [] };
        var text = WebUtility.HtmlDecode(Regex.Replace(HtmlDetailedReportWriter.Write(sourceText), "<[^>]*>", ""));
        Require(text.Contains(message, StringComparison.Ordinal),
            "Presentation cleanup must never remove ruler-like text inside a recorded message.");
        var path = "src\\recorded\n" + new string('\u2500', 120) + "\nfile.cs";
        var fileText = sourceText with
        {
            Commits = [baseline.Commits[0] with { Files = [baseline.Commits[0].Files[0] with { Path = path }] }]
        };
        var renderedPath = WebUtility.HtmlDecode(Regex.Replace(HtmlDetailedReportWriter.Write(fileText), "<[^>]*>", ""));
        Require(renderedPath.Contains(path, StringComparison.Ordinal),
            "Only generated bare rulers may be restyled; recorded multiline file paths must remain intact.");
    }

    private static void Navigation()
    {
        foreach (var snapshot in new[]
        {
            ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.WorkSnapshot(), ReportWriterFixtures.IncidentSnapshot()
        })
        {
            var html = HtmlDetailedReportWriter.Write(snapshot);
            AssertLinks(html);
            var roster = RosterLinks(html);
            Require(roster.Length == snapshot.Request.Roster.Length, "Every Team Roster chip must link to details.");
            for (var index = 0; index < roster.Length; index++)
            {
                var member = snapshot.Request.Roster[index];
                var target = Target(html, roster[index].Href).Text;
                Require(roster[index].Text.Contains(member.Name, StringComparison.Ordinal)
                    && (target.Contains(member.Email!, StringComparison.Ordinal)
                        || snapshot.Request.Analysis == "workItems"
                            && target == $"\U0001F464 {member.DisplayName}: 0 work item(s) completed"
                        || snapshot.Request.Analysis == "gitCommits"
                            && target == $"\U0001F464 {member.DisplayName}: 0 commit"
                        || snapshot.Request.Analysis == "incidentManagement"
                            && Target(html, roster[index].Href).Tag == "tr"
                            && snapshot.Request.Roster.Count(other => other.Name == member.Name) == 1
                            && target.TrimStart().StartsWith(member.DisplayName, StringComparison.Ordinal)),
                    "Roster links must target the correct identity, with compact zero-activity headings and qualification for duplicate names.");
            }
            if (snapshot.Request.Analysis == "incidentManagement")
            {
                var paths = Links(html).Where(link => link.Class == "report-icm-link").ToArray();
                Require(paths.Length > snapshot.Request.Sources.Length, "ICM path summary rows and captured selections need links.");
                foreach (var source in snapshot.Request.Sources)
                    Require(paths.Any(link => link.Text == source.Scope
                        && Target(html, link.Href).Text.Contains(source.Label, StringComparison.Ordinal)),
                        "Each source-qualified ICM selection must link to its first matching group.");
            }
        }

        var git = ReportWriterFixtures.GitSnapshot();
        var pair = git with
        {
            Commits = [git.Commits[0] with { Committer = git.Request.Roster[1] }], PullRequests = []
        };
        var pairedHtml = HtmlDetailedReportWriter.Write(pair);
        var pairedLinks = RosterLinks(pairedHtml);
        Require(pairedLinks[0].Href == pairedLinks[1].Href
            && Target(pairedHtml, pairedLinks[0].Href).Text.StartsWith("\U0001F464 Author:", StringComparison.Ordinal),
            "Both participants must navigate to their shared author/committer detail section.");
        AssertLinks(pairedHtml);

        var prOnly = HtmlDetailedReportWriter.Write(git with { Commits = [], PullRequests = [git.PullRequests[0]] });
        var prTarget = Target(prOnly, RosterLinks(prOnly)[0].Href);
        Require(prTarget.Tag == "h3" && prTarget.Text.EndsWith(": 0 commit", StringComparison.Ordinal)
            && !prOnly.Contains("Completed PR evidence", StringComparison.Ordinal)
            && !Links(prOnly).Any(link => link.Href == git.PullRequests[0].Url),
            "A PR-only member must retain their zero-commit destination without the redundant PR evidence section or links.");
        AssertLinks(prOnly);

        var missing = git.Request.Sources[0] with { Key = "missing-source", Scope = "Unavailable repository" };
        var partialPrOnly = HtmlDetailedReportWriter.Write(git with
        {
            Request = git.Request with { Sources = git.Request.Sources.Add(missing) },
            Commits = [], PullRequests = [git.PullRequests[0]],
            SkippedSources = [new(missing.Key, "HTTP 404")]
        });
        Require(RosterLinks(partialPrOnly).All(link => Target(partialPrOnly, link.Href).Tag == "h3"
                && Target(partialPrOnly, link.Href).Text.EndsWith(": 0 commit", StringComparison.Ordinal))
            && partialPrOnly.Contains("not evidence of zero activity", StringComparison.Ordinal)
            && partialPrOnly.Contains("Person headings count collected commits only.", StringComparison.Ordinal),
            "PR-only members in partial collections need zero collected-commit destinations with explicit unavailable-data disclosures, never removed evidence rows.");
        AssertLinks(partialPrOnly);

        var unresolved = HtmlDetailedReportWriter.Write(git with
        {
            Commits = [git.Commits[0] with { Author = new("Same Name"), Committer = new("Same Name") }],
            PullRequests = []
        });
        var unresolvedRoster = RosterLinks(unresolved);
        Require(unresolvedRoster[0].Href != unresolvedRoster[1].Href
            && !Target(unresolved, unresolvedRoster[0].Href).Text.Contains("Committer:", StringComparison.Ordinal),
            "Ambiguous names cannot redirect roster members to unattributed activity.");
        AssertLinks(unresolved);

        var unavailable = HtmlDetailedReportWriter.Write(git with
        {
            Commits = [], PullRequests = [],
            SkippedSources = git.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "HTTP 404")).ToImmutableArray()
        });
        Require(RosterLinks(unavailable).Length == git.Request.Roster.Length
            && unavailable.Contains("not evidence of zero activity", StringComparison.Ordinal),
            "Unavailable sources must retain usable roster links without fabricated zero activity.");
        AssertLinks(unavailable);

        var incidents = ReportWriterFixtures.IncidentSnapshot();
        var actionOnly = incidents with
        {
            Incidents = [incidents.Incidents.Single(record => record.Id == "5")],
            IncidentActions = incidents.IncidentActions.Where(action => action.IncidentId == "5").ToImmutableArray()
        };
        var actionHtml = HtmlDetailedReportWriter.Write(actionOnly);
        Require(Target(actionHtml, RosterLinks(actionHtml)[0].Href).Text.Contains("alice@example.test", StringComparison.Ordinal)
            && Target(actionHtml, RosterLinks(actionHtml)[0].Href).Tag == "tr",
            "Action-only contributors without owned incidents must navigate to their own overall summary row.");
        AssertLinks(actionHtml);

        var duplicatePaths = incidents with
        {
            Request = incidents.Request with
            {
                Sources = incidents.Request.Sources.Select(source => source with
                {
                    Organization = "", Scope = "Service\\Shared [night] & <team> #100%"
                }).ToImmutableArray()
            }
        };
        var duplicateHtml = HtmlDetailedReportWriter.Write(duplicatePaths);
        var selectionLinks = Links(duplicateHtml).Where(link => link.Class == "report-icm-link"
            && link.Text == duplicatePaths.Request.Sources[0].Scope).ToArray();
        Require(selectionLinks.Select(link => link.Href).Distinct().Count() == 3,
            "Equal path captions must still resolve by source keys, including overlapping memberships.");
        Require(!duplicateHtml.Contains("<team>", StringComparison.Ordinal)
            && !duplicateHtml.Contains("id=\"Service", StringComparison.Ordinal),
            "Path labels must remain encoded; navigation IDs must not contain source text.");
        AssertLinks(duplicateHtml);

        const string hostileName = "Member [x](javascript:alert(1)) <script>bad</script>\n\n\" & #1";
        var hostileMember = HtmlDetailedReportWriter.Write(git with
        {
            Request = git.Request with { Roster = git.Request.Roster.SetItem(0, git.Request.Roster[0] with { Name = hostileName }) }
        });
        Require(RosterLinks(hostileMember).Length == git.Request.Roster.Length
            && RosterLinks(hostileMember)[0].Text.Contains(hostileName, StringComparison.Ordinal)
            && !hostileMember.Contains("<script>", StringComparison.Ordinal)
            && !Regex.IsMatch(hostileMember, @"href=""javascript:", RegexOptions.IgnoreCase),
            "Hostile or multiline member labels must remain complete, encoded and linked without interpreting their text.");
        AssertLinks(hostileMember);

        var empty = HtmlDetailedReportWriter.Write(incidents with { Incidents = [], IncidentActions = [] });
        Require(Links(empty).Count(link => link.Class == "report-icm-link") == incidents.Request.Sources.Length
            && empty.Contains("No collected incident details for this ICM path", StringComparison.Ordinal),
            "Empty ICM selections must link to honest no-collected-detail destinations.");
        AssertLinks(empty);
    }

    private static void IncidentRosterOrdering()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        var alice = baseline.Request.Roster[0] with { Name = "alice Zulu" };
        var bob = baseline.Request.Roster[1] with { Name = "Bob Baker" };
        var zero = baseline.Request.Roster[2] with { Name = "Alice Adams" };
        var aaron = new ReportIdentity("  Aaron West", "aaron@example.test", "aaron-id");
        var zoe = new ReportIdentity("Zoe Adams", "zoe@example.test", "zoe-id");
        ImmutableArray<ReportIdentity> captured = [bob, zoe, alice, zero, aaron];
        var snapshot = baseline with { Request = baseline.Request with { Roster = captured } };
        var html = HtmlDetailedReportWriter.Write(snapshot);
        var roster = RosterLinks(html);
        ReportIdentity[] expected = [aaron, zero, alice, bob, zoe];
        Require(roster.Length == expected.Length, "Sorting must retain every captured member, including zero activity.");
        for (var index = 0; index < expected.Length; index++)
            Require(roster[index].Text.Contains(expected[index].Email!, StringComparison.Ordinal)
                && Target(html, roster[index].Href).Text.Contains(expected[index].DisplayName.Trim(), StringComparison.Ordinal),
                "Incident roster must sort by first name, case-insensitively, with full-name ties and correct identity links.");
        AssertLinks(html);

        var capturedLinks = Links(html).Where(link => link.Class == "report-member-link"
            && !link.Text.StartsWith("\u2022 ", StringComparison.Ordinal)).Select(link => link.Href);
        Require(capturedLinks.SequenceEqual(captured.Select(member =>
                roster.Single(link => link.Text.Contains(member.Email!, StringComparison.Ordinal)).Href))
            && snapshot.Request.Roster.SequenceEqual(captured),
            "Presentation sorting must not reorder captured input context or mutate the request.");
        var markdown = MarkdownReportWriter.Write(snapshot);
        var positions = captured.Select(member => markdown.IndexOf(member.Email!, StringComparison.Ordinal)).ToArray();
        Require(positions.All(position => position >= 0) && positions.SequenceEqual(positions.Order()),
            "Only detailed HTML, not the underlying legacy Markdown output, should sort the incident roster.");

        foreach (var other in new[] { ReportWriterFixtures.GitSnapshot(), ReportWriterFixtures.WorkSnapshot() })
        {
            var unchanged = HtmlDetailedReportWriter.Write(other with { Request = other.Request with { Roster = captured } });
            var links = RosterLinks(unchanged);
            Require(links.Length == captured.Length && links.Select((link, index) =>
                    link.Text.Contains(captured[index].Email!, StringComparison.Ordinal)).All(matches => matches),
                "Git and Work Item roster ordering must remain unchanged.");
        }
    }

    private static void IncidentRecordedUrls()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        foreach (var url in new[]
        {
            "https://example.test/incidents/1",
            "http://example.test/incidents/1)?x=one&y=two#details",
            "https://example.test/incidents/1?label=<tag>|`&name=example"
        })
        {
            var snapshot = baseline with
            {
                Incidents = baseline.Incidents.Select(incident => incident with { Url = url }).ToImmutableArray()
            };
            var html = HtmlDetailedReportWriter.Write(snapshot);
            var fields = RecordedUrlFields(html);
            var expectedHref = new Uri(url).AbsoluteUri.Replace("|", "%7C", StringComparison.Ordinal)
                .Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal);
            Require(fields.Count > 0 && fields.All(field => Links(field.Value) is [var link]
                && link.Text == url && link.Href == expectedHref),
                "Every recorded incident URL must itself be the hyperlink, retaining its exact displayed text and safe destination.");
            Require(!Links(html).Any(link => link.Text == "View Incident"),
                "Teams incident detailed HTML must not retain the redundant View Incident link.");
            AssertLinks(html);
        }
        foreach (var url in new[]
        {
            "", "javascript:alert(1)", "data:text/html,<script>bad</script>", "file:///C:/report.html",
            "https://user@example.test/incidents/1", "https://example.test/\nincident"
        })
        {
            var html = HtmlDetailedReportWriter.Write(baseline with
            {
                Incidents = baseline.Incidents.Select(incident => incident with { Url = url }).ToImmutableArray()
            });
            var fields = RecordedUrlFields(html);
            Require(fields.Count > 0 && fields.All(field => Links(field.Value).Length == 0)
                && !Links(html).Any(link => link.Text == "View Incident"),
                "Missing or unsafe incident URLs must remain text, never actionable links.");
            if (url.Length != 0)
                Require(html.Contains("Unsafe or unsupported URL: retained as text, not linked.", StringComparison.Ordinal),
                    "Rejected incident URLs must keep their existing explicit warning.");
            Require(!html.Contains("<script>", StringComparison.Ordinal), "Recorded URL text must remain encoded.");
        }
        var details = HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } });
        Require(Links(details).Any(link => link.Text == "View Incident")
            && RecordedUrlFields(details).All(field => Links(field.Value).Length == 0)
            && MarkdownReportWriter.Write(baseline).Contains("[View Incident]", StringComparison.Ordinal),
            "Incident Details view and legacy Markdown must retain their existing URL presentation.");

    }

    private static MatchCollection RecordedUrlFields(string html) =>
        Regex.Matches(html, @"<li>\s*<strong>Recorded URL:</strong>\s*(.*?)</li>", RegexOptions.Singleline);

    private static void GitRecordedUrls()
    {
        var baseline = ReportWriterFixtures.GitSnapshot() with { PullRequests = [] };
        foreach (var url in new[]
        {
            "https://example.test/commits/1",
            "http://example.test/commits/1)?x=one&y=two#details",
            "https://example.test/commits/1?label=<tag>|`&name=example"
        })
        {
            var snapshot = baseline with { Commits = baseline.Commits.Select(commit => commit with { Url = url }).ToImmutableArray() };
            var html = HtmlDetailedReportWriter.Write(snapshot);
            var fields = RecordedUrlFields(html);
            var expectedHref = new Uri(url).AbsoluteUri.Replace("|", "%7C", StringComparison.Ordinal)
                .Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal);
            Require(fields.Count == ReportDocumentBuilder.Build(snapshot).Commits.Length
                && fields.All(field => Links(field.Value) is [var link] && link.Text == url && link.Href == expectedHref)
                && !Links(html).Any(link => link.Text == "\U0001F310 URL"),
                "Every Teams Git commit must hyperlink its exact Recorded URL instead of retaining the redundant URL link.");
            AssertLinks(html);
        }
        foreach (var url in new[]
        {
            "", "javascript:alert(1)", "data:text/html,<script>bad</script>", "file:///C:/report.html",
            "https://user@example.test/commits/1", "https://example.test/\ncommit"
        })
        {
            var html = HtmlDetailedReportWriter.Write(baseline with
            {
                Commits = baseline.Commits.Select(commit => commit with { Url = url }).ToImmutableArray()
            });
            var fields = RecordedUrlFields(html);
            Require(fields.Count > 0 && fields.All(field => Links(field.Value).Length == 0)
                && !Links(html).Any(link => link.Text == "\U0001F310 URL") && !html.Contains("<script>", StringComparison.Ordinal),
                "Missing or unsafe commit URLs must remain encoded text, not become actionable links.");
            if (url.Length != 0)
                Require(html.Contains("Unsafe or unsupported URL: retained as text, not linked.", StringComparison.Ordinal),
                    "Rejected commit URLs must retain the existing explicit warning.");
        }
        foreach (var view in new[] { "details", "trends" })
        {
            var html = HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = view } });
            Require(Links(html).Any(link => link.Text == "\U0001F310 URL")
                && RecordedUrlFields(html).All(field => Links(field.Value).Length == 0),
                "Non-Teams Git views must retain their existing URL presentation.");
        }
        Require(MarkdownReportWriter.Write(baseline).Contains("[\U0001F310 URL]", StringComparison.Ordinal)
            && !Links(HtmlDetailedReportWriter.Write(ReportWriterFixtures.GitSnapshot())).Any(link => link.Text.StartsWith("View PR ", StringComparison.Ordinal)),
            "Legacy Markdown commit links remain unchanged while Teams Git omits the redundant completed-PR evidence links.");
    }

    private static void WorkItemRecordedUrls()
    {
        var baseline = ReportWriterFixtures.WorkSnapshot();
        var expectedFields = ReportDocumentBuilder.Build(baseline).WorkItems.Length;
        foreach (var url in new[]
        {
            "https://example.test/workitems/1",
            "http://example.test/workitems/1)?x=one&y=two#details",
            "https://example.test/workitems/1?label=<tag>|`&name=example"
        })
        {
            var snapshot = baseline with
            {
                WorkItems = baseline.WorkItems.Select(item => item with { Url = url }).ToImmutableArray()
            };
            var html = HtmlDetailedReportWriter.Write(snapshot);
            var fields = RecordedUrlFields(html);
            var expectedHref = new Uri(url).AbsoluteUri.Replace("|", "%7C", StringComparison.Ordinal)
                .Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal);
            Require(fields.Count == expectedFields
                && fields.All(field => Links(field.Value) is [var link] && link.Text == url && link.Href == expectedHref)
                && !Links(html).Any(link => link.Text.EndsWith("View Work Item", StringComparison.Ordinal)),
                "Every consolidated work item must hyperlink Recorded URL once, without separate View Work Item links.");
            AssertLinks(html);
        }
        foreach (var url in new[]
        {
            "", "javascript:alert(1)", "data:text/html,<script>bad</script>", "file:///C:/report.html",
            "https://user@example.test/workitems/1", "https://example.test/\nworkitem"
        })
        {
            var html = HtmlDetailedReportWriter.Write(baseline with
            {
                WorkItems = baseline.WorkItems.Select(item => item with { Url = url }).ToImmutableArray()
            });
            var fields = RecordedUrlFields(html);
            Require(fields.Count == expectedFields && fields.All(field => Links(field.Value).Length == 0)
                && !Links(html).Any(link => link.Text.EndsWith("View Work Item", StringComparison.Ordinal))
                && !html.Contains("<script>", StringComparison.Ordinal),
                "Missing or unsafe work-item URLs must remain encoded text in both detail locations.");
            if (url.Length != 0)
                Require(html.Contains("Unsafe or unsupported URL: retained as text, not linked.", StringComparison.Ordinal),
                    "Rejected work-item URLs must retain their existing warning.");
        }
        var details = HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } });
        Require(Links(details).Any(link => link.Text == "View Work Item")
            && RecordedUrlFields(details).All(field => Links(field.Value).Length == 0)
            && MarkdownReportWriter.Write(baseline).Contains("[View Work Item]", StringComparison.Ordinal),
            "Non-Teams Work Items and legacy Markdown must retain their original URL presentation.");
    }

    private static void GitRedundantIdentityFields()
    {
        string[] removed = ["Source-qualified key:", "Source memberships:", "Author identity:", "Committer identity:"];
        var recordedText = string.Join("\n", removed.Select(label => label + " original commit message text"));
        var baseline = ReportWriterFixtures.GitSnapshot();
        var snapshot = baseline with
        {
            Commits = baseline.Commits.Select(commit => commit with { Message = recordedText }).ToImmutableArray()
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var lines = DetailLines(html);
        Require(removed.All(label => !lines.Any(line => line.StartsWith(label, StringComparison.Ordinal))),
            "Only the four redundant footer fields must be omitted from Teams Git detailed commit blocks.");
        foreach (var label in new[] { "Commit ID:", "Organization:", "Repository ID:", "Author date:", "Committer date:" })
            Require(GitMetadataFields(html).Count(field => field.Label == label) == document.Commits.Length,
                "Every commit must retain its remaining footer field: " + label);
        foreach (var label in new[] { "\U0001F464 Author:", "\u270D\uFE0F Committer:" })
            Require(lines.Count(line => line.StartsWith(label, StringComparison.Ordinal)) == document.Commits.Length,
                "The top-of-commit author and committer entries must remain unchanged.");
        var headers = Regex.Matches(html, @"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline)
            .Select(header => Text(header.Groups[1].Value)).ToArray();
        Require(headers.Contains("Source key") && headers.Contains("Identity ID") && !headers.Contains("Source memberships")
            && Text(html).Contains(recordedText, StringComparison.Ordinal),
            "Captured source/identity context and matching recorded messages remain; the removed PR table must not leave its membership column.");
        Require(HtmlSummaryReportWriter.Write(snapshot, "report_Detailed.html")
            == HtmlSummaryReportWriter.Write(baseline, "report_Detailed.html"),
            "Detailed-only field suppression must not change summary HTML.");
        AssertLinks(html);

        var markdown = MarkdownReportWriter.Write(baseline);
        foreach (var label in removed)
            Require(markdown.Contains(label, StringComparison.Ordinal), "Legacy Markdown must retain " + label);
        foreach (var view in new[] { "details", "trends" })
        {
            var other = snapshot with { Request = snapshot.Request with { View = view } };
            var otherLines = DetailLines(HtmlDetailedReportWriter.Write(other));
            foreach (var label in removed)
                Require(otherLines.Count(line => line.StartsWith(label, StringComparison.Ordinal)) == document.Commits.Length,
                    "Non-Teams Git reports must retain every original footer field: " + label);
        }

        static string[] DetailLines(string content) =>
            Regex.Matches(content, @"<div class=""report-line"">(.*?)</div>", RegexOptions.Singleline)
                .Select(line => Text(line.Groups[1].Value)).ToArray();
    }

    private static void GitDetailMetadataIcons()
    {
        var snapshot = ReportWriterFixtures.GitSnapshot();
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var fields = GitMetadataFields(html);
        var expected = new (string Label, string Icon, Func<GitReportRecord, string> Value)[]
        {
            ("Commit ID:", "\U0001F511", commit => commit.Id),
            ("Organization:", "\U0001F3E2", commit => commit.Organization),
            ("Repository ID:", "\U0001F5C2\uFE0F", commit => commit.RepositoryId),
            ("Author date:", "\U0001F5D3\uFE0F", commit => commit.AuthorDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("Committer date:", "\U0001F4C5", commit => commit.CommitterDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        };
        Require(fields.Length == document.Commits.Length * expected.Length,
            "Each Teams Git commit must retain exactly one of each requested metadata field.");
        foreach (var (label, icon, value) in expected)
        {
            var actual = fields.Where(field => field.Label == label).ToArray();
            Require(actual.Length == document.Commits.Length && actual.All(field => field.Icon == icon)
                && actual.Select(field => field.Value).Order(StringComparer.Ordinal).SequenceEqual(
                    document.Commits.Select(commit => value(commit.Entity.Record)).Order(StringComparer.Ordinal)),
                "Metadata icons must preserve each exact value and timestamp: " + label);
        }
        var markdown = MarkdownReportWriter.Write(document);
        foreach (var (label, icon, _) in expected)
            Require(!markdown.Contains(icon + " " + label, StringComparison.Ordinal),
                "Only detailed HTML, not legacy Markdown, should decorate " + label);
        foreach (var view in new[] { "details", "trends" })
        {
            var other = HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = view } });
            var otherFields = GitMetadataFields(other);
            Require(otherFields.Length == fields.Length && otherFields.All(field => field.Icon.Length == 0),
                "Non-Teams Git views must retain undecorated metadata.");
        }
        AssertLinks(html);
    }

    private static (string Icon, string Label, string Value)[] GitMetadataFields(string html) =>
        Regex.Matches(html, @"<div class=""report-line"">(?:<span class=""report-icon"" aria-hidden=""true"">([^<]*)</span> )?"
            + @"<span class=""report-label"">(Commit ID:|Organization:|Repository ID:|Author date:|Committer date:)</span> (.*?)</div>",
            RegexOptions.Singleline)
            .Select(field => (Text(field.Groups[1].Value), Text(field.Groups[2].Value), Text(field.Groups[3].Value))).ToArray();

    private static void IncidentMemberNavigation()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        var unkeyed = baseline.IncidentActions[0] with { ActionId = "", ActionType = "Unkeyed fixture action" };
        var snapshot = baseline with
        {
            IncidentActions = baseline.IncidentActions.AddRange(new[]
            {
                unkeyed, unkeyed with { SourceKey = "source-b" },
                baseline.IncidentActions[0] with { SourceKey = "source-c", SiloId = "silo-b", ActionId = "second-silo" }
            })
        };
        CheckMembers(snapshot);
        CheckMembers(snapshot with { Incidents = snapshot.Incidents.Where(record => record.Id is "1" or "5").ToImmutableArray() });

        var active = new ReportIdentity("Active");
        var namedActive = snapshot with
        {
            Request = snapshot.Request with { Roster = snapshot.Request.Roster.Add(active) },
            Incidents = snapshot.Incidents.Select(record => record.Created ? record with { Owner = active } : record).ToImmutableArray()
        };
        var activeHtml = HtmlDetailedReportWriter.Write(namedActive);
        var activeLink = RosterLinks(activeHtml).Single(link => link.Text == "\u2022 Active");
        Require(Target(activeHtml, activeLink.Href).Text.StartsWith("\U0001F464 Active (", StringComparison.Ordinal)
            && activeHtml.IndexOf("id=\"" + activeLink.Href[1..] + "\"", StringComparison.Ordinal)
                < activeHtml.IndexOf("TEAM INCIDENT DETAILS BY ICM PATH:", StringComparison.Ordinal),
            "A member named Active must link to their earlier individual section, never the global Active statistics.");
        AssertLinks(activeHtml);

        foreach (var html in new[]
        {
            HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } }),
            HtmlSummaryReportWriter.Write(snapshot, "report.html"),
            MarkdownReportWriter.Write(snapshot)
        })
            Require(!html.Contains("Created ICMs (", StringComparison.Ordinal)
                && !html.Contains("Contributed ICMs (", StringComparison.Ordinal),
                "The new member detail buckets must be limited to Teams incident detailed HTML.");

        void CheckMembers(ReportSnapshot current)
        {
            var document = ReportDocumentBuilder.Build(current);
            var html = HtmlDetailedReportWriter.Write(document);
            AssertLinks(html);
            var roster = RosterLinks(html);
            var individualsEnd = html.IndexOf("TEAM INCIDENT DETAILS BY ICM PATH:", StringComparison.Ordinal);
            var context = ReportFoldMarkup.Parse(html).Single(fold => fold.Kind == "context");
            for (var index = 0; index < current.Request.Roster.Length; index++)
            {
                var member = current.Request.Roster[index];
                var link = roster.Single(link => link.Text.Contains(member.Email!, StringComparison.Ordinal));
                var row = document.OverallRows.Single(row => row.Contributor.Contains(member.Email!, StringComparison.Ordinal));
                var target = Target(html, link.Href);
                var position = html.IndexOf("id=\"" + link.Href[1..] + "\"", StringComparison.Ordinal);
                var ownsIncidents = document.Incidents.Any(incident => incident.Entity.Record.Owner.Email == member.Email);
                Require(position < context.Start && target.Tag == (ownsIncidents ? "h3" : "tr")
                    && (!ownsIncidents || position < individualsEnd),
                    "Roster links must point to existing earlier individuals or overall summary rows, not recreate later sections.");
                if (!ownsIncidents)
                {
                    var summaryRow = Regex.Match(html, @"<tr\b[^>]*id=""" + Regex.Escape(link.Href[1..]) + @"""[^>]*>(.*?)</tr>", RegexOptions.Singleline);
                    var cells = Regex.Matches(summaryRow.Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)
                        .Select(cell => Text(cell.Groups[1].Value)).ToArray();
                    Require(cells.Skip(1).SequenceEqual(new[] { row.Active, row.Created, row.Mitigated, row.Resolved, row.Contributed }
                        .Select(count => count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                        "A contributor without owned incidents retains all recorded credits in their linked overall row.");
                }
            }
        }
    }

    private static void IncidentDetailConsolidation()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        foreach (var snapshot in new[]
        {
            baseline,
            baseline with { Incidents = [], IncidentActions = [] },
            baseline with { Incidents = baseline.Incidents.Where(record => record.Id is "1" or "5").ToImmutableArray() },
            baseline with { Incidents = baseline.Incidents.Select(record => record with { Active = true }).ToImmutableArray() },
            baseline with
            {
                Request = baseline.Request with { Sources = [baseline.Request.Sources[0]] },
                Incidents = baseline.Incidents.Where(record => record.SourceKey == baseline.Request.Sources[0].Key).ToImmutableArray(),
                IncidentActions = baseline.IncidentActions.Where(action => action.SourceKey == baseline.Request.Sources[0].Key).ToImmutableArray()
            }
        })
        {
            var document = ReportDocumentBuilder.Build(snapshot);
            var html = HtmlDetailedReportWriter.Write(document);
            Require(!Regex.IsMatch(html, @"<th\b[^>]*>\s*Incident record\s*</th>", RegexOptions.IgnoreCase),
                "Incident Record tables must be absent from every Teams detailed section.");
            var context = ReportFoldMarkup.Parse(html).Single(fold => fold.Kind == "context");
            var statisticsStart = html.IndexOf("<h2>Incident statistics</h2>", StringComparison.Ordinal);
            Require(statisticsStart >= 0 && statisticsStart < context.Start
                && Regex.IsMatch(html[(context.Start + context.Length)..], @"^\s*</main>\s*</body>\s*</html>\s*$"),
                "Captured context must follow the full Incident statistics region and be the final section.");
            var before = html[..statisticsStart];
            var statistics = html[statisticsStart..context.Start];
            Require(!statistics.Contains("report-record-heading", StringComparison.Ordinal)
                && !statistics.Contains("Source-qualified key:", StringComparison.Ordinal)
                && !statistics.Contains("Visible incident metadata", StringComparison.Ordinal)
                && !statistics.Contains("Mitigation text", StringComparison.Ordinal)
                && !statistics.Contains("<h2>Incident details</h2>", StringComparison.Ordinal)
                && RecordedUrlFields(statistics).Count == 0,
                "The final statistics region must not repeat complete incident records or metadata.");
            Require(statistics.Contains("Distinct incident totals and weighted duration statistics", StringComparison.Ordinal)
                && !html.Contains("Recorded incident action history and contribution evidence", StringComparison.Ordinal)
                && !html.Contains("Several recorded actions on one incident count as one Contributed incident for that actor.", StringComparison.Ordinal)
                && statistics.Contains("<h2>Incident statistics</h2>", StringComparison.Ordinal),
                "Removing the later individuals must retain global Active and distinct overall statistics.");
            var totalsStart = statistics.IndexOf("<h2>Distinct incident totals and weighted duration statistics</h2>", StringComparison.Ordinal);
            var activeStatistics = statistics[..totalsStart];
            var multipleTeams = snapshot.Request.Sources.Select(source => source.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var activeTableHeadings = multipleTeams
                ? new[] { "Severity counts", "TTD by severity", "Active severity by owning team", "Active TTD by owning team and severity" }
                : new[] { "Severity counts", "TTD by severity" };
            Require(Regex.Matches(activeStatistics, @"<h4>(.*?)</h4>").Select(heading => Text(heading.Groups[1].Value))
                    .SequenceEqual(activeTableHeadings)
                && Regex.Matches(activeStatistics, "<table>").Count == activeTableHeadings.Length,
                "Teams global Active statistics must retain severity and TTD tables, with no TTM/TTR heading or table.");
            Require(Regex.Matches(statistics[totalsStart..], @"<h4>(.*?)</h4>").Select(heading => Text(heading.Groups[1].Value))
                    .SequenceEqual(new[] { "Severity counts", "TTD by severity", "TTM by severity", "TTR by severity" })
                && Regex.Matches(statistics[totalsStart..], "<table>").Count == 4,
                "Removing Active TTM/TTR must preserve all overall severity and duration tables.");
            Require(Regex.Matches(activeStatistics, @"<h3\b[^>]*>(.*?)</h3>").Select(heading => Text(heading.Groups[1].Value))
                    .SequenceEqual(new[] { "Active" })
                && activeStatistics.Contains("Active TTD by owning team and severity", StringComparison.Ordinal) == multipleTeams
                && !statistics.Contains("<details", StringComparison.Ordinal)
                && !html.Contains("Created ICMs (", StringComparison.Ordinal)
                && !html.Contains("Contributed ICMs (", StringComparison.Ordinal)
                && !html.Contains("Recorded actions for incident", StringComparison.Ordinal),
                "Only global Active statistics may precede overall totals; removed individual sections must not reappear elsewhere.");
            Require(!Regex.IsMatch(ReportFoldMarkup.OutsidePersons(html), @"<th\b[^>]*>\s*Action ID\s*</th>"),
                "No global action-history table may remain outside the member sections.");
            var actionRows = Regex.Matches(html, @"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline)
                .Where(table => Regex.Matches(table.Value, @"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline)
                    .Any(header => Text(header.Groups[1].Value) == "Action ID"))
                .Sum(table => Regex.Matches(table.Value, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)
                    .Count(row => row.Value.Contains("<td", StringComparison.Ordinal)));
            Require(actionRows == 0,
                "Removed individual sections must not leave orphaned or relocated action-history tables.");
            Require(Regex.Matches(before, "Source-qualified key:").Count == document.Incidents.Length
                && Regex.Matches(before, "Visible incident metadata").Count == document.Incidents.Length
                && Regex.Matches(before, "Mitigation text").Count == document.Incidents.Length
                && RecordedUrlFields(before).Count == document.Incidents.Length,
                "Every distinct incident's supplemental evidence must be retained exactly once in the earlier categories.");
            foreach (var record in document.Incidents.Select(incident => incident.Entity.Record))
            {
                Require(Text(before).Contains(record.EntityKey, StringComparison.Ordinal)
                    && Text(before).Contains(record.Mitigation, StringComparison.Ordinal)
                    && record.Metadata.All(field => Text(before).Contains(field.Key, StringComparison.Ordinal)
                        && Text(before).Contains(field.Value, StringComparison.Ordinal)),
                    "Consolidation must not remove recorded metadata, mitigation or silo-qualified identity.");
            }
            foreach (var link in Links(statistics).Where(link => link.Class == "report-incident-link"))
                Require(Target(before, link.Href).Text.StartsWith("Incident Details:", StringComparison.Ordinal),
                    "References in the statistics region must resolve to real earlier incident headings.");
            AssertLinks(html);
        }
        Require(MarkdownReportWriter.Write(baseline).Contains("## Incident details", StringComparison.Ordinal)
            && HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } })
                .Contains("<h2>Incident details</h2>", StringComparison.Ordinal),
            "Legacy Markdown and non-Teams Incident Details must retain their original full appendix.");
        Require(MarkdownReportWriter.Write(baseline).Contains("## Recorded incident action history and contribution evidence", StringComparison.Ordinal)
            && HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } })
                .Contains("<h2>Recorded incident action history and contribution evidence</h2>", StringComparison.Ordinal),
            "Removing the Teams global table must not remove the original action-history section from other formats or views.");
        var legacyActive = Regex.Match(MarkdownReportWriter.Write(baseline), @"### Active\n(.*?)(?=\n### |\z)", RegexOptions.Singleline).Value;
        var detailsActive = Regex.Match(HtmlDetailedReportWriter.Write(baseline with { Request = baseline.Request with { View = "details" } }),
            @"<h3>Active</h3>(.*?)(?=<h[23]\b|</main>)", RegexOptions.Singleline).Value;
        Require(legacyActive.Contains("#### TTM by severity", StringComparison.Ordinal)
            && legacyActive.Contains("#### TTR by severity", StringComparison.Ordinal)
            && detailsActive.Contains("<h4>TTM by severity</h4>", StringComparison.Ordinal)
            && detailsActive.Contains("<h4>TTR by severity</h4>", StringComparison.Ordinal),
            "Legacy Markdown and Incident Details must retain TTM/TTR in their Active sections.");
    }

    private static void IncidentActiveTeamLabels()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        foreach (var (tenant, team) in new[]
        {
            ("Tenant", "Shared team"),
            ("Tenant (silo literal)", "Team <safe> & (silo literal)"),
            ("", "")
        })
        {
            var snapshot = baseline with
            {
                Incidents = baseline.Incidents.Where(record => record.Active).Select(record => record with
                {
                    OwningTenant = tenant, OwningTeam = team
                }).ToImmutableArray(),
                IncidentActions = []
            };
            var document = ReportDocumentBuilder.Build(snapshot);
            var html = HtmlDetailedReportWriter.Write(document);
            var expectedLabel = $"{(tenant.Length == 0 ? "-" : tenant)}\\{(team.Length == 0 ? "-" : team)}";
            var severity = Table("Active severity by owning team");
            var durations = Table("Active TTD by owning team and severity");
            Require(severity.Length == 2 && durations.Length == 2 && document.ActiveTeamStatistics.Length == 2,
                "Removing the displayed silo suffix must not merge same-named teams in different silos.");
            Require(severity.All(row => row[0] == expectedLabel) && durations.All(row => row[0] == expectedLabel),
                "Both Active team tables must render captured tenant/team labels without the generated silo suffix.");
            Require(severity.All(row => row.Skip(1).SequenceEqual(new[] { "2", "1" }))
                && durations[0].Skip(1).SequenceEqual(new[] { "2", "1", "1.0 hours", "1.0 hours", "1.0 hours" })
                && durations[1].Skip(1).SequenceEqual(new[] { "2", "0", "-", "-", "-" }),
                "Label-only changes must preserve counts, valid durations and missing-duration semantics.");
            var severityLinks = Links(TableMarkup(html, "Active severity by owning team"));
            var durationLinks = Links(TableMarkup(html, "Active TTD by owning team and severity"));
            Require(severityLinks.Length == 2 && severityLinks.SequenceEqual(durationLinks)
                && severityLinks.All(link => link.Class == "report-icm-link" && link.Text == expectedLabel)
                && severityLinks.Select(link => link.Href).Distinct(StringComparer.Ordinal).Count() == 2,
                "Both tables must link same-caption owning teams to matching, distinct silo-qualified ICM-path destinations.");
            AssertLinks(html);
            var details = HtmlDetailedReportWriter.Write(snapshot with { Request = snapshot.Request with { View = "details" } });
            Require(!details.Contains("<h4>Active severity by owning team</h4>", StringComparison.Ordinal)
                && !details.Contains("<h4>Active TTD by owning team and severity</h4>", StringComparison.Ordinal),
                "The multi-team statistics tables must remain exclusive to Teams reports.");
            var legacy = MarkdownReportWriter.Write(document);
            Require(legacy.Contains(@"\(silo silo-a\)", StringComparison.Ordinal)
                && legacy.Contains(@"\(silo silo-b\)", StringComparison.Ordinal)
                && !legacy.Contains("report-source-reference-", StringComparison.Ordinal)
                && !html.Contains("<safe>", StringComparison.Ordinal),
                "Legacy Markdown keeps unlinked silo annotations and captured label text must remain encoded.");

            string[][] Table(string heading)
            {
                return Regex.Matches(TableMarkup(html, heading), @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)
                    .Select(row => Regex.Matches(row.Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)
                        .Select(cell => Text(cell.Groups[1].Value)).ToArray())
                    .Where(row => row.Length != 0).ToArray();
            }
        }

        static string TableMarkup(string html, string heading)
        {
            var match = Regex.Match(html, @"<h4>" + Regex.Escape(heading)
                + @"</h4>\s*<div\b[^>]*>\s*<table>(.*?)</table>", RegexOptions.Singleline);
            Require(match.Success, "Both Active team statistics tables must remain present.");
            return match.Groups[1].Value;
        }
    }

    private sealed record ReportLink(string Href, string Class, string Text);

    private static ReportLink[] Links(string html) => Regex.Matches(html, @"<a\b([^>]*)>(.*?)</a>", RegexOptions.Singleline)
        .Select(match => new ReportLink(Attribute(match.Groups[1].Value, "href"),
            Attribute(match.Groups[1].Value, "class"), Text(match.Groups[2].Value))).ToArray();

    private static ReportLink[] RosterLinks(string html) => Links(html)
        .Where(link => link.Class == "report-member-link" && link.Text.StartsWith("\u2022 ", StringComparison.Ordinal)).ToArray();

    private static string Attribute(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, @"\b" + name + @"=""([^""]*)""").Groups[1].Value);

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static (string Tag, string Text) Target(string html, string href)
    {
        Require(href.StartsWith("#", StringComparison.Ordinal), "Report navigation must remain within the offline file.");
        var match = Regex.Match(html, @"<(h[1-6]|tr)\b[^>]*\bid=""" + Regex.Escape(href[1..])
            + @"""[^>]*>(.*?)</\1>", RegexOptions.Singleline);
        Require(match.Success, "A navigation destination must be an existing heading or evidence row.");
        return (match.Groups[1].Value, Text(match.Groups[2].Value));
    }

    private static void AssertLinks(string html)
    {
        var ids = Regex.Matches(html, @"\bid=""([^""]*)""").Select(match => match.Groups[1].Value).ToArray();
        Require(ids.Length == ids.Distinct(StringComparer.Ordinal).Count(), "Navigation IDs must be unique.");
        foreach (var link in Links(html).Where(link => link.Href.StartsWith("#", StringComparison.Ordinal)))
        {
            Require(Regex.IsMatch(link.Href, @"^#report-(member|icm|incident)-[0-9]+$"),
                "Internal navigation must resolve to generated safe identifiers, never an unresolved placeholder.");
            Target(html, link.Href);
        }
    }

    private static void CheckArchive(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var archive = ReportArchiveBuilder.Build(snapshot);
        var analysis = snapshot.Request.Analysis switch
        {
            "gitCommits" => "Git_Commits",
            "workItems" => "Work_Items",
            "incidentManagement" => "Incident_Resolutions",
            _ => throw new InvalidOperationException("Unexpected archive test analysis.")
        };
        var stem = $"{analysis}_{snapshot.Request.View}_{snapshot.Request.Start:yyyyMMdd}_to_{snapshot.Request.End:yyyyMMdd}";
        var archiveStem = $"{stem}_{snapshot.Request.RunId:N}";
        Require(archive.FileName == archiveStem + ".zip"
            && ReportArchiveBuilder.GetFileName(snapshot.Request) == archive.FileName,
            "ZIP naming and Save As suggestions must preserve the run GUID for every supported analysis/view.");
        using var content = archive.Content;
        using var zip = new ZipArchive(content, ZipArchiveMode.Read);
        Require(zip.Entries.Count == 2 && zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal)
            .SetEquals([stem + ".html", stem + "_Detailed.html"]),
            "The ZIP must contain exactly two standalone HTML files named by analysis/view/dates, without the run GUID.");
        var detailed = Read(zip.GetEntry(stem + "_Detailed.html"));
        var summary = Read(zip.GetEntry(stem + ".html"));
        var previousSummary = HtmlSummaryReportWriter.Write(document, archiveStem + "_Detailed.html");
        Require(summary == previousSummary.Replace("./" + Uri.EscapeDataString(archiveStem + "_Detailed.html"),
                "./" + Uri.EscapeDataString(stem + "_Detailed.html"), StringComparison.Ordinal),
            "The existing summary must remain identical except for its GUID-free detail-link target.");
        Require(detailed == HtmlDetailedReportWriter.Write(document)
            && detailed.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal)
            && detailed.Contains("<h1>", StringComparison.Ordinal) && detailed.Contains("<table>", StringComparison.Ordinal)
            && detailed.Contains("<strong>", StringComparison.Ordinal),
            "Details must render headings, tables and emphasis as HTML, not display raw Markdown.");
        Require(detailed.Contains("Captured manager / report roster", StringComparison.Ordinal)
               == !(snapshot.Request.IsTeams && snapshot.Request.Analysis == "workItems")
            && summary.Contains("Captured roster:", StringComparison.Ordinal)
            && MarkdownReportWriter.Write(document).Contains("Captured manager / report roster", StringComparison.Ordinal),
            "Only Teams Work Items detailed HTML must omit the duplicate captured roster; other views, analyses, summary HTML and legacy Markdown retain it.");

        var text = WebUtility.HtmlDecode(Regex.Replace(detailed, "<[^>]*>", "")).Replace("\r\n", "\n", StringComparison.Ordinal);
        void Contains(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Require(text.Contains(value.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal),
                    "Detailed HTML omitted recorded content.");
        }
        Contains(document.Title);
        Contains(snapshot.Request.RunId.ToString("D"));
        foreach (var source in snapshot.Request.Sources) { Contains(source.Key); Contains(source.Scope); }
        foreach (var member in snapshot.Request.Roster) Contains(member.Name);
        foreach (var item in snapshot.WorkItems)
        {
            Contains(item.Title);
            foreach (var revision in item.History) Contains(revision.State);
        }
        foreach (var commit in snapshot.Commits)
        {
            Contains(commit.Id);
            Contains(commit.Message);
            foreach (var file in commit.Files) Contains(file.Path);
        }
        if (snapshot.Request.IsTeams && snapshot.Request.Analysis == "gitCommits")
            Require(!text.Contains("Completed PR evidence", StringComparison.Ordinal),
                "Teams Git archives must omit the repeated completed-PR evidence section.");
        else
            foreach (var pr in snapshot.PullRequests) Contains(pr.Title);
        foreach (var incident in snapshot.Incidents) { Contains(incident.Title); Contains(incident.Mitigation); }
        if (!(snapshot.Request.IsTeams && snapshot.Request.Analysis == "incidentManagement"))
            foreach (var action in snapshot.IncidentActions) { Contains(action.ActionId); Contains(action.ActionType); }
    }

    private static void Safety()
    {
        var baseline = ReportWriterFixtures.GitSnapshot();
        const string hostile = "<script>alert('x')</script>\n<img src=x onerror=alert(1)>\n```text\n[x](javascript:alert(1))\n& | Ω";
        var snapshot = baseline with
        {
            Commits = [baseline.Commits[0] with { Message = hostile, Url = "javascript:alert(1)" }],
            PullRequests = []
        };
        var html = HtmlDetailedReportWriter.Write(snapshot);
        Require(!Regex.IsMatch(html, @"<(script|img|iframe|object|embed)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(html, @"href\s*=\s*[""'](?:javascript|data|file):", RegexOptions.IgnoreCase)
            && html.Contains("&lt;script&gt;", StringComparison.Ordinal)
            && html.Contains("<pre><code", StringComparison.Ordinal),
            "Source text must remain encoded and complete, not become executable HTML or embedded resources.");
        Require(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "")).Contains(hostile, StringComparison.Ordinal),
            "Safe rendering must not remove hostile-looking recorded text.");
    }

    private static void LimitsAndCancellation()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => HtmlDetailedReportWriter.Write(baseline, cancellation.Token));
        var large = baseline with
        {
            Request = baseline.Request with { View = "details", Sources = [baseline.Request.Sources[0]] },
            IncidentActions = [],
            Incidents = Enumerable.Range(100, 10).Select(id => baseline.Incidents[0] with
            {
                Id = id.ToString(), Mitigation = new string('<', 900_000), Active = false
            }).ToImmutableArray()
        };
        Require(Encoding.UTF8.GetByteCount(MarkdownReportWriter.Write(large)) < ReportRenderingLimits.MaximumOutputBytes,
            "The HTML-expansion regression must start with Markdown below the output limit.");
        Throws<ReportSizeLimitException>(() => HtmlDetailedReportWriter.Write(large));
        var repeatedDetails = baseline with
        {
            Incidents = Enumerable.Range(100, 14).Select(id => baseline.Incidents[0] with
            {
                Id = id.ToString(), Mitigation = new string('x', 900_000)
            }).ToImmutableArray(),
            IncidentActions = Enumerable.Range(100, 14).Select(id => baseline.IncidentActions[0] with
            {
                IncidentId = id.ToString(), ActionId = "activity-" + id
            }).ToImmutableArray()
        };
        Require(Encoding.UTF8.GetByteCount(MarkdownReportWriter.Write(repeatedDetails)) < ReportRenderingLimits.MaximumOutputBytes,
            "The former per-member expansion fixture must fit without repeated metadata and mitigation.");
        var consolidated = HtmlDetailedReportWriter.Write(repeatedDetails);
        Require(Regex.Matches(consolidated, "Mitigation text").Count == 14
            && Encoding.UTF8.GetByteCount(consolidated) < ReportRenderingLimits.MaximumOutputBytes,
            "Removing repeated incident blocks must allow this formerly oversized export to retain each full mitigation once.");
    }

    private static string Read(ZipArchiveEntry? entry)
    {
        Require(entry is not null, "Expected named HTML ZIP entry is missing.");
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
