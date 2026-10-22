namespace Yojana.Platform.Contracts.Records;

/// <summary>Stable record kinds understood by the cross-module question boundary.</summary>
public static class RecordKind
{
    public const string Unit = "unit";
    public const string Claim = "claim";
    public const string Rma = "rma";
    public const string RefurbishmentWorkOrder = "refurbishment_work_order";
    public const string WarrantyPolicy = "warranty_policy";
    public const string Certificate = "certificate";
    public const string ImportJob = "import_job";
    public const string IntegrationMessage = "integration_message";

    public static readonly IReadOnlyList<string> All =
    [
        Unit, Claim, Rma, RefurbishmentWorkOrder, WarrantyPolicy, Certificate, ImportJob,
        IntegrationMessage,
    ];
}

/// <summary>The signed-in staff identity whose existing module read permissions are being checked.</summary>
public sealed record RecordReferenceViewer(Guid UserId, IReadOnlyList<string> Roles);

/// <summary>A record lookup never exposes a raw id as its display name.</summary>
public sealed record RecordReference(Guid RecordId, bool Exists, string? DisplayName, bool MayView);

/// <summary>
/// Keyed by <see cref="RecordKind"/>. Each owning module resolves a batch from its own tables, so callers never
/// depend on another module's persistence model.
/// </summary>
public interface IRecordReferenceResolver
{
    public const int MaxRecordIds = 500;

    Task<IReadOnlyDictionary<Guid, RecordReference>> ResolveAsync(
        IReadOnlyCollection<Guid> recordIds,
        RecordReferenceViewer viewer,
        CancellationToken cancellationToken);
}
