using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public static class ReportRenderingLimits
{
    public const int MaximumOutputBytes = 32 * 1024 * 1024;
    public const int MaximumOutputCharacters = MaximumOutputBytes;
    public const int MaximumExportFileBytes = 256 * 1024 * 1024;
    public const int MaximumDetailedMarkupBytes = 64 * 1024 * 1024;
    public const int MaximumInputCharacters = 32 * 1024 * 1024;
    public const int MaximumRecordCharacters = 1024 * 1024;
    public const int MaximumRecords = 200_000;
    public const int MaximumSummaryRows = 100_000;
    public const int MaximumIdentities = 4096;
    public const int MaximumRosterDepth = 64;
}

public sealed class ReportSizeLimitException(string message) : InvalidOperationException(message);

internal sealed class ReportInputBudget(CancellationToken token)
{
    private long characters;
    private int records;

    public void Record(params string?[] values)
    {
        token.ThrowIfCancellationRequested();
        if (++records > ReportRenderingLimits.MaximumRecords)
            throw new ReportSizeLimitException("The complete report exceeds the 200,000-record construction limit (including identities, history and files). Select a smaller scope or date range; no report was truncated.");
        long recordCharacters = 0;
        foreach (var value in values)
        {
            recordCharacters += value?.Length ?? 0;
            if (recordCharacters > ReportRenderingLimits.MaximumRecordCharacters)
                throw new ReportSizeLimitException("A report record exceeds the 1 Mi-character text limit. Select a smaller scope; no text was truncated.");
            characters += value?.Length ?? 0;
            if (characters > ReportRenderingLimits.MaximumInputCharacters)
                throw new ReportSizeLimitException("The complete report exceeds the 32 Mi-character input limit. Select a smaller scope or date range; no report was truncated.");
        }
    }

    public void Identity(ReportIdentity identity) =>
        Record(identity.Name, identity.Email, identity.Id, identity.PrincipalName);
}

internal sealed class ReportOutput(CancellationToken token, bool countOnly = false,
    TextWriter? destination = null, int maximumBytes = ReportRenderingLimits.MaximumOutputBytes)
{
    private readonly StringBuilder? text = countOnly || destination is not null ? null : new();
    private readonly int limit = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private int characters;
    private long utf8Bytes;
    private bool previousHighSurrogate;

    internal int Length => characters;
    internal long ByteCount => utf8Bytes;

    public void Append(string value) => Append(value.AsSpan());

    public void Append(ReadOnlySpan<char> value)
    {
        token.ThrowIfCancellationRequested();
        if ((long)characters + value.Length > limit)
            throw OutputLimit();
        for (var offset = 0; offset < value.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var chunk = value.Slice(offset, Math.Min(4096, value.Length - offset));
            foreach (var character in chunk) Count(character);
            text?.Append(chunk);
            if (!countOnly) destination?.Write(chunk);
        }
    }

    public void Character(char value)
    {
        Count(value);
        text?.Append(value);
        if (!countOnly) destination?.Write(value);
    }

    private void Count(char value)
    {
        if ((characters & 1023) == 0)
            token.ThrowIfCancellationRequested();
        if (characters == limit)
            throw OutputLimit();
        // An unpaired surrogate uses UTF-8's replacement character; a completed pair takes four bytes.
        var bytes = previousHighSurrogate && char.IsLowSurrogate(value) ? 1
            : value <= 0x7f ? 1 : value <= 0x7ff ? 2 : 3;
        if (utf8Bytes + bytes > limit)
            throw OutputLimit();
        utf8Bytes += bytes;
        characters++;
        previousHighSurrogate = char.IsHighSurrogate(value);
    }

    private ReportSizeLimitException OutputLimit() =>
        new($"The report exceeds the {ReportFormatting.Number(limit / (1024 * 1024))} MiB UTF-8 "
            + (destination is null ? "in-memory rendering limit" : "per-file export rendering limit")
            + ". Select a smaller scope or date range; no report was truncated.");

    private void Repeat(char value, int count)
    {
        if ((long)characters + count > limit)
            throw OutputLimit();
        for (var offset = 0; offset < count; offset += 4096)
        {
            var length = Math.Min(4096, count - offset);
            for (var index = 0; index < length; index++) Count(value);
            text?.Append(value, length);
            if (!countOnly && destination is not null)
                for (var index = 0; index < length; index++) destination.Write(value);
        }
    }

    public void Markdown(string? value, bool table = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Append("-");
            return;
        }
        var lineStart = true;
        var numberedPrefix = true;
        for (var i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var c = value[i];
            switch (c)
            {
                case '&': Append("&amp;"); break;
                case '<': Append("&lt;"); break;
                case '>': Append("&gt;"); break;
                case '\r':
                    if (table) Append("&#13;");
                    else if (i + 1 >= value.Length || value[i + 1] != '\n') Append("  \n");
                    break;
                case '\n':
                    Append(table ? "&#10;" : "  \n");
                    lineStart = true;
                    numberedPrefix = true;
                    continue;
                case '\\': case '`': case '*': case '_': case '{': case '}':
                case '[': case ']': case '(': case ')': case '#':
                case '!': case '|': case '~':
                    Character('\\');
                    Character(c);
                    break;
                case '+': case '-':
                    if (!table && lineStart) Character('\\');
                    Character(c);
                    break;
                case '.':
                    if (!table && numberedPrefix && i > 0) Character('\\');
                    Character(c);
                    break;
                case ':':
                    if (i + 2 < value.Length && value[i + 1] == '/' && value[i + 2] == '/')
                        Append("&#58;");
                    else Character(c);
                    break;
                default:
                    if (char.IsControl(c) && c != '\t') Append($"&#{(int)c};");
                    else Character(c);
                    break;
            }
            if (!char.IsWhiteSpace(c)) lineStart = false;
            if (!char.IsDigit(c)) numberedPrefix = false;
        }
    }

    public void Html(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Append("-");
            return;
        }
        for (var i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            switch (value[i])
            {
                case '&': Append("&amp;"); break;
                case '<': Append("&lt;"); break;
                case '>': Append("&gt;"); break;
                case '"': Append("&quot;"); break;
                case '\'': Append("&#39;"); break;
                default:
                    if (char.IsControl(value[i]) && value[i] is not ('\r' or '\n' or '\t'))
                        Append($"&#{(int)value[i]};");
                    else Character(value[i]);
                    break;
            }
        }
    }

    public void CodeBlock(string value)
    {
        var longest = 0;
        var run = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            run = value[i] == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        if (longest >= ReportRenderingLimits.MaximumOutputCharacters / 3)
            throw new ReportSizeLimitException("A report text field exceeds the safe Markdown fence limit. Select a smaller scope; no text was truncated.");
        var fenceLength = Math.Max(3, longest + 1);
        Repeat('`', fenceLength);
        Append("text\n");
        Append(value);
        if (!value.EndsWith('\n')) Append("\n");
        Repeat('`', fenceLength);
        Append("\n\n");
    }

    public override string ToString()
    {
        token.ThrowIfCancellationRequested();
        if (text is null)
            throw new InvalidOperationException("A non-buffered report pass cannot be returned as text.");
        var result = text.ToString();
        token.ThrowIfCancellationRequested();
        return result;
    }
}

