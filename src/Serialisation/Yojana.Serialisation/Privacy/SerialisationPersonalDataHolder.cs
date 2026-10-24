using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.Privacy;

public sealed class SerialisationPersonalDataHolder(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ImportFileStore importFiles) : IPersonalDataHolder
{
    private const int BatchSize = 500;
    private const string Redacted = "[Personal data redacted]";
    public string Store => "serialisation";

    public async Task<PersonalDataPreview> PreviewAsync(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.Party) return Empty();
        await RefuseOpenObligationsAsync(subject.SubjectId, cancellationToken);
        var mail = await db.CustomerMailMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.CustomerPartyRef == subject.SubjectId).ToListAsync(cancellationToken);
        var portal = await db.PortalRegistrations.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.PartyRef == subject.SubjectId && x.AttachmentContent != null).CountAsync(cancellationToken);
        var values = mail.SelectMany(x => new[] { x.CustomerName, x.ToAddress, x.ToName })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Concat(subject.KnownValues)
            .Distinct(StringComparer.Ordinal).ToList();
        var certificates = await CountCertificateRowsAsync(subject.SubjectId, values, cancellationToken);
        var ledger = await CountLedgerPartyCodesAsync(subject.SubjectId, values, cancellationToken);
        var imports = await db.ImportRowResults.AsNoTracking().CountAsync(x => x.TenantId == tenant.TenantId
            && x.CollisionOwnerPartyRef == subject.SubjectId, cancellationToken);
        var review = await ReviewItemsAsync(subject.SubjectId, cancellationToken);
        return new([
            new("customer_mail", mail.Count, "copied recipient and bodies replaced"),
            new("portal_attachments", portal, "attachment bytes purged"),
            new("unit_event_party_code", ledger, "party_code redacted in place under ADR-0017"),
            new("certificates", certificates, "owner snapshot redacted; original fingerprint retained"),
            new("imports", imports, "referenced file copies purged and raw values tokenised"),
            new("free_text", review.Count, "administrator choice applied after exact-value replacement"),
        ], review, values);
    }

    public async Task<PersonalDataEraseResult> EraseAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.Party) return new([]);
        await RefuseOpenObligationsAsync(subject.SubjectId, cancellationToken);
        var mail = await db.CustomerMailMessages.Where(x => x.TenantId == tenant.TenantId
            && x.CustomerPartyRef == subject.SubjectId).ToListAsync(cancellationToken);
        foreach (var row in mail)
        {
            row.CustomerName = subject.Token;
            row.ToAddress = subject.Email;
            row.ToName = subject.Token;
            row.Cc = "[]";
            row.Subject = ReplaceExact(row.Subject, subject.KnownValues, subject.Token)!;
            row.HtmlBody = Redacted;
            row.TextBody = Redacted;
            row.AboutLabel = ReplaceExact(row.AboutLabel, subject.KnownValues, subject.Token)!;
        }
        var portal = await db.PortalRegistrations.Where(x => x.TenantId == tenant.TenantId
            && x.PartyRef == subject.SubjectId && x.AttachmentContent != null).ToListAsync(cancellationToken);
        foreach (var row in portal)
        {
            row.AttachmentContent = null;
            row.AttachmentFileName = null;
            row.AttachmentContentType = null;
        }
        await ApplyFreeTextAsync(subject, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var ledger = await RedactPartyCodeForAdr0017Async(subject, cancellationToken);
        var certificates = await RedactCertificatesAsync(subject, erasureId, cancellationToken);
        var imports = await PurgeImportsAsync(subject, cancellationToken);
        return new([
            new("customer_mail", mail.Count, "copied recipient and bodies replaced"),
            new("portal_attachments", portal.Count, "attachment bytes purged"),
            new("unit_event_party_code", ledger, "party_code redacted in place under ADR-0017"),
            new("certificates", certificates, "owner snapshot redacted; original fingerprint retained"),
            new("imports", imports, "referenced file copies purged and raw values tokenised"),
            new("free_text", subject.ReviewDecisions.Count(x => SerialisationReviewItem(x.ItemKey)),
                "administrator choice applied after exact-value replacement"),
        ]);
    }

    private static bool SerialisationReviewItem(string itemKey) =>
        itemKey.StartsWith("claim:", StringComparison.Ordinal)
        || itemKey.StartsWith("claim-line:", StringComparison.Ordinal)
        || itemKey.StartsWith("claim-evidence:", StringComparison.Ordinal)
        || itemKey.StartsWith("rma-line:", StringComparison.Ordinal)
        || itemKey.StartsWith("return-receipt:", StringComparison.Ordinal)
        || itemKey.StartsWith("serial-range:", StringComparison.Ordinal)
        || itemKey.StartsWith("workbench:", StringComparison.Ordinal);

    private async Task RefuseOpenObligationsAsync(Guid partyRef, CancellationToken cancellationToken)
    {
        var openClaims = await db.Claims.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && (x.ClaimantPartyRef == partyRef || x.SettlementPartyRef == partyRef)
            && x.Status != ClaimStatus.Denied && x.Status != ClaimStatus.Settled && x.Status != ClaimStatus.Closed)
            .OrderBy(x => x.SubmittedAt).Select(x => new PersonalDataErasureBlocker(
                "claim", x.ClaimId, x.ClaimNumber, x.Status, "Open claim")).ToListAsync(cancellationToken);
        var openRmas = await db.Rmas.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && (x.ReturningPartyRef == partyRef || x.SettlementPartyRef == partyRef)
            && x.Stage != RmaStage.Closed).OrderBy(x => x.RequestedAt)
            .Select(x => new PersonalDataErasureBlocker("rma", x.RmaId,
                x.RmaNumber ?? "RMA", x.Stage, "Open RMA")).ToListAsync(cancellationToken);
        var holds = await db.PartyReplacementHolds.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.PartyRef == partyRef && x.LiftedAt == null).OrderBy(x => x.SetAt)
            .Select(x => new PersonalDataErasureBlocker("replacement_hold", null,
                "Replacement hold", "open", x.Reason)).ToListAsync(cancellationToken);
        var unreturned = await db.ReplacementLinks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.CounterpartyPartyRef == partyRef && x.State == ReplacementLinkState.AwaitingReturn)
            .OrderBy(x => x.CreatedAt).Select(x => new PersonalDataErasureBlocker(
                "replacement", x.WarrantyReplacementId, x.SourceNumber, x.State,
                "Replacement unit shipped; original unit not returned")).ToListAsync(cancellationToken);
        var blockers = openClaims.Concat(openRmas).Concat(holds).Concat(unreturned).ToList();
        if (blockers.Count > 0)
            throw new PersonalDataErasureRefusedException("erasure_open_obligations",
                "Personal data cannot be erased while the party has an open claim, RMA, replacement hold or unreturned replacement.",
                "close or settle the open obligation first", blockers);
    }

    private async Task<List<PersonalDataReviewItem>> ReviewItemsAsync(Guid partyRef,
        CancellationToken cancellationToken)
    {
        var items = new List<PersonalDataReviewItem>();
        var claims = await db.Claims.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && (x.ClaimantPartyRef == partyRef || x.SettlementPartyRef == partyRef))
            .Select(x => new { x.ClaimId, x.ClaimNumber, x.ReportedDescription }).ToListAsync(cancellationToken);
        foreach (var row in claims.Where(x => !string.IsNullOrWhiteSpace(x.ReportedDescription)))
            items.Add(Item($"claim:{row.ClaimId:N}:reported_description", "claim", row.ClaimNumber,
                "reportedDescription", row.ReportedDescription));
        var claimIds = claims.Select(x => x.ClaimId).ToList();
        var lines = await db.ClaimLines.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && claimIds.Contains(x.ClaimId) && x.Description != null)
            .Select(x => new { x.ClaimLineId, x.ClaimId, x.Description }).ToListAsync(cancellationToken);
        foreach (var row in lines)
            items.Add(Item($"claim-line:{row.ClaimLineId:N}:description", "claim", row.ClaimId.ToString("N"),
                "line.description", row.Description));
        var evidence = await db.ClaimEvidence.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && claimIds.Contains(x.ClaimId) && x.Description != null)
            .Select(x => new { x.EvidenceId, x.ClaimId, x.Description }).ToListAsync(cancellationToken);
        foreach (var row in evidence)
            items.Add(Item($"claim-evidence:{row.EvidenceId:N}:description", "claim",
                row.ClaimId.ToString("N"), "evidence.description", row.Description));
        var rmas = await db.Rmas.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && (x.ReturningPartyRef == partyRef || x.SettlementPartyRef == partyRef))
            .Select(x => new { x.RmaId, x.RmaNumber }).ToListAsync(cancellationToken);
        var rmaIds = rmas.Select(x => x.RmaId).ToList();
        var rmaLines = await db.RmaLines.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && rmaIds.Contains(x.RmaId) && x.Description != null)
            .Select(x => new { x.RmaLineId, x.RmaId, x.Description }).ToListAsync(cancellationToken);
        foreach (var row in rmaLines)
            items.Add(Item($"rma-line:{row.RmaLineId:N}:description", "rma", row.RmaId.ToString("N"),
                "line.description", row.Description));
        var receipts = await db.ReturnReceipts.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && rmaIds.Contains(x.RmaId) && x.ExceptionNote != null)
            .Select(x => new { x.ReturnReceiptId, x.RmaId, x.ExceptionNote }).ToListAsync(cancellationToken);
        foreach (var row in receipts)
            items.Add(Item($"return-receipt:{row.ReturnReceiptId:N}:exception_note", "rma",
                row.RmaId.ToString("N"), "receipt.exceptionNote", row.ExceptionNote));
        var ranges = await db.SerialRanges.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.AllocatedToPartyRef == partyRef && x.Note != null)
            .Select(x => new { x.RangeId, x.Note }).ToListAsync(cancellationToken);
        foreach (var row in ranges)
            items.Add(Item($"serial-range:{row.RangeId:N}:note", "serial_range", row.RangeId.ToString("N"),
                "note", row.Note));
        var subject = partyRef.ToString("D");
        var workbench = await db.WorkbenchRequests.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.TargetJson.Contains(subject))
            .Select(x => new { x.RequestId, x.Note, x.DecisionNote, x.UnclassifiedDescription, x.AnswersJson })
            .ToListAsync(cancellationToken);
        foreach (var row in workbench)
        {
            Add(row.Note, "note"); Add(row.DecisionNote, "decision_note");
            Add(row.UnclassifiedDescription, "unclassified_description");
            Add(row.AnswersJson, "answers_json");
            void Add(string? value, string field)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    items.Add(Item($"workbench:{row.RequestId:N}:{field}", "workbench",
                        row.RequestId.ToString("N"), field, value));
            }
        }
        return items;
    }

    private async Task ApplyFreeTextAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        var choices = subject.ReviewDecisions.ToDictionary(x => x.ItemKey, x => x.Action, StringComparer.Ordinal);
        var claims = await db.Claims.Where(x => x.TenantId == tenant.TenantId
            && (x.ClaimantPartyRef == subject.SubjectId || x.SettlementPartyRef == subject.SubjectId)).ToListAsync(cancellationToken);
        foreach (var row in claims)
            row.ReportedDescription = Text($"claim:{row.ClaimId:N}:reported_description", row.ReportedDescription);
        var claimIds = claims.Select(x => x.ClaimId).ToList();
        var claimLines = await db.ClaimLines.Where(x => x.TenantId == tenant.TenantId
            && claimIds.Contains(x.ClaimId)).ToListAsync(cancellationToken);
        foreach (var row in claimLines)
            row.Description = Text($"claim-line:{row.ClaimLineId:N}:description", row.Description);
        var evidence = await db.ClaimEvidence.Where(x => x.TenantId == tenant.TenantId
            && claimIds.Contains(x.ClaimId)).ToListAsync(cancellationToken);
        foreach (var row in evidence)
            row.Description = Text($"claim-evidence:{row.EvidenceId:N}:description", row.Description);
        var rmaIds = await db.Rmas.Where(x => x.TenantId == tenant.TenantId
                && (x.ReturningPartyRef == subject.SubjectId || x.SettlementPartyRef == subject.SubjectId))
            .Select(x => x.RmaId).ToListAsync(cancellationToken);
        var rmaLines = await db.RmaLines.Where(x => x.TenantId == tenant.TenantId
            && rmaIds.Contains(x.RmaId)).ToListAsync(cancellationToken);
        foreach (var row in rmaLines)
            row.Description = Text($"rma-line:{row.RmaLineId:N}:description", row.Description);
        var receipts = await db.ReturnReceipts.Where(x => x.TenantId == tenant.TenantId
            && rmaIds.Contains(x.RmaId)).ToListAsync(cancellationToken);
        foreach (var row in receipts)
            row.ExceptionNote = Text($"return-receipt:{row.ReturnReceiptId:N}:exception_note", row.ExceptionNote);
        var ranges = await db.SerialRanges.Where(x => x.TenantId == tenant.TenantId
            && x.AllocatedToPartyRef == subject.SubjectId).ToListAsync(cancellationToken);
        foreach (var row in ranges)
            row.Note = Text($"serial-range:{row.RangeId:N}:note", row.Note);
        var id = subject.SubjectId.ToString("D");
        var workbench = await db.WorkbenchRequests.Where(x => x.TenantId == tenant.TenantId
            && x.TargetJson.Contains(id)).ToListAsync(cancellationToken);
        foreach (var row in workbench)
        {
            row.Note = Text($"workbench:{row.RequestId:N}:note", row.Note);
            row.DecisionNote = Text($"workbench:{row.RequestId:N}:decision_note", row.DecisionNote);
            row.UnclassifiedDescription = Text($"workbench:{row.RequestId:N}:unclassified_description",
                row.UnclassifiedDescription);
            row.AnswersJson = choices.GetValueOrDefault($"workbench:{row.RequestId:N}:answers_json")
                == PersonalDataReviewAction.Redact
                ? JsonSerializer.Serialize(Redacted)
                : ReplaceExact(row.AnswersJson, subject.KnownValues, subject.Token)!;
        }
        var correctionPredicate = PersonalDataQuery.ContainsAny<CorrectionRecord>(subject.KnownValues, x => x.Note);
        var correctionCursor = Guid.Empty;
        while (true)
        {
            var corrections = await db.Corrections.Where(x => x.TenantId == tenant.TenantId
                    && x.CorrectionId.CompareTo(correctionCursor) > 0
                    && db.CorrectionUnits.Any(link => link.TenantId == tenant.TenantId
                        && link.CorrectionId == x.CorrectionId
                        && db.Episodes.Any(episode => episode.TenantId == tenant.TenantId
                            && episode.UnitId == link.UnitId && episode.PartyRef == subject.SubjectId)))
                .Where(correctionPredicate).OrderBy(x => x.CorrectionId).Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (corrections.Count == 0) break;
            foreach (var row in corrections)
                row.Note = ReplaceExact(row.Note, subject.KnownValues, subject.Token);
            correctionCursor = corrections[^1].CorrectionId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return;

        string? Text(string key, string? value)
        {
            if (value is null) return null;
            var exact = ReplaceExact(value, subject.KnownValues, subject.Token);
            return choices.GetValueOrDefault(key) == PersonalDataReviewAction.Redact ? Redacted : exact;
        }
    }

    /// <summary>
    /// ADR-0017's sole exception to append-only ledger handling: only exact personal values in the party_code and note
    /// JSON properties are changed, while event identity, type, time and references remain untouched.
    /// </summary>
    private async Task<int> RedactPartyCodeForAdr0017Async(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        return await RedactLedgerTableAsync(db.Events, subject, cancellationToken)
            + await RedactLedgerTableAsync(db.ArchivedEvents, subject, cancellationToken)
            + await RedactLedgerTableAsync(db.ArchiveStage, subject, cancellationToken);
    }

    private async Task<int> RedactLedgerTableAsync<T>(DbSet<T> table, ErasureSubject subject,
        CancellationToken cancellationToken) where T : UnitEventRow
    {
        var values = Values(subject.KnownValues);
        if (values.Count == 0) return 0;
        var predicate = PersonalDataQuery.ContainsAny<T>(values, x => x.Attributes);
        var cursorTime = DateTime.MinValue;
        var cursorId = Guid.Empty;
        var changed = 0;
        while (true)
        {
            var rows = await table.Where(x => x.TenantId == tenant.TenantId
                    && (x.EventTime > cursorTime || x.EventTime == cursorTime && x.EventId.CompareTo(cursorId) > 0)
                    && db.Episodes.Any(episode => episode.TenantId == tenant.TenantId
                        && episode.UnitId == x.UnitId && episode.PartyRef == subject.SubjectId))
                .Where(predicate).OrderBy(x => x.EventTime).ThenBy(x => x.EventId).Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (RewriteLedgerJson(row.Attributes, subject) is { } rewritten)
                {
                    row.Attributes = rewritten;
                    changed++;
                }
            }
            cursorTime = rows[^1].EventTime;
            cursorId = rows[^1].EventId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> CountLedgerPartyCodesAsync(Guid partyRef, List<string> knownValues,
        CancellationToken cancellationToken)
    {
        if (knownValues.Count == 0) return 0;
        return await CountLedgerTableAsync(db.Events, partyRef, knownValues, cancellationToken)
            + await CountLedgerTableAsync(db.ArchivedEvents, partyRef, knownValues, cancellationToken)
            + await CountLedgerTableAsync(db.ArchiveStage, partyRef, knownValues, cancellationToken);
    }

    private async Task<int> CountLedgerTableAsync<T>(DbSet<T> table, Guid partyRef,
        IReadOnlyList<string> knownValues, CancellationToken cancellationToken) where T : UnitEventRow
    {
        var predicate = PersonalDataQuery.ContainsAny<T>(knownValues, x => x.Attributes);
        var cursorTime = DateTime.MinValue;
        var cursorId = Guid.Empty;
        var count = 0;
        while (true)
        {
            var rows = await table.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && (x.EventTime > cursorTime || x.EventTime == cursorTime && x.EventId.CompareTo(cursorId) > 0)
                    && db.Episodes.Any(episode => episode.TenantId == tenant.TenantId
                        && episode.UnitId == x.UnitId && episode.PartyRef == partyRef))
                .Where(predicate).OrderBy(x => x.EventTime).ThenBy(x => x.EventId).Take(BatchSize)
                .Select(x => new { x.EventTime, x.EventId, x.Attributes }).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Count(x => JsonValueAt(x.Attributes!, "party_code") is { } code
                    && knownValues.Contains(code, StringComparer.Ordinal)
                || JsonValueAt(x.Attributes!, "note") is { } note
                    && knownValues.Contains(note, StringComparer.Ordinal));
            cursorTime = rows[^1].EventTime;
            cursorId = rows[^1].EventId;
        }
        return count;
    }

    private async Task<int> CountCertificateRowsAsync(Guid partyRef, List<string> knownValues,
        CancellationToken cancellationToken)
    {
        if (knownValues.Count == 0) return 0;
        var predicate = PersonalDataQuery.ContainsAny<CertificateInstance>(knownValues, x => x.OwnerSnapshot);
        var cursor = Guid.Empty;
        var count = 0;
        while (true)
        {
            var rows = await CertificateCandidates(partyRef, cursor, predicate, tracked: false, cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Count(x => OwnerSnapshotNamesParty(x.OwnerSnapshot!, knownValues));
            cursor = rows[^1].CertificateId;
        }
        return count;
    }

    private async Task<int> RedactCertificatesAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken)
    {
        var values = Values(subject.KnownValues);
        if (values.Count == 0) return 0;
        var predicate = PersonalDataQuery.ContainsAny<CertificateInstance>(values, x => x.OwnerSnapshot);
        var changed = 0;
        var cursor = Guid.Empty;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // TIGHT COUPLING (ADR-0017 certificate immutability): this exact session key is the only seam through the
            // database trigger. The trigger normally refuses application updates; renaming the key here or there makes
            // lawful owner redaction fail closed. The matching trigger comment lives in Erase01CertificateRedaction.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key=N'yojana.erasure_id', @value={erasureId}",
                cancellationToken);
            while (true)
            {
                var rows = await CertificateCandidates(subject.SubjectId, cursor, predicate, tracked: true,
                    cancellationToken);
                if (rows.Count == 0) break;
                foreach (var row in rows.Where(x => OwnerSnapshotNamesParty(x.OwnerSnapshot!, values)))
                {
                    row.OwnerSnapshot = RedactJson(row.OwnerSnapshot!, subject);
                    row.Payload = RedactJson(row.Payload, subject);
                    db.CertificateRedactions.Add(new CertificateRedaction
                    {
                        TenantId = tenant.TenantId, CertificateId = row.CertificateId, ErasureId = erasureId,
                        OriginalPayloadHash = row.PayloadHash, RedactedAt = clock.UtcNow,
                    });
                    changed++;
                }
                cursor = rows[^1].CertificateId;
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "EXEC sys.sp_set_session_context @key=N'yojana.erasure_id', @value=NULL", cancellationToken);
            await db.Database.CloseConnectionAsync();
        }
        return changed;
    }

    private Task<List<CertificateInstance>> CertificateCandidates(Guid partyRef, Guid cursor,
        System.Linq.Expressions.Expression<Func<CertificateInstance, bool>> predicate, bool tracked,
        CancellationToken cancellationToken)
    {
        var query = db.CertificateInstances.Where(x => x.TenantId == tenant.TenantId
                && x.CertificateId.CompareTo(cursor) > 0 && x.OwnerSnapshot != null
                && db.Episodes.Any(episode => episode.TenantId == tenant.TenantId
                    && episode.UnitId == x.UnitId && episode.PartyRef == partyRef)
                && !db.CertificateRedactions.Any(redaction => redaction.TenantId == tenant.TenantId
                    && redaction.CertificateId == x.CertificateId))
            .Where(predicate).OrderBy(x => x.CertificateId).Take(BatchSize);
        return (tracked ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
    }

    private async Task<int> PurgeImportsAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        var results = await db.ImportRowResults.Where(x => x.TenantId == tenant.TenantId
            && x.CollisionOwnerPartyRef == subject.SubjectId).ToListAsync(cancellationToken);
        var jobIds = results.Select(x => x.JobId).Distinct().ToList();
        if (jobIds.Count == 0) return 0;
        var jobs = await db.ImportJobs.Where(x => x.TenantId == tenant.TenantId && jobIds.Contains(x.JobId))
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            foreach (var reference in new[] { job.OriginalFileReference, job.ConvertedFileReference,
                         job.OriginalArchiveReference, job.ConvertedArchiveReference }.Where(x => x is not null).Distinct())
                await importFiles.PurgeAsync(reference!, cancellationToken);
            job.OriginalContent = null;
            job.ConvertedContent = null;
            job.OriginalFileReference = null;
            job.ConvertedFileReference = null;
            job.OriginalArchiveReference = null;
            job.ConvertedArchiveReference = null;
            job.OriginalFileCleanupReference = null;
            job.ConvertedFileCleanupReference = null;
        }
        foreach (var row in results)
        {
            if (row.RawValue is not null && subject.KnownValues.Any(value =>
                    row.RawValue.Contains(value, StringComparison.Ordinal))) row.RawValue = subject.Token;
        }
        await db.SaveChangesAsync(cancellationToken);
        return jobs.Count;
    }

    private static string RedactJson(string json, ErasureSubject subject)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Stored JSON is invalid.");
        RedactNode(node, null, subject);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void RedactNode(JsonNode node, string? propertyName, ErasureSubject subject)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    && subject.KnownValues.Contains(text, StringComparer.Ordinal))
                    obj[pair.Key] = pair.Key.Contains("code", StringComparison.OrdinalIgnoreCase)
                        ? subject.Code : pair.Key.Contains("email", StringComparison.OrdinalIgnoreCase)
                            ? subject.Email : subject.Token;
                else if (pair.Value is not null) RedactNode(pair.Value, pair.Key, subject);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)
                    && subject.KnownValues.Contains(text, StringComparer.Ordinal)) array[i] = subject.Token;
                else if (array[i] is not null) RedactNode(array[i]!, propertyName, subject);
            }
        }
    }

    private static string? RewriteLedgerJson(string? json, ErasureSubject subject)
    {
        if (json is null) return null;
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node is null) return null;
            var changed = false;
            if (node["party_code"] is JsonValue code && code.TryGetValue<string>(out var codeText)
                && subject.KnownValues.Contains(codeText, StringComparer.Ordinal))
            {
                node["party_code"] = subject.Code;
                changed = true;
            }
            if (node["note"] is JsonValue note && note.TryGetValue<string>(out var noteText)
                && subject.KnownValues.Contains(noteText, StringComparer.Ordinal))
            {
                node["note"] = subject.Token;
                changed = true;
            }
            return changed ? node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool OwnerSnapshotNamesParty(string json, IReadOnlyList<string> knownValues)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && ((root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        && knownValues.Contains(code.GetString()!, StringComparer.Ordinal))
                    || (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && knownValues.Contains(name.GetString()!, StringComparer.Ordinal)));
        }
        catch (JsonException) { return false; }
    }

    private static List<string> Values(IEnumerable<string> values) => values
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();

    private static string? ReplaceExact(string? value, IReadOnlyList<string> knownValues, string replacement)
    {
        if (value is null) return null;
        foreach (var known in knownValues.Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x.Length))
            value = value.Replace(known, replacement, StringComparison.Ordinal);
        return value;
    }

    private static string? JsonValueAt(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static PersonalDataReviewItem Item(string key, string store, string reference, string field,
        string? value) => new(key, store, reference, field,
        value is null ? null : value.Length <= 160 ? value : value[..160]);

    private static PersonalDataPreview Empty() => new([], [], []);
}
