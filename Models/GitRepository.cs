namespace ProductivityInsights.Models
{
    public class Commits
    {
        public string? href { get; set; }
    }

    public class Items
    {
        public string? href { get; set; }
    }

    public class LinkCollection
    {
        public SelfReference? self { get; set; }
        public ProjectDetails? project { get; set; }
        public WebReference? web { get; set; }
        public Ssh? ssh { get; set; }
        public Commits? commits { get; set; }
        public Refs? refs { get; set; }
        public PullRequests? pullRequests { get; set; }
        public Items? items { get; set; }
        public Pushes? pushes { get; set; }
    }

    public class ProjectDetails
    {
        public string? id { get; set; }
        public string? name { get; set; }
        public string? description { get; set; }
        public string? url { get; set; }
        public string? state { get; set; }
        public int? revision { get; set; }
        public string? visibility { get; set; }
        public DateTime lastUpdateTime { get; set; }
        public string? href { get; set; }
    }

    public class PullRequests
    {
        public string? href { get; set; }
    }

    public class Pushes
    {
        public string? href { get; set; }
    }

    public class Refs
    {
        public string? href { get; set; }
    }

    public class GitRepository
    {
        public string? id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public ProjectDetails? project { get; set; }
        public string? defaultBranch { get; set; }
        public long size { get; set; }
        public string? remoteUrl { get; set; }
        public string? sshUrl { get; set; }
        public string? webUrl { get; set; }
        public LinkCollection? _links { get; set; }
        public bool isDisabled { get; set; }
        public bool isInMaintenance { get; set; }
    }

    public class SelfReference
    {
        public string? href { get; set; }
    }

    public class Ssh
    {
        public string? href { get; set; }
    }

    public class WebReference
    {
        public string? href { get; set; }
    }
}

