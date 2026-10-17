using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Refurbishments;

public static class OpenRefurbishmentHandler
{
    public static Task<OpenRefurbishmentResult> Handle(OpenRefurbishment command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.OpenAsync(command, cancellationToken);
}

public static class OpenReworkHandler
{
    public static Task<OpenReworkResult> Handle(OpenRework command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.OpenReworkAsync(command, cancellationToken);
}

public static class AddRefurbishmentOperationHandler
{
    public static Task<RefurbishmentOperationResult> Handle(AddRefurbishmentOperation command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.AddOperationAsync(command, cancellationToken);
}

public static class CompleteRefurbishmentOperationHandler
{
    public static Task<RefurbishmentOperationResult> Handle(CompleteRefurbishmentOperation command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.CompleteOperationAsync(command, cancellationToken);
}

public static class RemoveRefurbishmentPartHandler
{
    public static Task<RefurbishmentPartResult> Handle(RemoveRefurbishmentPart command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.RemovePartAsync(command, cancellationToken);
}

public static class InstallRefurbishmentPartHandler
{
    public static Task<RefurbishmentPartResult> Handle(InstallRefurbishmentPart command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.InstallPartAsync(command, cancellationToken);
}

public static class RecordRefurbishmentLabourHandler
{
    public static Task<RefurbishmentLabourResult> Handle(RecordRefurbishmentLabour command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.RecordLabourAsync(command, cancellationToken);
}

public static class RecordRefurbishmentCostHandler
{
    public static Task<RefurbishmentCostResult> Handle(RecordRefurbishmentCost command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.RecordCostAsync(command, cancellationToken);
}

public static class ReleaseRefurbishmentHandler
{
    public static Task<ReleaseRefurbishmentResult> Handle(ReleaseRefurbishment command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.ReleaseAsync(command, cancellationToken);
}

public static class CancelRefurbishmentHandler
{
    public static Task<CancelRefurbishmentResult> Handle(CancelRefurbishment command, RefurbishmentService service, CancellationToken cancellationToken) =>
        service.CancelAsync(command, cancellationToken);
}

public sealed class DefaultRefurbishmentOpener(RefurbishmentService service) : IRefurbishmentOpener
{
    public async Task<RefurbishmentOpenReceipt> OpenAsync(RefurbishmentOpenRequest request, CancellationToken cancellationToken)
    {
        var result = await service.OpenAsync(new OpenRefurbishment(request.CommandId, request.TargetUnitId,
            RefurbishmentKind.Refurbish, request.OpenedAt, request.ActorRef, request.LegalEntityId,
            request.RepairLocationId, request.SourceRmaLineId, request.SourceDocument), cancellationToken);
        return new(result.WorkOrderId, result.WorkOrderNumber, result.Replayed);
    }
}
