using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Reconciliation;

/// <summary>
/// Today's calendar date for a connection, read in its legal entity's time zone (tenant zone, then UTC, as fallback).
/// Only "today" depends on the zone; the business dates being compared are calendar dates already (see
/// ReconciliationService.ModuleDigestsAsync).
/// </summary>
public sealed class ReconciliationBusinessDay(
    IntegrationDbContext db,
    ITenantContext tenant,
    ITimeZonePort zones,
    IClock clock)
{
    public async Task<DateOnly> TodayAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var legalEntityId = await db.ConnectionGatewaySettings.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId)
            .Select(row => (Guid?)row.LegalEntityId).SingleOrDefaultAsync(cancellationToken);
        var zone = await zones.ForLegalEntityAsync(legalEntityId ?? Guid.Empty,
            TimeZonePurpose.DocumentPresentation, cancellationToken) ?? "UTC";
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(zone)));
    }
}
