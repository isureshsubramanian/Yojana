using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

public sealed class MigrationReconciliationPort(
    SerialisationDbContext db,
    ITenantContext tenant,
    IMessageBus bus) : IMigrationReconciliationPort
{
    public async Task<IReadOnlyList<MigrationMasterReferenceUsage>> MasterReferenceUsageAsync(
        CancellationToken cancellationToken)
    {
        var excluded = SerialisedOnHandCustodyPolicy.ExcludedLifecycleStatuses;
        var items = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && !excluded.Contains(row.LifecycleStatus))
            .GroupBy(row => row.ItemId)
            .Select(group => new UsageProjection
            {
                ReferenceId = group.Key,
                Kind = "item",
                LiveUnits = group.Count(),
            }).ToListAsync(cancellationToken);
        var parties = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && !excluded.Contains(row.LifecycleStatus)
                && row.OwnerPartyRef != null)
            .GroupBy(row => row.OwnerPartyRef)
            .Select(group => new UsageProjection
            {
                ReferenceId = group.Key!.Value,
                Kind = "party",
                LiveUnits = group.Count(),
            }).ToListAsync(cancellationToken);
        return items.Concat(parties)
            .Select(row => new MigrationMasterReferenceUsage(row.ReferenceId, row.Kind, row.LiveUnits))
            .ToList();
    }

    public async Task<MigrationUnitPage> InStockUnitsAsync(Guid? afterUnitId, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 1000);
        var included = SerialisedOnHandCustodyPolicy.IncludedLocationKinds;
        var excluded = SerialisedOnHandCustodyPolicy.ExcludedLifecycleStatuses;
        var query = db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.LocationId != null && included.Contains(row.LocationKind!)
            && !excluded.Contains(row.LifecycleStatus)
            && !db.AssemblyLinks.Any(link => link.TenantId == row.TenantId
                && link.ChildUnitId == row.UnitId && link.RemovedAt == null));
        if (afterUnitId is { } cursor) query = query.Where(row => row.UnitId.CompareTo(cursor) > 0);
        var current = await query.OrderBy(row => row.UnitId).Take(take + 1)
            .Select(row => new UnitProjection
            {
                UnitId = row.UnitId,
                ItemId = row.ItemId,
                LifecycleStatus = row.LifecycleStatus,
                Disposition = row.Disposition,
                LocationId = row.LocationId,
                LocationKind = row.LocationKind,
                OwnerPartyRef = row.OwnerPartyRef,
                LiveEntitlementIds = row.LiveEntitlementIds,
                WarrantyEnd = row.WarrantyEndMax,
            }).ToListAsync(cancellationToken);
        var page = current.Take(take).ToList();
        var ids = page.Select(row => row.UnitId).ToList();
        var identifiers = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1)
            .ThenBy(row => row.IdentifierId)
            .Select(row => new IdentifierProjection
            {
                UnitId = row.UnitId,
                Type = row.Type,
                RawValue = row.RawValue,
                NormalisedValue = row.NormalisedValue,
            }).ToListAsync(cancellationToken);
        var legalEntities = await db.Events.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.UnitId))
            .GroupBy(row => row.UnitId)
            .Select(group => new LegalEntityProjection
            {
                UnitId = group.Key,
                LegalEntityId = group.OrderByDescending(row => row.Seq)
                    .Select(row => row.LegalEntityId).First(),
            }).ToDictionaryAsync(row => row.UnitId, row => row.LegalEntityId, cancellationToken);
        var firstIdentifiers = identifiers.GroupBy(row => row.UnitId).ToDictionary(group => group.Key,
            group => group.First());
        var result = page.Where(row => firstIdentifiers.ContainsKey(row.UnitId))
            .Select(row =>
            {
                var identifier = firstIdentifiers[row.UnitId];
                return new MigrationReconciliationUnit(row.UnitId, row.ItemId, identifier.Type,
                    identifier.RawValue, identifier.NormalisedValue, row.LifecycleStatus, row.Disposition,
                    row.LocationId, row.LocationKind, row.OwnerPartyRef,
                    row.LiveEntitlementIds != "[]", row.WarrantyEnd,
                    legalEntities.GetValueOrDefault(row.UnitId));
            }).ToList();
        return new(result, current.Count > take && page.Count != 0 ? page[^1].UnitId : null);
    }

    public async Task<MigrationUnitHistoryChangeCounts> CountAttributedUnitHistoryChangesAsync(
        MigrationUnitHistoryAttribution attribution, CancellationToken cancellationToken)
    {
        await using var command = await AttributionCommandAsync(attribution, cancellationToken);
        command.CommandText = AttributionSql + """
            SELECT kind, COUNT(DISTINCT unit_id)
            FROM attributed
            WHERE kind IS NOT NULL
            GROUP BY kind;
            """;
        var deliberate = 0;
        var unexpected = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var count = reader.GetInt32(1);
            if (reader.GetString(0) == MigrationUnitHistoryChangeKind.Deliberate) deliberate = count;
            else unexpected = count;
        }
        return new(deliberate, unexpected);
    }

    public async Task<MigrationUnitHistoryChangePage> AttributedUnitHistoryChangesAsync(
        MigrationUnitHistoryAttribution attribution, string kind, Guid? afterUnitId, int take,
        CancellationToken cancellationToken)
    {
        if (!MigrationUnitHistoryChangeKind.All.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        take = Math.Clamp(take, 1, 100);
        await using var command = await AttributionCommandAsync(attribution, cancellationToken);
        Add(command, "@kind", DbType.String, kind);
        Add(command, "@after", DbType.Guid, afterUnitId);
        Add(command, "@take", DbType.Int32, take + 1);
        command.CommandText = AttributionSql + """
            SELECT TOP (@take) unit_id, COUNT_BIG(*), MAX(recorded_time)
            FROM attributed
            WHERE kind = @kind AND (@after IS NULL OR unit_id > @after)
            GROUP BY unit_id
            ORDER BY unit_id;
            """;
        var changes = new List<HistoryChangeProjection>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                changes.Add(new()
                {
                    UnitId = reader.GetGuid(0),
                    EventsAdded = checked((int)reader.GetInt64(1)),
                    LastRecordedAt = reader.GetDateTime(2),
                });
        }
        var pageChanges = changes.Take(take).ToList();
        var pageIds = pageChanges.Select(row => row.UnitId).ToList();
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && pageIds.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1)
            .ThenBy(row => row.IdentifierId)
            .Select(row => new IdentifierProjection
            {
                UnitId = row.UnitId,
                Type = row.Type,
                RawValue = row.RawValue,
                NormalisedValue = row.NormalisedValue,
            }).ToListAsync(cancellationToken);
        var names = identifiers.GroupBy(row => row.UnitId)
            .ToDictionary(group => group.Key, group => group.First().RawValue);
        var rows = pageChanges.Select(change => new MigrationUnitHistoryChange(change.UnitId,
            names.GetValueOrDefault(change.UnitId) ?? "Unit without an open identifier", kind,
            change.EventsAdded, change.LastRecordedAt)).ToList();
        return new(rows, changes.Count > take && rows.Count != 0 ? rows[^1].UnitId : null);
    }

    private async Task<System.Data.Common.DbCommand> AttributionCommandAsync(
        MigrationUnitHistoryAttribution attribution, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var command = db.Database.GetDbConnection().CreateCommand();
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        Add(command, "@tenant", DbType.Guid, tenant.TenantId.Value);
        Add(command, "@run", DbType.Guid, attribution.ErpChangeRunId);
        Add(command, "@run_ref", DbType.String, RunReference(attribution.ErpChangeRunId));
        Add(command, "@deliberate", DbType.String, JsonSerializer.Serialize(
            attribution.DeliberateCommandIds.Distinct()));
        Add(command, "@documents", DbType.String, JsonSerializer.Serialize(
            attribution.UnexpectedDocumentIds.Distinct()));
        return command;
    }

    private const string AttributionSql = """
        ;WITH deliberate_commands AS (
            SELECT value command_id FROM OPENJSON(@deliberate) WITH (value uniqueidentifier '$')
        ), unexpected_documents AS (
            SELECT value document_id FROM OPENJSON(@documents) WITH (value uniqueidentifier '$')
        ), ledger AS (
            SELECT unit_id, recorded_time, document_type, document_id, attributes,
                   TRY_CONVERT(uniqueidentifier, JSON_VALUE(attributes, '$.command_id')) command_id
            FROM serialisation.unit_event
            WHERE tenant_id = @tenant
            UNION ALL
            SELECT unit_id, recorded_time, document_type, document_id, attributes,
                   TRY_CONVERT(uniqueidentifier, JSON_VALUE(attributes, '$.command_id')) command_id
            FROM serialisation.unit_event_archive
            WHERE tenant_id = @tenant
        ), attributed AS (
            SELECT unit_id, recorded_time,
                CASE
                    WHEN command_id IN (SELECT command_id FROM deliberate_commands) THEN 'deliberate'
                    WHEN document_id IN (SELECT document_id FROM unexpected_documents)
                      OR (document_type = 'erp_change' AND document_id = @run)
                      OR EXISTS (SELECT 1 FROM OPENJSON(JSON_QUERY(attributes, '$.evidence_refs'))
                                 WHERE value = @run_ref) THEN 'unexpected'
                END kind
            FROM ledger
            WHERE command_id IN (SELECT command_id FROM deliberate_commands)
               OR document_id IN (SELECT document_id FROM unexpected_documents)
               OR (document_type = 'erp_change' AND document_id = @run)
               OR EXISTS (SELECT 1 FROM OPENJSON(JSON_QUERY(attributes, '$.evidence_refs'))
                          WHERE value = @run_ref)
        )
        """;

    private static void Add(System.Data.Common.DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string RunReference(Guid runId) => $"erp_change_run:{runId:D}";

    public async Task<MigrationResolutionResult> CorrectIdentifierAsync(MigrationCorrectIdentifier request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<CorrectIdentifierResult>(new CorrectIdentifier(request.CommandId, request.UnitId,
            request.IdentifierType, request.NewRawValue, MigrationReconciliationReason.Value,
            request.EventTime, request.ActorRef, request.LegalEntityId, Note: request.Note,
            Correction: Metadata(request.ErpChangeRunId, request.CommandId, request.Note),
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])), Options(), cancellationToken);
        return new(CorrectionCommand.CorrectIdentifier, request.CommandId, request.UnitId);
    }

    public async Task<MigrationResolutionResult> MergeUnitsAsync(MigrationMergeUnits request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<MergeUnitsResult>(new MergeUnits(request.CommandId, request.SurvivorUnitId,
            request.AbsorbedUnitId, MigrationReconciliationReason.Value, request.EventTime, request.ActorRef,
            request.LegalEntityId, Note: request.Note,
            Correction: Metadata(request.ErpChangeRunId, request.CommandId, request.Note),
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])), Options(), cancellationToken);
        return new(CorrectionCommand.MergeUnits, request.CommandId, request.SurvivorUnitId);
    }

    public async Task<MigrationResolutionResult> ChangeCustodyAsync(MigrationChangeCustody request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<ChangeCustodyResult>(new ChangeCustody(request.CommandId, request.UnitId,
            request.LocationKind, MigrationReconciliationReason.Value, request.EventTime, request.ActorRef,
            request.LegalEntityId, request.LocationId, Source: CorrectionInitiatedVia.Integration,
            Document: new("erp_change", request.ErpChangeRunId),
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])),
            Options(), cancellationToken);
        return new(nameof(ChangeCustody), request.CommandId, request.UnitId);
    }

    private DeliveryOptions Options() => new() { TenantId = tenant.TenantId.ToString() };

    private static CorrectionMetadata Metadata(Guid runId, Guid commandId, string? note) => new(commandId,
        MigrationReconciliationReason.Value, note, [RunReference(runId)],
        InitiatedVia: CorrectionInitiatedVia.Integration,
        InitiatingRole: CorrectionInitiatingRole.InventoryController);

    private sealed class UsageProjection
    {
        public Guid ReferenceId { get; init; }
        public required string Kind { get; init; }
        public int LiveUnits { get; init; }
    }

    private sealed class UnitProjection
    {
        public Guid UnitId { get; init; }
        public Guid ItemId { get; init; }
        public required string LifecycleStatus { get; init; }
        public required string Disposition { get; init; }
        public Guid? LocationId { get; init; }
        public string? LocationKind { get; init; }
        public Guid? OwnerPartyRef { get; init; }
        public required string LiveEntitlementIds { get; init; }
        public DateOnly? WarrantyEnd { get; init; }
    }

    private sealed class IdentifierProjection
    {
        public Guid UnitId { get; init; }
        public required string Type { get; init; }
        public required string RawValue { get; init; }
        public required string NormalisedValue { get; init; }
    }

    private sealed class LegalEntityProjection
    {
        public Guid UnitId { get; init; }
        public Guid LegalEntityId { get; init; }
    }

    private sealed class HistoryChangeProjection
    {
        public Guid UnitId { get; init; }
        public int EventsAdded { get; init; }
        public DateTime LastRecordedAt { get; init; }
    }
}
