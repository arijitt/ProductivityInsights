using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ProductivityInsights.Services.Reports;

internal static class DetailedReportPresentation
{
    internal static readonly object RosterHierarchyKey = new();
    internal static readonly object PersonSectionStartKey = new();
    internal static readonly object PersonSectionEndKey = new();
    internal static readonly object ItemSectionStartKey = new();
    internal static readonly object ItemSectionEndKey = new();
    internal static readonly object ItemHeadingTitleKey = new();
    internal static readonly object ContextSectionStartKey = new();
    internal static readonly object ContextSectionEndKey = new();
    internal static readonly object QueueSectionStartKey = new();
    internal static readonly object QueueSectionEndKey = new();
    internal static readonly object OwnerSectionStartKey = new();
    internal static readonly object OwnerSectionEndKey = new();
    internal static readonly object RecordHeadingLevelKey = new();
    internal static readonly object ItemFieldsKey = new();

    internal static void Configure(HtmlRenderer renderer, string markdown, CancellationToken token, bool workItemDetails,
        bool featureRegistryDetails = false)
    {
        renderer.ObjectRenderers.Insert(0, new ReportDocumentRenderer(token));
        renderer.ObjectRenderers.Insert(0, new ReportParagraphRenderer(markdown, token, workItemDetails, featureRegistryDetails));
        renderer.ObjectRenderers.Insert(0, new ReportLiteralRenderer(workItemDetails, featureRegistryDetails));
        renderer.ObjectRenderers.Insert(0, new ReportTableRenderer());
    }

    private sealed class ReportDocumentRenderer(CancellationToken token) : HtmlObjectRenderer<MarkdownDocument>
    {
        protected override void Write(HtmlRenderer renderer, MarkdownDocument document)
        {
            foreach (var block in document)
            {
                token.ThrowIfCancellationRequested();
                var kind = block.GetData(PersonSectionStartKey) is true ? "person"
                    : block.GetData(ItemSectionStartKey) is true ? "item"
                    : block.GetData(ContextSectionStartKey) is true ? "context"
                    : block.GetData(QueueSectionStartKey) is true ? "queue"
                    : block.GetData(OwnerSectionStartKey) is true ? "owner" : null;
                if (kind is not null)
                {
                    renderer.WriteLine("<details class=\"report-" + kind + "-details\">");
                    renderer.WriteLine("<summary>");
                    renderer.Write(block);
                    renderer.WriteLine("</summary>");
                    renderer.WriteLine("<div class=\"report-" + kind + "-content\">");
                }
                else renderer.Write(block);
                if (block.GetData(ItemSectionEndKey) is true)
                    renderer.WriteLine("</div></details>");
                if (block.GetData(PersonSectionEndKey) is true)
                    renderer.WriteLine("</div></details>");
                if (block.GetData(ContextSectionEndKey) is true)
                    renderer.WriteLine("</div></details>");
                if (block.GetData(OwnerSectionEndKey) is true)
                    renderer.WriteLine("</div></details>");
                if (block.GetData(QueueSectionEndKey) is true)
                    renderer.WriteLine("</div></details>");
            }
        }
    }

    private sealed class ReportTableRenderer : HtmlTableRenderer
    {
        protected override void Write(HtmlRenderer renderer, Table table)
        {
            renderer.WriteLine("<div class=\"report-table-scroll\" role=\"region\" aria-label=\"Report data table\" tabindex=\"0\">");
            base.Write(renderer, table);
            renderer.WriteLine("</div>");
        }
    }