internal static class ReportFormatting
{
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Decimal(double? value) => value?.ToString("F1", CultureInfo.InvariantCulture) ?? "-";
    public static string Date(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";
    public static string CalendarDate(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string CompactTimestamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public static string RequestedDate(DateTime? value) =>
        value is { } date ? $"{date.ToString("O", CultureInfo.InvariantCulture)} ({date.Kind})" : "- (not supplied; see effective export bounds)";
    public static string Label(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    public static IEnumerable<(string Label, string Value)> BoundsMetadata(ReportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.EffectiveStartLiteral))
            yield return ("Effective query start literal", request.EffectiveStartLiteral);
        if (!string.IsNullOrWhiteSpace(request.EffectiveEndLiteral))
            yield return ("Effective query end literal", request.EffectiveEndLiteral);
        if (!string.IsNullOrWhiteSpace(request.EffectiveBoundsOrigin))
            yield return ("Effective bounds origin", request.EffectiveBoundsOrigin);
    }

    public static string Identity(ReportIdentity identity) => Identity(identity, includePrincipalAndId: true);

    public static string Identity(ReportIdentity identity, bool includePrincipalAndId)
    {
        var fields = new List<string> { string.IsNullOrWhiteSpace(identity.Name) ? "Unknown" : identity.Name };
        if (!string.IsNullOrWhiteSpace(identity.Email)) fields.Add($"email: {identity.Email}");
        if (includePrincipalAndId && !string.IsNullOrWhiteSpace(identity.PrincipalName)) fields.Add($"principal: {identity.PrincipalName}");
        if (includePrincipalAndId && !string.IsNullOrWhiteSpace(identity.Id)) fields.Add($"ID: {identity.Id}");
        return Join("; ", fields);
    }

    public static string Hours(double? hours) => hours switch
    {
        null => "-",
        < 1 => (hours.Value * 60).ToString("F0", CultureInfo.InvariantCulture) + " minutes",
        < 24 => Decimal(hours) + " hours",
        _ => Decimal(hours / 24) + " days"
    };

    public static string Ratio(long service, long test) => test == 0
        ? service == 0 ? "0:0" : "Inf"
        : ((double)service / test).ToString("F2", CultureInfo.InvariantCulture) + ":1";

    public static string? SafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.AbsoluteUri;
    }

