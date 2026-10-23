using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04ConsentStart(Uri ConsentUrl, DateTime ExpiresAt);
public sealed record Cx04ConsentCallback(string Code, string State);
public sealed record Cx04ConsentResult(string Status, DateTime RefreshChainExpiresAt);
public sealed record Cx04DisableResult(string Status, bool TokensDeleted, bool RemoteSignOutAttempted);

public sealed class Cx04AuthorisationException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}

public sealed class Cx04Authorisation(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    Cx04Settings settings, Cx04Client client, IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector, IConnectionAuditSink audit)
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public async Task<Cx04ConsentStart> BeginAsync(Guid connectionId, CancellationToken ct)
    {
        await EnsureConnectionAsync(connectionId, ct);
        var configured = await settings.GetAsync(connectionId, ct);
        var stateValue = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var verifierBytes = Encoding.ASCII.GetBytes(verifier);
        try
        {
            await credentials.SaveAsync(await protector.EncryptAsync(tenant.TenantId, connectionId,
                Cx04Client.PkcePurpose, verifierBytes, clock.UtcNow.Add(StateLifetime),
                clock.UtcNow.Add(StateLifetime), ct), ct);
        }
        finally { CryptographicOperations.ZeroMemory(verifierBytes); }
        var row = await db.Cx04AuthorisationStates.SingleAsync(value => value.TenantId == tenant.TenantId
            && value.ConnectionId == connectionId, ct);
        row.Status = Cx04AuthorisationStatus.ConsentPending;
        row.PendingStateHash = SHA256.HashData(Encoding.ASCII.GetBytes(stateValue));
        row.PendingStateExpiresAt = clock.UtcNow.Add(StateLifetime);
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(Cx04Protocol.Authorise(configured, stateValue, challenge), row.PendingStateExpiresAt.Value);
    }

    public async Task<Cx04ConsentResult> CompleteAsync(Guid connectionId, Cx04ConsentCallback callback,
        string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callback.Code) || string.IsNullOrWhiteSpace(callback.State))
            throw new Cx04AuthorisationException("oauth_callback_invalid", "The authorisation code and state are required.");
        Actor(actor);
        await EnsureConnectionAsync(connectionId, ct);
        var presentedHash = SHA256.HashData(Encoding.ASCII.GetBytes(callback.State));
        var row = await db.Cx04AuthorisationStates.AsNoTracking().SingleAsync(value =>
            value.TenantId == tenant.TenantId && value.ConnectionId == connectionId, ct);
        if (row.Status != Cx04AuthorisationStatus.ConsentPending || row.PendingStateHash is null
            || row.PendingStateExpiresAt <= clock.UtcNow
            || !CryptographicOperations.FixedTimeEquals(row.PendingStateHash, presentedHash))
            throw new Cx04AuthorisationException("oauth_state_invalid", "The consent state is invalid, expired or already used.");

        var consumed = await db.Cx04AuthorisationStates.Where(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId && value.Status == Cx04AuthorisationStatus.ConsentPending
                && value.PendingStateHash == row.PendingStateHash && value.PendingStateExpiresAt > clock.UtcNow)
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.PendingStateHash, (byte[]?)null)
                .SetProperty(value => value.PendingStateExpiresAt, (DateTime?)null)
                .SetProperty(value => value.Status, Cx04AuthorisationStatus.ConsentRequired)
                .SetProperty(value => value.UpdatedAt, clock.UtcNow), ct);
        if (consumed != 1)
            throw new Cx04AuthorisationException("oauth_state_invalid", "The consent state is invalid, expired or already used.");

        var pkceRecord = await credentials.GetCurrentAsync(tenant.TenantId, connectionId,
            Cx04Client.PkcePurpose, ct)
            ?? throw new Cx04AuthorisationException("oauth_state_invalid", "The consent verifier is missing.");
        if (pkceRecord.ExpiresAt <= clock.UtcNow)
            throw new Cx04AuthorisationException("oauth_state_invalid", "The consent verifier has expired.");
        var verifierBytes = await protector.DecryptAsync(pkceRecord, ct);
        try
        {
            var configured = await settings.GetAsync(connectionId, ct);
            Cx04TokenResponse issued;
            try
            {
                issued = await client.TokenRequestAsync(connectionId, configured,
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code", ["client_id"] = configured.ClientId,
                        ["code"] = callback.Code, ["redirect_uri"] = configured.RedirectUri.ToString(),
                        ["code_verifier"] = Encoding.ASCII.GetString(verifierBytes),
                    }, ct);
            }
            catch (Cx04RemoteException exception) when (exception.Code == "invalid_grant")
            {
                throw new Cx04AuthorisationException("authorisation_code_invalid",
                    "The authorisation code was refused; start consent again.");
            }
            if (string.IsNullOrWhiteSpace(issued.RefreshToken))
                throw new Cx04AuthorisationException("offline_access_missing",
                    "The connected application did not return a refresh token.");
            var expiry = clock.UtcNow.AddDays(configured.RefreshLifetimeDays);
            // The new pair is one encrypted version. The access token is not returned or used until the credential
            // store has committed the refresh token that can continue its chain.
            await client.SaveTokenAsync(connectionId, new(issued.AccessToken, issued.RefreshToken,
                clock.UtcNow.AddSeconds(issued.ExpiresIn)), expiry, ct);
            await credentialsDeleteAsync(Cx04Client.PkcePurpose, ct);
            var current = await db.Cx04AuthorisationStates.SingleAsync(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId, ct);
            current.Status = Cx04AuthorisationStatus.Active;
            current.RefreshChainExpiresAt = expiry;
            current.RefreshExpiryMode = configured.RefreshExpiryMode;
            current.RefreshLifetimeDays = configured.RefreshLifetimeDays;
            current.Reminder14DaysSent = false;
            current.Reminder3DaysSent = false;
            current.UpdatedAt = clock.UtcNow;
            var connection = await db.Connections.SingleAsync(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId, ct);
            connection.Status = Data.Entities.ConnectionStatus.Active;
            await db.ConnectionNotificationSources.Where(value => value.TenantId == tenant.TenantId
                    && value.ConnectionId == connectionId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Enabled, true)
                    .SetProperty(value => value.UpdatedAt, clock.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            await AuditAsync(connectionId, ConnectionAuditEventType.ConsentGranted, actor,
                new { refresh_chain_expires_at = expiry, refresh_expiry_mode = configured.RefreshExpiryMode }, ct);
            return new(current.Status, expiry);
        }
        finally { CryptographicOperations.ZeroMemory(verifierBytes); }

        Task credentialsDeleteAsync(string purpose, CancellationToken token) => db.ConnectionCredentials
            .Where(value => value.TenantId == tenant.TenantId && value.ConnectionId == connectionId
                && value.Purpose == purpose).ExecuteDeleteAsync(token);
    }

    public async Task<Cx04DisableResult> DisableAsync(Guid connectionId, string actor, CancellationToken ct)
    {
        Actor(actor);
        await EnsureConnectionAsync(connectionId, ct);
        var signOutAttempted = await client.ReadTokenAsync(connectionId, ct) is not null;
        if (signOutAttempted)
        {
            try { await client.SignOutCurrentAsync(connectionId, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }
        await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var row = await db.Cx04AuthorisationStates.SingleAsync(value => value.TenantId == tenant.TenantId
            && value.ConnectionId == connectionId, ct);
        row.Status = Cx04AuthorisationStatus.Disabled;
        row.PendingStateHash = null;
        row.PendingStateExpiresAt = null;
        row.RefreshChainExpiresAt = null;
        row.UpdatedAt = clock.UtcNow;
        var connection = await db.Connections.SingleAsync(value => value.TenantId == tenant.TenantId
            && value.ConnectionId == connectionId, ct);
        connection.Status = Data.Entities.ConnectionStatus.Paused;
        await db.ConnectionNotificationSources.Where(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Enabled, false)
                .SetProperty(value => value.UpdatedAt, clock.UtcNow), ct);
        await db.ConnectionCredentials.Where(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId
                && (value.Purpose == Cx04Client.TokenPurpose || value.Purpose == Cx04Client.PkcePurpose))
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        // TIGHT COUPLING (Disable audit): the state change, token deletion and append-only audit share this scoped
        // serialisable transaction. Separating them could leave a disabled connection with tokens or no audit.
        await AuditAsync(connectionId, ConnectionAuditEventType.ConsentDisabled, actor, new { remote_sign_out_attempted = signOutAttempted }, ct);
        await transaction.CommitAsync(ct);
        return new(row.Status, true, signOutAttempted);
    }

    public async Task<ConnectionAuthorisationView> ReadAsync(Guid connectionId, CancellationToken ct)
    {
        await EnsureConnectionAsync(connectionId, ct);
        var row = await db.Cx04AuthorisationStates.AsNoTracking().SingleAsync(value =>
            value.TenantId == tenant.TenantId && value.ConnectionId == connectionId, ct);
        return new(row.Status, row.RefreshChainExpiresAt, row.RefreshExpiryMode, row.RefreshLifetimeDays,
            row.Reminder14DaysSent, row.Reminder3DaysSent,
            row.Status is Cx04AuthorisationStatus.Active or Cx04AuthorisationStatus.ConsentPending,
            row.Status != Cx04AuthorisationStatus.Active);
    }

    private async Task EnsureConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        if (!await db.Connections.AsNoTracking().AnyAsync(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId && value.ConnectorCode == Cx04Capabilities.ConnectorCode
                && value.Status != Data.Entities.ConnectionStatus.Archived, ct))
            throw new Cx04AuthorisationException("connection_not_found", "The CX04 connection was not found.");
    }

    private Task<ConnectionAuditEvent> AuditAsync(Guid connectionId, ConnectionAuditEventType kind, string actor, object details,
        CancellationToken ct) => audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow),
        connectionId, kind, actor, clock.UtcNow, JsonSerializer.Serialize(details, IntegrationContracts.Json)), ct);

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new Cx04AuthorisationException("audit_actor_required", "An authenticated actor is required.");
    }
}

