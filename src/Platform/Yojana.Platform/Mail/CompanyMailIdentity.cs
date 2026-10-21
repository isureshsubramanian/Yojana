using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Mail;

public sealed record CompanyMailSettingsView(string CompanyName, string FooterLine, string SenderName, string ReplyTo,
    string Version);

/// <summary>
/// How a company appears on every e-mail it sends (P9-T28, Settings › Company): the footer line after its name, and the
/// sender name and reply-to that <see cref="TenantMailSettings"/> applies at queue time. No logo: e-mail carries the
/// monogram header only (owner decision 2026-10-21).
/// </summary>
public sealed class CompanyMailIdentity(
    ITenantSettings current,
    ITenantContext tenant,
    IServiceScopeFactory scopes,
    PlatformDbContext db)
{
    public const string FooterLineKey = "company.mail_footer_line";
    public const int MaxFooterLineLength = 120;
    public const int MaxSenderNameLength = 100;

    /// <summary>"Name · footer line", or the name alone when the company has none.</summary>
    public static string Compose(string companyName, string? footerLine) =>
        string.IsNullOrWhiteSpace(footerLine) ? companyName : $"{companyName} · {footerLine.Trim()}";

    /// <summary>
    /// The footer line of <paramref name="tenantId"/>. Account mail is sometimes written before a tenant is selected (a
    /// self-service reset link), so another tenant's line is read in a scope of its own rather than through this one.
    /// </summary>
    public async Task<string> CompanyLineAsync(TenantId tenantId, string companyName, CancellationToken cancellationToken)
    {
        if (tenant.HasTenant && tenant.TenantId == tenantId)
            return Compose(companyName, await current.GetAsync(FooterLineKey, cancellationToken));
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenantId);
        var line = await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetAsync(FooterLineKey, cancellationToken);
        return Compose(companyName, line);
    }

    public async Task<CompanyMailSettingsView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var footer = await current.GetVersionedAsync(FooterLineKey, cancellationToken);
        var sender = await current.GetVersionedAsync(TenantMailSettings.SenderNameKey, cancellationToken);
        var replyTo = await current.GetVersionedAsync(TenantMailSettings.ReplyToKey, cancellationToken);
        return new CompanyMailSettingsView(await CompanyNameAsync(cancellationToken), footer?.Value ?? string.Empty,
            sender?.Value ?? string.Empty, replyTo?.Value ?? string.Empty,
            Join(footer?.Version, sender?.Version, replyTo?.Version));
    }

    /// <summary>Saves all three or none; returns the changed keys for the audit record.</summary>
    public async Task<(CompanyMailSettingsView View, IReadOnlyList<string> Changed)> UpdateCurrentAsync(
        string? footerLine, string? senderName, string? replyTo, string version, CancellationToken cancellationToken)
    {
        var footer = (footerLine ?? string.Empty).Trim();
        var sender = (senderName ?? string.Empty).Trim();
        var reply = (replyTo ?? string.Empty).Trim();
        if (footer.Length > MaxFooterLineLength || footer.Any(char.IsControl))
            throw new CompanyMailSettingsException("footer_line_invalid",
                $"The footer line must be at most {MaxFooterLineLength} printable characters.");
        if (sender.Length > MaxSenderNameLength || sender.Any(char.IsControl))
            throw new CompanyMailSettingsException("sender_name_invalid",
                $"The sender name must be at most {MaxSenderNameLength} printable characters.");
        if (reply.Length > 0 && !TenantMailSettings.IsSingleAddress(reply))
            throw new CompanyMailSettingsException("reply_to_invalid", "Replies must go to one e-mail address.");

        var versions = version.Split(';');
        if (versions.Length != 3 || versions.Any(string.IsNullOrWhiteSpace))
            throw new TenantSettingVersionConflictException(FooterLineKey, version, "unknown");
        var before = await GetCurrentAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var savedFooter = await current.SetAsync(FooterLineKey, footer, versions[0], cancellationToken);
        var savedSender = await current.SetAsync(TenantMailSettings.SenderNameKey, sender, versions[1], cancellationToken);
        var savedReply = await current.SetAsync(TenantMailSettings.ReplyToKey, reply, versions[2], cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var changed = new List<string>();
        if (before.FooterLine != footer) changed.Add(FooterLineKey);
        if (before.SenderName != sender) changed.Add(TenantMailSettings.SenderNameKey);
        if (before.ReplyTo != reply) changed.Add(TenantMailSettings.ReplyToKey);
        return (new CompanyMailSettingsView(before.CompanyName, footer, sender, reply,
            Join(savedFooter.Version, savedSender.Version, savedReply.Version)), changed);
    }

    private Task<string> CompanyNameAsync(CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().Where(row => row.Id == tenant.TenantId).Select(row => row.Name)
            .FirstAsync(cancellationToken);

    private static string Join(params string?[] versions) =>
        string.Join(';', versions.Select(value => value ?? ITenantSettings.MissingVersion));
}

public sealed class CompanyMailSettingsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
