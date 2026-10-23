using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04ReminderSettingsView(IReadOnlyList<int> DaysBeforeExpiry,
    IReadOnlyList<string> RecipientRoles, IReadOnlyList<string> Channels, string SettingsVersion);

public sealed record UpdateCx04ReminderSettings(IReadOnlyList<int> DaysBeforeExpiry,
    IReadOnlyList<string> RecipientRoles, string ExpectedVersion);

internal sealed record StoredCx04ReminderSettings(IReadOnlyList<int> DaysBeforeExpiry,
    IReadOnlyList<string> RecipientRoles);

public sealed class Cx04ReminderSettings(ITenantSettings settings, ITenantContext tenant, IClock clock,
    IConnectionAuditSink audit, IntegrationDbContext db)
{
    public const string SettingKey = "integration.cx04.sign_in_reminders";
    public static readonly IReadOnlyList<int> DefaultDays = [14, 3];
    public static readonly IReadOnlyList<string> DefaultRoles = [YojanaRole.IntegrationOperator];
    public static readonly IReadOnlyList<string> Channels = ["in_app", "email"];

    public async Task<Cx04ReminderSettingsView> GetAsync(CancellationToken ct)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, ct);
        var value = stored is null ? Normalise(new(DefaultDays, DefaultRoles)) : Read(stored.Value);
        return new(value.DaysBeforeExpiry, value.RecipientRoles, Channels,
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<Cx04ReminderSettingsView> GetForConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        await EnsureConnectionAsync(connectionId, ct);
        return await GetAsync(ct);
    }

    public async Task<Cx04ReminderSettingsView> SetAsync(Guid connectionId, UpdateCx04ReminderSettings request,
        string actor, CancellationToken ct)
    {
        Cx04AuthorisationGuard.Actor(actor);
        await EnsureConnectionAsync(connectionId, ct);
        var before = await GetAsync(ct);
        var value = Normalise(new(request.DaysBeforeExpiry, request.RecipientRoles));
        var saved = await settings.SetAsync(SettingKey,
            JsonSerializer.Serialize(value, IntegrationContracts.Json), request.ExpectedVersion, ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            ConnectionAuditEventType.ConsentReminderSettingsChanged, actor, clock.UtcNow,
            JsonSerializer.Serialize(new
            {
                before = new { days_before_expiry = before.DaysBeforeExpiry, recipient_roles = before.RecipientRoles },
                after = new { days_before_expiry = value.DaysBeforeExpiry, recipient_roles = value.RecipientRoles },
            }, IntegrationContracts.Json)), ct);
        return new(value.DaysBeforeExpiry, value.RecipientRoles, Channels, saved.Version);
    }

    private async Task EnsureConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        if (!await db.Connections.AsNoTracking().AnyAsync(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId && value.ConnectorCode == Cx04Capabilities.ConnectorCode
                && value.Status != Data.Entities.ConnectionStatus.Archived, ct))
            throw new Cx04AuthorisationException("connection_not_found", "The CX04 connection was not found.");
    }

    private static StoredCx04ReminderSettings Read(string value)
    {
        try
        {
            return Normalise(JsonSerializer.Deserialize<StoredCx04ReminderSettings>(value,
                IntegrationContracts.Json) ?? new(DefaultDays, DefaultRoles));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' is invalid.", exception);
        }
    }

    private static StoredCx04ReminderSettings Normalise(StoredCx04ReminderSettings value)
    {
        var suppliedDays = value.DaysBeforeExpiry?.ToList() ?? [];
        var days = suppliedDays.Distinct().OrderDescending().ToList();
        if (days.Count is < 1 or > 3 || days.Any(day => day is < 1 or > 90))
            throw new Cx04AuthorisationException("reminder_days_invalid",
                "Choose one to three distinct reminder days from 1 to 90.");
        if (days.Count != suppliedDays.Count)
            throw new Cx04AuthorisationException("reminder_days_invalid", "Reminder days must be distinct.");
        var roles = value.RecipientRoles?.Select(role => role?.Trim() ?? "")
            .Where(role => role.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
        if (roles.Count == 0)
            throw new Cx04AuthorisationException("reminder_recipients_required",
                "Choose at least one reminder recipient role.");
        if (roles.Any(role => !YojanaRole.IsKnown(role)))
            throw new Cx04AuthorisationException("reminder_recipient_invalid",
                "Every reminder recipient must be a known tenant role.");
        return new(days, roles);
    }
}

