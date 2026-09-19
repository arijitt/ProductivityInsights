using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public static class HtmlDetailedReportWriter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().DisableHtml().Build();
    private static readonly Lazy<string> FontCss = new(CreateFontCss);

    public static string Write(ReportSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Write(ReportDocumentBuilder.Build(snapshot, cancellationToken), cancellationToken);

    public static string Write(ReportDocument document, CancellationToken cancellationToken = default)
    {
        var (body, markdown, codeBlocks) = Prepare(document, cancellationToken);
        Render(document, body, markdown, codeBlocks, cancellationToken, countOnly: true);
        return Render(document, body, markdown, codeBlocks, cancellationToken, countOnly: false).ToString();
    }

    public static long WriteTo(ReportDocument document, TextWriter destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var (body, markdown, codeBlocks) = Prepare(document, cancellationToken);
        return Render(document, body, markdown, codeBlocks, cancellationToken, countOnly: false,
            destination, ReportRenderingLimits.MaximumExportFileBytes).ByteCount;
    }

    private static (MarkdownDocument Body, string Markdown, DetailedReportCodeBlocks CodeBlocks) Prepare(
        ReportDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var navigation = new DetailedReportNavigation(document, cancellationToken);
        var codeBlocks = new DetailedReportCodeBlocks(Pipeline, cancellationToken);
        var markdown = MarkdownReportWriter.WriteForDetailedHtml(document, navigation, codeBlocks, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var body = Markdown.Parse(markdown, Pipeline);
        codeBlocks.Validate(body);
        navigation.Apply(body);
        cancellationToken.ThrowIfCancellationRequested();
        return (body, markdown, codeBlocks);
    }

    private static ReportOutput Render(ReportDocument document, MarkdownDocument body, string markdown, DetailedReportCodeBlocks codeBlocks,
        CancellationToken token, bool countOnly, TextWriter? destination = null, int maximumBytes = ReportRenderingLimits.MaximumOutputBytes)
    {
        var output = new ReportOutput(token, countOnly, destination, maximumBytes);
        output.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n");
        output.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        output.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; font-src data:; base-uri 'none'; form-action 'none'\" />\n<title>");
        output.Html(document.Title);
        output.Append("</title>\n<style>\n");
        output.Append(FontCss.Value);
        output.Append("""
            :root { color-scheme:dark; --report-font:'Montserrat','Segoe UI',Arial,sans-serif; --ink:#e6edf7; --accent:#80baff; --border:#33465f; --muted:#b4c4d9; --surface:#111c2e; --panel:#16243a; --heading:#f0f5ff; --heading-start:#16365a; --heading-end:#282744; --hover:#20334f; }
            * { box-sizing:border-box; }
            body { background:#0b1220; color:var(--ink); font-family:var(--report-font); font-size:14px; line-height:1.65; margin:0; padding:32px 24px; -webkit-font-smoothing:antialiased; }
            main { max-width:1360px; margin:auto; padding:36px 40px; background:var(--surface); border:1px solid var(--border); border-radius:16px; box-shadow:0 12px 36px rgba(0,0,0,.2); }
            h1 { margin:0 0 28px; padding:24px; background:linear-gradient(135deg,var(--heading-start),var(--heading-end)); border-left:5px solid var(--accent); border-radius:10px; color:var(--heading); font-size:28px; font-weight:700; line-height:1.3; letter-spacing:-.025em; }
            h2,h3,h4,h5,h6 { color:var(--heading); line-height:1.45; font-weight:600; break-after:avoid; }
            h1,h2,h3,h4,h5,h6 { white-space:pre-wrap; }
            h2 { margin:32px 0 16px; padding-bottom:10px; border-bottom:1px solid var(--border); font-size:19px; }
            h3 { margin:26px 0 12px; font-size:17px; }
            h4 { margin:22px 0 10px; font-size:15px; color:var(--accent); }
            h5,h6 { margin:18px 0 8px; font-size:14px; }
            p,li,th,td,h1,h2,h3,h4,h5,h6,.report-line { overflow-wrap:anywhere; }
            p { margin:10px 0 16px; }
            a { color:var(--accent); text-decoration:none; text-underline-offset:3px; }
            a:hover { text-decoration:underline; }
            a:focus-visible,.report-table-scroll:focus-visible { outline:3px solid #8bbdff; outline-offset:3px; }
            [id] { scroll-margin-top:24px; }
            :target { outline:2px solid var(--accent); outline-offset:5px; }
            .report-member-link,.report-icm-link { text-decoration:underline; text-decoration-color:var(--border); white-space:pre-wrap; }
            strong,.report-label { font-weight:600; color:var(--heading); }
            ul,ol { padding-left:24px; margin:12px 0 20px; }
            li { padding:3px 0; }
            ul:has(>li>strong:first-child) { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px 24px; list-style:none; padding:16px 20px; background:var(--panel); border:1px solid var(--border); border-radius:10px; }
            .report-icon { display:inline-block; font-family:'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif; font-variant-emoji:emoji; font-weight:400; line-height:1.25; vertical-align:-.05em; }
            .report-section-heading { padding:15px 18px; border:1px solid var(--border); border-radius:10px; background:linear-gradient(135deg,var(--heading-start),var(--heading-end)); letter-spacing:.02em; font-size:17px; }
            .report-person-heading { padding:12px 16px; border-left:3px solid var(--accent); border-radius:0 8px 8px 0; background:var(--panel); font-size:16px; }
            .report-section-heading .report-icon,.report-person-heading .report-icon { font-size:1.18em; margin-right:4px; }
            .report-lines { margin:12px 0 20px; }
            .report-line { margin:0; padding:4px 0; min-width:0; }
            .report-record-heading { color:var(--accent); padding:8px 0; font-size:16px; }
            .report-subheading { font-weight:600; margin:8px 0 4px; }
            .report-roster-member { display:inline-block; max-width:100%; margin:4px 6px 4px 0; padding:5px 12px; border:1px solid var(--border); border-radius:18px; background:var(--panel); font-size:13px; }
            .report-work-item { padding:10px 14px; margin:8px 0; border-left:3px solid var(--accent); background:var(--panel); font-weight:600; border-radius:0 6px 6px 0; }
            .report-file { margin-left:12px; padding:5px 12px; border-left:2px solid var(--border); color:var(--muted); font-family:Consolas,'Courier New',monospace; font-size:12.5px; }
            .report-rule { border:0; border-top:1px solid var(--border); margin:20px 0; }
            .report-table-scroll { max-width:100%; overflow-x:auto; margin:16px 0 26px; border:1px solid var(--border); border-radius:10px; }
            table { width:100%; border-collapse:collapse; font-size:12.5px; font-variant-numeric:tabular-nums; }
            th,td { padding:11px 14px; text-align:left; vertical-align:top; border-bottom:1px solid var(--border); border-right:1px solid var(--border); }
            th { background:var(--panel); color:var(--heading); font-weight:600; line-height:1.5; letter-spacing:.015em; }
            td { white-space:pre-wrap; }
            th:last-child,td:last-child { border-right:0; }
            tbody tr:last-child td { border-bottom:0; }
            tbody tr:nth-child(even) { background:var(--panel); }
            tbody tr:hover { background:var(--hover); }
            pre { margin:12px 0 20px; padding:16px 20px; border:1px solid var(--border); border-left:3px solid var(--accent); border-radius:8px; background:var(--panel); white-space:pre-wrap; overflow-wrap:anywhere; line-height:1.7; }
            code { font-family:Consolas,'Courier New',monospace; font-size:.94em; }
            pre>code.language-text { font-family:var(--report-font); font-size:13px; }
            blockquote { border-left:3px solid var(--accent); margin:16px 0; padding:8px 18px; background:var(--panel); color:var(--muted); }
            @media (max-width:700px) { body { padding:12px; } main { padding:20px 16px; border-radius:10px; } h1 { padding:18px; font-size:23px; } .report-section-heading { font-size:15px; } ul:has(>li>strong:first-child) { grid-template-columns:1fr; padding:12px; } th,td { padding:8px; } .report-file { margin-left:0; } }
            @page { margin:16mm; }
            @media print { :root { color-scheme:light; --ink:#24344d; --accent:#075fc7; --border:#dce5f0; --muted:#52627a; --surface:#fff; --panel:#f8fafc; --heading:#0d1b3f; --heading-start:#edf4ff; --heading-end:#f4f1ff; --hover:#eff5ff; } body { padding:0; background:white; color:#111; font-size:10pt; } main { max-width:none; padding:0; border:0; border-radius:0; box-shadow:none; } h1 { font-size:21pt; } :target { outline:none; } .report-table-scroll { overflow:visible; } table { font-size:8.5pt; } th,td { padding:6px; } thead { display:table-header-group; } tr { break-inside:avoid; } a { color:#111; text-decoration:underline; } }
            """);
        output.Append("\n</style>\n</head>\n<body>\n<main>\n");
        using var writer = new BoundedWriter(output);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        DetailedReportPresentation.Configure(renderer, markdown, token,
            workItemDetails: document.Snapshot.Request.IsTeams && document.Snapshot.Request.Analysis == "workItems");
        codeBlocks.Configure(renderer);
        renderer.Render(body);
        writer.Flush();
        output.Append("</main>\n</body>\n</html>\n");
        return output;
    }

    private static string CreateFontCss()
    {
        var assembly = typeof(HtmlDetailedReportWriter).Assembly;
        using var font = assembly.GetManifestResourceStream("ProductivityInsights.Reports.Montserrat.woff2")
            ?? throw new InvalidOperationException("The embedded detailed-report font is missing. Rebuild the application.");
        using var license = assembly.GetManifestResourceStream("ProductivityInsights.Reports.Montserrat.OFL")
            ?? throw new InvalidOperationException("The embedded detailed-report font license is missing. Rebuild the application.");
        using var bytes = new MemoryStream();
        font.CopyTo(bytes);
        using var reader = new StreamReader(license, Encoding.UTF8);
        return "/*\n" + reader.ReadToEnd() + "\n*/\n"
            + "@font-face { font-family:'Montserrat'; font-style:normal; font-weight:100 900; font-display:swap; "
            + "src:url(data:font/woff2;base64," + Convert.ToBase64String(bytes.ToArray()) + ") format('woff2'); }\n";
    }

    private sealed class BoundedWriter(ReportOutput output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => output.Character(value);
        public override void Write(string? value)
        {
            if (value is not null) output.Append(value);
        }
        public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));
        public override void Write(ReadOnlySpan<char> buffer)
        {
            output.Append(buffer);
        }
    }
}
