using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class IncidentActiveFoldingTests
{
    public static void Run()
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        VerifyIndividualOrder(baseline);
        var first = baseline.Incidents[0];
        var additions = new[]
        {
            first with { Id = "11", Owner = new("alice"), Title = "Alias-owned active incident", Created = false },
            first with { Id = "12", Owner = baseline.Request.Roster[1], Title = "Equal name, different owner", Created = false },
            first with { Id = "13", Owner = new("Unknown"), Title = "Unknown owning contact", Created = false }
        };
        var snapshot = baseline with
        {
            Incidents = baseline.Incidents.AddRange(additions.SelectMany(item =>
                new[] { item, item with { SourceKey = "source-b" } }))
        };
        Check(snapshot);
        Check(snapshot with { Request = snapshot.Request with { Sources = snapshot.Request.Sources.Reverse().ToImmutableArray() } });
        var folds = ReportFoldMarkup.Parse(HtmlDetailedReportWriter.Write(snapshot));
        var queues = folds.Where(fold => fold.Kind == "queue" && fold.ParentStart is null
            && Summary(fold).StartsWith("\U0001F7E2 Active", StringComparison.Ordinal)).ToArray();
        Require(queues.Length == 3 && queues.Count(queue => Text(queue.Content).Contains("Active incidents: 0", StringComparison.Ordinal)) == 1,
            "Keep source-membership groups and an explicit zero-active queue without duplicating overlapping selections.");
        var populated = queues.Single(queue => Summary(queue) == "\U0001F7E2 Active incidents: 4");
        var owners = folds.Where(fold => fold.Kind == "owner" && fold.ParentStart == populated.Start).ToArray();
        Require(owners.Length == 3 && owners.Count(owner => Summary(owner).Contains("alice@example.test", StringComparison.Ordinal)) == 1
            && owners.Count(owner => Summary(owner).Contains("bob@example.test", StringComparison.Ordinal)) == 1,
            "Aliases must share an owner fold while equal display names with distinct identities remain qualified and separate.");
        Require(owners.Single(owner => Summary(owner).Contains("alice@example.test", StringComparison.Ordinal)).Content
            .Contains("Alias-owned active incident", StringComparison.Ordinal),
            "An alias-only owner must join the captured identity, not form a separate name-based group.");
        Require(owners.Any(owner => Summary(owner).Contains("Unknown", StringComparison.Ordinal)),
            "Unavailable owning contacts remain explicit, not guessed from another person's name.");

        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Active = false }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Created = item.Active, Active = false }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Created = false }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Created = true }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Mitigated = false }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Mitigated = true }).ToImmutableArray() });
        var mitigated = snapshot with
        {
            Incidents = snapshot.Incidents.Select(item => item with
            {
                Mitigated = item.Active, Active = false, Created = false,
                MitigatedBy = snapshot.Request.Roster[2], MitigatedAt = snapshot.Request.Start.AddHours(2)
            }).ToImmutableArray()
        };
        Check(mitigated);
        var mitigationRows = ReportDocumentBuilder.Build(mitigated).OverallRows;
        Require(mitigationRows.Single(row => row.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).Mitigated == 5
            && mitigationRows.Sum(row => row.Mitigated) == 5,
            "Contact grouping must not transfer mitigation credit from the recorded mitigation actor to the current owner.");
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Resolved = false }).ToImmutableArray() });
        Check(snapshot with { Incidents = snapshot.Incidents.Select(item => item with { Resolved = true }).ToImmutableArray() });
        var resolved = snapshot with
        {
            Incidents = snapshot.Incidents.Select(item => item with
            {
                Resolved = item.Active, Active = false, Created = false, Mitigated = false,
                ResolvedBy = snapshot.Request.Roster[2], ResolvedAt = snapshot.Request.Start.AddHours(3)
            }).ToImmutableArray()
        };
        Check(resolved);
        var resolutionRows = ReportDocumentBuilder.Build(resolved).OverallRows;
        Require(resolutionRows.Single(row => row.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).Resolved == 5
            && resolutionRows.Sum(row => row.Resolved) == 5,
            "Contact grouping must not transfer resolution credit from the recorded resolution actor to the current owner.");
        var actionOnly = baseline.Incidents.Single(item => item.Id == "5");
        var unkeyed = new IncidentActionReportRecord("source-a", "5", "silo-a", "",
            baseline.Request.Roster[1], baseline.Request.Start.AddMinutes(3), "Updated");
        var contributed = snapshot with
        {
            Incidents = snapshot.Incidents.AddRange(new[]
            {
                actionOnly with { SourceKey = "source-b" },
                actionOnly with { SourceKey = "source-c", SiloId = "silo-b", Title = "Same ID, different silo" },
                actionOnly with { Id = "99", Owner = baseline.Request.Roster[2], Title = "No contribution evidence" }
            }),
            IncidentActions = snapshot.IncidentActions.AddRange(new[]
            {
                unkeyed, unkeyed with { SourceKey = "source-b" },
                unkeyed with { SourceKey = "source-c", SiloId = "silo-b" },
                unkeyed with { ActionId = "extra-action", Actor = new("alice") }
            })
        };
        Check(contributed);
        var contributionRows = ReportDocumentBuilder.Build(contributed).OverallRows;
        Require(contributionRows.Single(row => row.Contributor.Contains("bob@example.test", StringComparison.Ordinal)).Contributed == 3
            && contributionRows.Single(row => row.Contributor.Contains("Zero Activity", StringComparison.Ordinal)).Contributed == 0
            && contributionRows.Sum(row => row.Contributed) == 6,
            "Repeated incidents must retain actor/incident credit, silo qualification and null-ID actions without crediting owners lacking evidence.");
        try
        {
            HtmlDetailedReportWriter.Write(contributed with
            {
                IncidentActions = contributed.IncidentActions.Add(unkeyed with { ActionId = "out-of-period", Timestamp = snapshot.Request.Start.AddTicks(-1) })
            });
            throw new InvalidOperationException("Out-of-period contributor evidence was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("timestamp outside the captured period", StringComparison.Ordinal)) { }
        Check(snapshot with { Incidents = [], IncidentActions = [] });
        Check(snapshot with
        {
            Incidents = [], IncidentActions = [],
            SkippedSources = snapshot.Request.Sources.Select(source => new ReportSkippedSource(source.Key, "Unavailable")).ToImmutableArray()
        });
        foreach (var (url, destination) in new (string Url, string? Destination)[]
        {
            ("https://example.test/incident/1?a=one&b=two#section", "https://example.test/incident/1?a=one&b=two#section"),
            ("http://example.test/a|b<angle>`tick`", "http://example.test/a%7Cb%3Cangle%3E%60tick%60"),
            ("javascript:alert(1)", null), ("data:text/html,<script>alert(1)</script>", null),
            ("https://user:secret@example.test/", null), ("https://example.test/\ncontrol", null), ("", null)
        })
            Check(snapshot with
            {
                Incidents = snapshot.Incidents.Select(item => item with
                {
                    Title = "Title <img src=x> & [text](javascript:alert(1)) | `code`\nFull second line",
                    Url = url
                }).ToImmutableArray()
            }, urlOverride: true, destination);

        var longTitle = new string('x', 500_000) + " FULL TITLE END";
        var large = baseline with
        {
            Incidents = [first with { Title = longTitle, Created = true, Mitigated = true, Resolved = true }], IncidentActions = []
        };
        Check(large);
        Check(contributed with
        {
            Incidents = contributed.Incidents.Select(item => item with { Title = longTitle }).ToImmutableArray()
        });
    }

    private static void VerifyIndividualOrder(ReportSnapshot baseline)
    {
        var alpha = new ReportIdentity("Alpha Owner", "alpha@example.test", "alpha");
        var beta = new ReportIdentity("Beta Owner", "beta@example.test", "beta");
        var zulu = new ReportIdentity("Zulu Owner", "zulu@example.test", "zulu");
        var records = Enumerable.Range(1, 14).Select(id => baseline.Incidents[0] with
        {
            Id = id.ToString(CultureInfo.InvariantCulture),
            Owner = id <= 2 ? alpha : id <= 4 ? beta : zulu
        }).ToArray();
        var snapshot = baseline with
        {
            Request = baseline.Request with { Roster = [zulu, beta, alpha], RosterParentIndices = [] },
            Incidents = [.. records, .. records.Select(record => record with { SourceKey = "source-b" })],
            IncidentActions = []
        };
        var document = ReportDocumentBuilder.Build(snapshot);
        var html = HtmlDetailedReportWriter.Write(document);
        var folds = ReportFoldMarkup.Parse(html);
        CheckIndividuals(document, html, folds);
        var owners = folds.Where(fold => fold.Kind == "owner" && fold.ParentStart is null);
        Require(owners.Select(Summary).SequenceEqual(new[]
        {
            "\U0001F464 Zulu Owner (10 incident(s))",
            "\U0001F464 Alpha Owner (2 incident(s))",
            "\U0001F464 Beta Owner (2 incident(s))"
        }), "Individuals must sort numerically by distinct incidents, not names, roster order, source repetitions or overlapping categories; ties retain name order.");
        var active = folds.Single(fold => fold.Kind == "queue" && fold.ParentStart is null
            && Summary(fold).StartsWith("\U0001F7E2 Active", StringComparison.Ordinal));
        Require(folds.Where(fold => fold.Kind == "owner" && fold.ParentStart == active.Start).Select(Summary)
            .SequenceEqual(new[]
            {
                "\U0001F464 Owning Contact: Alpha Owner (2 incident(s))",
                "\U0001F464 Owning Contact: Beta Owner (2 incident(s))",
                "\U0001F464 Owning Contact: Zulu Owner (10 incident(s))"
            }), "Sorting individual sections must not reorder owning contacts within the ICM-path categories.");
    }

    private static void Check(ReportSnapshot snapshot, bool urlOverride = false, string? destination = null)
    {
        var document = ReportDocumentBuilder.Build(snapshot);
        var summary = HtmlSummaryReportWriter.Write(document, "report_Detailed.html");
        var legacy = MarkdownReportWriter.Write(document);
        var html = HtmlDetailedReportWriter.Write(document);
        var folds = ReportFoldMarkup.Parse(html);
        CheckIndividuals(document, html, folds);
        var allQueues = folds.Where(fold => fold.Kind == "queue" && fold.ParentStart is null).ToArray();
        var queues = allQueues.Where(fold => !Summary(fold).StartsWith("\U0001F4AC Contributed Incidents:", StringComparison.Ordinal)).ToArray();
        var contributedQueues = allQueues.Except(queues).ToArray();
        var sourceOrder = snapshot.Request.Sources.Select((source, index) => (source.Key, index))
            .ToDictionary(source => source.Key, source => source.index);
        var groups = document.Incidents.GroupBy(item => string.Join(",", item.Entity.SourceKeys.Select(key => sourceOrder[key])))
            .OrderBy(group => group.First().Entity.SourceKeys.Min(key => sourceOrder[key]))
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        (string Label, string Name, Func<IncidentReportRecord, bool> Select)[] categories =
        [
            ("\U0001F7E2 Active", "active", record => record.Active),
            ("\U0001F195 Created", "created", record => record.Created),
            ("\U0001F527 Mitigated", "mitigated", record => record.Mitigated),
            ("\u2705 Resolved", "resolved", record => record.Resolved)
        ];
        Require(queues.Length == groups.Length * categories.Length && queues.All(queue => queue.ParentStart is null),
            "Each captured ICM source group needs independent, initially collapsed Active, Created, Mitigated and Resolved disclosures.");
        Require(contributedQueues.Length == groups.Length && contributedQueues.All(queue => queue.ParentStart is null),
            "Every source group also needs an independent, initially collapsed Contributed Incidents section.");
        for (var index = 0; index < groups.Length; index++)
            CheckContributionCategory(document, contributedQueues[index], groups[index].ToArray(), folds);
        for (var index = 0; index < groups.Length; index++)
        for (var categoryIndex = 0; categoryIndex < categories.Length; categoryIndex++)
        {
            var queue = queues[index * categories.Length + categoryIndex];
            var category = categories[categoryIndex];
            var items = groups[index].Where(item => category.Select(item.Entity.Record)).ToArray();
            Require(Summary(queue) == category.Label + " incidents: " + items.Length,
                "A queue heading must count distinct source-qualified category records, not another category's membership.");
            var owners = folds.Where(fold => fold.Kind == "owner" && fold.ParentStart == queue.Start).ToArray();
            var expected = items.GroupBy(item => OwnerKey(item.Entity.Record.Owner), StringComparer.Ordinal).ToArray();
            Require(owners.Length == expected.Length
                && !queue.Content.Contains("Other selected", StringComparison.Ordinal)
                && !queue.Content.Contains("<details class=\"report-queue-details\">", StringComparison.Ordinal)
                && !queue.Content.Contains("<h2", StringComparison.Ordinal),
                "Only the queue's selected category and its owning-contact groups belong inside the disclosure.");
            if (items.Length == 0)
                Require(Text(queue.Content).Contains($"No {category.Name} incidents in this queue in the collected snapshot.", StringComparison.Ordinal),
                    "An empty category needs an explicit explanation, not an invented owner.");
            for (var ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++)
            {
                var records = expected[ownerIndex].Select(item => item.Entity.Record).ToArray();
                var owner = owners.Single(fold => Summary(fold).Contains(expected[ownerIndex].Key, StringComparison.Ordinal));
                Require(Summary(owner).StartsWith("\U0001F464 Owning Contact: ", StringComparison.Ordinal)
                    && Summary(owner).EndsWith($" ({records.Length} incident(s))", StringComparison.Ordinal),
                    "Each contact summary retains its identity-qualified name and exact category count.");
                var headings = Headings(owner.Content);
                Require(headings.Select(heading => heading.Title).SequenceEqual(records.Select(record => "Incident Details: " + record.Title)),
                    "Owner groups must preserve every incident's complete title and existing record order.");
                for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
                {
                    var text = Text(headings[recordIndex].Body);
                    Require(text.Contains("ID: " + records[recordIndex].Id + "\n", StringComparison.Ordinal)
                        && text.Contains("Silo: " + records[recordIndex].SiloId + "\n", StringComparison.Ordinal),
                        "Identical incident IDs in different silos must retain their own source-qualified record.");
                }
            }
        }
        Require(folds.Where(fold => fold.Kind == "owner" && fold.ParentStart is not null)
            .All(owner => allQueues.Any(queue => queue.Start == owner.ParentStart)),
            "Contact and contributor folds belong to their own category, never an unrelated person or summary.");

        var compact = Headings(html);
        var expectedOccurrences = document.Incidents.Sum(item =>
        {
            var record = item.Entity.Record;
            var membershipCount = (record.Active ? 1 : 0) + (record.Created ? 1 : 0)
                + (record.Mitigated ? 1 : 0) + (record.Resolved ? 1 : 0);
            return Math.Max(1, membershipCount)
                + (membershipCount != 0 ? membershipCount : Math.Max(1, ContributorKeys(document, item).Length));
        });
        Require(compact.Length == expectedOccurrences,
            "Every compact category occurrence needs a titled incident header without losing overlapping categories.");
        foreach (var heading in compact)
        {
            var record = document.Incidents.First(item => heading.Title == "Incident Details: " + item.Entity.Record.Title).Entity.Record;
            var url = Regex.Match(heading.Body, @"<div class=""report-line"">\s*URL:\s*(.*?)</div>", RegexOptions.Singleline);
            Require(url.Success, "Every compact incident retains its URL field.");
            var link = Regex.Match(url.Groups[1].Value, @"^<a href=""([^""]+)"">(.*?)</a>$", RegexOptions.Singleline);
            if ((urlOverride ? destination : record.Url) is { } safe)
            {
                Require(link.Success && WebUtility.HtmlDecode(link.Groups[1].Value) == safe
                    && Text(link.Groups[2].Value) == record.Url,
                    "A compact URL must be clickable at its recorded value using the established safe destination rules.");
            }
            else
                Require(!link.Success && (string.IsNullOrEmpty(record.Url)
                    || heading.Body.Contains("Unsafe or unsupported URL: retained as text, not linked.", StringComparison.Ordinal)),
                    "Unsafe URLs remain encoded text with an explicit warning, never clickable executable content.");
        }
        Require(!Regex.IsMatch(html, @"<(?:script|img|iframe)\b", RegexOptions.IgnoreCase),
            "Incident and contact text must never become executable markup.");
        Require(!html.Contains("<code>recorded-title</code>", StringComparison.Ordinal),
            "Repeated title references must render complete recorded text, never their internal placeholder.");
        Require(html.Contains("<h3>Active</h3>", StringComparison.Ordinal)
            && !html.Contains("Recorded incident action history and contribution evidence", StringComparison.Ordinal)
            && html.Contains("TTD by severity", StringComparison.Ordinal),
            "Preserve global Active statistics without the redundant global recorded-action table.");
        Require(legacy == MarkdownReportWriter.Write(document) && !legacy.Contains("Active incidents:", StringComparison.Ordinal)
            && !legacy.Contains("Created incidents:", StringComparison.Ordinal)
            && !legacy.Contains("Mitigated incidents:", StringComparison.Ordinal)
            && !legacy.Contains("Resolved incidents:", StringComparison.Ordinal)
            && !legacy.Contains("Contributed Incidents:", StringComparison.Ordinal)
            && !legacy.Contains("TEAM INCIDENT DETAILS BY INDIVIDUALS:", StringComparison.Ordinal)
            && !legacy.Contains("Incident Details: " + snapshot.Incidents.FirstOrDefault()?.Title + "  \n", StringComparison.Ordinal)
            && summary == HtmlSummaryReportWriter.Write(document, "report_Detailed.html")
            && !summary.Contains("TEAM INCIDENT DETAILS BY INDIVIDUALS:", StringComparison.Ordinal)
            && !summary.Contains("report-queue-details", StringComparison.Ordinal),
            "Detailed-only presentation must leave legacy Markdown, summary HTML and metrics unchanged.");
        var detailsSnapshot = snapshot with { Request = snapshot.Request with { View = "details", RosterParentIndices = [] } };
        if (snapshot.IncidentActions.All(action => action.ActionId.Length != 0))
        {
            var details = HtmlDetailedReportWriter.Write(detailsSnapshot);
            Require(!details.Contains("<details class=\"report-queue-details\"", StringComparison.Ordinal)
                && !details.Contains("Incident Details:", StringComparison.Ordinal),
                "Incident Details view must not acquire the Teams compact layout or active queue folds.");
        }
        else
        {
            try
            {
                HtmlDetailedReportWriter.Write(detailsSnapshot);
                throw new InvalidOperationException("Details unexpectedly accepted an action without a history ID.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("incomplete source-qualified identity", StringComparison.Ordinal)) { }
        }
        using var stream = new StringWriter();
        HtmlDetailedReportWriter.WriteTo(document, stream);
        Require(stream.ToString() == html, "Streamed and buffered exports must retain identical complete titles, links and nested disclosures.");
    }

    private static void CheckIndividuals(ReportDocument document, string html, ReportFoldMarkup.Fold[] folds)
    {
        var start = html.IndexOf("TEAM INCIDENT DETAILS BY INDIVIDUALS:", StringComparison.Ordinal);
        var pathStart = html.IndexOf("TEAM INCIDENT DETAILS BY ICM PATH:", StringComparison.Ordinal);
        Require(start > html.IndexOf("TEAM ROSTER", StringComparison.Ordinal) && pathStart > start
            && Regex.Matches(html, @"<h2\b[^>]*>(.*?)</h2>", RegexOptions.Singleline)
                .Any(heading => Text(heading.Groups[1].Value).EndsWith("TEAM INCIDENT DETAILS BY INDIVIDUALS:", StringComparison.Ordinal)),
            "The boxed individuals section must follow the roster and precede the ICM-path section.");
        var section = html[start..pathStart];
        var owners = folds.Where(fold => fold.Kind == "owner" && fold.ParentStart is null).ToArray();
        var expected = document.Incidents.GroupBy(item => OwnerKey(item.Entity.Record.Owner), StringComparer.Ordinal).ToArray();
        Require(owners.Length == expected.Length && owners.All(owner => owner.Start > start
            && owner.Start + owner.Length < pathStart),
            "All paths must combine into one independent collapsed group per captured owner, without inventing zero-activity owners.");
        var counts = owners.Select(owner => int.Parse(
            Regex.Match(Summary(owner), @"\(([\d,]+) incident\(s\)\)$").Groups[1].Value,
            NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        Require(counts.SequenceEqual(expected.Select(group => group.Count()).OrderDescending()),
            "Individual sections must be ordered by descending silo-qualified incident count across all selected paths.");
        Require(!section.Contains("Visible incident metadata", StringComparison.Ordinal)
            && !section.Contains("Mitigation text", StringComparison.Ordinal),
            "Individual browsing must not multiply the supplemental full recorded evidence.");
        (string Label, Func<IncidentReportRecord, bool> Select)[] categories =
        [
            ("\U0001F7E2 Active incidents", record => record.Active),
            ("\U0001F195 Created incidents", record => record.Created),
            ("\U0001F527 Mitigated incidents", record => record.Mitigated),
            ("\u2705 Resolved incidents", record => record.Resolved),
            ("\U0001F4AC Contributed Incidents", record => !record.Active && !record.Created && !record.Mitigated && !record.Resolved)
        ];
        foreach (var group in expected)
        {
            var owner = owners.Single(fold => Summary(fold).Contains(group.Key, StringComparison.Ordinal)
                || Summary(fold) == $"\U0001F464 {group.First().Entity.Record.Owner.DisplayName} ({group.Count()} incident(s))");
            Require(Summary(owner).StartsWith("\U0001F464 ", StringComparison.Ordinal)
                && !Summary(owner).StartsWith("\U0001F464 Owning Contact: ", StringComparison.Ordinal)
                && Summary(owner).EndsWith($"({group.Count()} incident(s))", StringComparison.Ordinal),
                "Individual headers show the icon, qualified name and distinct incident count without the Owning Contact prefix.");
            var actual = folds.Where(fold => fold.Kind == "queue" && fold.ParentStart == owner.Start).ToArray();
            Require(actual.Length == categories.Length && !owner.Content.Contains("Contributor:", StringComparison.Ordinal),
                "Each owning contact needs all five collapsed categories, not another set of contributor groups.");
            var closed = document.IncidentGroups.Skip(1).SingleOrDefault(candidate => ClosedMemberKey(candidate) == group.Key);
            CheckIndividualStatistics(owner.Content[..owner.Content.IndexOf("<details class=\"report-queue-details\">", StringComparison.Ordinal)], closed);
            for (var index = 0; index < categories.Length; index++)
            {
                var records = group.Where(item => categories[index].Select(item.Entity.Record)).Select(item => item.Entity.Record).ToArray();
                Require(Summary(actual[index]) == $"{categories[index].Label}: {records.Length} incident(s)",
                    "Individuals use the existing category flags without moving mitigation, resolution or contribution credit.");
                var headings = Headings(actual[index].Content);
                Require(headings.Select(heading => heading.Title).SequenceEqual(records.Select(record => "Incident Details: " + record.Title)),
                    "Each owner/category retains every incident from all paths in recorded order, without source or action duplication.");
                if (records.Length == 0)
                    Require(actual[index].Content.Contains("No incidents in this category for this owning contact in the collected snapshot.", StringComparison.Ordinal),
                        "An empty owner category needs explicit collected-data zero semantics.");
                for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
                {
                    var record = records[recordIndex];
                    var body = headings[recordIndex].Body;
                    Require(Text(body).Contains("ID: " + record.Id + "\n", StringComparison.Ordinal)
                        && Text(body).Contains("Silo: " + record.SiloId + "\n", StringComparison.Ordinal)
                        && Text(body).Contains("Owning Team Name: " + record.OwningTeam + "\n", StringComparison.Ordinal),
                        "Cross-path individual records must preserve incident ID, silo and actual owning team.");
                    var link = Regex.Match(body, @"<a\b[^>]*href=""(#report-incident-[0-9]+)""[^>]*>\s*Full recorded evidence\s*</a>");
                    Require(link.Success && html.IndexOf("id=\"" + link.Groups[1].Value[1..] + "\"", StringComparison.Ordinal) > pathStart,
                        "Each individual record needs an offline link to its canonical ICM-path evidence.");
                }
            }
        }
        Require(Regex.Matches(html, "Distinct closed incidents in group:").Count
            == owners.Length && folds.All(fold => fold.Kind != "person"),
            "Closed summaries belong only to the earlier individuals; no later personal sections may remain or be recreated.");
        if (document.Incidents.IsEmpty)
            Require(section.Contains(document.Snapshot.AllSourcesSkipped
                ? "No selected source could be collected. Activity is unavailable, not zero."
                : "No incidents in the completed snapshot.", StringComparison.Ordinal),
                "An empty individuals section must distinguish successful-empty from unavailable source data.");
    }

    private static string ClosedMemberKey(IncidentContributorGroup group)
    {
        var record = group.Incidents[0].Entity.Record;
        return OwnerKey(record.ResolvedAt is not null ? record.ResolvedBy : record.MitigatedBy);
    }

    private static void CheckIndividualStatistics(string section, IncidentContributorGroup? closed)
    {
        var incidents = closed?.Incidents ?? [];
        var statistics = closed?.SeverityStatistics ?? [];
        foreach (var (label, count) in new[]
        {
            ("Distinct closed incidents in group", incidents.Length),
            ("Currently resolved", incidents.Count(incident => incident.Entity.Record.Status == "Resolved")),
            ("Currently mitigated", incidents.Count(incident => incident.Entity.Record.Status == "Mitigated"))
        })
            Require(Regex.IsMatch(section, @"<strong>" + label + @":</strong>\s*" + count + @"</li>"),
                "Relocated closed/status counts must use the same actor-attributed, deduplicated incidents.");
        Require(Regex.Matches(section, @"<h4>(.*?)</h4>").Select(heading => Text(heading.Groups[1].Value))
            .SequenceEqual(new[] { "Severity counts", "TTD by severity", "TTM by severity", "TTR by severity" }),
            "All four existing statistic tables must be directly under the individual before the incident categories.");
        var tables = Regex.Matches(section, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline)
            .Select(table => Regex.Matches(table.Groups[1].Value, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)
                .Select(row => Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)
                    .Select(cell => Text(cell.Groups[1].Value)).ToArray()).ToArray()).ToArray();
        // Empty Markdown tables have no tbody, but must retain all four headers.
        Require(Regex.Matches(section, "<table>").Count == 4
            && (statistics.IsEmpty ? tables.All(table => table.Length == 0) : tables.Length == 4),
            "Zero closed groups retain empty statistic tables, not missing tables or fabricated durations.");
        if (statistics.IsEmpty)
        {
            Require(section.Contains("No closed incidents are attributed to this individual in the collected snapshot.", StringComparison.Ordinal),
                "A missing closed group must be explicit within the individual's summary.");
            return;
        }
        Require(tables.All(table => table.Length == statistics.Length),
            "Each statistic table must preserve every severity, including unknown severity.");
        for (var index = 0; index < statistics.Length; index++)
        {
            var severity = statistics[index];
            var label = severity.Severity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
            Require(tables[0][index].SequenceEqual(new[] { label, severity.Count.ToString(CultureInfo.InvariantCulture) }),
                "Severity counts must match the existing closed contributor group rather than current ownership.");
            var durations = new[] { severity.Detect, severity.Mitigate, severity.Resolve };
            for (var metric = 0; metric < durations.Length; metric++)
            {
                var duration = durations[metric];
                var row = tables[metric + 1][index];
                Require(row.Length == 8 && row[0] == label && row[1] == duration.Count.ToString(CultureInfo.InvariantCulture)
                    && row.Skip(5).SequenceEqual(new[] { duration.Minimum, duration.Average, duration.Maximum }
                        .Select(value => value?.ToString("F1", CultureInfo.InvariantCulture) ?? "-")),
                    "TTD/TTM/TTR must preserve known-duration denominators and weighted min/average/max, with missing values shown as unavailable.");
            }
        }
    }

    private static void CheckContributionCategory(ReportDocument document, ReportFoldMarkup.Fold category,
        IncidentDocument[] sourceIncidents, ReportFoldMarkup.Fold[] folds)
    {
        var incidents = sourceIncidents.Where(item => !item.Entity.Record.Active && !item.Entity.Record.Created
            && !item.Entity.Record.Mitigated && !item.Entity.Record.Resolved).ToArray();
        Require(Summary(category) == $"\U0001F4AC Contributed Incidents: {incidents.Length} incident(s)",
            "Renamed category counts must use distinct incidents, not the sum of repeated contributor group memberships.");
        Require(category.Content.Contains("without adding activity to report totals", StringComparison.Ordinal)
            && !category.Content.Contains("Other selected", StringComparison.Ordinal)
            && !category.Content.Contains("<details class=\"report-queue-details\">", StringComparison.Ordinal)
            && !category.Content.Contains("<h2", StringComparison.Ordinal),
            "Only the renamed category and its contribution groups belong inside this disclosure, with repetition explained.");
        var expected = incidents.SelectMany(incident => ContributorKeys(document, incident).Select(key => (Key: key, Incident: incident)))
            .GroupBy(item => item.Key).ToArray();
        var actual = folds.Where(fold => fold.Kind == "owner" && fold.ParentStart == category.Start).ToArray();
        var withoutEvidence = incidents.Where(incident => ContributorKeys(document, incident).Length == 0).ToArray();
        Require(actual.Length == expected.Length + (withoutEvidence.Length == 0 ? 0 : 1),
            "Aliases and multiple actions merge into one contributor group; evidence-free incidents stay in a separate explicit bucket.");
        foreach (var group in expected)
        {
            var contributor = actual.Single(fold => Summary(fold).StartsWith("\U0001F464 Contributor:", StringComparison.Ordinal)
                && Summary(fold).Contains(group.Key, StringComparison.Ordinal));
            Require(Summary(contributor).EndsWith($"({group.Count()} incident(s))", StringComparison.Ordinal),
                "Each contributor heading needs its own distinct incident count.");
            CheckRecords(contributor, group.Select(item => item.Incident).ToArray());
        }
        if (withoutEvidence.Length > 0)
        {
            var missing = actual.Single(fold => Summary(fold).Contains("No recorded in-period contributors", StringComparison.Ordinal));
            Require(missing.Content.Contains("not attributed to their owners or counted as contributions", StringComparison.Ordinal),
                "Missing evidence must be disclosed without inventing a contributor or dropping the incident.");
            CheckRecords(missing, withoutEvidence);
        }
        if (incidents.Length == 0)
            Require(category.Content.Contains("No incidents in this category in the collected snapshot.", StringComparison.Ordinal),
                "Empty Contributed categories need explicit selected-category zero semantics.");

        static void CheckRecords(ReportFoldMarkup.Fold fold, IncidentDocument[] records)
        {
            var headings = Headings(fold.Content);
            Require(headings.Select(heading => heading.Title).SequenceEqual(records.Select(item => "Incident Details: " + item.Entity.Record.Title)),
                "Each actual contributor must receive the complete ordered incident list, allowing repetitions across different contributors only.");
            for (var index = 0; index < headings.Length; index++)
            {
                var text = Text(headings[index].Body);
                Require(text.Contains("ID: " + records[index].Entity.Record.Id + "\n", StringComparison.Ordinal)
                    && text.Contains("Silo: " + records[index].Entity.Record.SiloId + "\n", StringComparison.Ordinal),
                    "Repeated incident display must retain both the recorded incident ID and its silo.");
            }
        }
    }

    private static string[] ContributorKeys(ReportDocument document, IncidentDocument incident) => document.IncidentActions
        .Where(action => action.Record.IncidentKey == incident.Entity.Record.EntityKey)
        .Select(action => OwnerKey(action.Record.Actor)).Distinct(StringComparer.Ordinal).ToArray();

    private static (string Title, string Body)[] Headings(string html)
    {
        var headings = Regex.Matches(html, @"<h[456]\b[^>]* class=""report-line report-record-heading"">(.*?)</h[456]>", RegexOptions.Singleline);
        return headings.Select((heading, index) => (Text(heading.Groups[1].Value),
            html[(heading.Index + heading.Length)..(index + 1 < headings.Count ? headings[index + 1].Index : html.Length)])).ToArray();
    }

    private static string Summary(ReportFoldMarkup.Fold fold) =>
        Text(Regex.Match(fold.Content, @"^\s*<summary>(.*?)</summary>", RegexOptions.Singleline).Groups[1].Value).Trim();
    private static string OwnerKey(ReportIdentity owner) => owner.Email ?? (owner.Name == "alice" ? "alice@example.test" : owner.Name);
    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Incident active folding: " + message);
    }
}
