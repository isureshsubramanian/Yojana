using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.LegacyMigration;

public sealed class MigrationSourceService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    MigrationSourceSecret secret,
    MigrationLicenceSettings licenceSettings,
    IConnectionAuditSink audit,
    IUserDirectory people,
    Staging.SourceExtractor extractor)
{
    public async Task<MigrationSourceView> CreateAsync(CreateMigrationSourceRequest request, string actor,
        CancellationToken cancellationToken)
    {
        Actor(actor);
        Validate(request.DisplayLabel, request.SourceSystem, request.Runtime, request.Route, request.DatabaseName,
            request.SchemaName, request.LinkedServerName, request.CommandTimeoutSeconds, request.ConnectionString, false);
        var defaultPosition = await licenceSettings.DefaultPositionAsync(cancellationToken);
        if (defaultPosition == MigrationLicencePosition.Confirmed &&
            (string.IsNullOrWhiteSpace(request.LicenceContractReference) || string.IsNullOrWhiteSpace(request.LicenceNote)))
            throw new MigrationRuleException("migration_extraction_licence_evidence_required",
                "The tenant's confirmed default requires a contract reference and note.");
        var now = clock.UtcNow;
        var row = new MigrationSource
        {
            TenantId = tenant.TenantId,
            ConnectionId = Guid.CreateVersion7(now),
            DisplayLabel = request.DisplayLabel.Trim(),
            SourceSystem = request.SourceSystem.Trim().ToLowerInvariant(),
            Runtime = request.Runtime,
            Route = request.Route,
            DatabaseName = request.DatabaseName.Trim(),
            SchemaName = request.SchemaName.Trim(),
            LinkedServerName = NullIfWhiteSpace(request.LinkedServerName),
            CommandTimeoutSeconds = request.CommandTimeoutSeconds,
            LicencePosition = defaultPosition,
            LicenceConfirmedBy = defaultPosition == MigrationLicencePosition.Confirmed ? actor : null,
            LicenceConfirmedAt = defaultPosition == MigrationLicencePosition.Confirmed ? now : null,
            LicenceContractReference = defaultPosition == MigrationLicencePosition.Confirmed
                ? request.LicenceContractReference!.Trim() : null,
            LicenceNote = defaultPosition == MigrationLicencePosition.Confirmed ? request.LicenceNote!.Trim() : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MigrationSources.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new MigrationRuleException("migration_source_conflict",
                "A migration source with that source-system code already exists.");
        }
        if (request.ConnectionString is not null)
            await secret.SetAsync(row.ConnectionId, request.ConnectionString, cancellationToken);
        if (row.LicencePosition == MigrationLicencePosition.Confirmed)
            await AuditLicenceAsync(row, ConnectionAuditEventType.MigrationLicenceConfirmed, actor, cancellationToken);
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<MigrationSourceView> UpdateAsync(UpdateMigrationSourceRequest request, string actor,
        CancellationToken cancellationToken)
    {
        Actor(actor);
        Validate(request.DisplayLabel, request.SourceSystem, request.Runtime, request.Route, request.DatabaseName,
            request.SchemaName, request.LinkedServerName, request.CommandTimeoutSeconds, request.ConnectionString, true);
        var row = await SourceAsync(request.SourceId, cancellationToken);
        row.DisplayLabel = request.DisplayLabel.Trim();
        row.SourceSystem = request.SourceSystem.Trim().ToLowerInvariant();
        row.Runtime = request.Runtime;
        row.Route = request.Route;
        row.DatabaseName = request.DatabaseName.Trim();
        row.SchemaName = request.SchemaName.Trim();
        row.LinkedServerName = NullIfWhiteSpace(request.LinkedServerName);
        row.CommandTimeoutSeconds = request.CommandTimeoutSeconds;
        row.UpdatedAt = clock.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new MigrationRuleException("migration_source_conflict",
                "A migration source with that source-system code already exists.");
        }
        if (request.ConnectionString is not null)
            await secret.SetAsync(row.ConnectionId, request.ConnectionString, cancellationToken);
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<MigrationSourcePage> ListAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.MigrationSources.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (Decode(cursor) is { } after)
            query = query.Where(x => x.CreatedAt < after.At ||
                (x.CreatedAt == after.At && x.ConnectionId.CompareTo(after.Id) < 0));
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.ConnectionId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var items = new List<MigrationSourceView>(Math.Min(take, rows.Count));
        foreach (var row in rows.Take(take)) items.Add(await ViewAsync(row, cancellationToken));
        var next = rows.Count > take && items.Count != 0
            ? Encode(new(items[^1].CreatedAt, items[^1].SourceId)) : null;
        return new(items, next);
    }

    public async Task<MigrationSourceView> SetLicenceAsync(Guid sourceId, UpdateMigrationLicenceRequest request,
        string actor, CancellationToken cancellationToken)
    {
        Actor(actor);
        if (request.Confirmed && (string.IsNullOrWhiteSpace(request.ContractReference) ||
                                  string.IsNullOrWhiteSpace(request.Note)))
            throw new MigrationRuleException("migration_extraction_licence_evidence_required",
                "Confirmation requires the customer contract reference and a note.");
        if (request.ContractReference?.Length > 500 || request.Note?.Length > 2000)
            throw new MigrationRuleException("migration_extraction_licence_evidence_invalid",
                "The contract reference or note is too long.");
        var row = await SourceAsync(sourceId, cancellationToken);
        row.LicencePosition = request.Confirmed ? MigrationLicencePosition.Confirmed : MigrationLicencePosition.Unconfirmed;
        row.LicenceConfirmedBy = request.Confirmed ? actor : null;
        row.LicenceConfirmedAt = request.Confirmed ? clock.UtcNow : null;
        row.LicenceContractReference = request.Confirmed ? request.ContractReference!.Trim() : null;
        row.LicenceNote = request.Confirmed ? request.Note!.Trim() : NullIfWhiteSpace(request.Note);
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await AuditLicenceAsync(row, request.Confirmed ? ConnectionAuditEventType.MigrationLicenceConfirmed
            : ConnectionAuditEventType.MigrationLicenceWithdrawn, actor, cancellationToken);
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<MigrationSourceTestResult> TestAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var row = await SourceAsync(sourceId, cancellationToken);
        if (row.Runtime != MigrationSourceRuntime.SqlServer)
            return new(sourceId, Described(Staging.SourceProtocol.Tables.Select(table =>
                new MigrationSourceTestTable(table.SourceTable, null, "migration_source_runtime_unsupported"))));
        return new(sourceId, Described(await extractor.CountAsync(row, cancellationToken)));
    }

    private static List<MigrationSourceTestTable> Described(IEnumerable<MigrationSourceTestTable> tables) =>
        tables.Select(table =>
        {
            var (role, label) = Staging.SourceProtocol.Describe(table.Table);
            return table with { Role = role, Label = label };
        }).ToList();

    internal async Task<MigrationSource> SourceAsync(Guid sourceId, CancellationToken cancellationToken) =>
        await db.MigrationSources.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId &&
            x.ConnectionId == sourceId, cancellationToken)
        ?? throw new MigrationRuleException("migration_source_not_found", "The migration source was not found.");

    private async Task<MigrationSourceView> ViewAsync(MigrationSource row, CancellationToken cancellationToken)
    {
        var names = row.LicenceConfirmedBy is null
            ? null : await people.DisplayNamesAsync([row.LicenceConfirmedBy], cancellationToken);
        var confirmedByName = row.LicenceConfirmedBy is null ? null
            : names!.GetValueOrDefault(row.LicenceConfirmedBy) ?? row.LicenceConfirmedBy;
        return new(row.ConnectionId, row.DisplayLabel, row.SourceSystem, row.Runtime, row.Route, row.DatabaseName,
            row.SchemaName, row.LinkedServerName, row.CommandTimeoutSeconds,
            await secret.IsSetAsync(row.ConnectionId, cancellationToken), row.LicencePosition,
            confirmedByName, row.LicenceConfirmedAt, row.LicenceContractReference, row.LicenceNote,
            row.CreatedAt, row.UpdatedAt);
    }

    private async Task AuditLicenceAsync(MigrationSource row, ConnectionAuditEventType eventType, string actor,
        CancellationToken cancellationToken) => await audit.AppendAsync(new(tenant.TenantId,
        Guid.CreateVersion7(clock.UtcNow), row.ConnectionId, eventType, actor, clock.UtcNow,
        JsonSerializer.Serialize(new
        {
            row.LicencePosition,
            row.LicenceContractReference,
            row.LicenceNote,
        })), cancellationToken);

    private static void Validate(string label, string sourceSystem, string runtime, string route, string database,
        string schema, string? linkedServer, int timeout, string? connectionString, bool editing)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 160 || string.IsNullOrWhiteSpace(sourceSystem) ||
            sourceSystem.Length > 40 || string.IsNullOrWhiteSpace(database) || database.Length > 128 ||
            string.IsNullOrWhiteSpace(schema) || schema.Length > 128 || linkedServer?.Length > 128 ||
            timeout is < 1 or > 3600)
            throw new MigrationRuleException("migration_source_invalid", "The migration source details are invalid.");
        if (runtime != MigrationSourceRuntime.SqlServer && runtime != MigrationSourceRuntime.UnsupportedColumnar)
            throw new MigrationRuleException("migration_source_runtime_invalid", "Choose a supported runtime code.");
        if (route is not (MigrationSourceRoute.Separate or MigrationSourceRoute.CrossDatabase or
            MigrationSourceRoute.LinkedServer))
            throw new MigrationRuleException("migration_source_route_invalid",
                "Choose separate, cross_database or linked_server.");
        if (route == MigrationSourceRoute.LinkedServer && string.IsNullOrWhiteSpace(linkedServer))
            throw new MigrationRuleException("migration_source_linked_server_required",
                "A linked-server route requires its configured server name.");
        if (route == MigrationSourceRoute.Separate && !editing && string.IsNullOrWhiteSpace(connectionString))
            throw new MigrationRuleException("migration_source_connection_string_required",
                "A separate source requires a connection string.");
    }

    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 320)
            throw new MigrationRuleException("audit_actor_required", "The authenticated user has no actor id.");
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(cursor)));
    private static Cursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new MigrationRuleException("migration_cursor_invalid", "The page cursor is invalid.");
        }
    }

    private sealed record Cursor(DateTime At, Guid Id);
}
