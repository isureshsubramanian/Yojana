namespace Yojana.Serialisation.Contracts;

/// <summary>How a pasted vendor list is matched to the production order's provisional-only units.</summary>
public static class IdentifierAssignmentMode
{
    public const string AllocationOrder = "allocation_order";
    public const string ExplicitPairs = "explicit_pairs";
    public static readonly IReadOnlyList<string> All = [AllocationOrder, ExplicitPairs];
}

/// <summary>One pasted line; <see cref="UnitId"/> is present only for <c>explicit_pairs</c>.</summary>
public sealed record IdentifierAssignmentInput(string Value, Guid? UnitId = null);

/// <summary>
/// Apply a pasted identifier list to the provisional-only units created for one production order (ADR-0001 case 135).
/// The item's serial scheme decides the identifier type and validates the value. A correction that names an earlier
/// AssignIdentifiers correction and supplies no lines reverses that assignment back to provisional identifiers.
/// </summary>
public sealed record AssignIdentifiers(
    Guid CommandId,
    DocumentRef ProductionOrder,
    string Mode,
    IReadOnlyList<IdentifierAssignmentInput> Lines,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    Guid? IssuerPartyRef = null,
    string? Qualifier = null,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionApprover? CommittedBy = null);

public static class IdentifierAssignmentOutcome
{
    public const string Assign = "assign";
    public const string Assigned = "assigned";
    public const string Rejected = "rejected";
    public const string Reversed = "reversed";
}

/// <summary>Per pasted line result; collisions name the existing holder and never modify it.</summary>
public sealed record IdentifierAssignmentLineResult(
    int Index,
    Guid? RequestedUnitId,
    Guid? UnitId,
    string RawValue,
    string? NormalisedValue,
    string Outcome,
    string? Reason = null,
    Guid? ConflictingUnitId = null,
    Guid? IdentifierId = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record AssignIdentifiersPreview(
    CorrectionPreview Preview,
    IReadOnlyList<IdentifierAssignmentLineResult> Lines,
    int RemainingCount,
    IReadOnlyList<Guid> AwaitingVendorSerialUnitIds,
    bool Reversal);

public sealed record PreviewAssignIdentifiers(AssignIdentifiers Command);

public sealed record AssignIdentifiersResult(
    Guid CorrectionId,
    int Assigned,
    IReadOnlyList<IdentifierAssignmentLineResult> Lines,
    int RemainingCount,
    IReadOnlyList<Guid> AwaitingVendorSerialUnitIds,
    bool Replayed = false,
    bool Reversal = false);

/// <summary>One provisional-only unit shown by the production-order awaiting-vendor-serial report.</summary>
public sealed record AwaitingVendorSerialUnit(Guid UnitId, Guid ItemId, string ProvisionalIdentifier, DateTime AllocatedAt);

public sealed record AwaitingVendorSerialReport(DocumentRef ProductionOrder, int Count, IReadOnlyList<AwaitingVendorSerialUnit> Units);
