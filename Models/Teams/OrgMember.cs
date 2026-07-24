namespace ProductivityInsights.Models.Teams
{
    /// <summary>
    /// A node in the manager reporting hierarchy. Holds the user's identity fields plus
    /// the list of (recursively populated) direct reports.
    /// </summary>
    public class OrgMember
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Mail { get; set; }

        public string? UserPrincipalName { get; set; }

        public string? JobTitle { get; set; }

        public string? Department { get; set; }

        public List<OrgMember> Reports { get; set; } = new List<OrgMember>();

        public bool HasReports => Reports.Count > 0;

        public static OrgMember FromGraphUser(GraphUser user)
        {
            return new OrgMember
            {
                Id = user.id,
                DisplayName = user.displayName,
                Mail = user.mail,
                UserPrincipalName = user.userPrincipalName,
                JobTitle = user.jobTitle,
                Department = user.department,
            };
        }
    }
}
