namespace ProductivityInsights.Options
{
    public class IncidentManagementKustoOptions
    {
        public const string SectionName = "IncidentManagementKusto";

        public string ClusterUri { get; set; } = string.Empty;

        public string Database { get; set; } = string.Empty;

        public int QueryTimeoutSeconds { get; set; } = 180;
    }
}
