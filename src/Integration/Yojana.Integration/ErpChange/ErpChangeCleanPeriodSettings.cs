using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.ErpChange;

/// <summary>The tenant-owned clean period inside the fixed 30-day minimum dual run.</summary>
public sealed class ErpChangeCleanPeriodSettings(
    IntegrationDbContext db,
    ITenantContext tenant,
    ITenantSettings settings,
    IClock clock,
    IConnectionAuditSink audit)
{
    public const string SettingKey = "integration.erp_change.clean_days";
    public const int DefaultDays = 7;

    public async Task<ErpChangeCleanPeriodView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var days = stored is null ? DefaultDays : Parse(stored.Value);
        return new(days, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ErpChangeCleanPeriodView> SetAsync(Guid auditConnectionId,
        UpdateErpChangeCleanPeriodRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ErpChangeRuleException("audit_actor_required", "An authenticated actor is required.");
        Validate(request.Days);
        _ = await db.Connections.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == auditConnectionId, cancellationToken)
            ?? throw new ErpChangeRuleException("connection_not_found", "The connection was not found.");
        var before = await GetAsync(cancellationToken);
        var saved = await settings.SetAsync(SettingKey,
            request.Days.ToString(CultureInfo.InvariantCulture), request.ExpectedVersion, cancellationToken);
        await audit.AppendAsync(new(tenant.TenantId, TimeOrderedId.New(clock.UtcNow), auditConnectionId,
            ConnectionAuditEventType.ErpChangeCleanPeriodChanged, actor, clock.UtcNow,
            JsonSerializer.Serialize(new { Before = before.Days, After = request.Days },
                IntegrationContracts.Json)), cancellationToken);
        return new(request.Days, saved.Version);
    }

    private static int Parse(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
            throw new ErpChangeRuleException("erp_change_clean_days_invalid",
                "The clean period setting must be a whole number from 1 to 90 days.");
        Validate(days);
        return days;
    }

    private static void Validate(int days)
    {
        if (days is < 1 or > 90)
            throw new ErpChangeRuleException("erp_change_clean_days_invalid",
                "The clean period must be from 1 to 90 days.");
    }
}
