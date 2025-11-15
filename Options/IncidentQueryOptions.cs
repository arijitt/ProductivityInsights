namespace ProductivityInsights.Options
{
    public class IncidentQueryOptions
    {
        public string OwningTeam { get; set; } = string.Empty;
        public string KeyVaultUrl { get; set; } = string.Empty;
        public string KeyVaultCertificateName { get; set; } = string.Empty;
        public DateTime? StartDateTime { get; set; }
        public DateTime? EndDateTime { get; set; }
    }
}
