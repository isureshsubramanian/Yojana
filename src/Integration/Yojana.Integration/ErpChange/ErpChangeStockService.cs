using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Backfills;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

public sealed class ErpChangeStockService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    BackfillService backfills,
    IMigrationReconciliationPort serialisation,
    IUserDirectory people)
{
    public async Task<ReconcileStockResult> ReconcileAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.Stock)
            throw new ErpChangeRuleException("erp_change_step_invalid", "Stock is reconciled in step 3.");
        await BackfillTargetStockAsync(run.TargetConnectionId, actor, cancellationToken);
        var module = await ReadAllUnitsAsync(cancellationToken);
        var source = await PositionsAsync(run.SourceConnectionId, cancellationToken);
        var target = await PositionsAsync(run.TargetConnectionId, cancellationToken);
        var sourceLocations = await LocationCodesAsync(run.SourceConnectionId, cancellationToken);
        var targetLocations = await LocationCodesAsync(run.TargetConnectionId, cancellationToken);
        var itemNames = await db.ReferenceProjections.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.EntityType == "item")
            .GroupBy(row => row.ModuleId).Select(group => new ItemNameProjection
            {
                ItemId = group.Key,
                Name = group.Select(row => row.DisplayName).FirstOrDefault(),
            }).ToDictionaryAsync(row => row.ItemId, row => row.Name, cancellationToken);
        var targetItems = await db.ExternalIdMaps.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ConnectionId == run.TargetConnectionId
                && row.EntityType == "item" && row.ValidTo == null)
            .ToDictionaryAsync(row => row.ExternalId, row => row.ModuleId,
                StringComparer.Ordinal, cancellationToken);
        var now = clock.UtcNow;
        run.UnitsCompared = module.Count;
        run.StockComparedAt = now;
        var matchedTarget = new HashSet<string>(StringComparer.Ordinal);
        var differences = new List<ErpChangeStockDifference>();
        var normalised = 0;
        foreach (var unit in module)
        {
            var key = ErpChangeNaturalKeys.Serial(unit.Identifier);
            source.TryGetValue(key, out var sourcePosition);
            target.TryGetValue(key, out var targetPosition);
            if (targetPosition is not null) matchedTarget.Add(key);
            var moduleLocation = unit.LocationId is { } locationId
                ? targetLocations.GetValueOrDefault(locationId)
                    ?? sourceLocations.GetValueOrDefault(locationId) ?? unit.LocationKind
                : unit.LocationKind;
            string? reason = null;
            var status = ErpChangeDifferenceStatus.Open;
            var suggested = "fix_in_new_erp";
            if (targetPosition is null) reason = StockDifferenceReason.MissingInTarget;
            else if (!string.Equals(unit.Identifier.Trim(), targetPosition.Serial.Trim(),
                         StringComparison.Ordinal))
            {
                reason = StockDifferenceReason.SerialDiffersByNormalisation;
                status = ErpChangeDifferenceStatus.Resolved;
                suggested = "none_same_after_normalising";
                normalised++;
            }
            else if (!SameLocation(moduleLocation, targetPosition.Location))
            {
                reason = StockDifferenceReason.LocationDiffers;
                if (sourcePosition is not null && SameLocation(sourcePosition.Location, targetPosition.Location)
                    && !SameLocation(moduleLocation, sourcePosition.Location))
                    suggested = StockResolutionCommand.ChangeCustody;
            }
            if (reason is null) continue;
            differences.Add(Difference(runId, unit.UnitId, unit.ItemId, unit.Identifier,
                itemNames.GetValueOrDefault(unit.ItemId), reason, moduleLocation, sourcePosition?.Location,
                targetPosition?.Location, unit.Disposition, sourcePosition?.Status, targetPosition?.Status,
                unit.HasLiveEntitlement, unit.WarrantyEnd, suggested, status, now,
                status == ErpChangeDifferenceStatus.Resolved ? "normalisation" : null,
                status == ErpChangeDifferenceStatus.Resolved ? actor : null));
        }
        var moduleBySerial = module.ToDictionary(row => ErpChangeNaturalKeys.Serial(row.Identifier),
            StringComparer.Ordinal);
        foreach (var extra in target.Where(pair => !matchedTarget.Contains(pair.Key)
                     && !moduleBySerial.ContainsKey(pair.Key)))
        {
            var itemId = targetItems.GetValueOrDefault(extra.Value.ItemExternalId);
            differences.Add(Difference(runId, null, itemId == Guid.Empty ? null : itemId,
                extra.Value.Serial, itemId == Guid.Empty ? null : itemNames.GetValueOrDefault(itemId),
                StockDifferenceReason.ExtraInTarget, null, null, extra.Value.Location, null, null,
                extra.Value.Status, false, null, "receive_or_ignore", ErpChangeDifferenceStatus.Open,
                now, null, null));
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ErpChangeStockDifferences.Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.IsCurrent)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsCurrent, false), cancellationToken);
        await db.ErpChangeStockSignoffs.Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId)
            .ExecuteDeleteAsync(cancellationToken);
        db.ErpChangeStockDifferences.AddRange(differences);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(runId, module.Count, differences.Count,
            differences.Count(row => row.Status == ErpChangeDifferenceStatus.Open
                && row.HasLiveEntitlement), normalised, now);
    }

    private async Task BackfillTargetStockAsync(Guid connectionId, string actor,
        CancellationToken cancellationToken)
    {
        var stockMessages = new[]
        {
            nameof(GoodsReceiptPosted), nameof(ProductionCompleted), nameof(DeliveryPosted),
            nameof(ReturnReceived), nameof(TransferPosted), nameof(InventoryAdjusted),
        };
        var entities = await db.Set<ConnectionPollWatermark>().AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
                && stockMessages.Contains(row.CanonicalMessage))
            .Select(row => row.EntityType).Distinct().ToListAsync(cancellationToken);
        var through = DateOnly.FromDateTime(clock.UtcNow);
        foreach (var entity in entities)
        {
            var job = await backfills.StartAsync(new(connectionId, entity,
                new DateOnly(2000, 1, 1), through, 250), actor, cancellationToken);
            await backfills.ProcessAsync(job, null, cancellationToken);
        }
    }

    public async Task<StockDifferencePage> ListAsync(Guid runId, StockDifferenceListRequest request,
        CancellationToken cancellationToken)
    {
        _ = await RunAsync(runId, cancellationToken);
        var take = Math.Clamp(request.Take, 1, 100);
        var query = db.ErpChangeStockDifferences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.IsCurrent);
        if (!string.IsNullOrWhiteSpace(request.Reason)) query = query.Where(row => row.ReasonClass == request.Reason);
        if (request.LiveCover is { } covered) query = query.Where(row => row.HasLiveEntitlement == covered);
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(row => row.Status == request.Status);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var text = request.Query.Trim();
            query = query.Where(row => row.Identifier.Contains(text)
                || (row.ItemName != null && row.ItemName.Contains(text))
                || (row.ModuleLocation != null && row.ModuleLocation.Contains(text))
                || (row.SourceLocation != null && row.SourceLocation.Contains(text))
                || (row.TargetLocation != null && row.TargetLocation.Contains(text)));
        }
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(row => row.ComparedAt < cursor.ComparedAt
                || (row.ComparedAt == cursor.ComparedAt
                    && row.ErpChangeStockDifferenceId.CompareTo(cursor.Id) > 0));
        var rows = await query.OrderByDescending(row => row.ComparedAt)
            .ThenBy(row => row.ErpChangeStockDifferenceId).Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var pageRows = rows.Take(take).ToList();
        var actorNames = await NamesAsync(pageRows.Select(row => row.ResolvedBy), cancellationToken);
        var page = pageRows.Select(row => View(row, actorNames)).ToList();
        var all = db.ErpChangeStockDifferences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.IsCurrent);
        return new(page, more && page.Count != 0 ? Encode(new(page[^1].ComparedAt,
                page[^1].DifferenceId)) : null,
            (await RunAsync(runId, cancellationToken)).UnitsCompared,
            await all.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open, cancellationToken),
            await all.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open
                && row.HasLiveEntitlement, cancellationToken),
            await all.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Resolved, cancellationToken));
    }

    public async Task<StockDifferenceRow> ResolveAsync(Guid runId, Guid differenceId,
        ResolveStockDifferenceRequest request, string actor, CancellationToken cancellationToken)
    {
        _ = await ActiveRunAsync(runId, cancellationToken);
        if (!StockResolutionCommand.All.Contains(request.Command))
            throw new ErpChangeRuleException("erp_change_resolution_invalid", "Choose an ordinary reconciliation command.");
        if (request.CommandId == Guid.Empty)
            throw new ErpChangeRuleException("erp_change_command_id_required", "A command id is required.");
        var row = await db.ErpChangeStockDifferences.SingleOrDefaultAsync(value =>
            value.TenantId == tenant.TenantId && value.ErpChangeRunId == runId
            && value.ErpChangeStockDifferenceId == differenceId && value.IsCurrent, cancellationToken)
            ?? throw new ErpChangeRuleException("erp_change_difference_not_found", "The stock difference was not found.");
        if (row.Status == ErpChangeDifferenceStatus.Resolved)
            return View(row, await NamesAsync([row.ResolvedBy], cancellationToken));
        if (row.UnitId is not { } unitId)
            throw new ErpChangeRuleException("erp_change_unit_required", "This remote-only record must first be received or mapped to a module unit.");
        var unit = await FindUnitAsync(unitId, cancellationToken);
        try
        {
            switch (request.Command)
            {
                case StockResolutionCommand.CorrectIdentifier:
                    if (string.IsNullOrWhiteSpace(request.NewIdentifier))
                        throw new ErpChangeRuleException("erp_change_identifier_required", "The corrected identifier is required.");
                    await serialisation.CorrectIdentifierAsync(new(request.CommandId, unitId,
                        request.IdentifierType ?? unit.IdentifierType, request.NewIdentifier, clock.UtcNow,
                        actor, unit.LegalEntityId, request.Note), cancellationToken);
                    break;
                case StockResolutionCommand.MergeUnits:
                    if (request.SurvivorUnitId is not { } survivor || request.AbsorbedUnitId is not { } absorbed)
                        throw new ErpChangeRuleException("erp_change_merge_units_required", "Both merge unit ids are required.");
                    if (survivor != unitId && absorbed != unitId)
                        throw new ErpChangeRuleException("erp_change_merge_difference_unit_required",
                            "The merge must include the unit represented by this stock difference.");
                    await serialisation.MergeUnitsAsync(new(request.CommandId, survivor, absorbed, clock.UtcNow,
                        actor, unit.LegalEntityId, request.Note), cancellationToken);
                    break;
                case StockResolutionCommand.ChangeCustody:
                    if (string.IsNullOrWhiteSpace(request.LocationKind))
                        throw new ErpChangeRuleException("erp_change_location_required", "The custody location kind is required.");
                    await serialisation.ChangeCustodyAsync(new(request.CommandId, unitId, request.LocationKind,
                        clock.UtcNow, actor, unit.LegalEntityId, request.LocationId, request.Note), cancellationToken);
                    break;
            }
        }
        catch (CommandRefusedException exception)
        {
            throw new ErpChangeRuleException(exception.Rule, exception.Message);
        }
        row.Status = ErpChangeDifferenceStatus.Resolved;
        row.ResolutionCommand = request.Command;
        row.ResolutionCommandId = request.CommandId;
        row.ResolvedBy = actor;
        row.ResolvedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return View(row, await NamesAsync([row.ResolvedBy], cancellationToken));
    }

    public async Task<StockResidualSignoffView> SignOffAsync(Guid runId,
        SignOffStockResidualRequest request, string actor, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.Stock)
            throw new ErpChangeRuleException("erp_change_step_invalid", "The residual list is signed off in step 3.");
        if (run.StockComparedAt is null)
            throw new ErpChangeRuleException("erp_change_stock_not_reconciled", "Reconcile stock before signing off the residual list.");
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ErpChangeRuleException("erp_change_note_required", "A note of at most 2,000 characters is required.");
        var covered = await db.ErpChangeStockDifferences.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.IsCurrent && row.Status == ErpChangeDifferenceStatus.Open
            && row.HasLiveEntitlement, cancellationToken);
        if (covered > 0)
            throw new ErpChangeRuleException("erp_change_covered_differences_open", "Resolve every difference on a unit with live cover before signing off the residual list.");
        var residual = await db.ErpChangeStockDifferences.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.IsCurrent && row.Status == ErpChangeDifferenceStatus.Open,
            cancellationToken);
        var signoff = await db.ErpChangeStockSignoffs.SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId, cancellationToken);
        if (signoff is null)
        {
            signoff = new()
            {
                TenantId = tenant.TenantId,
                ErpChangeStockSignoffId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = runId,
                ResidualCount = residual,
                Note = request.Note.Trim(),
                SignedOffBy = actor,
                SignedOffAt = clock.UtcNow,
            };
            db.ErpChangeStockSignoffs.Add(signoff);
            await db.SaveChangesAsync(cancellationToken);
        }
        var names = await NamesAsync([signoff.SignedOffBy], cancellationToken);
        return new(signoff.ErpChangeStockSignoffId, signoff.ResidualCount, signoff.Note,
            names.GetValueOrDefault(signoff.SignedOffBy) ?? "Former or system user", signoff.SignedOffAt);
    }

    private async Task<Dictionary<string, RemotePosition>> PositionsAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var rows = await db.InboundMessages.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.IsLatest
                && (row.MessageType == nameof(GoodsReceiptPosted)
                    || row.MessageType == nameof(ProductionCompleted)
                    || row.MessageType == nameof(DeliveryPosted)
                    || row.MessageType == nameof(ReturnReceived)
                    || row.MessageType == nameof(TransferPosted)
                    || row.MessageType == nameof(InventoryAdjusted)))
            .OrderBy(row => row.OccurredAt).ThenBy(row => row.InboundMessageId)
            .Select(row => new MessageProjection
            {
                MessageType = row.MessageType,
                CanonicalMessage = row.CanonicalMessage,
                OccurredAt = row.OccurredAt,
            }).ToListAsync(cancellationToken);
        var positions = new Dictionary<string, RemotePosition>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.CanonicalMessage);
            var payload = document.RootElement.GetProperty("payload");
            switch (row.MessageType)
            {
                case nameof(GoodsReceiptPosted):
                    Apply(positions, payload.Deserialize<GoodsReceiptPosted>(IntegrationContracts.Json)!.Lines,
                        present: true, null, row.OccurredAt);
                    break;
                case nameof(ProductionCompleted):
                    Apply(positions, payload.Deserialize<ProductionCompleted>(IntegrationContracts.Json)!.Lines,
                        present: true, null, row.OccurredAt);
                    break;
                case nameof(ReturnReceived):
                    Apply(positions, payload.Deserialize<ReturnReceived>(IntegrationContracts.Json)!.Lines,
                        present: true, null, row.OccurredAt);
                    break;
                case nameof(DeliveryPosted):
                    Apply(positions, payload.Deserialize<DeliveryPosted>(IntegrationContracts.Json)!.Lines,
                        present: false, null, row.OccurredAt);
                    break;
                case nameof(TransferPosted):
                    var transfer = payload.Deserialize<TransferPosted>(IntegrationContracts.Json)!;
                    Apply(positions, transfer.Lines, present: true, transfer.ToLocationExternalId,
                        row.OccurredAt);
                    break;
                case nameof(InventoryAdjusted):
                    var adjustment = payload.Deserialize<InventoryAdjusted>(IntegrationContracts.Json)!;
                    foreach (var line in adjustment.Lines)
                        Apply(positions, [line], line.Quantity >= 0, null, row.OccurredAt);
                    break;
            }
        }
        return positions;
    }

    private static void Apply(Dictionary<string, RemotePosition> positions,
        IReadOnlyList<CanonicalLine> lines, bool present, string? location, DateTime occurredAt)
    {
        foreach (var line in lines)
        foreach (var serial in line.Serials)
        {
            var key = ErpChangeNaturalKeys.Serial(serial.Value);
            if (key.Length == 0) continue;
            if (!present) positions.Remove(key);
            else positions[key] = new(serial.Value, line.ItemExternalId,
                location ?? line.LocationExternalId, "on_hand", occurredAt);
        }
    }

    private async Task<Dictionary<Guid, string>> LocationCodesAsync(Guid connectionId,
        CancellationToken cancellationToken) => await db.ExternalIdMaps.AsNoTracking()
        .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
            && row.EntityType == "location" && row.ValidTo == null)
        .GroupBy(row => row.ModuleId).Select(group => new LocationProjection
        {
            ModuleId = group.Key,
            ExternalId = group.Select(row => row.ExternalId).First(),
        }).ToDictionaryAsync(row => row.ModuleId, row => row.ExternalId, cancellationToken);

    private async Task<List<MigrationReconciliationUnit>> ReadAllUnitsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<MigrationReconciliationUnit>();
        Guid? cursor = null;
        do
        {
            var page = await serialisation.InStockUnitsAsync(cursor, 1000, cancellationToken);
            result.AddRange(page.Items);
            cursor = page.NextUnitId;
        } while (cursor is not null);
        return result;
    }

    private async Task<MigrationReconciliationUnit> FindUnitAsync(Guid unitId,
        CancellationToken cancellationToken)
    {
        Guid? cursor = null;
        do
        {
            var page = await serialisation.InStockUnitsAsync(cursor, 1000, cancellationToken);
            if (page.Items.FirstOrDefault(row => row.UnitId == unitId) is { } found) return found;
            cursor = page.NextUnitId;
        } while (cursor is not null);
        throw new ErpChangeRuleException("erp_change_unit_not_found", "The in-stock unit was not found.");
    }

    private ErpChangeStockDifference Difference(Guid runId, Guid? unitId, Guid? itemId,
        string identifier, string? itemName, string reason, string? moduleLocation, string? sourceLocation,
        string? targetLocation, string? moduleStatus, string? sourceStatus, string? targetStatus,
        bool covered, DateOnly? warrantyEnd, string suggested, string status, DateTime comparedAt,
        string? resolutionCommand, string? resolvedBy) => new()
    {
        TenantId = tenant.TenantId,
        ErpChangeStockDifferenceId = TimeOrderedId.New(comparedAt),
        ErpChangeRunId = runId,
        UnitId = unitId,
        ItemId = itemId,
        Identifier = identifier,
        ItemName = itemName,
        ReasonClass = reason,
        ModuleLocation = moduleLocation,
        SourceLocation = sourceLocation,
        TargetLocation = targetLocation,
        ModuleStatus = moduleStatus,
        SourceStatus = sourceStatus,
        TargetStatus = targetStatus,
        HasLiveEntitlement = covered,
        WarrantyEnd = warrantyEnd,
        SuggestedResolution = suggested,
        Status = status,
        ResolutionCommand = resolutionCommand,
        ResolvedBy = resolvedBy,
        ResolvedAt = resolvedBy is null ? null : comparedAt,
        ComparedAt = comparedAt,
        IsCurrent = true,
    };

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

    private static bool SameLocation(string? first, string? second) =>
        ErpChangeNaturalKeys.Text(first) == ErpChangeNaturalKeys.Text(second);

    private static StockDifferenceRow View(ErpChangeStockDifference row,
        IReadOnlyDictionary<string, string> names) => new(
        row.ErpChangeStockDifferenceId, row.UnitId, row.ItemId, row.Identifier, row.ItemName,
        row.ReasonClass, row.ModuleLocation, row.SourceLocation, row.TargetLocation, row.ModuleStatus,
        row.SourceStatus, row.TargetStatus, row.HasLiveEntitlement, row.WarrantyEnd,
        row.SuggestedResolution, row.Status, row.ResolutionCommand, row.ResolutionCommandId,
        row.ResolvedBy is null ? null : names.GetValueOrDefault(row.ResolvedBy) ?? "Former or system user",
        row.ResolvedAt, row.ComparedAt);

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(IEnumerable<string?> actors,
        CancellationToken cancellationToken)
    {
        var references = actors.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct().Take(IUserDirectory.MaxReferences).ToList();
        return references.Count == 0 ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync(references, cancellationToken);
    }

    private static string Encode(StockCursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{cursor.ComparedAt.Ticks}|{cursor.Id:D}"));

    private static StockCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParse(parts[1], out var id)
                ? new(new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record RemotePosition(string Serial, string ItemExternalId, string? Location,
        string Status, DateTime OccurredAt);
    private sealed record StockCursor(DateTime ComparedAt, Guid Id);

    private sealed class MessageProjection
    {
        public required string MessageType { get; init; }
        public required string CanonicalMessage { get; init; }
        public DateTime OccurredAt { get; init; }
    }

    private sealed class LocationProjection
    {
        public Guid ModuleId { get; init; }
        public required string ExternalId { get; init; }
    }

    private sealed class ItemNameProjection
    {
        public Guid ItemId { get; init; }
        public string? Name { get; init; }
    }
}
