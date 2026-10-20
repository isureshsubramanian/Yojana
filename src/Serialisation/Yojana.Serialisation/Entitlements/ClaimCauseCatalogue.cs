using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Yojana.Serialisation.Documents;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public sealed class ClaimCauseCatalogue(SerialisationDbContext db, ITenantContext tenant)
{
    public static readonly IReadOnlyList<ClaimCause> Defaults = [
        new("STOPPED_WORKING", "Stopped working"), new("LIQUID_SPILL", "Liquid spill"),
        new("DROPPED_KNOCKED", "Dropped or knocked"), new("POWER_SURGE", "Power surge"),
        new("PART_FAILED", "Part failed"), new("COSMETIC_DAMAGE", "Cosmetic damage"),
        new("STOLEN_LOST", "Stolen or lost"), new("SOMETHING_ELSE", "Something else")];

    public async Task<IReadOnlyList<ClaimCause>> ReadAsync(CancellationToken ct)
    {
        var rows = await db.ClaimCauses.AsNoTracking().Where(x => x.TenantId == tenant.TenantId).OrderBy(x => x.Code).ToListAsync(ct);
        // The default causes keep the order the service desk asks them in; tenant additions follow by code.
        int Rank(string code) { var index = Defaults.Select(x => x.Code).ToList().IndexOf(code); return index < 0 ? Defaults.Count : index; }
        return rows.Count == 0 ? Defaults : rows.OrderBy(x => Rank(x.Code)).ThenBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => new ClaimCause(x.Code, x.CustomerWording, x.Active)).ToList();
    }

    public static string Version(IReadOnlyList<ClaimCause> causes) => DocumentVersion.Encode(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(causes.OrderBy(x => x.Code, StringComparer.Ordinal))));

    internal async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        // The tenant key range serialises concurrent first use with catalogue edits (LockedRowsAsync).
        // A single INSERT holds its range lock until commit, so two first claims cannot insert the same defaults.
        var values = string.Join(", ", Defaults.Select((_, index) => $"({{{index * 2 + 1}}}, {{{index * 2 + 2}}})"));
        object[] parameters = [tenant.TenantId.Value, ..Defaults.SelectMany(x => new object[] { x.Code, x.CustomerWording })];
        await db.Database.ExecuteSqlInterpolatedAsync(FormattableStringFactory.Create($$"""
            INSERT INTO serialisation.claim_cause (tenant_id, code, customer_wording, active, updated_by, updated_at)
            SELECT {0}, defaults.code, defaults.wording, 1, N'', SYSUTCDATETIME()
            FROM (VALUES {{values}}) defaults(code, wording)
            WHERE NOT EXISTS (SELECT 1 FROM serialisation.claim_cause WITH (UPDLOCK, HOLDLOCK)
                WHERE tenant_id = {0});
            """, parameters), ct);
    }

    // Same tenant key-range seam as EnsureDefaultsAsync: keep the lock until the catalogue edit is saved.
    internal static Task<List<ClaimCauseRow>> LockedRowsAsync(SerialisationDbContext db, TenantId tenantId, CancellationToken ct) =>
        db.ClaimCauses.FromSqlInterpolated($"SELECT * FROM serialisation.claim_cause WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = {tenantId.Value}").ToListAsync(ct);

    public async Task ValidateReportedAsync(string? code, CancellationToken ct)
    {
        if (code is null) return;
        await EnsureDefaultsAsync(ct);
        var cause = (await ReadAsync(ct)).SingleOrDefault(x => x.Code == code)
            ?? throw new CommandRefusedException("claim_cause_unknown", "Choose a known claim cause.", "reload the claim causes");
        if (!cause.Active)
            throw new CommandRefusedException("claim_cause_inactive", "This claim cause is inactive.", "choose an active cause");
    }
}

public static class UpdateClaimCausesHandler
{
    public static async Task<ClaimCausePage> Handle(UpdateClaimCauses command, SerialisationDbContext db,
        ITenantContext tenant, IClock clock, CancellationToken ct)
    {
        if (command.Items.Count == 0 || command.Items.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != command.Items.Count
            || command.Items.Any(x => string.IsNullOrWhiteSpace(x.Code) || x.Code.Length > 40 || x.Code.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
                || string.IsNullOrWhiteSpace(x.CustomerWording) || x.CustomerWording.Length > 200))
            throw new CommandRefusedException("claim_causes_invalid", "Use unique upper-case cause codes and customer wording. Keep at least one cause.");
        var rows = await ClaimCauseCatalogue.LockedRowsAsync(db, tenant.TenantId, ct);
        var current = rows.Count == 0 ? ClaimCauseCatalogue.Defaults : rows.Select(x => new ClaimCause(x.Code, x.CustomerWording, x.Active)).ToList();
        DocumentVersion.RequireExpected(command.ExpectedVersion, Convert.FromBase64String(ClaimCauseCatalogue.Version(current)),
            rows.MaxBy(x => x.UpdatedAt)?.UpdatedBy ?? "", rows.MaxBy(x => x.UpdatedAt)?.UpdatedAt ?? default);
        var deleted = rows.Where(x => command.Items.All(i => i.Code != x.Code)).Select(x => x.Code).ToList();
        if (await db.PolicyCoverItemCauses.AnyAsync(x => x.TenantId == tenant.TenantId && deleted.Contains(x.CauseCode), ct)
            || await db.Claims.AnyAsync(x => x.TenantId == tenant.TenantId && deleted.Contains(x.ReportedCauseCode!), ct))
            throw new CommandRefusedException("claim_cause_referenced", "A referenced cause cannot be deleted. Deactivate it instead.");
        db.ClaimCauses.RemoveRange(rows.Where(x => deleted.Contains(x.Code)));
        foreach (var item in command.Items)
        {
            var row = rows.SingleOrDefault(x => x.Code == item.Code);
            if (row is null) { row = new() { TenantId = tenant.TenantId, Code = item.Code, CustomerWording = item.CustomerWording }; db.ClaimCauses.Add(row); }
            row.CustomerWording = item.CustomerWording; row.Active = item.Active; row.UpdatedBy = command.ActorRef; row.UpdatedAt = clock.UtcNow;
        }
        return new(command.Items.Take(100).ToList(), command.Items.Count, 0, 100, ClaimCauseCatalogue.Version(command.Items));
    }
}
