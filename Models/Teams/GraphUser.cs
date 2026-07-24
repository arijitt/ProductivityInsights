namespace ProductivityInsights.Models.Teams
{
    /// <summary>
    /// DTO for a Microsoft Graph user returned by directory/search and directReports calls.
    /// Property names match the Graph JSON response (camelCase).
    /// </summary>
    public class GraphUser
    {
        public string? id { get; set; }

        public string? displayName { get; set; }

        public string? mail { get; set; }

        public string? userPrincipalName { get; set; }

        public string? jobTitle { get; set; }

        public string? department { get; set; }
    }
}