    private sealed class ReportParagraphRenderer(string markdown, CancellationToken token, bool workItemDetails,
        bool featureRegistryDetails) : ParagraphRenderer
    {
        protected override void Write(HtmlRenderer renderer, ParagraphBlock paragraph)
        {
            token.ThrowIfCancellationRequested();
            if (renderer.ImplicitParagraph || paragraph.Parent is not MarkdownDocument || paragraph.Inline is null)
            {
                base.Write(renderer, paragraph);
                return;
            }
            List<List<Inline>> lines = [[]];
            for (var inline = paragraph.Inline.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                token.ThrowIfCancellationRequested();
                if (inline is LineBreakInline) lines.Add([]);
                else lines[^1].Add(inline);
            }
            while (lines.Count > 0 && lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
            if (paragraph.GetData(ItemSectionStartKey) is true || paragraph.GetData(RecordHeadingLevelKey) is int)
            {
                if (lines.Count != 1 || LineClass(PlainText(lines[0]).TrimStart()) != "report-record-heading")
                    throw new InvalidOperationException("The detailed report could not isolate an item's folding heading.");
                WriteReportLine(renderer, lines[0], paragraph.GetData(ItemHeadingTitleKey) as ReadOnlyMemory<char>?,
                    paragraph.GetData(RecordHeadingLevelKey) is int level ? level : 4, paragraph);
                return;
            }
            if (paragraph.GetData(RosterHierarchyKey) is ImmutableArray<int?> parents)
            {
                WriteRosterHierarchy(renderer, lines, parents);
                return;
            }
            if (lines.Count >= 3 && IsRule(lines[0], '\u2550') && IsRule(lines[^1], '\u2550'))
            {
                var heading = PlainText(lines[1]).TrimStart();
                var person = heading.StartsWith("\U0001F464", StringComparison.Ordinal)
                    || heading.StartsWith(GitReportPresentation.CommitterIcon, StringComparison.Ordinal)
                    || heading.StartsWith("\U0001F4E6", StringComparison.Ordinal);
                var tag = person ? "h3" : "h2";
                renderer.Write("<" + tag);
                renderer.WriteAttributes(paragraph);
                renderer.Write(" class=\"" + (person ? "report-person-heading" : "report-section-heading") + "\">");
                for (var index = 1; index < lines.Count - 1; index++)
                {
                    if (index > 1) renderer.Write("<br />");
                    WriteLine(renderer, lines[index]);
                }
                renderer.WriteLine("</" + tag + ">");
                return;
            }
            if (paragraph.GetData(ItemFieldsKey) is not true
                && !(featureRegistryDetails && lines.Any(line => PlainText(line).TrimStart()
                    .StartsWith("\U0001F33F Source branch:", StringComparison.Ordinal)))
                && !lines.Any(line => IsRule(line, '\u2500') || featureRegistryDetails && IsRule(line, '\u2501')
                    || LineClass(PlainText(line).TrimStart()) is not null))
            {
                base.Write(renderer, paragraph);
                return;
            }
            renderer.WriteLine("<div class=\"report-lines\">");
            foreach (var line in lines)
                WriteReportLine(renderer, line);
            renderer.WriteLine("</div>");
        }

        private void WriteReportLine(HtmlRenderer renderer, List<Inline> line, ReadOnlyMemory<char>? headingTitle = null, int headingLevel = 4,
            ParagraphBlock? headingBlock = null)
        {
            token.ThrowIfCancellationRequested();
            if (featureRegistryDetails && IsRule(line, '\u2501'))
            {
                renderer.WriteLine("<hr class=\"report-rule report-pr-divider\" style=\"border-top:3px solid currentColor;color:inherit;opacity:1\" />");
                return;
            }
            if (IsRule(line, '\u2500'))
            {
                renderer.WriteLine("<hr class=\"report-rule\" />");
                return;
            }
            var modifier = LineClass(PlainText(line).TrimStart());
            var tag = modifier == "report-record-heading" ? "h" + headingLevel : "div";
            renderer.Write("<" + tag);
            if (headingBlock is not null) renderer.WriteAttributes(headingBlock);
            renderer.Write(" class=\"report-line" + (modifier is null ? "" : " " + modifier) + "\">");
            WriteLine(renderer, line);
            if (headingTitle is { } title)
            {
                renderer.Write(' ');
                renderer.WriteEscape(title.Span);
            }
            renderer.WriteLine("</" + tag + ">");
        }

        private void WriteRosterHierarchy(HtmlRenderer renderer, List<List<Inline>> lines, ImmutableArray<int?> parents)
        {
            if (parents.IsEmpty || lines.Count < parents.Length + 2 || !IsRule(lines[parents.Length + 1], '\u2500'))
                throw new InvalidOperationException("The detailed roster hierarchy could not preserve every captured member.");
            renderer.WriteLine("<div class=\"report-lines\">");
            renderer.Write("<div class=\"report-line\">");
            WriteLine(renderer, lines[0]);
            renderer.WriteLine("</div>");
            renderer.WriteLine("<ul class=\"report-roster-hierarchy\" aria-label=\"Captured reporting hierarchy\" style=\"list-style:none;padding-left:0;max-width:100%;overflow-x:auto;\">");
            var depths = new int[parents.Length];
            var previousDepth = 0;
            for (var index = 0; index < parents.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var line = lines[index + 1];
                if (line.Count != 1 || line[0] is not LinkInline)
                    throw new InvalidOperationException("A captured roster member is missing its detail hyperlink.");
                var depth = parents[index] is { } parent ? depths[parent] + 1 : 0;
                depths[index] = depth;
                if (index > 0)
                {
                    if (depth > previousDepth)
                        renderer.WriteLine("<ul style=\"list-style:none;border-left:1px solid var(--border);margin:4px 0 4px 8px;padding-left:18px;\">");
                    else
                    {
                        renderer.WriteLine("</li>");
                        for (var level = previousDepth; level > depth; level--) renderer.WriteLine("</ul></li>");
                    }
                }
                renderer.Write("<li><span class=\"report-line report-roster-member\">");
                WriteLine(renderer, line);
                renderer.WriteLine("</span>");
                previousDepth = depth;
            }
            renderer.WriteLine("</li>");
            for (var level = previousDepth; level > 0; level--) renderer.WriteLine("</ul></li>");
            renderer.Write("</ul><hr class=\"report-rule\" />");
            if (lines.Count > parents.Length + 2)
            {
                renderer.WriteLine();
                for (var index = parents.Length + 2; index < lines.Count; index++)
                    WriteReportLine(renderer, lines[index]);
            }
            renderer.WriteLine("</div>");
        }

        private bool IsRule(List<Inline> line, char character)
        {
            if (line.Count != 1 || line[0] is not LiteralInline literal
                || literal.Content.Length != 120 || literal.Content.AsSpan().IndexOfAnyExcept(character) >= 0)
                return false;
            var start = literal.Span.Start;
            var end = start + 120;
            // Data lines use Markdown hard breaks; only the writer's bare ruler lines are presentation.
            return start >= 0 && end <= markdown.Length && (start == 0 || markdown[start - 1] == '\n')
                && (end == markdown.Length || markdown[end] is '\r' or '\n');
        }

        private string? LineClass(string text) =>
            text.StartsWith("\U0001F504 Commit:", StringComparison.Ordinal) || text.StartsWith("\U0001F504 PR #", StringComparison.Ordinal)
                || text.StartsWith("Incident Details:", StringComparison.Ordinal)
                || workItemDetails && text.StartsWith("\U0001F4CB Work Item #", StringComparison.Ordinal)
                || featureRegistryDetails && text.StartsWith("\U0001F504 Pull Request:", StringComparison.Ordinal)
                ? "report-record-heading"
            : text.StartsWith("\U0001F4C4 ", StringComparison.Ordinal) ? "report-file"
            : text.StartsWith("\U0001F4E6 ", StringComparison.Ordinal) || text.StartsWith("\U0001F4CA ", StringComparison.Ordinal)
                || text.StartsWith("\U0001F4C1 ", StringComparison.Ordinal)
                || workItemDetails && text.StartsWith("\u23F1\uFE0F Time Analysis", StringComparison.Ordinal) ? "report-subheading"
            : text.StartsWith("\u2022 [", StringComparison.Ordinal) ? "report-work-item"
            : text.StartsWith("\u2022 ", StringComparison.Ordinal) ? "report-roster-member"
            : null;

        private static string PlainText(List<Inline> line)
        {
            var text = new StringBuilder();
            foreach (var inline in line) Append(inline);
            return text.ToString();

            void Append(Inline inline)
            {
                if (inline is LiteralInline literal) text.Append(literal.Content.AsSpan());
                else if (inline is HtmlEntityInline entity) text.Append(entity.Transcoded.AsSpan());
                else if (inline is ContainerInline container)
                    foreach (var child in container) Append(child);
            }
        }

        private static void WriteLine(HtmlRenderer renderer, List<Inline> line)
        {
            foreach (var inline in line) renderer.Write(inline);
        }
    }

    private sealed class ReportLiteralRenderer(bool workItemDetails, bool featureRegistryDetails) : LiteralInlineRenderer
    {
        private static readonly HashSet<string> FeatureRegistryLabels = new(StringComparer.Ordinal)
        {
            "Source branch:", "Target branch:", "Created:", "Completed by:", "Completed:", "PR ID:"
        };

        private static readonly HashSet<string> WorkItemLabels = new(StringComparer.Ordinal)
        {
            "State:", "Assignee:", "Priority:", "Created Date:", "Closed Date:", "Time to Complete:",
            "Last Changed:", "Area Path:", "Iteration:", "Time in Active Work:"
        };

        private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
        {
            "Author:", "Committer:", "Date:", "Message:", "Commit ID:", "Organization:", "Repository ID:",
            "Source-qualified key:", "Source memberships:", "Author identity:", "Committer identity:",
            "Author date:", "Committer date:", "Line Changes:", "Changed Files:", "Parent count (not PR evidence):",
            "ID:", "Title:", "Status:", "Severity:", "Owning Team Name:", "Owning Contact:", "Create Date:",
            "Modified Date:", "Mitigate Date:", "Resolve Date:", "TTD:", "TTM:", "TTR:"
        };

        protected override void Write(HtmlRenderer renderer, LiteralInline literal)
        {
            if (literal.PreviousSibling is not (null or LineBreakInline))
            {
                base.Write(renderer, literal);
                return;
            }
            var text = literal.Content.ToString();
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            renderer.WriteEscape(text[..start]);
            var hasIcon = false;
            if (start < text.Length)
            {
                if (Rune.TryGetRuneAt(text, start, out var rune)
                    && rune.Value is >= 0x1f000 and <= 0x1faff or >= 0x2600 and <= 0x27ff or 0x23f1 or 0x23f2)
                {
                    var icon = StringInfo.GetNextTextElement(text, start);
                    Icon(renderer, icon);
                    start += icon.Length;
                    hasIcon = true;
                }
            }
            var remainder = text[start..];
            var labelStart = remainder.Length - remainder.TrimStart().Length;
            var colon = remainder.IndexOf(':', labelStart);
            if (colon >= labelStart && colon - labelStart < 40
                && (Labels.Contains(remainder[labelStart..(colon + 1)])
                    || workItemDetails && hasIcon && WorkItemLabels.Contains(remainder[labelStart..(colon + 1)])
                    || featureRegistryDetails && hasIcon && FeatureRegistryLabels.Contains(remainder[labelStart..(colon + 1)])))
            {
                var label = remainder[labelStart..(colon + 1)];
                var icon = label switch
                {
                    "Author:" or "Committer:" => "\U0001F464",
                    "Date:" => "\U0001F4C5",
                    "Message:" => "\U0001F4AC",
                    _ => null
                };
                if (!hasIcon && icon is not null) { Icon(renderer, icon); renderer.Write(' '); }
                renderer.WriteEscape(remainder[..labelStart]);
                renderer.Write("<span class=\"report-label\">").WriteEscape(label).Write("</span>");
                renderer.WriteEscape(remainder[(colon + 1)..]);
            }
            else renderer.WriteEscape(remainder);
        }

        private static void Icon(HtmlRenderer renderer, string icon) =>
            renderer.Write("<span class=\"report-icon\" aria-hidden=\"true\">").WriteEscape(icon).Write("</span>");
    }
}
