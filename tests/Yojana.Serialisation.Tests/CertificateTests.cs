using System.Reflection;
using System.Collections;
using System.Text.RegularExpressions;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class CertificateTests
{
    private static readonly DateTime RenderedAt = new(2026, 7, 20, 14, 5, 6, DateTimeKind.Utc);

    [Fact]
    public void Certificate_starters_and_shared_composer_style_use_one_document_font_and_size_scale()
    {
        var starterType = typeof(CertificateHtmlComposer).Assembly.GetType(
            "Yojana.Serialisation.Certificates.CertificateStarterLayouts", throwOnError: true)!;
        var definitions = (IEnumerable)starterType.GetField("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var starterLayouts = definitions.Cast<object>().Select(definition =>
            (string)definition.GetType().GetProperty("Body")!.GetValue(definition)!).ToList();
        Assert.All(starterLayouts, layout => Assert.Contains("<dl class=\"document-meta\">", layout, StringComparison.Ordinal));

        var styles = starterLayouts.Append(CertificateDocumentStyle.Css);

        Assert.All(styles, layout =>
        {
            Assert.DoesNotContain("monospace", layout, StringComparison.OrdinalIgnoreCase);
            var families = Regex.Matches(layout, @"font-family\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.All(families, family => Assert.True(
                family.Equals("var(--certificate-font-family)", StringComparison.OrdinalIgnoreCase)
                || family.Equals(CertificateDocumentStyle.FontFamily, StringComparison.OrdinalIgnoreCase),
                $"Unexpected certificate font family: {family}"));
            var sizes = Regex.Matches(layout, @"font-size\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
            Assert.All(sizes, size => Assert.StartsWith("var(--certificate-size-", size, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Battery_passport_has_optional_battery_fields_and_no_authoring_note()
    {
        var starterType = typeof(CertificateHtmlComposer).Assembly.GetType(
            "Yojana.Serialisation.Certificates.CertificateStarterLayouts", throwOnError: true)!;
        var definitions = ((IEnumerable)starterType.GetField("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Cast<object>();
        var battery = definitions.Single(definition => (string)definition.GetType().GetProperty("Form")!.GetValue(definition)!
            == CertificateFormType.BatteryPassport);
        var body = (string)battery.GetType().GetProperty("Body")!.GetValue(battery)!;

        Assert.Contains("{{#if battery}}", body, StringComparison.Ordinal);
        Assert.Contains("battery.state_of_health_percent", body, StringComparison.Ordinal);
        Assert.Contains("{{#if battery}}<h2>Battery details</h2>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("No battery-specific data was recorded", body, StringComparison.Ordinal);
        Assert.DoesNotContain("A predecessor is shown only when", body, StringComparison.Ordinal);

        var catalogue = FieldCatalogue.For<ReleaseRecordPayload>().Select(row => row.Path).ToList();
        Assert.Contains("battery.carbon_footprint_kg_co2e", catalogue);
        var condition = new CertificateTemplateValidator(TemplateEngine()).Validate(
            CertificateDocumentKind.ReleaseRecord, catalogue, "{{#if battery}}{{battery.chemistry}}{{/if}}", null, null, []);
        Assert.True(condition.Valid, string.Join("; ", condition.Errors));
    }

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
        var forbidden = new[] { "party", "owner", "customer" };

        Assert.DoesNotContain(memberPaths, path => forbidden.Any(word => path.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(["Issuer.Address"], memberPaths.Where(path => path.Contains("address", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Warranty_payload_groups_live_terms_and_keeps_policy_and_superseded_lineage()
    {
        var owner = Guid.NewGuid();
        var current = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var builder = new WarrantyCertificatePayloadBuilder(
            new WarrantyFacts([new CertificateEntitlementFact(current, "base_warranty", "manufacturer", "active",
                "deliver", new DateOnly(2026, 9, 1), new DateOnly(2028, 9, 1), "STD-24", "Standard 24-month warranty", 3, 24, "months",
                "{\"service_mode\":\"depot\"}", previous, previous, [previous])]),
            new PartyNames(owner, new PartyLabel(owner, "OWNER-7", "Fictional Field Lab", "customer")));
        var unit = new CertificateUnitFact(Guid.NewGuid(), LifecycleStatus.Released, UnitOrigin.Manufactured,
            HistoryDepth.Full, Guid.NewGuid(), null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), null,
            new CertificateOwnershipFact(Guid.NewGuid(), owner, "customer", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)), [], []);

        var (payload, snapshot) = await builder.BuildAsync(unit,
            new CertificateTriggerSnapshot("Entitlement.CoverageChanged", Guid.NewGuid(), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CertificatePresentation(new CertificateIssuerSnapshot("Issuer", "I-1"),
                new CertificateBrandingSnapshot("default", null, null),
                new CertificatePresentationSnapshot("UTC", CertificateDatePattern.DayMonthNameYear,
                    CertificateTimePattern.TwentyFourHour)),
            new CertificateProvenanceSnapshot(CertificateProvenance.SystemGenerated, [], []), CancellationToken.None);

        Assert.Equal("Fictional Field Lab", snapshot.Name);
        var term = Assert.Single(payload.Entitlements);
        Assert.Equal(("base_warranty", "deliver", "STD-24", 3), (term.CoverageFamily, term.StartBasis, term.PolicyCode, term.PolicyVersion));
        Assert.Equal("Standard 24-month warranty", term.PolicyName);
        Assert.Equal([previous], term.SupersededLineage);
        Assert.Equal("24 months; depot", term.TermSummary);
    }

    [Theory]
    [InlineData(CertificateIssued.Schema, CertificateIssued.Version)]
    [InlineData(CertificateSuperseded.Schema, CertificateSuperseded.Version)]
    [InlineData(CertificateWithdrawn.Schema, CertificateWithdrawn.Version)]
    [InlineData(CertificateReprinted.Schema, CertificateReprinted.Version)]
    public void Published_certificate_event_has_an_embedded_1_0_schema(string schema, string version)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));

        Assert.Equal("1.0", version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Certificate_template_helpers_format_dates_labels_and_money_for_the_template_language()
    {
        using var payload = JsonDocument.Parse("""
            {"made_on":"2026-07-20T14:05:06.1234567Z","identifier_type":"internal_serial",
             "amount":1234.5,"currency":"EUR"}
            """);
        var engine = TemplateEngine();

        var html = engine.Render("{{date made_on}}|{{datetime made_on}}|{{label identifier_type}}|{{money amount currency}}",
            payload.RootElement, RenderRequest(), "en");

        Assert.Equal("20 Jul 2026|20 Jul 2026, 14:05 UTC|Serial number|EUR 1,234.50", html);
        Assert.DoesNotContain(".123", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_datetime_helpers_use_the_snapshotted_zone_with_its_abbreviation()
    {
        using var payload = JsonDocument.Parse("""
            {"presentation":{"time_zone":"Asia/Kolkata","date_pattern":"d MMM yyyy","time_pattern":"HH:mm"},"tested_at":"2026-07-20T14:05:06Z"}
            """);
        var request = RenderRequest() with
        {
            Signature = new CertificateRenderSignature("Fictional signer", "release",
                DateTime.SpecifyKind(RenderedAt, DateTimeKind.Unspecified), "authenticated_user"),
            Metadata = RenderRequest().Metadata! with
            {
                IssuedAt = DateTime.SpecifyKind(RenderedAt, DateTimeKind.Unspecified),
            },
        };

        var html = TemplateEngine().Render(
            "{{datetime tested_at}}|{{datetime certificate.issued_at}}|{{datetime signature.signed_at}}",
            payload.RootElement, request, "en");

        Assert.Equal("20 Jul 2026, 19:35 IST|20 Jul 2026, 19:35 IST|20 Jul 2026, 19:35 IST", html);
    }

    [Theory]
    [InlineData(CertificateDatePattern.DayMonthNameYear, "20 Jul 2026", "20 Jul 2026, 14:05 UTC")]
    [InlineData(CertificateDatePattern.DayMonthYear, "20/07/2026", "20/07/2026, 14:05 UTC")]
    [InlineData(CertificateDatePattern.MonthDayYear, "07/20/2026", "07/20/2026, 14:05 UTC")]
    [InlineData(CertificateDatePattern.YearMonthDay, "2026-07-20", "2026-07-20, 14:05 UTC")]
    public void Certificate_helpers_use_each_snapshotted_date_pattern(string pattern, string expectedDate,
        string expectedDateTime)
    {
        using var payload = JsonDocument.Parse($$"""
            {"presentation":{"time_zone":"UTC","date_pattern":"{{pattern}}","time_pattern":"HH:mm"},"tested_at":"2026-07-20T14:05:06Z"}
            """);

        var html = TemplateEngine().Render("{{date tested_at}}|{{datetime tested_at}}", payload.RootElement,
            RenderRequest(), "en");

        Assert.Equal($"{expectedDate}|{expectedDateTime}", html);
    }

    [Fact]
    public void Certificate_datetime_helper_supports_the_snapshotted_twelve_hour_pattern()
    {
        using var payload = JsonDocument.Parse("""
            {"presentation":{"time_zone":"UTC","date_pattern":"MM/dd/yyyy","time_pattern":"h:mm tt"},"tested_at":"2026-07-20T14:05:06Z"}
            """);

        var html = TemplateEngine().Render("{{datetime tested_at}}", payload.RootElement, RenderRequest(), "en-US");

        Assert.Equal("07/20/2026, 2:05 PM UTC", html);
    }

    [Theory]
    [InlineData("en", CertificateDatePattern.DayMonthNameYear, CertificateTimePattern.TwentyFourHour)]
    [InlineData("en-US", CertificateDatePattern.MonthDayYear, CertificateTimePattern.TwelveHour)]
    [InlineData("fr", CertificateDatePattern.DayMonthYear, CertificateTimePattern.TwentyFourHour)]
    [InlineData("tenant-custom", CertificateDatePattern.MonthDayYear, CertificateTimePattern.TwentyFourHour)]
    public void Certificate_date_format_defaults_follow_the_language_culture(string language,
        string expectedDatePattern, string expectedTimePattern)
    {
        var choice = CertificateDateFormatSettings.DefaultFor(language);

        Assert.Equal((expectedDatePattern, expectedTimePattern), (choice.DatePattern, choice.TimePattern));
    }

    [Fact]
    public void Certificate_template_conditions_and_empty_repeats_remove_empty_fragments()
    {
        using var payload = JsonDocument.Parse("""{"revision":"","tests":[],"condition":"new"}""");
        var engine = TemplateEngine();
        const string template = "{{#if revision}}Revision {{revision}}{{else}}No revision{{/if}}|" +
            "{{#unless revision}}No revision row{{/unless}}|" +
            "{{#each tests}}{{label result}}{{else}}No tests recorded{{/each}}|{{label condition}}";

        var html = engine.Render(template, payload.RootElement, RenderRequest(), "en");

        Assert.Equal("No revision|No revision row|No tests recorded|New", html);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_template_validator_understands_relative_block_fields_and_names_unknown_helpers()
    {
        var validator = new CertificateTemplateValidator(TemplateEngine());
        var validation = validator.Validate(CertificateDocumentKind.ReleaseRecord,
            ["identifiers", "identifiers.type", "model.code"],
            "{{#each identifiers}}{{label type}}{{else}}None{{/each}} {{mystery model.code}}", null, null,
            ["identifiers", "identifiers.type", "model.code"]);

        Assert.False(validation.Valid);
        Assert.Empty(validation.UnknownPlaceholders);
        Assert.Equal(["mystery"], validation.UnknownHelpers);
        Assert.Contains("Unknown template helper 'mystery'.", validation.Errors);
    }

    [Fact]
    public void Certificate_template_validator_rejects_unclosed_blocks_and_release_party_fields()
    {
        var validator = new CertificateTemplateValidator(TemplateEngine());
        var validation = validator.Validate(CertificateDocumentKind.ReleaseRecord,
            ["owner.name", "model.code"], "{{#if model.code}}{{model.code}} {{owner.name}}", null, null, []);

        Assert.False(validation.Valid);
        Assert.Equal(["owner.name"], validation.PartyFieldViolations);
        Assert.Contains(validation.Errors, error => error.Contains("not closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Certificate_templates_never_render_opaque_database_references()
    {
        using var payload = JsonDocument.Parse("""
            {"trigger":{"event_id":"11111111-1111-1111-1111-111111111111"},
             "source_lots":["22222222222222222222222222222222"],
             "as_maintained":[{"lot_reference":"33333333333333333333333333333333"}]}
            """);

        var html = TemplateEngine().Render("{{trigger.event_id}}|{{#each source_lots}}{{this}}{{/each}}|{{#each as_maintained}}{{lot_reference}}{{/each}}",
            payload.RootElement, RenderRequest(), "en");

        Assert.Equal("||", html);
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

    private static CertificateTemplateEngine TemplateEngine() =>
        new(new BuiltInCertificateTemplateLanguageCatalogue());

    private static CertificateRenderRequest RenderRequest() => new("{}", Guid.Empty,
        Metadata: new CertificateRenderMetadata("CRT-1", 1, RenderedAt, null, null, "hash", "urn:test"));

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
