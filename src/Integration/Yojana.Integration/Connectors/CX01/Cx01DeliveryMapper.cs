using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>DeliveryNotes requires CardCode and DocumentLines (item, quantity, warehouse). Serials use
/// DocumentLines.SerialNumbers.InternalSerialNumber and, when mapped, SystemSerialNumber. Returns link with BaseType 16.</summary>
internal static class Cx01DeliveryMapper
{
    public static Dictionary<string, object?> Map(CreateReplacementDelivery command, string customer,
        Cx01OutboundUnit unit, Cx01Warehouse location, Cx01DocumentSettings settings, string? baseReturn)
    {
        var line = Cx01DocumentBody.Line(unit);
        Cx01DocumentBody.Warehouse(line, location);
        if (string.IsNullOrWhiteSpace(command.ShipToRef) || command.ShipToRef == "default")
            throw new Cx01UnmappableException("missing ship-to address name in the module event");
        if (command.PricePolicy.Value is not ("zero" or "difference" or "full"))
            throw new Cx01UnmappableException("The replacement price policy is not supported.");
        if (command.PricePolicy.Value == "zero")
        {
            if (!settings.ZeroPriceReplacement)
                throw new Cx01UnmappableException("Zero-price replacement posting is disabled on this connection; enter the no-charge delivery by hand.");
            line["UnitPrice"] = 0m;
        }
        else
        {
            if (command.Price is null) throw new Cx01UnmappableException("missing replacement price in the module event");
            Cx01Money.Apply(line, "UnitPrice", command.Price, settings);
        }
        Cx01DocumentBody.Base(line, 16, baseReturn, command.BaseReturnLine);
        return new() { ["CardCode"] = customer, ["ShipToCode"] = command.ShipToRef, ["DocumentLines"] = new[] { line } };
    }
}
