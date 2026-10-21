using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Mail;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

/// <summary>
/// The security notice sent when a way to complete sign-in is added or removed (authenticator app, passkey), so a
/// person notices a change they did not make. Shared by the second step and passkeys; lists the methods left after it.
/// </summary>
public sealed class SignInMethodNotices(
    PlatformDbContext db,
    IMailOutbox mail,
    MailOptions mailOptions,
    CompanyMailIdentity company,
    IServiceScopeFactory scopes,
    IClock clock)
{
    public async Task QueueAsync(AppUser user, TenantId? tenantId, string method, bool removed,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var tenantName = tenantId is { } selected
            ? await db.Tenants.AsNoTracking().Where(row => row.Id == selected).Select(row => row.Name)
                .SingleAsync(cancellationToken) : "Yojana";
        var line = tenantId is { } companyId
            ? await company.CompanyLineAsync(companyId, tenantName, cancellationToken) : null;
        var rendered = AccountMailLayout.Render(new AccountMailContent(tenantName, "Security notice",
            removed ? "A sign-in method was removed" : "A new sign-in method was added",
            [removed ? "A way to complete sign-in was removed from your Yojana account." :
                "A new way to complete sign-in was added to your Yojana account."],
            [("Method", method), (removed ? "Removed" : "Added", clock.UtcNow.ToString("d MMM yyyy, HH:mm 'UTC'")),
                ("From", string.IsNullOrWhiteSpace(request.ClientAddress) ? "Unknown address" : request.ClientAddress!),
                ("Your sign-in methods", await MethodsAsync(user, cancellationToken))],
            ("Review my sign-in methods", mailOptions.Link("/account/security")),
            ["Was this you? Then there is nothing to do.",
                "If not, change your password and tell your administrator."],
            $"Security notice for {tenantName}.", line));
        var outgoing = new OutgoingMail(removed ? MailKind.SignInMethodRemoved : MailKind.SignInMethodAdded,
            tenantId, user.Id, new MailRecipient(user.Email, user.DisplayName),
            removed ? "A Yojana sign-in method was removed" : "A Yojana sign-in method was added",
            rendered.Html, rendered.Text);
        if (tenantId is not { } selectedTenant)
        {
            await mail.QueueAsync(outgoing, cancellationToken);
            return;
        }

        // MailOutbox deliberately enforces tenant scope so sender identity cannot leak between companies. A
        // pre-tenant second-step flow must establish that scope explicitly before it queues the notification.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(selectedTenant);
        await scope.ServiceProvider.GetRequiredService<IMailOutbox>().QueueAsync(outgoing, cancellationToken);
    }

    /// <summary>Read after the change is saved, so the notice shows what is left rather than what was there.</summary>
    private async Task<string> MethodsAsync(AppUser user, CancellationToken cancellationToken)
    {
        var methods = new List<string>();
        if (user.PasswordHash is not null) methods.Add("Password");
        if (await db.UserAuthenticators.AsNoTracking().AnyAsync(row => row.UserId == user.Id &&
                row.Status == UserAuthenticatorStatus.Active, cancellationToken))
            methods.Add("authenticator app");
        var passkeys = await db.UserPasskeys.AsNoTracking().CountAsync(row => row.UserId == user.Id, cancellationToken);
        if (passkeys > 0) methods.Add(passkeys == 1 ? "1 passkey" : $"{passkeys} passkeys");
        return methods.Count == 0 ? "None" : string.Join(", ", methods);
    }
}

/// <summary>
/// Who has a working second step: an active authenticator app or at least one passkey. One definition, used by the
/// sign-in challenge, the sign-in policy screen and user management, so their counts cannot disagree.
/// </summary>
public static class SecondStepEnrolment
{
    public static IQueryable<Guid> EnrolledUsers(PlatformDbContext db) =>
        db.UserAuthenticators.Where(row => row.Status == UserAuthenticatorStatus.Active).Select(row => row.UserId)
            .Union(db.UserPasskeys.Select(row => row.UserId));
}
