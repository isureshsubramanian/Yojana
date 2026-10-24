using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;
using SerialisationCommandRefusedException = Yojana.Serialisation.Contracts.CommandRefusedException;

namespace Yojana.Web;

/// <summary>
/// The one HTTP seam that replaces browser-supplied legal entities before screen commands reach module handlers.
/// Integration ingress does not carry a signed-in user id and therefore remains connection-owned.
/// </summary>
public sealed class ScreenCommandLegalEntityFilter(IActingLegalEntity legalEntities) : IEndpointFilter
{
    // Tight coupling: Program maps the Serialisation and Integration endpoint groups through this filter, and this
    // boundary recognises their public entity member names. Renaming one without this set would leave it unstamped.
    private static readonly HashSet<string> EntityProperties = new(StringComparer.Ordinal)
    {
        "LegalEntityId", "ServiceEntityId", "ObligorEntityId",
    };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method)
            || context.HttpContext.User.FindFirst(YojanaClaim.UserId) is null)
            return await next(context);

        try
        {
            for (var index = 0; index < context.Arguments.Count; index++)
            {
                var argument = context.Arguments[index];
                if (argument is null) continue;
                context.Arguments[index] = await StampAsync(argument, context.HttpContext.Request.Path,
                    context.HttpContext.RequestAborted);
            }
            return await next(context);
        }
        catch (CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = exception.Rule,
                    ["unblockingStep"] = exception.UnblockingStep,
                });
        }
    }

    private async Task<object> StampAsync(object argument, PathString path, CancellationToken cancellationToken)
    {
        argument = argument switch
        {
            PreviewBulkOperation command => command with
            {
                Arguments = await StampBulkAsync(command.Operation, command.Arguments, cancellationToken),
            },
            ExecuteBulkOperation command => command with
            {
                Arguments = await StampBulkAsync(command.Operation, command.Arguments, cancellationToken),
            },
            RequestBulkApproval command => command with
            {
                Arguments = await StampBulkAsync(command.Operation, command.Arguments, cancellationToken),
            },
            WorkbenchPreviewRequest request => await StampWorkbenchAsync(request, cancellationToken),
            _ => argument,
        };

        foreach (var property in argument.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.PropertyType == typeof(Guid)
                         && EntityProperties.Contains(property.Name) && property.SetMethod is not null))
        {
            var requested = (Guid)property.GetValue(argument)!;
            var choiceAllowed = ChoiceAllowed(path, argument, property.Name);
            var resolved = await ResolveAsync(requested, choiceAllowed, cancellationToken);
            property.SetValue(argument, resolved);
        }
        return argument;
    }

    private async Task<BulkOperationArguments> StampBulkAsync(string operation, BulkOperationArguments arguments,
        CancellationToken cancellationToken)
    {
        var acting = await ResolveAsync(arguments.LegalEntityId,
            string.Equals(operation, "release_units", StringComparison.Ordinal), cancellationToken);
        var extra = arguments.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(arguments.Extra, StringComparer.Ordinal);
        if (extra.TryGetValue("obligor_entity_id", out var value))
        {
            var requested = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
            extra["obligor_entity_id"] = (await ResolveAsync(requested, choiceAllowed: true,
                cancellationToken)).ToString();
        }
        return arguments with { LegalEntityId = acting, Extra = extra.Count == 0 ? null : extra };
    }

    private async Task<WorkbenchPreviewRequest> StampWorkbenchAsync(WorkbenchPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var node = JsonNode.Parse(request.Answers.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
        var requested = ReadGuid(node, "legal_entity_id") ?? ReadGuid(node, "legalEntityId") ?? Guid.Empty;
        node.Remove("legalEntityId");
        node["legal_entity_id"] = (await ResolveAsync(requested, choiceAllowed: false,
            cancellationToken)).ToString();
        return request with
        {
            Answers = request.Answers with { Arguments = JsonSerializer.SerializeToElement(node) },
        };
    }

    private async Task<Guid> ResolveAsync(Guid requestedId, bool choiceAllowed,
        CancellationToken cancellationToken)
    {
        var result = await legalEntities.ResolveAsync(requestedId == Guid.Empty ? null : requestedId,
            choiceAllowed, cancellationToken);
        if (!result.Accepted)
            throw new SerialisationCommandRefusedException(result.RefusalRule!, result.RefusalMessage!,
                result.RefusalRule == "legal_entity_not_configured"
                    ? "choose a default legal entity in Company settings" : null);
        return result.LegalEntityId!.Value;
    }

    private static bool ChoiceAllowed(PathString path, object argument, string propertyName)
    {
        if (argument is SubmitClaim && propertyName is "ServiceEntityId" or "ObligorEntityId") return true;
        if (propertyName != "LegalEntityId") return false;
        return path.Equals("/api/units/commission", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/units/release", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/serial-ranges", StringComparison.OrdinalIgnoreCase);
    }

    private static Guid? ReadGuid(JsonObject node, string propertyName) =>
        node[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var parsed) ? parsed : null;
}
