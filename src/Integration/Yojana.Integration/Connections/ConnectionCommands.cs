using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connections;

public sealed class ConnectionCommands(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IServiceProvider services, ConnectionCredentialProtector protector, IConnectionCredentialStore credentials,
    ConnectionThrottleProfiles throttles, ConnectorOnboardingChecklistGenerator checklist,
    ConnectionHealthProjection health, ConnectionHealthSettings settings, IConnectionAuditSink audit)
{
    public async Task<RegistrationResult> RegisterAsync(RegisterConnection request, string actor, CancellationToken ct)
    {
        Actor(actor);
        if (request.RequestId == Guid.Empty || request.LegalEntityId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DisplayLabel) || request.DisplayLabel.Length > 160)
            throw new ConnectionRuleException("connection_invalid", "Request id, legal entity and a label of at most 160 characters are required.");
        var adapter = Adapter(request.ConnectorCode);
        await using var operation = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId,
            $"register:{request.RequestId:N}", ct);
        var connection = await db.Connections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.RegistrationRequestId == request.RequestId, ct);
        if (connection is not null && connection.ConnectorCode != request.ConnectorCode)
            throw new ConnectionRuleException("registration_request_conflict", "The request id already belongs to another connector.");
        if (connection?.RegistrationCompleted == true)
            return await ResultAsync(connection, null, ct);
        if (connection?.Status == ConnectionStatus.Archived)
            throw new ConnectionRuleException("connection_archived", "An archived connection cannot be registered again.");
        var catalogue = await db.ConnectorCatalogue.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectorCode == request.ConnectorCode, ct);
        if (catalogue is null || !catalogue.Enabled)
            throw new ConnectionRuleException("connector_unavailable", "Enable this connector in the tenant catalogue before registering it.");
        try
        {
            adapter.ValidateSettings(request.ConnectorSettings);
            request.Throttle?.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ConnectionRuleException("connection_settings_invalid", "The connector settings or throttle parameters are invalid.");
        }
        if (request.Credentials is null || request.Credentials.Count > 8
            || request.Credentials.Select(x => x.Purpose).Distinct(StringComparer.Ordinal).Count() != request.Credentials.Count
            || adapter.RequiredCredentialPurposes(request.ConnectorSettings).Any(purpose => !request.Credentials.Any(x => x.Purpose == purpose))
            || request.Credentials.Any(x => string.IsNullOrWhiteSpace(x.Secret) || string.IsNullOrWhiteSpace(x.Purpose)
                || x.Purpose.Length > 100 || x.Secret.Length > 64000))
            throw new ConnectionRuleException("connection_credentials_invalid", "Supply the required credentials once per purpose.");
        if (connection is null)
        {
            connection = new()
            {
                TenantId = tenant.TenantId, ConnectionId = Guid.CreateVersion7(clock.UtcNow),
                RegistrationRequestId = request.RequestId, ConnectorCode = request.ConnectorCode,
                DisplayLabel = request.DisplayLabel.Trim(), Status = ConnectionStatus.Registering, CreatedAt = clock.UtcNow,
            };
            db.Connections.Add(connection);
            db.ConnectionGatewaySettings.Add(new()
            {
                TenantId = tenant.TenantId, ConnectionId = connection.ConnectionId, LegalEntityId = request.LegalEntityId,
                OutboundMode = adapter.DefaultOutboundMode, ReadOnly = true, NativeWarrantyObjectMode = "frozen",
            });
            await db.SaveChangesAsync(ct);

        }
        var id = connection.ConnectionId;
        if (!await db.ConnectionEvents.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
            && x.EventType == nameof(ConnectionAuditEventType.ConnectionRegistered), ct))
            await Audit(id, ConnectionAuditEventType.ConnectionRegistered, actor, "{}", ct);
        var thresholds = await settings.GetAsync(id, ct);
        foreach (var secret in request.Credentials)
        {
            if (await credentials.GetCurrentAsync(tenant.TenantId, id, secret.Purpose, ct) is not null) continue;
            var bytes = Encoding.UTF8.GetBytes(secret.Secret);
            try
            {
                await credentials.SaveAsync(await protector.EncryptAsync(tenant.TenantId, id, secret.Purpose,
                    bytes, secret.ExpiresAt, secret.ReplaceBy ?? clock.UtcNow.AddDays(thresholds.CredentialRotationDays), ct), ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var declaration = await adapter.ConfigureAsync(id, request.ConnectorSettings, ct);
        await throttles.OnboardAsync(new(tenant.TenantId, id), declaration.Throttle, ct);
        if (request.Throttle is not null) await throttles.UpdateAsync(new(tenant.TenantId, id), request.Throttle, ct);
        if (!await db.ConnectionCapabilities.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct))
            db.ConnectionCapabilities.AddRange(declaration.Capabilities.Select(x => new ConnectionCapability
            {
                TenantId = tenant.TenantId, ConnectionId = id, CanonicalMessage = x.Key, SupportLevel = x.Value,
            }));
        if (!await db.ConnectionPollWatermarks.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct))
            db.ConnectionPollWatermarks.AddRange(declaration.PollEntities.Select(entity => new ConnectionPollWatermark
            {
                TenantId = tenant.TenantId, ConnectionId = id, EntityType = entity.Key, CanonicalMessage = entity.Value,
                IntervalSeconds = 120, OverlapSeconds = 300, NextPollAt = clock.UtcNow,
            }));
        if (!await db.ConnectionNotificationSources.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct))
            db.ConnectionNotificationSources.AddRange(declaration.NotificationSources.Select(source => new ConnectionNotificationSource
            {
                TenantId = tenant.TenantId, ConnectionId = id, Source = source, Enabled = true, UpdatedAt = clock.UtcNow,
            }));
        if (!await db.ConnectionDegradationSettings.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct))
            for (var rule = 1; rule <= 7; rule++)
                db.ConnectionDegradationSettings.Add(new()
                {
                    TenantId = tenant.TenantId, ConnectionId = id, RuleNumber = rule, Enabled = true,
                    SettingsJson = "{}", UpdatedAt = clock.UtcNow,
                });
        connection.Status = ConnectionStatus.Active;
        await db.SaveChangesAsync(ct);
        // A relay is provisioned after registration. Do not hold the connection operation lock while
        // waiting for an agent that cannot yet have been registered by the administrator.
        var profile = services.GetKeyedService<Yojana.Integration.Relays.IRelayConnectorProfile>(connection.ConnectorCode);
        var relayOptions = profile is null ? null : await profile.OptionsAsync(id, ct);
        var result = relayOptions?.Transport == "site_relay"
            ? await ResultAsync(connection, null, ct) : await ProbeAsync(connection, adapter, ct);
        connection.RegistrationCompleted = true;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<RegistrationResult> SmokeReadAsync(Guid id, CancellationToken ct)
    {
        await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, id, ct);
        var connection = await ConnectionAsync(id, ct);
        if (connection.Status != ConnectionStatus.Active)
            throw new ConnectionRuleException("connection_inactive", "Only an active connection may run a smoke read.");
        return await ProbeAsync(connection, Adapter(connection.ConnectorCode), ct);
    }

    private async Task<RegistrationResult> ProbeAsync(Connection connection, IConnectionLifecycleAdapter adapter, CancellationToken ct)
    {
        ConnectionProbe? probe = null;
        try
        {
            probe = await adapter.SmokeReadAsync(connection.ConnectionId, ct);
            connection.LastSmokeReadSucceeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Remote text can contain credentials or connection details. Only a stable local error reaches health.
            connection.LastSmokeReadSucceeded = false;
        }
        connection.LastSmokeReadAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await health.RefreshAsync(connection.ConnectionId, ct);
        return await ResultAsync(connection, probe, ct);
    }

    private async Task<RegistrationResult> ResultAsync(Connection connection, ConnectionProbe? probe, CancellationToken ct)
    {
        var generated = await checklist.GenerateAsync(connection.ConnectionId, ct);
        var snapshot = await db.ConnectionHealth.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connection.ConnectionId, ct);
        var checks = generated.Items.Select(item => new OnboardingCheck(item.Code, item.Label,
            item.Code == "credentials" ? connection.LastSmokeReadSucceeded == true ? "pass" : "fail"
                : item.Status == OnboardingItemStatus.Confirmed ? "pass" : "needs_owner",
            item.Detail)).ToList();
        checks.Add(new("smoke_read", "Read-only connection check", connection.LastSmokeReadSucceeded == true
            ? "pass" : "fail", "Reads authentication, metadata and release evidence without changing remote business data."));
        checks.Add(new("healthy_sweep", "Healthy daily check", snapshot?.IsLive == true ? "pass" : "needs_owner", "A healthy sweep is required before the connection is live."));
        var state = await db.ConnectionHealth.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connection.ConnectionId).Select(x => x.Status).SingleOrDefaultAsync(ct) ?? "degraded";
        probe ??= await Adapter(connection.ConnectorCode).LatestProbeAsync(connection.ConnectionId, ct);
        return new(connection.ConnectionId, state, checks, probe?.SurveySummary);
    }

    public async Task<ArchiveConnectionResult> ArchiveAsync(Guid id, ArchiveConnection request,
        string actor, bool isAdministrator, CancellationToken ct)
    {
        Actor(actor);
        if (request.Force && !isAdministrator)
            throw new ConnectionRuleException("force_archive_forbidden", "Only a tenant administrator may force an archive.");
        if (request.Force && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000))
            throw new ConnectionRuleException("force_archive_reason_required", "Force archive requires a reason of at most 2000 characters.");
        await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, id, ct);
        var connection = await ConnectionAsync(id, ct);
        var preconditions = new ArchivePreconditions(
            await db.OutboundCommands.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && (x.Status == "pending" || x.Status == "retry" || x.Status == "processing"
                    || x.Status == "awaiting_review"), ct),
            await db.DeadLetterItems.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && (x.Status == "needs_attention" || x.Status == "retry_pending"), ct),
            await db.BackfillCursors.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && x.Status == "running", ct),
            await db.ManualOutboundTasks.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && x.Status == "open", ct));
        if (connection.Status == ConnectionStatus.Archived) return new(id, true, preconditions);
        if (preconditions.Blocked && !request.Force) return new(id, false, preconditions);
        // Unsubscribe before retiring credentials so the adapter can authenticate. Only an administrator's
        // reasoned force archive can leave remote cleanup to the customer's administrator via the integration desk.
        var remoteSubscriptionLeft = false;
        try { await Adapter(connection.ConnectorCode).UnsubscribeAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!request.Force) return new(id, false, preconditions, "unsubscribe_failed");
            remoteSubscriptionLeft = true;
        }
        await using (var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct))
        {
            var now = clock.UtcNow;
            if (remoteSubscriptionLeft)
            {
                db.ManualOutboundTasks.Add(new()
                {
                    TenantId = tenant.TenantId, ManualOutboundTaskId = Guid.CreateVersion7(now), ConnectionId = id,
                    CommandType = ManualTaskCommandType.RemoveRemoteSubscription, Desk = "integration",
                    Draft = "A subscription may remain on the remote system. Ask the customer's administrator to remove it there, then complete this task.",
                    Status = ManualTaskStatus.Open, CreatedAt = now,
                });
                // TIGHT COUPLING (archive audit transaction): EfConnectionAuditSink shares this scoped
                // IntegrationDbContext transaction. No distributed transaction spans archive and audit storage;
                // the SQL audit must join this SERIALIZABLE transaction or cleanup can commit without its audit.
                await Audit(id, ConnectionAuditEventType.RemoteSubscriptionLeft, actor,
                    System.Text.Json.JsonSerializer.Serialize(new { ErrorCode = "unsubscribe_failed" }), ct);
            }
            connection.Status = ConnectionStatus.Archived;
            connection.ArchivedAt = now;
            await db.ConnectionNotificationSources.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false).SetProperty(x => x.UpdatedAt, now), ct);
            await db.ConnectionCredentials.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id && x.RetiredAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RetiredAt, now), ct);
            await db.BackfillCursors.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && x.Status != "completed" && x.Status != "cancelled")
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled")
                    .SetProperty(x => x.CompletedAt, now).SetProperty(x => x.LastChangedBy, actor), ct);
            await db.ExternalIdMaps.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id && x.ValidTo == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ValidTo, now), ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await Audit(id, ConnectionAuditEventType.ConnectionArchived, actor,
            System.Text.Json.JsonSerializer.Serialize(new { request.Force, request.Reason, Preconditions = preconditions }), ct);
        await health.RefreshAsync(id, ct);
        return new(id, true, preconditions, remoteSubscriptionLeft ? "unsubscribe_failed" : null);
    }

    private IConnectionLifecycleAdapter Adapter(string code) => services.GetKeyedService<IConnectionLifecycleAdapter>(code)
        ?? throw new ConnectionRuleException("connector_unavailable", "No lifecycle adapter is installed for this connector.");
    private async Task<Connection> ConnectionAsync(Guid id, CancellationToken ct) =>
        await db.Connections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct)
        ?? throw new ConnectionRuleException("connection_not_found", "The connection was not found.");
    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ConnectionRuleException("audit_actor_required", "An authenticated actor is required.");
    }
    private Task<ConnectionAuditEvent> Audit(Guid id, ConnectionAuditEventType kind, string actor, string details, CancellationToken ct) =>
        audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), id, kind, actor, clock.UtcNow, details), ct);
}

/// <summary>Database-scoped operation serialisation, including requests served by different hosts.</summary>
internal sealed class ConnectionOperationLock(IntegrationDbContext db, string resource) : IAsyncDisposable
{
    public static Task<ConnectionOperationLock> ForConnectionAsync(IntegrationDbContext db, TenantId tenant,
        Guid connectionId, CancellationToken ct) => AcquireAsync(db, tenant, $"connection:{connectionId:N}", ct);

    public static async Task<ConnectionOperationLock> AcquireAsync(IntegrationDbContext db, TenantId tenant,
        string operation, CancellationToken ct)
    {
        var resource = $"integration:{tenant}:{operation}";
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=15000; IF @r < 0 THROW 51000, 'Connection operation is busy.', 1;", ct);
            return new(db, resource);
        }
        catch { await db.Database.CloseConnectionAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_releaseapplock @Resource={resource}, @LockOwner='Session'"); }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
