using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims;

public static class RecoveryClaimFilter
{
    public const string Open = "open";
    public const string Approved = "approved";
    public const string All = "all";

    public static string Parse(string? status)
    {
        var value = string.IsNullOrWhiteSpace(status) ? Open : status.Trim().ToLowerInvariant();
        return value is Open or Approved or All ? value : throw new ArgumentException("status must be open, approved or all.", nameof(status));
    }
}

public static class RecoveryClaimCsv
{
    public const string Header = "recovery_number,raised_at,customer_claim,claim_date,child_serial,child_item,vendor,vendor_term_start,vendor_term_end,term_months,claim_month_of_term,requested,approved,currency,status,vendor_reference,vendor_note\r\n";

    public static string Row(RecoveryClaimListRow row, string childItem, string vendor) => string.Join(',', new[]
    {
        Cell(row.RecoveryNumber), Cell(row.RaisedAt.ToString("O", CultureInfo.InvariantCulture)), Cell(row.CustomerClaimNumber), Cell(row.ClaimDate.ToString("O", CultureInfo.InvariantCulture)),
        Cell(row.ChildSerial), Cell(childItem), Cell(vendor), Cell(row.VendorTermStart?.ToString("O", CultureInfo.InvariantCulture)), Cell(row.VendorTermEnd?.ToString("O", CultureInfo.InvariantCulture)),
        Cell(row.VendorTermMonths?.ToString(CultureInfo.InvariantCulture)), Cell(row.ClaimMonthOfTerm?.ToString(CultureInfo.InvariantCulture)),
        Cell(row.RequestedAmount.ToString(CultureInfo.InvariantCulture)), Cell(row.ApprovedAmount?.ToString(CultureInfo.InvariantCulture)), Cell(row.Currency), Cell(row.Status),
        Cell(row.VendorReference), Cell(row.VendorNote),
    }) + "\r\n";

    internal static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Set-oriented recovery reads. Names stay behind the item and party ports; this query returns stable references only.</summary>
public sealed class RecoveryQueries(SerialisationDbContext db, ITenantContext tenant)
{
    private sealed class RecoveryFact
    {
        public Guid RecoveryClaimId { get; init; }
        public required string RecoveryNumber { get; init; }
        public DateTime RaisedAt { get; init; }
        public Guid CustomerClaimId { get; init; }
        public required string CustomerClaimNumber { get; init; }
        public DateOnly ClaimDate { get; init; }
        public DateTime? ClaimApprovedAt { get; init; }
        public Guid ChildUnitId { get; init; }
        public string? SearchSerial { get; init; }
        public Guid ChildItemId { get; init; }
        public Guid? ParentUnitId { get; init; }
        public Guid VendorPartyRef { get; init; }
        public DateOnly? VendorTermStart { get; init; }
        public DateOnly? VendorTermEnd { get; init; }
        public int DurationValue { get; init; }
        public required string DurationUnit { get; init; }
        public decimal RequestedAmount { get; init; }
        public decimal? ApprovedAmount { get; init; }
        public required string Currency { get; init; }
        public required string Status { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public string? ApprovedBy { get; init; }
        public string? VendorReference { get; init; }
        public string? VendorNote { get; init; }
    }

    private sealed class NotRaisedFact
    {
        public Guid CustomerClaimId { get; init; }
        public required string CustomerClaimNumber { get; init; }
        public DateOnly ClaimDate { get; init; }
        public DateTime ApprovedAt { get; init; }
        public Guid RootUnitId { get; init; }
        public Guid RootItemId { get; init; }
        public required string CoverageSource { get; init; }
        public string? PolicyReason { get; init; }
    }

    private sealed class AssemblyFact
    {
        public Guid ParentUnitId { get; init; }
        public Guid ChildUnitId { get; init; }
        public DateTime InstalledAt { get; init; }
        public DateTime? RemovedAt { get; init; }
    }

