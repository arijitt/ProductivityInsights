using System.Net;
using System.Text;
using System.Text.Json;

namespace ProductivityInsights.ReportTests.AdoReports;

internal sealed class AdoReportTestHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add($"{request.Method} {request.RequestUri}");
        AdoReportAssert.That(request.Headers.Authorization?.Scheme == "Bearer"
            && request.Headers.Authorization.Parameter == "fixture-token", "Bearer token injection was not used.");
        return send(request, cancellationToken);
    }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    public static string Query(HttpRequestMessage request, string name) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[name] ?? "";
}

internal static class AdoReportAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static async Task FailsAsync(Func<Task> action, params string[] fragments)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException exception)
        {
            foreach (var fragment in fragments)
                That(exception.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"Expected contextual error containing '{fragment}', got '{exception.Message}'.");
            That(!exception.Message.Contains("fixture-token", StringComparison.Ordinal), "An error exposed the token.");
            return;
        }
        throw new InvalidOperationException("Expected the required-data failure to block the report.");
    }
}
