using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Portal;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P7-T07–T09: disclosure, ownership-period security and the customer portal read API.</summary>
[Collection("serialisation host")]
public sealed class PortalTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Case_136_public_disclosure_is_closed_and_the_tenant_can_disable_the_QR_resolver()
    {
        var tenant = await fixture.NewTenantAsync("p7pub");
        var item = Guid.NewGuid();
        var unit = await UnitAsync(tenant, item, Serial("PUB"));
        RegisterItem(item, "P7-PUBLIC", "Fictional Field Terminal");
        using var scope = fixture.ScopeFor(tenant);
        var resolver = scope.ServiceProvider.GetRequiredService<PublicPortalResolver>();
        var serial = await Db(scope).Identifiers.Where(row => row.UnitId == unit && row.ValidTo == null)
            .Select(row => row.RawValue).SingleAsync();
        var visible = await resolver.ResolveAsync("tenant-public", serial, null, CancellationToken.None);
        Assert.Equal(PublicPortalOutcome.Found, visible.Outcome);
        Assert.Equal("P7-PUBLIC", visible.ItemCode);
        Assert.Equal("2026-09", visible.ManufactureMonth);

        var forbiddenNames = new[] { "party", "owner", "date", "document", "service", "cost", "note", "unitid" };
        Assert.DoesNotContain(typeof(PublicPortalResolution).GetProperties(), property =>
            forbiddenNames.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(typeof(PublicPortalResolution).GetProperties(), property =>
            property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?)
            || property.PropertyType == typeof(DateOnly) || property.PropertyType == typeof(DateOnly?));
        Assert.Equal([nameof(PublicPortalResolution.ManufactureMonth)],
            typeof(PublicPortalResolution).GetProperties()
                .Where(property => property.Name.Contains("date", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("month", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Name));
        Assert.DoesNotContain(PortalDisclosureField.PublicSafe, field => field.Contains("party", StringComparison.Ordinal)
            || field.Contains("document", StringComparison.Ordinal)
            || field.Contains("service", StringComparison.Ordinal));
        Assert.Equal([PortalDisclosureField.ManufactureMonth], PortalDisclosureField.PublicSafe
            .Where(field => field.Contains("date", StringComparison.Ordinal)
                || field.Contains("month", StringComparison.Ordinal)));

        var policy = scope.ServiceProvider.GetRequiredService<DisclosurePolicyService>();
        var current = await policy.GetAsync(CancellationToken.None);
        Assert.Contains(current.Fields, row => row.Field == PortalDisclosureField.ManufactureMonth
            && row.Tier == DisclosureTier.Public && row.Enabled);
        var unsafeFields = current.Fields.Select(row => row.Field == PortalDisclosureField.DeliveryDate
            ? row with { Tier = DisclosureTier.Public } : row).ToList();
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => policy.SetFieldsAsync(
            new UpdateDisclosureFields(unsafeFields), "test:tenant-admin", CancellationToken.None));
        Assert.Equal("public_field_forbidden", refused.Rule);
        var monthOff = current.Fields.Select(row => row.Field == PortalDisclosureField.ManufactureMonth
            ? row with { Enabled = false } : row).ToList();
        await policy.SetFieldsAsync(new UpdateDisclosureFields(monthOff), "test:tenant-admin",
            CancellationToken.None);
        var noMonth = await resolver.ResolveAsync("tenant-public", serial, null, CancellationToken.None);
        Assert.Null(noMonth.ManufactureMonth);
        Assert.Equal("P7-PUBLIC", noMonth.ItemCode);
        var noPublicFields = current.Fields.Select(row => row.Tier == DisclosureTier.Public
            ? row with { Enabled = false } : row).ToList();
        await policy.SetFieldsAsync(new UpdateDisclosureFields(noPublicFields), "test:tenant-admin",
            CancellationToken.None);
        var blank = await resolver.ResolveAsync("tenant-public", serial, null, CancellationToken.None);
        Assert.Equal(PublicPortalOutcome.Found, blank.Outcome);
        Assert.Null(blank.Identifier);
        var disabled = await policy.SetPublicEnabledAsync(new UpdatePublicPortalSetting(false,
            current.SettingsVersion), CancellationToken.None);
        Assert.False(disabled.PublicTierEnabled);
        var hidden = await resolver.ResolveAsync("tenant-public", serial, null, CancellationToken.None);
        Assert.Equal(PublicPortalOutcome.AuthenticationRequired, hidden.Outcome);
        Assert.Null(hidden.Identifier);
    }

    [DatabaseFact]
    public async Task Case_47_portal_API_partitions_service_history_between_prior_and_later_owners()
    {
        var tenant = await fixture.NewTenantAsync("p7rls");
        var unit = await UnitAsync(tenant, Guid.NewGuid(), Serial("RLS"));
        var prior = Guid.NewGuid();
        var later = Guid.NewGuid();
        await OwnAsync(tenant, unit, prior, At.AddDays(1));
        await OwnAsync(tenant, unit, later, At.AddDays(10));
        await AddServiceEventsAsync(tenant, unit,
            (At.AddDays(5), RmaEventType.Requested), (At.AddDays(12), RmaEventType.Closed));

        using var priorScope = fixture.ScopeForPortal(tenant, prior);
        var priorPage = await priorScope.ServiceProvider.GetRequiredService<PortalQueries>()
            .ServiceHistoryAsync(new(tenant, prior), unit, 20, null, null, CancellationToken.None);
        using var laterScope = fixture.ScopeForPortal(tenant, later);
        var laterPage = await laterScope.ServiceProvider.GetRequiredService<PortalQueries>()
            .ServiceHistoryAsync(new(tenant, later), unit, 20, null, null, CancellationToken.None);
        Assert.Equal([RmaEventType.Requested], priorPage!.Rows.Select(row => row.EventType));
        Assert.Equal([RmaEventType.Closed], laterPage!.Rows.Select(row => row.EventType));
    }

    [DatabaseFact]
    public async Task Case_47_SQL_RLS_partitions_raw_unit_party_and_history_reads_by_ownership_window()
    {
        var tenant = await fixture.NewTenantAsync("p7sqlrls");
        var item = Guid.NewGuid();
        var root = await UnitAsync(tenant, item, Serial("SQLRLS"));
        var child = await UnitAsync(tenant, Guid.NewGuid(), Serial("SQLCHILD"));
        var prior = Guid.NewGuid();
        var later = Guid.NewGuid();
        var priorEpisode = await OwnAsync(tenant, root, prior, At.AddDays(1));
        var laterEpisode = await OwnAsync(tenant, root, later, At.AddDays(10));
        await AddAssemblyAsync(tenant, root, child, At.AddDays(2));
        await AddServiceEventsAsync(tenant, root,
            (At.AddDays(5), "service.prior"), (At.AddDays(12), "service.later"));
        await AddServiceEventsAsync(tenant, child,
            (At.AddDays(6), "component.prior"), (At.AddDays(13), "component.later"));
        await AddEntitlementAsync(tenant, root, prior, "prior-cover", new DateOnly(2026, 9, 9));
        await AddEntitlementAsync(tenant, root, later, "later-cover", new DateOnly(2026, 10, 9));

        Guid priorCertificate;
        Guid laterCertificate;
        using (var arrange = fixture.ScopeFor(tenant))
        {
            var db = Db(arrange);
            var initial = await db.Identifiers.Where(row => row.UnitId == root
                    && row.Type == IdentifierType.InternalSerial).SingleAsync();
            db.Identifiers.AddRange(
                Identifier(tenant, root, item, IdentifierType.VendorSerial, "PRIOR-ONLY", At.AddDays(5),
                    At.AddDays(10)),
                Identifier(tenant, root, item, IdentifierType.CustomerSerial, "LATER-ONLY", At.AddDays(12), null));
            db.ArchivedEvents.AddRange(
                ArchivedEvent(tenant, root, At.AddDays(7), "archive.prior", 9_100_001),
                ArchivedEvent(tenant, root, At.AddDays(14), "archive.later", 9_100_002));
            await arrange.ServiceProvider.GetRequiredService<CertificateTemplateService>()
                .EnsureStartersAsync(CancellationToken.None);
            var templateVersion = await db.CertificateTemplateVersions.Select(row => row.TemplateVersionId)
                .FirstAsync();
            priorCertificate = Guid.NewGuid();
            laterCertificate = Guid.NewGuid();
            db.CertificateInstances.AddRange(
                Certificate(tenant, root, templateVersion, priorCertificate, 1, At.AddDays(8), "PRIOR-CERT"),
                Certificate(tenant, root, templateVersion, laterCertificate, 2, At.AddDays(14), "LATER-CERT"));
            await db.SaveChangesAsync();
            Assert.NotEqual(Guid.Empty, initial.IdentifierId);
        }

        await AssertRawScopeAsync(prior, priorEpisode.EpisodeId,
            ["service.prior", "component.prior"], ["service.later", "component.later"],
            ["archive.prior"], "prior-cover", priorCertificate,
            IdentifierType.VendorSerial, IdentifierType.CustomerSerial, expectedCurrentRows: 0);
        await AssertRawScopeAsync(later, laterEpisode.EpisodeId,
            ["service.later", "component.later"], ["service.prior", "component.prior"],
            ["archive.later"], "later-cover", laterCertificate,
            IdentifierType.CustomerSerial, IdentifierType.VendorSerial, expectedCurrentRows: 2);

        async Task AssertRawScopeAsync(Guid partyRef, Guid expectedEpisodeId,
            IReadOnlyList<string> expectedLiveEvents, IReadOnlyList<string> forbiddenLiveEvents,
            IReadOnlyList<string> expectedArchivedEvents,
            string expectedCoverage, Guid expectedCertificate, string visiblePeriodIdentifier,
            string forbiddenPeriodIdentifier, int expectedCurrentRows)
        {
            using var scope = fixture.ScopeForPortal(tenant, partyRef);
            var db = Db(scope);
            var sessionParty = await db.Database.SqlQueryRaw<Guid>(
                "SELECT CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier) AS [Value]")
                .SingleAsync();
            Assert.Equal(partyRef, sessionParty);
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context @key=N'portal_party_ref', @value=NULL;"));

            Assert.Equal([expectedEpisodeId], await RawIdsAsync(db,
                "SELECT episode_id AS [Value] FROM serialisation.custody_episode WHERE tenant_id = @tenant AND unit_id = @unit",
                tenant, root));
            var liveEvents = await RawStringsAsync(db,
                "SELECT event_type AS [Value] FROM serialisation.unit_event WHERE tenant_id = @tenant AND (unit_id = @unit OR unit_id = @other)",
                tenant, root, child);
            Assert.All(expectedLiveEvents, expected => Assert.Contains(expected, liveEvents));
            Assert.All(forbiddenLiveEvents, forbidden => Assert.DoesNotContain(forbidden, liveEvents));
            Assert.Equal(expectedArchivedEvents, await RawStringsAsync(db,
                "SELECT event_type AS [Value] FROM serialisation.unit_event_archive WHERE tenant_id = @tenant AND unit_id = @unit",
                tenant, root));
            Assert.Equal([expectedCoverage], await RawStringsAsync(db,
                "SELECT coverage_family AS [Value] FROM serialisation.entitlement WHERE tenant_id = @tenant AND unit_id = @unit",
                tenant, root));
            Assert.Equal([expectedCertificate], await RawIdsAsync(db,
                "SELECT certificate_id AS [Value] FROM serialisation.certificate_instance WHERE tenant_id = @tenant AND unit_id = @unit",
                tenant, root));
            var identifierTypes = await RawStringsAsync(db,
                "SELECT type AS [Value] FROM serialisation.unit_identifier WHERE tenant_id = @tenant AND unit_id = @unit",
                tenant, root);
            Assert.Contains(IdentifierType.InternalSerial, identifierTypes);
            Assert.Contains(visiblePeriodIdentifier, identifierTypes);
            Assert.DoesNotContain(forbiddenPeriodIdentifier, identifierTypes);
            Assert.Equal(2, (await RawIdsAsync(db,
                "SELECT unit_id AS [Value] FROM serialisation.unit WHERE tenant_id = @tenant AND (unit_id = @unit OR unit_id = @other)",
                tenant, root, child)).Count);
            Assert.Single(await RawIdsAsync(db,
                "SELECT link_id AS [Value] FROM serialisation.assembly_link WHERE tenant_id = @tenant AND parent_unit_id = @unit",
                tenant, root));
            Assert.Equal(expectedCurrentRows, (await RawIdsAsync(db,
                "SELECT unit_id AS [Value] FROM serialisation.unit_current WHERE tenant_id = @tenant AND (unit_id = @unit OR unit_id = @other)",
                tenant, root, child)).Count);
        }
    }

    [DatabaseFact]
    public async Task Ticket_T4_installed_base_defaults_to_roots_pages_by_key_and_streams_CSV_rows()
    {
        var tenant = await fixture.NewTenantAsync("p7t4");
        var owner = Guid.NewGuid();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var parent = await UnitAsync(tenant, parentItem, Serial("T4P"));
        var component = await UnitAsync(tenant, componentItem, Serial("T4C"));
        await OwnAsync(tenant, parent, owner, At.AddDays(1), "PO-4401", "CUSTOM-SYSTEM-4");
        RegisterItem(parentItem, "SYS-440", "Fictional Array System");
        RegisterItem(componentItem, "DRV-44", "Fictional Storage Module");
        await AddAssemblyAsync(tenant, parent, component, At.AddDays(2));
        await AddEntitlementAsync(tenant, parent, owner, "base", new DateOnly(2026, 9, 30));

        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        var rootPage = await api.InstalledBaseAsync(new(tenant, owner), false, 1, null, null,
            CancellationToken.None);
        var root = Assert.Single(rootPage.Rows);
        Assert.False(root.IsComponent);
        Assert.Equal("PO-4401", root.CustomerPoRef);
        Assert.Equal(new DateOnly(2026, 9, 30), Assert.Single(root.Coverage).Expiry);
        var expanded = await api.InstalledBaseAsync(new(tenant, owner), true, 20, null, null,
            CancellationToken.None);
        Assert.Equal(2, expanded.Rows.Count);
        Assert.Contains(expanded.Rows, row => row.UnitId == component && row.IsComponent);
        var exported = new List<PortalInstalledBaseRow>();
        await foreach (var row in api.ExportInstalledBaseAsync(new(tenant, owner), false, CancellationToken.None))
            exported.Add(row);
        Assert.Equal([parent], exported.Select(row => row.UnitId));
    }

    [DatabaseFact]
    public async Task Portal_unit_page_read_returns_the_owner_summary_and_hides_an_unowned_unit()
    {
        var tenant = await fixture.NewTenantAsync("p7unitpage");
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var item = Guid.NewGuid();
        var serial = Serial("UNITPAGE");
        var unit = await UnitAsync(tenant, item, serial);
        RegisterItem(item, "UNIT-PAGE", "Fictional Service Console");
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(owner, "FIELD-01",
            "Fictional Field Cooperative", PartyType.Customer));
        await OwnAsync(tenant, unit, owner, At.AddDays(1));

        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        var visible = await api.UnitAsync(new(tenant, owner), unit, CancellationToken.None);
        Assert.NotNull(visible);
        Assert.Equal(serial.Replace("-", "", StringComparison.Ordinal), visible.Serial);
        Assert.Equal("Fictional Service Console", visible.ItemName);
        Assert.Equal("Fictional Field Cooperative",
            (await api.ContextAsync(new(tenant, owner), CancellationToken.None)).OrganisationName);
        Assert.Null(await api.UnitAsync(new(tenant, stranger), unit, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Ticket_T14_customer_assertion_does_not_grant_lookup_and_expiry_is_never_guessed_from_delivery()
    {
        var tenant = await fixture.NewTenantAsync("p7t14");
        var owner = Guid.NewGuid();
        var claimant = Guid.NewGuid();
        var item = Guid.NewGuid();
        var serial = Serial("T14");
        var unit = await UnitAsync(tenant, item, serial);
        await OwnAsync(tenant, unit, owner, At.AddDays(1));
        await AddEntitlementAsync(tenant, unit, owner, "base", new DateOnly(2026, 9, 27));
        var reconciliation = await InvokeAsync<CustomerListReconciliationPage>(tenant,
            new ReconcileCustomerList(Guid.NewGuid(), claimant,
                new CustomerListSource(Pasted: [new CustomerListInput(serial)]), At.AddDays(2), "test:claimant"));
        var assertion = Assert.Single(reconciliation.Items);
        Assert.Equal(CustomerListMatchOutcome.OwnedByAnotherParty, assertion.Outcome);
        Assert.Equal(CustomerAssertionStatus.PendingVerification, assertion.AssertionStatus);
        Assert.NotNull(assertion.AssertionId);
        using var readScope = fixture.ScopeFor(tenant);
        var api = readScope.ServiceProvider.GetRequiredService<PortalQueries>();
        Assert.Equal(PortalQueryOutcome.NotFound,
            (await api.LookupAsync(new(tenant, claimant), serial, null, CancellationToken.None)).Outcome);
        var found = await api.LookupAsync(new(tenant, owner), serial, null, CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 9, 27), Assert.Single(Assert.Single(found.Units).Coverage).Expiry);
        Assert.NotEqual(DateOnly.FromDateTime(At.AddDays(1)), Assert.Single(found.Units[0].Coverage).Expiry);
    }

    [DatabaseFact]
    public async Task Ticket_T17_lookup_accepts_software_licence_and_MAC_and_installed_base_returns_customer_refs()
    {
        var tenant = await fixture.NewTenantAsync("p7t17");
        var owner = Guid.NewGuid();
        var unit = await UnitAsync(tenant, Guid.NewGuid(), Serial("T17"));
        await OwnAsync(tenant, unit, owner, At.AddDays(1), "PO-LIC-17", "F-17");
        await AssignAsync(tenant, unit, IdentifierType.SoftwareLicense, "LIC-FICTIONAL-001");
        await AssignAsync(tenant, unit, IdentifierType.Mac, "00:11:22:33:44:55");
        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        Assert.Equal(unit, Assert.Single((await api.LookupAsync(new(tenant, owner), "LIC-FICTIONAL-001",
            IdentifierType.SoftwareLicense, CancellationToken.None)).Units).UnitId);
        Assert.Equal(unit, Assert.Single((await api.LookupAsync(new(tenant, owner), "00:11:22:33:44:55",
            IdentifierType.Mac, CancellationToken.None)).Units).UnitId);
        var installed = Assert.Single((await api.InstalledBaseAsync(new(tenant, owner), false, 20, null, null,
            CancellationToken.None)).Rows);
        Assert.Equal("PO-LIC-17", installed.CustomerPoRef);
        Assert.Equal("F-17", installed.CustomerItemRef);
    }

    [DatabaseFact]
    public async Task Ticket_T19_lookup_follows_a_superseded_serial_and_service_history_has_no_internal_fields()
    {
        var tenant = await fixture.NewTenantAsync("p7t19");
        var owner = Guid.NewGuid();
        var oldSerial = Serial("T19");
        var unit = await UnitAsync(tenant, Guid.NewGuid(), oldSerial);
        await OwnAsync(tenant, unit, owner, At.AddDays(1));
        await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit,
            IdentifierType.InternalSerial, Serial("T19N"), IdentifierCorrectionReason.Typo, At.AddDays(2),
            "test:serial-admin", Entity));
        await AddServiceEventsAsync(tenant, unit, (At.AddDays(3), RmaEventType.Requested));
        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        var lookup = await api.LookupAsync(new(tenant, owner), oldSerial, null, CancellationToken.None);
        Assert.Equal(PortalQueryOutcome.Found, lookup.Outcome);
        Assert.Contains(Assert.Single(lookup.Units).Identifiers, row => !row.Current && row.ReplacedBy != null);
        var history = await api.ServiceHistoryAsync(new(tenant, owner), unit, 20, null, null,
            CancellationToken.None);
        Assert.Single(history!.Rows);
        Assert.DoesNotContain(typeof(PortalServiceHistoryRow).GetProperties(), property =>
            property.Name.Contains("cost", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("note", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("actor", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("document", StringComparison.OrdinalIgnoreCase));
    }

    [DatabaseFact]
    public async Task Ticket_T39_component_where_used_returns_current_and_prior_parents_with_parent_and_component_cover()
    {
        var tenant = await fixture.NewTenantAsync("p7t39");
        var owner = Guid.NewGuid();
        var first = await UnitAsync(tenant, Guid.NewGuid(), Serial("T39A"));
        var second = await UnitAsync(tenant, Guid.NewGuid(), Serial("T39B"));
        var componentSerial = Serial("T39C");
        var component = await UnitAsync(tenant, Guid.NewGuid(), componentSerial);
        await OwnAsync(tenant, first, owner, At.AddDays(1));
        await OwnAsync(tenant, second, owner, At.AddDays(1));
        await AddAssemblyAsync(tenant, first, component, At.AddDays(2), At.AddDays(5));
        await AddAssemblyAsync(tenant, second, component, At.AddDays(6));
        await AddEntitlementAsync(tenant, first, owner, "base", new DateOnly(2026, 9, 20));
        await AddEntitlementAsync(tenant, second, owner, "base", new DateOnly(2026, 9, 21));
        await AddEntitlementAsync(tenant, second, owner, "component", new DateOnly(2026, 9, 22), component);
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<PortalQueries>()
            .WhereUsedAsync(new(tenant, owner), new(componentSerial, IdentifierType.InternalSerial),
                CancellationToken.None);
        Assert.Equal(PortalQueryOutcome.Found, result.Outcome);
        Assert.Equal(2, result.Parents.Count);
        Assert.Single(result.Parents, row => row.Current);
        Assert.All(result.Parents, row => Assert.Single(row.LiveEntitlements));
        Assert.Equal("component", Assert.Single(result.ComponentEntitlements).CoverageFamily);
    }

    [DatabaseFact]
    public async Task Ticket_T47_validator_first_reports_invalid_and_valid_unmatched_component_serials_separately()
    {
        var tenant = await fixture.NewTenantAsync("p7t47");
        var owner = Guid.NewGuid();
        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        var list = await api.PastedSerialsAsync(new(tenant, owner), new PortalSerialListRequest(
            [new("123", IdentifierType.Vin), new("1M8GDM9AXKP042788", IdentifierType.Vin)]),
            CancellationToken.None);
        Assert.Equal("123", Assert.Single(list.Invalid).Input);
        Assert.Equal("1M8GDM9AXKP042788", Assert.Single(list.Unmatched));
        var untyped = await api.PastedSerialsAsync(new(tenant, owner), new PortalSerialListRequest([new("")]),
            CancellationToken.None);
        Assert.Equal("empty", Assert.Single(Assert.Single(untyped.Invalid).Reasons));
        var whereUsed = await api.WhereUsedAsync(new(tenant, owner), new("123", IdentifierType.Vin),
            CancellationToken.None);
        Assert.Equal(PortalQueryOutcome.Invalid, whereUsed.Outcome);
        Assert.NotEmpty(whereUsed.ValidationReasons);
    }

    [DatabaseFact]
    public async Task Invariant_3_certificates_return_no_release_owner_and_only_the_viewers_warranty_snapshot()
    {
        var tenant = await fixture.NewTenantAsync("p7cert");
        var firstOwner = Guid.NewGuid();
        var laterOwner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(firstOwner, "FIRST-OWNER",
            "Fictional First Owner", PartyType.Customer));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(laterOwner, "LATER-OWNER",
            "Fictional Later Owner", PartyType.Customer));
        var unit = await UnitAsync(tenant, Guid.NewGuid(), Serial("CERT"));
        await OwnAsync(tenant, unit, firstOwner, At.AddDays(1));
        using (var templateScope = fixture.ScopeFor(tenant))
            await templateScope.ServiceProvider.GetRequiredService<CertificateTemplateService>()
                .EnsureStartersAsync(CancellationToken.None);
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), unit, At.AddDays(2),
            "test:quality", Entity, "test-signature"));
        await AddEntitlementAsync(tenant, unit, firstOwner, "base", new DateOnly(2026, 9, 30));
        var release = await InvokeAsync<IssueCertificateVersionResult>(tenant, new IssueCertificateVersion(Guid.NewGuid(), unit,
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, At.AddDays(3),
            "test:certificates", Entity, TriggerSchema: ReleaseRecordIssuePolicy.SelectionTrigger));
        var warranty = await InvokeAsync<IssueCertificateVersionResult>(tenant, new IssueCertificateVersion(Guid.NewGuid(), unit,
            CertificateDocumentKind.WarrantyCertificate, CertificateFormType.WarrantyCertificate, null,
            At.AddDays(4), "test:certificates", Entity));
        Assert.True(release.Issued || release.RefusalRule == "certificate_facts_unchanged",
            $"{release.RefusalRule}: {release.RefusalDetail}");
        Assert.True(warranty.Issued, $"{warranty.RefusalRule}: {warranty.RefusalDetail}");
        await OwnAsync(tenant, unit, laterOwner, At.AddDays(10));
        await AddEntitlementAsync(tenant, unit, laterOwner, "extended", new DateOnly(2026, 10, 31));
        var laterWarranty = await InvokeAsync<IssueCertificateVersionResult>(tenant,
            new IssueCertificateVersion(Guid.NewGuid(), unit, CertificateDocumentKind.WarrantyCertificate,
                CertificateFormType.WarrantyCertificate, "ownership transfer", At.AddDays(11),
                "test:certificates", Entity));
        Assert.True(laterWarranty.Issued, $"{laterWarranty.RefusalRule}: {laterWarranty.RefusalDetail}");

        using var scope = fixture.ScopeFor(tenant);
        var api = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        var first = await api.CertificatesAsync(new(tenant, firstOwner), unit, 20, null, null,
            CancellationToken.None);
        Assert.Contains(first!.Rows, row => row.Kind == CertificateDocumentKind.ReleaseRecord && row.Owner is null);
        Assert.Contains(first.Rows, row => row.Kind == CertificateDocumentKind.WarrantyCertificate
            && row.Owner?.Code == "FIRST-OWNER");
        var later = await api.CertificatesAsync(new(tenant, laterOwner), unit, 20, null, null,
            CancellationToken.None);
        Assert.DoesNotContain(later!.Rows, row => row.Kind == CertificateDocumentKind.WarrantyCertificate
            && row.Owner?.Code == "FIRST-OWNER");
        Assert.Contains(later.Rows, row => row.Kind == CertificateDocumentKind.WarrantyCertificate
            && row.Owner?.Code == "LATER-OWNER");
        Assert.All(later.Rows.Where(row => row.Kind == CertificateDocumentKind.ReleaseRecord),
            row => Assert.Null(row.Owner));
    }

    private async Task<Guid> UnitAsync(TenantId tenant, Guid itemId, string serial)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "test:production", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return result.Lines.Single().UnitId!.Value;
    }

    private Task<OpenOwnershipResult> OwnAsync(TenantId tenant, Guid unitId, Guid partyRef, DateTime at,
        string? customerPoRef = null, string? customerItemRef = null) => InvokeAsync<OpenOwnershipResult>(tenant,
        new OpenOwnership(Guid.NewGuid(), unitId, partyRef, PartyType.Customer, EpisodeReason.Sale, at,
            "test:sales", Entity, CustomerPoRef: customerPoRef, CustomerItemRef: customerItemRef));

    private Task<AssignIdentifierResult> AssignAsync(TenantId tenant, Guid unitId, string type, string value) =>
        InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unitId,
            new IdentifierInput(type, value), At.AddDays(2), "test:customer", Entity));

    private async Task AddAssemblyAsync(TenantId tenant, Guid parent, Guid child, DateTime installed,
        DateTime? removed = null)
    {
        using var scope = fixture.ScopeFor(tenant);
        Db(scope).AssemblyLinks.Add(new AssemblyLink
        {
            TenantId = tenant, LinkId = Guid.NewGuid(), ParentUnitId = parent, ChildUnitId = child,
            Relation = AssemblyRelation.Component, InstalledAt = installed, RemovedAt = removed,
            InstallingEventId = Guid.NewGuid(), RemovingEventId = removed is null ? null : Guid.NewGuid(),
        });
        await Db(scope).SaveChangesAsync();
    }

    private async Task AddEntitlementAsync(TenantId tenant, Guid unitId, Guid owner, string family,
        DateOnly end, Guid? componentUnitId = null)
    {
        using var scope = fixture.ScopeFor(tenant);
        Db(scope).Entitlements.Add(new Entitlement
        {
            TenantId = tenant, EntitlementId = Guid.NewGuid(), UnitId = unitId,
            Scope = componentUnitId is null ? EntitlementScope.Unit : EntitlementScope.Component,
            ScopeChildUnitId = componentUnitId, Type = EntitlementType.Manufacturer, CoverageFamily = family,
            Direction = EntitlementDirection.Outbound, OwnerPartyRef = owner, OwnerPartyType = PartyType.Customer,
            ObligorEntityId = Entity, PolicyVersionId = Guid.NewGuid(), StartTrigger = StartTrigger.Deliver,
            StartBasis = StartTrigger.Deliver, StartDate = new DateOnly(2026, 9, 2), StartTimeZone = "Etc/UTC",
            EndDate = end, DurationValue = 1, DurationUnit = DurationRule.Months,
            EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time,
            Transferable = Transferability.Yes, AssuranceOrService = AssuranceOrService.Assurance,
            Status = EntitlementStatus.Active, Confidence = Confidence.High,
            Provenance = EntitlementProvenance.Created, CreatedEventId = Guid.NewGuid(), CreatedAt = At.AddDays(1),
        });
        await Db(scope).SaveChangesAsync();
    }

    private async Task AddServiceEventsAsync(TenantId tenant, Guid unitId,
        params (DateTime At, string Type)[] events)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = Db(scope);
        var sequence = 8_000_000L + Random.Shared.Next(1, 100_000);
        foreach (var item in events)
        {
            db.Events.Add(new UnitEvent
            {
                TenantId = tenant, EventId = Guid.NewGuid(), EventTime = item.At, UnitId = unitId,
                Seq = sequence++, EventType = item.Type, LegalEntityId = Entity, ActorRef = "test:service",
                RecordedTime = item.At, IdempotencyKey = Guid.NewGuid().ToString("N"),
                Attributes = "{\"internal_note\":\"private\",\"cost\":999}",
            });
        }
        await db.SaveChangesAsync();
    }

    private static UnitIdentifier Identifier(TenantId tenant, Guid unitId, Guid itemId, string type,
        string value, DateTime from, DateTime? to) => new()
    {
        TenantId = tenant, IdentifierId = Guid.NewGuid(), UnitId = unitId, Type = type,
        Scheme = IdentifierScheme.Internal, RawValue = value, NormalisedValue = value,
        ScopeKey = itemId.ToString("D"), ValidFrom = from, ValidTo = to, ActorRef = "test:identity",
        RecordedTime = from, EventId = Guid.NewGuid(),
    };

    private static UnitEventArchive ArchivedEvent(TenantId tenant, Guid unitId, DateTime at, string type,
        long sequence) => new()
    {
        TenantId = tenant, EventId = Guid.NewGuid(), EventTime = at, UnitId = unitId, Seq = sequence,
        EventType = type, LegalEntityId = Entity, ActorRef = "test:archive", RecordedTime = at,
        IdempotencyKey = Guid.NewGuid().ToString("N"), Attributes = "{}",
    };

    private static CertificateInstance Certificate(TenantId tenant, Guid unitId, Guid templateVersion,
        Guid certificateId, int version, DateTime issuedAt, string trackingNumber) => new()
    {
        TenantId = tenant, CertificateId = certificateId, UnitId = unitId,
        DocumentKind = CertificateDocumentKind.WarrantyCertificate,
        FormType = CertificateFormType.WarrantyCertificate, Version = version, TrackingNumber = trackingNumber,
        TemplateVersionId = templateVersion, Payload = "{}", PayloadHash = Guid.NewGuid().ToString("N"),
        FactsHash = Guid.NewGuid().ToString("N"), TriggeringEventId = Guid.NewGuid(),
        IssuerEntityId = Entity, IssuerSnapshot = "{}", OwnerSnapshot = "{}", BrandingSnapshot = "{}",
        Language = "en", Provenance = CertificateProvenance.SystemGenerated, IssuedAt = issuedAt,
        Status = CertificateStatus.Issued,
    };

    private static async Task<List<Guid>> RawIdsAsync(SerialisationDbContext db, string sql, TenantId tenant,
        Guid unit, Guid? other = null)
    {
        var parameters = new List<object> { new SqlParameter("@tenant", tenant.Value),
            new SqlParameter("@unit", unit) };
        if (other is { } otherUnit) parameters.Add(new SqlParameter("@other", otherUnit));
        return await db.Database.SqlQueryRaw<Guid>(sql, parameters.ToArray()).ToListAsync();
    }

    private static async Task<List<string>> RawStringsAsync(SerialisationDbContext db, string sql,
        TenantId tenant, Guid unit, Guid? other = null)
    {
        var parameters = new List<object> { new SqlParameter("@tenant", tenant.Value),
            new SqlParameter("@unit", unit) };
        if (other is { } otherUnit) parameters.Add(new SqlParameter("@other", otherUnit));
        return await db.Database.SqlQueryRaw<string>(sql, parameters.ToArray()).ToListAsync();
    }

    private void RegisterItem(Guid itemId, string code, string name) =>
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new(itemId, code, name));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static SerialisationDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();

    private static string Serial(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N")[..10]}".ToUpperInvariant();
}
