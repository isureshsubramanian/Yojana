using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationLinkOverrideTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Defaults_validate_and_csv_overrides_round_trip_atomically_and_survive_a_new_map_run()
    {
        var tenant = await fixture.NewTenantAsync("lnkovr");
        var policyId = await new MigrationMappingTests(fixture).PublishedPolicyAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var ids = MigrationMappingTests.SeedPipeline(db, tenant, policyId);
        await db.SaveChangesAsync();
        var settings = scope.ServiceProvider.GetRequiredService<MappingSettings>();
        var initial = await settings.GetAsync(default);
        Assert.Equal(new ReplacementLinkValues(ReplacementDirection.CustomerFacing,
            ReplacementReason.WarrantyDefect, ReplacementLinkState.ClosedReturned,
            ReplacementConditionAllowed.Any, ReplacementPricePolicy.Zero, 0m, "XXX"), initial.LinkDefaults);
        var invalid = await Assert.ThrowsAsync<MappingRuleException>(() => settings.SetAsync(
            new(true, new DateOnly(2026, 9, 1), initial.ConditionMap,
                initial.LinkDefaults with { DifferenceAmount = 1m }), default));
        Assert.Equal("replacement_zero_price_difference", invalid.Rule);
        await settings.SetAsync(new(true, new DateOnly(2026, 9, 1), initial.ConditionMap, initial.LinkDefaults), default);

        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        await mapping.EnqueueAsync(ids.BatchId, default);
        Assert.Equal(1, await mapping.RunPendingAsync(default));
        var firstMap = (await mapping.GetAsync(ids.BatchId, default))!;
        Assert.True(firstMap.ReplacementLinks > 0);
        var service = scope.ServiceProvider.GetRequiredService<LinkOverrideService>();
        var exported = Encoding.UTF8.GetString(await service.ExportAsync(ids.BatchId, default));
        var rows = MigrationCsvReader.Read(exported);
        Assert.Equal(firstMap.ReplacementLinks + 1, rows.Count);
        var header = rows[0].ToList();
        var values = rows[1].ToList();
        values[header.IndexOf("direction")] = ReplacementDirection.VendorFacing;
        values[header.IndexOf("replacement_reason")] = ReplacementReason.Goodwill;
        values[header.IndexOf("state")] = ReplacementLinkState.ClosedBilled;
        values[header.IndexOf("replacement_condition_allowed")] = ReplacementConditionAllowed.New;
        values[header.IndexOf("price_policy")] = ReplacementPricePolicy.Difference;
        values[header.IndexOf("difference_amount")] = "25";
        values[header.IndexOf("currency")] = "EUR";
        var changedCsv = Csv(header, values.Take(12));
        var added = await UploadAsync(service, changedCsv);
        Assert.Equal((1, 0, 0, 0, 0),
            (added.Added, added.Changed, added.Removed, added.Unchanged, added.ErrorTotal));
        var unchanged = await UploadAsync(service, changedCsv);
        Assert.Equal((0, 0, 0, 1),
            (unchanged.Added, unchanged.Changed, unchanged.Removed, unchanged.Unchanged));

        var afterUpload = MigrationCsvReader.Read(Encoding.UTF8.GetString(
            await service.ExportAsync(ids.BatchId, default)));
        Assert.Equal(ReplacementDirection.VendorFacing,
            afterUpload[1][header.IndexOf("direction")]);
        Assert.Equal("True", afterUpload[1][header.IndexOf("direction_overridden")]);

        var badValues = values.Take(12).ToList();
        badValues[header.IndexOf("currency")] = "XXX";
        var bad = await UploadAsync(service, Csv(header, badValues));
        Assert.Equal(1, bad.ErrorTotal);
        Assert.Equal("replacement_currency_required", Assert.Single(bad.Errors).Reason);
        Assert.Equal(ReplacementDirection.VendorFacing,
            (await service.ListAsync(null, 50, default)).Items.Single().Direction);

        var emptyValues = values.Take(5).Concat(Enumerable.Repeat("", 7)).ToList();
        var removed = await UploadAsync(service, Csv(header, emptyValues));
        Assert.Equal(1, removed.Removed);
        Assert.Empty((await service.ListAsync(null, 50, default)).Items);
        Assert.Equal(1, (await UploadAsync(service, changedCsv)).Added);

        var originalTime = fixture.Clock.UtcNow;
        fixture.Clock.UtcNow = originalTime.AddMinutes(5);
        try
        {
            await mapping.EnqueueAsync(ids.BatchId, default);
            Assert.Equal(1, await mapping.RunPendingAsync(default));
        }
        finally { fixture.Clock.UtcNow = originalTime; }
        var secondMap = (await mapping.GetAsync(ids.BatchId, default))!;
        Assert.NotEqual(firstMap.RunId, secondMap.RunId);
        Assert.Single(await db.MigrationLinkOverrides.AsNoTracking().ToListAsync());
        var afterRemap = MigrationCsvReader.Read(Encoding.UTF8.GetString(
            await service.ExportAsync(ids.BatchId, default)));
        Assert.Equal(ReplacementDirection.VendorFacing, afterRemap[1][header.IndexOf("direction")]);

        var refusedDelete = await Assert.ThrowsAsync<LinkOverrideRuleException>(() =>
            service.DeleteAllAsync(new(false), default));
        Assert.Equal("migration_link_override_confirmation_required", refusedDelete.Rule);
        Assert.Equal(1, (await service.DeleteAllAsync(new(true), default)).Removed);
    }

    private static async Task<LinkOverrideUploadResponse> UploadAsync(LinkOverrideService service, string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        await using var stream = new MemoryStream(bytes);
        return await service.UploadAsync(stream, bytes.Length, "replacement-links.csv", "user:migration-lead", default);
    }

    private static string Csv(IReadOnlyList<string> header, IEnumerable<string> values) =>
        string.Join(',', header.Take(12)) + "\r\n" + string.Join(',', values.Select(Field)) + "\r\n";

    private static string Field(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
