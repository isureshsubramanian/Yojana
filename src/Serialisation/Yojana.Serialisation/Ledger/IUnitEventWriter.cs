using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// The only way facts enter the ledger (MOD-04 contract). Appends run inside the caller's unit of work: the
/// writer adds rows to the module's DbContext and applies the current-state projection; the caller's
/// <c>SaveChanges</c> (the Wolverine transaction, or the command's own) commits both or neither.
/// </summary>
public interface IUnitEventWriter
{
    /// <summary>
    /// Records the drafts. Rows whose idempotency key already exists at the same business time are skipped and
    /// reported as such; the others receive a <c>seq</c> from the ledger sequence in (event time, recorded time)
    /// order per unit. Throws <see cref="OutOfOrderApplyException"/> when a draft would apply behind the unit's
    /// <c>last_event_seq</c>.
    /// </summary>
    Task<AppendResult> AppendAsync(IReadOnlyList<UnitEventDraft> drafts, CancellationToken cancellationToken);
}

public sealed record AppendResult(IReadOnlyList<UnitEvent> Written, IReadOnlyList<UnitEventDraft> Duplicates)
{
    public long? LastSeq => Written.Count == 0 ? null : Written.Max(e => e.Seq);
}

/// <summary>Coupling 2 guard: an apply that would go behind <c>unit_current.last_event_seq</c> is refused, never reordered.</summary>
public sealed class OutOfOrderApplyException(Guid unitId, long lastSeq, long attemptedSeq)
    : InvalidOperationException($"Unit {unitId}: current state is at seq {lastSeq}; refusing to apply seq {attemptedSeq}.")
{
    public Guid UnitId { get; } = unitId;

    public long LastSeq { get; } = lastSeq;

    public long AttemptedSeq { get; } = attemptedSeq;
}

/// <summary>A draft that creates current state for a unit that already has a row, or changes one that has none.</summary>
public sealed class ProjectionStateException(string message) : InvalidOperationException(message);
