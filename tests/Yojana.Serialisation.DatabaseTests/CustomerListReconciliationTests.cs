using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Portal;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P7-T11: durable three-way reconciliation and the assertion decision path.</summary>
[Collection("serialisation host")]
public sealed class CustomerListReconciliationTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Pasted_list_matches_any_normalised_identifier_pages_by_key_and_streams_csv_and_xlsx()
    {
        var tenant = await fixture.NewTenantAsync("recpage");
        var customer = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var item = Guid.NewGuid();
        var ownedSerial = Serial("OWN");
        var otherSerial = Serial("OTHER");
        var owned = await UnitAsync(tenant, item, ownedSerial);
        var elsewhere = await UnitAsync(tenant, item, otherSerial);
        await OwnAsync(tenant, owned, customer, At.AddDays(1));
        await OwnAsync(tenant, elsewhere, otherOwner, At.AddDays(1));
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), elsewhere,
            new IdentifierInput(IdentifierType.ManufacturerSerial, " mfg-44 01 "), At.AddDays(2),
            "test:identity", Entity));
        RegisterNames(item, "SYS-REC", "Fictional Reconciliation Unit", customer, "Fictional Customer");
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(otherOwner, "OTHER-01",
            "Fictional Other Owner", PartyType.Customer));

        var command = new ReconcileCustomerList(Guid.NewGuid(), customer, new CustomerListSource(Pasted:
        [
            new CustomerListInput(ownedSerial),
            new CustomerListInput("MFG4401"),
            new CustomerListInput("NOT-FOUND-01", IdentifierType.InternalSerial),
            new CustomerListInput("123", IdentifierType.Vin),
        ]), At.AddDays(3), "ignored", Take: 2);
        var first = await InvokeAsync<CustomerListReconciliationPage>(tenant, command);

        Assert.Equal((4, 1, 1, 1, 1),
            (first.Total, first.Owned, first.OwnedByAnotherParty, first.NotFound, first.Invalid));
        Assert.Equal([CustomerListMatchOutcome.Owned, CustomerListMatchOutcome.OwnedByAnotherParty],
            first.Items.Select(row => row.Outcome));
        var claimed = first.Items[1];
        Assert.Equal("Fictional Other Owner", claimed.OtherOwner);
        Assert.Equal("SYS-REC · Fictional Reconciliation Unit", claimed.Item);
        Assert.Equal(CustomerAssertionStatus.PendingVerification, claimed.AssertionStatus);
        Assert.NotNull(first.NextCursor);

        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<CustomerListReconciliationService>();
        var second = await service.PageAsync(first.ReconciliationId, first.NextCursor, 2, false,
            CancellationToken.None);
        Assert.Equal([CustomerListMatchOutcome.NotFound, CustomerListMatchOutcome.Invalid],
            second.Items.Select(row => row.Outcome));
        Assert.Contains("vin_length_not_17", second.Items[1].ValidationReasons);
        Assert.Null(second.NextCursor);

        var replay = await InvokeAsync<CustomerListReconciliationPage>(tenant, command);
        Assert.True(replay.Replayed);
        var exporter = scope.ServiceProvider.GetRequiredService<ITabularExporter>();
        await using var csv = new MemoryStream();
        await exporter.WriteAsync("csv", new TabularExport(["row", "input", "outcome"],
            service.ExportAsync(first.ReconciliationId, CancellationToken.None)), csv, CancellationToken.None);
        var csvText = Encoding.UTF8.GetString(csv.ToArray());
        Assert.Contains("Fictional Other Owner", csvText);
        Assert.Contains("invalid", csvText);
        await using var xlsx = new MemoryStream();
        await exporter.WriteAsync("xlsx", new TabularExport(["row", "input", "outcome"],
            service.ExportAsync(first.ReconciliationId, CancellationToken.None)), xlsx, CancellationToken.None);
        Assert.Equal((byte)'P', xlsx.ToArray()[0]);
        Assert.Equal((byte)'K', xlsx.ToArray()[1]);
    }

    [DatabaseFact]
    public async Task Default_verification_moves_custody_with_ownership_and_replay_returns_the_same_decision()
    {
        var tenant = await fixture.NewTenantAsync("recdecide");
        var priorOwner = Guid.NewGuid();
        var claimant = Guid.NewGuid();
        var item = Guid.NewGuid();
        var verifiedSerial = Serial("VERIFY");
        var rejectedSerial = Serial("REJECT");
        var verifiedUnit = await UnitAsync(tenant, item, verifiedSerial);
        var rejectedUnit = await UnitAsync(tenant, item, rejectedSerial);
        await OwnAsync(tenant, verifiedUnit, priorOwner, At.AddDays(1));
        await OwnAsync(tenant, rejectedUnit, priorOwner, At.AddDays(1));
        await HoldAsync(tenant, verifiedUnit, LocationKind.CustomerSite, priorOwner, PartyType.Customer,
            At.AddDays(2));
        RegisterNames(item, "SYS-DEC", "Fictional Decision Unit", claimant, "Fictional Claimant");
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(priorOwner, "PRIOR-01",
            "Fictional Prior Owner", PartyType.Customer));

        var reconciliation = await InvokeAsync<CustomerListReconciliationPage>(tenant,
            new ReconcileCustomerList(Guid.NewGuid(), claimant, new CustomerListSource(Pasted:
            [new(verifiedSerial), new(rejectedSerial)]), At.AddDays(2), "ignored", Take: 20));
        var verifiedAssertion = reconciliation.Items.Single(row => row.Input == verifiedSerial).AssertionId!.Value;
        var rejectedAssertion = reconciliation.Items.Single(row => row.Input == rejectedSerial).AssertionId!.Value;
        Assert.True(reconciliation.Items.Single(row => row.Input == verifiedSerial).UnitAtCustomerSiteDefault);

        using (var beforeScope = fixture.ScopeFor(tenant))
        {
            var portal = beforeScope.ServiceProvider.GetRequiredService<PortalQueries>();
            Assert.Equal(PortalQueryOutcome.NotFound,
                (await portal.LookupAsync(new(tenant, claimant), verifiedSerial, null,
                    CancellationToken.None)).Outcome);
        }

        var evidenceDate = At.AddDays(5);
        var verify = new VerifyCustomerAssertion(Guid.NewGuid(), verifiedAssertion, "delivery:DN-4401",
            evidenceDate, Entity, "ignored", [YojanaRole.SerialAdministrator]);
        var verified = await InvokeAsync<CustomerAssertionDecision>(tenant, verify);
        Assert.Equal(CustomerAssertionStatus.Verified, verified.Status);
        Assert.NotNull(verified.OwnershipEpisodeId);
        Assert.NotNull(verified.CustodyEpisodeId);
        Assert.True(verified.UnitAtCustomerSite);
        var replay = await InvokeAsync<CustomerAssertionDecision>(tenant, verify with { UnitAtCustomerSite = false });
        Assert.True(replay.Replayed);
        Assert.Equal(verified.OwnershipEpisodeId, replay.OwnershipEpisodeId);
        Assert.Equal(verified.CustodyEpisodeId, replay.CustodyEpisodeId);
        Assert.True(replay.UnitAtCustomerSite);
        var rejected = await InvokeAsync<CustomerAssertionDecision>(tenant, new RejectCustomerAssertion(
            Guid.NewGuid(), rejectedAssertion, "delivery evidence names a different serial", "ignored",
            [YojanaRole.OrderDeskSupervisor]));
        Assert.Equal(CustomerAssertionStatus.Rejected, rejected.Status);
        Assert.Equal("delivery evidence names a different serial", rejected.RejectionReason);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var prior = await db.Episodes.SingleAsync(row => row.UnitId == verifiedUnit
            && row.Kind == EpisodeKind.Ownership && row.PartyRef == priorOwner);
        var current = await db.Episodes.SingleAsync(row => row.UnitId == verifiedUnit
            && row.Kind == EpisodeKind.Ownership && row.PartyRef == claimant);
        Assert.Equal(evidenceDate, prior.ValidTo);
        Assert.Equal(evidenceDate, current.ValidFrom);
        Assert.Equal("partner_report", current.Source);
        Assert.Equal("delivery:DN-4401", current.ExternalOrderRef);
        var priorCustody = await db.Episodes.SingleAsync(row => row.UnitId == verifiedUnit
            && row.Kind == EpisodeKind.Custody && row.PartyRef == priorOwner);
        var currentCustody = await db.Episodes.SingleAsync(row => row.UnitId == verifiedUnit
            && row.Kind == EpisodeKind.Custody && row.PartyRef == claimant);
        Assert.Equal(evidenceDate, priorCustody.ValidTo);
        Assert.Equal(evidenceDate, currentCustody.ValidFrom);
        Assert.Equal((LocationKind.CustomerSite, EpisodeReason.Sale, "partner_report", "delivery:DN-4401"),
            (currentCustody.LocationKind, currentCustody.Reason, currentCustody.Source,
                currentCustody.ExternalOrderRef));
        var custodyEvent = await db.Events.SingleAsync(row => row.UnitId == verifiedUnit
            && row.EventTime == evidenceDate && row.EventType == UnitEventType.CustodyChanged);
        Assert.Contains(priorOwner.ToString(), custodyEvent.Attributes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(claimant.ToString(), custodyEvent.Attributes, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == verifiedUnit
            && row.EventTime == evidenceDate
            && (row.EventType == UnitEventType.OwnershipTransferred
                || row.EventType == UnitEventType.CustodyChanged)));
        Assert.Single(await db.Episodes.Where(row => row.UnitId == verifiedUnit
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null).ToListAsync());
        var retained = await db.CustomerAssertions.SingleAsync(row => row.AssertionId == rejectedAssertion);
        Assert.Equal(CustomerAssertionStatus.Rejected, retained.Status);
        Assert.Equal("delivery evidence names a different serial", retained.RejectionReason);
        Assert.Null(retained.OwnershipEpisodeId);
        var portalAfter = scope.ServiceProvider.GetRequiredService<PortalQueries>();
        Assert.Equal(PortalQueryOutcome.Found,
            (await portalAfter.LookupAsync(new(tenant, claimant), verifiedSerial, null,
                CancellationToken.None)).Outcome);
        Assert.Equal(PortalQueryOutcome.NotFound,
            (await portalAfter.LookupAsync(new(tenant, claimant), rejectedSerial, null,
                CancellationToken.None)).Outcome);
    }

    [DatabaseFact]
    public async Task Explicit_false_leaves_customer_site_custody_and_a_third_party_location_defaults_false()
    {
        var tenant = await fixture.NewTenantAsync("reccustodyoff");
        var priorOwner = Guid.NewGuid();
        var claimant = Guid.NewGuid();
        var carrier = Guid.NewGuid();
        var item = Guid.NewGuid();
        var heldSerial = Serial("HELD");
        var remoteSerial = Serial("REMOTE");
        var heldUnit = await UnitAsync(tenant, item, heldSerial);
        var remoteUnit = await UnitAsync(tenant, item, remoteSerial);
        await OwnAsync(tenant, heldUnit, priorOwner, At.AddDays(1));
        await OwnAsync(tenant, remoteUnit, priorOwner, At.AddDays(1));
        await HoldAsync(tenant, heldUnit, LocationKind.CustomerSite, priorOwner, PartyType.Customer,
            At.AddDays(2));
        await HoldAsync(tenant, remoteUnit, LocationKind.At3pl, carrier, PartyType.ThreePl,
            At.AddDays(2));
        RegisterNames(item, "SYS-HOLD", "Fictional Storage Controller", claimant, "Fictional New Owner");

        var reconciliation = await InvokeAsync<CustomerListReconciliationPage>(tenant,
            new ReconcileCustomerList(Guid.NewGuid(), claimant, new CustomerListSource(Pasted:
                [new(heldSerial), new(remoteSerial)]), At.AddDays(3), "ignored", Take: 20));
        var held = reconciliation.Items.Single(row => row.Input == heldSerial);
        var remote = reconciliation.Items.Single(row => row.Input == remoteSerial);
        Assert.True(held.UnitAtCustomerSiteDefault);
        Assert.False(remote.UnitAtCustomerSiteDefault);

        var heldDecision = await InvokeAsync<CustomerAssertionDecision>(tenant, new VerifyCustomerAssertion(
            Guid.NewGuid(), held.AssertionId!.Value, "customer-statement:HOLD-01", At.AddDays(5), Entity,
            "ignored", [YojanaRole.SerialAdministrator], UnitAtCustomerSite: false));
        var remoteDecision = await InvokeAsync<CustomerAssertionDecision>(tenant, new VerifyCustomerAssertion(
            Guid.NewGuid(), remote.AssertionId!.Value, "customer-statement:REMOTE-01", At.AddDays(5), Entity,
            "ignored", [YojanaRole.SerialAdministrator]));
        Assert.False(heldDecision.UnitAtCustomerSite);
        Assert.Null(heldDecision.CustodyEpisodeId);
        Assert.False(remoteDecision.UnitAtCustomerSite);
        Assert.Null(remoteDecision.CustodyEpisodeId);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var heldCustody = await db.Episodes.SingleAsync(row => row.UnitId == heldUnit
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var remoteCustody = await db.Episodes.SingleAsync(row => row.UnitId == remoteUnit
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((LocationKind.CustomerSite, priorOwner),
            (heldCustody.LocationKind, heldCustody.PartyRef));
        Assert.Equal((LocationKind.At3pl, carrier), (remoteCustody.LocationKind, remoteCustody.PartyRef));
        var details = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.UnitQueries>();
        Assert.True((await details.DetailAsync(heldUnit, CancellationToken.None))!.OwnerAndHolderDiffer);
        Assert.False((await details.DetailAsync(remoteUnit, CancellationToken.None))!.OwnerAndHolderDiffer);
    }

    [DatabaseFact]
    public async Task Assertion_decisions_are_tenant_isolated()
    {
        var owningTenant = await fixture.NewTenantAsync("recisolationa");
        var otherTenant = await fixture.NewTenantAsync("recisolationb");
        var claimant = Guid.NewGuid();
        var unit = await UnitAsync(owningTenant, Guid.NewGuid(), Serial("PRIVATE"));
        await OwnAsync(owningTenant, unit, Guid.NewGuid(), At.AddDays(1));
        var serial = await SerialForAsync(owningTenant, unit);
        var reconciliation = await InvokeAsync<CustomerListReconciliationPage>(owningTenant,
            new ReconcileCustomerList(Guid.NewGuid(), claimant, new CustomerListSource(Pasted:
                [new(serial)]), At.AddDays(2), "ignored"));
        var assertionId = Assert.Single(reconciliation.Items).AssertionId!.Value;

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<CustomerAssertionDecision>(otherTenant, new VerifyCustomerAssertion(Guid.NewGuid(),
                assertionId, "customer-statement:PRIVATE-01", At.AddDays(5), Entity, "ignored",
                [YojanaRole.SerialAdministrator])));
        Assert.Equal("customer_assertion_unknown", refusal.Rule);

        using var scope = fixture.ScopeFor(owningTenant);
        var stored = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .CustomerAssertions.SingleAsync(row => row.AssertionId == assertionId);
        Assert.Equal(CustomerAssertionStatus.PendingVerification, stored.Status);
        Assert.Null(stored.OwnershipEpisodeId);
        Assert.Null(stored.CustodyEpisodeId);
    }

    [DatabaseFact]
    public async Task Import_job_and_selection_set_are_both_reconciliation_sources()
    {
        var tenant = await fixture.NewTenantAsync("recsource");
        var customer = Guid.NewGuid();
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var provisional = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.Provisional, "TEMP-REC-1")])],
            At, "test:production", Entity, Document: order));
        var importedSerial = scheme.Format(0, 1);
        var import = await InvokeAsync<RunImportResult>(tenant, new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("customer-list.csv", ImportFormat.Csv, Encoding.UTF8.GetBytes($"Serial\r\n{importedSerial}\r\n")),
            At.AddHours(1), Entity, "ignored", order, new("Serial"), CommittedBy: Committer()));
        var importedUnit = provisional.Lines.Single().UnitId!.Value;
        await OwnAsync(tenant, importedUnit, customer, At.AddDays(1));
        var importPage = await InvokeAsync<CustomerListReconciliationPage>(tenant,
            new ReconcileCustomerList(Guid.NewGuid(), customer,
                new CustomerListSource(ImportJobId: Assert.Single(import.Jobs).JobId), At.AddDays(2), "ignored"));
        Assert.Equal(CustomerListSourceKind.ImportJob, importPage.SourceKind);
        Assert.Equal(CustomerListMatchOutcome.Owned, Assert.Single(importPage.Items).Outcome);

        var selectedSerial = Serial("SELECT");
        var selectedUnit = await UnitAsync(tenant, item, selectedSerial);
        await OwnAsync(tenant, selectedUnit, customer, At.AddDays(1));
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([selectedSerial]), "test:reviewer"));
        var selectionPage = await InvokeAsync<CustomerListReconciliationPage>(tenant,
            new ReconcileCustomerList(Guid.NewGuid(), customer,
                new CustomerListSource(SelectionId: selection.SelectionId), At.AddDays(2), "ignored"));
        Assert.Equal(CustomerListSourceKind.SelectionSet, selectionPage.SourceKind);
        Assert.Equal(CustomerListMatchOutcome.Owned, Assert.Single(selectionPage.Items).Outcome);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, Guid itemId, string serial)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "test:production", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return result.Lines.Single().UnitId!.Value;
    }

    private Task<OpenOwnershipResult> OwnAsync(TenantId tenant, Guid unitId, Guid partyRef, DateTime at) =>
        InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, partyRef,
            PartyType.Customer, EpisodeReason.Sale, at, "test:sales", Entity));

    private Task<ChangeCustodyResult> HoldAsync(TenantId tenant, Guid unitId, string locationKind,
        Guid partyRef, string partyType, DateTime at) =>
        InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unitId, locationKind,
            EpisodeReason.Sale, at, "test:delivery", Entity, PartyRef: partyRef, PartyType: partyType));

    private async Task<string> SerialForAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers
            .Where(row => row.UnitId == unitId && row.Type == IdentifierType.InternalSerial
                && row.ValidTo == null)
            .Select(row => row.NormalisedValue).SingleAsync();
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private SerialSchemeDefinition RegisterScheme(Guid item)
    {
        var code = "RC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, IdentifierType.VendorSerial, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { SerialScheme = code });
        return scheme;
    }

    private void RegisterNames(Guid item, string itemCode, string itemName, Guid party, string partyName)
    {
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new(item, itemCode, itemName));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(party, "CUSTOMER", partyName,
            PartyType.Customer));
    }

    private static CorrectionCommitter Committer() => new("test:production", [YojanaRole.ProductionSupervisor]);

    private static string Serial(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}".ToUpperInvariant();
}
