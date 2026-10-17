using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Corrections;

// TIGHT COUPLING (P3-T02 correction audit projection)
// WHAT: a correction_record is added in the same SerialisationDbContext transaction as every correction's ledger facts;
// handlers reach it only through this service. WHY: one correction may emit facts for many units but must count once,
// and a reversal link must be atomic with its fact. WHAT BREAKS IF SEPARATED: KPI counts drift or correction-of links
// dangle after a partial failure. Call Record once per command and Attributes on every fact; do not write the table directly.

/// <summary>Validates common correction metadata, writes its KPI projection and renders it onto every ledger fact.</summary>
public sealed class CorrectionLedger(SerialisationDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<CorrectionMetadata> PrepareAsync(
        string command,
        CorrectionMetadata? supplied,
        Guid commandId,
        string legacyReason,
        string? legacyNote,
        string? legacyApproverRef,
        DateTime eventTime,
        string defaultInitiatingRole,
        CancellationToken cancellationToken)
    {
        var approvers = (supplied?.Approvers ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(legacyApproverRef) && approvers.All(a => a.UserRef != legacyApproverRef)) approvers.Add(new(legacyApproverRef, []));
        var metadata = supplied is null
            ? new CorrectionMetadata(commandId, legacyReason, legacyNote, [], CorrectionInitiatedVia.Api, null, defaultInitiatingRole, approvers)
            : supplied with
            {
                Note = supplied.Note ?? legacyNote,
                EvidenceRefs = supplied.EvidenceRefs ?? [],
                InitiatingRole = supplied.InitiatingRole ?? (supplied.InitiatedVia is CorrectionInitiatedVia.System or CorrectionInitiatedVia.Migration ? CorrectionInitiatingRole.System : defaultInitiatingRole),
                Approvers = approvers,
            };

        if (metadata.CorrectionId == Guid.Empty) throw new CommandRefusedException("correction_id_required", "A correction id is required.");
        if (!string.Equals(metadata.ReasonCode, legacyReason, StringComparison.Ordinal)) throw new CommandRefusedException("reason_mismatch", "The command reason and correction metadata reason must be the same.");
        CorrectionRules.ValidateReason(command, metadata.ReasonCode);
        if (!CorrectionInitiatedVia.All.Contains(metadata.InitiatedVia)) throw new CommandRefusedException("initiated_via_unknown", $"'{metadata.InitiatedVia}' is not one of {string.Join(", ", CorrectionInitiatedVia.All)}.");
        if (metadata.InitiatingRole is null || !CorrectionInitiatingRole.All.Contains(metadata.InitiatingRole)) throw new CommandRefusedException("initiating_role_unknown", $"'{metadata.InitiatingRole}' is not a correction initiating role.");
        if (metadata.EvidenceRefs!.Any(string.IsNullOrWhiteSpace)) throw new CommandRefusedException("evidence_ref_blank", "Evidence references cannot be blank.");
        if (metadata.Approvers!.Any(a => string.IsNullOrWhiteSpace(a.UserRef))) throw new CommandRefusedException("approver_ref_blank", "Approver references cannot be blank.");
        if (await db.Corrections.AsNoTracking().AnyAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == metadata.CorrectionId, cancellationToken))
            throw new CommandRefusedException("correction_id_in_use", $"Correction id {metadata.CorrectionId} has already been used.", "send a new correction id, or replay the original command idempotently");
        if (metadata.CorrectsCorrectionId is { } priorId)
        {
            var prior = await db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == priorId, cancellationToken)
                ?? throw new CommandRefusedException("corrected_correction_unknown", $"Correction {priorId} does not exist.", "select an earlier correction from this tenant's history");
            if (CorrectionRules.LinkProblem(metadata.CorrectionId, prior.CorrectionId) is { } refusal) throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
        }

        return metadata;
    }

    public void Record(string command, CorrectionMetadata metadata, string actorRef, DateTime eventTime, int unitCount)
    {
        db.Corrections.Add(new CorrectionRecord
        {
            TenantId = tenant.TenantId,
            CorrectionId = metadata.CorrectionId,
            Command = command,
            ReasonCode = metadata.ReasonCode,
            RootCauseCategory = CorrectionRules.RootCause(command, metadata.ReasonCode),
            InitiatedVia = metadata.InitiatedVia,
            InitiatingRole = metadata.InitiatingRole!,
            ActorRef = actorRef,
            EventTime = eventTime,
            RecordedTime = clock.UtcNow,
            CorrectsCorrectionId = metadata.CorrectsCorrectionId,
            UnitCount = unitCount,
            Note = metadata.Note,
            EvidenceRefs = JsonSerializer.Serialize(metadata.EvidenceRefs, LedgerJson.Options),
            Approvers = JsonSerializer.Serialize(metadata.Approvers, LedgerJson.Options),
        });
    }

    /// <summary>Merges common metadata into command-specific attributes, keeping it flat and identical on every fact.</summary>
    public static JsonObjectText Attributes(CorrectionMetadata metadata, object commandSpecific)
    {
        var root = JsonSerializer.SerializeToNode(commandSpecific, LedgerJson.Options)!.AsObject();
        root["correction_id"] = metadata.CorrectionId;
        root["reason_code"] = metadata.ReasonCode;
        root["note"] = metadata.Note;
        root["evidence_refs"] = JsonSerializer.SerializeToNode(metadata.EvidenceRefs, LedgerJson.Options);
        root["initiated_via"] = metadata.InitiatedVia;
        root["corrects_correction_id"] = metadata.CorrectsCorrectionId;
        root["initiating_role"] = metadata.InitiatingRole;
        root["approvers"] = JsonSerializer.SerializeToNode(metadata.Approvers, LedgerJson.Options);
        return JsonObjectText.Parse(root.ToJsonString(LedgerJson.Options));
    }
}

