namespace ProductivityInsights.Models
{
    public class GitCommit
    {
        public string? Organization { get; set; }

        public string? ProjectName { get; set; }

        public string? RepositoryId { get; set; }

        public string? CommitId { get; set; }

        public string? Comment { get; set; }

        public string? AuthorName { get; set; }

        public string? AuthorEmail { get; set; }

        public DateTime AuthorDate { get; set; }

        public string? CommitterName { get; set; }

        public string? CommitterEmail { get; set; }

        public DateTime CommitterDate { get; set; }

        public string? Url { get; set; }

        public string? RemoteUrl { get; set; }

        public Dictionary<string, int> ChangeCounts { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, CommitTypes> ChangedFilesCommitTypes { get; set; } = new Dictionary<string, CommitTypes>();

        public Dictionary<string, int> ChangedFilesTotalLineCounts { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, int> ChangedFilesEmptyLineCounts { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, int> ChangedFilesEffectiveLineCounts { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, int> ChangedFilesAddedLineCounts { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, int> ChangedFilesDeletedLineCounts { get; set; } = new Dictionary<string, int>();

        public int AddedLines { get; set; }

        public int DeletedLines { get; set; }

        public bool IsMergeCommit { get; set; }

        public List<string> ParentCommitIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// Represents a collection of Git commits
    /// </summary>
    public class GitCommitCollection
    {
        public List<GitCommit> Value { get; set; } = new List<GitCommit>();
        public int Count { get; set; }
    }
}
