using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class CertificateTests
{
    [Fact]
    public void Canonical_json_has_the_same_text_and_hash_for_different_property_order()
    {
        var first = CanonicalJson.Canonicalise("""{"z":3,"nested":{"b":2,"a":1},"a":"same"}""");
        var second = CanonicalJson.Canonicalise("""{ "a":"same", "nested": { "a":1, "b":2 }, "z":3 }""");

        Assert.Equal("""{"a":"same","nested":{"a":1,"b":2},"z":3}""", first);
        Assert.Equal(first, second);
        Assert.Equal(CanonicalJson.Hash(first), CanonicalJson.Hash(second));
    }

    [Fact]
    public void Canonical_json_writes_utc_round_trip_times_and_snake_case_members()
    {
        var json = CanonicalJson.Serialise(new { EffectiveAt = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Local) });

        Assert.Contains("\"effective_at\"", json, StringComparison.Ordinal);
        Assert.EndsWith("Z\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_hash_ignores_trigger_snapshot_but_not_certificate_facts()
    {
        var first = CanonicalJson.Canonicalise("""{"model":{"code":"YT-100"},"trigger":{"event_id":"11111111-1111-1111-1111-111111111111","effective_at":"2026-09-01T09:00:00Z"}}""");
        var retriggered = CanonicalJson.Canonicalise("""{"trigger":{"effective_at":"2026-09-01T10:00:00Z","event_id":"22222222-2222-2222-2222-222222222222"},"model":{"code":"YT-100"}}""");
        var changed = CanonicalJson.Canonicalise("""{"model":{"code":"YT-200"},"trigger":{"event_id":"22222222-2222-2222-2222-222222222222","effective_at":"2026-09-01T10:00:00Z"}}""");

        Assert.Equal(CanonicalJson.HashExcludingRootProperties(first, "trigger"),
            CanonicalJson.HashExcludingRootProperties(retriggered, "trigger"));
        Assert.NotEqual(CanonicalJson.HashExcludingRootProperties(first, "trigger"),
            CanonicalJson.HashExcludingRootProperties(changed, "trigger"));
    }

    [Fact]
    public void Release_record_payload_exposes_no_party_bindable_member()
    {
        var memberPaths = MemberPaths(typeof(ReleaseRecordPayload)).ToList();
        var forbidden = new[] { "party", "owner", "customer", "address" };

        Assert.DoesNotContain(memberPaths, path => forbidden.Any(word => path.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Warranty_payload_groups_live_terms_and_keeps_policy_and_superseded_lineage()
    {
        var owner = Guid.NewGuid();
        var current = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var builder = new WarrantyCertificatePayloadBuilder(
            new WarrantyFacts([new CertificateEntitlementFact(current, "base_warranty", "manufacturer", "active",
                "deliver", new DateOnly(2026, 9, 1), new DateOnly(2028, 9, 1), "STD-24", 3, 24, "months",
                "{\"service_mode\":\"depot\"}", previous, previous, [previous])]),
            new PartyNames(owner, new PartyLabel(owner, "OWNER-7", "Fictional Field Lab", "customer")));
        var unit = new CertificateUnitFact(Guid.NewGuid(), LifecycleStatus.Released, UnitOrigin.Manufactured,
            HistoryDepth.Full, Guid.NewGuid(), null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), null,
            new CertificateOwnershipFact(Guid.NewGuid(), owner, "customer", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)), [], []);

        var (payload, snapshot) = await builder.BuildAsync(unit,
            new CertificateTriggerSnapshot("Entitlement.CoverageChanged", Guid.NewGuid(), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CertificatePresentation(new CertificateIssuerSnapshot("Issuer", "I-1"), new CertificateBrandingSnapshot("default", null, null)),
            new CertificateProvenanceSnapshot(CertificateProvenance.SystemGenerated, [], []), CancellationToken.None);

        Assert.Equal("Fictional Field Lab", snapshot.Name);
        var term = Assert.Single(payload.Entitlements);
        Assert.Equal(("base_warranty", "deliver", "STD-24", 3), (term.CoverageFamily, term.StartBasis, term.PolicyCode, term.PolicyVersion));
        Assert.Equal([previous], term.SupersededLineage);
        Assert.Equal("24 months; depot", term.TermSummary);
    }

    [Theory]
    [InlineData(CertificateIssued.Schema, CertificateIssued.Version)]
    [InlineData(CertificateSuperseded.Schema, CertificateSuperseded.Version)]
    [InlineData(CertificateReprinted.Schema, CertificateReprinted.Version)]
    public void Published_certificate_event_has_an_embedded_1_0_schema(string schema, string version)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));

        Assert.Equal("1.0", version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }

    private static IEnumerable<string> MemberPaths(Type type, string prefix = "", HashSet<Type>? stack = null)
    {
        stack ??= [];
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(JsonElement))
            yield break;
        if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            type = type.GetGenericArguments()[0];
        if (!stack.Add(type)) yield break;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            yield return path;
            foreach (var nested in MemberPaths(property.PropertyType, path, stack)) yield return nested;
        }
        stack.Remove(type);
    }

    private sealed class WarrantyFacts(IReadOnlyList<CertificateEntitlementFact> rows) : ICertificateEntitlementReadPort
    {
        public Task<IReadOnlyList<CertificateEntitlementFact>> LiveAsync(Guid unitId, CancellationToken cancellationToken) => Task.FromResult(rows);
    }

    private sealed class PartyNames(Guid id, PartyLabel label) : IPartyNamePort
    {
        public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(partyRefs.Contains(id)
                ? new Dictionary<Guid, PartyLabel> { [id] = label }
                : new Dictionary<Guid, PartyLabel>());

        public Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PartyLabel>>([]);
    }
}
