using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;

namespace ProductivityInsights.Services.Reports;

public static class ReportIdentityMatcher
{
    public static bool Matches(ReportIdentity identity, ReportRequest request) =>
        !request.IsTeams || Resolve(identity, request.Roster) is not null;

    public static ReportIdentity? Resolve(ReportIdentity identity, ImmutableArray<ReportIdentity> roster)
    {
        if (roster.IsDefaultOrEmpty)
            return null;

        var candidates = roster.Distinct().ToArray();
        var addresses = Addresses(identity);
        var exactIds = candidates.Where(candidate => Equal(identity.Id, candidate.Id)).ToArray();
        if (exactIds.Length != 0)
            return Unique(exactIds.Where(candidate => !Contradicts(identity, candidate)).ToArray());
        var exact = candidates.Where(candidate => addresses.Overlaps(Addresses(candidate))).ToArray();
        if (exact.Length != 0)
            return Unique(exact.Where(candidate => !Contradicts(identity, candidate)).ToArray());

        // Unmatched strong evidence must not degrade to an alias or display-name guess.
        if (addresses.Count != 0 || !string.IsNullOrWhiteSpace(identity.Id))
            return null;

        var name = Normalize(identity.Name);
        var principal = Normalize(identity.PrincipalName);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsAlias(name)) aliases.Add(name);
        if (IsAlias(principal)) aliases.Add(principal);
        var byAlias = candidates.Where(candidate =>
            aliases.Overlaps(Aliases(candidate))).ToArray();
        if (byAlias.Length != 0)
            return Unique(byAlias.Where(candidate => !Contradicts(identity, candidate)).ToArray());

        if (string.IsNullOrEmpty(name) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
            return null;
        return Unique(candidates.Where(candidate => Equal(name, candidate.Name)
            && !Contradicts(identity, candidate)).ToArray());
    }

    private static ReportIdentity? Unique(ReportIdentity[] candidates) =>
        candidates.Length == 1 ? candidates[0] : null;

    private static bool Contradicts(ReportIdentity supplied, ReportIdentity candidate)
    {
        var addresses = Addresses(supplied);
        var expected = Addresses(candidate);
        // ADO identity IDs and directory object IDs need not use the same namespace.
        return addresses.Count != 0 && expected.Count != 0
            && addresses.Any(address => !expected.Contains(address));
    }

    private static HashSet<string> Addresses(ReportIdentity identity)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { identity.Email, identity.PrincipalName, identity.Name })
        {
            var normalized = Normalize(value);
            if (normalized.Contains('@'))
                result.Add(normalized);
        }
        return result;
    }

    private static HashSet<string> Aliases(ReportIdentity identity)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in Addresses(identity))
            result.Add(address[..address.IndexOf('@')]);
        var principal = Normalize(identity.PrincipalName);
        if (IsAlias(principal)) result.Add(principal);
        return result;
    }

    private static bool IsAlias(string value) => value.Length != 0
        && !value.Contains('@') && !value.Any(char.IsWhiteSpace)
        && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => value?.Trim() ?? "";
}
