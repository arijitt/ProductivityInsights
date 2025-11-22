namespace ProductivityInsights.Models.WorkItems
{
    using System.Text.Json.Serialization;
    using System;
    using System.Collections.Generic;

    public class Attributes
    {
        public DateTime authorizedDate { get; set; }

        public int id { get; set; }

        public DateTime resourceCreatedDate { get; set; }

        public DateTime resourceModifiedDate { get; set; }

        public DateTime revisedDate { get; set; }

        public string? name { get; set; }

        public string? comment { get; set; }

        public bool? isLocked { get; set; }
    }

    public class Avatar
    {
        public string? href { get; set; }
    }

    public class CommentVersionRef
    {
        public int commentId { get; set; }

        public int version { get; set; }

        public string? url { get; set; }
    }

    public class Fields
    {
        [JsonPropertyName("System.Id")]
        public int SystemId { get; set; }

        [JsonPropertyName("System.AreaId")]
        public int SystemAreaId { get; set; }

        [JsonPropertyName("System.AreaPath")]
        public string? SystemAreaPath { get; set; }

        [JsonPropertyName("System.TeamProject")]
        public string? SystemTeamProject { get; set; }

        [JsonPropertyName("System.NodeName")]
        public string? SystemNodeName { get; set; }

        [JsonPropertyName("System.AreaLevel1")]
        public string? SystemAreaLevel1 { get; set; }

        [JsonPropertyName("System.AreaLevel2")]
        public string? SystemAreaLevel2 { get; set; }

        [JsonPropertyName("System.Rev")]
        public int SystemRev { get; set; }

        [JsonPropertyName("System.AuthorizedDate")]
        public DateTime SystemAuthorizedDate { get; set; }

        [JsonPropertyName("System.RevisedDate")]
        public DateTime SystemRevisedDate { get; set; }

        [JsonPropertyName("System.IterationId")]
        public int SystemIterationId { get; set; }

        [JsonPropertyName("System.IterationPath")]
        public string? SystemIterationPath { get; set; }

        [JsonPropertyName("System.IterationLevel1")]
        public string? SystemIterationLevel1 { get; set; }

        [JsonPropertyName("System.IterationLevel2")]
        public string? SystemIterationLevel2 { get; set; }

        [JsonPropertyName("System.IterationLevel3")]
        public string? SystemIterationLevel3 { get; set; }

        [JsonPropertyName("System.IterationLevel4")]
        public string? SystemIterationLevel4 { get; set; }

        [JsonPropertyName("System.IterationLevel5")]
        public string? SystemIterationLevel5 { get; set; }

        [JsonPropertyName("System.WorkItemType")]
        public string? SystemWorkItemType { get; set; }

        [JsonPropertyName("System.State")]
        public string? SystemState { get; set; }

        [JsonPropertyName("System.Reason")]
        public string? SystemReason { get; set; }

        [JsonPropertyName("System.AssignedTo")]
        public SystemAssignedTo? SystemAssignedTo { get; set; }

        [JsonPropertyName("System.CreatedDate")]
        public DateTime SystemCreatedDate { get; set; }

        [JsonPropertyName("System.CreatedBy")]
        public SystemCreatedBy? SystemCreatedBy { get; set; }

        [JsonPropertyName("System.ChangedDate")]
        public DateTime SystemChangedDate { get; set; }

        [JsonPropertyName("System.ChangedBy")]
        public SystemChangedBy? SystemChangedBy { get; set; }

        [JsonPropertyName("System.AuthorizedAs")]
        public SystemAuthorizedAs? SystemAuthorizedAs { get; set; }

        [JsonPropertyName("System.PersonId")]
        public int SystemPersonId { get; set; }

        [JsonPropertyName("System.Watermark")]
        public int SystemWatermark { get; set; }

        [JsonPropertyName("System.CommentCount")]
        public int SystemCommentCount { get; set; }

        [JsonPropertyName("System.Title")]
        public string? SystemTitle { get; set; }

        [JsonPropertyName("System.BoardColumn")]
        public string? SystemBoardColumn { get; set; }

        [JsonPropertyName("System.BoardColumnDone")]
        public bool SystemBoardColumnDone { get; set; }

        /*
        [JsonPropertyName("Microsoft.VSTS.Common.BacklogPriority")]
        public long? MicrosoftVSTSCommonBacklogPriority { get; set; }
        */

        [JsonPropertyName("Microsoft.VSTS.Common.ClosedDate")]
        public DateTime? MicrosoftVSTSCommonClosedDate { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ClosedBy")]
        public MicrosoftVSTSCommonClosedBy? MicrosoftVSTSCommonClosedBy { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.StateChangeDate")]
        public DateTime? MicrosoftVSTSCommonStateChangeDate { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.Priority")]
        public int? MicrosoftVSTSCommonPriority { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ValueArea")]
        public string? MicrosoftVSTSCommonValueArea { get; set; }

        [JsonPropertyName("WEF_225BC825339E4DFFA1D60197E584F180_System.ExtensionMarker")]
        public bool WEF_225BC825339E4DFFA1D60197E584F180_SystemExtensionMarker { get; set; }

        [JsonPropertyName("WEF_225BC825339E4DFFA1D60197E584F180_Kanban.Column")]
        public string? WEF_225BC825339E4DFFA1D60197E584F180_KanbanColumn { get; set; }

        [JsonPropertyName("WEF_225BC825339E4DFFA1D60197E584F180_Kanban.Column.Done")]
        public bool WEF_225BC825339E4DFFA1D60197E584F180_KanbanColumnDone { get; set; }

        [JsonPropertyName("GlobalMaster.IcMIncidentCount")]
        public int? GlobalMasterIcMIncidentCount { get; set; }

        [JsonPropertyName("GlobalMaster.IcMRepairItemType")]
        public string? GlobalMasterIcMRepairItemType { get; set; }

        [JsonPropertyName("GlobalMaster.IcMDeliveryType")]
        public string? GlobalMasterIcMDeliveryType { get; set; }

        [JsonPropertyName("GlobalMaster.IcMIncidentSeverity")]
        public int? GlobalMasterIcMIncidentSeverity { get; set; }

        [JsonPropertyName("System.Description")]
        public string? SystemDescription { get; set; }

        [JsonPropertyName("GlobalMaster.IcMIncidentIDs")]
        public string? GlobalMasterIcMIncidentIDs { get; set; }

        [JsonPropertyName("System.Tags")]
        public string? SystemTags { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.Severity")]
        public string? MicrosoftVSTSCommonSeverity { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Common.ResolvedBy")]
        public MicrosoftVSTSCommonResolvedBy? MicrosoftVSTSCommonResolvedBy { get; set; }

        [JsonPropertyName("Custom.ResolutionReason")]
        public string? CustomResolutionReason { get; set; }

        [JsonPropertyName("System.History")]
        public string? SystemHistory { get; set; }

        [JsonPropertyName("Microsoft.VSTS.Scheduling.TargetDate")]
        public DateTime? MicrosoftVSTSSchedulingTargetDate { get; set; }

        [JsonPropertyName("System.Parent")]
        public int? SystemParent { get; set; }
        public string? href { get; set; }
    }

    public class Html
    {
        public string? href { get; set; }
    }

    public class Links
    {
        public Avatar? avatar { get; set; }

        public Self? self { get; set; }

        public WorkItemUpdates? workItemUpdates { get; set; }

        public WorkItemRevisions? workItemRevisions { get; set; }

        public WorkItemComments? workItemComments { get; set; }

        public Html? html { get; set; }

        public WorkItemType? workItemType { get; set; }

        public Fields? fields { get; set; }
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

    public class MicrosoftVSTSCommonResolvedBy
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
        [JsonPropertyName("System.Description")]
        public string? SystemDescription { get; set; }

        [JsonPropertyName("GlobalMaster.IcMIncidentIDs")]
        public string? GlobalMasterIcMIncidentIDs { get; set; }

        [JsonPropertyName("System.History")]
        public string? SystemHistory { get; set; }
    }

    public class Relation
    {
        public string? rel { get; set; }

        public string? url { get; set; }

        public Attributes? attributes { get; set; }
    }

    public class WorkItemDetailsCollection
    {
        public int count { get; set; }

        public List<WorkItemValue>? value { get; set; }
    }

    public class Self
    {
        public string? href { get; set; }
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

    public class SystemAuthorizedAs
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

    public class WorkItemValue
    {
        public int id { get; set; }

        public int rev { get; set; }
        public Fields? fields { get; set; }

        public MultilineFieldsFormat? multilineFieldsFormat { get; set; }

        public Links? _links { get; set; }

        public string? url { get; set; }

        public List<Relation>? relations { get; set; }

        public CommentVersionRef? commentVersionRef { get; set; }
    }

    public class WorkItemComments
    {
        public string? href { get; set; }
    }

    public class WorkItemRevisions
    {
        public string? href { get; set; }
    }

    public class WorkItemType
    {
        public string? href { get; set; }
    }

    public class WorkItemUpdates
    {
        public string? href { get; set; }
    }
}


