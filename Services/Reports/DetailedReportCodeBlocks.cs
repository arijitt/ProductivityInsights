using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace ProductivityInsights.Services.Reports;

internal sealed class DetailedReportCodeBlocks(MarkdownPipeline pipeline, CancellationToken token)
{
    private readonly Dictionary<int, string> references = [];
    private readonly Dictionary<string, CodeBlock> parsed = new(StringComparer.Ordinal);

    internal void Add(int position, string text)
    {
        token.ThrowIfCancellationRequested();
        references.Add(position, text);
    }

    internal void Configure(HtmlRenderer renderer) => renderer.ObjectRenderers.Insert(0, new ReferenceRenderer(this));

    internal void Validate(MarkdownDocument document)
    {
        var remaining = references.Keys.ToHashSet();
        foreach (var block in document.Descendants<CodeBlock>())
        {
            token.ThrowIfCancellationRequested();
            remaining.Remove(block.Span.Start);
        }
        if (remaining.Count != 0)
            throw new InvalidOperationException("The detailed report could not locate all recorded text blocks. No partial report was produced.");
    }

    private CodeBlock Resolve(CodeBlock block)
    {
        token.ThrowIfCancellationRequested();
        if (!references.TryGetValue(block.Span.Start, out var text)) return block;
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

    private sealed class ReferenceRenderer(DetailedReportCodeBlocks blocks) : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block) => base.Write(renderer, blocks.Resolve(block));
    }
}
