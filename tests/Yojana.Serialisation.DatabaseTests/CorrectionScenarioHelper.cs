using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>Shared arrange and workbench-flow helpers for the correction acceptance scenarios.</summary>
internal sealed class CorrectionScenarioHelper(SerialisationHostFixture fixture, Guid legalEntityId, DateTime at)
{
    public async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    public async Task<(Guid UnitId, Guid ItemId, string Serial)> CommissionAsync(TenantId tenant, string prefix,
        string origin = UnitOrigin.Manufactured, DocumentRef? document = null, Guid? location = null, Guid? item = null,
        string identifierType = IdentifierType.InternalSerial)
    {
        var itemId = item ?? Guid.NewGuid();
        var serial = prefix + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId, origin,
            [new CommissionLine([new IdentifierInput(identifierType, serial)])], at, "scenario", legalEntityId,
            Document: document, LocationId: location, LocationKind: location is null ? null : LocationKind.Warehouse));
        return (Assert.Single(result.Lines).UnitId!.Value, itemId, serial);
    }

    public async Task<Guid> SelectAsync(TenantId tenant, IReadOnlyList<string> serials)
    {
        var selectionId = Guid.NewGuid();
        await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(selectionId, SelectionSource.ByPastedList(serials), "scenario"));
        return selectionId;
    }

    public async Task<(T Result, CorrectionPreview Preview, WorkbenchRequestRow Request)> CommitWorkbenchAsync<T>(TenantId tenant,
        WorkbenchPreviewRequest request, WorkbenchActor initiator, WorkbenchActor committer,
        params WorkbenchActor[] approvers) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var displayed = await service.PreviewAsync(request, CancellationToken.None);
        var raised = await service.InitiateAsync(Guid.NewGuid(), request, initiator, CancellationToken.None);
        Assert.Equal(displayed.PreviewHash, raised.PreviewHash);
        Assert.True(raised.Status == WorkbenchRequestStatus.AwaitingApproval, $"Request is {raised.Status}: {raised.Preview.Refusal?.Rule} {raised.Preview.Refusal?.Message}");
        foreach (var approver in approvers)
        {
            raised = await service.ApproveAsync(raised.RequestId, approver, "scenario approval", CancellationToken.None);
            if (raised.Status == WorkbenchRequestStatus.Approved) break;
        }
        Assert.Equal(WorkbenchRequestStatus.Approved, raised.Status);
        var committed = await service.CommitAsync(raised.RequestId, committer, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Committed, committed.Status);
        if (committed.Result is not { } resultJson)
            throw new InvalidOperationException("The committed workbench request has no result.");
        var result = resultJson.Deserialize<T>(LedgerJson.Options)
            ?? throw new InvalidOperationException("The committed workbench request has no readable result.");
        return (result, displayed.Preview, committed);
    }

    public static WorkbenchPreviewRequest WorkbenchRequest<T>(Guid correctionId, string choice, string command,
        WorkbenchTarget target, string reason, T arguments, string route = "route") where T : class =>
        new(correctionId, choice, new(command, JsonSerializer.SerializeToElement(arguments, LedgerJson.Options),
                new Dictionary<string, string> { ["route"] = route }),
            target, reason, "workbench acceptance scenario", [new("reference", $"scenario:{correctionId:N}", "reviewed")]);
}
