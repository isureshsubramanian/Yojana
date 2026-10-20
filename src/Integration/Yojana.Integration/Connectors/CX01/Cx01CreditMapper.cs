using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>CreditNotes requires CardCode and DocumentLines. Invoice links use BaseType 13, BaseEntry, BaseLine;
/// returned serial-managed lines carry InternalSerialNumber/SystemSerialNumber in SerialNumbers. Canonical Amount
/// is the line total, so quantity never multiplies the credited amount.</summary>
internal static class Cx01CreditMapper
{
    public static Dictionary<string, object?> Map(CreateCreditMemo command, string customer,
        IReadOnlyList<Cx01OutboundUnit?> units, Cx01Warehouse location, string? invoice, Cx01DocumentSettings settings)
    {
        if (command.RestockingFee is { Amount: not 0 })
            throw new Cx01UnmappableException("A restocking fee needs a configured charge-line mapping; enter this credit by hand.");
        if (command.Lines.Count == 0) throw new Cx01UnmappableException("The credit has no lines.");
        if (command.Lines.Select(x => x.Amount.Currency).Distinct().Count() != 1)
            throw new Cx01DocumentRuleException("A document must use one currency.");
        var lines = command.Lines.Select((source, i) =>
        {
            if (source.Quantity <= 0) throw new Cx01DocumentRuleException("Document quantities must be positive.");
            if (source.Amount.Amount == 0 && (string.IsNullOrWhiteSpace(source.Amount.Currency) || source.Amount.Currency == "XXX"))
                throw new Cx01UnmappableException("missing credit amount and currency in the module event");
            Dictionary<string, object?> line;
            if (units[i] is { } unit) line = Cx01DocumentBody.Line(unit, source.Quantity);
            else
            {
                if (source.ItemExternalId is null) throw new Cx01UnmappableException("The credit line has no mapped item or unit.");
                line = new() { ["ItemCode"] = source.ItemExternalId, ["Quantity"] = source.Quantity };
                if (source.Serials is { Count: > 0 })
                {
                    if (source.Serials.Count != source.Quantity) throw new Cx01UnmappableException("The credit serial count must equal its quantity.");
                    line["SerialNumbers"] = source.Serials.Select(x => ((Dictionary<string, object?>[])Cx01DocumentBody.Line(
                        new(source.ItemExternalId, x.Value, x.ErpSerialId))["SerialNumbers"]!)[0]).ToArray();
                }
            }
            Cx01DocumentBody.Warehouse(line, location);
            line["ItemDescription"] = source.Description;
            Cx01Money.Apply(line, "LineTotal", source.Amount, settings);
            Cx01DocumentBody.Base(line, 13, invoice, source.BaseLine);
            return line;
        }).ToArray();
        return new() { ["CardCode"] = customer, ["DocumentLines"] = lines };
    }
}
