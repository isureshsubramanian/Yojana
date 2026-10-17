using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T03/T04: RMA state, receipt exceptions, disposition effects and allocated child returns.</summary>
[Collection("serialisation host")]
public sealed class RmaTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("55555555-0000-0000-0000-00000000d001");
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
    private InMemoryWarrantyPorts Warranties => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Six_stages_are_adjacent_illegal_transitions_are_refused_and_all_commands_replay()
    {
        var setup = await SetUpReturnedUnitAsync("rma-state");
        var request = Request(setup, Guid.NewGuid());
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, request);
        Assert.Equal(RmaStage.Requested, requested.Stage);
        Assert.True((await InvokeAsync<RequestRmaResult>(setup.Tenant, request)).Replayed);

        var receiveTooSoon = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReceiveReturnResult>(setup.Tenant,
            Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid())));
        Assert.Equal("rma_transition_not_allowed", receiveTooSoon.Rule);

        var authorise = new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1));
        var authorised = await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, authorise);
        Assert.Equal(RmaStage.Authorised, authorised.Stage);
        Assert.StartsWith("RMA-2026-", authorised.RmaNumber, StringComparison.Ordinal);
        Assert.True((await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, authorise)).Replayed);
        Assert.Equal(100m, Assert.Single(authorised.Lines).CoveragePct);

        var dispatch = new RecordReturnDispatched(Guid.NewGuid(), requested.RmaId, At.AddHours(2), "Asterline Freight", "AF-10001");
        var dispatched = await InvokeAsync<RecordReturnDispatchedResult>(setup.Tenant, dispatch);
        Assert.Equal(RmaStage.InTransit, dispatched.Stage);
        Assert.True((await InvokeAsync<RecordReturnDispatchedResult>(setup.Tenant, dispatch)).Replayed);

        var receiptCommand = Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid());
        var received = await InvokeAsync<ReceiveReturnResult>(setup.Tenant, receiptCommand);
        Assert.Equal(RmaStage.Received, received.Stage);
        Assert.True((await InvokeAsync<ReceiveReturnResult>(setup.Tenant, receiptCommand)).Replayed);

        var dispositionCommand = new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new(requested.LineIds[0], RmaDisposition.RepairAndReturn)], new DateOnly(2026, 9, 1), At.AddHours(5));
        var dispositioned = await InvokeAsync<DispositionRmaResult>(setup.Tenant, dispositionCommand);
        Assert.Equal(RmaStage.Dispositioned, dispositioned.Stage);
        Assert.True((await InvokeAsync<DispositionRmaResult>(setup.Tenant, dispositionCommand)).Replayed);
        var publishedDisposition = Assert.Single(await PublishedEventSink.WaitForAsync<RmaDispositioned>(e => e.RmaId == requested.RmaId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((requested.RmaId, requested.LineIds[0], setup.Unit, RmaDisposition.RepairAndReturn, null, null),
            (publishedDisposition.RmaId, publishedDisposition.RmaLineId, publishedDisposition.UnitId,
                publishedDisposition.Disposition, publishedDisposition.EntitlementClosedReason, publishedDisposition.ReplacementIntentId));
        Assert.Equal((RmaDispositioned.Schema, RmaDispositioned.Version, setup.Unit),
            (publishedDisposition.Header.Schema, publishedDisposition.Header.Version, publishedDisposition.Header.UnitId));
        await WaitForStageAsync(setup.Tenant, requested.RmaId, RmaStage.Closed);

        var redisposition = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<DispositionRmaResult>(setup.Tenant,
            dispositionCommand with { CommandId = Guid.NewGuid() }));
        Assert.Equal("rma_transition_not_allowed", redisposition.Rule);

        var authoriseClosed = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AuthoriseRmaResult>(setup.Tenant,
            new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(6))));
        Assert.Equal("rma_transition_not_allowed", authoriseClosed.Rule);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var saved = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Rmas.AsNoTracking().SingleAsync(r => r.RmaId == requested.RmaId);
        Assert.Equal(RmaStage.Closed, saved.Stage);
        Assert.NotNull(saved.AuthorisedAt);
        Assert.NotNull(saved.InTransitAt);
        Assert.NotNull(saved.ReceivedAt);
        Assert.NotNull(saved.DispositionedAt);
        Assert.NotNull(saved.ClosedAt);
    }

    [DatabaseFact]
    public async Task Dispatch_then_receive_moves_only_custody_and_does_not_record_dispatch_twice()
    {
        var setup = await SetUpReturnedUnitAsync("rma-dispatch");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var command = new RecordReturnDispatched(Guid.NewGuid(), requested.RmaId, At.AddHours(2), "Asterline Freight", "AF-10002");
        var dispatched = await InvokeAsync<RecordReturnDispatchedResult>(setup.Tenant, command);
        Assert.True((await InvokeAsync<RecordReturnDispatchedResult>(setup.Tenant, command)).Replayed);

        using (var dispatchedScope = fixture.ScopeFor(setup.Tenant))
        {
            var db = dispatchedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var owner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
            var holder = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
            Assert.Equal((setup.Customer, PartyType.Customer), (owner.PartyRef, owner.PartyType));
            Assert.Equal((LocationKind.Returning, setup.Customer, PartyType.Customer), (holder.LocationKind, holder.PartyRef, holder.PartyType));
            Assert.Equal(EntitlementStatus.Active, await db.Entitlements.Where(row => row.EntitlementId == setup.EntitlementId).Select(row => row.Status).SingleAsync());
            Assert.Single(await db.Identifiers.Where(row => row.UnitId == setup.Unit && row.ValidTo == null).ToListAsync());
            Assert.Equal(dispatched.EventId, await db.Events.Where(row => row.UnitId == setup.Unit && row.EventType == RmaEventType.Dispatched).Select(row => row.EventId).SingleAsync());
        }

        var receiptCommand = Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid());
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, receiptCommand);
        using var receivedScope = fixture.ScopeFor(setup.Tenant);
        var receivedDb = receivedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var receivedHolder = await receivedDb.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var receivedOwner = await receivedDb.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        Assert.Equal((LocationKind.Quarantine, receiptCommand.ReceivingLocationId, (Guid?)null, PartyType.Us),
            (receivedHolder.LocationKind, receivedHolder.LocationId, receivedHolder.PartyRef, receivedHolder.PartyType));
        Assert.Equal((setup.Customer, PartyType.Customer), (receivedOwner.PartyRef, receivedOwner.PartyType));
        Assert.Equal(EntitlementStatus.Active, await receivedDb.Entitlements.Where(row => row.EntitlementId == setup.EntitlementId).Select(row => row.Status).SingleAsync());
        Assert.Single(await receivedDb.Identifiers.Where(row => row.UnitId == setup.Unit && row.ValidTo == null).ToListAsync());
        Assert.Equal(1, await receivedDb.Events.CountAsync(row => row.UnitId == setup.Unit && row.EventType == RmaEventType.Dispatched));
        Assert.Contains(await receivedDb.Events.Where(row => row.UnitId == setup.Unit).Select(row => row.EventType).ToListAsync(), eventType => eventType == RmaEventType.Received);
    }

    [DatabaseFact]
    public async Task Receipt_directly_from_authorised_skips_dispatch_without_inventing_an_in_transit_fact()
    {
        var setup = await SetUpReturnedUnitAsync("rma-direct-receipt");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var receiptCommand = Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()) with { DispatchedAt = null };
        var received = await InvokeAsync<ReceiveReturnResult>(setup.Tenant, receiptCommand);
        Assert.Equal(RmaStage.Received, received.Stage);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rma = await db.Rmas.AsNoTracking().SingleAsync(row => row.RmaId == requested.RmaId);
        Assert.Null(rma.InTransitAt);
        Assert.False(await db.Events.AnyAsync(row => row.UnitId == setup.Unit && row.EventType == RmaEventType.Dispatched));
        var holder = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var owner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        Assert.Equal((LocationKind.Quarantine, receiptCommand.ReceivingLocationId, (Guid?)null, PartyType.Us),
            (holder.LocationKind, holder.LocationId, holder.PartyRef, holder.PartyType));
        Assert.Equal((setup.Customer, PartyType.Customer), (owner.PartyRef, owner.PartyType));
        Assert.Equal(EntitlementStatus.Active, await db.Entitlements.Where(row => row.EntitlementId == setup.EntitlementId).Select(row => row.Status).SingleAsync());
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == setup.Unit && row.ValidTo == null).ToListAsync());
        Assert.Contains(await db.Events.Where(row => row.UnitId == setup.Unit).Select(row => row.EventType).ToListAsync(), eventType => eventType == RmaEventType.Received);
    }

    [DatabaseFact]
    public async Task Replacement_disposition_stays_open_reports_its_follow_up_and_closes_when_shipped()
    {
        var setup = await SetUpReturnedUnitAsync("rma-replacement-close");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()) with { DispatchedAt = null });
        var dispositioned = await InvokeAsync<DispositionRmaResult>(setup.Tenant, new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new(requested.LineIds[0], RmaDisposition.Replace)], new DateOnly(2026, 9, 1), At.AddHours(5)));
        Assert.Equal(RmaStage.Dispositioned, dispositioned.Stage);
        await WaitForStageAsync(setup.Tenant, requested.RmaId, RmaStage.Dispositioned);

        var close = new CloseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(6), "supervisor");
        var refused = await InvokeAsync<CloseRmaResult>(setup.Tenant, close);
        Assert.False(refused.Closed);
        var followUp = Assert.Single(refused.OpenFollowUps);
        Assert.Equal((requested.LineIds[0], RmaDisposition.Replace, "replacement_not_shipped"),
            (followUp.RmaLineId, followUp.Disposition, followUp.Code));
        Assert.False((await InvokeAsync<CloseRmaResult>(setup.Tenant, close)).Closed);

        var (replacementUnit, _) = await CommissionAsync(setup.Tenant, setup.Item,
            "REPL-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-10));
        var created = await InvokeAsync<CreateWarrantyReplacementResult>(setup.Tenant, new CreateWarrantyReplacement(
            Guid.NewGuid(), ReplacementSourceKind.Rma, requested.RmaId, setup.EntitlementId, replacementUnit,
            ReplacementReason.WarrantyDefect, ReplacementPricePolicy.Zero, 0m, "EUR", "DE", false, false,
            At.AddHours(6), Entity, "service.desk", ReplacementConditionAllowed.Any));
        var shipped = await InvokeAsync<ShipWarrantyReplacementResult>(setup.Tenant, new ShipWarrantyReplacement(
            Guid.NewGuid(), created.Document.Document.WarrantyReplacementId, new DateOnly(2026, 9, 1),
            At.AddHours(7), "warehouse"));
        Assert.Equal(ReplacementLinkState.ClosedReturned, shipped.LinkState);
        await WaitForStageAsync(setup.Tenant, requested.RmaId, RmaStage.Closed);
        Assert.True((await InvokeAsync<CloseRmaResult>(setup.Tenant,
            new CloseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(8), "supervisor"))).Replayed);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rma = await db.Rmas.AsNoTracking().SingleAsync(row => row.RmaId == requested.RmaId);
        Assert.Equal(At.AddHours(7), rma.ClosedAt);
        var originalOwner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var originalHolder = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var replacementOwner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == replacementUnit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var replacementHolder = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == replacementUnit && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((Entity, PartyType.Us, LocationKind.Quarantine, (Guid?)null, PartyType.Us),
            (originalOwner.PartyRef, originalOwner.PartyType, originalHolder.LocationKind, originalHolder.PartyRef, originalHolder.PartyType));
        Assert.Equal((setup.Customer, PartyType.Customer, LocationKind.CustomerSite, setup.Customer, PartyType.Customer),
            (replacementOwner.PartyRef, replacementOwner.PartyType, replacementHolder.LocationKind, replacementHolder.PartyRef, replacementHolder.PartyType));
        var sourceEntitlement = await db.Entitlements.AsNoTracking().SingleAsync(row => row.EntitlementId == setup.EntitlementId);
        var targetEntitlement = await db.Entitlements.AsNoTracking().SingleAsync(row => row.EntitlementId == shipped.TargetEntitlementId);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.Replaced), (sourceEntitlement.Status, sourceEntitlement.ClosedReason));
        Assert.Equal((EntitlementStatus.Active, replacementUnit, setup.Customer), (targetEntitlement.Status, targetEntitlement.UnitId, targetEntitlement.OwnerPartyRef));
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == setup.Unit && row.ValidTo == null).ToListAsync());
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == replacementUnit && row.ValidTo == null).ToListAsync());
        var originalEvents = await db.Events.Where(row => row.UnitId == setup.Unit).Select(row => row.EventType).ToListAsync();
        var replacementEvents = await db.Events.Where(row => row.UnitId == replacementUnit).Select(row => row.EventType).ToListAsync();
        Assert.Contains(RmaEventType.Dispositioned, originalEvents);
        Assert.Contains(ReplacementEventType.Shipped, replacementEvents);
        Assert.Contains(UnitEventType.OwnershipOpened, replacementEvents);
        Assert.Contains(UnitEventType.Shipped, replacementEvents);
    }

    [DatabaseTheory]
    [InlineData(ReturnOutcome.Mismatch)]
    [InlineData(ReturnOutcome.Unauthorised)]
    [InlineData(ReturnOutcome.Counterfeit)]
    public async Task Receipt_exceptions_are_explicit_and_never_rebind_the_authorised_unit(string requestedOutcome)
    {
        var setup = await SetUpReturnedUnitAsync("rma-ex-" + requestedOutcome[..3]);
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));

        DocumentSerial? receivedIdentifier = null;
        Guid? otherUnit = null;
        if (requestedOutcome == ReturnOutcome.Mismatch)
        {
            (otherUnit, var otherSerial) = await CommissionAsync(setup.Tenant, setup.Item, "OTHER-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-4));
            receivedIdentifier = new DocumentSerial(otherSerial);
        }
        var command = new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Guid.NewGuid(),
            [new(requested.LineIds[0], receivedIdentifier, ConditionGrades.Defective, "graded_value", requestedOutcome, "inspection hold")],
            At.AddHours(2), At.AddHours(3), At.AddHours(3));
        var result = await InvokeAsync<ReceiveReturnResult>(setup.Tenant, command);
        var receipt = Assert.Single(result.Lines);
        Assert.Equal(requestedOutcome, receipt.Outcome);
        Assert.True(receipt.ExceptionRaised);
        if (requestedOutcome == ReturnOutcome.Mismatch) Assert.Equal(otherUnit, receipt.ReceivedUnitId); else Assert.NotNull(receipt.ProvisionalUnitId);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var line = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().RmaLines.AsNoTracking().SingleAsync(l => l.RmaLineId == requested.LineIds[0]);
            Assert.Equal(setup.Unit, line.AuthorisedUnitId);
            Assert.Equal(otherUnit, line.ReceivedUnitId);
        }
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<DispositionRmaResult>(setup.Tenant,
            new DispositionRma(Guid.NewGuid(), requested.RmaId, [new(requested.LineIds[0], RmaDisposition.Reject)], new DateOnly(2026, 9, 1), At.AddHours(4))));
        Assert.Equal("rma_receipt_exception_open", refused.Rule);
    }

    [DatabaseFact]
    public async Task Unknown_grade_is_refused_before_a_receipt_or_custody_fact_is_written()
    {
        var setup = await SetUpReturnedUnitAsync("rma-grade");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Guid.NewGuid(), [new(requested.LineIds[0], new(setup.Serial), "grade_z", "graded_value")], At.AddHours(2), At.AddHours(3), At.AddHours(3))));
        Assert.Equal("condition_grade_invalid", refused.Rule);
        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.False(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ReturnReceipts.AnyAsync(r => r.RmaId == requested.RmaId));
    }

    [DatabaseFact]
    public async Task Request_validation_refusals_are_typed_and_write_no_rma()
    {
        var setup = await SetUpReturnedUnitAsync("rma-request-refuse");
        var valid = Request(setup, Guid.NewGuid());
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with { CommandId = Guid.NewGuid(), Lines = [] }, "rma_lines_required");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with { CommandId = Guid.NewGuid(), RequestDate = valid.RequestDate.AddDays(1) }, "rma_request_date_future");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with { CommandId = Guid.NewGuid(), Lines = [new RmaRequestLineInput()] }, "rma_identity_choice");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with
        {
            CommandId = Guid.NewGuid(),
            Lines = [new RmaRequestLineInput(Identifier: new DocumentSerial(setup.Serial), RequestedAmount: -1)]
        }, "rma_coverage_input_invalid");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with
        {
            CommandId = Guid.NewGuid(),
            Lines = [new RmaRequestLineInput(ProofOfPurchase: new DocumentRef("delivery", Guid.NewGuid()))]
        }, "proof_of_purchase_unknown");
        var pluralProof = new DocumentRef("delivery", Guid.NewGuid());
        Documents.Add(new ShipmentDocument(pluralProof, setup.Customer, At.AddDays(-20),
            [new DocumentLine(1, setup.Item, 2, [new(setup.Serial), new("SECOND-" + Guid.NewGuid().ToString("N")[..8])])], Guid.NewGuid()));
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with
        {
            CommandId = Guid.NewGuid(),
            Lines = [new RmaRequestLineInput(ProofOfPurchase: pluralProof)]
        }, "proof_of_purchase_not_singular");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with
        {
            CommandId = Guid.NewGuid(),
            Lines = [new RmaRequestLineInput(UnitId: Guid.NewGuid())]
        }, "rma_unit_unknown");
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, valid with
        {
            CommandId = Guid.NewGuid(),
            Lines =
            [
                new RmaRequestLineInput(Identifier: new DocumentSerial(setup.Serial)),
                new RmaRequestLineInput(Identifier: new DocumentSerial(setup.Serial)),
            ]
        }, "rma_unit_duplicate");

        var (unitWithoutOwner, serialWithoutOwner) = await CommissionAsync(setup.Tenant, setup.Item, "NOOWNER-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-10));
        var missingFacts = valid with
        {
            CommandId = Guid.NewGuid(),
            Lines = [new RmaRequestLineInput(Identifier: new DocumentSerial(serialWithoutOwner))]
        };
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, missingFacts, "current_ownership_required");
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), unitWithoutOwner, setup.Customer, PartyType.Customer,
            EpisodeReason.Sale, At.AddDays(-9), "test", Entity));
        await AssertRefusedAsync<RequestRmaResult>(setup.Tenant, missingFacts with { CommandId = Guid.NewGuid() }, "original_sale_not_resolved");

        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.False(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Rmas.AnyAsync());
    }

    [DatabaseFact]
    public async Task Receipt_and_disposition_validation_refusals_are_typed_and_roll_back_the_stage()
    {
        var setup = await SetUpReturnedUnitAsync("rma-command-refuse");
        await AssertRefusedAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [], At, At, At), "rma_unknown");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var lineId = requested.LineIds[0];

        await AssertRefusedAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Guid.NewGuid(), [], At.AddHours(2), At.AddHours(3), At.AddHours(3)), "rma_receipt_lines_incomplete");
        await AssertRefusedAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Guid.NewGuid(), [new(lineId, new(setup.Serial), ConditionGrades.Defective, "graded_value")], At.AddHours(4), At.AddHours(3), At.AddHours(3)), "return_time_invalid");
        await AssertRefusedAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Guid.NewGuid(), [new(lineId, new(setup.Serial), ConditionGrades.Defective, "graded_value", "unknown")], At.AddHours(2), At.AddHours(3), At.AddHours(3)), "return_outcome_invalid");
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, lineId, setup.Serial, Guid.NewGuid()));

        await AssertRefusedAsync<DispositionRmaResult>(setup.Tenant,
            new DispositionRma(Guid.NewGuid(), requested.RmaId, [], new DateOnly(2026, 9, 1), At.AddHours(5)), "rma_disposition_lines_incomplete");
        await AssertRefusedAsync<DispositionRmaResult>(setup.Tenant,
            new DispositionRma(Guid.NewGuid(), requested.RmaId, [new(lineId, "unknown")], new DateOnly(2026, 9, 1), At.AddHours(5)), "rma_disposition_invalid");
        await AssertRefusedAsync<DispositionRmaResult>(setup.Tenant,
            new DispositionRma(Guid.NewGuid(), requested.RmaId, [new(lineId, RmaDisposition.ReturnToVendor)], new DateOnly(2026, 9, 1), At.AddHours(5)), "vendor_required");

        using var scope = fixture.ScopeFor(setup.Tenant);
        var saved = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Rmas.AsNoTracking().SingleAsync(r => r.RmaId == requested.RmaId);
        Assert.Equal(RmaStage.Received, saved.Stage);
    }

    [DatabaseFact]
    public async Task Provisional_identity_invalid_custody_and_unallocated_kit_each_block_disposition_or_receipt()
    {
        var provisionalSetup = await SetUpReturnedUnitAsync("rma-provisional");
        var provisionalRequest = await InvokeAsync<RequestRmaResult>(provisionalSetup.Tenant, Request(provisionalSetup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(provisionalSetup.Tenant, new AuthoriseRma(Guid.NewGuid(), provisionalRequest.RmaId, At.AddHours(1)));
        var provisionalReceipt = await InvokeAsync<ReceiveReturnResult>(provisionalSetup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), provisionalRequest.RmaId, Guid.NewGuid(),
                [new(provisionalRequest.LineIds[0], null, ConditionGrades.Defective, "graded_value")], At.AddHours(2), At.AddHours(3), At.AddHours(3)));
        Assert.True(Assert.Single(provisionalReceipt.Lines).ExceptionRaised);
        await AssertRefusedAsync<DispositionRmaResult>(provisionalSetup.Tenant,
            new DispositionRma(Guid.NewGuid(), provisionalRequest.RmaId, [new(provisionalRequest.LineIds[0], RmaDisposition.Reject)], new(2026, 9, 1), At.AddHours(5)),
            "received_unit_required");

        var custodySetup = await SetUpReturnedUnitAsync("rma-custody");
        var (vendorUnit, vendorSerial) = await CommissionAsync(custodySetup.Tenant, custodySetup.Item, "VENDOR-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-10));
        await InvokeAsync<ChangeCustodyResult>(custodySetup.Tenant, new ChangeCustody(Guid.NewGuid(), vendorUnit, LocationKind.Vendor,
            EpisodeReason.Return, At.AddDays(-9), "test", Entity, PartyRef: Guid.NewGuid(), PartyType: PartyType.Vendor));
        var custodyRequest = await InvokeAsync<RequestRmaResult>(custodySetup.Tenant, Request(custodySetup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(custodySetup.Tenant, new AuthoriseRma(Guid.NewGuid(), custodyRequest.RmaId, At.AddHours(1)));
        await AssertRefusedAsync<ReceiveReturnResult>(custodySetup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), custodyRequest.RmaId, Guid.NewGuid(),
                [new(custodyRequest.LineIds[0], new(vendorSerial), ConditionGrades.Defective, "graded_value")], At.AddHours(2), At.AddHours(3), At.AddHours(3)),
            "return_custody_not_accepted");

        var kitSetup = await SetUpReturnedUnitAsync("rma-kit-no-price");
        var (parent, _) = await CommissionAsync(kitSetup.Tenant, Guid.NewGuid(), "KITNP-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-10));
        using (var linkScope = fixture.ScopeFor(kitSetup.Tenant))
        {
            var db = linkScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.AssemblyLinks.Add(new AssemblyLink
            {
                TenantId = kitSetup.Tenant, LinkId = Guid.NewGuid(), ParentUnitId = parent, ChildUnitId = kitSetup.Unit,
                Relation = AssemblyRelation.Component, InstalledAt = At.AddDays(-9), InstallingEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        var kitRequest = await InvokeAsync<RequestRmaResult>(kitSetup.Tenant, Request(kitSetup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(kitSetup.Tenant, new AuthoriseRma(Guid.NewGuid(), kitRequest.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(kitSetup.Tenant, Receive(kitRequest.RmaId, kitRequest.LineIds[0], kitSetup.Serial, Guid.NewGuid()));
        await AssertRefusedAsync<DispositionRmaResult>(kitSetup.Tenant,
            new DispositionRma(Guid.NewGuid(), kitRequest.RmaId, [new(kitRequest.LineIds[0], RmaDisposition.ReturnToStock)], new(2026, 9, 1), At.AddHours(5)),
            "kit_price_allocation_required");
    }

    [DatabaseTheory]
    [InlineData(RmaDisposition.ReturnToStock, LocationKind.Warehouse, EntitlementClosedReason.ReturnedToStock, true)]
    [InlineData(RmaDisposition.Refurbish, LocationKind.InRepair, null, true)]
    [InlineData(RmaDisposition.RepairAndReturn, LocationKind.InRepair, null, false)]
    [InlineData(RmaDisposition.Scrap, LocationKind.Scrapped, null, true)]
    [InlineData(RmaDisposition.ReturnToVendor, LocationKind.Vendor, null, true)]
    [InlineData(RmaDisposition.Credit, LocationKind.Warehouse, EntitlementClosedReason.Refunded, true)]
    [InlineData(RmaDisposition.Refund, LocationKind.Warehouse, EntitlementClosedReason.Refunded, true)]
    [InlineData(RmaDisposition.Replace, LocationKind.Quarantine, null, true)]
    [InlineData(RmaDisposition.Reject, LocationKind.Quarantine, null, false)]
    [InlineData(RmaDisposition.RestockAsNew, LocationKind.Warehouse, null, true)]
    public async Task Every_disposition_has_the_specified_custody_entitlement_and_ownership_effect(
        string disposition, string expectedLocation, string? closeReason, bool ownershipLeavesCustomer)
    {
        var setup = await SetUpReturnedUnitAsync("rma-d-" + disposition[..Math.Min(5, disposition.Length)]);
        if (disposition == RmaDisposition.ReturnToStock)
        {
            using var inheritedScope = fixture.ScopeFor(setup.Tenant);
            var entitlement = await inheritedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.SingleAsync(e => e.EntitlementId == setup.EntitlementId);
            entitlement.ParentEntitlementId = Guid.NewGuid();
            await inheritedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()));
        var vendor = disposition == RmaDisposition.ReturnToVendor ? Guid.NewGuid() : (Guid?)null;
        var command = new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new(requested.LineIds[0], disposition, vendor)], new DateOnly(2026, 9, 1), At.AddHours(5));
        var result = await InvokeAsync<DispositionRmaResult>(setup.Tenant, command);
        Assert.Equal(RmaStage.Dispositioned, result.Stage);
        var resultLine = Assert.Single(result.Lines);
        Assert.Equal(closeReason, resultLine.EntitlementClosedReason);
        Assert.Equal(disposition == RmaDisposition.Replace, resultLine.ReplacementIntentId is not null);

        int ownershipEpisodeCount;
        using (var beforeReplay = fixture.ScopeFor(setup.Tenant))
        {
            ownershipEpisodeCount = await beforeReplay.ServiceProvider.GetRequiredService<SerialisationDbContext>().Episodes
                .CountAsync(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership);
        }
        Assert.True((await InvokeAsync<DispositionRmaResult>(setup.Tenant, command)).Replayed);
        if (disposition == RmaDisposition.Replace)
            await WaitForStageAsync(setup.Tenant, requested.RmaId, RmaStage.Dispositioned);
        else
            await WaitForStageAsync(setup.Tenant, requested.RmaId, RmaStage.Closed);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == setup.Unit);
        Assert.Equal(expectedLocation, current.LocationKind);
        var currentHolder = await db.Episodes.AsNoTracking().SingleAsync(e => e.UnitId == setup.Unit
            && e.Kind == EpisodeKind.Custody && e.ValidTo == null);
        Assert.Equal(expectedLocation, currentHolder.LocationKind);
        var entitlementStatus = await db.Entitlements.AsNoTracking().Where(e => e.EntitlementId == setup.EntitlementId).Select(e => new { e.Status, e.ClosedReason }).SingleAsync();
        Assert.Equal(closeReason is null ? EntitlementStatus.Active : EntitlementStatus.Closed, entitlementStatus.Status);
        Assert.Equal(closeReason, entitlementStatus.ClosedReason);
        Assert.Equal(0, await db.RmaLines.CountAsync(l => l.RmaId == requested.RmaId && l.Disposition == disposition && l.ReplacementIntentId != null && disposition != RmaDisposition.Replace));
        Assert.Equal(ownershipEpisodeCount, await db.Episodes.CountAsync(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership));
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == setup.Unit && row.ValidTo == null).ToListAsync());
        Assert.Contains(RmaEventType.Dispositioned,
            await db.Events.Where(row => row.UnitId == setup.Unit).Select(row => row.EventType).ToListAsync());

        var line = await db.RmaLines.AsNoTracking().SingleAsync(l => l.RmaLineId == requested.LineIds[0]);
        var customerEpisode = await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == line.OwnershipEpisodeId);
        var currentOwner = await db.Episodes.AsNoTracking().SingleAsync(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership && e.ValidTo == null);
        if (ownershipLeavesCustomer)
        {
            Assert.Equal(disposition == RmaDisposition.ReturnToVendor ? (PartyType.Vendor, vendor!.Value) : (PartyType.Us, Entity),
                (currentOwner.PartyType, currentOwner.PartyRef));
            Assert.Equal(At.AddHours(3), customerEpisode.ValidTo);
            Assert.Equal(("rma", requested.RmaId, 1), (customerEpisode.ClosingDocumentType, customerEpisode.ClosingDocumentId, customerEpisode.ClosingDocumentLine));
            var closed = await db.Events.AsNoTracking().SingleAsync(e => e.UnitId == setup.Unit
                && e.EventType == UnitEventType.OwnershipClosed && e.DocumentId == requested.RmaId && e.EventTime == At.AddHours(3));
            Assert.Equal(At.AddHours(3), closed.EventTime);
            using var attributes = JsonDocument.Parse(closed.Attributes!);
            Assert.Equal(EpisodeReason.Return, attributes.RootElement.GetProperty("reason").GetString());
        }
        else
        {
            Assert.Equal((PartyType.Customer, setup.Customer), (currentOwner.PartyType, currentOwner.PartyRef));
            Assert.Null(customerEpisode.ValidTo);
            Assert.False(await db.Events.AnyAsync(e => e.UnitId == setup.Unit
                && e.EventType == UnitEventType.OwnershipClosed && e.DocumentId == requested.RmaId));
        }
    }

    [DatabaseFact]
    public async Task Disposition_does_not_open_a_duplicate_ownership_episode_when_the_return_is_already_ours()
    {
        var setup = await SetUpReturnedUnitAsync("rma-owned");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.Unit, Entity, PartyType.Us,
            EpisodeReason.Return, At.AddHours(3), "test", Entity, Document: new DocumentRef("return_receipt", Guid.NewGuid())));

        int before;
        using (var beforeScope = fixture.ScopeFor(setup.Tenant))
        {
            before = await beforeScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Episodes
                .CountAsync(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership);
        }

        await InvokeAsync<DispositionRmaResult>(setup.Tenant, new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new(requested.LineIds[0], RmaDisposition.Refurbish)], new DateOnly(2026, 9, 1), At.AddHours(5)));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(before, await db.Episodes.CountAsync(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership));
        Assert.Equal(PartyType.Us, await db.Episodes.Where(e => e.UnitId == setup.Unit && e.Kind == EpisodeKind.Ownership && e.ValidTo == null)
            .Select(e => e.PartyType).SingleAsync());
    }

    [DatabaseFact]
    public async Task Closed_period_receipt_keeps_business_time_and_records_the_open_posting_period_and_publishes_events()
    {
        var setup = await SetUpReturnedUnitAsync("rma-period");
        await InvokeAsync<ConsumedEventResult>(setup.Tenant, new FinancePeriodClosed(new(Guid.NewGuid(), Entity, At, "finance"), "2026-09"));
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        var authorised = await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var received = await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var receipt = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ReturnReceipts.AsNoTracking().SingleAsync(r => r.RmaId == requested.RmaId);
            Assert.Equal((At.AddHours(3), "2026-10"), (receipt.BusinessTime, receipt.PostingPeriod));
        }
        var publishedAuthorised = Assert.Single(await PublishedEventSink.WaitForAsync<RmaAuthorised>(e => e.RmaId == requested.RmaId, 1, TimeSpan.FromSeconds(20)));
        var publishedReceived = Assert.Single(await PublishedEventSink.WaitForAsync<RmaReceived>(e => e.RmaId == requested.RmaId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((requested.RmaId, requested.LineIds[0], authorised.RmaNumber, setup.Unit, setup.Shipment,
                100m, ClaimCoverageSource.Warranty, (Guid?)setup.EntitlementId),
            (publishedAuthorised.RmaId, publishedAuthorised.RmaLineId, publishedAuthorised.RmaNumber,
                publishedAuthorised.AuthorisedUnitId, publishedAuthorised.OriginalSaleRef, publishedAuthorised.CoveragePct,
                publishedAuthorised.CoverageSource, publishedAuthorised.EntitlementId));
        Assert.NotEqual(Guid.Empty, publishedAuthorised.OwnershipEpisodeId);
        Assert.Equal((RmaAuthorised.Schema, RmaAuthorised.Version),
            (publishedAuthorised.Header.Schema, publishedAuthorised.Header.Version));
        Assert.Equal((requested.RmaId, requested.LineIds[0], received.Lines[0].ReturnReceiptId, setup.Unit, (Guid?)setup.Unit,
                (Guid?)null, ReturnOutcome.Accepted, ConditionGrades.Defective, "graded_value", "2026-10"),
            (publishedReceived.RmaId, publishedReceived.RmaLineId, publishedReceived.ReturnReceiptId,
                publishedReceived.AuthorisedUnitId, publishedReceived.ReceivedUnitId, publishedReceived.ProvisionalUnitId,
                publishedReceived.Outcome, publishedReceived.ConditionGrade, publishedReceived.ValuationRule, publishedReceived.PostingPeriod));
        Assert.Equal((RmaReceived.Schema, RmaReceived.Version),
            (publishedReceived.Header.Schema, publishedReceived.Header.Version));
    }

    [DatabaseFact]
    public async Task Case_79_child_return_stores_sale_allocation_closes_the_link_and_marks_parent_incomplete()
    {
        var setup = await SetUpReturnedUnitAsync("rma-kit", priceAllocation: 275m);
        var (parent, _) = await CommissionAsync(setup.Tenant, Guid.NewGuid(), "KIT-" + Guid.NewGuid().ToString("N")[..8], At.AddDays(-10));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.AssemblyLinks.Add(new AssemblyLink
            {
                TenantId = setup.Tenant, LinkId = Guid.NewGuid(), ParentUnitId = parent, ChildUnitId = setup.Unit,
                Relation = AssemblyRelation.Component, InstalledAt = At.AddDays(-9), InstallingEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()));
        await InvokeAsync<DispositionRmaResult>(setup.Tenant, new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new(requested.LineIds[0], RmaDisposition.ReturnToStock)], new DateOnly(2026, 9, 1), At.AddHours(5)));

        using var verify = fixture.ScopeFor(setup.Tenant);
        var db2 = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var line = await db2.RmaLines.AsNoTracking().SingleAsync(l => l.RmaId == requested.RmaId);
        Assert.Equal((parent, 275m, true), (line.ParentUnitId, line.PriceAllocation, line.ParentIncomplete));
        Assert.NotNull(await db2.AssemblyLinks.Where(l => l.ParentUnitId == parent && l.ChildUnitId == setup.Unit).Select(l => l.RemovedAt).SingleAsync());
        Assert.Equal(UnitDisposition.Incomplete, await db2.Current.Where(c => c.UnitId == parent).Select(c => c.Disposition).SingleAsync());
    }

    [DatabaseFact]
    public async Task Case_80_second_return_uses_the_current_owner_episode_and_latest_shipment()
    {
        var setup = await SetUpReturnedUnitAsync("rma-again");
        var first = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        var firstDetail = await DetailAsync(setup.Tenant, first.RmaId);
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), first.RmaId, At.AddHours(1)));
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(first.RmaId, first.LineIds[0], setup.Serial, Guid.NewGuid()));
        await InvokeAsync<DispositionRmaResult>(setup.Tenant, new DispositionRma(Guid.NewGuid(), first.RmaId,
            [new(first.LineIds[0], RmaDisposition.Credit)], new DateOnly(2026, 9, 1), At.AddHours(5)));

        var secondShipment = new DocumentRef("delivery", Guid.NewGuid(), 1);
        Documents.Add(new ShipmentDocument(secondShipment, Guid.NewGuid(), At.AddDays(2),
            [new DocumentLine(1, setup.Item, 1, [new DocumentSerial(setup.Serial)], PriceAllocation: 350m)], Guid.NewGuid()));
        await InvokeAsync<ConsumedEventResult>(setup.Tenant, new SalesShipmentPosted(new(Guid.NewGuid(), Entity, At.AddDays(2), "sales"), secondShipment));
        var secondDelivery = new DocumentRef("delivery_confirmation", Guid.NewGuid());
        Documents.Add(new DeliveryConfirmation(secondDelivery, secondShipment, At.AddDays(2).AddHours(1), Guid.NewGuid()));
        await InvokeAsync<ConsumedEventResult>(setup.Tenant, new SalesDeliveryConfirmed(new(Guid.NewGuid(), Entity, At.AddDays(2).AddHours(1), "carrier"), secondDelivery));
        var secondSetup = setup with { Shipment = secondShipment };
        var second = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(secondSetup, Guid.NewGuid(), At.AddDays(3)));
        var secondDetail = await DetailAsync(setup.Tenant, second.RmaId);
        Assert.NotEqual(firstDetail.Lines[0].OwnershipEpisodeId, secondDetail.Lines[0].OwnershipEpisodeId);
        Assert.Equal(setup.Shipment, firstDetail.Lines[0].OriginalSaleRef);
        Assert.Equal(secondShipment, secondDetail.Lines[0].OriginalSaleRef);
    }

    [DatabaseFact]
    public async Task Case_89_return_from_a_closed_account_is_accepted_against_the_recorded_owner_episode()
    {
        var setup = await SetUpReturnedUnitAsync("rma-closed-account");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, Request(setup, Guid.NewGuid()));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant, new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1)));
        var received = await InvokeAsync<ReceiveReturnResult>(setup.Tenant, Receive(requested.RmaId, requested.LineIds[0], setup.Serial, Guid.NewGuid()));
        Assert.Equal(ReturnOutcome.Accepted, Assert.Single(received.Lines).Outcome);
        var detail = await DetailAsync(setup.Tenant, requested.RmaId);
        Assert.NotEqual(Guid.Empty, detail.Lines[0].OwnershipEpisodeId);
        Assert.Equal(setup.Customer, detail.ReturningPartyRef);
    }

    [DatabaseFact]
    public async Task Proof_of_purchase_request_resolves_the_serial_and_list_supports_stage_age_summary_and_find()
    {
        var setup = await SetUpReturnedUnitAsync("rma-proof");
        var command = new RequestRma(Guid.NewGuid(), DateOnly.FromDateTime(At), setup.Customer, setup.Customer, Entity, Entity, "DE", "EUR",
            [new RmaRequestLineInput(ProofOfPurchase: setup.Shipment, ProofLine: setup.Shipment.Line, RequestedAmount: 100m)], At);
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant, command);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await db.Rmas.AnyAsync(row => row.RmaId == requested.RmaId));
        var queries = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Rmas.RmaQueries>();
        var unfiltered = await queries.ListAsync(new RmaListFilter(null, null, null, null), At.AddDays(10), 0, 25, CancellationToken.None);
        Assert.Contains(unfiltered.Rows, row => row.RmaId == requested.RmaId);
        var aged = await queries.ListAsync(new RmaListFilter(RmaStage.Requested, 5, 15, null), At.AddDays(10), 0, 25, CancellationToken.None);
        Assert.Contains(aged.Rows, row => row.RmaId == requested.RmaId);
        var list = await queries.ListAsync(new RmaListFilter(RmaStage.Requested, 5, 15, setup.Serial[..6]), At.AddDays(10), 0, 25, CancellationToken.None);
        Assert.Equal(requested.RmaId, Assert.Single(list.Rows).RmaId);
        Assert.Equal(1, list.Summary.Single(x => x.Stage == RmaStage.Requested).Count);
        var detail = (await queries.DetailAsync(requested.RmaId, CancellationToken.None))!;
        Assert.Equal((setup.Unit, setup.Shipment), (Assert.Single(detail.Lines).RequestedUnitId, detail.Lines[0].OriginalSaleRef));
    }

    private async Task<RmaDetail> DetailAsync(TenantId tenant, Guid rmaId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return (await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Rmas.RmaQueries>().DetailAsync(rmaId, CancellationToken.None))!;
    }

    private async Task WaitForStageAsync(TenantId tenant, Guid rmaId, string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var stage = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Rmas.AsNoTracking()
                .Where(row => row.RmaId == rmaId).Select(row => row.Stage).SingleAsync();
            if (stage == expected) return;
            await Task.Delay(100);
        }

        using var finalScope = fixture.ScopeFor(tenant);
        var actual = await finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Rmas.AsNoTracking()
            .Where(row => row.RmaId == rmaId).Select(row => row.Stage).SingleAsync();
        Assert.Fail($"RMA {rmaId} stayed at {actual}; expected {expected}.");
    }

    private async Task AssertRefusedAsync<T>(TenantId tenant, object command, string rule) where T : class
    {
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<T>(tenant, command));
        Assert.Equal(rule, refused.Rule);
    }

    private static RequestRma Request(Setup setup, Guid commandId, DateTime? at = null) => new(commandId,
        DateOnly.FromDateTime(at ?? At), setup.Customer, setup.Customer, Entity, Entity, "DE", "EUR",
        [new RmaRequestLineInput(Identifier: new DocumentSerial(setup.Serial), RequestedAmount: 100m)], at ?? At);

    private static ReceiveReturn Receive(Guid rmaId, Guid lineId, string serial, Guid commandId) => new(commandId, rmaId,
        Guid.NewGuid(), [new(lineId, new DocumentSerial(serial), ConditionGrades.Defective, "graded_value")],
        At.AddHours(2), At.AddHours(3), At.AddHours(3));

    private async Task<Setup> SetUpReturnedUnitAsync(string prefix, decimal? priceAllocation = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var policyCode = "RMA-" + Guid.NewGuid().ToString("N")[..8];
        var terms = PolicyValidatorTestsTerms.Standard24();
        var policy = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), policyCode, policyCode,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2025, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(policy.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        Warranties.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, policyCode, true));
        var serial = prefix.ToUpperInvariant()[..Math.Min(prefix.Length, 10)] + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (unit, _) = await CommissionAsync(tenant, item, serial, At.AddDays(-40));
        var customer = Guid.NewGuid();
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, CoverageFamilyCode.Base,
            new DateOnly(2026, 7, 1), Entity, At.AddDays(-35), "service.desk", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At.AddDays(-35) },
            OwnerPartyRef: customer, OwnerPartyType: PartyType.Customer, Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        var shipment = new DocumentRef("delivery", Guid.NewGuid(), 1);
        Documents.Add(new ShipmentDocument(shipment, customer, At.AddDays(-30),
            [new DocumentLine(1, item, 1, [new DocumentSerial(serial)], PriceAllocation: priceAllocation)], Guid.NewGuid()));
        var posted = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(new(Guid.NewGuid(), Entity, At.AddDays(-30), "sales"), shipment));
        Assert.Empty(posted.Exceptions);
        var delivery = new DocumentRef("delivery_confirmation", Guid.NewGuid());
        Documents.Add(new DeliveryConfirmation(delivery, shipment, At.AddDays(-29), Guid.NewGuid()));
        var delivered = await InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(new(Guid.NewGuid(), Entity, At.AddDays(-29), "carrier"), delivery));
        Assert.Empty(delivered.Exceptions);
        return new(tenant, item, unit, serial, customer, shipment, entitlement.EntitlementId);
    }

    private async Task<(Guid Unit, string Serial)> CommissionAsync(TenantId tenant, Guid item, string serial, DateTime at)
    {
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], at, "test", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return (Assert.Single(commissioned.Lines).UnitId!.Value, serial);
    }

    private sealed record Setup(TenantId Tenant, Guid Item, Guid Unit, string Serial, Guid Customer, DocumentRef Shipment, Guid EntitlementId);
}
