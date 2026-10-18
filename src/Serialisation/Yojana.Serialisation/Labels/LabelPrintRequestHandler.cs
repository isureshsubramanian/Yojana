using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

/// <summary>
/// Consumes the narrow print-request hand-offs emitted beside identifier swaps, ownership source corrections,
/// replacement shipment and replacement-link correction. Producers never know label tables or template selection.
/// </summary>
public static class LabelPrintRequestHandler
{
    public static async Task Handle(AssetTagPrintJobRequested request, LabelPrintService service, LabelSettings settings,
        CancellationToken cancellationToken)
    {
        var policy = await settings.GetAsync(cancellationToken);
        await service.PrintAsync(new PrintLabel(request.RequestId, request.UnitId, null, request.IdentifierId,
            policy.DefaultPrinterRef, request.RequestedAt, request.LegalEntityId, request.ActorRef, request.TriggeringEventId), cancellationToken);
    }

    public static async Task Handle(LabelReprintJobRequested request, LabelPrintService service, LabelSettings settings,
        CancellationToken cancellationToken)
    {
        var policy = await settings.GetAsync(cancellationToken);
        await service.PrintAsync(new PrintLabel(request.RequestId, request.UnitId, null, null,
            policy.DefaultPrinterRef, request.RequestedAt, request.LegalEntityId, request.ActorRef, request.TriggeringEventId), cancellationToken);
    }

    public static async Task Handle(ReplacementAssetTagPrintRequested request, LabelPrintService service, LabelSettings settings,
        CancellationToken cancellationToken)
    {
        var policy = await settings.GetAsync(cancellationToken);
        await service.PrintAsync(new PrintLabel(request.Header.EventId, request.Header.UnitId, null, request.IdentifierId,
            policy.DefaultPrinterRef, request.Header.EventTime, request.Header.LegalEntityId, request.Header.ActorRef,
            request.Header.EventId), cancellationToken);
    }
}
