using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class WorkbenchTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly WorkbenchActor Technician = new("technician", [YojanaRole.Technician]);
    private static readonly WorkbenchActor SerialAdministrator = new("serial-admin", [YojanaRole.SerialAdministrator]);
    private static readonly WorkbenchActor ProductionSupervisor = new("production-supervisor", [YojanaRole.ProductionSupervisor]);

    [DatabaseFact]
    public async Task Reword_and_reorder_persist_per_tenant_without_leaking()
    {
        var tenant = await fixture.NewTenantAsync("wbchoice");
        var otherTenant = await fixture.NewTenantAsync("wbchoiceother");
        using (var scope = fixture.ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<WorkbenchChoiceSettings>();
            var current = await settings.GetConfigurationAsync(CancellationToken.None);
            var choices = current.Choices.Select(c => c.Choice == CorrectionWorkbenchChoice.WrongItem
                ? c with { Label = "The recorded product is wrong", Order = 15, SearchWords = ["catalogue mismatch"] } : c).ToList();
            var saved = await settings.SetAsync(new(choices, 12m), "application-admin", CancellationToken.None);
            Assert.Equal(1, saved.Version);
            Assert.Equal("application-admin", saved.UpdatedBy);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var view = await scope.ServiceProvider.GetRequiredService<WorkbenchChoiceSettings>().GetViewAsync(CancellationToken.None);
            Assert.Equal("The recorded product is wrong", view.Current.Choices.Single(c => c.Choice == CorrectionWorkbenchChoice.WrongItem).Label);
            Assert.Equal(15, view.Current.Choices.Single(c => c.Choice == CorrectionWorkbenchChoice.WrongItem).Order);
            Assert.NotEmpty(view.History);
            var picker = await scope.ServiceProvider.GetRequiredService<WorkbenchPickerService>().QueryAsync(null, "catalogue", CancellationToken.None);
            Assert.Equal(CorrectionWorkbenchChoice.WrongItem, Assert.Single(picker.Groups.SelectMany(g => g.Choices)).Choice);
            Assert.True(picker.Fallback.Fallback);
        }

        using (var scope = fixture.ScopeFor(otherTenant))
        {
            var other = await scope.ServiceProvider.GetRequiredService<WorkbenchChoiceSettings>().GetConfigurationAsync(CancellationToken.None);
            Assert.Equal("The serial is on the wrong item", other.Choices.Single(c => c.Choice == CorrectionWorkbenchChoice.WrongItem).Label);
            Assert.Equal(10m, other.UnclassifiedAlertPercent);
        }
    }

    [DatabaseFact]
    public async Task Added_choice_routes_to_its_existing_command()
    {
        var tenant = await fixture.NewTenantAsync("wbadded");
        var prefix = "WA" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var unit = (await CommissionAsync(tenant, prefix, 1)).Single();
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<WorkbenchChoiceSettings>();
        var current = await settings.GetConfigurationAsync(CancellationToken.None);
        var added = new WorkbenchChoice("unfinished_order_unit", "The order ended before this unit was completed", "Item, build and allocation",
            "Void an unfinished unit through the existing audited command.", [CorrectionCommand.VoidUnit], [], VoidUnitReason.WorkOrderQuantityReduced,
            CorrectionRootCause.AllocationBeforeBirth, ["unfinished", "cancelled order"], 15, [YojanaRole.Technician],
            [YojanaRole.ProductionSupervisor], Standard: false);
        await settings.SetAsync(new([.. current.Choices, added], current.UnclassifiedAlertPercent), "application-admin", CancellationToken.None);

        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var command = new VoidUnit(Guid.Empty, unit, VoidUnitReason.WorkOrderQuantityReduced, At.AddHours(1), "ignored", Entity,
            new DocumentRef("production_order", Guid.NewGuid()));
        var request = Request(Guid.NewGuid(), added.Choice, CorrectionCommand.VoidUnit, unit, VoidUnitReason.WorkOrderQuantityReduced, command);
        var raised = await service.InitiateAsync(Guid.NewGuid(), request, Technician, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.AwaitingApproval, raised.Status);
        Assert.Equal(WorkbenchRequestStatus.Approved, (await service.ApproveAsync(raised.RequestId, ProductionSupervisor, null, CancellationToken.None)).Status);
        Assert.Equal(WorkbenchRequestStatus.Committed, (await service.CommitAsync(raised.RequestId, ProductionSupervisor, CancellationToken.None)).Status);
        Assert.Equal(CorrectionCommand.VoidUnit, (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Corrections
            .SingleAsync(c => c.CorrectionId == request.CommandId)).Command);
    }

    [DatabaseFact]
    public async Task Unclassified_request_refiles_under_the_same_request_and_kpi_counts_the_command_once()
    {
        var tenant = await fixture.NewTenantAsync("wbrefile");
        var prefix = "WU" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var unit = (await CommissionAsync(tenant, prefix, 1)).Single();
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var requestId = Guid.NewGuid();
        var filed = await service.FileUnclassifiedAsync(requestId,
            new(new(WorkbenchTargetKind.Unit, UnitId: unit), "The plate has a character that does not match the record", [new("photo", "evidence:plate")]),
            Technician, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Unclassified, filed.Status);
        Assert.Equal(unit, Assert.Single(filed.Preview.Units).UnitId);

        var commandId = Guid.NewGuid();
        var command = new CorrectIdentifier(Guid.Empty, unit, IdentifierType.InternalSerial, $"{prefix}-101",
            IdentifierCorrectionReason.Typo, At.AddHours(2), "ignored", Entity);
        var classified = await service.RefileAsync(requestId, new(Request(commandId, CorrectionWorkbenchChoice.MistypedOrCrossed,
            CorrectionCommand.CorrectIdentifier, unit, IdentifierCorrectionReason.Typo, command)), SerialAdministrator, CancellationToken.None);
        Assert.Equal(requestId, classified.RequestId);
        Assert.Equal(WorkbenchRequestStatus.Approved, classified.Status);
        Assert.Equal(WorkbenchRequestStatus.Committed, (await service.CommitAsync(requestId, SerialAdministrator, CancellationToken.None)).Status);

        var report = await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.CorrectionKpiQueries>().QueryAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CorrectionKpiBreakdown.RootCause, null, CancellationToken.None);
        Assert.Equal(1, report.Rows.Single(r => r.Dimension == CorrectionRootCause.TypoOrCrossedPair.ToString()).Corrections);
        Assert.DoesNotContain(report.Rows, r => r.Dimension == "unclassified");
        var requestProjection = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Corrections.SingleAsync(c => c.CorrectionId == requestId);
        Assert.Equal(CorrectionRequestStatus.Refiled, requestProjection.RequestStatus);
    }

    [DatabaseFact]
    public async Task Unclassified_request_can_escalate_and_triggers_the_tenant_alert()
    {
        var tenant = await fixture.NewTenantAsync("wbescalate");
        var prefix = "WE" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var unit = (await CommissionAsync(tenant, prefix, 1)).Single();
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<WorkbenchChoiceSettings>();
        var configuration = await settings.GetConfigurationAsync(CancellationToken.None);
        await settings.SetAsync(new(configuration.Choices, 80m), "application-admin", CancellationToken.None);
        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var requestId = Guid.NewGuid();
        await service.FileUnclassifiedAsync(requestId,
            new(new(WorkbenchTargetKind.Unit, UnitId: unit), "No listed command describes the physical state"), Technician, CancellationToken.None);
        var escalated = await service.EscalateAsync(requestId, SerialAdministrator, "Product change assessment required", CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.NoCommandFits, escalated.Status);

        var report = await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.CorrectionKpiQueries>().QueryAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CorrectionKpiBreakdown.RootCause, null, CancellationToken.None);
        Assert.Equal(1, report.Rows.Single(r => r.Dimension == "unclassified").Corrections);
        Assert.Equal(80m, report.UnclassifiedAlert!.ThresholdPercent);
        Assert.Equal(100m, report.UnclassifiedAlert.RatePercent);
        Assert.True(report.UnclassifiedAlert.Alert);
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Guid>> CommissionAsync(TenantId tenant, string prefix, int count)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Manufactured,
            Enumerable.Range(1, count).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-{i:000}")])).ToList(),
            At, "line", Entity));
        return result.Lines.Select(l => l.UnitId!.Value).ToList();
    }

    [DatabaseFact]
    public async Task Entry_resolution_uses_identifier_history_aliases_asset_tags_licences_and_selection_sets()
    {
        var tenant = await fixture.NewTenantAsync("wbres");
        var prefix = "WR" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var unitIds = await CommissionAsync(tenant, prefix, 2);
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unitIds[0],
            new IdentifierInput(IdentifierType.CustomerAssetTag, $"ASSET-{prefix}"), At.AddMinutes(1), "line", Entity));
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unitIds[0],
            new IdentifierInput(IdentifierType.SoftwareLicense, $"LIC-{prefix}"), At.AddMinutes(2), "line", Entity));
        await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unitIds[0], IdentifierType.InternalSerial,
            $"{prefix}-101", IdentifierCorrectionReason.Typo, At.AddMinutes(3), "line", Entity));
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([$"{prefix}-101"]), "line"));

        using var scope = fixture.ScopeFor(tenant);
        var resolver = scope.ServiceProvider.GetRequiredService<WorkbenchEntryResolver>();
        foreach (var value in new[] { $"{prefix}-001", $"ASSET-{prefix}", $"LIC-{prefix}" })
        {
            var resolved = await resolver.ResolveAsync(new(Guid.NewGuid(), new(WorkbenchTargetKind.Identifier, Value: value), "line"), CancellationToken.None);
            Assert.Equal(unitIds[0], Assert.Single(resolved.Candidates).ResolvedUnitId);
        }
        var selected = await resolver.ResolveAsync(new(Guid.NewGuid(), new(WorkbenchTargetKind.SelectionSet, SelectionId: selection.SelectionId), "line"), CancellationToken.None);
        Assert.Equal(unitIds[0], Assert.Single(selected.Candidates).ResolvedUnitId);

        var mergeId = Guid.NewGuid();
        await InvokeAsync<MergeUnitsResult>(tenant, new MergeUnits(mergeId, unitIds[0], unitIds[1], MergeUnitsReason.DuplicateRecord,
            At.AddMinutes(10), "serial-committer", Entity, Correction: new(mergeId, MergeUnitsReason.DuplicateRecord,
                Approvers: [new("serial-approver", [YojanaRole.SerialAdministrator])]),
            CommittedBy: new("serial-committer", [YojanaRole.SerialAdministrator])));
        var merged = await resolver.ResolveAsync(new(Guid.NewGuid(), new(WorkbenchTargetKind.Identifier, Value: $"{prefix}-002"), "line"), CancellationToken.None);
        var candidate = Assert.Single(merged.Candidates);
        Assert.True(candidate.MergedAlias);
        Assert.Equal(unitIds[0], candidate.ResolvedUnitId);
    }

    [DatabaseFact]
    public async Task Swap_and_void_follow_initiate_approve_commit_and_requests_are_tenant_isolated()
    {
        var tenant = await fixture.NewTenantAsync("wbflow");
        var otherTenant = await fixture.NewTenantAsync("wbother");
        var prefix = "WF" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var units = await CommissionAsync(tenant, prefix, 3);

        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var queries = scope.ServiceProvider.GetRequiredService<WorkbenchRequestQueries>();
        var swap = new SwapIdentifiers(Guid.Empty, units[0], units[1], IdentifierType.InternalSerial,
            SwapIdentifiersReason.CrossRecordedAtBuild, At.AddHours(1), "ignored", Entity);
        var swapRequest = Request(Guid.NewGuid(), CorrectionWorkbenchChoice.MistypedOrCrossed, CorrectionCommand.SwapIdentifiers,
            units[0], SwapIdentifiersReason.CrossRecordedAtBuild, swap);
        var raisedSwap = await service.InitiateAsync(Guid.NewGuid(), swapRequest, Technician, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.AwaitingApproval, raisedSwap.Status);
        var approvedSwap = await service.ApproveAsync(raisedSwap.RequestId, SerialAdministrator, "checked labels", CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Approved, approvedSwap.Status);
        // Approved but not committed: waiting in the inbox of whoever may commit it, not of the initiator who may not.
        Assert.Contains((await queries.InboxAsync(SerialAdministrator.Roles, 0, 50, CancellationToken.None)).Items,
            r => r.RequestId == raisedSwap.RequestId && r.Status == WorkbenchRequestStatus.Approved);
        Assert.DoesNotContain((await queries.InboxAsync(Technician.Roles, 0, 50, CancellationToken.None)).Items,
            r => r.RequestId == raisedSwap.RequestId);
        await AssertRuleAsync("approval_required", () => service.CommitAsync(raisedSwap.RequestId, Technician, CancellationToken.None));
        var committedSwap = await service.CommitAsync(raisedSwap.RequestId, SerialAdministrator, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Committed, committedSwap.Status);
        var swapCorrection = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Corrections.AsNoTracking()
            .SingleAsync(c => c.CorrectionId == swapRequest.CommandId);
        Assert.Equal(CorrectionInitiatedVia.Workbench, swapCorrection.InitiatedVia);
        Assert.Contains("test:evidence", swapCorrection.EvidenceRefs);
        Assert.Contains("serial-admin", swapCorrection.Approvers);

        var voidCommand = new VoidUnit(Guid.Empty, units[2], VoidUnitReason.WorkOrderQuantityReduced, At.AddHours(2), "ignored", Entity,
            new DocumentRef("production_order", Guid.NewGuid()));
        var voidRequest = Request(Guid.NewGuid(), CorrectionWorkbenchChoice.WrongAllocation, CorrectionCommand.VoidUnit,
            units[2], VoidUnitReason.WorkOrderQuantityReduced, voidCommand);
        var raisedVoid = await service.InitiateAsync(Guid.NewGuid(), voidRequest, Technician, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.AwaitingApproval, raisedVoid.Status);
        Assert.Equal(WorkbenchRequestStatus.Approved,
            (await service.ApproveAsync(raisedVoid.RequestId, ProductionSupervisor, null, CancellationToken.None)).Status);
        Assert.Equal(WorkbenchRequestStatus.Committed,
            (await service.CommitAsync(raisedVoid.RequestId, ProductionSupervisor, CancellationToken.None)).Status);

        Assert.NotNull(await queries.GetAsync(raisedSwap.RequestId, CancellationToken.None));
        using var otherScope = fixture.ScopeFor(otherTenant);
        Assert.Null(await otherScope.ServiceProvider.GetRequiredService<WorkbenchRequestQueries>().GetAsync(raisedSwap.RequestId, CancellationToken.None));
        Assert.Empty(await otherScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().WorkbenchRequests.AsNoTracking().ToListAsync());
    }

    [DatabaseFact]
    public async Task Approval_refuses_when_the_identical_preview_changed()
    {
        var tenant = await fixture.NewTenantAsync("wbstale");
        var prefix = "WS" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var units = await CommissionAsync(tenant, prefix, 2);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var command = new VoidUnit(Guid.Empty, units[0], VoidUnitReason.WorkOrderQuantityReduced, At.AddHours(1), "ignored", Entity,
            new DocumentRef("production_order", Guid.NewGuid()));
        var request = Request(Guid.NewGuid(), CorrectionWorkbenchChoice.WrongAllocation, CorrectionCommand.VoidUnit,
            units[0], VoidUnitReason.WorkOrderQuantityReduced, command);
        var raised = await service.InitiateAsync(Guid.NewGuid(), request, Technician, CancellationToken.None);

        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), units[0],
            [new ChildInput(units[1], Relation: AssemblyRelation.Component)], At.AddMinutes(30), "line", Entity));
        await AssertRuleAsync("preview_changed", () => service.ApproveAsync(raised.RequestId, ProductionSupervisor, null, CancellationToken.None));
        Assert.Equal(WorkbenchRequestStatus.Superseded,
            (await scope.ServiceProvider.GetRequiredService<WorkbenchRequestQueries>().GetAsync(raised.RequestId, CancellationToken.None))!.Status);
    }

    private static WorkbenchPreviewRequest Request<T>(Guid commandId, string choice, string command, Guid unitId, string reason, T arguments) where T : class =>
        new(commandId, choice, new(command, JsonSerializer.SerializeToElement(arguments, LedgerJson.Options)),
            new(WorkbenchTargetKind.Unit, UnitId: unitId), reason, "workbench test", [new("reference", "test:evidence", "reviewed")]);

    private static async Task AssertRuleAsync(string expected, Func<Task> action)
    {
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(action);
        Assert.Equal(expected, refusal.Rule);
    }
}
