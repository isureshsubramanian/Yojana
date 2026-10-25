using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Backfills;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

public sealed class ErpChangeMasterDataService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    BackfillService backfills,
    InboundGateway gateway,
    IMigrationReconciliationPort units,
    IUserDirectory people)
{
    public async Task<RemapMasterDataResult> RemapAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.MasterData)
            throw new ErpChangeRuleException("erp_change_step_invalid", "Master data is re-mapped in step 2.");
        var entities = await db.Set<ConnectionPollWatermark>().AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == run.TargetConnectionId
                && (row.CanonicalMessage == nameof(ItemUpserted)
                    || row.CanonicalMessage == nameof(PartyUpserted)))
            .Select(row => row.EntityType).Distinct().ToListAsync(cancellationToken);
        var jobs = new List<Guid>();
        var through = DateOnly.FromDateTime(clock.UtcNow);
        foreach (var entity in entities)
        {
            var job = await backfills.StartAsync(new(run.TargetConnectionId, entity,
                new DateOnly(2000, 1, 1), through, 250), actor, cancellationToken);
            jobs.Add(job);
            await backfills.ProcessAsync(job, null, cancellationToken);
        }
        await TranslateTargetMasterDataAsync(run.TargetConnectionId, cancellationToken);

        var usage = (await units.MasterReferenceUsageAsync(cancellationToken))
            .ToDictionary(row => (row.Kind, row.ReferenceId), row => row.LiveUnits);
        var source = await ReferencesAsync(run.SourceConnectionId, cancellationToken);
        var target = await ReferencesAsync(run.TargetConnectionId, cancellationToken);
        var mappedItems = 0;
        var mappedParties = 0;
        var created = 0;
        foreach (var candidate in target)
        {
            var matches = candidate.Kind == "item"
                ? ItemMatches(candidate, source)
                : PartyMatches(candidate, source);
            if (matches.Count == 1)
            {
                await MapAsync(run, candidate.ExternalId, candidate.Kind, matches[0].ModuleId,
                    ExternalIdMappingSource.Migration, actor, cancellationToken);
                if (candidate.Kind == "item") mappedItems++; else mappedParties++;
                continue;
            }
            var liveUnits = matches.Sum(match => usage.GetValueOrDefault((candidate.Kind, match.ModuleId)));
            created += await UpsertExceptionAsync(run, candidate, matches,
                matches.Count == 0 ? MasterDataProblem.Unmatched : MasterDataProblem.Ambiguous,
                liveUnits, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        var representedRows = await db.ErpChangeMasterDataExceptions.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId
                && row.Problem == MasterDataProblem.Ambiguous
                && row.Status == ErpChangeExceptionStatus.Open)
            .Select(row => new { Kind = row.EntityKind, row.CandidateModuleIdsJson })
            .ToListAsync(cancellationToken);
        var representedSourceReferences = representedRows.SelectMany(row =>
                (JsonSerializer.Deserialize<List<Guid>>(row.CandidateModuleIdsJson, IntegrationContracts.Json) ?? [])
                .Select(moduleId => (row.Kind, ModuleId: moduleId)))
            .ToHashSet();
        var targetMappedIds = (await db.ExternalIdMaps.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ConnectionId == run.TargetConnectionId
                && row.ValidTo == null && (row.EntityType == "item" || row.EntityType == "party"))
            .Select(row => new { Kind = row.EntityType, row.ModuleId }).ToListAsync(cancellationToken))
            .Select(row => (row.Kind, row.ModuleId)).ToHashSet();
        foreach (var sourceReference in source.Where(reference =>
                     usage.GetValueOrDefault((reference.Kind, reference.ModuleId)) > 0
                     && !targetMappedIds.Contains((reference.Kind, reference.ModuleId))
                     && !representedSourceReferences.Contains((reference.Kind, reference.ModuleId))))
        {
            var missing = sourceReference with
            {
                ExternalId = $"missing:{sourceReference.ExternalId}",
            };
            created += await UpsertExceptionAsync(run, missing, [sourceReference],
                MasterDataProblem.MissingInTarget,
                usage[(sourceReference.Kind, sourceReference.ModuleId)], cancellationToken);
        }
        run.MasterDataRemappedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var open = await db.ErpChangeMasterDataExceptions.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.Status == ErpChangeExceptionStatus.Open, cancellationToken);
        return new(runId, jobs, mappedItems, mappedParties, open, created);
    }

    private async Task TranslateTargetMasterDataAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var messageTypes = new[] { nameof(ItemUpserted), nameof(BomUpserted), nameof(PartyUpserted) };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pending = await db.InboundMessages.AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.ConnectionId == connectionId && row.IsLatest
                    && messageTypes.Contains(row.MessageType)
                    && row.TranslationStatus != InboundTranslationStatus.Completed
                    && row.TranslationStatus != InboundTranslationStatus.Superseded)
                .OrderBy(row => row.AcceptedAt).ThenBy(row => row.InboundMessageId)
                .Select(row => new { row.InboundMessageId, row.TranslationStatus })
                .ToListAsync(cancellationToken);
            if (pending.Count == 0) return;
            if (pending.Any(row => row.TranslationStatus is InboundTranslationStatus.NeedsAttention
                    or InboundTranslationStatus.Held))
                break;
            foreach (var row in pending.Where(row => row.TranslationStatus is
                         InboundTranslationStatus.Pending or InboundTranslationStatus.Failed))
                await gateway.TranslateAsync(row.InboundMessageId, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        throw new ErpChangeRuleException("erp_change_master_data_processing",
            "Master-data translation is still processing or needs attention; retry after clearing its inbox state.");
    }

    public async Task<MasterDataExceptionPage> ListAsync(Guid runId, MasterDataExceptionListRequest request,
        CancellationToken cancellationToken)
    {
        _ = await RunAsync(runId, cancellationToken);
        var take = Math.Clamp(request.Take, 1, 100);
        var query = db.ErpChangeMasterDataExceptions.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId);
        if (!string.IsNullOrWhiteSpace(request.Kind)) query = query.Where(row => row.EntityKind == request.Kind);
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(row => row.Status == request.Status);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var text = request.Query.Trim();
            query = query.Where(row => row.TargetExternalId.Contains(text) || row.DisplayName.Contains(text)
                || (row.TaxId != null && row.TaxId.Contains(text)));
        }
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(row => row.LiveUnitsAffected < cursor.LiveUnits
                || (row.LiveUnitsAffected == cursor.LiveUnits
                    && row.ErpChangeMasterDataExceptionId.CompareTo(cursor.Id) > 0));
        var rows = await query.OrderByDescending(row => row.LiveUnitsAffected)
            .ThenBy(row => row.ErpChangeMasterDataExceptionId).Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var pageRows = rows.Take(take).ToList();
        var actorNames = await NamesAsync(pageRows.Select(row => row.ResolvedBy), cancellationToken);
        var page = pageRows.Select(row => View(row, actorNames)).ToList();
        var all = db.ErpChangeMasterDataExceptions.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId);
        var source = await RunAsync(runId, cancellationToken);
        var targetMaps = db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == source.TargetConnectionId && row.ValidTo == null);
        var used = await units.MasterReferenceUsageAsync(cancellationToken);
        var itemIds = used.Where(row => row.Kind == "item").Select(row => row.ReferenceId).ToList();
        var partyIds = used.Where(row => row.Kind == "party").Select(row => row.ReferenceId).ToList();
        var itemMapped = await targetMaps.CountAsync(row => row.EntityType == "item"
            && itemIds.Contains(row.ModuleId), cancellationToken);
        var partyMapped = await targetMaps.CountAsync(row => row.EntityType == "party"
            && partyIds.Contains(row.ModuleId), cancellationToken);
        return new(page, more && page.Count != 0 ? Encode(new(page[^1].LiveUnitsAffected,
                page[^1].ExceptionId)) : null,
            itemMapped, partyMapped,
            await all.CountAsync(row => row.Status == ErpChangeExceptionStatus.Open, cancellationToken),
            await all.CountAsync(row => row.Problem == MasterDataProblem.Ambiguous, cancellationToken));
    }

    public async Task<MasterDataExceptionRow> ResolveAsync(Guid runId, Guid exceptionId,
        ResolveMasterDataExceptionRequest request, string actor, bool candidateOnly,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        var row = await ExceptionAsync(runId, exceptionId, cancellationToken);
        if (row.Status != ErpChangeExceptionStatus.Open)
            return View(row, await NamesAsync([row.ResolvedBy], cancellationToken));
        var candidates = JsonSerializer.Deserialize<List<Guid>>(row.CandidateModuleIdsJson,
            IntegrationContracts.Json) ?? [];
        if (candidateOnly && !candidates.Contains(request.ModuleId))
            throw new ErpChangeRuleException("erp_change_candidate_invalid", "Choose one of the recorded candidates.");
        if (!await db.ReferenceProjections.AsNoTracking().AnyAsync(reference =>
                reference.TenantId == tenant.TenantId && reference.EntityType == row.EntityKind
                && reference.ModuleId == request.ModuleId && !reference.Deleted, cancellationToken))
            throw new ErpChangeRuleException("erp_change_reference_not_found", "The module reference was not found.");
        if (row.Problem != MasterDataProblem.MissingInTarget)
            await MapAsync(run, row.TargetExternalId, row.EntityKind, request.ModuleId,
                ExternalIdMappingSource.Operator, actor, cancellationToken);
        row.Status = ErpChangeExceptionStatus.Mapped;
        row.ResolvedModuleId = request.ModuleId;
        row.ResolvedBy = actor;
        row.ResolvedAt = clock.UtcNow;
        row.ResolutionNote = LimitNote(request.Note);
        await db.SaveChangesAsync(cancellationToken);
        return View(row, await NamesAsync([row.ResolvedBy], cancellationToken));
    }

    public async Task<MasterDataExceptionRow> MarkNotNeededAsync(Guid runId, Guid exceptionId,
        MarkMasterDataNotNeededRequest request, string actor, CancellationToken cancellationToken)
    {
        _ = await ActiveRunAsync(runId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ErpChangeRuleException("erp_change_note_required", "A note of at most 2,000 characters is required.");
        var row = await ExceptionAsync(runId, exceptionId, cancellationToken);
        if (row.Status == ErpChangeExceptionStatus.Open)
        {
            row.Status = ErpChangeExceptionStatus.NotNeeded;
            row.ResolvedBy = actor;
            row.ResolvedAt = clock.UtcNow;
            row.ResolutionNote = request.Note.Trim();
            await db.SaveChangesAsync(cancellationToken);
        }
        return View(row, await NamesAsync([row.ResolvedBy], cancellationToken));
    }

    private async Task<List<MasterReference>> ReferencesAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var rows = await db.ExternalIdMaps.AsNoTracking().Where(map => map.TenantId == tenant.TenantId
                && map.ConnectionId == connectionId && map.ValidTo == null
                && (map.EntityType == "item" || map.EntityType == "party"))
            .Join(db.ReferenceProjections.AsNoTracking(), map => new { map.TenantId, map.EntityType, map.ModuleId },
                reference => new { reference.TenantId, reference.EntityType, reference.ModuleId },
                (map, reference) => new ReferenceProjectionRow
                {
                    ExternalId = map.ExternalId,
                    Kind = map.EntityType,
                    ModuleId = map.ModuleId,
                    Code = reference.Code,
                    DisplayName = reference.DisplayName,
                    AttributesJson = reference.AttributesJson,
                    Deleted = reference.Deleted,
                }).Where(row => !row.Deleted).ToListAsync(cancellationToken);
        return rows.Select(row =>
        {
            string? taxId = null;
            string? country = null;
            string? manufacturerCode = row.Code;
            if (row.Kind == "party")
            {
                var attributes = JsonSerializer.Deserialize<Gateway.PartyReferenceAttributes>(row.AttributesJson,
                    IntegrationContracts.Json);
                taxId = attributes?.TaxId;
                country = attributes?.Country;
            }
            else
            {
                var attributes = JsonSerializer.Deserialize<Gateway.ItemReferenceAttributes>(row.AttributesJson,
                    IntegrationContracts.Json);
                if (!string.IsNullOrWhiteSpace(attributes?.ManufacturerItemCode))
                    manufacturerCode = attributes.ManufacturerItemCode;
            }
            return new MasterReference(row.ExternalId, row.Kind, row.ModuleId, row.Code ?? row.ExternalId,
                row.DisplayName ?? row.ExternalId, manufacturerCode ?? row.ExternalId, taxId, country);
        }).ToList();
    }

    private static List<MasterReference> ItemMatches(MasterReference target,
        IReadOnlyList<MasterReference> source)
    {
        var code = ErpChangeNaturalKeys.ItemCode(target.ManufacturerItemCode);
        var sameCode = source.Where(reference => reference.Kind == "item"
            && ErpChangeNaturalKeys.ItemCode(reference.ManufacturerItemCode) == code).ToList();
        if (sameCode.Count <= 1) return sameCode;
        var description = ErpChangeNaturalKeys.Text(target.DisplayName);
        var sameDescription = sameCode.Where(reference =>
            ErpChangeNaturalKeys.Text(reference.DisplayName) == description).ToList();
        return sameDescription.Count > 0 ? sameDescription : sameCode;
    }

    private static List<MasterReference> PartyMatches(MasterReference target,
        IReadOnlyList<MasterReference> source)
    {
        var country = ErpChangeNaturalKeys.Country(target.Country);
        var tax = ErpChangeNaturalKeys.TaxId(target.TaxId);
        var candidates = source.Where(reference => reference.Kind == "party"
            && ErpChangeNaturalKeys.Country(reference.Country) == country);
        if (tax.Length > 0)
            return candidates.Where(reference => ErpChangeNaturalKeys.TaxId(reference.TaxId) == tax).ToList();
        var name = ErpChangeNaturalKeys.Text(target.DisplayName);
        return candidates.Where(reference => ErpChangeNaturalKeys.Text(reference.DisplayName) == name).ToList();
    }

    private async Task MapAsync(ErpChangeRun run, string externalId, string kind, Guid moduleId,
        string source, string actor, CancellationToken cancellationToken)
    {
        var map = await db.ExternalIdMaps.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId && row.EntityType == kind
            && row.ExternalId == externalId && row.ValidTo == null, cancellationToken);
        if (map?.ModuleId == moduleId) return;
        var now = clock.UtcNow;
        if (map is not null) map.ValidTo = now;
        db.ExternalIdMaps.Add(new ExternalIdMap
        {
            TenantId = tenant.TenantId,
            ExternalIdMapId = TimeOrderedId.New(now),
            ConnectionId = run.TargetConnectionId,
            EntityType = kind,
            ExternalId = externalId,
            NaturalKeyHash = NaturalHash(kind, externalId),
            ModuleId = moduleId,
            MappingSource = source,
            MappedBy = actor,
            ValidFrom = now,
            MappedAt = now,
        });
    }

    private async Task<int> UpsertExceptionAsync(ErpChangeRun run, MasterReference target,
        IReadOnlyList<MasterReference> candidates, string problem, int liveUnits,
        CancellationToken cancellationToken)
    {
        var row = await db.ErpChangeMasterDataExceptions.SingleOrDefaultAsync(value =>
            value.TenantId == tenant.TenantId && value.ErpChangeRunId == run.ErpChangeRunId
            && value.EntityKind == target.Kind && value.TargetExternalId == target.ExternalId,
            cancellationToken);
        if (row is null)
        {
            row = new()
            {
                TenantId = tenant.TenantId,
                ErpChangeMasterDataExceptionId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = run.ErpChangeRunId,
                EntityKind = target.Kind,
                TargetExternalId = target.ExternalId,
                DisplayName = target.DisplayName,
                Problem = problem,
                CandidateModuleIdsJson = "[]",
                CandidateLabelsJson = "[]",
                Status = ErpChangeExceptionStatus.Open,
                CreatedAt = clock.UtcNow,
            };
            db.ErpChangeMasterDataExceptions.Add(row);
        }
        else if (row.Status != ErpChangeExceptionStatus.Open) return 0;
        row.DisplayName = target.DisplayName;
        row.TaxId = target.TaxId;
        row.Country = target.Country;
        row.Problem = problem;
        row.LiveUnitsAffected = liveUnits;
        row.CandidateModuleIdsJson = JsonSerializer.Serialize(candidates.Select(value => value.ModuleId),
            IntegrationContracts.Json);
        row.CandidateLabelsJson = JsonSerializer.Serialize(candidates.Select(value => value.DisplayName),
            IntegrationContracts.Json);
        return db.Entry(row).State == EntityState.Added ? 1 : 0;
    }

    private async Task<ErpChangeRun> ActiveRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        if (run.Status != ErpChangeRunStatus.Active)
            throw new ErpChangeRuleException("erp_change_run_closed", "The ERP change run is closed.");
        return run;
    }

    private async Task<ErpChangeRun> RunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The ERP change run was not found.");

    private async Task<ErpChangeMasterDataException> ExceptionAsync(Guid runId, Guid exceptionId,
        CancellationToken cancellationToken) =>
        await db.ErpChangeMasterDataExceptions.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.ErpChangeMasterDataExceptionId == exceptionId,
            cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_exception_not_found", "The master-data exception was not found.");

    private static MasterDataExceptionRow View(ErpChangeMasterDataException row,
        IReadOnlyDictionary<string, string> names)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(row.CandidateModuleIdsJson, IntegrationContracts.Json) ?? [];
        var labels = JsonSerializer.Deserialize<List<string>>(row.CandidateLabelsJson, IntegrationContracts.Json) ?? [];
        return new(row.ErpChangeMasterDataExceptionId, row.EntityKind, row.TargetExternalId,
            row.DisplayName, row.TaxId, row.Country, row.Problem,
            ids.Select((id, index) => new MasterDataCandidate(id,
                index < labels.Count ? labels[index] : "Module record")).ToList(),
            row.LiveUnitsAffected, row.Status, row.ResolvedModuleId,
            row.ResolvedBy is null ? null : names.GetValueOrDefault(row.ResolvedBy) ?? "Former or system user",
            row.ResolvedAt,
            row.ResolutionNote);
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(IEnumerable<string?> actors,
        CancellationToken cancellationToken)
    {
        var references = actors.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct().Take(IUserDirectory.MaxReferences).ToList();
        return references.Count == 0 ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync(references, cancellationToken);
    }

    private static string? LimitNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        if (note.Length > 2000)
            throw new ErpChangeRuleException("erp_change_note_invalid", "The note must not exceed 2,000 characters.");
        return note.Trim();
    }

    private static string NaturalHash(string kind, string externalId) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{kind.Trim().ToUpperInvariant()}\n{externalId.Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    private static string Encode(ExceptionCursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{cursor.LiveUnits}|{cursor.Id:D}"));

    private static ExceptionCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], out var units) && Guid.TryParse(parts[1], out var id)
                ? new(units, id) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record ExceptionCursor(int LiveUnits, Guid Id);
    private sealed record MasterReference(string ExternalId, string Kind, Guid ModuleId, string Code,
        string DisplayName, string ManufacturerItemCode, string? TaxId, string? Country);

    private sealed class ReferenceProjectionRow
    {
        public required string ExternalId { get; init; }
        public required string Kind { get; init; }
        public Guid ModuleId { get; init; }
        public string? Code { get; init; }
        public string? DisplayName { get; init; }
        public required string AttributesJson { get; init; }
        public bool Deleted { get; init; }
    }
}
