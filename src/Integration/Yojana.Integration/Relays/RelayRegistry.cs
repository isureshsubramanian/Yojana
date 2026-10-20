using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Relays;

public sealed class RelayRegistry(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IServiceProvider services, IConnectionAuditSink audit, RelaySigningKeyProtection keys)
{
    public async Task<(Connection Connection, IRelayConnectorProfile Profile)> ProfileAsync(Guid connectionId, CancellationToken ct, bool requireRelay = true)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status == ConnectionStatus.Active, ct)
            ?? throw new ConnectionRuleException("connection_not_found", "The active connection was not found.");
        var profile = services.GetKeyedService<IRelayConnectorProfile>(connection.ConnectorCode)
            ?? throw new ConnectionRuleException("relay_unsupported", "The connector has no relay profile.");
        if (requireRelay && (await profile.OptionsAsync(connectionId, ct)).Transport != "site_relay")
            throw new ConnectionRuleException("relay_transport_required", "Choose site_relay for this connection first.");
        return (connection, profile);
    }

    public async Task<SiteRelayIssued> RegisterAsync(Guid connectionId, RegisterSiteRelay request, string actor, CancellationToken ct)
    {
        var (_, profile) = await ProfileAsync(connectionId, ct);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 160
            || !request.ProductUrl.IsAbsoluteUri || request.ProductUrl.Scheme != "https" && !request.ProductUrl.IsLoopback
            || !string.IsNullOrEmpty(request.ProductUrl.UserInfo))
            throw new ConnectionRuleException("relay_invalid", "A relay name and HTTPS product URL are required.");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var id = Guid.CreateVersion7(clock.UtcNow);
        var configuration = await profile.ConfigurationAsync(connectionId, tenant.TenantId.Value, id, request.ProductUrl, ct);
        db.SiteRelays.Add(new()
        {
            TenantId = tenant.TenantId, RelayId = id, ConnectionId = connectionId,
            DisplayName = request.DisplayName.Trim(), CreatedAt = clock.UtcNow,
            SigningKeyEnvelope = await keys.EncryptAsync(tenant.TenantId, connectionId, id, secret, ct),
        });
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, id, ConnectionAuditEventType.RelayRegistered, actor, ct);
        return new(id, secret, configuration);
    }

    public async Task<SiteRelayRotated> RotateAsync(Guid connectionId, Guid relayId, RotateSiteRelay request, string actor, CancellationToken ct)
    {
        if (request.OverlapMinutes is < 1 or > 1440)
            throw new ConnectionRuleException("relay_rotation_invalid", "Rotation overlap must be 1 to 1440 minutes.");
        _ = await ProfileAsync(connectionId, ct);
        // TIGHT COUPLING (relay nonce admission): RelayRegistry and RelayAuthentication share the
        // tenant/relay "relay-auth" lock only for nonce admission and credential mutation. Authentication
        // verifies outside it, then rechecks the envelope under it; separating the locks admits revoked keys.
        await using var operation = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId, $"relay-auth:{relayId:N}", ct);
        var row = await RelayAsync(connectionId, relayId, ct);
        if (row.Status != "active") throw new ConnectionRuleException("relay_revoked", "A revoked relay cannot rotate.");
        if (request.Force && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000))
            throw new ConnectionRuleException("relay_rotation_reason_required", "Forced rotation needs a reason of at most 2000 characters.");
        if (!request.Force && row.PreviousSecretExpiresAt > clock.UtcNow)
            throw new ConnectionRuleException("relay_rotation_pending", "Wait until the previous rotation overlap ends.");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        row.PreviousSigningKeyEnvelope = request.Force ? null : row.SigningKeyEnvelope;
        row.PreviousSecretExpiresAt = request.Force ? clock.UtcNow : clock.UtcNow.AddMinutes(request.OverlapMinutes);
        row.SigningKeyEnvelope = await keys.EncryptAsync(tenant.TenantId, connectionId, relayId, secret, ct);
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, relayId, ConnectionAuditEventType.RelayRotated, actor, ct, request.Reason, request.Force);
        return new(relayId, secret, row.PreviousSecretExpiresAt.Value);
    }

    public async Task<RelayReceipt> RevokeAsync(Guid connectionId, Guid relayId, string actor, CancellationToken ct)
    {
        // TIGHT COUPLING (relay nonce admission): RelayRegistry and RelayAuthentication share the
        // tenant/relay "relay-auth" lock only for nonce admission and credential mutation. Authentication
        // verifies outside it, then rechecks the envelope under it; separating the locks admits revoked keys.
        await using var operation = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId, $"relay-auth:{relayId:N}", ct);
        var row = await RelayAsync(connectionId, relayId, ct);
        if (row.Status == "revoked") return new(true);
        row.Status = "revoked";
        row.SigningKeyEnvelope = "";
        row.PreviousSigningKeyEnvelope = null;
        row.PreviousSecretExpiresAt = null;
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, relayId, ConnectionAuditEventType.RelayRevoked, actor, ct);
        return new(true);
    }

    public async Task<SiteRelayPage> ListAsync(Guid connectionId, string? cursor, int take, CancellationToken ct)
    {
        var (_, profile) = await ProfileAsync(connectionId, ct, requireRelay: false);
        take = Math.Clamp(take, 1, 100);
        var query = db.SiteRelays.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId);
        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var after)) throw new ConnectionRuleException("cursor_invalid", "The relay cursor is invalid.");
            query = query.Where(x => x.RelayId.CompareTo(after) > 0);
        }
        var rows = await query.OrderBy(x => x.RelayId).Take(take + 1).ToListAsync(ct);
        var cutoff = clock.UtcNow.AddSeconds(-(await profile.OptionsAsync(connectionId, ct)).OfflineAfterSeconds);
        var connected = await db.SiteRelays.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.Status == "active" && x.LastSeenAt >= cutoff, ct);
        return new(rows.Take(take).Select(row => new SiteRelayView(row.RelayId, row.DisplayName, row.Status, row.CreatedAt,
            row.LastSeenAt, row.AgentVersion, row.HostName, row.PreviousSecretExpiresAt)).ToList(),
            rows.Count > take ? rows[take - 1].RelayId.ToString("D") : null, connected);
    }

    private async Task<SiteRelay> RelayAsync(Guid connectionId, Guid relayId, CancellationToken ct) =>
        await db.SiteRelays.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.RelayId == relayId, ct) ?? throw new ConnectionRuleException("relay_not_found", "The relay was not found.");

    private Task<ConnectionAuditEvent> AuditAsync(Guid connection, Guid relay, ConnectionAuditEventType kind,
        string actor, CancellationToken ct, string? reason = null, bool forced = false) => audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow),
            connection, kind, actor, clock.UtcNow, System.Text.Json.JsonSerializer.Serialize(new { RelayId = relay, Reason = reason, Forced = forced })), ct);
}
