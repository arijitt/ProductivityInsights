namespace ProductivityInsights.Models.CommitChanges
{
    public enum ChangeTypes
    {
        Unchanged = 0,
        Addition = 1,
        Deletion = 2,
        Modification = 3
    }

    public class Block
    {
        public int changeType { get; set; }
        public int mLine { get; set; }
        public List<string>? mLines { get; set; }
        public int mLinesCount { get; set; }
        public int oLine { get; set; }
        public List<string>? oLines { get; set; }
        public int oLinesCount { get; set; }
        public bool truncatedBefore { get; set; }
        public bool? truncatedAfter { get; set; }
    }

    public class CharChange
    {
        public int changeType { get; set; }
        public int mLine { get; set; }
        public int mLinesCount { get; set; }
        public int oLine { get; set; }
        public int oLinesCount { get; set; }
    }

    public class ContentMetadata
    {
        public string? contentType { get; set; }
        public int encoding { get; set; }
        public string? extension { get; set; }
        public string? fileName { get; set; }
        public string? vsLink { get; set; }
    }

    public class LineChange
    {
        public int changeType { get; set; }
        public int mLine { get; set; }
        public List<string>? mLines { get; set; }
        public int mLinesCount { get; set; }
        public int oLine { get; set; }
        public List<string>? oLines { get; set; }
        public int oLinesCount { get; set; }
        public bool truncatedBefore { get; set; }
        public bool? truncatedAfter { get; set; }
    }

    public class LineCharBlock
    {
        public List<CharChange>? charChange { get; set; }
        public LineChange? lineChange { get; set; }
    }

    public class ModifiedFile
    {
        public string? __type { get; set; }
        public ContentMetadata? contentMetadata { get; set; }
        public string? serverItem { get; set; }
        public string? version { get; set; }
        public string? versionDescription { get; set; }
        public object? commitId { get; set; }
        public int gitObjectType { get; set; }
        public ObjectId? objectId { get; set; }
    }

    public class ObjectId
    {
        public string? full { get; set; }
        public string? @short { get; set; }
    }

    public class OriginalFile
    {
        public string? __type { get; set; }
        public ContentMetadata? contentMetadata { get; set; }
        public string? serverItem { get; set; }
        public string? version { get; set; }
        public string? versionDescription { get; set; }
        public object? commitId { get; set; }
        public int gitObjectType { get; set; }
        public ObjectId? objectId { get; set; }
    }

    public class CommitLineChangeDetails
    {
        public List<Block>? blocks { get; set; }
        public List<LineCharBlock>? lineCharBlocks { get; set; }
        public ModifiedFile? modifiedFile { get; set; }
        public string? modifiedFileEncoding { get; set; }
        public OriginalFile? originalFile { get; set; }
        public string? originalFileEncoding { get; set; }
    }
}
