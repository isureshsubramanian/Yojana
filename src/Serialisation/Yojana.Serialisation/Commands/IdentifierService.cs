using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Commands;

/// <summary>An identifier input after the type's policy ran: what would be stored, and whether it may be.</summary>
public sealed record PreparedIdentifier(IdentifierInput Input, IdentifierPolicy Policy, string Normalised, string ScopeKey, ValidationOutcome Validation, IReadOnlyList<string> Artefacts)
{
    public bool Blocks => Validation.Blocks;

    public UnitIdentifier ToRow(TenantId tenant, Guid unitId, DateTime validFrom, string actorRef, DateTime recordedTime, Guid eventId) => new()
    {
        TenantId = tenant,
        IdentifierId = TimeOrderedId.New(validFrom),
        UnitId = unitId,
        Type = Input.Type,
        Scheme = Policy.Scheme,
        IssuerPartyRef = Input.IssuerPartyRef,
        EnterpriseIdentifier = Input.EnterpriseIdentifier,
        Qualifier = Input.Qualifier,
        RawValue = Input.RawValue,
        NormalisedValue = Normalised,
        ScopeKey = ScopeKey,
        ValidFrom = validFrom,
        ActorRef = actorRef,
        RecordedTime = recordedTime,
        EventId = eventId,
    };
}

/// <summary>Who holds an identifier string right now, if anyone.</summary>
public sealed record OpenIdentifierHolder(Guid UnitId, Guid IdentifierId, string LifecycleStatus);

/// <summary>Identity rules 1–4 applied for the commands: normalise, validate, scope, and look up the current holder of a value.</summary>
public sealed class IdentifierService(SerialisationDbContext db, IIdentifierPolicySource policies, ITenantContext tenant)
{
    public PreparedIdentifier Prepare(IdentifierInput input, Guid? itemId, Guid? ownerPartyRef)
    {
        var policy = policies.For(input.Type);
        var normalised = IdentifierNormaliser.Normalise(input.RawValue, policy);
        var validation = IdentifierValidator.Validate(normalised.Value, policy);
        return new PreparedIdentifier(input, policy, normalised.Value, policy.ScopeKeyFor(itemId, ownerPartyRef), validation, normalised.ArtefactsStripped);
    }

    /// <summary>The unit that currently holds (scheme, scope, value), with its status, or null.</summary>
    public async Task<OpenIdentifierHolder?> HolderOfAsync(string scheme, string scopeKey, string normalised, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var hit = await (from i in db.Identifiers.AsNoTracking()
                         join u in db.Units.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { u.TenantId, u.UnitId }
                         where i.TenantId == tenantId && i.Scheme == scheme && i.ScopeKey == scopeKey && i.NormalisedValue == normalised && i.ValidTo == null
                         select new OpenIdentifierHolder(i.UnitId, i.IdentifierId, u.LifecycleStatus)).FirstOrDefaultAsync(cancellationToken);
        return hit;
    }

    /// <summary>Every unit holding (scheme, value) in any scope: a lookup without an item must disambiguate (Identity rule 1).</summary>
    public async Task<IReadOnlyList<OpenIdentifierHolder>> HoldersOfValueAsync(string scheme, string normalised, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await (from i in db.Identifiers.AsNoTracking()
                      join u in db.Units.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { u.TenantId, u.UnitId }
                      where i.TenantId == tenantId && i.Scheme == scheme && i.NormalisedValue == normalised && i.ValidTo == null
                      select new OpenIdentifierHolder(i.UnitId, i.IdentifierId, u.LifecycleStatus)).ToListAsync(cancellationToken);
    }

    /// <summary>The unit's open identifier of a type, or null.</summary>
    public Task<UnitIdentifier?> OpenOfTypeAsync(Guid unitId, string type, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return db.Identifiers.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.UnitId == unitId && i.Type == type && i.ValidTo == null, cancellationToken);
    }
}
