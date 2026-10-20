using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public sealed record SecurityRequestContext(string? ClientAddress, string? UserAgent);

public sealed class SecurityEventWriter(PlatformDbContext db, IClock clock, SignInOptions options)
{
    public void Add(
        string kind,
        Guid? userId,
        TenantId? tenantId,
        Guid? actorUserId,
        SecurityRequestContext request,
        string reason,
        string? unknownNormalisedEmail = null)
    {
        if (!SecurityEventKind.All.Contains(kind))
            throw new ArgumentException($"Unknown security event kind '{kind}'.", nameof(kind));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 100 || reason.Any(char.IsControl))
            throw new ArgumentException("A security event reason must be 1 to 100 printable characters.", nameof(reason));

        db.SecurityEvents.Add(new SecurityEvent
        {
            Id = TimeOrderedId.New(clock.UtcNow),
            OccurredAt = clock.UtcNow,
            Kind = kind,
            UserId = userId,
            TenantId = tenantId,
            ActorUserId = actorUserId,
            ClientAddress = Truncate(request.ClientAddress, 45),
            UserAgent = Truncate(request.UserAgent, 256),
            Reason = reason,
            UnknownUserHash = unknownNormalisedEmail is null ? null : UnknownUserHash(unknownNormalisedEmail),
        });
    }

    private string UnknownUserHash(string normalisedEmail)
    {
        var configured = Encoding.UTF8.GetBytes(options.AuditHmacKey);
        return Convert.ToHexString(HMACSHA256.HashData(configured, Encoding.UTF8.GetBytes(normalisedEmail))).ToLowerInvariant();
    }

    private static string? Truncate(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}

public sealed class SecurityStampService(
    PlatformDbContext db,
    SecurityEventWriter events) : ISecurityStampService
{
    public async Task RotateSecurityStampAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 100 ||
            reason.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')))
        {
            throw new ArgumentException("A security-stamp reason must be a short machine-readable code.", nameof(reason));
        }

        var user = await db.Users.FirstOrDefaultAsync(row => row.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("The user no longer exists.");
        user.SecurityStamp = Guid.NewGuid();
        events.Add(SecurityEventKind.SecurityStampRotated, userId, null, null,
            new SecurityRequestContext(null, null), reason);
        await db.SaveChangesAsync(cancellationToken);
    }
}