/// <summary>Pure reason, root-cause and reversal rules shared by commands and tests.</summary>
public static class CorrectionRules
{
    public static IReadOnlyList<string> Reasons(string command) => command switch
    {
        CorrectionCommand.CorrectIdentifier => IdentifierCorrectionReason.All,
        CorrectionCommand.SwapIdentifiers => SwapIdentifiersReason.All,
        CorrectionCommand.CorrectItem => CorrectItemReason.All,
        CorrectionCommand.CorrectAsBuilt => CorrectAsBuiltReason.All,
        CorrectionCommand.LateSerialization => LateSerializationReason.All,
        CorrectionCommand.VoidUnit => VoidUnitReason.All,
        CorrectionCommand.MergeUnits => MergeUnitsReason.All,
        CorrectionCommand.AssignIdentifiers => AssignIdentifiersReason.All,
        CorrectionCommand.ReassignAllocation => ReassignAllocationReason.All,
        CorrectionCommand.CorrectDocumentSerials => CorrectDocumentSerialsReason.All,
        CorrectionCommand.CorrectSourceDocument => CorrectSourceDocumentReason.All,
        _ => [],
    };

    public static void ValidateReason(string command, string reason)
    {
        var allowed = Reasons(command);
        if (allowed.Count == 0) throw new CommandRefusedException("correction_command_unknown", $"'{command}' is not a correction command.");
        if (!allowed.Contains(reason)) throw new CommandRefusedException("reason_unknown", $"Reason '{reason}' is not one of {string.Join(", ", allowed)}.");
    }

    /// <summary>
    /// A correction may reference any correction already recorded, with no time limit. Order is by recording, not by
    /// effective time: CorrectAsBuilt back-dates effective_at, so its reversal can legitimately carry an earlier event time.
    /// </summary>
    public static CommandRefusal? LinkProblem(Guid correctionId, Guid priorId) =>
        correctionId == priorId ? new("correction_cannot_correct_itself", "A correction cannot correct itself.", "select an earlier correction") : null;

    public static int RootCause(string command, string reason) => command switch
    {
        CorrectionCommand.CorrectDocumentSerials => CorrectionRootCause.DocumentSerials,
        CorrectionCommand.CorrectIdentifier when reason == IdentifierCorrectionReason.Relabel => CorrectionRootCause.RefurbishmentReserialisation,
        CorrectionCommand.CorrectIdentifier or CorrectionCommand.SwapIdentifiers => CorrectionRootCause.TypoOrCrossedPair,
        CorrectionCommand.CorrectItem or CorrectionCommand.CorrectAsBuilt or CorrectionCommand.CorrectSourceDocument => CorrectionRootCause.WrongItemOrOrder,
        CorrectionCommand.LateSerialization or CorrectionCommand.ReassignAllocation or CorrectionCommand.VoidUnit or CorrectionCommand.AssignIdentifiers => CorrectionRootCause.AllocationBeforeBirth,
        CorrectionCommand.MergeUnits => CorrectionRootCause.TypoOrCrossedPair,
        _ => throw new CommandRefusedException("root_cause_unknown", $"Correction command '{command}' has no KPI root-cause mapping."),
    };
}
