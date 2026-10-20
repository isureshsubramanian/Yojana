using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public sealed record SignInLockState(bool AccountLocked, TimeSpan? AddressRetryAfter);

public sealed record FailedAttemptResult(bool AccountLockedNow, TimeSpan? AddressRetryAfter);

public sealed class SignInThrottleService(PlatformDbContext db, IClock clock, SignInOptions options)
{
    // TIGHT COUPLING (trusted client address): clientAddress comes from HttpContext.RemoteIpAddress after
    // PlatformSetup's explicitly configured forwarded-header middleware. Bypassing that boundary permits address-key
    // spoofing; running the middleware too late groups every proxied client under the proxy's address.
    private const string AccountKey = "account";
    private const string AddressKey = "address";

    public async Task<SignInLockState> LockStateAsync(string normalisedEmail, string clientAddress,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var accountHash = Hash(AccountKey, normalisedEmail);
        var addressHash = Hash(AddressKey, clientAddress);
        var rows = await db.SignInThrottles.AsNoTracking()
            .Where(row => (row.KeyKind == AccountKey && row.KeyHash == accountHash) ||
                          (row.KeyKind == AddressKey && row.KeyHash == addressHash))
            .ToListAsync(cancellationToken);
        var account = rows.FirstOrDefault(row => row.KeyKind == AccountKey);
        var address = rows.FirstOrDefault(row => row.KeyKind == AddressKey);
        return new SignInLockState(
            account?.LockedUntil > now,
            address?.LockedUntil > now ? address.LockedUntil.Value - now : null);
    }

    /// <summary>Reads account-only lockouts for a bounded user-list page; address throttles are never scanned.</summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> LockedAccountsAsync(
        IReadOnlyCollection<string> normalisedEmails, CancellationToken cancellationToken)
    {
        if (normalisedEmails.Count == 0) return new Dictionary<string, DateTime>();
        var keys = normalisedEmails.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(email => Convert.ToHexString(Hash(AccountKey, email)), email => email,
                StringComparer.Ordinal);
        var hashes = keys.Keys.Select(Convert.FromHexString).ToList();
        var rows = await db.SignInThrottles.AsNoTracking()
            .Where(row => row.KeyKind == AccountKey && hashes.Contains(row.KeyHash) && row.LockedUntil > clock.UtcNow)
            .Select(row => new { row.KeyHash, row.LockedUntil }).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => keys[Convert.ToHexString(row.KeyHash)], row => row.LockedUntil!.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<FailedAttemptResult> RegisterFailureAsync(string normalisedEmail, string clientAddress,
        bool includeAccount, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                var now = clock.UtcNow;
                var accountLocked = false;
                if (includeAccount)
                {
                    var account = await GetOrCreateAsync(AccountKey, normalisedEmail, now,
                        TimeSpan.FromMinutes(options.AccountWindowMinutes), cancellationToken);
                    accountLocked = RegisterAccountFailure(account, now);
                }

                var address = await GetOrCreateAsync(AddressKey, clientAddress, now,
                    TimeSpan.FromMinutes(options.AddressWindowMinutes), cancellationToken);
                var addressRetryAfter = RegisterAddressFailure(address, now);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new FailedAttemptResult(accountLocked, addressRetryAfter);
            }
            catch (Exception exception) when (attempt < 3 &&
                                               (exception is DbUpdateException ||
                                                exception is SqlException { Number: 1205 }))
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<SignInLockState> CompleteSuccessAsync(string normalisedEmail, string clientAddress,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var now = clock.UtcNow;
        var accountHash = Hash(AccountKey, normalisedEmail);
        var addressHash = Hash(AddressKey, clientAddress);
        var account = await db.SignInThrottles.FirstOrDefaultAsync(
            row => row.KeyKind == AccountKey && row.KeyHash == accountHash, cancellationToken);
        var address = await db.SignInThrottles.AsNoTracking().FirstOrDefaultAsync(
            row => row.KeyKind == AddressKey && row.KeyHash == addressHash, cancellationToken);
        var state = new SignInLockState(account?.LockedUntil > now,
            address?.LockedUntil > now ? address.LockedUntil.Value - now : null);
        if (!state.AccountLocked && state.AddressRetryAfter is null && account is not null)
        {
            db.SignInThrottles.Remove(account);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    private async Task<SignInThrottle> GetOrCreateAsync(string kind, string value, DateTime now, TimeSpan window,
        CancellationToken cancellationToken)
    {
        var hash = Hash(kind, value);
        var row = await db.SignInThrottles.FirstOrDefaultAsync(
            candidate => candidate.KeyKind == kind && candidate.KeyHash == hash, cancellationToken);
        if (row is null)
        {
            row = NewRow(kind, hash, now, window);
            db.SignInThrottles.Add(row);
        }
        else if (row.ExpiresAt <= now)
        {
            row.WindowStartedAt = now;
            row.FailureCount = 0;
            row.LockoutCount = 0;
            row.LockedUntil = null;
            row.ExpiresAt = now + window;
        }
        else if (kind == AccountKey && row.LockedUntil <= now && row.WindowStartedAt + window <= now)
        {
            // The five failures are a fixed 15-minute window. Escalation survives that window, so a later batch of
            // five failures gets the next delay rather than silently returning to one minute.
            row.WindowStartedAt = now;
            row.FailureCount = 0;
            row.LockedUntil = null;
            row.ExpiresAt = now + window;
        }

        row.UpdatedAt = now;
        return row;
    }

    private bool RegisterAccountFailure(SignInThrottle row, DateTime now)
    {
        row.FailureCount++;
        if (row.FailureCount < options.AccountFailureThreshold)
        {
            return false;
        }

        row.LockoutCount++;
        var multiplier = Math.Pow(2, Math.Min(row.LockoutCount - 1, 30));
        var minutes = Math.Min(options.MaximumLockoutMinutes, options.InitialLockoutMinutes * multiplier);
        row.LockedUntil = now.AddMinutes(minutes);
        row.ExpiresAt = row.LockedUntil.Value.AddMinutes(options.AccountWindowMinutes);
        return true;
    }

    private TimeSpan? RegisterAddressFailure(SignInThrottle row, DateTime now)
    {
        row.FailureCount++;
        if (row.FailureCount < options.AddressFailureThreshold)
        {
            return null;
        }

        row.LockedUntil = row.WindowStartedAt.AddMinutes(options.AddressWindowMinutes);
        if (row.LockedUntil <= now) row.LockedUntil = now.AddMinutes(options.AddressWindowMinutes);
        row.ExpiresAt = row.LockedUntil.Value;
        return row.LockedUntil.Value - now;
    }

    private static SignInThrottle NewRow(string kind, byte[] hash, DateTime now, TimeSpan window) => new()
    {
        KeyKind = kind,
        KeyHash = hash,
        WindowStartedAt = now,
        ExpiresAt = now + window,
        UpdatedAt = now,
    };

    private static byte[] Hash(string kind, string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + value));
}
