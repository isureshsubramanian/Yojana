using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>No Service module is registered in P5, so there are no service orders to re-adjudicate yet.</summary>
public sealed class DefaultServiceOrderQueryPort : IServiceOrderQueryPort
{
    public Task<IReadOnlyList<OpenServiceOrderCoverage>> OpenForUnitAsync(Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenServiceOrderCoverage>>([]);

    public Task UpdateCoverageDecisionReferenceAsync(Guid serviceOrderId, Guid? serviceOrderLineId, Guid coverageDecisionId,
        CoverageDecisionSnapshot decision, CancellationToken cancellationToken) => Task.CompletedTask;
}
