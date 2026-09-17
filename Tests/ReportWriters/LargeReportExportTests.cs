using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.ReportWriters;

public static class LargeReportExportTests
{
    public static void Run()
    {
        RecordedTextCompatibility();
        StreamingFailuresAndCancellation();
        ArchiveAbovePreviousLimits();
        StreamedOutputLimit();
    }

    private static void RecordedTextCompatibility()
    {
        var baseline = ReportWriterFixtures.GitSnapshot();
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
        foreach (var message in new[]
        {
            "", "last line without newline", "line one\r\nline two\rline three\n",
            "<script>text only</script> & ``` fenced\n    indented\tline\n\U0001F642 \u4e00 \ud800"
        })
        {
            var snapshot = baseline with
            {
                Commits =
                [
                    baseline.Commits[0] with { Message = message },
                    baseline.Commits[0] with { Id = "second-message", Message = "Different recorded text & <encoded>\n" },
                    baseline.Commits[0] with { Id = "repeated-message", Message = message }
                ],
                PullRequests = []
            };
            var expected = CodeBlocks(Markdown.ToHtml(MarkdownReportWriter.Write(snapshot), pipeline));
            var actual = CodeBlocks(HtmlDetailedReportWriter.Write(snapshot));
            Require(actual.SequenceEqual(expected), "Referenced code blocks must retain the original Markdown renderer's exact content and escaping.");
            var document = ReportDocumentBuilder.Build(snapshot);
            using var writer = new StringWriter();
            var bytes = HtmlDetailedReportWriter.WriteTo(document, writer);
            Require(writer.ToString() == HtmlDetailedReportWriter.Write(document)
                && bytes == Encoding.UTF8.GetByteCount(writer.ToString()),
                "Streamed and buffered details must be identical, including UTF-8 and malformed-surrogate accounting.");
            using var summary = new StringWriter();
            var summaryBytes = HtmlSummaryReportWriter.WriteTo(document, "report_Detailed.html", summary);
            Require(summary.ToString() == HtmlSummaryReportWriter.Write(document, "report_Detailed.html")
                && summaryBytes == Encoding.UTF8.GetByteCount(summary.ToString()),
                "Streaming must not change summary HTML or its byte count.");
        }

        static IEnumerable<string> CodeBlocks(string html) =>
            Regex.Matches(html, @"<pre><code\b.*?</code></pre>", RegexOptions.Singleline)
                .Select(match => match.Value.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void StreamingFailuresAndCancellation()
    {
        var document = ReportDocumentBuilder.Build(ReportWriterFixtures.IncidentSnapshot());
        using var cancellation = new CancellationTokenSource();
        using var sink = new ObservedStream(() => cancellation.Cancel());
        using var writer = new StreamWriter(sink, new UTF8Encoding(false), 1024, leaveOpen: true);
        Throws<OperationCanceledException>(() => HtmlDetailedReportWriter.WriteTo(document, writer, cancellation.Token));
        Require(sink.Bytes > 0 && sink.CanWrite, "Cancellation during streaming must propagate without taking ownership of the caller's sink.");
        using var failedSink = new ObservedStream(() => throw new IOException("Synthetic output failure."));
        using var failedWriter = new StreamWriter(failedSink, new UTF8Encoding(false), 1024, leaveOpen: true);
        Throws<IOException>(() => HtmlDetailedReportWriter.WriteTo(document, failedWriter));
        Require(failedSink.CanWrite, "Destination failures must propagate, not turn into a successful partial report.");
    }

    private static void ArchiveAbovePreviousLimits()
    {
        const int incidentCount = 14;
        const int textLength = 700_000;
        var snapshot = Snapshot(incidentCount, "\nBEGIN-LARGE\n" + new string('\u4e00', textLength) + "\nEND-LARGE\n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var archive = ReportArchiveBuilder.Build(snapshot);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(archive.Content is FileStream && archive.Content.CanRead && archive.Content.CanSeek && archive.Content.Position == 0,
            "Large archives must use a completed, seekable temporary stream, not a ZIP-sized memory buffer.");
        var path = ((FileStream)archive.Content).Name;
        try
        {
            using var zip = new ZipArchive(archive.Content, ZipArchiveMode.Read, leaveOpen: true);
            Require(zip.Entries.Count == 2, "Large exports must still contain exactly two complete HTML files.");
            var detail = zip.Entries.Single(entry => entry.Name.EndsWith("_Detailed.html", StringComparison.Ordinal));
            Require(detail.Length > 64L * 1024 * 1024 && detail.Length < ReportRenderingLimits.MaximumExportFileBytes,
                "The regression must exceed both the old 32 MiB file limit and old 64 MiB pair limit.");
            using var reader = new StreamReader(detail.Open(), new UTF8Encoding(false, true));
            var buffer = new char[8192];
            long textCharacters = 0;
            var starts = 0;
            var ends = 0;
            var tail = "";
            var lastIncident = false;
            int length;
            while ((length = reader.Read(buffer)) != 0)
            {
                for (var index = 0; index < length; index++)
                    if (buffer[index] == '\u4e00') textCharacters++;
                var window = tail + new string(buffer, 0, length);
                starts += Occurrences(window, "BEGIN-LARGE", tail.Length);
                ends += Occurrences(window, "END-LARGE", tail.Length);
                lastIncident |= window.Contains("Incident silo-a:113", StringComparison.Ordinal);
                tail = window[^Math.Min(64, window.Length)..];
            }
            Require(textCharacters == 3L * incidentCount * textLength
                && starts == 3 * incidentCount && ends == starts && lastIncident
                && tail.Contains("</html>", StringComparison.Ordinal),
                "Every full mitigation must survive Active, Created and Contributed details, including the last record and document footer.");
            var summaryEntry = zip.Entries.Single(entry => entry != detail);
            using var summary = new StreamReader(summaryEntry.Open());
            Require(summary.ReadToEnd() == HtmlSummaryReportWriter.Write(ReportDocumentBuilder.Build(snapshot), detail.Name),
                "The large export's summary must remain byte-for-byte equivalent to the existing format.");
            Require(allocated < 128L * 1024 * 1024,
                "Large exports must not allocate complete repeated Markdown, full HTML strings or a ZIP-sized memory buffer.");
            Console.WriteLine($"Large report: {detail.Length:N0} UTF-8 bytes; generation allocated {allocated:N0} bytes.");
        }
        finally
        {
            archive.Content.Dispose();
        }
        Require(!File.Exists(path), "Closing the export stream must delete its private temporary ZIP.");
    }

    private static void StreamedOutputLimit()
    {
        var baseline = Snapshot(14, new string('x', 900_000));
        var roster = Enumerable.Range(0, 20).Select(index =>
            new ReportIdentity($"Member {index}", $"member{index}@example.test", $"member-{index}")).ToImmutableArray();
        var snapshot = baseline with
        {
            Request = baseline.Request with { Roster = roster },
            Incidents = baseline.Incidents.Select(incident => incident with { Owner = roster[0] }).ToImmutableArray(),
            IncidentActions = roster.SelectMany((member, memberIndex) => baseline.IncidentActions.Select(action => action with
            {
                Actor = member, ActionId = $"{memberIndex}-{action.ActionId}"
            })).ToImmutableArray()
        };
        var exception = Throws<ReportSizeLimitException>(() => ReportArchiveBuilder.Build(snapshot));
        Require(exception.Message.Contains("256 MiB UTF-8", StringComparison.Ordinal)
            && exception.Message.Contains("no report was truncated", StringComparison.Ordinal),
            "Streaming must retain the larger hard byte limit and reject, never publish, an oversized archive.");
    }

    private static ReportSnapshot Snapshot(int count, string text)
    {
        var baseline = ReportWriterFixtures.IncidentSnapshot();
        return baseline with
        {
            Incidents = Enumerable.Range(100, count).Select(id => baseline.Incidents[0] with
            {
                Id = id.ToString(), Mitigation = text
            }).ToImmutableArray(),
            IncidentActions = Enumerable.Range(100, count).Select(id => baseline.IncidentActions[0] with
            {
                IncidentId = id.ToString(), ActionId = "action-" + id
            }).ToImmutableArray()
        };
    }

    private static int Occurrences(string text, string value, int alreadyRead)
    {
        var result = 0;
        for (var start = text.IndexOf(value, StringComparison.Ordinal); start >= 0;
            start = text.IndexOf(value, start + value.Length, StringComparison.Ordinal))
            if (start + value.Length > alreadyRead) result++;
        return result;
    }

    private sealed class ObservedStream(Action observe) : Stream
    {
        private bool disposed;
        public long Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !disposed;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Bytes += buffer.Length;
            observe();
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
