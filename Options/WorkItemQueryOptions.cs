namespace ProductivityInsights.Options
{
    public class WorkItemQueryOptions
    {
        public string OrganizationName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public string AreaPath { get; set; } = string.Empty;
        public DateTime? StartDateTime { get; set; }
        public DateTime? EndDateTime { get; set; }
    }
}
