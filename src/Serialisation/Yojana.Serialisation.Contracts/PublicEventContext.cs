namespace Yojana.Serialisation.Contracts;

/// <summary>Filter and reference facts exposed to the public-event translator without exposing Serialisation tables.</summary>
public sealed record PublicEventContext(string? ItemFamily, Guid? CurrentOwnerPartyRef, string? Serial);

public interface IPublicEventContextPort
{
    Task<PublicEventContext?> ForUnitAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>Returns the preferred current serial of each listed unit in one read; units without one are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> SerialsAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken);

    /// <summary>Returns a readable claim, RMA, replacement, certificate, campaign or import reference.</summary>
    Task<string?> ReferenceAsync(string kind, Guid recordId, CancellationToken cancellationToken);

    /// <summary>Returns unit membership already recorded by a bulk source such as an import.</summary>
    Task<IReadOnlyList<Guid>> OperationUnitsAsync(string kind, Guid operationId,
        CancellationToken cancellationToken);
}

/// <summary>One completed operation whose unit membership is carried once, separately from the public summary.</summary>
public sealed record BulkOperationCompleted(
    PublishedSummaryHeader Header,
    Guid OperationId,
    string Kind,
    int UnitCount,
    IReadOnlyList<Guid> UnitIds) : IPublishedSummaryEvent
{
    public const string Schema = "Bulk.OperationCompleted";
    public const string Version = "1.0";
}
