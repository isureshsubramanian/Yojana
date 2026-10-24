using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Relay.Contracts;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connections;

public sealed class ConnectionOnboarding(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IServiceProvider services,
    IConnectionAuditSink audit)
{
    public async Task ApplyAsync(Connection connection, ConnectionSyncStart syncStart,
        string? certificatePin, string actor, CancellationToken ct)
    {
        var value = Validate(syncStart);
        var pin = NormaliseCertificatePin(certificatePin);
        var gateway = await db.ConnectionGatewaySettings.SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connection.ConnectionId, ct);
        var previousPin = gateway.ServiceCertificateSha256;
        gateway.SyncStartMode = value.Mode;
        gateway.SyncStartDate = value.Date;
        gateway.SyncStartConfiguredAt = clock.UtcNow;
        gateway.MasterDataFull = value.MasterDataFull;
        gateway.ServiceCertificateSha256 = pin;

        var seed = Seed(connection.ConnectorCode);
        var rows = await db.ConnectionPollWatermarks.Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connection.ConnectionId).ToListAsync(ct);
        var instant = StartInstant(value);
        foreach (var row in rows.Where(row => string.IsNullOrWhiteSpace(row.Watermark)))
        {
            if (value.MasterDataFull && seed.IsMasterData(row.EntityType)) continue;
            if (instant is { } at) row.Watermark = seed.WatermarkAt(at);
        }
        await db.SaveChangesAsync(ct);
        if (!StringComparer.Ordinal.Equals(previousPin, pin))
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow),
                connection.ConnectionId, ConnectionAuditEventType.ConnectionCertificatePinned, actor,
                clock.UtcNow, JsonSerializer.Serialize(new { Pinned = pin is not null })), ct);
    }

    public async Task<RebaselineResult> RebaselineAsync(Connection connection, RebaselineConnection request,
        string actor, CancellationToken ct)
    {
        if (request.To.Kind != DateTimeKind.Utc || request.To > clock.UtcNow
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2_000)
            throw new CommandRefusedException("sync_baseline_invalid",
                "Choose a UTC instant no later than now and give a reason of at most 2000 characters.");
        var seed = Seed(connection.ConnectorCode);
        var query = db.ConnectionPollWatermarks.Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connection.ConnectionId);
        if (request.Entities is { Count: > 0 })
        {
            if (request.Entities.Any(string.IsNullOrWhiteSpace)
                || request.Entities.Distinct(StringComparer.Ordinal).Count() != request.Entities.Count)
                throw new CommandRefusedException("sync_baseline_entities_invalid",
                    "Choose each entity at most once.");
            query = query.Where(x => request.Entities.Contains(x.EntityType));
        }
        var rows = await query.OrderBy(x => x.EntityType).ToListAsync(ct);
        if (rows.Count == 0 || request.Entities is { Count: > 0 } && rows.Count != request.Entities.Count)
            throw new CommandRefusedException("sync_baseline_entity_not_found",
                "One or more selected entities do not belong to this connection.");
        var backwards = rows.FirstOrDefault(row => row.Watermark is { Length: > 0 } watermark
            && seed.InstantOf(watermark) is { } current && request.To < current);
        if (backwards is not null)
            throw new CommandRefusedException("sync_baseline_backwards",
                $"The {backwards.EntityType} marker is already later than the requested instant.",
                "choose an instant at or after every selected marker");
        var from = rows.ToDictionary(row => row.EntityType,
            row => row.Watermark is { Length: > 0 } watermark ? seed.InstantOf(watermark) : null,
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.Watermark = seed.WatermarkAt(request.To);
            row.NextPollAt = clock.UtcNow;
            row.Revision++;
        }
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connection.ConnectionId,
            ConnectionAuditEventType.ConnectionSyncBaselined, actor, clock.UtcNow,
            JsonSerializer.Serialize(new
            {
                Entities = rows.Select(row => row.EntityType).ToList(), From = from, To = request.To,
                request.Reason, ChangesInGapSkipped = true,
            })), ct);
        return new(connection.ConnectionId, rows.Select(row => row.EntityType).ToList(), request.To);
    }

    public Task<FullHistoryEstimate> EstimateAsync(Connection connection, CancellationToken ct) =>
        Seed(connection.ConnectorCode).EstimateFullHistoryAsync(connection.ConnectionId, ct);

    public async Task<ServiceCertificateView> FetchCertificateAsync(Connection connection, CancellationToken ct)
    {
        var lifecycle = services.GetRequiredKeyedService<Connectors.IConnectionLifecycleAdapter>(connection.ConnectorCode);
        var target = await lifecycle.ServiceUriAsync(connection.ConnectionId, ct)
            ?? throw new ConnectionRuleException("certificate_fetch_unsupported",
                "This connector does not expose a service certificate target.");
        return await FetchCertificateAsync(target, ct);
    }

    public async Task<ServiceCertificateView> FetchCertificateAsync(Uri target, CancellationToken ct)
    {
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
            throw new CommandRefusedException("certificate_fetch_requires_https",
                "A server certificate can only be fetched from an HTTPS service address.");
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await tcp.ConnectAsync(target.Host, target.IsDefaultPort ? 443 : target.Port, timeout.Token);
        X509Certificate2? presented = null;
        // This callback observes the certificate for an explicit administrator confirmation; it does not authorise
        // an application request. Product HTTP transports retain normal CA validation unless the saved leaf pin matches.
#pragma warning disable CA5359 // Discovery only: the returned fingerprint still requires an explicit saved confirmation.
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is not null) presented = new X509Certificate2(certificate);
            return true;
        });
