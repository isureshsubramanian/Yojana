using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>Returns requires CardCode and DocumentLines with quantity, warehouse and SerialNumbers carrying
/// InternalSerialNumber/SystemSerialNumber. The mapped RMA field remains an annotation in documents mode.</summary>
internal static class Cx01ReturnMapper
{
    public static Dictionary<string, object?> Map(CreateServiceOrder command, string customer, Cx01OutboundUnit unit,
        Cx01Warehouse location, Cx01ConnectionSettings settings)
    {
        var line = Cx01DocumentBody.Line(unit);
        Cx01DocumentBody.Warehouse(line, location);
        var body = new Dictionary<string, object?> { ["CardCode"] = customer, ["DocumentLines"] = new[] { line } };
        var mappedField = Cx01FieldMap.Effective(settings.FieldMap)[Cx01FieldMap.ReturnRmaReference].Field;
        var field = settings.FieldMap?.ContainsKey(Cx01FieldMap.ReturnRmaReference) == true
            ? mappedField : settings.RmaReferenceField ?? mappedField;
        if (field is not null) body[field] = settings.OwnReferencePrefix + command.ErpReferenceText;
        return body;
    }
}
