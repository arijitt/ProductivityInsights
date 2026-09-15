using System.Net;

namespace ProductivityInsights.Services.Reports;

internal sealed class AdoReportRequestException(
    string context, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : InvalidOperationException($"Report export: {context}: {message}", inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
