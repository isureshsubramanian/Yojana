using Yojana.Developer.Contracts;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Developer.Webhooks;

public sealed class DeveloperWebhookSettingsService(DeveloperWebhookSettings current, ITenantSettings settings,
    ITenantContext tenant, SecurityEventWriter security, PlatformDbContext platform)
{
    public async Task<WebhookSettingsView> GetAsync(CancellationToken ct) =>
        new(await current.RetentionDaysAsync(ct));

    public async Task<WebhookSettingsView> UpdateAsync(UpdateWebhookSettingsRequest request, Guid actor,
        SecurityRequestContext requestContext, CancellationToken ct)
    {
        if (request.RetentionDays is < 1 or > 3650)
            throw new CommandRefusedException("webhook_retention_out_of_range",
                "Delivery-log retention must be between 1 and 3650 days.");
        var before = await current.RetentionDaysAsync(ct);
        if (before == request.RetentionDays) return new(before);
        await settings.SetAsync(DeveloperWebhookSettings.RetentionDaysKey,
            request.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        security.Add(SecurityEventKind.SettingsChanged, null, tenant.TenantId, actor, requestContext,
            $"developer.webhook_retention:{before}->{request.RetentionDays}");
        await platform.SaveChangesAsync(ct);
        return new(request.RetentionDays);
    }
}
