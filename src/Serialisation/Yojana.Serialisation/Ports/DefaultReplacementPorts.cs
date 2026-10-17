using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Reads the grading sibling's append-only table when it is installed. Before that migration exists, or before a unit
/// has a grade, replacement allocation treats received stock as new; the grading module replaces this port at merge.
/// </summary>
public sealed class DefaultUnitConditionQueryPort(SerialisationDbContext db, ITenantContext tenant) : IUnitConditionQueryPort
{
    public async Task<string> LatestGradeAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<string>("""
            IF OBJECT_ID(N'[serialisation].[unit_condition_event]', N'U') IS NULL
                SELECT CAST('' AS nvarchar(40)) AS [Value];
            ELSE
            BEGIN
                DECLARE @grade_column sysname = CASE
                    WHEN COL_LENGTH('serialisation.unit_condition_event', 'condition_grade') IS NOT NULL THEN 'condition_grade'
                    WHEN COL_LENGTH('serialisation.unit_condition_event', 'grade') IS NOT NULL THEN 'grade'
                    ELSE NULL END;
                DECLARE @time_column sysname = CASE
                    WHEN COL_LENGTH('serialisation.unit_condition_event', 'event_time') IS NOT NULL THEN 'event_time'
                    WHEN COL_LENGTH('serialisation.unit_condition_event', 'recorded_at') IS NOT NULL THEN 'recorded_at'
                    ELSE NULL END;
                IF @grade_column IS NULL OR @time_column IS NULL
                    SELECT CAST('' AS nvarchar(40)) AS [Value];
                ELSE
                BEGIN
                    DECLARE @sql nvarchar(max) = N'SELECT TOP (1) CAST(' + QUOTENAME(@grade_column)
                        + N' AS nvarchar(40)) AS [Value] FROM [serialisation].[unit_condition_event] WHERE tenant_id = @tenant AND unit_id = @unit ORDER BY '
                        + QUOTENAME(@time_column) + N' DESC';
                    EXEC sp_executesql @sql, N'@tenant uniqueidentifier, @unit uniqueidentifier', @tenant = {0}, @unit = {1};
                END
            END
            """, tenant.TenantId.Value, unitId).ToListAsync(cancellationToken);

        var grade = rows.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (grade is not null) return grade;
        return await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .Select(row => row.ConditionGrade).FirstOrDefaultAsync(cancellationToken) ?? ConditionGrades.New;
    }
}

/// <summary>Claim is local; RMA remains behind the same contract until its sibling implementation is registered.</summary>
public sealed class DefaultReplacementSourceQueryPort(SerialisationDbContext db, ITenantContext tenant) : IReplacementSourceQueryPort
{
    public async Task<ReplacementSource?> ResolveAsync(string kind, Guid id, Guid sourceEntitlementId, CancellationToken cancellationToken)
    {
        if (kind != ReplacementSourceKind.Claim) return null;
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.ClaimId == id, cancellationToken);
        if (claim is null) return null;
        if (claim.Remedy != ClaimRemedy.Replacement || claim.Status is not (ClaimStatus.Approved or ClaimStatus.PartiallyApproved or ClaimStatus.Settled or ClaimStatus.Closed)) return null;
        var entitlementIds = JsonSerializer.Deserialize<List<Guid>>(claim.EntitlementIds, LedgerJson.Options) ?? [];
        if (!entitlementIds.Contains(sourceEntitlementId)) return null;
        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.EntitlementId == sourceEntitlementId, cancellationToken);
        if (entitlement is null || entitlement.UnitId != claim.UnitId) return null;
        var sale = entitlement.SourceDocumentType is not null && entitlement.SourceDocumentId is { } documentId
            ? new DocumentRef(entitlement.SourceDocumentType, documentId, entitlement.SourceDocumentLine)
            : null;
        return new(kind, id, claim.ClaimNumber, claim.UnitId, sourceEntitlementId, claim.ClaimDate, sale, null);
    }
}

/// <summary>No return-reason table exists before the RMA sibling migration; no fee is safer than inventing one.</summary>
public sealed class DefaultReturnReasonQueryPort : IReturnReasonQueryPort
{
    public Task<RestockingFeeQuote> QuoteAsync(string? returnReasonCode, decimal basis, CancellationToken cancellationToken) =>
        Task.FromResult(new RestockingFeeQuote("none", 0m));
}
