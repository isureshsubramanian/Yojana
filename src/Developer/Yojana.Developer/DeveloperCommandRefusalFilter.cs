using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts;

namespace Yojana.Developer;

/// <summary>Maps the Developer settings surface to the same coded refusal contract as other screen commands.</summary>
public sealed class DeveloperCommandRefusalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = exception.Rule,
                    ["rule"] = exception.Rule,
                });
        }
    }
}
