namespace ProductivityInsights.Models.WorkItems
{
    public class WIQLQueryResult
    {
        public string? QueryType { get; set; }

        public string? QueryResultType { get; set; }

        public DateTime AsOf { get; set; }

        public List<WorkItemReference>? WorkItems { get; set; }
    }
}
