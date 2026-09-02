using System.Text.Json.Serialization;

namespace ProductivityInsights.Models.Incidents
{
    using System.Text.Json.Serialization;
    using System;
    using System.Collections.Generic;

    public class AcknowledgementData
    {
        public bool IsAcknowledged { get; set; }

        public DateTime? AcknowledgeDate { get; set; }

        public string? AcknowledgeContactAlias { get; set; }

        public object? NotificationId { get; set; }

        public object? NotificationToken { get; set; }

        public object? AcknowledgeSource { get; set; }
    }

    public class IncidentLocation
    {
        public string? Environment { get; set; }

        public string? DataCenter { get; set; }

        public string? DeviceGroup { get; set; }

        public string? DeviceName { get; set; }

        public object? ServiceInstanceId { get; set; }
    }

    public class MitigationData
    {
        public DateTime Date { get; set; }

        public string? ChangedBy { get; set; }

        public string? Mitigation { get; set; }
    }

    public class RaisingLocation
    {
        public string? Environment { get; set; }

        public string? DataCenter { get; set; }

        public string? DeviceGroup { get; set; }

        public string? DeviceName { get; set; }

        public object? ServiceInstanceId { get; set; }
    }

    public class ResolutionData
    {
        public DateTime Date { get; set; }

        public string? ChangedBy { get; set; }

        public bool CreatePostmortem { get; set; }
    }

    public class IncidentCollection
    {
        [JsonPropertyName("odata.metadata")]
        public string? odatametadata { get; set; }

        public List<IncidentValue>? value { get; set; }

        [JsonPropertyName("odata.nextLink")]
        public string? odatanextLink { get; set; }
    }

    public class Source
    {
        public string? SourceId { get; set; }

        public string? Origin { get; set; }

        public string? CreatedBy { get; set; }

        public DateTime CreateDate { get; set; }

        public string? IncidentId { get; set; }

        public DateTime ModifiedDate { get; set; }

        public object? Revision { get; set; }
    }

    public class IncidentValue
    {
        public string? Id { get; set; }

        public int? Severity { get; set; }

        public string? Status { get; set; }

        public DateTime CreateDate { get; set; }

        public DateTime ModifiedDate { get; set; }

        public Source? Source { get; set; }

        public string? CorrelationId { get; set; }

        public string? RoutingId { get; set; }

        public RaisingLocation? RaisingLocation { get; set; }

        public IncidentLocation? IncidentLocation { get; set; }

        public string? ParentIncidentId { get; set; }

        public string? RelatedLinksCount { get; set; }

        public string? ExternalLinksCount { get; set; }

        public DateTime? LastCorrelationDate { get; set; }

        public string? HitCount { get; set; }

        public string? ChildCount { get; set; }

        public string? Title { get; set; }

        public object? ReproSteps { get; set; }

        public string? OwningContactAlias { get; set; }

        public string? OwningTenantId { get; set; }

        public string? OwningTeamId { get; set; }

        public string? OwningTeamName { get; set; }

        public MitigationData? MitigationData { get; set; }

        public ResolutionData? ResolutionData { get; set; }

        public bool IsCustomerImpacting { get; set; }

        public bool IsNoise { get; set; }

        public bool IsSecurityRisk { get; set; }

        public string? TsgId { get; set; }

        public object? CustomerName { get; set; }

        public object? CommitDate { get; set; }

        public string? Keywords { get; set; }

        public object? Component { get; set; }

        public string? IncidentType { get; set; }

        public DateTime ImpactStartDate { get; set; }

        public string? OriginatingTenantId { get; set; }

        public string? SubscriptionId { get; set; }

        public string? SupportTicketId { get; set; }

        public string? MonitorId { get; set; }

        public object? IncidentSubType { get; set; }

        public string? HowFixed { get; set; }

        public object? TsgOutput { get; set; }

        public string? SourceOrigin { get; set; }

        public string? ResponsibleTenantId { get; set; }

        public string? ResponsibleTeamId { get; set; }

        public List<object>? ImpactedServicesIds { get; set; }

        public List<object>? ImpactedTeamsPublicIds { get; set; }

        public List<object>? ImpactedComponents { get; set; }

        public object? NewDescriptionEntry { get; set; }

        public AcknowledgementData? AcknowledgementData { get; set; }

        public object? ReactivationData { get; set; }

        public List<object>? CustomFieldGroups { get; set; }

        public List<object>? ExternalIncidents { get; set; }

        public string? SiloId { get; set; }

        public object? IncidentManagerContactId { get; set; }

        public object? ExecutiveIncidentManagerContactId { get; set; }

        public object? CommunicationsManagerContactId { get; set; }

        public object? SiteReliabilityContactId { get; set; }

        public object? HealthResourceId { get; set; }

        public object? DiagnosticsLink { get; set; }

        public object? ChangeList { get; set; }

        public bool IsOutage { get; set; }

        public object? OutageImpactLevel { get; set; }

        public string? Summary { get; set; }

        public List<object>? Tags { get; set; }

        public bool IsCustomerSupportEngagement { get; set; }
    }
}
