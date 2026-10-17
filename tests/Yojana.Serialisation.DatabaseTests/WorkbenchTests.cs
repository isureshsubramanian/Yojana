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
