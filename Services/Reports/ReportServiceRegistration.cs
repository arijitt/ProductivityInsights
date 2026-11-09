using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ProductivityInsights.Options;
using ProductivityInsights.Utilities;

namespace ProductivityInsights.Services.Reports;

public static class ReportServiceRegistration
{
    public static IServiceCollection AddReportExports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("ReportExportAdo", client => client.Timeout = TimeSpan.FromMinutes(3));
        services.AddScoped(provider => new AdoReportCollector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ReportExportAdo"),
            async token => (await UserToken.GetToken().WaitAsync(token)).Token));
        services.AddKeyedScoped<AdoReportCollectionLimits>("workItems",
            (_, _) => ReadWorkItemCollectionLimits(configuration));
        services.AddKeyedScoped<AdoReportCollector>("workItems", (provider, _) => new AdoReportCollector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ReportExportAdo"),
            async token => (await UserToken.GetToken().WaitAsync(token)).Token,
            provider.GetRequiredKeyedService<AdoReportCollectionLimits>("workItems")));
        services.AddScoped<IOptions<IncidentReportCoverage>>(_ =>
            Microsoft.Extensions.Options.Options.Create(ReadIncidentCoverage(configuration)));
        services.AddScoped<IIncidentReportCoverageProvider>(provider => new IncidentReportConfiguredCoverageProvider(
            () => provider.GetRequiredService<IOptions<IncidentReportCoverage>>().Value));
        services.AddScoped<IIncidentReportQueryClient>(provider => new IncidentReportQueryClient(
            provider.GetRequiredService<IOptions<IncidentManagementKustoOptions>>().Value,
            provider.GetRequiredService<IIncidentReportCoverageProvider>()));
        services.AddScoped(provider => new IncidentReportCollector(
            provider.GetRequiredService<IOptions<IncidentManagementKustoOptions>>(),
            provider.GetRequiredService<IIncidentReportQueryClient>()));
        services.AddScoped<IReportCollector>(provider => new ReportCollectionService(
            () => provider.GetRequiredService<AdoReportCollector>(),
            () => provider.GetRequiredService<IncidentReportCollector>(),
            () => provider.GetRequiredKeyedService<AdoReportCollector>("workItems")));
        return services;
    }

    private static AdoReportCollectionLimits ReadWorkItemCollectionLimits(IConfiguration configuration)
    {
        const string key = "ReportExport:WorkItems:MaxCollectionBytes";
        var maximum = configuration.GetValue<long?>(key) ?? 512L * 1024 * 1024;
        if (maximum <= 0)
            throw new InvalidOperationException($"{key} must be a positive number of bytes.");
        return new AdoReportCollectionLimits { MaxCollectionBytes = maximum };
    }

    private static IncidentReportCoverage ReadIncidentCoverage(IConfiguration configuration)
    {
        const string sectionName = "IncidentReportCoverage";
        var section = configuration.GetSection(sectionName);
        string Required(string name)
        {
            var value = section[name];
            if (string.IsNullOrWhiteSpace(value))
                throw new IncidentReportExportException(
                    $"ICM export coverage is unconfigured: {sectionName}:{name}. Obtain source-owner retained-window and purge evidence before enabling export.");
            return value.Trim();
        }
        DateTimeOffset Timestamp(string name)
        {
            var value = Required(name);
            if (!Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                throw new IncidentReportExportException(
                    $"{sectionName}:{name} requires a timestamp with an explicit UTC offset.");
            return result;
        }
        TimeSpan Retention(string table)
        {
            var name = $"MinimumRetention:{table}";
            if (!TimeSpan.TryParseExact(Required(name), "c", CultureInfo.InvariantCulture, out var value)
                || value <= TimeSpan.Zero)
                throw new IncidentReportExportException(
                    $"{sectionName}:{name} requires a positive constant-format TimeSpan.");
            return value;
        }
        var teams = section.GetSection("Teams").Get<string[]>() ?? [];
        if (teams.Length == 0 || teams.Any(string.IsNullOrWhiteSpace))
            throw new IncidentReportExportException(
                $"{sectionName}:Teams requires the owning teams covered by the source evidence.");

        return new IncidentReportCoverage(
            Required("ClusterUri"), Required("Database"), Required("SiloId"),
            teams.Select(team => team.Trim()).ToImmutableArray(),
            Timestamp("HistoryFrom"), Timestamp("HistoryThrough"),
            Timestamp("LatestIncidentsThrough"), Timestamp("PurgesThrough"),
            Required("Evidence"), Required("RetentionSemantics"))
        {
            MinimumRetention = IncidentReportSourceVerification.RequiredSchema.Keys.ToImmutableDictionary(
                table => table, Retention, StringComparer.Ordinal)
        };
    }
}
