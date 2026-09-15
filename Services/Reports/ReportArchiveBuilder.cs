using System.IO.Compression;
using System.Text;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public sealed record ReportArchive(string FileName, MemoryStream Content);

public static class ReportArchiveBuilder
{
    public const long MaximumReportBytes = 64L * 1024 * 1024;

    public static string GetFileName(ReportRequest request) => GetStem(request) + ".zip";

    private static string GetStem(ReportRequest request)
    {
        var scope = request.IsTeams ? "teams" : request.View;
        var analysis = request.Analysis switch
        {
            "workItems" => "Work_Items",
            "gitCommits" => "Git_Commits",
            "incidentManagement" => "Incident_Resolutions",
            _ => throw new InvalidOperationException("Unsupported report analysis.")
        };
        if (request.View is not ("details" or "teams" or "trends")
            || request.View == "trends" && request.Analysis != "gitCommits")
            throw new InvalidOperationException("This view has no supported report data.");
        return $"{analysis}_{scope}_{request.Start:yyyyMMdd}_to_{request.End:yyyyMMdd}_{request.RunId:N}";
    }

    public static ReportArchive Build(ReportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = GetStem(snapshot.Request);
        var document = ReportDocumentBuilder.Build(snapshot, cancellationToken);
        var markdown = MarkdownReportWriter.Write(document, cancellationToken);
        var html = HtmlSummaryReportWriter.Write(document, stem + ".md", cancellationToken);
        if ((long)Encoding.UTF8.GetByteCount(markdown) + Encoding.UTF8.GetByteCount(html) > MaximumReportBytes)
            throw new InvalidOperationException("The complete report exceeds the 64 MiB export resource limit. Select a smaller scope or date range.");

        var stream = new MemoryStream();
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(zip, stem + ".md", markdown, cancellationToken);
                AddEntry(zip, stem + ".html", html, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return new ReportArchive(stem + ".zip", stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void AddEntry(ZipArchive zip, string name, string content, CancellationToken token)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        for (var offset = 0; offset < content.Length; offset += 8192)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(content.AsSpan(offset, Math.Min(8192, content.Length - offset)));
        }
    }
}