#pragma warning restore CA5359
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = target.Host,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, timeout.Token);
        using (presented)
        {
            if (presented is null)
                throw new ConnectionRuleException("certificate_not_presented",
                    "The service did not present a leaf certificate.");
            return new(presented.Subject, presented.Issuer, presented.NotBefore.ToUniversalTime(),
                presented.NotAfter.ToUniversalTime(), ServiceCertificatePin.Fingerprint(presented));
        }
    }

    private IConnectorWatermarkSeed Seed(string connectorCode) =>
        services.GetKeyedService<IConnectorWatermarkSeed>(connectorCode)
        ?? throw new ConnectionRuleException("connector_unavailable",
            "The connector cannot translate a sync start into its watermark format.");

    private DateTime? StartInstant(ConnectionSyncStart value) => value.Mode switch
    {
        "today" => clock.UtcNow,
        "date" => DateTime.SpecifyKind(value.Date!.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
        _ => null,
    };

    public static ConnectionSyncStart Validate(ConnectionSyncStart value)
    {
        var mode = value.Mode.Trim().ToLowerInvariant();
        if (mode is not ("today" or "date" or "full_history")
            || mode == "date" && value.Date is null || mode != "date" && value.Date is not null)
            throw new CommandRefusedException("sync_start_invalid",
                "Choose Today, A date with a calendar date, or Full history.");
        return value with { Mode = mode };
    }

    public static string? NormaliseCertificatePin(string? value)
    {
        try { return ServiceCertificatePin.Normalise(value); }
        catch (InvalidOperationException exception) when (exception.Message == "service_certificate_pin_invalid")
        {
            throw new CommandRefusedException("service_certificate_pin_invalid",
                "Supply a SHA-256 certificate fingerprint containing exactly 64 hexadecimal digits.");
        }
    }
}

/// <summary>Small read port used by connector transports when constructing their TLS handlers.</summary>
public sealed class ConnectionCertificatePins(IntegrationDbContext db, ITenantContext tenant)
{
    public Task<string?> ReadAsync(Guid connectionId, CancellationToken ct) =>
        db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.ServiceCertificateSha256).SingleAsync(ct);
}
