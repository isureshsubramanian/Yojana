using System.Text;
using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public sealed record TenantUserRow(
    Guid? UserId,
    Guid? InvitationId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> SignInMethods,
    DateTime? LastSignInAt,
    string Status,
    DateTime? InvitationExpiresAt,
    DateTime? DeactivatedAt,
    string? DeactivatedBy,
    DateTime? LockedUntil,
    string? Version);

public sealed record TenantUserPage(IReadOnlyList<TenantUserRow> Items, string? NextCursor);
public sealed record TenantUserSummary(int Active, int Invited, int Deactivated);
public sealed record TenantUserDetail(TenantUserRow User, IReadOnlyList<string> AlsoMemberOf,
    int OtherMembershipCount, DateTime? PasswordChangedAt);

public sealed class UserManagementService(
    PlatformDbContext db,
    ITenantContext tenant,
    SignInThrottleService throttles,
    SecurityEventWriter events,
    ISecurityStampService stamps,
    Contracts.Time.IClock clock)
{
    private const string CustomerPortalRole = "customer_portal";

    public async Task<TenantUserPage> PageAsync(string? status, string? role, string? q, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new UserManagementInputException("take_invalid", "Take must be from 1 to 100.");
        status = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        if (status is not ("all" or "active" or "invited" or "deactivated"))
            throw new UserManagementInputException("status_invalid", "Status must be all, active, invited or deactivated.");
        if (!string.IsNullOrWhiteSpace(role) && !YojanaRole.IsKnown(role))
            throw new UserManagementInputException("role_invalid", "The role is unknown.");

        var after = DecodeCursor(cursor);
        var tenantId = tenant.TenantId;
        var search = q?.Trim();
        var candidates = new List<Candidate>(take * 2 + 2);

        if (status is "all" or "active" or "deactivated")
        {
            var users = db.Memberships.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.CustomerPartyRef == null &&
                            !m.Roles.Contains(CustomerPortalRole));
            if (status == "active") users = users.Where(m => m.DeactivatedAt == null);
            if (status == "deactivated") users = users.Where(m => m.DeactivatedAt != null);
            if (!string.IsNullOrWhiteSpace(role)) users = users.Where(m => m.Roles.Contains(role));
            if (!string.IsNullOrWhiteSpace(search))
                users = users.Where(m => m.User!.DisplayName.Contains(search) || m.User.Email.Contains(search));
            if (after is not null)
                users = users.Where(m => m.User!.DisplayName.CompareTo(after.Name) > 0 ||
                                         (m.User.DisplayName == after.Name && m.UserId.CompareTo(after.Id) > 0));
            var rows = await users.OrderBy(m => m.User!.DisplayName).ThenBy(m => m.UserId).Take(take + 1)
                .Select(m => new
                {
                    m.UserId, m.User!.DisplayName, m.User.Email, m.User.PasswordHash, m.Roles,
                    m.DeactivatedAt, m.DeactivatedByUserId, m.Version,
                }).ToListAsync(cancellationToken);
            candidates.AddRange(rows.Select(row => new Candidate(row.UserId, null, row.DisplayName, row.Email,
                row.Roles, row.PasswordHash is null ? [] : ["password"], row.DeactivatedAt,
                row.DeactivatedByUserId, null, row.Version)));
        }

        if (status is "all" or "invited")
        {
            // user_invitation is global by design. Tenant-side reads must always carry this explicit predicate.
            var invitations = db.UserInvitations.AsNoTracking().Where(i => i.TenantId == tenantId &&
                i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > clock.UtcNow);
            if (!string.IsNullOrWhiteSpace(role)) invitations = invitations.Where(i => i.Roles.Contains(role));
            if (!string.IsNullOrWhiteSpace(search))
                invitations = invitations.Where(i => i.DisplayName.Contains(search) || i.Email.Contains(search));
            if (after is not null)
                invitations = invitations.Where(i => i.DisplayName.CompareTo(after.Name) > 0 ||
                                                       (i.DisplayName == after.Name && i.Id.CompareTo(after.Id) > 0));
            var rows = await invitations.OrderBy(i => i.DisplayName).ThenBy(i => i.Id).Take(take + 1)
                .Select(i => new { i.Id, i.DisplayName, i.Email, i.Roles, i.ExpiresAt }).ToListAsync(cancellationToken);
            candidates.AddRange(rows.Select(row => new Candidate(null, row.Id, row.DisplayName, row.Email,
                row.Roles, [], null, null, row.ExpiresAt, null)));
        }

        // The tie-break must order ids as SQL Server does (SqlGuid), or equal names at a page boundary are skipped or
        // repeated: the cursor predicate above is evaluated by the database.
        var ordered = candidates.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => new System.Data.SqlTypes.SqlGuid(row.Id)).Take(take + 1).ToList();
        var hasMore = ordered.Count > take;
        if (hasMore) ordered.RemoveAt(ordered.Count - 1);

        var userIds = ordered.Where(row => row.UserId is not null).Select(row => row.UserId!.Value).ToList();
        var lastSignIns = await db.SecurityEvents.AsNoTracking()
            .Where(row => row.Kind == SecurityEventKind.SignInSucceeded && row.UserId != null &&
                          userIds.Contains(row.UserId.Value))
            .GroupBy(row => row.UserId!.Value)
            .Select(group => new { UserId = group.Key, At = group.Max(row => row.OccurredAt) })
            .ToDictionaryAsync(row => row.UserId, row => row.At, cancellationToken);
        var actorIds = ordered.Select(row => row.DeactivatedByUserId).OfType<Guid>().Distinct().ToList();
        var actorNames = await db.Users.AsNoTracking().Where(row => actorIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.DisplayName, cancellationToken);
        var locks = await throttles.LockedAccountsAsync(ordered.Where(row => row.UserId is not null)
            .Select(row => row.Email).ToList(), cancellationToken);

        var result = ordered.Select(row => ToView(row, lastSignIns, actorNames, locks)).ToList();
        return new TenantUserPage(result, hasMore ? EncodeCursor(ordered[^1]) : null);
    }

    public async Task<TenantUserSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var staff = db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId &&
            m.CustomerPartyRef == null && !m.Roles.Contains(CustomerPortalRole));
        var active = await staff.CountAsync(m => m.DeactivatedAt == null, cancellationToken);
        var deactivated = await staff.CountAsync(m => m.DeactivatedAt != null, cancellationToken);
        // user_invitation has no RLS; this predicate is the tenant boundary.
        var invited = await db.UserInvitations.AsNoTracking().CountAsync(i => i.TenantId == tenantId &&
            i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > clock.UtcNow, cancellationToken);
        return new TenantUserSummary(active, invited, deactivated);
    }

    public async Task<TenantUserDetail?> DetailAsync(Guid userId, Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var membership = await db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId &&
                m.UserId == userId && m.CustomerPartyRef == null && !m.Roles.Contains(CustomerPortalRole))
            .Select(m => new
            {
                m.UserId, m.User!.DisplayName, m.User.Email, m.User.PasswordHash, m.Roles,
                m.DeactivatedAt, m.DeactivatedByUserId, m.Version,
            }).FirstOrDefaultAsync(cancellationToken);
        if (membership is null) return null;

        var candidate = new Candidate(membership.UserId, null, membership.DisplayName, membership.Email,
            membership.Roles, membership.PasswordHash is null ? [] : ["password"], membership.DeactivatedAt,
            membership.DeactivatedByUserId, null, membership.Version);
        var last = await db.SecurityEvents.AsNoTracking().Where(e => e.UserId == userId &&
                e.Kind == SecurityEventKind.SignInSucceeded)
            .MaxAsync(e => (DateTime?)e.OccurredAt, cancellationToken);
        var passwordChanged = await db.SecurityEvents.AsNoTracking().Where(e => e.UserId == userId &&
                (e.Kind == SecurityEventKind.PasswordChanged || e.Kind == SecurityEventKind.PasswordResetCompleted))
            .MaxAsync(e => (DateTime?)e.OccurredAt, cancellationToken);
        var actorName = membership.DeactivatedByUserId is { } deactivator
            ? await db.Users.AsNoTracking().Where(u => u.Id == deactivator).Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var locks = await throttles.LockedAccountsAsync([membership.Email], cancellationToken);

        var administeredTenants = await db.Memberships.AsNoTracking().Where(m => m.UserId == actorUserId &&
                m.DeactivatedAt == null && m.Roles.Contains(YojanaRole.TenantAdministrator))
            .Select(m => m.TenantId).ToListAsync(cancellationToken);
        var otherMemberships = await db.Memberships.AsNoTracking().Where(m => m.UserId == userId &&
                m.TenantId != tenantId && m.DeactivatedAt == null)
            .Select(m => new { m.TenantId, m.Tenant!.Name }).ToListAsync(cancellationToken);
        var named = otherMemberships.Where(row => administeredTenants.Contains(row.TenantId))
            .Select(row => row.Name).OrderBy(name => name).ToList();
        var hiddenCount = otherMemberships.Count - named.Count;
        return new TenantUserDetail(ToView(candidate,
                new Dictionary<Guid, DateTime> { [userId] = last ?? default },
                membership.DeactivatedByUserId is { } id && actorName is not null
                    ? new Dictionary<Guid, string> { [id] = actorName } : new Dictionary<Guid, string>(), locks,
                last is not null), named, hiddenCount, passwordChanged);
    }

    public async Task<TenantUserRow> ChangeRolesAsync(Guid userId, IReadOnlyList<string>? requestedRoles,
        string? version, Guid actorUserId, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var roles = (requestedRoles ?? []).Distinct(StringComparer.Ordinal).OrderBy(value => value).ToList();
        if (roles.Count == 0 || roles.Any(role => !YojanaRole.IsKnown(role)))
            throw new UserManagementInputException("roles_invalid", "Choose at least one known staff role.");
        byte[] expected;
        if (string.IsNullOrWhiteSpace(version))
            throw new UserManagementInputException("version_invalid", "The version is invalid.");
        try { expected = Convert.FromBase64String(version); }
        catch (FormatException) { throw new UserManagementInputException("version_invalid", "The version is invalid."); }

        var tenantId = tenant.TenantId;
        // The administrator count and membership write are one serializable invariant. Separating or weakening this
        // ordering lets two administrators remove their roles concurrently and leave the tenant without an administrator.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var membership = await db.Memberships.Include(m => m.User).FirstOrDefaultAsync(m => m.TenantId == tenantId &&
            m.UserId == userId && m.CustomerPartyRef == null && !m.Roles.Contains(CustomerPortalRole), cancellationToken);
        if (membership is null) throw new UserNotFoundException();
        if (!membership.Version.SequenceEqual(expected)) throw new UserVersionConflictException();
        if (membership.DeactivatedAt == null && membership.Roles.Contains(YojanaRole.TenantAdministrator) &&
            !roles.Contains(YojanaRole.TenantAdministrator) && await ActiveAdministratorCountAsync(cancellationToken) <= 1)
            throw new LastAdministratorException();

        var before = membership.Roles.OrderBy(value => value).ToList();
        membership.Roles = roles;
        events.Add(SecurityEventKind.RolesChanged, userId, tenantId, actorUserId, request,
            RoleChangeReason(before, roles));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new UserVersionConflictException(); }
        // Roles are claims in the cookie. Rotating the stamp is the only way to stop old sessions retaining old roles.
        await stamps.RotateSecurityStampAsync(userId, "roles_changed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await DetailAsync(userId, actorUserId, cancellationToken))!.User;
    }

    public async Task DeactivateAsync(Guid userId, string? reason, Guid actorUserId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        if (userId == actorUserId) throw new CannotDeactivateSelfException();
        if (string.IsNullOrWhiteSpace(reason))
            throw new UserManagementInputException("reason_required", "Give a reason for deactivation.");
        if (reason.Trim().Length > 100 || reason.Any(char.IsControl))
            throw new UserManagementInputException("reason_invalid", "The reason must be at most 100 printable characters.");
        var tenantId = tenant.TenantId;
        // Coupled to the administrator count below for the same shared last-administrator invariant as role changes;
        // the serializable boundary must cover both the count and the deactivation write.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var membership = await StaffMembershipAsync(userId, cancellationToken);
        if (membership is null) throw new UserNotFoundException();
        if (membership.DeactivatedAt is not null) return;
        if (membership.Roles.Contains(YojanaRole.TenantAdministrator) &&
            await ActiveAdministratorCountAsync(cancellationToken) <= 1) throw new LastAdministratorException();
        membership.DeactivatedAt = clock.UtcNow;
        membership.DeactivatedByUserId = actorUserId;
        events.Add(SecurityEventKind.UserDeactivated, userId, tenantId, actorUserId, request,
            reason.Trim());
        await db.SaveChangesAsync(cancellationToken);
        await stamps.RotateSecurityStampAsync(userId, "membership_deactivated", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReactivateAsync(Guid userId, Guid actorUserId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        var membership = await StaffMembershipAsync(userId, cancellationToken);
        if (membership is null) throw new UserNotFoundException();
        if (membership.DeactivatedAt is null) return;
        membership.DeactivatedAt = null;
        membership.DeactivatedByUserId = null;
        events.Add(SecurityEventKind.UserReactivated, userId, tenant.TenantId, actorUserId, request, "reactivated");
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<Data.Platform.Entities.UserTenantMembership?> StaffMembershipAsync(Guid userId,
        CancellationToken cancellationToken) => db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenant.TenantId &&
        m.UserId == userId && m.CustomerPartyRef == null && !m.Roles.Contains(CustomerPortalRole), cancellationToken);

    private Task<int> ActiveAdministratorCountAsync(CancellationToken cancellationToken) =>
        db.Memberships.CountAsync(m => m.TenantId == tenant.TenantId && m.DeactivatedAt == null &&
            m.CustomerPartyRef == null && !m.Roles.Contains(CustomerPortalRole) &&
            m.Roles.Contains(YojanaRole.TenantAdministrator), cancellationToken);

    private static TenantUserRow ToView(Candidate row, Dictionary<Guid, DateTime> lastSignIns,
        IReadOnlyDictionary<Guid, string> actorNames, IReadOnlyDictionary<string, DateTime> locks,
        bool hasLastSignIn = true)
    {
        DateTime until = default;
        var locked = row.UserId is not null && locks.TryGetValue(row.Email, out until);
        var status = row.InvitationId is not null ? "invited" : row.DeactivatedAt is not null ? "deactivated" :
            locked ? "locked" : "active";
        DateTime? last = null;
        if (hasLastSignIn && row.UserId is { } id && lastSignIns.TryGetValue(id, out var at) && at != default) last = at;
        return new TenantUserRow(row.UserId, row.InvitationId, row.DisplayName, row.Email, row.Roles,
            row.SignInMethods, last, status, row.InvitationExpiresAt, row.DeactivatedAt,
            row.DeactivatedByUserId is { } actor ? actorNames.GetValueOrDefault(actor) : null,
            locked ? until : null, row.Version is null ? null : Convert.ToBase64String(row.Version));
    }

    private static string RoleChangeReason(IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var removed = before.Except(after).ToList();
        var added = after.Except(before).ToList();
        var reason = $"before:-{string.Join(',', removed)};after:+{string.Join(',', added)}";
        return reason[..Math.Min(100, reason.Length)];
    }

    private static string EncodeCursor(Candidate row) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new UserCursor(row.DisplayName, row.Id))));

    private static UserCursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            return JsonSerializer.Deserialize<UserCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))
                   ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new UserManagementInputException("cursor_invalid", "The user-list cursor is invalid.");
        }
    }

    private sealed record Candidate(Guid? UserId, Guid? InvitationId, string DisplayName, string Email,
        IReadOnlyList<string> Roles, IReadOnlyList<string> SignInMethods, DateTime? DeactivatedAt,
        Guid? DeactivatedByUserId, DateTime? InvitationExpiresAt, byte[]? Version)
    {
        public Guid Id => UserId ?? InvitationId!.Value;
    }

    private sealed record UserCursor(string Name, Guid Id);
}

public sealed class UserManagementInputException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class UserNotFoundException : Exception;
public sealed class UserVersionConflictException : Exception;
public sealed class LastAdministratorException : Exception;
public sealed class CannotDeactivateSelfException : Exception;
