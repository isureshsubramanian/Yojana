using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX02;

public sealed class Cx02OutboundCompleteness(
    Cx02Settings settings,
    ICx02OutboundFacts facts,
    ICx02AttachmentContent content) : IOutboundCompletenessCheck
{
    public async Task<IReadOnlyList<string>> MissingAsync(Guid connectionId, string outboundMode,
        IOutboundCommand command, CancellationToken ct)
    {
        if (outboundMode != "documents") return [];
        var configured = await settings.GetAsync(connectionId, ct);
        var options = configured.Outbound ?? new();
        var missing = new List<string>();
        async Task<Cx02OutboundUnit?> Unit(UnitReference reference)
        {
            if (reference.UnitId == Guid.Empty || string.IsNullOrWhiteSpace(reference.IdentifierValue)) return null;
            return await facts.UnitAsync(connectionId, reference.UnitId, reference.IdentifierValue,
                ct);
        }
        try
        {
            switch (command)
            {
                case CreateReplacementDelivery delivery:
                    var replacement = await Unit(delivery.ReplacementUnit);
                    if (replacement is null) missing.Add("replacement_unit");
                    else Cx02TrackedDocumentMapper.Replacement(delivery,
                        await RequiredReferenceAsync("party", delivery.CustomerRef), replacement, options);
                    break;
                case CreateCreditMemo credit:
                    var units = new List<Cx02OutboundUnit?>();
                    foreach (var line in credit.Lines)
                        units.Add(line.UnitId is { } id
                            ? await facts.UnitAsync(connectionId, id,
                                line.Serials is { Count: > 0 } ? line.Serials[0].Value : null, ct) : null);
                    Cx02CreditMapper.Map(credit, await RequiredReferenceAsync("party", credit.CustomerRef), units,
                        options);
                    break;
                case PostWarrantyExpense expense:
                    Cx02JournalMapper.Map(expense, options);
                    break;
                case ChangeStockStatusOrLocation stock:
                    var stockUnit = await Unit(stock.Unit);
                    if (stockUnit is null) missing.Add("unit");
                    else Cx02TrackedDocumentMapper.Stock(stock, stockUnit, options);
                    break;
                case CreateServiceOrder service:
                    var serviceUnit = await Unit(service.Unit);
                    if (serviceUnit is null) missing.Add("unit");
                    else Cx02TrackedDocumentMapper.Service(service,
                        await RequiredReferenceAsync("party", service.CustomerRef), serviceUnit);
                    break;
                case AttachDocument attachment:
                    content.BeginAttempt();
                    if (string.IsNullOrWhiteSpace(attachment.ContentRef)) missing.Add("content_ref");
                    else
                    {
                        var file = await content.ReadAsync(attachment.ContentRef, options.AttachmentSizeLimit,
                            ct);
                        if (file is null) missing.Add("content_ref");
                    }
                    _ = Cx02AttachmentMapper.Target(await facts.ReferenceAsync(connectionId, "document",
                        attachment.TargetRecordRef, ct) ?? attachment.TargetRecordRef);
                    _ = Cx02AttachmentMapper.FileName(attachment.ErpReferenceText, attachment.DisplayName, options);
                    break;
            }
        }
        catch (OutboundAttachmentTooLargeException exception) { missing.Add("content_ref (" + exception.Message + ")"); }
        catch (Cx02OutboundRuleException exception) { missing.Add(exception.Message); }
        return missing.Distinct(StringComparer.Ordinal).ToList();

        async Task<string> RequiredReferenceAsync(string kind, string reference) =>
            await facts.ReferenceAsync(connectionId, kind, reference, ct)
            ?? throw new Cx02OutboundRuleException($"Map the {kind} reference before posting.");
    }
}
