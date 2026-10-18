using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

public sealed class ClaimLedger(SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant)
{
    public static string Key(Guid commandId, Guid identity, string eventType, DateTime at) =>
        IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, commandId, identity, eventType, at);

    public Task<UnitEvent?> ReplayedAsync(string key, DateTime at, CancellationToken cancellationToken) =>
        db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.IdempotencyKey == key && e.EventTime == at, cancellationToken);

    public async Task<Claim> LoadAsync(Guid claimId, CancellationToken cancellationToken) =>
        await db.Claims.FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.ClaimId == claimId, cancellationToken)
        ?? throw new CommandRefusedException("claim_unknown", $"Claim {claimId} does not exist.");

    public static void Transition(Claim claim, string eventType)
    {
        var next = ClaimStateMachine.Next(claim.Status, eventType)
            ?? throw new CommandRefusedException("claim_transition_not_allowed", $"Claim {claim.ClaimId} is {claim.Status}; {eventType} is not allowed from there.",
                claim.Status == ClaimStatus.Closed ? "a closed claim cannot be reopened" : null);
        claim.Status = next;
    }

    public Task<AppendResult> AppendAsync(IReadOnlyList<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        // Claim and recovery facts are audit history, not current unit state. Keeping them off unit_current means
        // concurrent intake on one product cannot race an ownership, custody or entitlement rowversion update.
        return writer.AppendAsync(drafts.Select(draft => draft with { ProjectCurrentState = false }).ToList(), cancellationToken);
    }

    public static Guid EventId(DateTime at) => TimeOrderedId.New(at);
}

