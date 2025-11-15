namespace ProductivityInsights.Options
{
    public class CommitQueryOptions
    {
        public string OrganizationName { get; set; }  = string.Empty;

        public string ProjectName { get; set; } = string.Empty;

        public string GitRepositoryName { get; set; } = string.Empty;  
         
        public string TargetBranchName { get; set; } = string.Empty;

        public DateTime? StartDateTime { get; set; }

        public DateTime? EndDateTime { get; set; }    
    }
}
