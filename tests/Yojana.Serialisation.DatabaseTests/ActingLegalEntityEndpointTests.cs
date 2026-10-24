using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Web;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

public sealed class ActingLegalEntityEndpointTests
{
    private static readonly Guid DefaultEntity = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ChosenEntity = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Receive_release_and_serial_range_choices_are_honoured()
    {
        var commission = await StampAsync("/api/units/commission", new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(),
            "purchased", [new CommissionLine([new IdentifierInput("internal_serial", "SN-1")])], At, string.Empty,
            ChosenEntity));
        var release = await StampAsync("/api/selections/selection/execute", new ExecuteBulkOperation(Guid.NewGuid(),
            Guid.NewGuid(), "release_units", Arguments(ChosenEntity), new BulkApproval(null)));
        var range = await StampAsync("/api/serial-ranges/", new AllocateSerialRange(Guid.NewGuid(), "default", 10,
            At, string.Empty, AllocatedTo: new DocumentRef("production_order", Guid.NewGuid()),
            LegalEntityId: ChosenEntity));

        Assert.Equal(ChosenEntity, commission.LegalEntityId);
        Assert.Equal(ChosenEntity, release.Arguments.LegalEntityId);
        Assert.Equal(ChosenEntity, range.LegalEntityId);
    }

    [Fact]
    public async Task Claim_service_and_obligor_choices_are_honoured()
    {
        var command = new SubmitClaim(Guid.NewGuid(), Guid.NewGuid(), null, DateOnly.FromDateTime(At), Guid.NewGuid(),
            Guid.NewGuid(), ChosenEntity, ChosenEntity, "GB", "GBP",
            [new ClaimLineInput("parts", "base_warranty", 10)], [], "repair", "choice", At, string.Empty);

        var stamped = await StampAsync("/api/claims/", command);

        Assert.Equal(ChosenEntity, stamped.ServiceEntityId);
        Assert.Equal(ChosenEntity, stamped.ObligorEntityId);
    }

    [Fact]
    public async Task Bulk_obligor_choice_is_honoured_while_the_acting_entity_defaults()
    {
        var arguments = Arguments(ChosenEntity) with
        {
            Extra = new Dictionary<string, string> { ["obligor_entity_id"] = ChosenEntity.ToString() },
        };
        var command = new PreviewBulkOperation(Guid.NewGuid(), "entitlement.create", arguments);

        var stamped = await StampAsync("/api/selections/selection/preview", command);

        Assert.Equal(DefaultEntity, stamped.Arguments.LegalEntityId);
        Assert.Equal(ChosenEntity.ToString(), stamped.Arguments.Extra!["obligor_entity_id"]);
    }

    [Fact]
    public async Task Resolver_refusals_use_the_standard_coded_problem_response()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/units/commission";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(YojanaClaim.UserId, Guid.NewGuid().ToString())], "test"));
        var command = new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), "purchased",
            [new CommissionLine([new IdentifierInput("internal_serial", "SN-1")])], At, string.Empty, Guid.Empty);
        var filter = new ScreenCommandLegalEntityFilter(new RefusingResolver());
        var nextCalled = false;

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http, [command]), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status409Conflict, problem.ProblemDetails.Status);
        Assert.Equal("legal_entity_unknown", problem.ProblemDetails.Title);
        Assert.Equal("legal_entity_unknown", problem.ProblemDetails.Extensions["rule"]);
    }

    private static BulkOperationArguments Arguments(Guid legalEntityId) => new(At, string.Empty, legalEntityId,
        new DocumentRef("production_order", Guid.NewGuid()));

    private static async Task<T> StampAsync<T>(string path, T command) where T : class
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = path;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(YojanaClaim.UserId, Guid.NewGuid().ToString())], "test"));
        var invocation = new DefaultEndpointFilterInvocationContext(http, [command]);
        var filter = new ScreenCommandLegalEntityFilter(new Resolver());
        object? stamped = null;
        await filter.InvokeAsync(invocation, context =>
        {
            stamped = context.Arguments[0];
            return ValueTask.FromResult(stamped);
        });
        return Assert.IsType<T>(stamped);
    }

    private sealed class Resolver : IActingLegalEntity
    {
        public Task<ActingLegalEntityOptions> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ActingLegalEntityOptions(DefaultEntity,
                [new(DefaultEntity, "LE-DEFAULT", "Fictional Default Works"),
                    new(ChosenEntity, "LE-CHOSEN", "Fictional Chosen Works")]));

        public Task<ActingLegalEntityResolution> ResolveAsync(Guid? requestedId, bool choiceAllowed,
            CancellationToken cancellationToken) => Task.FromResult(new ActingLegalEntityResolution(
                choiceAllowed && requestedId is not null ? requestedId : DefaultEntity));

        public Task<ActingLegalEntityResolution> SetDefaultAsync(Guid partyId,
            CancellationToken cancellationToken) => Task.FromResult(new ActingLegalEntityResolution(partyId));
    }

    private sealed class RefusingResolver : IActingLegalEntity
    {
        public Task<ActingLegalEntityOptions> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ActingLegalEntityOptions(null, []));

        public Task<ActingLegalEntityResolution> ResolveAsync(Guid? requestedId, bool choiceAllowed,
            CancellationToken cancellationToken) => Task.FromResult(new ActingLegalEntityResolution(null,
                "legal_entity_unknown", "The selected legal entity is not an active legal entity of this tenant."));

        public Task<ActingLegalEntityResolution> SetDefaultAsync(Guid partyId,
            CancellationToken cancellationToken) => ResolveAsync(partyId, true, cancellationToken);
    }
}
