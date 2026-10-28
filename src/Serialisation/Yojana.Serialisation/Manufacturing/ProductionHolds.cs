using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Manufacturing;

public sealed class ProductionHoldQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<InProductionOrderPage> PageAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var after = Decode(cursor);
        var groups = db.ProductionComponentHolds.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.LinkedAt == null && row.ReturnedAt == null)
            .GroupBy(row => new { row.ProductionOrderType, row.ProductionOrderId })
            .Select(group => new
            {
                group.Key.ProductionOrderType,
                group.Key.ProductionOrderId,
                HeldCount = group.Count(),
                OldestIssuedAt = group.Min(row => row.IssuedAt),
            });
        if (after is { } key)
            groups = groups.Where(row => row.OldestIssuedAt > key.At
                || row.OldestIssuedAt == key.At && row.ProductionOrderId.CompareTo(key.Id) > 0);
        var ordered = await groups.OrderBy(row => row.OldestIssuedAt).ThenBy(row => row.ProductionOrderId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var rows = new List<InProductionOrderRow>();
        foreach (var group in ordered.Take(take))
        {
            var parents = await db.ProductionOrderReceipts.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ProductionOrderType == group.ProductionOrderType
                && row.ProductionOrderId == group.ProductionOrderId).ToListAsync(cancellationToken);
            var build = await db.ProductionOrderBuilds.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenant.TenantId && row.ProductionOrderType == group.ProductionOrderType
                && row.ProductionOrderId == group.ProductionOrderId, cancellationToken);
            rows.Add(new(new(group.ProductionOrderType, group.ProductionOrderId), build?.ProducedItemId,
                group.HeldCount, group.OldestIssuedAt, parents.Count, build?.PlannedQuantity,
                build?.PlannedQuantity != 1m || parents.Count > 1
                    ? "waiting_for_assignment" : "waiting_for_receipt"));
        }
        var next = ordered.Count <= take || rows.Count == 0 ? null
            : Encode(rows[^1].OldestIssuedAt, rows[^1].ProductionOrder.Id);
        return new(rows, next);
    }

    public async Task<InProductionOrderDetail?> DetailAsync(DocumentRef order,
        CancellationToken cancellationToken)
    {
        var holds = await db.ProductionComponentHolds.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.ProductionOrderType == order.Type
            && row.ProductionOrderId == order.Id && row.LinkedAt == null && row.ReturnedAt == null)
            .OrderBy(row => row.IssuedAt).ThenBy(row => row.HoldId).ToListAsync(cancellationToken);
        if (holds.Count == 0) return null;
        var ids = holds.Select(row => row.UnitId).ToList();
        var serials = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && ids.Contains(row.UnitId) && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue, cancellationToken);
        var components = holds.Select(row => new InProductionComponentRow(row.HoldId, row.UnitId, row.ItemId,
            serials.GetValueOrDefault(row.UnitId) ?? "Identifier unavailable", row.IssuedAt,
            new(row.IssueDocumentType, row.IssueDocumentId, row.IssueLine), row.IssueLine)).ToList();
        var receipts = await db.ProductionOrderReceipts.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ProductionOrderType == order.Type && row.ProductionOrderId == order.Id)
            .OrderBy(row => row.ReceivedAt).ToListAsync(cancellationToken);
        var parentIds = receipts.Select(row => row.ParentUnitId).ToList();
        var parentFacts = await (from assignment in db.ItemAssignments.AsNoTracking()
                                 join identifier in db.Identifiers.AsNoTracking()
                                     on new { assignment.TenantId, assignment.UnitId } equals new { identifier.TenantId, identifier.UnitId }
                                 where assignment.TenantId == tenant.TenantId && parentIds.Contains(assignment.UnitId)
                                     && assignment.ValidTo == null && identifier.ValidTo == null
                                     && identifier.Type == IdentifierType.InternalSerial
                                 select new { assignment.UnitId, assignment.ItemId, identifier.RawValue })
            .ToListAsync(cancellationToken);
        var parents = receipts.Join(parentFacts, receipt => receipt.ParentUnitId, fact => fact.UnitId,
            (receipt, fact) => new InProductionParentRow(fact.UnitId, fact.ItemId, fact.RawValue,
                new(receipt.ReceiptDocumentType, receipt.ReceiptDocumentId))).ToList();
        var build = await db.ProductionOrderBuilds.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ProductionOrderType == order.Type
            && row.ProductionOrderId == order.Id, cancellationToken);
        return new(order, build?.ProducedItemId, build?.PlannedQuantity, components, parents,
            build?.PlannedQuantity != 1m || parents.Count > 1
                ? "waiting_for_assignment" : "waiting_for_receipt");
    }

    private static string Encode(DateTime at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        at.ToString("O", CultureInfo.InvariantCulture) + "|" + id.ToString("N")));

    private static (DateTime At, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return (DateTime.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            throw new Yojana.Serialisation.Contracts.CommandRefusedException("cursor_invalid", "The in-production continuation is invalid.",
                "reload the first page");
        }
    }
}