    public async Task<RecoveryClaimPage> PageAsync(string? status, Guid? vendor, Guid? childItem, string? find, List<Guid>? matchingVendors, DateOnly asOf, int skip, int take, CancellationToken cancellationToken)
    {
        var parsedStatus = RecoveryClaimFilter.Parse(status);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 500);
        var query = Query();
        if (parsedStatus == RecoveryClaimFilter.Open) query = query.Where(row => row.Status == RecoveryClaimStatus.Raised);
        else if (parsedStatus == RecoveryClaimFilter.Approved) query = query.Where(row => row.Status == RecoveryClaimStatus.Approved);
        if (vendor is { } vendorId) query = query.Where(row => row.VendorPartyRef == vendorId);
        if (childItem is { } itemId) query = query.Where(row => row.ChildItemId == itemId);
        var search = find?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            matchingVendors ??= [];
            query = query.Where(row => row.RecoveryNumber.Contains(search) || row.CustomerClaimNumber.Contains(search)
                || (row.SearchSerial != null && row.SearchSerial.Contains(search)) || matchingVendors.Contains(row.VendorPartyRef));
        }

        var total = await query.CountAsync(cancellationToken);
        var facts = await query.OrderBy(row => row.Status == RecoveryClaimStatus.Approved).ThenBy(row => row.RaisedAt).ThenBy(row => row.RecoveryClaimId)
            .Skip(skip).Take(take).ToListAsync(cancellationToken);
        var unitIds = facts.Select(row => row.ChildUnitId).Concat(facts.Select(row => row.ParentUnitId).OfType<Guid>()).Distinct().ToList();
        var serials = await SerialsAsync(unitIds, cancellationToken);
        var rows = facts.Select(row => new RecoveryClaimListRow(
            row.RecoveryClaimId, row.RecoveryNumber, row.RaisedAt, row.CustomerClaimId, row.CustomerClaimNumber, row.ClaimDate, row.ClaimApprovedAt,
            row.ChildUnitId, Serial(serials, row.ChildUnitId), row.ChildItemId, row.ParentUnitId, row.ParentUnitId is { } parentId ? Serial(serials, parentId) : null,
            row.VendorPartyRef, row.VendorTermStart, row.VendorTermEnd, TermMonths(row.DurationValue, row.DurationUnit), MonthOfTerm(row.VendorTermStart, row.ClaimDate),
            row.RequestedAmount, row.ApprovedAmount, row.Currency, row.Status, Math.Max(0, asOf.DayNumber - DateOnly.FromDateTime(row.RaisedAt).DayNumber),
            row.ApprovedAt, row.ApprovedBy, row.VendorReference, row.VendorNote)).ToList();
        return new(rows, total, skip, take);
    }

    public async Task<RecoveryClaimSummary> SummaryAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var recoveries = db.RecoveryClaims.AsNoTracking().Where(row => row.TenantId == tenantId);
        var open = Totals(await recoveries.Where(row => row.Status == RecoveryClaimStatus.Raised)
            .GroupBy(row => row.Currency).Select(group => new CurrencyGroup(group.Key, group.Count(), group.Sum(row => row.RequestedAmount), group.Sum(row => row.ApprovedAmount ?? 0m)))
            .ToListAsync(cancellationToken));
        var yearStart = new DateTime(asOf.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = yearStart.AddYears(1);
        var approved = Totals(await recoveries.Where(row => row.Status == RecoveryClaimStatus.Approved && row.ApprovedAt >= yearStart && row.ApprovedAt < yearEnd)
            .GroupBy(row => row.Currency).Select(group => new CurrencyGroup(group.Key, group.Count(), group.Sum(row => row.RequestedAmount), group.Sum(row => row.ApprovedAmount ?? 0m)))
            .ToListAsync(cancellationToken));
        var oldest = await recoveries.Where(row => row.Status == RecoveryClaimStatus.Raised)
            .OrderBy(row => row.RaisedAt).ThenBy(row => row.RecoveryClaimId)
            .Select(row => new { row.RaisedAt, row.RecoveryNumber, row.VendorPartyRef }).FirstOrDefaultAsync(cancellationToken);
        var vendors = Options(await recoveries.GroupBy(row => row.VendorPartyRef).Select(group => new OptionGroup(group.Key, group.Count())).ToListAsync(cancellationToken));
        var childItems = Options(await (from recovery in recoveries
                                        join current in db.Current.AsNoTracking() on new { recovery.TenantId, UnitId = recovery.ChildUnitId } equals new { current.TenantId, current.UnitId }
                                        group recovery by current.ItemId into groupRows
                                        select new OptionGroup(groupRows.Key, groupRows.Count())).ToListAsync(cancellationToken));
        var expired = (await NotRaisedRowsAsync(null, asOf, cancellationToken)).Count(row => row.Reason == RecoveryNotRaisedReason.VendorTermEnded);
        return new(open.Sum(row => row.ClaimCount), open, approved, expired,
            oldest is null ? null : new(Math.Max(0, asOf.DayNumber - DateOnly.FromDateTime(oldest.RaisedAt).DayNumber), oldest.RecoveryNumber, oldest.VendorPartyRef),
            vendors, childItems);
    }

