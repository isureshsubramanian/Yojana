using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims.Risk;

public static class DecideClaimRiskFlagHandler
{
    public static async Task<ClaimRiskFlagDecisionResult> Handle(DecideClaimRiskFlag command,
        SerialisationDbContext db, ClaimRiskRulesSettings settings, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (command.Decision is not (ClaimRiskFlagStatus.Cleared or ClaimRiskFlagStatus.Confirmed))
            throw new CommandRefusedException("claim_risk_decision_invalid", "A risk flag decision is 'cleared' or 'confirmed'.");
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new CommandRefusedException("claim_risk_decision_reason_required", "A risk flag decision needs a reason.");
        var reason = command.Reason.Trim();
        if (reason.Length > 1000)
            throw new CommandRefusedException("claim_risk_decision_reason_too_long", "A risk flag decision reason is at most 1,000 characters.");

        var tenantId = tenant.TenantId;
        var data = await (from flag in db.ClaimRiskFlags
            join claim in db.Claims on new { flag.TenantId, flag.ClaimId } equals new { claim.TenantId, claim.ClaimId }
            where flag.TenantId == tenantId && flag.FlagId == command.FlagId && flag.ClaimId == command.ClaimId
            select new { Flag = flag, Claim = claim }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("claim_risk_flag_unknown", "The claim risk flag does not exist.");
        if (data.Flag.Status != ClaimRiskFlagStatus.Open)
            throw new CommandRefusedException("claim_risk_flag_already_decided", "A decided claim risk flag cannot be decided again.");

        var policy = await settings.GetAsync(cancellationToken);
        if (!command.ActorRoles.Contains(policy.ReviewerRole, StringComparer.Ordinal))
            throw new CommandRefusedException("claim_risk_reviewer_required",
                $"The reviewer must hold the tenant's '{policy.ReviewerRole}' risk-review role.",
                $"route the flag to a {policy.ReviewerRole} reviewer");
        if (policy.RequireDifferentReviewer && command.ActorRef == data.Claim.SubmittedBy)
            throw new CommandRefusedException("claim_risk_different_reviewer_required",
                "The user who submitted the claim cannot perform its risk review.",
                "route the flag to another risk reviewer");

        data.Flag.Status = command.Decision;
        data.Flag.DecidedAt = command.DecidedAt;
        data.Flag.DecidedBy = command.ActorRef;
        data.Flag.DecisionReason = reason;
        return new(data.Flag.FlagId, data.Flag.ClaimId, data.Claim.ClaimNumber, data.Flag.RuleCode,
            data.Flag.Status, command.DecidedAt, reason);
    }
}
