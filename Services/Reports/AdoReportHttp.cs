using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ProductivityInsights.Services.Reports;

internal sealed class AdoReportHttp(
    HttpClient client, Func<CancellationToken, Task<string>> tokenProvider, AdoReportCollectionLimits limits)
{
    private long totalResponseBytes;

    public async Task<JsonElement> JsonAsync(
        HttpMethod method, string url, object? body, string context, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, url, body, context, cancellationToken);
        try
        {
            if (response.Content.Headers.ContentLength is { } length)
                CheckBudget(length, 0, context);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = await ReadBoundedAsync(stream, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Error(context, "Expected a JSON object.");
            cancellationToken.ThrowIfCancellationRequested();
            var result = document.RootElement.Clone();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (JsonException exception)
        {
            throw Error(context, "The service returned invalid JSON.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AdoReportRequestException(context, "The required response could not be fully read.", inner: exception);
        }
        catch (IOException exception)
        {
            throw new AdoReportRequestException(context, "The required response could not be fully read.", inner: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdoReportRequestException(context, "Reading the required response timed out.", inner: exception);
        }
    }

    private async Task<MemoryStream> ReadBoundedAsync(
        Stream stream, string context, CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        var completed = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = Math.Min(limits.MaxResponseBytes - result.Length,
                    limits.MaxCollectionBytes - totalResponseBytes);
                // Read at most one byte beyond the remaining budget to distinguish exact-limit EOF.
                var size = remaining < chunk.Length ? checked((int)remaining + 1) : chunk.Length;
                var read = await stream.ReadAsync(chunk.AsMemory(0, size), cancellationToken);
                if (read == 0)
                    break;
                CheckBudget(read, result.Length, context);
                totalResponseBytes += read;
                result.Write(chunk, 0, read);
            }
            completed = true;
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
            if (!completed)
                result.Dispose();
        }
    }

    private void CheckBudget(long incomingBytes, long responseBytes, string context)
    {
        if (incomingBytes > limits.MaxResponseBytes - responseBytes)
            throw Error(context,
                $"The required response exceeds the {limits.MaxResponseBytes:N0}-byte response limit. Narrow the source/date range or review the configured export response limit; no partial report was produced.");
        if (incomingBytes > limits.MaxCollectionBytes - totalResponseBytes)
            throw Error(context,
                $"The required data exceeds the {limits.MaxCollectionBytes:N0}-byte cumulative collection budget. Narrow the source/date range or review the configured export collection limit; no partial report was produced.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body, string context, CancellationToken cancellationToken)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(method, url);
            var token = await tokenProvider(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(token))
                throw Error(context, "Authentication returned an empty access token.");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json is not null)
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new AdoReportRequestException(context, "The required Azure DevOps request could not be completed.", inner: exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AdoReportRequestException(context, "The required Azure DevOps request timed out.", inner: exception);
            }
            if (response.IsSuccessStatusCode)
                return response;

            var status = response.StatusCode;
            if (attempt < 3 && status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } retryAt ? retryAt - DateTimeOffset.UtcNow
                        : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                response.Dispose();
                if (delay > TimeSpan.FromMinutes(2))
                    throw new AdoReportRequestException(context, $"HTTP {(int)status}; the server requested a retry later than the export retry budget.", status);
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
                continue;
            }
            response.Dispose();
            throw new AdoReportRequestException(context,
                $"Azure DevOps request failed with HTTP {(int)status} ({status}). Verify source, branch, and read permissions.", status);
        }

    }

    public static InvalidOperationException Error(string context, string message, Exception? inner = null) =>
        new($"Report export: {context}: {message}", inner);

    public static string Escape(string value) => Uri.EscapeDataString(value);

    public static JsonElement Required(JsonElement element, string name, string context)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw Error(context, $"Required field '{name}' is missing.");
        return value;
    }

    public static JsonElement Array(JsonElement element, string name, string context)
    {
        var value = Required(element, name, context);
        if (value.ValueKind != JsonValueKind.Array)
            throw Error(context, $"Field '{name}' must be an array.");
        return value;
    }

    public static JsonElement Object(JsonElement element, string name, string context)
    {
        var value = Required(element, name, context);
        if (value.ValueKind != JsonValueKind.Object)
            throw Error(context, $"Field '{name}' must be an object.");
        return value;
    }

    public static string Text(JsonElement element, string name, string context, bool allowEmpty = false)
    {
        var value = Required(element, name, context);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())))
            throw Error(context, $"Field '{name}' must be {(allowEmpty ? "a string" : "a nonempty string")}.");
        return value.GetString()!;
    }

    public static string? OptionalText(JsonElement element, string name, string context)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Error(context, $"Field '{name}' must be a string.");
        return value.GetString();
    }

    public static int Integer(JsonElement element, string name, string context, int minimum = 0)
    {
        var value = Required(element, name, context);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < minimum)
            throw Error(context, $"Field '{name}' must be an integer >= {minimum}.");
        return result;
    }

    public static DateTimeOffset Date(JsonElement element, string name, string context)
    {
        var value = Text(element, name, context);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var result))
            throw Error(context, $"Field '{name}' is not a valid timestamp.");
        return result;
    }

    public static bool? Boolean(JsonElement element, string name, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Error(context, $"Cannot read '{name}' from a non-object value.");
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(context, $"Field '{name}' must be a boolean.");
        return value.GetBoolean();
    }
}