public static class AssignProductionComponentsHandler
{
    public static Task<AssignProductionComponentsResult> Handle(AssignProductionComponents command,
        DocumentContext context, CancellationToken cancellationToken) => context.InTransactionAsync<AssignProductionComponentsResult>(async () =>
    {
        if (command.CommandId == Guid.Empty)
            throw new Yojana.Serialisation.Contracts.CommandRefusedException("command_id_required", "Assignment needs a command id.");
        if (command.ComponentSerials.Count == 0 || command.ComponentSerials.Any(string.IsNullOrWhiteSpace))
            throw new Yojana.Serialisation.Contracts.CommandRefusedException("components_required", "Scan at least one component serial.");
        if (command.ComponentSerials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != command.ComponentSerials.Count)
            throw new Yojana.Serialisation.Contracts.CommandRefusedException("component_repeated", "A component serial was scanned more than once.");
        var replay = await context.Db.ProductionComponentAssignments.AsNoTracking().Where(row =>
            row.TenantId == context.Tenant.TenantId && row.CommandId == command.CommandId).ToListAsync(cancellationToken);
        if (replay.Count > 0)
            return new(replay[0].ParentUnitId, replay.Select(row => row.ComponentUnitId).ToList(),
                await context.Db.ProductionComponentHolds.AsNoTracking().Where(row => row.TenantId == context.Tenant.TenantId
                    && replay.Select(item => item.HoldId).Contains(row.HoldId) && row.AssemblyLinkId != null)
                    .Select(row => row.AssemblyLinkId!.Value).ToListAsync(cancellationToken), true);
        var parent = await context.Resolver.ResolveAsync(new DocumentSerial(command.ParentSerial), null,
            cancellationToken) ?? throw new Yojana.Serialisation.Contracts.CommandRefusedException("parent_unknown", "The parent serial is unknown.",
            "scan a commissioned parent serial");
        var componentIds = new List<Guid>();
        foreach (var serial in command.ComponentSerials)
        {
            var component = await context.Resolver.ResolveAsync(new DocumentSerial(serial), null, cancellationToken)
                ?? throw new Yojana.Serialisation.Contracts.CommandRefusedException("component_unknown", $"Component serial '{serial}' is unknown.",
                    "scan a commissioned component serial");
            componentIds.Add(component.UnitId);
        }
        // A unit can have earlier holds (linked, or returned and issued again); only its open hold on this order counts.
        var holds = await context.Db.ProductionComponentHolds.Where(row => row.TenantId == context.Tenant.TenantId
            && componentIds.Contains(row.UnitId) && row.ProductionOrderType == command.ProductionOrder.Type
            && row.ProductionOrderId == command.ProductionOrder.Id && row.LinkedAt == null && row.ReturnedAt == null)
            .ToListAsync(cancellationToken);
        if (holds.Count != componentIds.Count)
            throw new Yojana.Serialisation.Contracts.CommandRefusedException("component_not_held_for_order", "A scanned component is not held in production.",
                "refresh the order and scan an open held component");
        var assembly = await Components.AssignAsync(context, command.CommandId, command.ProductionOrder,
            parent.UnitId, holds, command.ActorRef, command.LegalEntityId, context.Clock.UtcNow, cancellationToken);
        return new(parent.UnitId, componentIds, assembly.LinkIds, false);
    }, cancellationToken);
}

/// <summary>Tenant setting; 30 days is an estimate with no source and can be changed per customer.</summary>
public sealed class ProductionHoldAttentionSettings(ITenantSettings settings)
{
    public const string Key = "production_hold_attention_days";
    public const int DefaultDays = 30;

    public async Task<int> DaysAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(Key, cancellationToken);
        return int.TryParse(raw, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650
            ? days : DefaultDays;
    }
}

public sealed class ProductionHoldAttentionSweep(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    ProductionHoldAttentionSettings settings)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-await settings.DaysAsync(cancellationToken));
        var orders = await db.ProductionComponentHolds.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.LinkedAt == null && row.ReturnedAt == null && row.IssuedAt < cutoff)
            .Select(row => new { row.ProductionOrderType, row.ProductionOrderId }).Distinct()
            .ToListAsync(cancellationToken);
        foreach (var order in orders)
            if (!await db.ProductionHoldTasks.AnyAsync(task => task.TenantId == tenant.TenantId
                && task.ProductionOrderType == order.ProductionOrderType
                && task.ProductionOrderId == order.ProductionOrderId && task.Kind == "attention"
                && task.Status == "open", cancellationToken))
                db.ProductionHoldTasks.Add(new ProductionHoldTask
                {
                    TenantId = tenant.TenantId,
                    TaskId = TimeOrderedId.New(clock.UtcNow),
                    ProductionOrderType = order.ProductionOrderType,
                    ProductionOrderId = order.ProductionOrderId,
                    Kind = "attention",
                    Status = "open",
                    RaisedAt = clock.UtcNow,
                });
        await db.SaveChangesAsync(cancellationToken);
    }
}

[DisallowConcurrentExecution]
public sealed class ProductionHoldAttentionJob(ITenantContextSetter setter, ProductionHoldAttentionSweep sweep)
    : TenantJob(setter)
{
    public const string JobName = "serialisation.production_hold_attention";
    public const string Cron = "0 15 4 * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        sweep.RunAsync(cancellationToken);
}
