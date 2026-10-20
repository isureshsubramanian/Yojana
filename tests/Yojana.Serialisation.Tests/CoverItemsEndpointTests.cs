using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class CoverItemsEndpointTests
{
    [Fact]
    public async Task Edit_translates_an_exhausted_database_concurrency_race_to_a_typed_conflict()
    {
        var bus = DispatchProxy.Create<IMessageBus, ConcurrencyBus>();
        var result = await SerialisationEndpoints.InvokeEdit<PolicyCoverItemPage>(bus, new Tenant(),
            new UpdatePolicyCoverItems(Guid.NewGuid(), [], ExpectedVersion: "AQ=="), CancellationToken.None);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("document_changed", problem.ProblemDetails.Extensions["rule"]);
        Assert.Equal(DocumentVersionRules.Reload, problem.ProblemDetails.Extensions["unblockingStep"]);
    }

    public class ConcurrencyBus : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new DbUpdateConcurrencyException("competing edit");
    }

    private sealed class Tenant : ITenantContext
    {
        public bool HasTenant => true;
        public TenantId TenantId { get; } = new(Guid.NewGuid());
    }
}
