namespace ProductivityInsights.Services.Reports;

public sealed record AdoReportCollectionLimits
{
    // Limits apply to bytes read from decoded HTTP response streams, before JSON parsing.
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxCollectionBytes { get; init; } = 128L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxResponseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes), "The response byte limit must be positive.");
        if (MaxCollectionBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCollectionBytes), "The collection byte limit must be positive.");
    }
}
