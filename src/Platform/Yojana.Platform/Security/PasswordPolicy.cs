using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

/// <summary>
/// ADR-0008: works out whose rules apply and hands the checking to <see cref="PasswordCheck"/>. A person follows the
/// strictest rules of their active companies, the same precedent as the reset-link length in PasswordResetService.
/// </summary>
public sealed class PasswordPolicy(SignInOptions options, PasswordSecuritySettings settings, PlatformDbContext db)
    : IPasswordPolicy
{
    public async Task<PasswordPolicyResult> ValidateAsync(PasswordCandidate candidate,
        CancellationToken cancellationToken)
    {
        var companies = await CompaniesAsync(candidate.UserId, candidate.InvitedTo, cancellationToken);
        var rules = await StrictestAsync(companies, cancellationToken);
        return PasswordCheck.Evaluate(candidate.Password, rules,
            new PasswordIdentity(candidate.Email, candidate.DisplayName, companies.Select(row => row.Name).ToList()),
            options.MaximumPasswordLength);
    }

    public async Task<PasswordRules> RulesForAsync(Guid? userId, TenantId? invitedTo,
        CancellationToken cancellationToken) =>
        await StrictestAsync(await CompaniesAsync(userId, invitedTo, cancellationToken), cancellationToken);

    private async Task<PasswordRules> StrictestAsync(IReadOnlyList<(TenantId TenantId, string Name)> companies,
        CancellationToken cancellationToken)
    {
        if (companies.Count == 0) return PasswordRules.Default;
        PasswordRules? strictest = null;
        foreach (var company in companies)
        {
            var rules = await settings.RulesForTenantAsync(company.TenantId, cancellationToken);
            strictest = strictest is null ? rules : strictest.StricterOf(rules);
        }
        return strictest!;
    }

    private async Task<IReadOnlyList<(TenantId TenantId, string Name)>> CompaniesAsync(Guid? userId,
        TenantId? invitedTo, CancellationToken cancellationToken)
    {
        var companies = new List<(TenantId TenantId, string Name)>();
        if (userId is { } user)
        {
            var rows = await db.Memberships.AsNoTracking()
                .Where(row => row.UserId == user && row.DeactivatedAt == null && row.Tenant!.IsActive)
                .Select(row => new { row.TenantId, row.Tenant!.Name })
                .ToListAsync(cancellationToken);
            companies.AddRange(rows.Select(row => (row.TenantId, row.Name)));
        }
        if (invitedTo is { } tenant && companies.All(row => row.TenantId != tenant))
        {
            var name = await db.Tenants.AsNoTracking().Where(row => row.Id == tenant).Select(row => row.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (name is not null) companies.Add((tenant, name));
        }
        return companies;
    }
}
