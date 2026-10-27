using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Wolverine;

namespace Yojana.Serialisation.Documents;

/// <summary>Everything a document handler reaches for, resolved once per message scope so the handlers stay readable.</summary>
public sealed class DocumentContext(
    SerialisationDbContext db, IUnitEventWriter writer, UnitEventIdempotencyReader idempotency, BulkIngestion bulk, IdentifierService identifiers, EffectiveItemProfiles items, SerialRangeService ranges, SerialReuseSettings reuseSettings, ITenantContext tenant, IClock clock,
    EpisodeService episodes, UnitResolver resolver, DocumentOptions options, PostingPeriods periods, AsBuiltCompleteness completeness,
    IPurchasingDocumentQueryPort purchasing, IManufacturingDocumentQueryPort manufacturing, IInventoryDocumentQueryPort inventory, ISalesDocumentQueryPort sales, IServiceDocumentQueryPort service,
    IItemPolicyPort itemPolicies, IBomQueryPort bom, IUnitSaleShipmentGuard saleShipmentGuard, SerialConfirmationSettings serialConfirmation, IMessageBus bus)
{
    public SerialisationDbContext Db => db;
    public IUnitEventWriter Writer => writer;
    public BulkIngestion Bulk => bulk;
    public ITenantContext Tenant => tenant;
    public IClock Clock => clock;
    public EpisodeService Episodes => episodes;
    public UnitResolver Resolver => resolver;
    public DocumentOptions Options => options;
    public PostingPeriods Periods => periods;
    public AsBuiltCompleteness Completeness => completeness;
    public IPurchasingDocumentQueryPort Purchasing => purchasing;
    public IManufacturingDocumentQueryPort Manufacturing => manufacturing;
    public IInventoryDocumentQueryPort Inventory => inventory;
    public ISalesDocumentQueryPort Sales => sales;
    public IServiceDocumentQueryPort Service => service;
    public IItemPolicyPort ItemPolicies => itemPolicies;
    public IBomQueryPort Bom => bom;
    public IUnitSaleShipmentGuard SaleShipmentGuard => saleShipmentGuard;
    public SerialConfirmationSettings SerialConfirmation => serialConfirmation;
    public IMessageBus Bus => bus;

    /// <summary>
    /// Runs a document under one transaction (the caller's when open). Handlers may call SaveChanges inside so that rows
    /// written by an earlier step (units commissioned) are visible to a later one (components assembled) without leaving
    /// the transaction; the commit happens once at the end, or never.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        var owned = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = owned ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var result = await body();
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public Task<CommissionUnitsResult> CommissionAsync(CommissionUnits command, CancellationToken cancellationToken) =>
        // Coupled to SerialisationModule's SerialReuseSettings factory: a direct ITenantSettings dependency here would
        // expose PlatformDbContext beside SerialisationDbContext and make Wolverine refuse the document-handler graph.
        CommissionUnitsHandler.Handle(command, db, writer, bulk, identifiers, items, ranges, reuseSettings, tenant, clock, cancellationToken);

    public Task<RecordAssemblyResult> AssembleAsync(RecordAssembly command, CancellationToken cancellationToken) =>
        RecordAssemblyHandler.Handle(command, db, writer, tenant, cancellationToken);

    /// <summary>True when this document line already produced an event for the unit (a replay through another path).</summary>
    public Task<bool> DocumentAppliedAsync(Guid unitId, DocumentRef document, int line, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.UnitId == unitId && e.DocumentType == document.Type && e.DocumentId == document.Id && e.DocumentLine == line, cancellationToken);
    }

    /// <summary>The units this document line already produced an event for, in one read of the document index (P1-T24).</summary>
    public async Task<HashSet<Guid>> UnitsAppliedAsync(DocumentRef document, int line, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return (await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == document.Type && e.DocumentId == document.Id && e.DocumentLine == line).Select(e => e.UnitId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>
    /// The (key, business time) pairs among these that the ledger holds already (P1-T24). Naming the business times as
    /// well lets the unique (tenant, key, event_time) index seek only the partitions of those times; a document's events
    /// share one or two.
    /// </summary>
    public async Task<HashSet<(string Key, DateTime At)>> SeenManyAsync(IReadOnlyCollection<(string Key, DateTime At)> keys, CancellationToken cancellationToken)
        => await idempotency.SeenAsync(tenant.TenantId, keys, cancellationToken);

    /// <summary>True when an event with this key was recorded before.</summary>
    public Task<bool> SeenAsync(string idempotencyKey, DateTime eventTime, CancellationToken cancellationToken)
        => idempotency.SeenAsync(tenant.TenantId, idempotencyKey, eventTime, cancellationToken);
}
