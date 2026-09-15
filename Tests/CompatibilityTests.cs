using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProductivityInsights.ReportTests;

public static class CompatibilityTests
{
    private const string Baseline = "1379858";

    public static void Run()
    {
        var root = RepositoryRoot();
        var protectedFiles = Git(root, "ls-tree", "-r", "--name-only", Baseline,
                "--", "Metrics", "Models", "Options", "Utilities")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var changes = Git(root, ["diff", "--name-only", Baseline, "--", .. protectedFiles]);
        Require(string.IsNullOrWhiteSpace(changes), $"Existing API/DTO/auth files changed:\n{changes}");

        var baselinePage = Git(root, "show", $"{Baseline}:Components/Pages/AnalysisOptions.razor");
        var currentPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "AnalysisOptions.razor"));
        Require(NormalizePage(baselinePage) == NormalizePage(currentPage),
            "An existing analysis API call, report renderer, form, or source mapping changed outside permitted export hooks.");

        var baselineCss = Git(root, "show", $"{Baseline}:Components/Pages/AnalysisOptions.razor.css");
        var currentCss = File.ReadAllText(Path.Combine(root, "Components", "Pages", "AnalysisOptions.razor.css"))
            .Replace(".analysis-export-btn", ".analysis-pdf-btn");
        currentCss = Regex.Replace(currentCss, @"\.report-export-status\s*\{[^}]*\}", "");
        Require(NormalizeLines(baselineCss) == NormalizeLines(currentCss),
            "Existing UX report styling changed.");

        var baselineTheme = Git(root, "show", $"{Baseline}:wwwroot/theme.js");
        baselineTheme = baselineTheme[..baselineTheme.IndexOf("window._loadScript", StringComparison.Ordinal)];
        var currentTheme = File.ReadAllText(Path.Combine(root, "wwwroot", "theme.js"));
        Require(NormalizeLines(baselineTheme) == NormalizeLines(currentTheme),
            "Non-export theme/modal/scrolling functions changed.");
    }

    private static string NormalizePage(string value)
    {
        value = value.Replace("\r\n", "\n");
        value = RemoveRange(value, "                    <button class=\"btn btn-sm analysis-pdf-btn\"",
            "                    @if (selectedView == \"details\"");
        value = RemoveRange(value, "                    <button class=\"btn btn-sm analysis-export-btn\"",
            "                    @if (selectedView == \"details\"");
        value = RemoveRange(value, "    private IJSObjectReference? _jsModule;", "    public async ValueTask DisposeAsync()");
        value = RemoveRange(value, "    public async ValueTask DisposeAsync()", "    private RenderFragment RenderAnalysisForm()");
        value = RemoveRange(value, "    private async Task DownloadAsPdf()", "    private IncidentManagementRequest CaptureIncidentManagementRequest()");
        value = Regex.Replace(value,
            @"(?m)^[ \t]*(?:(?:Begin|Complete|Hide|Restore)ReportExportContext\([^\r\n]*\);|private bool isGeneratingPdf = false;)[ \t]*\n", "");
        return NormalizeLines(value);
    }

    private static string RemoveRange(string value, string start, string end)
    {
        var from = value.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
            return value;
        var to = value.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Require(to >= 0, $"Missing compatibility boundary {end}");
        return value.Remove(from, to - from);
    }

    private static string NormalizeLines(string value) =>
        string.Join('\n', value.Replace("\r\n", "\n").TrimStart('\uFEFF').Split('\n')
            .Select(line => line.TrimEnd()).Where(line => !string.IsNullOrWhiteSpace(line)));

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProductivityInsights.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate the application repository.");
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, $"Compatibility baseline lookup failed: {error}");
        return output;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
