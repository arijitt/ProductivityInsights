using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

internal readonly record struct IncidentActionKey(
    string IncidentKey, string ActionId, ReportIdentity? Actor, DateTimeOffset? Timestamp, string? ActionType)
{
    internal static IncidentActionKey From(IncidentActionReportRecord action) =>
        action.ActionId.Length == 0
            ? new(action.IncidentKey, "", action.Actor, action.Timestamp, action.ActionType)
            : new(action.IncidentKey, action.ActionId, null, null, null);
}
