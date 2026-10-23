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
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04ConsentStart(Uri ConsentUrl, DateTime ExpiresAt);
public sealed record Cx04ConsentCallback(string Code, string State);
public sealed record Cx04ConsentResult(string Status, DateTime RefreshChainExpiresAt);
public sealed record Cx04DisableRequest(string? Reason, bool Confirm);
public sealed record Cx04DisableResult(string Status, bool TokensDeleted, bool RemoteSignOutAttempted,
    DateTime DisabledAt, string Reason);

public sealed class Cx04AuthorisationException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}

public sealed class Cx04Authorisation(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    Cx04Settings settings, Cx04Client client, IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector, IConnectionAuditSink audit, Cx04ReminderSettings reminderSettings,
    Cx04SignInNotifications notifications, IUserDirectory people, IConnectionThrottleProfileStore throttles)
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
        Cx04AuthorisationGuard.Actor(actor);
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
            current.ReminderDaysSentJson = "[]";
            current.SignedInBy = actor;
            current.SignedInAt = clock.UtcNow;
            current.DisabledBy = null;
            current.DisabledAt = null;
            current.DisabledReason = null;
            current.PauseReason = null;
            current.PauseNotifiedAt = null;
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

    public async Task<Cx04DisableResult> DisableAsync(Guid connectionId, Cx04DisableRequest request,
        string actor, CancellationToken ct)
    {
        Cx04AuthorisationGuard.Actor(actor);
        var reason = Cx04AuthorisationGuard.DisableReason(request.Reason, request.Confirm);
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
        if (row.Status == Cx04AuthorisationStatus.Disabled)
            throw new Cx04AuthorisationException("sign_in_already_disabled",
                "The connection sign-in is already disabled.");
        row.Status = Cx04AuthorisationStatus.Disabled;
        row.PendingStateHash = null;
        row.PendingStateExpiresAt = null;
        row.RefreshChainExpiresAt = null;
        row.DisabledBy = actor;
        row.DisabledAt = clock.UtcNow;
        row.DisabledReason = reason;
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
        // TIGHT COUPLING (Disable audit): the state change, token deletion and append-only audit share this scoped
        // serialisable transaction. Separating them could leave a disabled connection with tokens or no audit.
        await AuditAsync(connectionId, ConnectionAuditEventType.ConsentDisabled, actor,
            new { reason, remote_sign_out_attempted = signOutAttempted }, ct);
        await notifications.NotifyPauseAsync(row, "disabled", "the connection sign-in was disabled", ct);
        await transaction.CommitAsync(ct);
        return new(row.Status, true, signOutAttempted, row.DisabledAt.Value, reason);
    }

    public async Task<ConnectionAuthorisationView> ReadAsync(Guid connectionId, string? cursor,
        int take, CancellationToken ct)
    {
        await EnsureConnectionAsync(connectionId, ct);
        take = Math.Clamp(take, 1, 100);
        var row = await db.Cx04AuthorisationStates.AsNoTracking().SingleAsync(value =>
            value.TenantId == tenant.TenantId && value.ConnectionId == connectionId, ct);
        var configured = await settings.GetAsync(connectionId, ct);
        var reminders = await reminderSettings.GetAsync(ct);
        var sent = SentDays(row);
        var actorRefs = new[] { row.SignedInBy, row.DisabledBy }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = actorRefs.Count == 0
            ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync(actorRefs, ct);
        var waiting = await db.OutboundCommands.AsNoTracking().CountAsync(value =>
            value.TenantId == tenant.TenantId && value.ConnectionId == connectionId
            && (value.Status == Data.Entities.OutboundCommandStatus.Pending
                || value.Status == Data.Entities.OutboundCommandStatus.Retry
                || value.Status == Data.Entities.OutboundCommandStatus.Processing), ct);
        var licensed = configured.RequestsPerMinute
            ?? Cx04Capabilities.PublishedRequestsPerMinute(configured.LicenceTier);
        var throttle = await throttles.GetAsync(new(tenant.TenantId, connectionId), ct);
        var governed = throttle?.Profile.RequestsPerMinute
            ?? Cx04Capabilities.For(configured).Throttle.DefaultProfile.RequestsPerMinute
            ?? throw new InvalidOperationException("The CX04 request lane has no per-minute limit.");
        var history = await HistoryAsync(connectionId, cursor, take, ct);
        return new(row.Status, row.RefreshChainExpiresAt, row.RefreshExpiryMode, row.RefreshLifetimeDays,
            row.LastCallAt, reminders.DaysBeforeExpiry, reminders.RecipientRoles, sent,
            reminders.DaysBeforeExpiry.FirstOrDefault(day => !sent.Contains(day)) is var next && next > 0 ? next : null,
            Person(row.SignedInBy, names), row.SignedInAt, Person(row.DisabledBy, names), row.DisabledAt,
            row.DisabledReason, waiting, configured.LicenceTier.ToString().ToLowerInvariant(), licensed,
            governed, licensed - governed,
            row.Status is Cx04AuthorisationStatus.Active or Cx04AuthorisationStatus.ConsentPending,
            row.Status != Cx04AuthorisationStatus.Active, history);
    }

    public Task<ConnectionAuthorisationView> ReadAsync(Guid connectionId, CancellationToken ct) =>
        ReadAsync(connectionId, null, 50, ct);

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
    private async Task<Cx04SignInHistoryPage> HistoryAsync(Guid connectionId, string? cursor, int take,
        CancellationToken ct)
    {
        long? before = null;
        if (!string.IsNullOrWhiteSpace(cursor)
            && (!long.TryParse(cursor, out var parsed) || parsed < 1))
            throw new Cx04AuthorisationException("cursor_invalid", "The sign-in history cursor is invalid.");
        else if (!string.IsNullOrWhiteSpace(cursor)) before = long.Parse(cursor);
        var since = clock.UtcNow.AddDays(-90);
        var query = db.ConnectionEvents.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
            && value.ConnectionId == connectionId && value.OccurredAt >= since
            && (value.EventType == nameof(ConnectionAuditEventType.ConsentGranted)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentDisabled)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentRequired)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentExpiry14Days)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentExpiry3Days)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentExpiryReminder)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentPauseNotified)
                || value.EventType == nameof(ConnectionAuditEventType.ConsentRenewed)));
        var eventQuery = query.Where(value => value.EventType != nameof(ConnectionAuditEventType.ConsentRenewed));
        if (before is { } eventSequence)
            eventQuery = eventQuery.Where(value => value.SequenceNo < eventSequence);
        var rows = await eventQuery.OrderByDescending(value => value.SequenceNo).Take(take + 1).ToListAsync(ct);
        var renewalQuery = query.Where(value => value.EventType == nameof(ConnectionAuditEventType.ConsentRenewed))
            .GroupBy(value => value.OccurredAt.Date)
            .Select(group => new Cx04RenewalHistoryProjection
            {
                Sequence = group.Max(value => value.SequenceNo),
                OccurredAt = group.Key,
                Count = group.Count(),
            });
        if (before is { } renewalSequence)
            renewalQuery = renewalQuery.Where(value => value.Sequence < renewalSequence);
        var renewals = await renewalQuery.OrderByDescending(value => value.Sequence).Take(take + 1).ToListAsync(ct);
        var actorRefs = rows.Where(value => !value.ActorRef.StartsWith("system:", StringComparison.Ordinal))
            .Select(value => value.ActorRef).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = actorRefs.Count == 0 ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync(actorRefs, ct);
        var items = rows.Select(value => HistoryItem(value.SequenceNo, value.EventType, value.ActorRef,
                value.OccurredAt, value.DetailsJson, names)).Concat(renewals.Select(value =>
                new Cx04SignInHistoryItem(value.Sequence, "access_renewed", "System", value.OccurredAt,
                    "ok", value.Count)))
            .OrderByDescending(value => value.Sequence).Take(take + 1).ToList();
        var page = items.Take(take).ToList();
        return new(page, items.Count > take ? page[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static Cx04SignInHistoryItem HistoryItem(long sequence, string eventType, string actor,
        DateTime occurredAt, string detailsJson, IReadOnlyDictionary<string, string> names)
    {
        using var details = JsonDocument.Parse(detailsJson);
        var root = details.RootElement;
        var kind = eventType switch
        {
            nameof(ConnectionAuditEventType.ConsentGranted) => "signed_in",
            nameof(ConnectionAuditEventType.ConsentDisabled) => "disabled",
            nameof(ConnectionAuditEventType.ConsentRequired) => "sign_in_required",
            nameof(ConnectionAuditEventType.ConsentPauseNotified) => "sync_paused",
            _ => "expiry_reminder",
        };
        var result = kind switch { "disabled" => "tokens_deleted", "sign_in_required" => "paused", _ => "sent" };
        var reason = root.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
        int? days = root.TryGetProperty("days_before_expiry", out var dayValue) ? dayValue.GetInt32() : null;
        var roles = root.TryGetProperty("recipient_roles", out var roleValue) && roleValue.ValueKind == JsonValueKind.Array
            ? roleValue.EnumerateArray().Select(value => value.GetString()!).Where(value => value is not null).ToList()
            : null;
        return new(sequence, kind, actor.StartsWith("system:", StringComparison.Ordinal) ? "System"
            : names.GetValueOrDefault(actor) ?? "Unknown user", occurredAt, result, 1, reason, days, roles);
    }

    private static List<int> SentDays(Cx04AuthorisationState row)
    {
        try { return JsonSerializer.Deserialize<List<int>>(row.ReminderDaysSentJson, IntegrationContracts.Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? Person(string? actor, IReadOnlyDictionary<string, string> names) => actor is null ? null
        : actor.StartsWith("system:", StringComparison.Ordinal) ? "System"
        : names.GetValueOrDefault(actor) ?? "Unknown user";

    private sealed class Cx04RenewalHistoryProjection
    {
        public long Sequence { get; init; }
        public DateTime OccurredAt { get; init; }
        public int Count { get; init; }
    }
}

public sealed class Cx04ConsentExpiryReminders(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IConnectionAuditSink audit, Cx04ReminderSettings settings, Cx04SignInNotifications notifications)
{
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var configured = await settings.GetAsync(ct);
        var largestDay = configured.DaysBeforeExpiry.Max();
        var rows = await db.Cx04AuthorisationStates.Where(value => value.TenantId == tenant.TenantId
                && value.Status == Cx04AuthorisationStatus.Active && value.RefreshChainExpiresAt != null
                && value.RefreshChainExpiresAt <= clock.UtcNow.AddDays(largestDay))
            .OrderBy(value => value.RefreshChainExpiresAt).ThenBy(value => value.ConnectionId).Take(100)
            .ToListAsync(ct);
        var notificationCount = 0;
        foreach (var row in rows)
        {
            var sent = SentDays(row).ToHashSet();
            foreach (var days in configured.DaysBeforeExpiry.Where(days =>
                         row.RefreshChainExpiresAt <= clock.UtcNow.AddDays(days) && !sent.Contains(days)))
            {
                sent.Add(days);
                row.ReminderDaysSentJson = JsonSerializer.Serialize(sent.OrderDescending(), IntegrationContracts.Json);
                await notifications.NotifyReminderAsync(row, days, configured.RecipientRoles, ct);
                notificationCount++;
            }
            if (row.RefreshChainExpiresAt <= clock.UtcNow)
            {
                row.Status = Cx04AuthorisationStatus.ConsentRequired;
                var connection = await db.Connections.SingleAsync(value => value.TenantId == tenant.TenantId
                    && value.ConnectionId == row.ConnectionId, ct);
                connection.Status = Data.Entities.ConnectionStatus.Paused;
                await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), row.ConnectionId,
                    ConnectionAuditEventType.ConsentRequired, "system:cx04-authorisation", clock.UtcNow,
                    JsonSerializer.Serialize(new { reason = "expired", expired_at = row.RefreshChainExpiresAt },
                        IntegrationContracts.Json)), ct);
                await notifications.NotifyPauseAsync(row, "expired",
                    $"the sign-in expired at {row.RefreshChainExpiresAt:yyyy-MM-dd HH:mm} UTC", ct);
            }
            row.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return notificationCount;
    }

    private static List<int> SentDays(Cx04AuthorisationState row)
    {
        try { return JsonSerializer.Deserialize<List<int>>(row.ReminderDaysSentJson, IntegrationContracts.Json) ?? []; }
        catch (JsonException) { return []; }
    }
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
