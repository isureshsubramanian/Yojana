using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Validation;

public interface IMigrationValidationQueue
{
    Task<int> EnqueueEligibleAsync(CancellationToken cancellationToken);
    Task<int> RunPendingAsync(CancellationToken cancellationToken);
}

public sealed class ValidationService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ValidationSettings settings,
    MappingSettings mappingSettings,
    IMigrationValidationTarget target,
    IUserDirectory people,
    ValidationSourceReader sourceReader) : IMigrationValidationQueue
{
    private const int MaxPageSize = 200;
    private static readonly IReadOnlyDictionary<string, int> CheckPositions = new[]
    {
        "unit_master", "active_cover", "open_service_calls", "stock_by_item_warehouse",
        MigrationProfileClasses.SerialInMultipleWarehouses.Code, MigrationProfileClasses.NegativeQuantity.Code,
        "historical_events", "status_histogram", "event_per_unit", "one_current_row", "signed_quantity",
        "stratified_sample", PlanGapKind.CardCoverPolicyNotSet,
        PlanGapKind.SerialMasterCoverPolicyNotSet, "cover_gap_report", "attachments_by_hash",
    }.Select((code, index) => new { code, index }).ToDictionary(x => x.code, x => x.index,
        StringComparer.Ordinal);

    public async Task<StartValidationResponse> EnqueueAsync(Guid batchId, bool rehearsal,
        CancellationToken cancellationToken)
    {
        var map = await ReadyMapAsync(batchId, cancellationToken);
        if (await db.MigrationValidationRuns.AnyAsync(x => x.TenantId == tenant.TenantId &&
                x.BatchId == batchId && (x.Status == ValidationRunStatus.Queued ||
                x.Status == ValidationRunStatus.Running), cancellationToken))
            throw new ValidationRuleException("migration_validation_already_running",
                "Validation is already queued or running for this batch.");
        var seed = Seed(map.RunId);
        var run = new MigrationValidationRun
        {
            TenantId = tenant.TenantId, RunId = Guid.CreateVersion7(clock.UtcNow), BatchId = batchId,
            MapRunId = map.RunId, TargetKind = target.Kind, Rehearsal = rehearsal, SampleSeed = seed,
            RequestedAt = clock.UtcNow, Status = ValidationRunStatus.Queued,
        };
        db.MigrationValidationRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, batchId, run.Status, run.TargetKind);
    }

    public async Task<int> EnqueueEligibleAsync(CancellationToken cancellationToken)
    {
        var maps = await db.MigrationMapRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.Status == MappingRunStatus.Completed && !db.MigrationValidationRuns.Any(v =>
                    v.TenantId == x.TenantId && v.MapRunId == x.RunId))
            .OrderBy(x => x.FinishedAt).ThenBy(x => x.RunId).ToListAsync(cancellationToken);
        foreach (var map in maps)
            db.MigrationValidationRuns.Add(new MigrationValidationRun
            {
                TenantId = tenant.TenantId, RunId = Guid.CreateVersion7(clock.UtcNow), BatchId = map.BatchId,
                MapRunId = map.RunId, TargetKind = target.Kind, Rehearsal = map.AllowedWaitingReviews,
                SampleSeed = Seed(map.RunId),
                RequestedAt = clock.UtcNow, Status = ValidationRunStatus.Queued,
            });
        if (maps.Count != 0) await db.SaveChangesAsync(cancellationToken);
        return maps.Count;
    }

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var run = await db.MigrationValidationRuns.Where(x => x.TenantId == tenant.TenantId &&
                    x.Status == ValidationRunStatus.Queued).OrderBy(x => x.RequestedAt).ThenBy(x => x.RunId)
                .FirstOrDefaultAsync(cancellationToken);
            if (run is null) return completed;
            run.Status = ValidationRunStatus.Running;
            run.StartedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                await ExecuteAsync(run, cancellationToken);
                run.Status = ValidationRunStatus.Completed;
                run.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (Exception ex)
            {
                run.Status = ValidationRunStatus.Failed;
                run.FailureCode = "migration_validation_failed";
                run.FailureDetail = ex is ValidationRuleException rule
                    ? rule.Message
                    : "Validation could not be completed. Run the checks again; if it fails again, ask an administrator to inspect the service log.";
                run.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        return completed;
    }

    public async Task<MigrationValidationView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationValidationRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.BatchId == batchId).OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : await ViewAsync(run, cancellationToken);
    }

    public async Task<TabularExport> ExportChecksAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var view = await GetAsync(batchId, cancellationToken) ?? throw new ValidationRuleException(
            "migration_validation_run_not_found", "The validation run was not found.");
        var target = view.TargetKind == ValidationTargetKind.Module ? "Module total" : "Plan total";
        return new TabularExport(
            ["Check", "Limit", "Source total", target, "Difference", "Result", "Explanation",
                "Explained by", "Explained at", "How counted", "Ran at", "Duration (milliseconds)",
                "Prepared by", "Prepared at", "Reviewed by", "Reviewed at"],
            ExportRows(view.Checks, cancellationToken));
    }

    public async Task<ValidationCheckDetail?> CheckAsync(Guid checkId, CancellationToken cancellationToken)
    {
        var check = await db.MigrationValidationChecks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.CheckId == checkId, cancellationToken);
        if (check is null) return null;
        var currentSignatures = await SignaturesAsync([check.CheckId], cancellationToken);
        var run = await db.MigrationValidationRuns.AsNoTracking().SingleAsync(x =>
            x.TenantId == tenant.TenantId && x.RunId == check.RunId, cancellationToken);
        var historyIds = await (from historicCheck in db.MigrationValidationChecks.AsNoTracking()
            join historicRun in db.MigrationValidationRuns.AsNoTracking()
                on new { historicCheck.TenantId, historicCheck.RunId } equals
                new { historicRun.TenantId, historicRun.RunId }
            where historicCheck.TenantId == tenant.TenantId && historicRun.BatchId == run.BatchId &&
                historicCheck.Code == check.Code
            select historicCheck.CheckId).ToListAsync(cancellationToken);
        var signatureHistory = await SignaturesAsync(historyIds, cancellationToken);
        var parts = await db.MigrationValidationCheckParts.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == checkId).OrderBy(x => x.Code)
            .Select(x => new ValidationCheckPartView(x.Code, x.Label, x.Value)).ToListAsync(cancellationToken);
        return new(await CheckViewAsync(check, currentSignatures.GetValueOrDefault(checkId, []), cancellationToken),
            parts, signatureHistory.Values.SelectMany(x => x).OrderBy(x => x.SignedAt).ToList());
    }

    public async Task<ValidationDifferencePage> DifferencesAsync(Guid checkId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxPageSize);
        if (!await db.MigrationValidationChecks.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId &&
                x.CheckId == checkId, cancellationToken))
            throw new ValidationRuleException("migration_validation_check_not_found", "The validation check was not found.");
        var after = Decode(cursor);
        var rows = await db.MigrationValidationDifferences.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == checkId && x.DifferenceId.CompareTo(after) > 0)
            .OrderBy(x => x.DifferenceId).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        return new(page.Select(x => new ValidationDifferenceView(x.DifferenceId, x.Kind, x.ItemCode,
            x.WarehouseCode, x.Serial, x.SourceValue, x.TargetValue, x.Difference, x.Detail)).ToList(),
            rows.Count > take ? Encode(page[^1].DifferenceId) : null);
    }

    public async Task<ValidationCheckView> ExplainAsync(Guid checkId, string explanation, string actor,
        CancellationToken cancellationToken)
    {
        var check = await RequiredCheckAsync(checkId, cancellationToken);
        if (check.Result == ValidationResult.HardFail)
            throw new ValidationRuleException("migration_validation_hard_fail_not_explainable",
                "A hard fail must be fixed in the source application and checked by a new extraction and rehearsal.");
        if (check.Result != ValidationResult.Fail)
            throw new ValidationRuleException("migration_validation_check_not_failed",
                "Only a failed check can be changed to explained.");
        if (string.IsNullOrWhiteSpace(explanation))
            throw new ValidationRuleException("migration_validation_explanation_required",
                "Write an explanation before accepting this difference.");
        check.OriginalResult = check.Result;
        check.Result = ValidationResult.Explained;
        check.Explanation = explanation.Trim();
        check.ExplainedBy = actor;
        check.ExplainedAt = clock.UtcNow;
        check.ResultFingerprint = Fingerprint(check);
        await VoidSignaturesAsync(check.CheckId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await CheckViewAsync(check, [], cancellationToken);
    }

    public async Task<ValidationCheckView> SignAsync(Guid checkId, string role, string actor,
        IReadOnlyCollection<string> actorRoles, CancellationToken cancellationToken)
    {
        if (role is not (ValidationSignatureRole.Preparer or ValidationSignatureRole.Reviewer))
            throw new ValidationRuleException("migration_validation_signature_role_invalid",
                "Signature role must be preparer or reviewer.");
        if (role == ValidationSignatureRole.Preparer && !actorRoles.Contains(YojanaRole.MigrationLead) &&
            !actorRoles.Contains(YojanaRole.TenantAdministrator))
            throw new ValidationRuleException("migration_validation_preparer_role_required",
                "A preparer must be a migration lead or tenant administrator.");
        if (role == ValidationSignatureRole.Reviewer && !actorRoles.Contains(YojanaRole.TenantAdministrator))
            throw new ValidationRuleException("migration_validation_reviewer_role_required",
                "A reviewer must be a tenant administrator.");
        var check = await RequiredCheckAsync(checkId, cancellationToken);
        if (check.Result == ValidationResult.HardFail)
            throw new ValidationRuleException("migration_validation_hard_fail_not_signable",
                "A hard fail cannot be signed; fix it in the source application and run the checks again.");
        var active = await db.MigrationValidationSignatures.Where(x => x.TenantId == tenant.TenantId &&
            x.CheckId == checkId && !x.Superseded).ToListAsync(cancellationToken);
        if (active.Any(x => x.Role == role))
            throw new ValidationRuleException("migration_validation_role_already_signed",
                $"This check already has an active {role} signature.");
        if (active.Any(x => x.Actor == actor))
            throw new ValidationRuleException("migration_validation_distinct_signers_required",
                "The preparer and reviewer must be two different people.");
        db.MigrationValidationSignatures.Add(new MigrationValidationSignature
        {
            TenantId = tenant.TenantId, SignatureId = Guid.CreateVersion7(clock.UtcNow), CheckId = checkId,
            Role = role, Actor = actor, SignedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        var signatures = await SignaturesAsync([checkId], cancellationToken);
        return await CheckViewAsync(check, signatures[checkId], cancellationToken);
    }

    public async Task<ValidationSamplePage> SampleAsync(Guid runId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxPageSize);
        var after = Decode(cursor);
        var rows = await db.MigrationValidationSamples.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.RunId == runId && x.SampleId.CompareTo(after) > 0).OrderBy(x => x.SampleId).Take(take + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var names = await ResolveNamesAsync(page.Select(x => x.TracedBy), cancellationToken);
        return new(page.Select(x => new ValidationSampleView(x.SampleId, x.Serial, x.ItemCode, x.ItemFamily,
            x.AgeBand, x.SourceTrailJson, x.TargetTrailJson, x.Outcome, x.Note,
            Name(names, x.TracedBy), x.TracedAt)).ToList(), rows.Count > take ? Encode(page[^1].SampleId) : null);
    }

    public async Task<ValidationSampleView> TraceAsync(Guid sampleId, string outcome, string? note, string actor,
        CancellationToken cancellationToken)
    {
        if (outcome is not ("traced" or "mismatch"))
            throw new ValidationRuleException("migration_validation_sample_outcome_invalid",
                "Sample outcome must be traced or mismatch.");
        var sample = await db.MigrationValidationSamples.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId &&
            x.SampleId == sampleId, cancellationToken) ?? throw new ValidationRuleException(
                "migration_validation_sample_not_found", "The validation sample was not found.");
        sample.Outcome = outcome;
        sample.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        sample.TracedBy = actor;
        sample.TracedAt = clock.UtcNow;
        var check = await RequiredCheckAsync(sample.CheckId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var outcomes = await db.MigrationValidationSamples.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == check.CheckId).Select(x => x.Outcome)
            .ToListAsync(cancellationToken);
        var oldFingerprint = check.ResultFingerprint;
        check.TargetTotal = outcomes.LongCount(x => x == "traced");
        check.Difference = check.TargetTotal - check.SourceTotal;
        check.Result = outcomes.All(x => x == "traced") ? ValidationResult.Pass : ValidationResult.Fail;
        check.ResultFingerprint = Fingerprint(check);
        if (oldFingerprint != check.ResultFingerprint) await VoidSignaturesAsync(check.CheckId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var names = await ResolveNamesAsync([sample.TracedBy], cancellationToken);
        return new(sample.SampleId, sample.Serial, sample.ItemCode, sample.ItemFamily, sample.AgeBand,
            sample.SourceTrailJson, sample.TargetTrailJson, sample.Outcome, sample.Note,
            Name(names, sample.TracedBy), sample.TracedAt);
    }

    public async Task<SignoffRecordView> CreateSignoffAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        if (await db.MigrationSignoffRecords.AnyAsync(x => x.TenantId == tenant.TenantId && x.RunId == runId,
                cancellationToken))
            throw new ValidationRuleException("migration_signoff_already_exists",
                "An immutable sign-off record already exists for this validation run.");
        var run = await db.MigrationValidationRuns.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId &&
            x.RunId == runId, cancellationToken) ?? throw new ValidationRuleException(
                "migration_validation_run_not_found", "The validation run was not found.");
        if (run.Status != ValidationRunStatus.Completed)
            throw new ValidationRuleException("migration_validation_not_completed",
                "Complete the validation run before creating its sign-off record.");
        var checks = await db.MigrationValidationChecks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.RunId == runId).ToListAsync(cancellationToken);
        checks = checks.OrderBy(x => CheckPositions.GetValueOrDefault(x.Code, int.MaxValue)).ToList();
        var signatures = await db.MigrationValidationSignatures.AsNoTracking().Where(x =>
            x.TenantId == tenant.TenantId && checks.Select(c => c.CheckId).Contains(x.CheckId) && !x.Superseded)
            .ToListAsync(cancellationToken);
        if (!run.Rehearsal && checks.Any(x => x.Result == ValidationResult.HardFail))
            throw new ValidationRuleException("migration_signoff_hard_fail",
                "A hard fail blocks final sign-off. Fix it in the source application and run the extraction and rehearsal again.");
        if (!run.Rehearsal && checks.Any(c => signatures.Count(s => s.CheckId == c.CheckId) != 2))
            throw new ValidationRuleException("migration_signoff_unsigned_checks",
                "Every check needs an active preparer and reviewer signature.");
        var record = new MigrationSignoffRecord
        {
            TenantId = tenant.TenantId, RecordId = Guid.CreateVersion7(clock.UtcNow), RunId = runId,
            Rehearsal = run.Rehearsal, CreatedAt = clock.UtcNow, CreatedBy = actor,
        };
        db.MigrationSignoffRecords.Add(record);
        var position = 0;
        foreach (var check in checks)
        {
            var preparer = signatures.SingleOrDefault(x => x.CheckId == check.CheckId &&
                x.Role == ValidationSignatureRole.Preparer);
            var reviewer = signatures.SingleOrDefault(x => x.CheckId == check.CheckId &&
                x.Role == ValidationSignatureRole.Reviewer);
            db.MigrationSignoffLines.Add(new MigrationSignoffLine
            {
                TenantId = tenant.TenantId, LineId = Guid.CreateVersion7(clock.UtcNow.AddTicks(++position)),
                RecordId = record.RecordId, Position = position, ReportName = check.Label,
                SourceTotal = check.SourceTotal, TargetTotal = check.TargetTotal, Difference = check.Difference,
                Explanation = check.Explanation, Result = check.Result, Preparer = preparer?.Actor ?? "unsigned",
                PreparedAt = preparer?.SignedAt ?? record.CreatedAt, Reviewer = reviewer?.Actor ?? "unsigned",
                ReviewedAt = reviewer?.SignedAt ?? record.CreatedAt,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return (await SignoffAsync(record.RecordId, cancellationToken))!;
    }

    public async Task<SignoffRecordView?> SignoffAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var record = await db.MigrationSignoffRecords.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.RecordId == recordId, cancellationToken);
        if (record is null) return null;
        var lines = await db.MigrationSignoffLines.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.RecordId == recordId).OrderBy(x => x.Position).ToListAsync(cancellationToken);
        var names = await ResolveNamesAsync(lines.SelectMany(x => new[] { x.Preparer, x.Reviewer })
            .Append(record.CreatedBy), cancellationToken);
        return new(record.RecordId, record.RunId, record.Rehearsal, record.CreatedAt,
            Name(names, record.CreatedBy)!, lines.Select(x => new SignoffLineView(x.Position, x.ReportName,
                x.SourceTotal, x.TargetTotal, x.Difference, x.Explanation, x.Result,
                Name(names, x.Preparer)!, x.PreparedAt, Name(names, x.Reviewer)!, x.ReviewedAt)).ToList());
    }

    public async Task<byte[]> ExportCsvAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var record = await SignoffAsync(recordId, cancellationToken) ?? throw new ValidationRuleException(
            "migration_signoff_not_found", "The sign-off record was not found.");
        var csv = new StringBuilder("Record,Stamp,Report,Source total,Target total,Difference,Result,Explanation,Preparer,Prepared at,Reviewer,Reviewed at\r\n");
        foreach (var line in record.Lines)
            csv.AppendJoin(',', Csv(record.RecordId.ToString("D")), Csv(record.Rehearsal ? "REHEARSAL" : "FINAL"),
                Csv(line.ReportName), Csv(line.SourceTotal), Csv(line.TargetTotal), Csv(line.Difference),
                Csv(line.Result), Csv(line.Explanation), Csv(line.PreparerName), Csv(line.PreparedAt),
                Csv(line.ReviewerName), Csv(line.ReviewedAt)).Append("\r\n");
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private async Task ExecuteAsync(MigrationValidationRun run, CancellationToken cancellationToken)
    {
        var map = await db.MigrationMapRuns.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId &&
            x.RunId == run.MapRunId, cancellationToken);
        var reconstruction = await db.MigrationReconstructionRuns.AsNoTracking().SingleAsync(x =>
            x.TenantId == tenant.TenantId && x.RunId == map.ReconstructionRunId, cancellationToken);
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId &&
            x.BatchId == run.BatchId, cancellationToken);
        var canon = await db.MigrationCanonRuns.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId &&
            x.RunId == reconstruction.CanonicalisationRunId, cancellationToken);
        var snapshot = await target.ReadAsync(map.RunId, map.CutoverDate, cancellationToken);
        var configured = await settings.GetAsync(cancellationToken);
        var previous = await db.MigrationValidationRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.BatchId == run.BatchId && x.RunId != run.RunId && x.Status == ValidationRunStatus.Completed)
            .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.RunId).FirstOrDefaultAsync(cancellationToken);

        var activeCandidates = await db.MigrationUnitCandidates.AsNoTracking().LongCountAsync(x =>
            x.TenantId == tenant.TenantId && x.RunId == canon.RunId && x.Disposition == CandidateDisposition.Active,
            cancellationToken);
        var mergedByRule = await (from sourceRow in db.MigrationUnitCandidateSources.AsNoTracking()
            join candidate in db.MigrationUnitCandidates.AsNoTracking()
                on new { sourceRow.TenantId, sourceRow.CandidateId } equals
                new { candidate.TenantId, candidate.CandidateId }
            where sourceRow.TenantId == tenant.TenantId && sourceRow.RunId == canon.RunId &&
                sourceRow.Role == CandidateSourceRole.Merged
            group sourceRow by candidate.MatchRule into grouped
            select new { Rule = grouped.Key, Count = grouped.LongCount() }).ToListAsync(cancellationToken);
        var normalisedMerges = mergedByRule.Where(x => x.Rule is CanonicalMatchRule.TrimmedUpper or
            CanonicalMatchRule.FamilyNormalised).Sum(x => x.Count);
        var reviewMerges = mergedByRule.Where(x => x.Rule == CanonicalMatchRule.Reviewed).Sum(x => x.Count);
        var sourceUnits = Math.Max(0, activeCandidates - map.WaitingCandidateCount);
        var unit = AddCheck(run, "unit_master", "Unit master count", "zero", sourceUnits, snapshot.Units,
            sourceUnits == snapshot.Units ? ValidationResult.Pass : ValidationResult.Fail,
            "Source serial rows after placeholder removal, normalisation, review merges and exclusions; compared with target units.");
        AddPart(unit, "serial_rows", "Serial rows", canon.SourceSerialCount);
        var profile = await db.MigrationProfileResults.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.RunId == canon.ProfileRunId).ToDictionaryAsync(x => x.ClassCode, cancellationToken);
        AddPart(unit, "placeholders", "Less placeholders", profile.GetValueOrDefault(
            MigrationProfileClasses.PlaceholderSerial.Code)?.RowCount ?? 0);
        AddPart(unit, "normalised_duplicates", "Less trimmed or normalised duplicates",
            normalisedMerges);
        AddPart(unit, "review_merges", "Less merged by review", reviewMerges);
        AddPart(unit, "excluded", "Less excluded", map.ExcludedCandidateCount + map.WaitingCandidateCount);
        AddPart(unit, "after_review", "Unit master count after review", sourceUnits);

        var activeContracts = await (from contract in db.Set<ServiceContractStagingRow>().AsNoTracking()
            join line in db.Set<ContractLineStagingRow>().AsNoTracking()
                on new { contract.TenantId, contract.BatchId, contract.ContractId } equals
                new { line.TenantId, line.BatchId, line.ContractId }
            where contract.TenantId == tenant.TenantId && contract.BatchId == run.BatchId &&
                contract.Status == "A" && contract.ServiceType == "W" && contract.EndDate != null &&
                contract.EndDate.Value.Date >= map.CutoverDate.ToDateTime(TimeOnly.MinValue)
            select line.SourcePk).LongCountAsync(cancellationToken);
        AddCheck(run, "active_cover", "Active cover at cutover", "zero", activeContracts,
            snapshot.ActiveEntitlements, activeContracts == snapshot.ActiveEntitlements ? ValidationResult.Pass :
                ValidationResult.Fail, "Lines on active source warranty contracts ending on or after cutover compared with target entitlements live at cutover.");

        var openCalls = await db.Set<ServiceCallStagingRow>().AsNoTracking().LongCountAsync(x =>
            x.TenantId == tenant.TenantId && x.BatchId == run.BatchId && x.ClosedAt == null, cancellationToken);
        AddCheck(run, "open_service_calls", "Open service calls", "zero", openCalls,
            snapshot.OpenServiceCalls, snapshot.OpenServiceCalls == 0 && openCalls != 0 ? ValidationResult.Explained :
                openCalls == snapshot.OpenServiceCalls ? ValidationResult.Pass : ValidationResult.Fail,
            "Open source service calls compared with target service calls.", snapshot.OpenServiceCalls == 0 &&
                openCalls != 0 ? "service calls are not loaded by this phase" : null);

        await AddStockCheckAsync(run, snapshot, cancellationToken);
        AddHardFact(run, profile, MigrationProfileClasses.SerialInMultipleWarehouses,
            "Serial in two warehouses");
        AddHardFact(run, profile, MigrationProfileClasses.NegativeQuantity, "Negative quantity");

        var reconstructedSourceEvents = await db.MigrationHistoryEvents.AsNoTracking().LongCountAsync(x =>
            x.TenantId == tenant.TenantId && x.ReconstructionRunId == reconstruction.RunId &&
            x.Provenance == HistoryProvenance.Source, cancellationToken);
        var excludedEvents = await db.MigrationPlanLogs.AsNoTracking().LongCountAsync(x =>
            x.TenantId == tenant.TenantId && x.MapRunId == map.RunId && x.Kind == PlanLogKind.EventExcluded,
            cancellationToken);
        var transactionRows = reconstructedSourceEvents + reconstruction.CancellationPairsRemoved * 2;
        var historicalSource = Math.Max(0, reconstructedSourceEvents - excludedEvents);
        var targetEvents = await db.MigrationPlanEvents.AsNoTracking().LongCountAsync(x =>
            x.TenantId == tenant.TenantId && x.MapRunId == map.RunId && x.Provenance != HistoryProvenance.Reconstructed,
            cancellationToken);
        var eventDifference = targetEvents - historicalSource;
        var eventPercent = historicalSource == 0 ? (targetEvents == 0 ? 0 : 100) :
            Math.Abs(eventDifference) * 100m / historicalSource;
        var history = AddCheck(run, "historical_events", "Historical events",
            $"< {configured.HistoricalTolerancePercent.ToString(CultureInfo.InvariantCulture)}%", historicalSource,
            targetEvents, eventPercent < configured.HistoricalTolerancePercent ? ValidationResult.Pass :
            ValidationResult.Fail, "Transaction rows linked to canonical units, less legitimate cancellation pairs and review-excluded events, compared with source-provenance target events.");
        AddPart(history, "transaction_rows", "Transaction rows linked to canonical units", transactionRows);
        AddPart(history, "cancellation_rows", "Less cancellation-pair rows", reconstruction.CancellationPairsRemoved * 2);
        AddPart(history, "review_excluded", "Less events excluded by review", excludedEvents);
        AddPart(history, "difference_percent", "Absolute difference percent", eventPercent);

        await AddStatusCheckAsync(run, map, snapshot, cancellationToken);
        AddCheck(run, "event_per_unit", "At least one event per unit", "zero", snapshot.Units,
            snapshot.UnitsWithEvent, snapshot.Units == snapshot.UnitsWithEvent ? ValidationResult.Pass :
                ValidationResult.Fail, "Target units compared with distinct target units carrying at least one event.");
        AddCheck(run, "one_current_row", "Exactly one current row per unit", "zero", snapshot.Units,
            snapshot.UnitsWithExactlyOneCurrentRow, snapshot.Units == snapshot.UnitsWithExactlyOneCurrentRow ?
                ValidationResult.Pass : ValidationResult.Fail,
            "Target units compared with units carrying exactly one current location or custody state.");
        await AddSignedQuantityCheckAsync(run, map, batch.SourceSystem, cancellationToken);
        await AddSampleAsync(run, map, canon, configured, cancellationToken);

        var policyGapCounts = await db.MigrationPlanGaps.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.MapRunId == map.RunId &&
                (x.Kind == PlanGapKind.CardCoverPolicyNotSet ||
                 x.Kind == PlanGapKind.SerialMasterCoverPolicyNotSet))
            .GroupBy(x => x.Kind).Select(group => new { Kind = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(x => x.Kind, x => x.Count, cancellationToken);
        AddCoverPolicyWarning(run, PlanGapKind.CardCoverPolicyNotSet,
            policyGapCounts.GetValueOrDefault(PlanGapKind.CardCoverPolicyNotSet),
            "Equipment-card cover dates retained without a configured published base-family policy.");
        AddCoverPolicyWarning(run, PlanGapKind.SerialMasterCoverPolicyNotSet,
            policyGapCounts.GetValueOrDefault(PlanGapKind.SerialMasterCoverPolicyNotSet),
            "Serial-master cover dates retained without a configured published supplier-family policy.");

        // Sold units the load will leave without base cover: no base entitlement naming a policy in the plan, whether
        // the plan recorded a gap (no template, unapproved template, unpublished policy) or kept dates without a
        // policy (equipment card). The gap report run after the load lists the same units for backfill (P9-T09).
        var coverGaps = await db.MigrationPlanUnits.AsNoTracking().LongCountAsync(planUnit =>
            planUnit.TenantId == tenant.TenantId && planUnit.MapRunId == map.RunId &&
            planUnit.LocationState == PlanLocationState.Shipped &&
            !db.MigrationPlanEntitlements.Any(cover => cover.TenantId == planUnit.TenantId &&
                cover.PlanUnitId == planUnit.PlanUnitId && cover.CoverageFamily == CoverageFamilyCode.Base &&
                cover.PolicyId != null), cancellationToken);
        AddCheck(run, "cover_gap_report", "Units sold with no base cover", "report", coverGaps, null,
            ValidationResult.Report, "Sold plan units with no base entitlement naming a published policy; the gap report after the load lists them for backfill.");
        AddCheck(run, "attachments_by_hash", "Attachments by content hash", "report",
            snapshot.AttachmentsListed, snapshot.AttachmentsWithHash, ValidationResult.Report,
            "Plan attachments listed compared with attachments carrying a content hash.");

        await db.SaveChangesAsync(cancellationToken);
        foreach (var check in db.MigrationValidationChecks.Local.Where(x => x.RunId == run.RunId).ToList())
            check.ResultFingerprint = await ResultFingerprintAsync(check, cancellationToken);
        await CarrySignaturesAsync(previous, run, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AddStockCheckAsync(MigrationValidationRun run, MigrationValidationTargetSnapshot targetSnapshot,
        CancellationToken cancellationToken)
    {
        var sourceRows = await db.Set<SerialQuantityStagingRow>().AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.BatchId == run.BatchId && x.ItemCode != null &&
                x.WarehouseCode != null).GroupBy(x => new { x.ItemCode, x.WarehouseCode }).Select(g => new
            {
                Item = g.Key.ItemCode!, Warehouse = g.Key.WarehouseCode!, Quantity = g.Sum(x => x.Quantity ?? 0),
            }).ToListAsync(cancellationToken);
        var source = sourceRows.ToDictionary(x => (x.Item, x.Warehouse), x => x.Quantity);
        var targetRows = targetSnapshot.InStockByItemWarehouse.ToDictionary(x => (x.ItemCode, x.Warehouse),
            x => x.Quantity);
        var all = source.Keys.Union(targetRows.Keys).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
        var differences = all.Select(key => new { Key = key, Source = source.GetValueOrDefault(key),
            Target = targetRows.GetValueOrDefault(key) }).Where(x => x.Source != x.Target).ToList();
        var check = AddCheck(run, "stock_by_item_warehouse", "In stock per item and warehouse", "zero",
            (long)source.Values.Sum(), (long)targetRows.Values.Sum(), differences.Count == 0 ? ValidationResult.Pass :
                ValidationResult.Fail, "In-stock target units counted by item and warehouse compared with summed source per-warehouse quantity.");
        AddPart(check, "pairs", "Item and warehouse pairs", all.Count);
        foreach (var row in differences) AddDifference(check, "item_warehouse", row.Key.Item1, row.Key.Item2,
            null, row.Source, row.Target, null);
    }

    private void AddHardFact(MigrationValidationRun run, IReadOnlyDictionary<string, MigrationProfileResult> profile,
        MigrationProfileClass fact, string label)
    {
        var count = profile.GetValueOrDefault(fact.Code)?.RowCount ?? 0;
        AddCheck(run, fact.Code, label, "none", count, null, count == 0 ? ValidationResult.Pass :
            ValidationResult.HardFail, $"Reuses the '{fact.Name}' fact from the completed profiling run; it is not recomputed by validation.");
    }

    private async Task AddStatusCheckAsync(MigrationValidationRun run, MigrationMapRun map,
        MigrationValidationTargetSnapshot snapshot, CancellationToken cancellationToken)
    {
        var target = snapshot.StatusHistogram.ToDictionary(x => x.Bucket, x => x.Count, StringComparer.OrdinalIgnoreCase);
        var rules = (await mappingSettings.GetAsync(cancellationToken)).ConditionMap;
        var source = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Guid? after = null;
        while (true)
        {
            var query = db.MigrationPlanUnits.AsNoTracking().Where(unit => unit.TenantId == tenant.TenantId &&
                unit.MapRunId == map.RunId);
            if (after is not null) query = query.Where(unit => unit.PlanUnitId.CompareTo(after.Value) > 0);
            var rows = await query.OrderBy(unit => unit.PlanUnitId).Take(20_000).Select(unit => new
            {
                unit.PlanUnitId,
                CardStatus = (from sourceRow in db.MigrationUnitCandidateSources
                    join card in db.Set<EquipmentCardStagingRow>()
                        on new { sourceRow.TenantId, BatchId = run.BatchId, SourcePk = sourceRow.SourceKey }
                        equals new { card.TenantId, card.BatchId, SourcePk = card.SourcePk }
                    where sourceRow.TenantId == tenant.TenantId && sourceRow.CandidateId == unit.CandidateId &&
                        sourceRow.SourceKind == CandidateSourceKind.EquipmentCard &&
                        sourceRow.Role == CandidateSourceRole.Kept
                    select card.Status).FirstOrDefault(),
                LastMovement = db.MigrationPlanEvents.Where(evt => evt.TenantId == tenant.TenantId &&
                        evt.PlanUnitId == unit.PlanUnitId).OrderByDescending(evt => evt.Sequence)
                    .Select(evt => evt.MovementKind).FirstOrDefault(),
            }).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var bucket = rules.First(rule => (rule.CardStatus is null || string.Equals(rule.CardStatus,
                    row.CardStatus, StringComparison.OrdinalIgnoreCase)) && (rule.LastMovementKind is null ||
                    rule.LastMovementKind == row.LastMovement)).Grade;
                source[bucket] = source.GetValueOrDefault(bucket) + 1;
            }
            after = rows[^1].PlanUnitId;
        }
        var buckets = source.Keys.Union(target.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var differences = buckets.Where(x => source.GetValueOrDefault(x) != target.GetValueOrDefault(x)).ToList();
        var check = AddCheck(run, "status_histogram", "Status histogram", "zero", source.Values.Sum(),
            target.Values.Sum(), differences.Count == 0 ? ValidationResult.Pass : ValidationResult.Fail,
            "Source card status mapped by the approved condition rules and compared per target unit-state bucket.");
        foreach (var bucket in buckets)
        {
            AddPart(check, "source_" + SafeCode(bucket), $"Source {bucket}", source.GetValueOrDefault(bucket));
            if (source.GetValueOrDefault(bucket) != target.GetValueOrDefault(bucket))
                AddDifference(check, "status_bucket", bucket, null, null, source.GetValueOrDefault(bucket),
                    target.GetValueOrDefault(bucket), "Condition or state bucket");
        }
    }

    private async Task AddSignedQuantityCheckAsync(MigrationValidationRun run, MigrationMapRun map,
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        var check = AddCheck(run, "signed_quantity", "Signed quantity per serial", "zero", 0, 0,
            ValidationResult.Pass,
            "Signed stock-affecting transaction quantities summed per serial and compared with source quantity on hand.");
        var units = from unit in db.MigrationPlanUnits.AsNoTracking()
            join candidate in db.MigrationUnitCandidates.AsNoTracking()
                on new { unit.TenantId, unit.CandidateId } equals
                new { candidate.TenantId, candidate.CandidateId }
            join master in db.Set<SerialMasterStagingRow>().AsNoTracking()
                on new { candidate.TenantId, BatchId = run.BatchId, SourcePk = candidate.PrimarySourceKey }
                equals new { master.TenantId, master.BatchId, SourcePk = master.SourcePk }
            where unit.TenantId == tenant.TenantId && unit.MapRunId == map.RunId &&
                master.ItemCode != null && master.SystemNumber != null
            select new { Item = master.ItemCode!, System = master.SystemNumber!.Value, master.SerialNumber };
        var total = await units.LongCountAsync(cancellationToken);
        var mismatchRows = await sourceReader.SignedQuantityMismatchesAsync(run.BatchId, map.RunId, sourceSystem,
            cancellationToken);
        foreach (var row in mismatchRows)
            AddDifference(check, "serial_quantity", row.Item, null, row.Serial, row.SourceQuantity,
                row.MovementQuantity, null);
        var mismatches = mismatchRows.Count;
        check.SourceTotal = total;
        check.TargetTotal = total - mismatches;
        check.Difference = -mismatches;
        check.Result = mismatches == 0 ? ValidationResult.Pass : ValidationResult.Fail;
    }

    private async Task AddSampleAsync(MigrationValidationRun run, MigrationMapRun map, MigrationCanonRun canon,
        ValidationSettingsView configured, CancellationToken cancellationToken)
    {
        var candidates = from unit in db.MigrationPlanUnits.AsNoTracking()
            join candidate in db.MigrationUnitCandidates.AsNoTracking()
                on new { unit.TenantId, unit.CandidateId } equals new { candidate.TenantId, candidate.CandidateId }
            join master in db.Set<SerialMasterStagingRow>().AsNoTracking()
                on new { candidate.TenantId, BatchId = run.BatchId, SourcePk = candidate.PrimarySourceKey } equals
                new { master.TenantId, master.BatchId, SourcePk = master.SourcePk }
            where unit.TenantId == tenant.TenantId && unit.MapRunId == map.RunId
            select new SampleCandidate
            {
                PlanUnitId = unit.PlanUnitId, CandidateId = candidate.CandidateId,
                StoredSerial = candidate.StoredSerial, CanonicalSerial = candidate.CanonicalSerial,
                ItemCode = candidate.ItemCode, Family = candidate.Family, InboundDate = master.InboundDate,
            };
        var byStratum = new Dictionary<(string Family, string Age), List<RankedSampleCandidate>>();
        var global = new List<RankedSampleCandidate>();
        var candidateCount = 0;
        await foreach (var candidate in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            candidateCount++;
            var ranked = new RankedSampleCandidate(candidate, SampleRank(run.SampleSeed, candidate.CandidateId));
            AddBest(global, ranked, configured.SampleSize);
            var key = (candidate.Family, AgeBand(candidate.InboundDate, map.CutoverDate,
                configured.AgeBandYears));
            if (!byStratum.TryGetValue(key, out var stratum)) byStratum[key] = stratum = [];
            AddBest(stratum, ranked, configured.SampleSize);
        }
        var perStratum = Math.Max(1, configured.SampleSize / Math.Max(1, byStratum.Count));
        var selected = byStratum.Values.OrderBy(rows => rows.Min(row => row.Rank))
            .SelectMany(rows => rows.OrderBy(row => row.Rank).Take(perStratum))
            .Concat(global.OrderBy(row => row.Rank)).DistinctBy(row => row.Candidate.CandidateId)
            .Take(Math.Min(configured.SampleSize, candidateCount)).OrderBy(row => row.Rank)
            .Select(row => row.Candidate).ToList();
        var check = AddCheck(run, "stratified_sample", "Stratified sample", "all", selected.Count, 0,
            selected.Count == 0 ? ValidationResult.Pass : ValidationResult.Fail,
            "Deterministic sample stratified by item family and configured age band; every source trail is compared beside its plan events and traced by a person.");
        var planUnitIds = selected.Select(row => row.PlanUnitId).ToList();
        var candidateIds = selected.Select(row => row.CandidateId).ToList();
        var targetTrails = await db.MigrationPlanEvents.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && planUnitIds.Contains(x.PlanUnitId))
            .OrderBy(x => x.PlanUnitId).ThenBy(x => x.Sequence).Select(x => new TargetTrailRow
            {
                PlanUnitId = x.PlanUnitId, EffectiveDate = x.EffectiveDate, MovementKind = x.MovementKind,
                DocumentNumber = x.DocumentNumber, Quantity = x.Quantity, WarehouseCode = x.WarehouseCode,
            }).ToListAsync(cancellationToken);
        var sourceTrails = await (from source in db.MigrationEventCandidates.AsNoTracking()
            join history in db.MigrationHistoryEvents.AsNoTracking()
                on new { source.TenantId, source.EventCandidateId } equals
                new { history.TenantId, EventCandidateId = history.EventCandidateId!.Value }
            where source.TenantId == tenant.TenantId && source.RunId == canon.RunId &&
                  candidateIds.Contains(source.CandidateId) && history.EventCandidateId != null
            orderby source.CandidateId, history.Sequence
            select new SourceTrailRow
            {
                CandidateId = source.CandidateId, EffectiveDate = history.EffectiveDate,
                MovementKind = history.MovementKind, DocumentNumber = history.DocumentNumber,
                Quantity = history.Quantity, Warehouse = history.Warehouse,
            }).ToListAsync(cancellationToken);
        var targetByUnit = targetTrails.ToLookup(row => row.PlanUnitId);
        var sourceByCandidate = sourceTrails.ToLookup(row => row.CandidateId);
        foreach (var row in selected)
        {
            db.MigrationValidationSamples.Add(new MigrationValidationSample
            {
                TenantId = tenant.TenantId, SampleId = Guid.CreateVersion7(clock.UtcNow), RunId = run.RunId,
                CheckId = check.CheckId, PlanUnitId = row.PlanUnitId,
                Serial = row.StoredSerial ?? row.CanonicalSerial, ItemCode = row.ItemCode,
                ItemFamily = row.Family, AgeBand = AgeBand(row.InboundDate, map.CutoverDate,
                    configured.AgeBandYears), SourceTrailJson = JsonSerializer.Serialize(sourceByCandidate[
                        row.CandidateId]), TargetTrailJson = JsonSerializer.Serialize(targetByUnit[row.PlanUnitId]),
            });
        }
        AddPart(check, "seed", "Deterministic seed", run.SampleSeed);
    }

    private MigrationValidationCheck AddCheck(MigrationValidationRun run, string code, string label,
        string limit, long? source, long? targetTotal, string result, string how, string? explanation = null)
    {
        var started = Stopwatch.GetTimestamp();
        var check = new MigrationValidationCheck
        {
            TenantId = tenant.TenantId, CheckId = Guid.CreateVersion7(clock.UtcNow), RunId = run.RunId,
            Code = code, Label = label, LimitKind = limit, SourceTotal = source, TargetTotal = targetTotal,
            Difference = source is null || targetTotal is null ? null : targetTotal - source,
            Result = result, Explanation = explanation, HowCounted = how, RanAt = clock.UtcNow,
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
        db.MigrationValidationChecks.Add(check);
        return check;
    }

    private void AddCoverPolicyWarning(MigrationValidationRun run, string kind, long count, string how) =>
        AddCheck(run, kind, PlanGapKind.Label(kind), "warning", count, null,
            count == 0 ? ValidationResult.Pass : ValidationResult.Warning, how);

    private void AddPart(MigrationValidationCheck check, string code, string label, decimal value) =>
        db.MigrationValidationCheckParts.Add(new MigrationValidationCheckPart
        {
            TenantId = tenant.TenantId, PartId = Guid.CreateVersion7(clock.UtcNow), CheckId = check.CheckId,
            Code = code, Label = label, Value = value,
        });

    private void AddDifference(MigrationValidationCheck check, string kind, string? item, string? warehouse,
        string? serial, decimal source, decimal targetValue, string? detail) =>
        db.MigrationValidationDifferences.Add(new MigrationValidationDifference
        {
            TenantId = tenant.TenantId, DifferenceId = Guid.CreateVersion7(clock.UtcNow), CheckId = check.CheckId,
            Kind = kind, ItemCode = item, WarehouseCode = warehouse, Serial = serial, SourceValue = source,
            TargetValue = targetValue, Difference = targetValue - source, Detail = detail,
        });

    private async Task CarrySignaturesAsync(MigrationValidationRun? previous, MigrationValidationRun current,
        CancellationToken cancellationToken)
    {
        if (previous is null) return;
        var oldChecks = await db.MigrationValidationChecks.Where(x => x.TenantId == tenant.TenantId &&
            x.RunId == previous.RunId).ToListAsync(cancellationToken);
        var oldIds = oldChecks.Select(x => x.CheckId).ToList();
        var oldSignatures = await db.MigrationValidationSignatures.Where(x => x.TenantId == tenant.TenantId &&
            oldIds.Contains(x.CheckId) && !x.Superseded).ToListAsync(cancellationToken);
        foreach (var check in db.MigrationValidationChecks.Local.Where(x => x.RunId == current.RunId).ToList())
        {
            var old = oldChecks.SingleOrDefault(x => x.Code == check.Code);
            if (old is null) continue;
            var signatures = oldSignatures.Where(x => x.CheckId == old.CheckId).ToList();
            if (old.ResultFingerprint == check.ResultFingerprint)
            {
                foreach (var signature in signatures) db.MigrationValidationSignatures.Add(new()
                {
                    TenantId = tenant.TenantId, SignatureId = Guid.CreateVersion7(clock.UtcNow),
                    CheckId = check.CheckId, Role = signature.Role, Actor = signature.Actor,
                    SignedAt = signature.SignedAt, CarriedFromSignatureId = signature.SignatureId,
                });
            }
            else foreach (var signature in signatures)
            {
                signature.Superseded = true;
                signature.SupersededAt = clock.UtcNow;
            }
        }
    }

    private async Task<MigrationMapRun> ReadyMapAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (!await db.MigrationBatches.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId &&
                x.BatchId == batchId, cancellationToken))
            throw new ValidationRuleException("migration_batch_not_found", "The migration batch was not found.");
        var runs = await db.MigrationMapRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.BatchId == batchId).OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.RunId)
            .Take(2).ToListAsync(cancellationToken);
        var latest = runs.FirstOrDefault();
        if (latest is null) throw new ValidationRuleException("migration_validation_map_missing",
            "Complete mapping for this batch before validation.");
        if (latest.Status == MappingRunStatus.Failed)
            throw new ValidationRuleException("migration_validation_map_failed",
                "The latest map run failed; complete a new map run before validation.");
        if (latest.Status != MappingRunStatus.Completed && runs.Skip(1).Any(x =>
                x.Status == MappingRunStatus.Completed))
            throw new ValidationRuleException("migration_validation_map_superseded",
                "The completed map run was superseded by a newer map run that must finish before validation.");
        if (latest.Status != MappingRunStatus.Completed)
            throw new ValidationRuleException("migration_validation_map_not_completed",
                "The latest map run has not completed.");
        return latest;
    }

    private async Task<MigrationValidationView> ViewAsync(MigrationValidationRun run,
        CancellationToken cancellationToken)
    {
        var checks = await db.MigrationValidationChecks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.RunId == run.RunId).ToListAsync(cancellationToken);
        checks = checks.OrderBy(x => CheckPositions.GetValueOrDefault(x.Code, int.MaxValue)).ToList();
        var signatures = await SignaturesAsync(checks.Select(x => x.CheckId).ToList(), cancellationToken);
        var views = new List<ValidationCheckView>();
        foreach (var check in checks) views.Add(await CheckViewAsync(check,
            signatures.GetValueOrDefault(check.CheckId, []), cancellationToken));
        var unit = checks.SingleOrDefault(x => x.Code == "unit_master");
        var history = checks.SingleOrDefault(x => x.Code == "historical_events");
        var samples = await db.MigrationValidationSamples.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.RunId == run.RunId).Select(x => x.Outcome).ToListAsync(cancellationToken);
        var hard = checks.Where(x => x.Result == ValidationResult.HardFail).ToList();
        var rehearsalNumber = run.Rehearsal
            ? await db.MigrationValidationRuns.AsNoTracking().CountAsync(x => x.TenantId == tenant.TenantId &&
                x.Rehearsal && (x.RequestedAt < run.RequestedAt ||
                    (x.RequestedAt == run.RequestedAt && x.RunId.CompareTo(run.RunId) <= 0)), cancellationToken)
            : (int?)null;
        var loadDate = run.TargetKind == ValidationTargetKind.Module
            ? await db.Set<MigrationApplyRun>().AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                    x.MapRunId == run.MapRunId && x.FinishedAt != null &&
                    (x.Status == ApplyRunStatus.Completed || x.Status == ApplyRunStatus.RolledBack))
                .OrderByDescending(x => x.FinishedAt).ThenByDescending(x => x.RunId)
                .Select(x => x.FinishedAt).FirstOrDefaultAsync(cancellationToken)
            : null;
        var signoff = await db.MigrationSignoffRecords.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.RunId == run.RunId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.RecordId)
            .Select(x => new { x.RecordId, x.Rehearsal, x.CreatedAt, x.CreatedBy })
            .FirstOrDefaultAsync(cancellationToken);
        var hasFinalSignoff = await db.MigrationSignoffRecords.AsNoTracking().AnyAsync(record =>
            record.TenantId == tenant.TenantId && !record.Rehearsal &&
            db.MigrationValidationRuns.AsNoTracking().Any(validation => validation.TenantId == record.TenantId &&
                validation.RunId == record.RunId && validation.MapRunId == run.MapRunId && !validation.Rehearsal),
            cancellationToken);
        var gapRows = await db.MigrationPlanGaps.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
                x.MapRunId == run.MapRunId).GroupBy(x => x.Kind)
            .Select(group => new { Kind = group.Key, Count = group.LongCount() }).OrderBy(x => x.Kind)
            .ToListAsync(cancellationToken);
        var gaps = gapRows.Select(x => new MigrationGapCount(x.Kind, PlanGapKind.Label(x.Kind), x.Count)).ToList();
        var signoffNames = await ResolveNamesAsync(signoff is null ? [] : [signoff.CreatedBy], cancellationToken);
        return new(run.RunId, run.BatchId, run.MapRunId, run.TargetKind, run.Rehearsal, rehearsalNumber,
            run.Status, run.StartedAt, run.FinishedAt, loadDate, signoff?.RecordId, signoff?.Rehearsal,
            signoff?.CreatedAt, signoff is null ? null : Name(signoffNames, signoff.CreatedBy), hasFinalSignoff,
            run.FailureCode,
            run.FailureDetail, checks.Count, checks.Count(x => x.Result == ValidationResult.Pass),
            checks.Count(x => x.Result == ValidationResult.Explained),
            checks.Count(x => x.Result is ValidationResult.Fail or ValidationResult.HardFail), hard.Count,
            checks.Count(x => signatures.GetValueOrDefault(x.CheckId, []).Count(s => !s.Superseded) == 2),
            unit?.TargetTotal ?? 0, history?.TargetTotal ?? 0,
            history?.SourceTotal > 0 ? Math.Abs(history.Difference ?? 0) * 100m / history.SourceTotal : 0,
            samples.Count(x => x == "traced"), samples.Count,
            hard.Count == 0 ? null : $"{hard.Count} hard fail{(hard.Count == 1 ? "" : "s")} stop the cutover. Fix in the source application; the next extraction and rehearsal check again.",
            gaps, views);
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> ExportRows(
        IReadOnlyList<ValidationCheckView> checks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparer = check.Signatures.FirstOrDefault(x => x.Role == ValidationSignatureRole.Preparer);
            var reviewer = check.Signatures.FirstOrDefault(x => x.Role == ValidationSignatureRole.Reviewer);
            yield return [check.Label, LimitLabel(check.LimitKind), Text(check.SourceTotal),
                Text(check.TargetTotal), Text(check.Difference), ResultLabel(check.Result), check.Explanation,
                check.ExplainedByName, Text(check.ExplainedAt), check.HowCounted, Text(check.RanAt),
                check.DurationMilliseconds.ToString(CultureInfo.InvariantCulture), preparer?.ActorName,
                Text(preparer?.SignedAt), reviewer?.ActorName, Text(reviewer?.SignedAt)];
        }
    }

    private static string LimitLabel(string value)
    {
        if (value == "zero") return "Zero";
        if (value == "none") return "None";
        if (value == "all") return "All";
        return value.StartsWith('<') ? $"Less than {value[1..].Trim()}" : value;
    }

    private static string ResultLabel(string value) => value switch
    {
        ValidationResult.Pass => "Pass",
        ValidationResult.Explained => "Explained",
        ValidationResult.Fail => "Fail",
        ValidationResult.HardFail => "Hard fail",
        ValidationResult.Report => "Report",
        ValidationResult.Warning => "Warning",
        _ => value,
    };

    private static string? Text<T>(T? value) => value switch
    {
        null => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private async Task<ValidationCheckView> CheckViewAsync(MigrationValidationCheck check,
        IReadOnlyList<ValidationSignatureView> signatures, CancellationToken cancellationToken)
    {
        var names = await ResolveNamesAsync([check.ExplainedBy], cancellationToken);
        return new(check.CheckId, check.Code, check.Label, check.LimitKind, check.SourceTotal,
            check.TargetTotal, check.Difference, check.Result, check.Explanation,
            Name(names, check.ExplainedBy), check.ExplainedAt, check.HowCounted,
            check.RanAt, check.DurationMilliseconds, signatures.Where(x => !x.Superseded).ToList());
    }

    private async Task<Dictionary<Guid, IReadOnlyList<ValidationSignatureView>>> SignaturesAsync(
        IReadOnlyCollection<Guid> checkIds, CancellationToken cancellationToken)
    {
        var rows = await db.MigrationValidationSignatures.AsNoTracking().Where(x =>
            x.TenantId == tenant.TenantId && checkIds.Contains(x.CheckId)).OrderBy(x => x.SignedAt)
            .ToListAsync(cancellationToken);
        var names = await ResolveNamesAsync(rows.Select(x => x.Actor), cancellationToken);
        return rows.GroupBy(x => x.CheckId).ToDictionary(g => g.Key, g => (IReadOnlyList<ValidationSignatureView>)
            g.Select(x => new ValidationSignatureView(x.Role, Name(names, x.Actor)!, x.SignedAt,
                x.Superseded, x.SupersededAt)).ToList());
    }

    private async Task<MigrationValidationCheck> RequiredCheckAsync(Guid checkId,
        CancellationToken cancellationToken) => await db.MigrationValidationChecks.SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.CheckId == checkId, cancellationToken) ??
        throw new ValidationRuleException("migration_validation_check_not_found", "The validation check was not found.");

    private async Task VoidSignaturesAsync(Guid checkId, CancellationToken cancellationToken)
    {
        var rows = await db.MigrationValidationSignatures.Where(x => x.TenantId == tenant.TenantId &&
            x.CheckId == checkId && !x.Superseded).ToListAsync(cancellationToken);
        foreach (var row in rows) { row.Superseded = true; row.SupersededAt = clock.UtcNow; }
    }

    private async Task<string> ResultFingerprintAsync(MigrationValidationCheck check,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(string.Join('|', check.Code, check.SourceTotal, check.TargetTotal, check.Difference,
            check.Result, check.Explanation));
        await foreach (var part in db.MigrationValidationCheckParts.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == check.CheckId).OrderBy(x => x.Code)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
            Append($"P|{part.Code}|{part.Value}");
        await foreach (var difference in db.MigrationValidationDifferences.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == check.CheckId).OrderBy(x => x.Kind)
            .ThenBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode).ThenBy(x => x.Serial)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
            Append($"D|{difference.Kind}|{difference.ItemCode}|{difference.WarehouseCode}|{difference.Serial}|" +
                $"{difference.SourceValue}|{difference.TargetValue}|{difference.Difference}|{difference.Detail}");
        await foreach (var sample in db.MigrationValidationSamples.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.CheckId == check.CheckId).OrderBy(x => x.PlanUnitId)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
            Append($"S|{sample.PlanUnitId}|{sample.Serial}|{sample.ItemCode}|{sample.ItemFamily}|{sample.AgeBand}|" +
                $"{sample.SourceTrailJson}|{sample.TargetTrailJson}|{sample.Outcome}");
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(IEnumerable<string?> values,
        CancellationToken cancellationToken)
    {
        var refs = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToList();
        return refs.Count == 0 ? new Dictionary<string, string>() :
            await people.DisplayNamesAsync(refs, cancellationToken);
    }

    private static string? Name(IReadOnlyDictionary<string, string> names, string? actor)
    {
        if (actor is null) return null;
        if (actor == "unsigned") return "Unsigned";
        return names.GetValueOrDefault(actor, "Unknown user");
    }
    private static int Seed(Guid mapRunId) => BitConverter.ToInt32(SHA256.HashData(mapRunId.ToByteArray()), 0) & int.MaxValue;
    private static string Fingerprint(MigrationValidationCheck check) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('|', check.Code, check.SourceTotal, check.TargetTotal, check.Difference,
            check.Result, check.Explanation))));
    private static long SampleRank(int seed, Guid id) => BitConverter.ToInt64(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{seed}:{id:D}")), 0) & long.MaxValue;
    private static void AddBest(List<RankedSampleCandidate> rows, RankedSampleCandidate candidate, int maximum)
    {
        rows.Add(candidate);
        if (rows.Count <= maximum) return;
        var worst = 0;
        for (var index = 1; index < rows.Count; index++)
            if (rows[index].Rank > rows[worst].Rank) worst = index;
        rows.RemoveAt(worst);
    }
    private static string AgeBand(DateTime? inbound, DateOnly cutover, IReadOnlyList<int> bands)
    {
        if (inbound is null) return "unknown";
        var years = (cutover.ToDateTime(TimeOnly.MinValue) - inbound.Value).TotalDays / 365.2425;
        for (var i = 0; i < bands.Count; i++) if (years < bands[i]) return i == 0 ? $"< {bands[i]} y" :
            $"{bands[i - 1]}–{bands[i]} y";
        return $"> {bands[^1]} y";
    }
    private static string SafeCode(string value) => new(value.ToLowerInvariant().Select(c =>
        char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private static string? Encode(Guid id) => Convert.ToBase64String(id.ToByteArray());
    private static Guid Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return Guid.Empty;
        try { return new Guid(Convert.FromBase64String(cursor)); }
        catch (FormatException) { throw new ValidationRuleException("migration_validation_cursor_invalid", "The paging cursor is invalid."); }
    }
    private static string Csv(object? value)
    {
        var text = value switch { null => "", DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        if (value is string && text.Length != 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private sealed class SampleCandidate
    {
        public Guid PlanUnitId { get; init; }
        public Guid CandidateId { get; init; }
        public string? StoredSerial { get; init; }
        public required string CanonicalSerial { get; init; }
        public required string ItemCode { get; init; }
        public required string Family { get; init; }
        public DateTime? InboundDate { get; init; }
    }

    private sealed record RankedSampleCandidate(SampleCandidate Candidate, long Rank);

    private sealed class TargetTrailRow
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Guid PlanUnitId { get; init; }
        public DateTime EffectiveDate { get; init; }
        public required string MovementKind { get; init; }
        public int? DocumentNumber { get; init; }
        public decimal Quantity { get; init; }
        public string? WarehouseCode { get; init; }
    }

    private sealed class SourceTrailRow
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Guid CandidateId { get; init; }
        public DateTime EffectiveDate { get; init; }
        public required string MovementKind { get; init; }
        public int? DocumentNumber { get; init; }
        public decimal Quantity { get; init; }
        public string? Warehouse { get; init; }
    }
}
