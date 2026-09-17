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
        IncidentMemberActivityDetails();
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
                Require(roster[index].Text.Contains(member.Name, StringComparison.Ordinal)
                    && Target(html, roster[index].Href).Text.Contains(member.Email!, StringComparison.Ordinal),
                    "Roster links must target the correct qualified identity, including duplicate names and zero activity.");
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
            && Target(pairedHtml, pairedLinks[0].Href).Text.Contains("Contributor:", StringComparison.Ordinal),
            "Both participants must navigate to their shared author/committer detail section.");
        AssertLinks(pairedHtml);

        var prOnly = HtmlDetailedReportWriter.Write(git with { Commits = [], PullRequests = [git.PullRequests[0]] });
        var prTarget = Target(prOnly, RosterLinks(prOnly)[0].Href);
        Require(prTarget.Tag == "tr" && prTarget.Text.Contains(git.PullRequests[0].Title, StringComparison.Ordinal),
            "A PR-only roster member must link to actual closedBy evidence, not a zero-commit placeholder.");
        AssertLinks(prOnly);

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
            && actionHtml.Contains("no separate closed-incident detail group", StringComparison.Ordinal),
            "Action-only contributors must navigate to their existing member details, not another actor's closed incidents.");
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
                && Target(html, roster[index].Href).Text.Contains(expected[index].Email!, StringComparison.Ordinal),
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

        static MatchCollection RecordedUrlFields(string html) =>
            Regex.Matches(html, @"<li>\s*<strong>Recorded URL:</strong>\s*(.*?)</li>", RegexOptions.Singleline);
    }

    private static void IncidentMemberActivityDetails()
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
        Require(IncidentKeys(Bucket(MemberRegion(activeHtml, activeLink.Href), "Created", 1)).SequenceEqual(["silo-a:1"]),
            "A member named Active must have personal Created details, not be mistaken for the global Active section.");
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
            string[][] createdKeys = [["silo-a:1"], [], []];
            string[][] contributedKeys = [["silo-a:1", "silo-a:5", "silo-b:1"], ["silo-a:1"], []];
            for (var index = 0; index < current.Request.Roster.Length; index++)
            {
                var member = current.Request.Roster[index];
                var link = roster.Single(link => link.Text.Contains(member.Email!, StringComparison.Ordinal));
                var region = MemberRegion(html, link.Href);
                var row = document.OverallRows.Single(row => row.Contributor.Contains(member.Email!, StringComparison.Ordinal));
                var created = Bucket(region, "Created", row.Created);
                var contributed = Bucket(region, "Contributed", row.Contributed);
                Require(IncidentKeys(created).SequenceEqual(createdKeys[index])
                    && IncidentKeys(contributed).SequenceEqual(contributedKeys[index]),
                    "Member details must exactly match Created/Contributed credits, distinguishing identities and silos while deduplicating source overlap.");
                if (index == 0)
                {
                    Require(Text(created).Contains("NotTheOwner", StringComparison.Ordinal)
                        && Text(created).Contains(current.Incidents[0].Mitigation, StringComparison.Ordinal)
                        && Links(created).Any(link => link.Text == current.Incidents[0].Url),
                        "Created details must retain full fields, mitigation and URL, crediting the current owner rather than creator metadata.");
                    var actions = Regex.Matches(contributed, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)
                        .Select(row => Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)
                            .Select(cell => Text(cell.Groups[1].Value)).ToArray())
                        .Where(cells => cells.Length == 8).ToArray();
                    Require(actions.Select(cells => cells[3]).Order(StringComparer.Ordinal).SequenceEqual(
                            new[] { "Not provided", "action-1", "action-2", "action-4", "action-6", "second-silo" })
                        && actions.All(cells => !cells[4].Contains("bob@example.test", StringComparison.Ordinal)),
                        "Contributed details must include only that member's recorded actions, retaining missing IDs and alias/source deduplication.");
                }
                if (index == 2)
                    Require(Text(created).Contains("No Created incidents", StringComparison.Ordinal)
                        && Text(contributed).Contains("No Contributed incidents", StringComparison.Ordinal),
                        "Zero-activity members need explicit empty buckets, not another member's incident details.");
            }
        }

        static string MemberRegion(string html, string href)
        {
            var match = Regex.Match(html, @"<h3\b[^>]*\bid=""" + Regex.Escape(href[1..])
                + @"""[^>]*>.*?</h3>(.*?)(?=<h[23]\b|</main>)", RegexOptions.Singleline);
            Require(match.Success, "The roster link must lead to a real per-member detail section.");
            return match.Groups[1].Value;
        }

        static string Bucket(string region, string kind, int count)
        {
            var match = Regex.Match(region, @"<h4\b[^>]*>" + kind + @" ICMs \(" + count
                + @"\)</h4>(.*?)(?=<h[234]\b|$)", RegexOptions.Singleline);
            Require(match.Success, $"Each member needs a {kind} bucket matching the existing count.");
            return match.Groups[1].Value;
        }

        static IEnumerable<string> IncidentKeys(string bucket) => Regex.Matches(bucket,
                @"<li>\s*<strong>(?:<span\b[^>]*>)?Source-qualified key:(?:</span>)?</strong>\s*(.*?)</li>", RegexOptions.Singleline)
            .Select(match => Text(match.Groups[1].Value)).Order(StringComparer.Ordinal);
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
            Require(Regex.IsMatch(link.Href, @"^#report-(member|icm)-[0-9]+$"),
                "Internal navigation must resolve to generated safe identifiers, never an unresolved placeholder.");
            Target(html, link.Href);
        }
    }

    private static void CheckArchive(ReportSnapshot snapshot)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var archive = ReportArchiveBuilder.Build(snapshot);
        var stem = Path.GetFileNameWithoutExtension(archive.FileName);
        using var content = archive.Content;
        using var zip = new ZipArchive(content, ZipArchiveMode.Read);
        Require(zip.Entries.Count == 2 && zip.Entries.All(entry => entry.Name.EndsWith(".html", StringComparison.Ordinal)),
            "The ZIP must replace, not accompany, the Markdown entry with HTML.");
        var detailed = Read(zip.GetEntry(stem + "_Detailed.html"));
        var summary = Read(zip.GetEntry(stem + ".html"));
        var previousSummary = HtmlSummaryReportWriter.Write(document, stem + ".md");
        Require(summary == previousSummary.Replace("./" + Uri.EscapeDataString(stem + ".md"),
                "./" + Uri.EscapeDataString(stem + "_Detailed.html"), StringComparison.Ordinal),
            "The existing summary must remain identical except for its detail-link target.");
        Require(detailed == HtmlDetailedReportWriter.Write(document)
            && detailed.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal)
            && detailed.Contains("<h1>", StringComparison.Ordinal) && detailed.Contains("<table>", StringComparison.Ordinal)
            && detailed.Contains("<strong>", StringComparison.Ordinal),
            "Details must render headings, tables and emphasis as HTML, not display raw Markdown.");

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
        foreach (var pr in snapshot.PullRequests) Contains(pr.Title);
        foreach (var incident in snapshot.Incidents) { Contains(incident.Title); Contains(incident.Mitigation); }
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
            "The per-member expansion fixture must fit before adding Created and Contributed copies.");
        Throws<ReportSizeLimitException>(() => HtmlDetailedReportWriter.Write(repeatedDetails));
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