    // Grouped rows are ordered in memory: EF cannot order by members of a record built by a constructor in the query.
    private sealed record CurrencyGroup(string Currency, int Count, decimal Requested, decimal Approved);

    private sealed record OptionGroup(Guid Id, int Count);

    private static List<RecoveryCurrencyTotal> Totals(List<CurrencyGroup> groups) => groups
        .OrderByDescending(g => g.Count).ThenBy(g => g.Currency, StringComparer.Ordinal)
        .Select(g => new RecoveryCurrencyTotal(g.Currency, g.Count, g.Requested, g.Approved)).ToList();

    private static List<RecoveryFilterOption> Options(List<OptionGroup> groups) => groups
        .OrderByDescending(g => g.Count).ThenBy(g => g.Id).Select(g => new RecoveryFilterOption(g.Id, g.Count)).ToList();

    public async Task<RecoveryNotRaisedReport> NotRaisedAsync(int days, DateOnly asOf, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 3660);
        return new(days, await NotRaisedRowsAsync(days, asOf, cancellationToken));
    }

    private IQueryable<RecoveryFact> Query()
    {
        var tenantId = tenant.TenantId;
        return from recovery in db.RecoveryClaims.AsNoTracking()
               join claim in db.Claims.AsNoTracking() on new { recovery.TenantId, ClaimId = recovery.CustomerClaimId } equals new { claim.TenantId, claim.ClaimId }
               join entitlement in db.Entitlements.AsNoTracking() on new { recovery.TenantId, EntitlementId = recovery.InboundEntitlementId } equals new { entitlement.TenantId, entitlement.EntitlementId }
               join current in db.Current.AsNoTracking() on new { recovery.TenantId, UnitId = recovery.ChildUnitId } equals new { current.TenantId, current.UnitId }
               where recovery.TenantId == tenantId
               let parentUnitId = db.AssemblyLinks.Where(link => link.TenantId == tenantId && link.ChildUnitId == recovery.ChildUnitId
                       && DateOnly.FromDateTime(link.InstalledAt) <= claim.ClaimDate
                       && (link.RemovedAt == null || DateOnly.FromDateTime(link.RemovedAt.Value) >= claim.ClaimDate))
                   .OrderByDescending(link => link.InstalledAt).Select(link => (Guid?)link.ParentUnitId).FirstOrDefault()
               let searchSerial = db.Identifiers.Where(identifier => identifier.TenantId == tenantId && identifier.UnitId == recovery.ChildUnitId && identifier.ValidTo == null)
                   .OrderBy(identifier => identifier.Type == IdentifierType.InternalSerial ? 0 : 1).ThenByDescending(identifier => identifier.RecordedTime)
                   .Select(identifier => identifier.RawValue).FirstOrDefault()
               select new RecoveryFact
               {
                   RecoveryClaimId = recovery.RecoveryClaimId, RecoveryNumber = recovery.RecoveryNumber, RaisedAt = recovery.RaisedAt,
                   CustomerClaimId = claim.ClaimId, CustomerClaimNumber = claim.ClaimNumber, ClaimDate = claim.ClaimDate, ClaimApprovedAt = claim.AdjudicatedAt,
                   ChildUnitId = recovery.ChildUnitId, SearchSerial = searchSerial, ChildItemId = current.ItemId, ParentUnitId = parentUnitId, VendorPartyRef = recovery.VendorPartyRef,
                   VendorTermStart = entitlement.StartDate, VendorTermEnd = entitlement.EndDate, DurationValue = entitlement.DurationValue, DurationUnit = entitlement.DurationUnit,
                   RequestedAmount = recovery.RequestedAmount, ApprovedAmount = recovery.ApprovedAmount, Currency = recovery.Currency, Status = recovery.Status,
                   ApprovedAt = recovery.ApprovedAt, ApprovedBy = recovery.ApprovedBy, VendorReference = recovery.VendorReference, VendorNote = recovery.VendorNote,
               };
    }

    private async Task<List<RecoveryNotRaisedRow>> NotRaisedRowsAsync(int? days, DateOnly asOf, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var fromAt = days is { } count ? asOf.AddDays(1 - count).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DateTime.MinValue;
        var until = asOf.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var candidates = await (from claim in db.Claims.AsNoTracking()
                                join current in db.Current.AsNoTracking() on new { claim.TenantId, claim.UnitId } equals new { current.TenantId, current.UnitId }
                                where claim.TenantId == tenantId && (claim.Status == ClaimStatus.Approved || claim.Status == ClaimStatus.PartiallyApproved)
                                    && claim.AdjudicatedAt != null && claim.AdjudicatedAt >= fromAt && claim.AdjudicatedAt < until
                                    && !db.RecoveryClaims.Any(recovery => recovery.TenantId == tenantId && recovery.CustomerClaimId == claim.ClaimId)
                                orderby claim.AdjudicatedAt descending, claim.ClaimId
                                select new NotRaisedFact
                                {
                                    CustomerClaimId = claim.ClaimId, CustomerClaimNumber = claim.ClaimNumber, ClaimDate = claim.ClaimDate,
                                    ApprovedAt = claim.AdjudicatedAt!.Value, RootUnitId = claim.UnitId, RootItemId = current.ItemId, CoverageSource = claim.CoverageSource,
                                    PolicyReason = claim.RecoveryNotRaisedReason,
                                }).ToListAsync(cancellationToken);
        if (candidates.Count == 0) return [];

        var earliest = candidates.Min(row => row.ClaimDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var latest = candidates.Max(row => row.ClaimDate).AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var links = await db.AssemblyLinks.AsNoTracking().Where(link => link.TenantId == tenantId && link.InstalledAt < latest && (link.RemovedAt == null || link.RemovedAt >= earliest) && link.ChildUnitId != null)
            .Select(link => new AssemblyFact { ParentUnitId = link.ParentUnitId, ChildUnitId = link.ChildUnitId!.Value, InstalledAt = link.InstalledAt, RemovedAt = link.RemovedAt }).ToListAsync(cancellationToken);
        var unitsByClaim = candidates.ToDictionary(row => row.CustomerClaimId, row => UnitsAt(row.RootUnitId, row.ClaimDate, links));
        var unitIds = unitsByClaim.Values.SelectMany(value => value).Distinct().ToList();
        var entitlements = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId)
                && row.Direction == EntitlementDirection.Inbound && row.PayerPartyRef != null)
            .Select(row => new { row.UnitId, row.PayerPartyRef, row.StartDate, row.EndDate, row.DurationValue, row.DurationUnit }).ToListAsync(cancellationToken);
        var currentItems = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.ItemId }).ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var serials = await SerialsAsync(unitIds, cancellationToken);
        var answer = new List<RecoveryNotRaisedRow>(candidates.Count);
        foreach (var claim in candidates)
        {
            // The approval-time reason wins: campaign-backed recall and goodwill both use campaign_claim. The source
            // fallback below remains for recall claims recorded before approval began snapshotting a reason.
            if (claim.PolicyReason is { } decidedReason)
            {
                answer.Add(new(claim.CustomerClaimId, claim.CustomerClaimNumber, claim.ClaimDate, claim.ApprovedAt, claim.RootUnitId, Serial(serials, claim.RootUnitId), claim.RootItemId,
                    decidedReason, null, null, null, null, null));
                continue;
            }

            if (claim.CoverageSource == ClaimCoverageSource.Recall)
            {
                answer.Add(new(claim.CustomerClaimId, claim.CustomerClaimNumber, claim.ClaimDate, claim.ApprovedAt, claim.RootUnitId, Serial(serials, claim.RootUnitId), claim.RootItemId,
                    RecoveryNotRaisedReason.RecallClaim, null, null, null, null, null));
                continue;
            }

            var units = unitsByClaim[claim.CustomerClaimId];
            var expired = entitlements.Where(row => units.Contains(row.UnitId) && row.StartDate <= claim.ClaimDate && row.EndDate < claim.ClaimDate)
                .OrderByDescending(row => row.EndDate).ThenBy(row => row.UnitId).FirstOrDefault();
            if (expired is null)
            {
                answer.Add(new(claim.CustomerClaimId, claim.CustomerClaimNumber, claim.ClaimDate, claim.ApprovedAt, claim.RootUnitId, Serial(serials, claim.RootUnitId), claim.RootItemId,
                    RecoveryNotRaisedReason.NoVendorTerm, null, null, null, null, null));
                continue;
            }

            answer.Add(new(claim.CustomerClaimId, claim.CustomerClaimNumber, claim.ClaimDate, claim.ApprovedAt, expired.UnitId, Serial(serials, expired.UnitId), currentItems[expired.UnitId],
                RecoveryNotRaisedReason.VendorTermEnded, expired.PayerPartyRef, expired.StartDate, expired.EndDate, TermMonths(expired.DurationValue, expired.DurationUnit), MonthOfTerm(expired.StartDate, claim.ClaimDate)));
        }

        return answer;
    }

    private static HashSet<Guid> UnitsAt(Guid rootUnitId, DateOnly on, IReadOnlyList<AssemblyFact> links)
    {
        var units = new HashSet<Guid> { rootUnitId };
        var frontier = new List<Guid> { rootUnitId };
        var from = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var until = from.AddDays(1);
        for (var depth = 0; depth < InboundRecoveryRules.MaximumAssemblyDepth && frontier.Count > 0; depth++)
        {
            var next = links.Where(link => frontier.Contains(link.ParentUnitId) && link.InstalledAt < until
                    && (link.RemovedAt is null || link.RemovedAt >= from))
                .Select(link => link.ChildUnitId).Where(units.Add).ToList();
            frontier = next;
        }
        return units;
    }

    private async Task<Dictionary<Guid, string>> SerialsAsync(List<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return [];
        var tenantId = tenant.TenantId;
        var rows = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .Select(row => new { row.UnitId, row.Type, row.RawValue, row.RecordedTime, row.IdentifierId }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1).ThenByDescending(row => row.RecordedTime).ThenBy(row => row.IdentifierId)
            .Select(row => row.RawValue).First());
    }

    private static string Serial(IReadOnlyDictionary<Guid, string> serials, Guid unitId) => serials.GetValueOrDefault(unitId) ?? "—";

    internal static int? TermMonths(int value, string unit) => unit == DurationRule.Months ? value : null;

    internal static int? MonthOfTerm(DateOnly? start, DateOnly claimDate)
    {
        if (start is not { } first || claimDate < first) return null;
        var months = (claimDate.Year - first.Year) * 12 + claimDate.Month - first.Month;
        return Math.Max(1, months + (claimDate.Day >= first.Day ? 1 : 0));
    }
}
