using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.DeadLetters;

public sealed class DeadLetterService(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IManualTaskReferenceVerifier referenceVerifier, ManualTaskDeskSettings deskSettings,
    IInboundMessageParkingSink parking) : IOutboundRetryWindow
{
    public Task<DateTime?> StartedAtAsync(Guid outboundCommandId, CancellationToken cancellationToken) =>
        (from item in db.DeadLetterItems.AsNoTracking()
         join action in db.DeadLetterEvents.AsNoTracking()
             on new { item.TenantId, item.DeadLetterItemId } equals new { action.TenantId, action.DeadLetterItemId }
         where item.TenantId == tenant.TenantId && item.SourceKind == DeadLetterSourceKind.OutboundCommand
             && item.SourceId == outboundCommandId
             && (action.Action == DeadLetterAction.MapAndRetry || action.Action == DeadLetterAction.CreateProvisionalAndRetry)
         select (DateTime?)action.OccurredAt).MaxAsync(cancellationToken);

    public async Task<Guid> ParkReconciliationAsync(Guid resultId, Guid connectionId, string entity,
        string rawComparison, string failure, CancellationToken cancellationToken)
    {
        var existing = await db.DeadLetterItems.Where(x => x.TenantId == tenant.TenantId
                && x.SourceKind == DeadLetterSourceKind.ReconciliationResult && x.SourceId == resultId)
            .Select(x => (Guid?)x.DeadLetterItemId).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing.Value;
        var id = TimeOrderedId.New(clock.UtcNow);
        db.DeadLetterItems.Add(new DeadLetterItem
        {
            TenantId = tenant.TenantId,
            DeadLetterItemId = id,
            ConnectionId = connectionId,
            Direction = DeadLetterDirection.Inbound,
            RecordType = entity,
            SourceKind = DeadLetterSourceKind.ReconciliationResult,
            SourceId = resultId,
            ReasonCode = "reconciliation_range_unavailable",
            RawPayload = rawComparison,
            CanonicalAttempt = "{}",
            Failure = failure,
            Status = DeadLetterStatus.NeedsAttention,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        });
        AddEvent(id, DeadLetterAction.Parked, $"system:connection:{connectionId:N}", failure, new { entity });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<Guid> ParkInboundAsync(Guid inboundMessageId, string reasonCode, string failure,
        CancellationToken cancellationToken)
    {
        var source = await db.InboundMessages.SingleAsync(x => x.TenantId == tenant.TenantId
            && x.InboundMessageId == inboundMessageId, cancellationToken);
        var id = await parking.ParkInboundAsync(source,
            Required(reasonCode, "reason_code_required", "A dead-letter reason code is required."),
            Required(failure, "failure_required", "A dead-letter failure is required."), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<DeadLetterPage> ListAsync(DeadLetterListRequest request,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(request.Take, 1, 200);
        var query = db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (request.ConnectionId is { } connectionId) query = query.Where(x => x.ConnectionId == connectionId);
        if (!string.IsNullOrWhiteSpace(request.Reason)) query = query.Where(x => x.ReasonCode == request.Reason);
        if (!string.IsNullOrWhiteSpace(request.RecordType)) query = query.Where(x => x.RecordType == request.RecordType);
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(x => x.Status == request.Status);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var find = request.Query.Trim();
            query = query.Where(x => x.RecordType.Contains(find) || x.ReasonCode.Contains(find)
                || x.Failure.Contains(find) || x.RawPayload.Contains(find) || x.CanonicalAttempt.Contains(find));
        }
        if (request.MinimumAgeHours is { } minimum)
            query = query.Where(x => x.CreatedAt <= clock.UtcNow.AddHours(-Math.Max(0, minimum)));
        if (request.MaximumAgeHours is { } maximum)
            query = query.Where(x => x.CreatedAt >= clock.UtcNow.AddHours(-Math.Max(0, maximum)));
        var total = await query.CountAsync(cancellationToken);
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(x => x.CreatedAt > cursor.CreatedAt
                || (x.CreatedAt == cursor.CreatedAt && x.DeadLetterItemId.CompareTo(cursor.DeadLetterItemId) > 0));

        // Member-init projection is intentional: positional record construction inside this EF query does not translate.
        var rows = await query.Join(db.Connections.AsNoTracking(), item => new { item.TenantId, item.ConnectionId },
                connection => new { connection.TenantId, connection.ConnectionId }, (item, connection) =>
                    new DeadLetterListProjection
            {
                DeadLetterItemId = item.DeadLetterItemId,
                ConnectionId = item.ConnectionId,
                ConnectionLabel = connection.DisplayLabel,
                Direction = item.Direction,
                RecordType = item.RecordType,
                RecordReference = item.SourceKind == DeadLetterSourceKind.InboundMessage
                    ? db.InboundMessages.Where(source => source.TenantId == item.TenantId
                            && source.InboundMessageId == item.SourceId)
                        .Select(source => source.ErpExternalId).FirstOrDefault()
                    : db.OutboundCommands.Where(source => source.TenantId == item.TenantId
                            && source.OutboundCommandId == item.SourceId)
                        .Select(source => source.ErpReferenceText).FirstOrDefault(),
                ReasonCode = item.ReasonCode,
                Failure = item.Failure,
                Status = item.Status,
                AttemptCount = db.DeadLetterEvents.Count(evt => evt.TenantId == item.TenantId
                    && evt.DeadLetterItemId == item.DeadLetterItemId),
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
            }).OrderBy(x => x.CreatedAt).ThenBy(x => x.DeadLetterItemId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var page = rows.Take(take).Select(x => new DeadLetterListRow(x.DeadLetterItemId, x.ConnectionId,
            x.ConnectionLabel, x.Direction, x.RecordType, x.RecordReference, x.ReasonCode, x.Failure, x.Status, x.AttemptCount,
            x.CreatedAt, x.UpdatedAt)).ToList();
        var next = more && page.Count != 0 ? Encode(new(page[^1].CreatedAt, page[^1].DeadLetterItemId)) : null;
        return new(page, next, total, await SummaryAsync(cancellationToken));
    }

    public async Task<IntegrationAttention> AttentionAsync(CancellationToken cancellationToken) => new(
        await db.DeadLetterItems.AsNoTracking().CountAsync(x => x.TenantId == tenant.TenantId
            && x.Status == DeadLetterStatus.NeedsAttention, cancellationToken)
        + await db.ConnectionHealth.AsNoTracking().CountAsync(x => x.TenantId == tenant.TenantId
            && (x.Status == "down" || x.Status == "degraded"), cancellationToken));

    public async Task<DeadLetterDetail?> DetailAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.DeadLetterItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.DeadLetterItemId == itemId, cancellationToken);
        if (item is null) return null;
        var eventRows = await db.DeadLetterEvents.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.DeadLetterItemId == itemId).OrderBy(x => x.OccurredAt).ThenBy(x => x.DeadLetterEventId)
            .Select(x => new DeadLetterEventProjection
            {
                DeadLetterEventId = x.DeadLetterEventId,
                Action = x.Action,
                ActorRef = x.ActorRef,
                Reason = x.Reason,
                DetailsJson = x.DetailsJson,
                OccurredAt = x.OccurredAt,
            }).ToListAsync(cancellationToken);
        var events = eventRows.Select(x => new DeadLetterEventView(x.DeadLetterEventId, x.Action, x.ActorRef,
            x.Reason, x.DetailsJson, x.OccurredAt)).ToList();
        var manual = await db.ManualOutboundTasks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.DeadLetterItemId == itemId, cancellationToken);
        var connectionLabel = await ConnectionLabelAsync(item.ConnectionId, cancellationToken);
        return new(item.DeadLetterItemId, item.ConnectionId, connectionLabel, item.Direction, item.RecordType, item.SourceKind,
            item.SourceId, item.ReasonCode, item.RawPayload, item.CanonicalAttempt, item.Failure, item.Status,
            item.CreatedAt, item.UpdatedAt, item.ResolvedAt, events,
            manual is null ? null : View(manual, connectionLabel));
    }

    public Task<DeadLetterActionResult> MapAndRetryAsync(Guid itemId, MapAndRetryDeadLetterRequest request,
        string actorRef, CancellationToken cancellationToken) => MapAndRetryAsync(itemId, request.EntityType,
        request.ExternalId, request.ModuleId, false, actorRef, cancellationToken);

    public Task<DeadLetterActionResult> CreateProvisionalAndRetryAsync(Guid itemId,
        CreateProvisionalAndRetryDeadLetterRequest request, string actorRef,
        CancellationToken cancellationToken) => MapAndRetryAsync(itemId, request.EntityType,
        request.ExternalId, TimeOrderedId.New(clock.UtcNow), true, actorRef, cancellationToken);

    public async Task<DeadLetterActionResult> DiscardAsync(Guid itemId, string reason, string actorRef,
        CancellationToken cancellationToken)
    {
        reason = Required(reason, "discard_reason_required", "Discarding a dead letter requires a reason.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await OpenItemAsync(itemId, cancellationToken);
        item.Status = DeadLetterStatus.Discarded;
        item.UpdatedAt = clock.UtcNow;
        item.ResolvedAt = clock.UtcNow;
        actorRef = RequiredActor(actorRef);
        await CloseManualTaskAsync(itemId, actorRef, cancellationToken);
        AddEvent(itemId, DeadLetterAction.Discard, actorRef, reason, new { });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(itemId, item.Status);
    }

    public async Task<ManualOutboundTaskPage> ManualTasksAsync(string? status, string? desk, string? cursor,
        int take, CancellationToken cancellationToken)
    {
        var query = db.ManualOutboundTasks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(desk)) query = query.Where(x => x.Desk == desk);
        var total = await query.CountAsync(cancellationToken);
        if (Decode(cursor) is { } key)
            query = query.Where(x => x.CreatedAt > key.CreatedAt
                || (x.CreatedAt == key.CreatedAt && x.ManualOutboundTaskId.CompareTo(key.DeadLetterItemId) > 0));
        var pageSize = Math.Clamp(take, 1, 200);
        var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.ManualOutboundTaskId)
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var more = rows.Count > pageSize;
        var labels = await ConnectionLabelsAsync(rows.Select(x => x.ConnectionId).Distinct().ToList(),
            cancellationToken);
        var page = rows.Take(pageSize).Select(row => View(row, labels[row.ConnectionId])).ToList();
        var next = more && page.Count != 0
            ? Encode(new(page[^1].CreatedAt, page[^1].ManualOutboundTaskId)) : null;
        var deskCounts = await db.ManualOutboundTasks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Status == ManualTaskStatus.Open)
            .GroupBy(x => x.Desk).Select(group => new { Desk = group.Key, Count = group.Count() })
            .OrderBy(row => row.Desk).ToListAsync(cancellationToken);
        var configured = (await deskSettings.GetAsync(cancellationToken)).Desks
            .ToDictionary(row => row.Code, row => row.Label, StringComparer.Ordinal);
        var desks = deskCounts.Select(row => new ManualTaskDeskCount(row.Desk,
            configured.GetValueOrDefault(row.Desk) ?? row.Desk, row.Count)).ToList();
        return new(page, next, total, desks);
    }

    public async Task<ManualOutboundTaskView?> ManualTaskAsync(Guid taskId,
        CancellationToken cancellationToken)
    {
        var row = await db.ManualOutboundTasks.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ManualOutboundTaskId == taskId)
            .Select(x => new ManualTaskProjection
            {
                ManualOutboundTaskId = x.ManualOutboundTaskId,
                DeadLetterItemId = x.DeadLetterItemId,
                OutboundCommandId = x.OutboundCommandId,
                ConnectionId = x.ConnectionId,
                CommandType = x.CommandType,
                Desk = x.Desk,
                Draft = x.Draft,
                Status = x.Status,
                CreatedAt = x.CreatedAt,
                ErpExternalId = x.ErpExternalId,
                ErpChangeMarker = x.ErpChangeMarker,
                CompletedBy = x.CompletedBy,
                CompletedAt = x.CompletedAt,
                Note = x.Note,
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var connectionLabel = await ConnectionLabelAsync(row.ConnectionId, cancellationToken);
        return new(row.ManualOutboundTaskId, row.DeadLetterItemId,
            row.OutboundCommandId, row.ConnectionId, connectionLabel, row.CommandType, row.Desk, row.Draft, row.Status, row.CreatedAt,
            row.ErpExternalId, row.ErpChangeMarker, row.CompletedBy, row.CompletedAt, row.Note);
    }

    public async Task<ManualOutboundTaskView> CompleteManualTaskAsync(Guid taskId,
        CompleteManualTaskRequest request, string actorRef, CancellationToken cancellationToken)
    {
        actorRef = RequiredActor(actorRef);
        var task = await db.ManualOutboundTasks.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ManualOutboundTaskId == taskId, cancellationToken)
            ?? throw new DeadLetterRuleException("manual_task_not_found", "The manual task was not found.");
        if (task.Status != ManualTaskStatus.Open)
            throw new DeadLetterRuleException("manual_task_closed", "The manual task is already completed.");
        // A cleanup acknowledgement has no remote document id and must never create a document mapping.
        if (ManualTaskCommandType.IsAcknowledgementOnly(task.CommandType))
        {
            task.Status = ManualTaskStatus.Completed;
            task.CompletedBy = actorRef;
            task.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return View(task, await ConnectionLabelAsync(task.ConnectionId, cancellationToken));
        }
        var externalId = Required(request.ErpExternalId ?? "", "erp_external_id_required",
            "The ERP document id is required.");
        var match = await referenceVerifier.VerifyAsync(task.ConnectionId, task.CommandType, externalId, task.Draft,
            cancellationToken);
        if (match != ManualTaskReferenceMatch.Found && !request.AllowUnverified)
            throw new DeadLetterRuleException("manual_task_external_id_unverified",
                match == ManualTaskReferenceMatch.NotFound
                    ? "The ERP number was not found. Check it, or record it anyway with the warning acknowledged."
                    : "This connection could not verify the ERP number. Check it, or record it anyway with the warning acknowledged.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var command = await db.OutboundCommands.SingleAsync(x => x.TenantId == tenant.TenantId
            && x.OutboundCommandId == task.OutboundCommandId, cancellationToken);
        await AddMappingAsync(command.ConnectionId, "document", externalId, OutboundRelay.ModuleId(command),
            ExternalIdMappingSource.Manual, actorRef, command.ModuleDocumentRef, cancellationToken);
        command.Status = OutboundCommandStatus.Done;
        command.ErpExternalId = externalId;
        command.ErpChangeMarker = request.ErpChangeMarker;
        command.Outcome = OutboundConnectorOutcome.Manual;
        command.LastFailure = null;
        command.CompletedAt = clock.UtcNow;
        task.Status = ManualTaskStatus.Completed;
        task.ErpExternalId = externalId;
        task.ErpChangeMarker = request.ErpChangeMarker;
        task.CompletedBy = actorRef;
        task.CompletedAt = clock.UtcNow;
        // A review task (record held remotely for a person's approval) has no dead letter until it is rejected.
        if (task.DeadLetterItemId is { } itemId)
        {
            var item = await db.DeadLetterItems.SingleAsync(x => x.TenantId == tenant.TenantId
                && x.DeadLetterItemId == itemId, cancellationToken);
            item.Status = DeadLetterStatus.Resolved;
            item.UpdatedAt = clock.UtcNow;
            item.ResolvedAt = clock.UtcNow;
            AddEvent(item.DeadLetterItemId, DeadLetterAction.ManualMapping, actorRef, null,
                new { external_id = externalId, change_marker = request.ErpChangeMarker });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return View(task, await ConnectionLabelAsync(task.ConnectionId, cancellationToken));
    }

    private async Task<DeadLetterActionResult> MapAndRetryAsync(Guid itemId, string entityType,
        string externalId, Guid moduleId, bool provisional, string actorRef,
        CancellationToken cancellationToken)
    {
        entityType = Required(entityType, "entity_type_required", "A canonical entity type is required.");
        externalId = Required(externalId, "external_id_required", "An external id is required.");
        if (moduleId == Guid.Empty) throw new DeadLetterRuleException("module_id_required", "A module id is required.");
        actorRef = RequiredActor(actorRef);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await OpenItemAsync(itemId, cancellationToken);
        await AddMappingAsync(item.ConnectionId, entityType, externalId, moduleId,
            provisional ? ExternalIdMappingSource.Provisional : ExternalIdMappingSource.Operator,
            actorRef, externalId, cancellationToken);
        await AddReferenceProjectionAsync(entityType, externalId, moduleId, provisional,
            cancellationToken);
        await ScheduleRetryAsync(item, cancellationToken);
        await CloseManualTaskAsync(itemId, actorRef, cancellationToken);
        item.Status = DeadLetterStatus.RetryPending;
        item.UpdatedAt = clock.UtcNow;
        item.ResolvedAt = clock.UtcNow;
        var action = provisional ? DeadLetterAction.CreateProvisionalAndRetry : DeadLetterAction.MapAndRetry;
        AddEvent(itemId, action, actorRef, null, new { entity_type = entityType, external_id = externalId,
            module_id = moduleId, provisional });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(itemId, item.Status, moduleId);
    }

    private async Task ScheduleRetryAsync(DeadLetterItem item, CancellationToken cancellationToken)
    {
        if (item.SourceKind == DeadLetterSourceKind.OutboundCommand)
        {
            var command = await db.OutboundCommands.SingleAsync(x => x.TenantId == tenant.TenantId
                && x.OutboundCommandId == item.SourceId, cancellationToken);
            command.Status = OutboundCommandStatus.Pending;
            command.AttemptCount = 0;
            command.NextAttemptAt = clock.UtcNow;
            command.LastAttemptAt = null;
            command.LockedUntil = null;
            command.CompletedAt = null;
            command.Outcome = null;
            command.LastFailure = null;
            return;
        }
        if (item.SourceKind == DeadLetterSourceKind.InboundMessage)
        {
            var inbound = await db.InboundMessages.SingleAsync(x => x.TenantId == tenant.TenantId
                && x.InboundMessageId == item.SourceId, cancellationToken);
            inbound.TranslationStatus = InboundTranslationStatus.Pending;
            inbound.TranslationAttemptCount = 0;
            inbound.TranslationStartedAt = null;
            inbound.NextTranslationAttemptAt = null;
            inbound.LastTranslationError = null;
            inbound.AttentionReasonCode = null;
            inbound.TranslatedAt = null;
            return;
        }
        throw new DeadLetterRuleException("dead_letter_source_unknown", "The dead-letter source cannot be retried.");
    }

    private async Task AddMappingAsync(Guid connectionId, string entityType, string externalId, Guid moduleId,
        string source, string actorRef, string naturalKey, CancellationToken cancellationToken)
    {
        var row = await db.ExternalIdMaps.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.EntityType == entityType && x.ExternalId == externalId
            && x.ValidTo == null,
            cancellationToken);
        if (row is not null)
        {
            if (row.ModuleId != moduleId)
                throw new DeadLetterRuleException("external_mapping_conflict",
                    "The external id is already mapped to another module record.");
            return;
        }
        db.ExternalIdMaps.Add(new ExternalIdMap
        {
            TenantId = tenant.TenantId,
            ExternalIdMapId = TimeOrderedId.New(clock.UtcNow),
            ConnectionId = connectionId,
            EntityType = entityType,
            ExternalId = externalId,
            ModuleId = moduleId,
            ValidFrom = clock.UtcNow,
            NaturalKeyHash = OutboundRelay.NaturalKeyHash(naturalKey),
            MappingSource = source,
            MappedBy = actorRef,
            MappedAt = clock.UtcNow,
        });
    }

    private async Task AddReferenceProjectionAsync(string entityType, string externalId,
        Guid moduleId, bool provisional, CancellationToken cancellationToken)
    {
        if (entityType is not ("item" or "party")) return;
        var row = await db.ReferenceProjections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntityType == entityType && x.ModuleId == moduleId,
            cancellationToken);
        if (row is not null) return;
        db.ReferenceProjections.Add(new ReferenceProjection
        {
            TenantId = tenant.TenantId,
            EntityType = entityType,
            ModuleId = moduleId,
            Code = externalId,
            DisplayName = provisional ? "Provisional integration record" : null,
            AttributesJson = "{}",
            UpdatedAt = clock.UtcNow,
        });
    }

    private async Task<DeadLetterItem> OpenItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.DeadLetterItems.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.DeadLetterItemId == itemId, cancellationToken)
            ?? throw new DeadLetterRuleException("dead_letter_not_found", "The dead-letter item was not found.");
        if (item.Status != DeadLetterStatus.NeedsAttention)
            throw new DeadLetterRuleException("dead_letter_closed", "The dead-letter item no longer needs attention.");
        return item;
    }

    private async Task CloseManualTaskAsync(Guid itemId, string actorRef,
        CancellationToken cancellationToken)
    {
        var manual = await db.ManualOutboundTasks.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.DeadLetterItemId == itemId && x.Status == ManualTaskStatus.Open, cancellationToken);
        if (manual is null) return;
        manual.Status = ManualTaskStatus.Completed;
        manual.CompletedBy = actorRef;
        manual.CompletedAt = clock.UtcNow;
    }

    private void AddEvent(Guid itemId, string action, string actorRef, string? reason, object details) =>
        db.DeadLetterEvents.Add(new DeadLetterEvent
        {
            TenantId = tenant.TenantId,
            DeadLetterEventId = TimeOrderedId.New(clock.UtcNow),
            DeadLetterItemId = itemId,
            Action = action,
            ActorRef = actorRef,
            Reason = reason,
            DetailsJson = JsonSerializer.Serialize(details),
            OccurredAt = clock.UtcNow,
        });

    private static string RequiredActor(string value) => Required(value, "audit_actor_required",
        "An authenticated operator is required.");

    private static string Required(string value, string rule, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DeadLetterRuleException(rule, message);
        return value.Trim();
    }

    private async Task<string> ConnectionLabelAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.DisplayLabel).SingleAsync(cancellationToken);

    private async Task<Dictionary<Guid, string>> ConnectionLabelsAsync(IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken) => await db.Connections.AsNoTracking()
        .Where(x => x.TenantId == tenant.TenantId && connectionIds.Contains(x.ConnectionId))
        .ToDictionaryAsync(x => x.ConnectionId, x => x.DisplayLabel, cancellationToken);

    private async Task<DeadLetterSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-30);
        var rows = await db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .GroupBy(_ => 1).Select(group => new DeadLetterSummaryProjection
            {
                Open = group.Count(x => x.Status == DeadLetterStatus.NeedsAttention),
                Retried = group.Count(x => x.Status == DeadLetterStatus.RetryPending),
                Discarded = group.Count(x => x.Status == DeadLetterStatus.Discarded),
                UnknownItem = group.Count(x => x.Status == DeadLetterStatus.NeedsAttention
                    && (x.ReasonCode == "item_not_mapped" || x.ReasonCode == "unknown_item")),
                SerialNotFound = group.Count(x => x.Status == DeadLetterStatus.NeedsAttention
                    && x.ReasonCode == "serial_not_found"),
                PartyNotLinked = group.Count(x => x.Status == DeadLetterStatus.NeedsAttention
                    && (x.ReasonCode == "party_not_mapped" || x.ReasonCode == "party_not_linked")),
                CouldNotSend = group.Count(x => x.Status == DeadLetterStatus.NeedsAttention
                    && x.Direction == DeadLetterDirection.Outbound),
                ResolvedLast30Days = group.Count(x => x.ResolvedAt >= cutoff),
                RetriedLast30Days = group.Count(x => x.ResolvedAt >= cutoff
                    && x.Status == DeadLetterStatus.RetryPending),
                DiscardedLast30Days = group.Count(x => x.ResolvedAt >= cutoff
                    && x.Status == DeadLetterStatus.Discarded),
            }).SingleOrDefaultAsync(cancellationToken);
        return rows is null ? new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0) : new(rows.Open, rows.Retried,
            rows.Discarded, rows.UnknownItem, rows.SerialNotFound, rows.PartyNotLinked, rows.CouldNotSend, rows.ResolvedLast30Days,
            rows.RetriedLast30Days, rows.DiscardedLast30Days);
    }

    private static ManualOutboundTaskView View(ManualOutboundTask row, string connectionLabel) => new(row.ManualOutboundTaskId,
        row.DeadLetterItemId, row.OutboundCommandId, row.ConnectionId, connectionLabel, row.CommandType, row.Desk, row.Draft, row.Status,
        row.CreatedAt, row.ErpExternalId, row.ErpChangeMarker, row.CompletedBy, row.CompletedAt, row.Note);

    private static string Encode(DeadLetterCursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{cursor.CreatedAt.Ticks}:{cursor.DeadLetterItemId:N}"));

    private static DeadLetterCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParse(parts[1], out var id)
                ? new(new DateTime(ticks, DateTimeKind.Utc), id)
                : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new DeadLetterRuleException("cursor_invalid", "The page cursor is invalid.");
        }
    }

    private sealed class DeadLetterListProjection
    {
        public Guid DeadLetterItemId { get; init; }
        public Guid ConnectionId { get; init; }
        public required string ConnectionLabel { get; init; }
        public required string Direction { get; init; }
        public required string RecordType { get; init; }
        public string? RecordReference { get; init; }
        public required string ReasonCode { get; init; }
        public required string Failure { get; init; }
        public required string Status { get; init; }
        public int AttemptCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class DeadLetterSummaryProjection
    {
        public int Open { get; init; }
        public int Retried { get; init; }
        public int Discarded { get; init; }
        public int UnknownItem { get; init; }
        public int SerialNotFound { get; init; }
        public int PartyNotLinked { get; init; }
        public int CouldNotSend { get; init; }
        public int ResolvedLast30Days { get; init; }
        public int RetriedLast30Days { get; init; }
        public int DiscardedLast30Days { get; init; }
    }

    private sealed class ManualTaskProjection
    {
        public Guid ManualOutboundTaskId { get; init; }
        public Guid? DeadLetterItemId { get; init; }
        public Guid? OutboundCommandId { get; init; }
        public Guid ConnectionId { get; init; }
        public required string CommandType { get; init; }
        public required string Desk { get; init; }
        public required string Draft { get; init; }
        public required string Status { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? ErpExternalId { get; init; }
        public string? ErpChangeMarker { get; init; }
        public string? CompletedBy { get; init; }
        public DateTime? CompletedAt { get; init; }
        public string? Note { get; init; }
    }

    private sealed class DeadLetterEventProjection
    {
        public Guid DeadLetterEventId { get; init; }
        public required string Action { get; init; }
        public required string ActorRef { get; init; }
        public string? Reason { get; init; }
        public required string DetailsJson { get; init; }
        public DateTime OccurredAt { get; init; }
    }
}
