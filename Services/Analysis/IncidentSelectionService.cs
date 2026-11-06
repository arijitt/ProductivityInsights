using ProductivityInsights.Metrics;
using ProductivityInsights.Models.Incidents;

namespace ProductivityInsights.Services.Analysis;

public enum IncidentSelectionKind { Resolved, Mitigated, Active, Created }

public sealed record IncidentSelectionResult(IncidentCollection Resolved, IncidentCollection Mitigated,
    IncidentCollection Active, IncidentCollection Created);

public interface IIncidentSelectionClient
{
    Task<IncidentCollection?> SearchAsync(IncidentSelectionKind kind, string owningTeam,
        DateTime? start, DateTime? end, CancellationToken cancellationToken);
}

public sealed class IncidentSelectionClient(IncidentManagementMetrics metrics) : IIncidentSelectionClient
{
    public Task<IncidentCollection?> SearchAsync(IncidentSelectionKind kind, string owningTeam,
        DateTime? start, DateTime? end, CancellationToken cancellationToken) => kind switch
    {
        IncidentSelectionKind.Resolved => metrics.SearchResolvedIncidentsAsync(owningTeam, start, end, cancellationToken),
        IncidentSelectionKind.Mitigated => metrics.SearchMitigatedIncidentsAsync(owningTeam, start, end, cancellationToken),
        IncidentSelectionKind.Active => metrics.SearchAllActiveIncidentsAsync(owningTeam, cancellationToken),
        IncidentSelectionKind.Created => metrics.SearchCreatedIncidentsAsync(owningTeam, start, end, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed class IncidentSelectionService(IIncidentSelectionClient client)
{
    public async Task<IncidentSelectionResult> CollectAsync(IReadOnlyList<string> owningTeams,
        DateTime? start, DateTime? end, CancellationToken cancellationToken)
    {
        if (owningTeams.Count == 0) throw new InvalidOperationException("Select at least one owning team.");
        var kinds = Enum.GetValues<IncidentSelectionKind>();
        var collected = kinds.ToDictionary(kind => kind, _ => new Dictionary<(string Silo, string Id), IncidentValue>());
        foreach (var team in owningTeams.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responses = await Task.WhenAll(kinds.Select(kind => client.SearchAsync(kind, team, start, end, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < kinds.Length; index++)
            {
                if (responses[index]?.value is not { } incidents)
                    throw new InvalidOperationException($"Incident {kinds[index]} data is unavailable for {team}. Retry the analysis.");
                foreach (var incident in incidents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(incident.SiloId) || string.IsNullOrWhiteSpace(incident.Id))
                        throw new InvalidOperationException($"Incident identity is incomplete for {team}.");
                    collected[kinds[index]].TryAdd((incident.SiloId, incident.Id), incident);
                }
            }
        }
        IncidentCollection Collection(IncidentSelectionKind kind) => new() { value = collected[kind].Values.ToList() };
        return new(Collection(IncidentSelectionKind.Resolved), Collection(IncidentSelectionKind.Mitigated),
            Collection(IncidentSelectionKind.Active), Collection(IncidentSelectionKind.Created));
    }
}
