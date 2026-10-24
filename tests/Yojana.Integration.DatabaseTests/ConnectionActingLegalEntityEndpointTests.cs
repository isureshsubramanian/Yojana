using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Yojana.Integration.Connections;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Web;
using Xunit;

namespace Yojana.Integration.DatabaseTests;

public sealed class ConnectionActingLegalEntityEndpointTests
{
    [Fact]
    public async Task Connection_registration_uses_the_server_default()
    {
        var expected = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/integration/connections";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(YojanaClaim.UserId, Guid.NewGuid().ToString())], "test"));
        var command = new RegisterConnection(Guid.NewGuid(), "CX01", "Fictional source", Guid.Empty,
            JsonDocument.Parse("{}").RootElement, []);
        var invocation = new DefaultEndpointFilterInvocationContext(http, [command]);
        var filter = new ScreenCommandLegalEntityFilter(new Resolver(expected));
        RegisterConnection? stamped = null;

        await filter.InvokeAsync(invocation, context =>
        {
            stamped = Assert.IsType<RegisterConnection>(context.Arguments[0]);
            return ValueTask.FromResult<object?>(stamped);
        });

        Assert.Equal(expected, stamped!.LegalEntityId);
    }

    private sealed class Resolver(Guid expected) : IActingLegalEntity
    {
        public Task<ActingLegalEntityOptions> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ActingLegalEntityOptions(expected,
                [new(expected, "LE-DEFAULT", "Fictional Default Works")]));

        public Task<ActingLegalEntityResolution> ResolveAsync(Guid? requestedId, bool choiceAllowed,
            CancellationToken cancellationToken) => Task.FromResult(new ActingLegalEntityResolution(expected));

        public Task<ActingLegalEntityResolution> SetDefaultAsync(Guid partyId,
            CancellationToken cancellationToken) => Task.FromResult(new ActingLegalEntityResolution(partyId));
    }
}
