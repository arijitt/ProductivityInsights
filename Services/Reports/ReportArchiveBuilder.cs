using System.IO.Compression;
using System.Text;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public sealed record ReportArchive(string FileName, Stream Content);

public static class ReportArchiveBuilder
{
    public const long MaximumReportBytes = 2L * ReportRenderingLimits.MaximumExportFileBytes;

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
        var detailedName = stem + "_Detailed.html";
        var stream = CreateTemporaryArchive();
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var bytes = AddEntry(zip, detailedName, writer => HtmlDetailedReportWriter.WriteTo(document, writer, cancellationToken));
                bytes += AddEntry(zip, stem + ".html", writer => HtmlSummaryReportWriter.WriteTo(document, detailedName, writer, cancellationToken));
                if (bytes > MaximumReportBytes)
                    throw new ReportSizeLimitException($"The complete report exceeds the {ReportFormatting.Number(MaximumReportBytes / (1024 * 1024))} MiB combined export limit. No report was truncated.");
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

    private static FileStream CreateTemporaryArchive()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), $"ProductivityInsights-{Guid.NewGuid():N}.zip"), options);
    }

    private static long AddEntry(ZipArchive zip, string name, Func<TextWriter, long> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
        return write(writer);
    }
}
