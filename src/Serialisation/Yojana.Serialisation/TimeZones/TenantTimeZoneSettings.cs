using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.TimeZones;

/// <summary>Tenant-owned document time zones over the platform's temporal settings store.</summary>
public sealed class TenantTimeZoneSettings(ITenantSettings settings, IPartyNamePort parties, IClock clock)
{
    // Coupled to the factory registration in SerialisationModule: a direct registration exposes the platform settings
    // DbContext beside SerialisationDbContext in certificate and entitlement handler graphs, which stops Wolverine startup.
    public const string Key = "general.time_zone";

    public async Task<TenantTimeZoneSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null ? null : Deserialise(stored.Value);
        var legalEntities = (await parties.SearchAsync("", 500, cancellationToken))
            .Where(row => row.PartyType == PartyType.LegalEntity)
            .Select(row => new LegalEntityTimeZoneOption(row.PartyRef, row.Name ?? row.Code, row.Code))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var history = new List<TenantTimeZoneHistoryRow>();
        foreach (var version in await settings.HistoryAsync(Key, cancellationToken))
        {
            var historic = Deserialise(version.Value);
            history.Add(new(historic.TenantTimeZone, historic.LegalEntityOverrides, historic.UpdatedBy,
                historic.UpdatedAt, version.ValidFrom, version.ValidTo));
        }

        return new(policy?.TenantTimeZone, policy?.LegalEntityOverrides ?? [], legalEntities,
            TimeZoneCatalogue.All(clock.UtcNow), stored?.Version ?? ITenantSettings.MissingVersion,
            policy?.UpdatedBy, policy?.UpdatedAt, history);
    }

    public async Task<TenantTimeZoneSettingsView> SetAsync(UpdateTenantTimeZoneSettings update, string actorRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorRef))
            throw new CommandRefusedException("audit_actor_required", "Time-zone settings need an audit actor.");
        var tenantZone = NormaliseZone(update.TenantTimeZone);
        var entityIds = update.LegalEntityOverrides.Select(row => row.LegalEntityId).Distinct().ToList();
        if (entityIds.Count != update.LegalEntityOverrides.Count)
            throw new CommandRefusedException("time_zone_legal_entity_duplicate",
                "Each legal entity can have only one time-zone override.");
        var names = entityIds.Count == 0
            ? new Dictionary<Guid, PartyLabel>()
            : await parties.NamesAsync(entityIds, cancellationToken);
        var overrides = update.LegalEntityOverrides.Select(row =>
        {
            if (!names.TryGetValue(row.LegalEntityId, out var label) || label.PartyType != PartyType.LegalEntity)
                throw new CommandRefusedException("time_zone_legal_entity_unknown",
                    "A time-zone override must name a known legal entity.");
            return new LegalEntityTimeZoneOverride(row.LegalEntityId, NormaliseZone(row.TimeZone)
                ?? throw new CommandRefusedException("time_zone_required", "A legal-entity override needs a time zone."));
        }).OrderBy(row => row.LegalEntityId).ToList();
        var policy = new TenantTimeZonePolicy(tenantZone, overrides, actorRef, clock.UtcNow);
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), update.ExpectedVersion,
            cancellationToken);
        return await GetAsync(cancellationToken);
    }

    internal async Task<TenantTimeZonePolicy?> PolicyAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } value ? Deserialise(value) : null;

    internal static TenantTimeZonePolicy Deserialise(string value) =>
        JsonSerializer.Deserialize<TenantTimeZonePolicy>(value, LedgerJson.Options)
        ?? throw new InvalidOperationException("Stored time-zone settings are invalid.");

    private static string? NormaliseZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var requested = value.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(requested, out _))
            throw new CommandRefusedException("time_zone_unknown", $"Unknown time zone '{requested}'.");
        if (requested.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return "Etc/UTC";
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(requested, out _)) return requested;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(requested, out var iana)) return iana;
        throw new CommandRefusedException("time_zone_not_iana",
            $"Time zone '{requested}' has no IANA identifier and cannot be stored.");
    }
}

