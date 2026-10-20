using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>ServiceCalls requires CustomerCode, Subject and mapped ItemCode/InternalSerialNum. Service calls identify
/// the installed serial directly; they do not allocate stock through DocumentLines.</summary>
internal static class Cx01ServiceMapper
{
    public static Dictionary<string, object?> Map(CreateServiceOrder command, string customer, Cx01OutboundUnit unit) => new()
    {
        ["CustomerCode"] = customer, ["Subject"] = command.ProblemDescription,
        ["ItemCode"] = unit.ItemCode, ["InternalSerialNum"] = unit.Serial,
    };
}