    public static string MarkdownFileUrl(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.Length > 255)
            throw new ArgumentException("The matching detail filename exceeds the 255-character filename limit.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(fileName)
            || !(fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            || fileName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || fileName.Any(char.IsControl)
            || fileName is "." or "..")
            throw new ArgumentException("The detail link must name a sibling .md or .html file, not a path or URL.", nameof(fileName));
        return "./" + Uri.EscapeDataString(fileName);
    }

    public static string SourceList(ImmutableArray<string> keys, ReportRequest request) =>
        Join("; ", keys.Select(key => request.Sources.First(source => source.Key == key).Label));

    public static string Join(string separator, IEnumerable<string> values, CancellationToken token = default)
    {
        var result = new StringBuilder();
        var first = true;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            var separatorLength = first ? 0 : separator.Length;
            if ((long)result.Length + separatorLength + value.Length > ReportRenderingLimits.MaximumRecordCharacters)
                throw new ReportSizeLimitException("Combined report labels exceed the 1 Mi-character text limit. Select fewer sources; no labels were truncated.");
            if (!first) result.Append(separator);
            result.Append(value);
            first = false;
        }
        return result.ToString();
    }
}

internal sealed class ReportIdentityCatalog
{
    private readonly ImmutableArray<ReportIdentity> candidates;
    private readonly ImmutableArray<ReportIdentity> roster;
    private readonly Dictionary<ReportIdentity, (string Key, string Label)> cache = new();
    private readonly CancellationToken token;

    public ReportIdentityCatalog(ReportRequest request, IEnumerable<ReportIdentity> identities, CancellationToken token)
    {
        this.token = token;
        roster = request.Roster;
        var distinct = new HashSet<ReportIdentity>();
        foreach (var identity in request.Roster.Concat(identities))
        {
            token.ThrowIfCancellationRequested();
            distinct.Add(identity);
            if (distinct.Count > ReportRenderingLimits.MaximumIdentities)
                throw new ReportSizeLimitException("The report exceeds the 4,096 distinct identity rendering limit. Select a smaller scope; no identities were omitted.");
        }
        var seed = ImmutableArray.CreateBuilder<ReportIdentity>();
        foreach (var identity in request.Roster.Concat(distinct
            .Where(Strong).OrderByDescending(i => !string.IsNullOrWhiteSpace(i.Id))))
        {
            token.ThrowIfCancellationRequested();
            if (!seed.Contains(identity) && Resolve(identity, seed.ToImmutable()) is null)
                seed.Add(identity);
        }
        candidates = seed.ToImmutable();
    }

    public (string Key, string Label) Get(ReportIdentity identity)
    {
        token.ThrowIfCancellationRequested();
        if (cache.TryGetValue(identity, out var value)) return value;
        var resolved = Resolve(identity, roster) ?? Resolve(identity, candidates);
        var canonical = resolved ?? identity;
        var strong = Strong(canonical);
        var name = canonical.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown";
        var address = new[] { canonical.Email, canonical.PrincipalName, canonical.Name }
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.Contains('@'));
        var id = canonical.Id?.Trim().ToUpperInvariant() ?? "";
        var addresses = new[] { canonical.Email, canonical.PrincipalName, canonical.Name }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Contains('@'))
            .Select(value => value!.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var key = strong
            ? $"identity:{id.Length}:{id}" + string.Concat(addresses.Select(value => $"{value.Length}:{value}"))
            : "unresolved:" + name.Trim();
        var label = name;
        if (!string.IsNullOrWhiteSpace(address) && !name.Equals(address, StringComparison.OrdinalIgnoreCase))
            label += $" <{address}>";
        if (!string.IsNullOrWhiteSpace(canonical.PrincipalName)
            && !string.Equals(canonical.PrincipalName, address, StringComparison.OrdinalIgnoreCase))
            label += $" [principal: {canonical.PrincipalName}]";
        if (!string.IsNullOrWhiteSpace(canonical.Id)) label += $" [ID: {canonical.Id}]";
        if (!strong && resolved is null && !name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            label += " (unresolved identity)";
        value = (key.Trim().ToUpperInvariant(), label);
        cache[identity] = value;
        return value;
    }

    private static bool Strong(ReportIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Id)
        || HasAddress(identity);

    private static bool HasAddress(ReportIdentity identity) =>
        new[] { identity.Email, identity.PrincipalName, identity.Name }
            .Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains('@'));

    private static ReportIdentity? Resolve(ReportIdentity identity, ImmutableArray<ReportIdentity> candidates)
    {
        var resolved = ReportIdentityMatcher.Resolve(identity, candidates);
        // Crossing ID namespaces requires address evidence, not a coincident display name.
        if (resolved is not null && !string.IsNullOrWhiteSpace(identity.Id)
            && !string.IsNullOrWhiteSpace(resolved.Id) && !HasAddress(identity)
            && !string.Equals(identity.Id.Trim(), resolved.Id.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        return resolved;
    }
}
