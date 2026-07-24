namespace ProductivityInsights.Models.Teams
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Wraps a Microsoft Graph collection response (users / directReports), including
    /// the OData pagination link.
    /// </summary>
    public class GraphUserCollection
    {
        public List<GraphUser>? value { get; set; }

        [JsonPropertyName("@odata.nextLink")]
        public string? odatanextLink { get; set; }
    }
}
