using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public sealed record PasskeyCeremonyStart(Guid CeremonyId, JsonElement Options);
public sealed record PasskeyView(Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt, bool Synced);
public sealed record PasskeyAccountView(bool Allowed, bool Passwordless, IReadOnlyList<PasskeyView> Passkeys);
public sealed record PasskeyAssertion(AppUser? User, bool Locked, bool NotAllowed);

public sealed class PasskeyUnavailableException : Exception;
public sealed class PasskeyNotAllowedException : Exception;
public sealed class PasskeyCeremonyExpiredException : Exception;
public sealed class PasskeyRejectedException : Exception;
public sealed class PasskeyNameInvalidException : Exception;
public sealed class PasskeyLimitReachedException : Exception;
public sealed class PasskeyNotFoundException : Exception;

/// <summary>
/// Passkey (WebAuthn) ceremonies, P9-T27. The ceremony flow is the one from the owner's TapInAuth library (start:
/// options from Fido2 kept server-side; finish: Fido2 verifies the browser's answer against them), here on Yojana's own
/// user store, throttle, security audit and tenant policy. Fido2NetLib does every protocol check; nothing is
/// verified by hand. The options are stored as a <see cref="PasskeyCeremony"/> row and consumed by the first answer,
/// so a captured answer cannot be replayed.
/// </summary>
public sealed class PasskeyService(
    PlatformDbContext db,
    PasskeyPolicy policy,
    SecondStepPolicy secondStepPolicy,
    SignInThrottleService throttles,
    SecurityEventWriter events,
    ISecurityStampService stamps,
    SignInMethodNotices notices,
    IServiceProvider services,
    IClock clock)
{
    public const int MaximumPerUser = 20;
    public const int MaximumNameLength = 60;
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    public async Task<PasskeyAccountView> AccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var permission = await policy.EffectiveForUserAsync(userId, cancellationToken);
        var passkeys = await db.UserPasskeys.AsNoTracking().Where(row => row.UserId == userId)
            .OrderBy(row => row.CreatedAt)
            .Select(row => new PasskeyView(row.Id, row.Name, row.CreatedAt, row.LastUsedAt, row.BackedUp))
            .ToListAsync(cancellationToken);
        return new PasskeyAccountView(permission.Allowed, permission.Passwordless, passkeys);
    }

    /// <param name="currentPassword">Asked so that a signed-in session alone (a stolen cookie) cannot add a lasting way
    /// in, as for replacing the authenticator app.</param>
    public async Task<PasskeyCeremonyStart> StartRegistrationAsync(Guid userId, string? name, string? currentPassword,
        CancellationToken cancellationToken)
    {
        var fido = Fido();
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) trimmed = "Passkey";
        if (trimmed.Length > MaximumNameLength || trimmed.Any(char.IsControl)) throw new PasskeyNameInvalidException();
        if (!(await policy.EffectiveForUserAsync(userId, cancellationToken)).Allowed) throw new PasskeyNotAllowedException();
        var user = await RequirePasswordAsync(userId, currentPassword, cancellationToken);
        var existing = await db.UserPasskeys.AsNoTracking().Where(row => row.UserId == userId)
            .Select(row => row.CredentialId).ToListAsync(cancellationToken);
        if (existing.Count >= MaximumPerUser) throw new PasskeyLimitReachedException();

        var options = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            // A discoverable credential with the fingerprint, face or PIN check, so the same passkey can later sign the
            // person in on its own (TapInAuth asked only for "preferred"; passwordless sign-in needs it required).
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = new AuthenticationExtensionsClientInputs { CredProps = true },
        });
        return await SaveCeremonyAsync(PasskeyCeremonyPurpose.Register, userId, trimmed, options.ToJson(),
            cancellationToken);
    }

    public async Task<PasskeyView> FinishRegistrationAsync(Guid userId, Guid ceremonyId,
        AuthenticatorAttestationRawResponse? answer, TenantId? tenantId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        var fido = Fido();
        var ceremony = await ConsumeAsync(ceremonyId, PasskeyCeremonyPurpose.Register, userId, cancellationToken);
        if (answer?.Response is null) throw new PasskeyRejectedException();
        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = answer,
                OriginalOptions = CredentialCreateOptions.FromJson(ceremony.OptionsJson),
                // Credential ids are unique across every user: one authenticator answer can belong to one account.
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await db.UserPasskeys.AsNoTracking().AnyAsync(row => row.CredentialId == args.CredentialId, ct),
            }, cancellationToken);
        }
        catch (Fido2VerificationException) { throw new PasskeyRejectedException(); }

        if (await db.UserPasskeys.CountAsync(row => row.UserId == userId, cancellationToken) >= MaximumPerUser)
            throw new PasskeyLimitReachedException();
        var row = new UserPasskey
        {
            Id = Guid.CreateVersion7(), UserId = userId, CredentialId = credential.Id, PublicKey = credential.PublicKey,
            SignCount = credential.SignCount, Name = ceremony.Name ?? "Passkey",
            Aaguid = credential.AaGuid == Guid.Empty ? null : credential.AaGuid,
            Transports = credential.Transports is { Length: > 0 } transports
                ? string.Join(',', transports.Select(transport => transport.ToString().ToLowerInvariant())) : null,
            BackupEligible = credential.IsBackupEligible, BackedUp = credential.IsBackedUp, CreatedAt = clock.UtcNow,
        };
        db.UserPasskeys.Add(row);
        events.Add(SecurityEventKind.PasskeyAdded, userId, tenantId, userId, request, "passkey_added");
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId, cancellationToken);
        await notices.QueueAsync(user, tenantId, $"Passkey · {row.Name}", false, request, cancellationToken);
        return new PasskeyView(row.Id, row.Name, row.CreatedAt, null, row.BackedUp);
    }

    /// <param name="userId">The person who has just given their password (a passkey as the second step), or null for a
    /// passwordless sign-in, where the browser offers every passkey it holds for this site.</param>
    public async Task<PasskeyCeremonyStart> StartSignInAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var fido = Fido();
        List<PublicKeyCredentialDescriptor> allowed = [];
        if (userId is { } known)
        {
            var passkeys = await db.UserPasskeys.AsNoTracking().Where(row => row.UserId == known)
                .Select(row => new { row.CredentialId, row.Transports }).ToListAsync(cancellationToken);
            allowed = passkeys.Select(row => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey,
                row.CredentialId, Transports(row.Transports))).ToList();
        }
        var options = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            // Alone, the passkey is both steps, so the device must check the person; after a password it is the second.
            UserVerification = userId is null ? UserVerificationRequirement.Required : UserVerificationRequirement.Preferred,
        });
        return await SaveCeremonyAsync(userId is null ? PasskeyCeremonyPurpose.Passwordless
            : PasskeyCeremonyPurpose.SecondStep, userId, null, options.ToJson(), cancellationToken);
    }

    /// <summary>
    /// Verifies a sign-in answer. A rejected answer counts as a failed sign-in for the account it names (and always for
    /// the address), exactly as a wrong authenticator code does; a locked account stays locked whichever way it signs in.
    /// </summary>
    public async Task<PasskeyAssertion> FinishSignInAsync(Guid? userId, Guid ceremonyId,
        AuthenticatorAssertionRawResponse? answer, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var fido = Fido();
        var clientAddress = string.IsNullOrWhiteSpace(request.ClientAddress) ? "unknown" : request.ClientAddress.Trim();
        var ceremony = await ConsumeAsync(ceremonyId, userId is null ? PasskeyCeremonyPurpose.Passwordless
            : PasskeyCeremonyPurpose.SecondStep, userId, cancellationToken);
        // An address over its limit is refused before any lookup, so it cannot probe which credential ids exist.
        if ((await throttles.LockStateAsync(string.Empty, clientAddress, cancellationToken)).AddressRetryAfter is not null)
            return new PasskeyAssertion(null, true, false);
        var credentialId = answer?.RawId is { Length: > 0 } rawId ? rawId : null;
        var passkey = credentialId is null ? null
            : await db.UserPasskeys.SingleOrDefaultAsync(row => row.CredentialId == credentialId, cancellationToken);
        var owner = passkey is null ? null
            : await db.Users.SingleOrDefaultAsync(row => row.Id == passkey.UserId && row.IsActive, cancellationToken);
        if (passkey is null || owner is null || userId is { } expected && passkey.UserId != expected)
            return await RejectAsync(userId is null ? owner : await db.Users.AsNoTracking()
                .SingleAsync(row => row.Id == userId, cancellationToken), clientAddress, request, cancellationToken);

        var lockState = await throttles.LockStateAsync(owner.Email, clientAddress, cancellationToken);
        if (lockState.AccountLocked || lockState.AddressRetryAfter is not null) return new PasskeyAssertion(null, true, false);

        VerifyAssertionResult verified;
        try
        {
            verified = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = answer!,
                OriginalOptions = AssertionOptions.FromJson(ceremony.OptionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                // The browser names the account the passkey was made for; it must be the passkey's owner.
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(passkey.UserId.ToByteArray())),
            }, cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            return await RejectAsync(owner, clientAddress, request, cancellationToken);
        }

        // Checked only after the signature, so the answer "not allowed" goes to the passkey's holder alone and never
        // tells a stranger which credential ids exist. A refused but genuine answer is not a failed attempt.
        var permission = await policy.EffectiveForUserAsync(owner.Id, cancellationToken);
        if (!permission.Allowed || userId is null && !permission.Passwordless)
        {
            events.Add(SecurityEventKind.SignInFailed, owner.Id, null, null, request, "passkey_not_allowed");
            await db.SaveChangesAsync(cancellationToken);
            return new PasskeyAssertion(null, false, true);
        }

        passkey.SignCount = verified.SignCount;
        passkey.BackedUp = verified.IsBackedUp;
        passkey.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var completion = await throttles.CompleteSuccessAsync(owner.Email, clientAddress, cancellationToken);
        return completion.AccountLocked || completion.AddressRetryAfter is not null
            ? new PasskeyAssertion(null, true, false)
            : new PasskeyAssertion(owner, false, false);
    }

    public async Task<AppUser> RemoveAsync(Guid userId, Guid passkeyId, string? currentPassword, TenantId? tenantId,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var passkey = await db.UserPasskeys.SingleOrDefaultAsync(row => row.Id == passkeyId && row.UserId == userId,
            cancellationToken) ?? throw new PasskeyNotFoundException();
        var user = await RequirePasswordAsync(userId, currentPassword, cancellationToken);
        // The last second step of someone whose company requires one cannot be removed by them; an administrator's
        // second-step reset is the way back, as for the authenticator app.
        if (await secondStepPolicy.EffectiveForUserAsync(userId, cancellationToken) == SecondStepMode.Required &&
            !await db.UserAuthenticators.AnyAsync(row => row.UserId == userId &&
                row.Status == UserAuthenticatorStatus.Active, cancellationToken) &&
            !await db.UserPasskeys.AnyAsync(row => row.UserId == userId && row.Id != passkeyId, cancellationToken))
            throw new SecondStepRemovalNotAllowedException();
        db.UserPasskeys.Remove(passkey);
        events.Add(SecurityEventKind.PasskeyRemoved, userId, tenantId, userId, request, "passkey_removed");
        await db.SaveChangesAsync(cancellationToken);
        // Sessions the removed passkey opened elsewhere end now; the caller re-issues this one.
        await stamps.RotateSecurityStampAsync(userId, "passkey_removed", cancellationToken);
        await notices.QueueAsync(user, tenantId, $"Passkey · {passkey.Name}", true, request, cancellationToken);
        return await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId, cancellationToken);
    }

    private async Task<PasskeyAssertion> RejectAsync(AppUser? user, string clientAddress,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var failure = await throttles.RegisterFailureAsync(user?.Email ?? string.Empty, clientAddress,
            includeAccount: user is not null, cancellationToken);
        events.Add(SecurityEventKind.SignInFailed, user?.Id, null, null, request, "passkey_rejected");
        if (failure.AccountLockedNow || failure.AddressRetryAfter is not null)
            events.Add(SecurityEventKind.LockedOut, user?.Id, null, null, request,
                failure.AccountLockedNow ? "account_lockout" : "address_limit");
        await db.SaveChangesAsync(cancellationToken);
        if (failure.AccountLockedNow || failure.AddressRetryAfter is not null) return new PasskeyAssertion(null, true, false);
        throw new PasskeyRejectedException();
    }

    private async Task<PasskeyCeremonyStart> SaveCeremonyAsync(string purpose, Guid? userId, string? name,
        string optionsJson, CancellationToken cancellationToken)
    {
        var row = new PasskeyCeremony
        {
            Id = Guid.CreateVersion7(), Purpose = purpose, UserId = userId, Name = name, OptionsJson = optionsJson,
            CreatedAt = clock.UtcNow, ExpiresAt = clock.UtcNow + CeremonyLifetime,
        };
        db.PasskeyCeremonies.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        using var options = JsonDocument.Parse(optionsJson);
        return new PasskeyCeremonyStart(row.Id, options.RootElement.Clone());
    }

    /// <summary>The delete is the claim: of two answers racing for one ceremony, only the one that deletes it goes on.</summary>
    private async Task<PasskeyCeremony> ConsumeAsync(Guid ceremonyId, string purpose, Guid? userId,
        CancellationToken cancellationToken)
    {
        var ceremony = await db.PasskeyCeremonies.AsNoTracking().SingleOrDefaultAsync(row => row.Id == ceremonyId,
            cancellationToken);
        if (ceremony is null || ceremony.Purpose != purpose || ceremony.UserId != userId ||
            await db.PasskeyCeremonies.Where(row => row.Id == ceremonyId).ExecuteDeleteAsync(cancellationToken) != 1 ||
            ceremony.ExpiresAt <= clock.UtcNow)
            throw new PasskeyCeremonyExpiredException();
        return ceremony;
    }

    /// <summary>A wrong current password counts toward the account lockout, as on the second-step pages.</summary>
    private async Task<AppUser> RequirePasswordAsync(Guid userId, string? password, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId && row.IsActive, cancellationToken);
        if (user.PasswordHash is not null && PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash)) return user;
        await throttles.RegisterAccountFailureAsync(user.Email, cancellationToken);
        throw new SecondStepPasswordWrongException();
    }

    private static AuthenticatorTransport[]? Transports(string? value) => value?.Split(',')
        .Select(name => Enum.TryParse<AuthenticatorTransport>(name, true, out var transport) ? (AuthenticatorTransport?)transport : null)
        .OfType<AuthenticatorTransport>().ToArray();

    private IFido2 Fido() => services.GetService<IFido2>() ?? throw new PasskeyUnavailableException();
}
