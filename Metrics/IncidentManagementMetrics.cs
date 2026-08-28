namespace ProductivityInsights.Metrics
{
    using Azure.Identity;
    using Kusto.Data;
    using Kusto.Data.Common;
    using Kusto.Data.Net.Client;
    using Microsoft.Extensions.Options;
    using ProductivityInsights.Models.Incidents;
    using ProductivityInsights.Options;
    using System.Data;

    public sealed class IncidentManagementMetrics : IDisposable
    {
        private const string QueryPrefix = """
            declare query_parameters(owningTeam:string, startDate:datetime, endDate:datetime);
            let scopedIncidents = Incidents
            | where tostring(SiloId) == "1";
            let candidateIds = scopedIncidents
            | where OwningTeamName == owningTeam
            | distinct IncidentId;
            candidateIds
            | join kind=inner hint.strategy=broadcast (scopedIncidents) on IncidentId
            | summarize arg_max(ModifiedDate, *) by IncidentId
            | where OwningTeamName == owningTeam
            | join kind=leftanti (PurgedIncidents | distinct IncidentId) on IncidentId
            """;

        private const string Projection = """
            | project
                Id=tostring(IncidentId),
                Severity=toint(Severity),
                Status=tostring(Status),
                CreateDate=todatetime(CreateDate),
                ImpactStartDate=todatetime(ImpactStartDate),
                ModifiedDate=todatetime(ModifiedDate),
                Title=tostring(Title),
                OwningContactAlias=tostring(OwningContactAlias),
                IsOutage=tobool(IsOutage),
                IsNoise=tobool(IsNoise),
                SiloId=tostring(SiloId),
                SourceOrigin=tostring(SourceOrigin),
                MitigateDate=todatetime(MitigateDate),
                MitigatedBy=tostring(MitigatedBy),
                Mitigation=tostring(Mitigation),
                ResolveDate=todatetime(ResolveDate),
                ResolvedBy=tostring(ResolvedBy)
            """;

        private readonly IncidentManagementKustoOptions options;
        private readonly ICslQueryProvider queryProvider;

        public IncidentManagementMetrics(IOptions<IncidentManagementKustoOptions> options)
        {
            this.options = options.Value;

            var connectionString = new KustoConnectionStringBuilder(
                    this.options.ClusterUri,
                    this.options.Database)
                .WithAadAzureTokenCredentialsAuthentication(new DefaultAzureCredential());

            queryProvider = KustoClientFactory.CreateCslQueryProvider(connectionString);
        }

        public Task<IncidentCollection?> SearchAllActiveIncidentsAsync(
            string owningTeamName,
            CancellationToken cancellationToken = default)
        {
            string query = $"""
                {QueryPrefix}
                | where Status =~ "ACTIVE"
                {Projection}
                """;

            return ExecuteIncidentQueryAsync(
                query,
                owningTeamName,
                startDate: null,
                endDate: null,
                cancellationToken);
        }

        public Task<IncidentCollection?> SearchCreatedIncidentsAsync(
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate,
            CancellationToken cancellationToken = default)
        {
            string query = $"""
                {QueryPrefix}
                | where CreateDate between (startDate .. endDate)
                {Projection}
                """;

            return ExecuteIncidentQueryAsync(
                query,
                owningTeamName,
                startDate,
                endDate,
                cancellationToken);
        }

        public Task<IncidentCollection?> SearchMitigatedIncidentsAsync(
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate,
            CancellationToken cancellationToken = default)
        {
            string query = $"""
                {QueryPrefix}
                | where Status =~ "MITIGATED"
                | where MitigateDate between (startDate .. endDate)
                {Projection}
                """;

            return ExecuteIncidentQueryAsync(
                query,
                owningTeamName,
                startDate,
                endDate,
                cancellationToken);
        }

        public Task<IncidentCollection?> SearchResolvedIncidentsAsync(
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate,
            CancellationToken cancellationToken = default)
        {
            string query = $"""
                {QueryPrefix}
                | where Status =~ "RESOLVED"
                | where ResolveDate between (startDate .. endDate)
                {Projection}
                """;

            return ExecuteIncidentQueryAsync(
                query,
                owningTeamName,
                startDate,
                endDate,
                cancellationToken);
        }

        public void Dispose()
        {
            queryProvider.Dispose();
        }

        private async Task<IncidentCollection?> ExecuteIncidentQueryAsync(
            string query,
            string owningTeamName,
            DateTime? startDate,
            DateTime? endDate,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(owningTeamName))
            {
                Console.WriteLine("Incident Management Kusto query requires an owning team.");
                return null;
            }

            try
            {
                var requestProperties = new ClientRequestProperties
                {
                    ClientRequestId = $"ProductivityInsights.IncidentManagement;{Guid.NewGuid()}"
                };

                requestProperties.SetParameter("owningTeam", owningTeamName.Trim());
                requestProperties.SetParameter(
                    "startDate",
                    ToUtc(startDate ?? DateTime.UnixEpoch));
                requestProperties.SetParameter(
                    "endDate",
                    ToUtc(endDate ?? DateTime.UtcNow));
                requestProperties.SetOption(
                    ClientRequestProperties.OptionServerTimeout,
                    TimeSpan.FromSeconds(options.QueryTimeoutSeconds));
                requestProperties.SetOption(
                    ClientRequestProperties.OptionRequestReadOnly,
                    true);

#if DEBUG
                Console.WriteLine(
                    $"Querying Incident Management data from {options.ClusterUri}/{options.Database} " +
                    $"for owning team '{owningTeamName}'.");
#endif

                using IDataReader reader = await queryProvider.ExecuteQueryAsync(
                    options.Database,
                    query,
                    requestProperties,
                    cancellationToken);

                var incidents = new List<IncidentValue>();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    incidents.Add(MapIncident(reader));
                }

                return new IncidentCollection { value = incidents };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying Incident Management data from Kusto: {ex.Message}");
                return null;
            }
        }

        private static IncidentValue MapIncident(IDataRecord record)
        {
            DateTime? mitigateDate = GetNullableDateTime(record, "MitigateDate");
            DateTime? resolveDate = GetNullableDateTime(record, "ResolveDate");

            return new IncidentValue
            {
                Id = GetNullableString(record, "Id"),
                Severity = GetNullableInt32(record, "Severity"),
                Status = NormalizeStatus(GetNullableString(record, "Status")),
                CreateDate = GetNullableDateTime(record, "CreateDate") ?? default,
                ImpactStartDate = GetNullableDateTime(record, "ImpactStartDate") ?? default,
                ModifiedDate = GetNullableDateTime(record, "ModifiedDate") ?? default,
                Title = GetNullableString(record, "Title"),
                OwningContactAlias = GetNullableString(record, "OwningContactAlias"),
                IsOutage = GetNullableBoolean(record, "IsOutage") ?? false,
                IsNoise = GetNullableBoolean(record, "IsNoise") ?? false,
                SiloId = GetNullableString(record, "SiloId"),
                SourceOrigin = GetNullableString(record, "SourceOrigin"),
                MitigationData = mitigateDate.HasValue
                    ? new MitigationData
                    {
                        Date = mitigateDate.Value,
                        ChangedBy = GetNullableString(record, "MitigatedBy"),
                        Mitigation = GetNullableString(record, "Mitigation")
                    }
                    : null,
                ResolutionData = resolveDate.HasValue
                    ? new ResolutionData
                    {
                        Date = resolveDate.Value,
                        ChangedBy = GetNullableString(record, "ResolvedBy")
                    }
                    : null
            };
        }

        private static DateTime ToUtc(DateTime value)
        {
            // Preserve the existing ICM API behavior, which treats the selected wall-clock
            // value as UTC rather than converting it through the server's local timezone.
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string? GetNullableString(IDataRecord record, string columnName)
        {
            int ordinal = record.GetOrdinal(columnName);
            return record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal));
        }

        private static int? GetNullableInt32(IDataRecord record, string columnName)
        {
            int ordinal = record.GetOrdinal(columnName);
            return record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));
        }

        private static bool? GetNullableBoolean(IDataRecord record, string columnName)
        {
            int ordinal = record.GetOrdinal(columnName);
            return record.IsDBNull(ordinal) ? null : Convert.ToBoolean(record.GetValue(ordinal));
        }

        private static DateTime? GetNullableDateTime(IDataRecord record, string columnName)
        {
            int ordinal = record.GetOrdinal(columnName);
            if (record.IsDBNull(ordinal))
            {
                return null;
            }

            DateTime value = Convert.ToDateTime(record.GetValue(ordinal));
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string? NormalizeStatus(string? status)
        {
            return status?.ToUpperInvariant() switch
            {
                "ACTIVE" => "Active",
                "MITIGATED" => "Mitigated",
                "RESOLVED" => "Resolved",
                "HOLDING" => "Holding",
                "NEW" => "New",
                "CORRELATING" => "Correlating",
                _ => status
            };
        }
    }
}
