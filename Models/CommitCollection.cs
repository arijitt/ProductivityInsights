namespace ProductivityInsights.Models.CommitChanges
{
    public class Author
    {
        public string? name { get; set; }
        public string? email { get; set; }
        public DateTime date { get; set; }
    }

    public class ChangeCounts
    {
        public int Add { get; set; }
        public int Edit { get; set; }
        public int Delete { get; set; }
    }

    public class Committer
    {
        public string? name { get; set; }
        public string? email { get; set; }
        public DateTime date { get; set; }
    }

    public class CommitCollection
    {
        public int count { get; set; }
        public List<CommitValue>? value { get; set; }
    }

    public class CommitValue
    {
        public string? commitId { get; set; }
        public Author? author { get; set; }
        public Committer? committer { get; set; }
        public string? comment { get; set; }
        public bool commentTruncated { get; set; }
        public ChangeCounts? changeCounts { get; set; }
        public string? url { get; set; }
        public string? remoteUrl { get; set; }
    }
}
