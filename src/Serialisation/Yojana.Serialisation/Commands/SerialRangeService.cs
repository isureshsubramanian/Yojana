using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Commands;

// EF1002: the only interpolated values are object names; data goes through parameters.
#pragma warning disable EF1002

/// <summary>
/// SerialRange allocation and consumption (ADR-0001 SerialRange; case 7; Identity rule 13). Overlap detection runs under
/// an application lock per (tenant, scheme) inside the caller's transaction, so two allocations of one scheme never race
/// into the same ordinals. Released ranges (cancelled orders) keep blocking their ordinals: serials are never re-issued
/// (OD-07 default).
/// </summary>
public sealed class SerialRangeService(SerialisationDbContext db, ISerialSchemeSource schemes, SerialAllocationReservationStore reservations, ITenantContext tenant)
{
    public async Task<AllocateSerialRangeResult> AllocateAsync(AllocateSerialRange command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (command.Quantity <= 0)
        {
            throw new CommandRefusedException("quantity_invalid", "Quantity must be at least 1.");
        }

        var scheme = schemes.Find(command.Scheme) ?? throw new CommandRefusedException("scheme_unknown", $"Serial scheme '{command.Scheme}' is not defined for this tenant.", "define the scheme in settings");
        var existing = await db.SerialRanges.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Note == CommandTag(command.CommandId), cancellationToken);
        if (existing is not null)
        {
            var members = existing.RandomisedListRef is null ? [] : await db.SerialRangeMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.RangeId == existing.RangeId).OrderBy(m => m.Ordinal).Select(m => m.Value).ToListAsync(cancellationToken);
            return new AllocateSerialRangeResult(existing.RangeId, existing.Scheme, existing.StartValue, existing.EndValue, existing.Count, existing.RandomisedListRef is not null, members, false, command.Start);
        }

        await LockSchemeAsync(tenantId, scheme.Code, cancellationToken);
        var block = scheme.BlockFor(command.EventTime);
        var range = new SerialRange
        {
            TenantId = tenantId, RangeId = TimeOrderedId.New(command.EventTime), Scheme = scheme.Code, EnterpriseIdentifier = command.EnterpriseIdentifier, Qualifier = command.Qualifier, ItemId = command.ItemId,
            Count = command.Quantity, AllocatedToPartyRef = command.AllocatedToPartyRef, AllocatedToDocumentType = command.AllocatedTo?.Type, AllocatedToDocumentId = command.AllocatedTo?.Id,
            ReservedAt = command.EventTime, ReservedBy = command.ActorRef, Status = SerialRangeStatus.Reserved, Note = CommandTag(command.CommandId),
        };

        if (command.Randomised)
        {
            var taken = await TakenCountersAsync(tenantId, scheme, block, cancellationToken);
            if (scheme.MaxCounter + 1 - taken.Count < command.Quantity)
            {
                throw new CommandRefusedException("block_exhausted", $"Scheme {scheme.Code} block {block} has fewer than {command.Quantity} free serials.", "start a new block or widen the scheme's counter");
            }

            var picked = new HashSet<long>();
            while (picked.Count < command.Quantity)
            {
                var counter = RandomNumberGenerator.GetInt32(0, (int)Math.Min(int.MaxValue, scheme.MaxCounter + 1));
                if (!taken.Contains(counter)) picked.Add(counter);
            }

            range.RandomisedListRef = "rnd:" + range.RangeId.ToString("N")[..12];
            db.SerialRanges.Add(range);
            var values = new List<string>(command.Quantity);
            foreach (var counter in picked.OrderBy(c => c))
            {
                var value = scheme.Format(block, counter);
                values.Add(value);
                db.SerialRangeMembers.Add(new SerialRangeMember { TenantId = tenantId, RangeId = range.RangeId, Ordinal = SerialSchemeDefinition.OrdinalOf(block, counter), Value = value });
            }

            return new AllocateSerialRangeResult(range.RangeId, scheme.Code, null, null, command.Quantity, true, values, false, null);
        }

        long startCounter;
        var moved = false;
        if (command.Start is not null)
        {
            var parsed = scheme.TryParse(command.Start) ?? throw new CommandRefusedException("start_not_in_scheme", $"'{command.Start}' is not a serial of scheme {scheme.Code}.");
            if (scheme.BlockFormat is not null && parsed.Block != block)
            {
                block = parsed.Block; // an explicit start chooses its block
            }

            startCounter = parsed.Counter;
        }
        else
        {
            startCounter = await NextFreeCounterAsync(tenantId, scheme, block, 0, cancellationToken);
        }

        if (startCounter + command.Quantity - 1 > scheme.MaxCounter)
        {
            throw new CommandRefusedException("block_overflow", $"{command.Quantity} serials from {scheme.Format(block, startCounter)} would cross the end of block {block} of scheme {scheme.Code} ({scheme.CounterDigits} digits); a range stays within one block.", "allocate in the next block or split the quantity");
        }

        var startOrdinal = SerialSchemeDefinition.OrdinalOf(block, startCounter);
        var endOrdinal = startOrdinal + command.Quantity - 1;
        var overlap = await db.SerialRanges.AsNoTracking().Where(r => r.TenantId == tenantId && r.Scheme == scheme.Code && r.StartOrdinal != null && r.StartOrdinal <= endOrdinal && r.EndOrdinal >= startOrdinal).OrderBy(r => r.StartOrdinal).FirstOrDefaultAsync(cancellationToken)
            ?? await RandomMemberOverlapAsync(tenantId, scheme, startOrdinal, endOrdinal, cancellationToken);
        if (overlap is not null)
        {
            var next = await NextFreeCounterAsync(tenantId, scheme, block, startCounter, cancellationToken);
            if (command.Start is not null)
            {
                var nextFree = next + command.Quantity - 1 <= scheme.MaxCounter ? scheme.Format(block, next) : "(next block)";
                throw new CommandRefusedException("range_overlap",
                    $"{scheme.Format(block, startCounter)} → {scheme.FormatOrdinal(endOrdinal)} overlaps {overlap.StartValue} → {overlap.EndValue}, {overlap.Status} by {overlap.AllocatedToDocumentType ?? "?"} {overlap.AllocatedToDocumentId:N} on {overlap.ReservedAt:yyyy-MM-dd}. Next free start is {nextFree}.",
                    $"allocate from {nextFree}, or omit Start to take the next free serials");
            }

            startCounter = next;
            moved = true;
            startOrdinal = SerialSchemeDefinition.OrdinalOf(block, startCounter);
            endOrdinal = startOrdinal + command.Quantity - 1;
            if (startCounter + command.Quantity - 1 > scheme.MaxCounter)
            {
                throw new CommandRefusedException("block_exhausted", $"Scheme {scheme.Code} block {block} has no {command.Quantity} contiguous free serials.", "start a new block or allocate a randomised list");
            }
        }

        range.StartOrdinal = startOrdinal;
        range.EndOrdinal = endOrdinal;
        range.StartValue = scheme.FormatOrdinal(startOrdinal);
        range.EndValue = scheme.FormatOrdinal(endOrdinal);
        db.SerialRanges.Add(range);
        return new AllocateSerialRangeResult(range.RangeId, scheme.Code, range.StartValue, range.EndValue, command.Quantity, false, [], moved, command.Start);
    }

    /// <summary>
    /// After commissioning: counts each serial against the range that reserved it, moving the range to commissioning or
    /// exhausted and marking randomised members. Serials outside any range are ignored (not every tenant pre-allocates).
    /// </summary>
    // TIGHT COUPLING (P3-T11 effective reservation)
    // WHAT: commissioning reads SerialAllocationOverride rows written by ReassignAllocationHandler and takes the
    // scheme's shared application lock while reassignment takes its exclusive lock. WHY: a moved serial must be accepted
    // only on its effective order without racing a reassignment, and the lookup must stay set-based. WHAT BREAKS IF
    // SEPARATED: the unit can record the old order while the override commits the new one. Keep the mirrored handler
    // comment and both locks in SerialAllocationReservationStore.
    public async Task RecordCommissionedAsync(Guid itemId, IReadOnlyList<(Guid UnitId, string NormalisedValue)> serials,
        DocumentRef? productionOrder, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var byScheme = serials.Select(s => (s.UnitId, s.NormalisedValue, Scheme: schemes.Recognise(s.NormalisedValue))).Where(x => x.Scheme is not null).GroupBy(x => x.Scheme!.Code);
        foreach (var group in byScheme)
        {
            var scheme = group.First().Scheme!;
            await reservations.LockForCommissioningAsync(scheme.Code, cancellationToken);
            var ordinals = group.Select(x => (x.UnitId, Ordinal: scheme.TryParse(x.NormalisedValue)!.Value.Ordinal)).ToList();
            var allocated = await reservations.ResolveAsync(scheme.Code, itemId, ordinals.Select(o => o.Ordinal).ToList(), true, cancellationToken);
            foreach (var serial in ordinals)
            {
                if (!allocated.TryGetValue(serial.Ordinal, out var reservation)) continue;
                if (reservation.EffectiveOrder is { } expected
                    && (productionOrder is null || expected.Type != productionOrder.Type || expected.Id != productionOrder.Id))
                    throw new CommandRefusedException("serial_reserved_for_other_order",
                        $"Serial {scheme.FormatOrdinal(serial.Ordinal)} is reserved for production order {expected.Id}, not {productionOrder?.Id}.",
                        "commission it from its assigned production order or reassign the reservation first");
                if (reservation.Override is not null) reservation.Override.CommissionedUnitId = serial.UnitId;
            }
            var min = ordinals.Min(o => o.Ordinal);
            var max = ordinals.Max(o => o.Ordinal);
            var blocks = await db.SerialRanges.Where(r => r.TenantId == tenantId && r.Scheme == scheme.Code && r.ItemId == itemId && r.StartOrdinal != null && r.StartOrdinal <= max && r.EndOrdinal >= min).ToListAsync(cancellationToken);
            foreach (var range in blocks)
            {
                var hits = ordinals.Count(o => o.Ordinal >= range.StartOrdinal && o.Ordinal <= range.EndOrdinal);
                Bump(range, hits);
            }

            var members = await (from member in db.SerialRangeMembers
                                 join range in db.SerialRanges on new { member.TenantId, member.RangeId } equals new { range.TenantId, range.RangeId }
                                 where member.TenantId == tenantId && range.Scheme == scheme.Code && range.ItemId == itemId
                                       && member.CommissionedUnitId == null && ordinals.Select(o => o.Ordinal).Contains(member.Ordinal)
                                 select member).ToListAsync(cancellationToken);
            if (members.Count > 0)
            {
                var rangeIds = members.Select(m => m.RangeId).Distinct().ToList();
                var randomRanges = await db.SerialRanges.Where(r => r.TenantId == tenantId && rangeIds.Contains(r.RangeId)).ToListAsync(cancellationToken);
                foreach (var member in members)
                {
                    member.CommissionedUnitId = ordinals.First(o => o.Ordinal == member.Ordinal).UnitId;
                }

                foreach (var range in randomRanges)
                {
                    Bump(range, members.Count(m => m.RangeId == range.RangeId));
                }
            }
        }
    }

    private static void Bump(SerialRange range, int hits)
    {
        if (hits == 0) return;
        range.CommissionedCount += hits;
        range.Status = range.CommissionedCount >= range.Count ? SerialRangeStatus.Exhausted : SerialRangeStatus.Commissioning;
    }

    private static string CommandTag(Guid commandId) => "cmd:" + commandId.ToString("N");

    private async Task LockSchemeAsync(TenantId tenantId, string scheme, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            // An application lock lives for the transaction; outside one it would be released at once and protect nothing.
            await db.Database.BeginTransactionAsync(cancellationToken);
        }

        var resource = $"serial_range:{tenantId}:{scheme}";
        // A batch with DECLARE/EXEC is not composable, so it runs as a plain command on the context's connection and transaction.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000; SELECT @r;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        var rc = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (rc < 0)
        {
            throw new CommandRefusedException("scheme_busy", $"Another allocation of scheme {scheme} is in progress.", "retry in a moment");
        }
    }

    private async Task<long> NextFreeCounterAsync(TenantId tenantId, SerialSchemeDefinition scheme, long block, long from, CancellationToken cancellationToken)
    {
        var blockStart = SerialSchemeDefinition.OrdinalOf(block, 0);
        var blockEnd = SerialSchemeDefinition.OrdinalOf(block, scheme.MaxCounter);
        var ends = await db.SerialRanges.AsNoTracking().Where(r => r.TenantId == tenantId && r.Scheme == scheme.Code && r.EndOrdinal != null && r.EndOrdinal >= blockStart && r.StartOrdinal <= blockEnd).Select(r => new { r.StartOrdinal, r.EndOrdinal }).ToListAsync(cancellationToken);
        var candidate = Math.Max(from, 0);
        // Walk forward over the occupied intervals of the block (there are few per block; this is not a hot path).
        var intervals = ends.Select(e => (Start: e.StartOrdinal!.Value - blockStart, End: e.EndOrdinal!.Value - blockStart)).OrderBy(i => i.Start).ToList();
        var taken = await TakenCountersAsync(tenantId, scheme, block, cancellationToken);
        while (true)
        {
            var hit = intervals.FirstOrDefault(i => i.Start <= candidate && i.End >= candidate);
            if (hit != default)
            {
                candidate = hit.End + 1;
                continue;
            }

            if (taken.Contains(candidate))
            {
                candidate++;
                continue;
            }

            return candidate;
        }
    }

    private async Task<HashSet<long>> TakenCountersAsync(TenantId tenantId, SerialSchemeDefinition scheme, long block, CancellationToken cancellationToken)
    {
        var blockStart = SerialSchemeDefinition.OrdinalOf(block, 0);
        var blockEnd = SerialSchemeDefinition.OrdinalOf(block, scheme.MaxCounter);
        var members = await (from m in db.SerialRangeMembers.AsNoTracking() join r in db.SerialRanges.AsNoTracking() on new { m.TenantId, m.RangeId } equals new { r.TenantId, r.RangeId }
                             where m.TenantId == tenantId && r.Scheme == scheme.Code && m.Ordinal >= blockStart && m.Ordinal <= blockEnd select m.Ordinal - blockStart).ToListAsync(cancellationToken);
        return members.ToHashSet();
    }

    private async Task<SerialRange?> RandomMemberOverlapAsync(TenantId tenantId, SerialSchemeDefinition scheme, long startOrdinal, long endOrdinal, CancellationToken cancellationToken)
    {
        var rangeId = await (from m in db.SerialRangeMembers.AsNoTracking() join r in db.SerialRanges.AsNoTracking() on new { m.TenantId, m.RangeId } equals new { r.TenantId, r.RangeId }
                             where m.TenantId == tenantId && r.Scheme == scheme.Code && m.Ordinal >= startOrdinal && m.Ordinal <= endOrdinal select (Guid?)m.RangeId).FirstOrDefaultAsync(cancellationToken);
        return rangeId is null ? null : await db.SerialRanges.AsNoTracking().FirstAsync(r => r.TenantId == tenantId && r.RangeId == rangeId, cancellationToken);
    }
}

/// <summary><see cref="AllocateSerialRange"/> as a bus command.</summary>
public static class AllocateSerialRangeHandler
{
    public static Task<AllocateSerialRangeResult> Handle(AllocateSerialRange command, SerialRangeService ranges, CancellationToken cancellationToken) => ranges.AllocateAsync(command, cancellationToken);
}