public sealed class Cx04ConsentExpiryReminders(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IConnectionAuditSink audit)
{
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var rows = await db.Cx04AuthorisationStates.Where(value => value.TenantId == tenant.TenantId
                && value.Status == Cx04AuthorisationStatus.Active && value.RefreshChainExpiresAt != null
                && value.RefreshChainExpiresAt <= clock.UtcNow.AddDays(14))
            .OrderBy(value => value.RefreshChainExpiresAt).ThenBy(value => value.ConnectionId).Take(100)
            .ToListAsync(ct);
        var notifications = 0;
        foreach (var row in rows)
        {
            if (!row.Reminder14DaysSent)
            {
                row.Reminder14DaysSent = true;
                await NotifyAsync(row, ConnectionAuditEventType.ConsentExpiry14Days, 14, ct);
                notifications++;
            }
            if (row.RefreshChainExpiresAt <= clock.UtcNow.AddDays(3) && !row.Reminder3DaysSent)
            {
                row.Reminder3DaysSent = true;
                await NotifyAsync(row, ConnectionAuditEventType.ConsentExpiry3Days, 3, ct);
                notifications++;
            }
            if (row.RefreshChainExpiresAt <= clock.UtcNow)
            {
                row.Status = Cx04AuthorisationStatus.ConsentRequired;
                var connection = await db.Connections.SingleAsync(value => value.TenantId == tenant.TenantId
                    && value.ConnectionId == row.ConnectionId, ct);
                connection.Status = Data.Entities.ConnectionStatus.Paused;
                await NotifyAsync(row, ConnectionAuditEventType.ConsentRequired, 0, ct);
            }
            row.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return notifications;
    }

    private Task<ConnectionAuditEvent> NotifyAsync(Cx04AuthorisationState row, ConnectionAuditEventType kind, int days,
        CancellationToken ct) => audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow),
        row.ConnectionId, kind, "system:cx04-authorisation", clock.UtcNow,
        JsonSerializer.Serialize(new { days_before_expiry = days,
            refresh_chain_expires_at = row.RefreshChainExpiresAt }, IntegrationContracts.Json)), ct);
}

[DisallowConcurrentExecution]
public sealed class Cx04ConsentExpiryJob(ITenantContextSetter setter, Cx04ConsentExpiryReminders reminders)
    : TenantJob(setter)
{
    public const string JobName = "integration.cx04-consent-expiry";
    public const string Cron = "15 0 * * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        reminders.EvaluateAsync(cancellationToken);
}