public static class TimeZoneCatalogue
{
    public static IReadOnlyList<TimeZoneOption> All(DateTime nowUtc)
    {
        var instant = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var iana = IanaId(zone.Id);
            if (iana is not null && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var resolved))
                zones.TryAdd(iana, resolved);
        }
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Etc/UTC", out var utc)) zones.TryAdd("Etc/UTC", utc);
        return zones.Select(row =>
        {
            var offset = row.Value.GetUtcOffset(instant);
            var offsetText = Offset(offset);
            return new TimeZoneOption(row.Key, offsetText, $"{row.Key} · {offsetText}");
        }).OrderBy(row => row.Id, StringComparer.Ordinal).ToList();
    }

    private static string? IanaId(string id)
    {
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return "Etc/UTC";
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)) return id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
    }

    private static string Offset(TimeSpan value)
    {
        if (value == TimeSpan.Zero) return "UTC+00:00";
        var sign = value < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{value.Duration():hh\\:mm}";
    }
}

public static class DocumentTimeZoneFormatter
{
    public static string Abbreviation(TimeZoneInfo timeZone, DateTimeOffset local)
    {
        if (timeZone.Equals(TimeZoneInfo.Utc) || timeZone.Id.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            || timeZone.Id.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)) return "UTC";
        var name = timeZone.IsDaylightSavingTime(local) ? timeZone.DaylightName : timeZone.StandardName;
        if (name.Contains("Coordinated Universal", StringComparison.OrdinalIgnoreCase)) return "UTC";
        var letters = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals("of", StringComparison.OrdinalIgnoreCase))
            .Select(word => char.ToUpperInvariant(word[0])).ToArray();
        if (letters.Length is > 1 and <= 5) return new string(letters);
        var sign = local.Offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{local.Offset.Duration():hh\\:mm}";
    }
}

/// <summary>
/// Production adapter: a legal-entity override outranks the entity's direct owner-module zone for presentation and
/// entitlement calendars alike. Both purposes then inherit the tenant zone and finally UTC. Entitlement location
/// lookup remains direct-or-null so the entitlement handler can try the obligor before those shared fallbacks.
/// </summary>
public sealed class TenantTimeZonePort(TenantTimeZoneSettings settings, ITimeZoneSourcePort source)
    : ITimeZonePort, ITenantTimeZoneReadPort
{
    // Coupled to CreateEntitlementHandler.ZoneAsync: calendar location lookup must return direct-or-null so that method
    // can try the obligor before this adapter applies tenant/UTC fallback through legal-entity lookup. Applying the
    // fallback here would silently skip a configured obligor zone.
    public async Task<string?> ForLocationAsync(Guid locationId, string purpose,
        CancellationToken cancellationToken)
    {
        var direct = await KnownAsync(source.ForLocationAsync(locationId, cancellationToken));
        if (direct is not null) return direct;
        if (purpose != TimeZonePurpose.DocumentPresentation) return null;
        var policy = await PolicyAsync(cancellationToken);
        return Known(policy?.TenantTimeZone) ?? "UTC";
    }

    public async Task<string?> ForLegalEntityAsync(Guid legalEntityId, string purpose,
        CancellationToken cancellationToken)
    {
        if (purpose is not (TimeZonePurpose.DocumentPresentation or TimeZonePurpose.EntitlementCalendar))
            return await KnownAsync(source.ForLegalEntityAsync(legalEntityId, cancellationToken));
        var policy = await PolicyAsync(cancellationToken);
        var configured = policy?.LegalEntityOverrides.FirstOrDefault(row => row.LegalEntityId == legalEntityId)?.TimeZone;
        var entityZone = Known(configured)
            ?? await KnownAsync(source.ForLegalEntityAsync(legalEntityId, cancellationToken));
        if (entityZone is not null) return entityZone;
        return Known(policy?.TenantTimeZone) ?? "UTC";
    }

    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        Known((await PolicyAsync(cancellationToken))?.TenantTimeZone) ?? "UTC";

    // The adapter is scoped to one request or command, so the tenant setting is read once per scope rather than once
    // per unit; a bulk command over 500 units would otherwise issue 500 setting reads.
    private Task<TenantTimeZonePolicy?>? cachedPolicy;

    private Task<TenantTimeZonePolicy?> PolicyAsync(CancellationToken cancellationToken) =>
        cachedPolicy ??= settings.PolicyAsync(cancellationToken);

    private static async Task<string?> KnownAsync(Task<string?> candidate) => Known(await candidate);

    private static string? Known(string? candidate) =>
        candidate is not null && TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out _) ? candidate : null;
}
