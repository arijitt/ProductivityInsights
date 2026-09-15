using System.Collections.Immutable;
using ProductivityInsights.Models.Reports;
using ProductivityInsights.Services.Reports;

namespace ProductivityInsights.ReportTests.AdoReports;

public static class ReportIdentityMatcherTests
{
    public static void Run()
    {
        var alice = new ReportIdentity("Alice Example", "alice@example.com", "id-a", "alice@corp.example");
        var bob = new ReportIdentity("Bob Example", "bob@example.com", "id-b");
        ImmutableArray<ReportIdentity> roster = [alice, bob];
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("alice"), roster) == alice,
            "ICM aliases must resolve against unique email or UPN local parts.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("other", PrincipalName: "ALICE@CORP.EXAMPLE"), roster) == alice,
            "UPN matching must be exact and case insensitive.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Bob Example", "alice@example.com"), roster) == alice,
            "An exact address must outrank a different display name.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example", "outsider@example.com"), roster) is null,
            "Contradictory email must prevent a name fallback.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example", "outsider@example.com", "id-a"), roster) is null,
            "An ID must not override a contradictory email.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("", Id: "id-a"), roster) == alice,
            "An exact ID must resolve without a name.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new(""), [new(""), alice]) is null,
            "Blank identity keys must never match or throw.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Unknown"), [new("Unknown")]) is null,
            "Unknown labels must not become person identities.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example"), [alice, bob with { Name = alice.Name }]) is null,
            "Name collisions must remain unresolved.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("alice"),
            [alice, new("Another Alice", "alice@other.example")]) is null, "Alias collisions must remain unresolved.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example", "alice@example.com"),
            [alice, new("Alice Example", "different@example.com")]) == alice, "Exact email must disambiguate names.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example", "alice@example.com", "ado-storage-key"),
            roster) == alice, "ADO storage IDs must not veto an exact directory email.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Alice Example", "outsider@example.com",
            PrincipalName: "alice@corp.example"), roster) is null, "A matching UPN must not hide a contradictory email.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Bob Example", "bob@example.com", "id-a"),
            roster) is null, "Conflicting exact IDs and addresses must remain unresolved.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Same Name", Id: "id-b"),
            [new("Same Name", Id: "id-a")]) is null, "An unmatched strong ID must prevent a same-name merge.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("alice", Id: "unmatched-id"),
            roster) is null, "An unmatched strong ID must prevent short-alias fallback without exact address evidence.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("Same Name", Id: "id-b"),
            [new("Same Name")]) is null, "An ID-less name match cannot establish equivalence to a supplied strong ID.");
        AdoReportAssert.That(ReportIdentityMatcher.Resolve(new("alice"), []) is null, "Empty roster must be safe.");
    }
}