public sealed class Cx04SignInNotifications(ITenantContext tenant, IClock clock,
    ITenantStaffDirectory staff, IMailOutbox mail, IConnectionAuditSink audit,
    Cx04ReminderSettings settings, IUserDirectory people)
{
    public async Task NotifyReminderAsync(Cx04AuthorisationState state, int days,
        IReadOnlyList<string> roles, CancellationToken ct)
    {
        var recipients = await RecipientsAsync(roles, ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), state.ConnectionId,
            ReminderEvent(days), "system:cx04-authorisation", clock.UtcNow,
            JsonSerializer.Serialize(new
            {
                days_before_expiry = days, refresh_chain_expires_at = state.RefreshChainExpiresAt,
                recipient_roles = roles, recipient_count = recipients.Count,
                channels = Cx04ReminderSettings.Channels,
            }, IntegrationContracts.Json)), ct);
        await SendAsync(recipients, "integration.cx04.sign_in_expiry",
            $"Connection sign-in expires in {days} day{(days == 1 ? "" : "s")}",
            $"The connection sign-in expires at {state.RefreshChainExpiresAt:yyyy-MM-dd HH:mm} UTC. Sign in again before then to avoid a sync pause.", ct);
    }

    public async Task NotifyPauseAsync(Cx04AuthorisationState state, string reason, string detail,
        CancellationToken ct)
    {
        if (state.PauseNotifiedAt is not null) return;
        var configured = await settings.GetAsync(ct);
        var roles = configured.RecipientRoles.Append(YojanaRole.TenantAdministrator)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var recipients = await RecipientsAsync(roles, ct);
        var disabledBy = reason == "disabled" && state.DisabledBy is { } actor
            ? (await people.DisplayNamesAsync([actor], ct)).GetValueOrDefault(actor) ?? "Unknown user"
            : null;
        if (disabledBy is not null)
            detail = $"it was disabled by {disabledBy}: {state.DisabledReason}";
        state.PauseReason = reason;
        state.PauseNotifiedAt = clock.UtcNow;
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), state.ConnectionId,
            ConnectionAuditEventType.ConsentPauseNotified, "system:cx04-authorisation", clock.UtcNow,
            JsonSerializer.Serialize(new
            {
                reason, detail, disabled_by = disabledBy, disabled_reason = state.DisabledReason,
                recipient_roles = roles, recipient_count = recipients.Count,
                channels = Cx04ReminderSettings.Channels,
            }, IntegrationContracts.Json)), ct);
        await SendAsync(recipients, "integration.cx04.sync_paused", "Connection sync paused",
            $"Connection sync paused because {detail}. Queued work is retained and resumes after a fresh sign-in.", ct);
    }

    private async Task<IReadOnlyList<TenantStaffNotificationRecipient>> RecipientsAsync(
        IReadOnlyList<string> roles, CancellationToken ct)
    {
        var recipients = new List<TenantStaffNotificationRecipient>();
        foreach (var role in roles)
            recipients.AddRange(await staff.NotificationRecipientsAsync(null, role, ct));
        return recipients.DistinctBy(recipient => recipient.UserId).ToList();
    }

    private async Task SendAsync(IReadOnlyList<TenantStaffNotificationRecipient> recipients,
        string kind, string subject, string text, CancellationToken ct)
    {
        foreach (var recipient in recipients)
            await mail.QueueAsync(new(kind, tenant.TenantId, recipient.UserId,
                new(recipient.Email, recipient.Name), subject,
                $"<p>{WebUtility.HtmlEncode(text)}</p>", text), ct);
    }

    private static ConnectionAuditEventType ReminderEvent(int days) => days switch
    {
        14 => ConnectionAuditEventType.ConsentExpiry14Days,
        3 => ConnectionAuditEventType.ConsentExpiry3Days,
        _ => ConnectionAuditEventType.ConsentExpiryReminder,
    };
}

internal static class Cx04AuthorisationGuard
{
    public static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new Cx04AuthorisationException("audit_actor_required", "An authenticated actor is required.");
    }

    public static string DisableReason(string? reason, bool confirm)
    {
        if (!confirm)
            throw new Cx04AuthorisationException("disable_confirmation_required",
                "Confirm that the connection sign-in should be disabled.");
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200)
            throw new Cx04AuthorisationException("disable_reason_invalid",
                "A disable reason from 1 to 200 characters is required.");
        return trimmed;
    }
}
