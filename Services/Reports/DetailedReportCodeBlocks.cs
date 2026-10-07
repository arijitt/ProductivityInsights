using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ProductivityInsights.Services.Reports;

internal sealed class DetailedReportCodeBlocks(MarkdownPipeline pipeline, CancellationToken token)
{
    private readonly Dictionary<int, (string Text, bool InlineTitle)> references = [];
    private readonly Dictionary<int, string> inlineReferences = [];
    private readonly Dictionary<string, CodeBlock> parsed = new(StringComparer.Ordinal);

    internal void Add(int position, string text, bool inlineTitle = false)
    {
        token.ThrowIfCancellationRequested();
        references.Add(position, (text, inlineTitle));
    }

    internal void AddInline(int position, string text)
    {
        token.ThrowIfCancellationRequested();
        inlineReferences.Add(position, text);
    }

    internal void Configure(HtmlRenderer renderer)
    {
        renderer.ObjectRenderers.Insert(0, new ReferenceRenderer(this));
        renderer.ObjectRenderers.Insert(0, new InlineReferenceRenderer(this, token));
    }

    internal void Validate(MarkdownDocument document)
    {
        var remaining = references.Keys.ToHashSet();
        foreach (var block in document.Descendants<CodeBlock>())
        {
            token.ThrowIfCancellationRequested();
            remaining.Remove(block.Span.Start);
        }
        var remainingInline = inlineReferences.Keys.ToHashSet();
        foreach (var inline in document.Descendants<CodeInline>())
        {
            token.ThrowIfCancellationRequested();
            remainingInline.Remove(inline.Span.Start);
        }
        if (remaining.Count != 0 || remainingInline.Count != 0)
            throw new InvalidOperationException("The detailed report could not locate all recorded text blocks. No partial report was produced.");
    }

    private CodeBlock Resolve(CodeBlock block)
    {
        token.ThrowIfCancellationRequested();
        if (!references.TryGetValue(block.Span.Start, out var reference)) return block;
        var text = reference.Text;
        if (parsed.TryGetValue(text, out var result)) return result;
        // Parse each distinct recorded text once, not every repeated member/category appearance.
        var source = new ReportOutput(token);
        source.CodeBlock(text);
        var document = Markdown.Parse(source.ToString(), pipeline);
        token.ThrowIfCancellationRequested();
        if (document.Count != 1 || document[0] is not CodeBlock code)
            throw new InvalidOperationException("Recorded report text could not be rendered as a complete code block.");
        parsed.Add(text, code);
        return code;
    }

    private bool WriteInlineTitle(HtmlRenderer renderer, CodeBlock block)
    {
        token.ThrowIfCancellationRequested();
        if (!references.TryGetValue(block.Span.Start, out var reference) || !reference.InlineTitle) return false;
        renderer.Write("<div class=\"report-line report-pr-title\"><span class=\"report-icon\" aria-hidden=\"true\">\U0001F4AC</span> <span class=\"report-label\">Title:</span> <span class=\"report-pr-title-value\" style=\"white-space:pre-wrap\">");
        renderer.WriteEscape(reference.Text);
        renderer.WriteLine("</span></div>");
        return true;
    }

    private sealed class ReferenceRenderer(DetailedReportCodeBlocks blocks) : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            if (!blocks.WriteInlineTitle(renderer, block)) base.Write(renderer, blocks.Resolve(block));
        }
    }

    private sealed class InlineReferenceRenderer(DetailedReportCodeBlocks blocks, CancellationToken token) : CodeInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeInline inline)
        {
            token.ThrowIfCancellationRequested();
            if (blocks.inlineReferences.TryGetValue(inline.Span.Start, out var text))
                renderer.WriteEscape(text);
            else base.Write(renderer, inline);
        }
    }
}
