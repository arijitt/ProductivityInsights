namespace ProductivityInsights.Models.WorkItems.HistoryCollection
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    public class Fields
    {
        [JsonPropertyName("System.WorkItemType")]
        public string? SystemWorkItemType { get; set; }

        [JsonPropertyName("System.State")]
        public string? SystemState { get; set; }

        [JsonPropertyName("System.Reason")]
        public string? SystemReason { get; set; }

        [JsonPropertyName("System.CreatedDate")]
        public DateTime SystemCreatedDate { get; set; }

        [JsonPropertyName("System.CreatedBy")]
        public SystemCreatedBy? SystemCreatedBy { get; set; }

        [JsonPropertyName("System.ChangedDate")]
        public DateTime SystemChangedDate { get; set; }

        [JsonPropertyName("System.ChangedBy")]
        public SystemChangedBy? SystemChangedBy { get; set; }

        [JsonPropertyName("System.CommentCount")]
        public int SystemCommentCount { get; set; }

        [JsonPropertyName("System.TeamProject")]
        public string? SystemTeamProject { get; set; }

        [JsonPropertyName("System.AreaPath")]
        public string? SystemAreaPath { get; set; }

        [JsonPropertyName("System.IterationPath")]
        public string? SystemIterationPath { get; set; }

        [JsonPropertyName("System.Title")]
        public string? SystemTitle { get; set; }

        [JsonPropertyName("System.BoardColumnDone")]
        public bool SystemBoardColumnDone { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.Priority")]
        public int MicrosoftVSTSCommonPriority { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ValueArea")]
        public string? MicrosoftVSTSCommonValueArea { get; set; }

        [JsonPropertyName("WEF_225BC825339E4DFFA1D60197E584F180_Kanban.Column.Done")]
        public bool WEF_225BC825339E4DFFA1D60197E584F180_KanbanColumnDone { get; set; }

        [JsonPropertyName("System.BoardColumn")]
        public string? SystemBoardColumn { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.StateChangeDate")]
        public DateTime MicrosoftVSTSCommonStateChangeDate { get; set; }

        [JsonPropertyName("WEF_225BC825339E4DFFA1D60197E584F180_Kanban.Column")]
        public string? WEF_225BC825339E4DFFA1D60197E584F180_KanbanColumn { get; set; }

        /*
        [JsonPropertyName("Microsoft.VSTS.Common.BacklogPriority")]
        public int? MicrosoftVSTSCommonBacklogPriority { get; set; }
        */

        [JsonPropertyName("System.AssignedTo")]
        public SystemAssignedTo? SystemAssignedTo { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ClosedDate")]
        public DateTime? MicrosoftVSTSCommonClosedDate { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ClosedBy")]
        public MicrosoftVSTSCommonClosedBy? MicrosoftVSTSCommonClosedBy { get; set; }
    }

    public class Links
    {
        public Avatar? avatar { get; set; }
    }

    public class MicrosoftVSTSCommonClosedBy
    {
        public string? displayName { get; set; }
        public string? url { get; set; }
        public Links? _links { get; set; }
        public string? id { get; set; }
        public string? uniqueName { get; set; }
        public string? imageUrl { get; set; }
        public string? descriptor { get; set; }
    }

    public class MultilineFieldsFormat
    {
    }

    public class WorkItemHistoryCollection
    {
        public int count { get; set; }
        public List<Value>? value { get; set; }
    }

    public class SystemAssignedTo
    {
        public string? displayName { get; set; }
        public string? url { get; set; }
        public Links? _links { get; set; }
        public string? id { get; set; }
        public string? uniqueName { get; set; }
        public string? imageUrl { get; set; }
        public string? descriptor { get; set; }
    }

    public class SystemChangedBy
    {
        public string? displayName { get; set; }
        public string? url { get; set; }
        public Links? _links { get; set; }
        public string? id { get; set; }
        public string? uniqueName { get; set; }
        public string? imageUrl { get; set; }
        public string? descriptor { get; set; }
    }

    public class SystemCreatedBy
    {
        public string? displayName { get; set; }
        public string? url { get; set; }
        public Links? _links { get; set; }
        public string? id { get; set; }
        public string? uniqueName { get; set; }
        public string? imageUrl { get; set; }
        public string? descriptor { get; set; }
    }

    public class Value
    {
        public int id { get; set; }
        public int rev { get; set; }
        public Fields? fields { get; set; }
        public MultilineFieldsFormat? multilineFieldsFormat { get; set; }
        public string? url { get; set; }
    }
}
