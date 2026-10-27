using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class Cx01AdapterTests
{
    [Fact]
    public void Bill_of_materials_sync_defaults_to_once_a_day_at_two_in_the_morning()
    {
        var settings = new Cx01ConnectionSettings(new Uri("https://source.example.invalid"), "FICTIONAL_DEMO", "test");
        Assert.Equal(Cx01BomSyncSchedule.Daily, settings.BomSyncSchedule);
        Assert.Equal("02:00", settings.BomSyncDailyAt);
    }

    [Theory]
    // Site in India (UTC+05:30): 02:00 local is 20:30 UTC the day before.
    [InlineData("2026-09-01T21:00:00Z", "2026-09-01T20:30:00Z")]
    [InlineData("2026-09-01T20:29:00Z", "2026-08-31T20:30:00Z")]
    public void The_daily_run_is_the_latest_two_in_the_morning_site_time(string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            Cx01BomSyncSchedule.MostRecentDailyRun("02:00", TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"),
                DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));

    [Theory]
    [InlineData(null, "daily", "2026-09-01T10:00:00Z", true)]                    // never read: read now
    [InlineData("2026-09-01T20:31:00Z", "daily", "2026-09-02T10:00:00Z", false)] // read after last night's 02:00
    [InlineData("2026-09-01T20:29:00Z", "daily", "2026-09-01T20:45:00Z", true)]  // 02:00 passed since the last read
    [InlineData("2026-09-01T10:00:00Z", "every_6_hours", "2026-09-01T15:59:00Z", false)]
    [InlineData("2026-09-01T10:00:00Z", "every_6_hours", "2026-09-01T16:00:00Z", true)]
    public async Task A_bill_of_materials_read_is_due_only_on_its_schedule(string? lastRead, string schedule, string now, bool due)
    {
        var settings = new Cx01ConnectionSettings(new Uri("https://source.example.invalid"), "FICTIONAL_DEMO", "test")
            { BomSyncSchedule = schedule };
        var watermark = lastRead is null ? null
            : Cx01BomSyncSchedule.Watermark(DateTime.Parse(lastRead, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal(due, await new Cx01BomSyncSchedule(new FixedZone("Asia/Kolkata")).DueAsync(settings, watermark,
            DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal), CancellationToken.None));
    }

    private sealed class FixedZone(string zone) : Yojana.Serialisation.Contracts.ITenantTimeZoneReadPort
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(zone);
    }

    [Theory]
    [InlineData("Items?$top=80&$skip=20", "/b1s/v2/Items?$top=80&$skip=20")]
    [InlineData("/b1s/v2/Items?$skip=20", "/b1s/v2/Items?$skip=20")]
    [InlineData("https://erp.example.invalid:50000/b1s/v2/Items?$skip=20", "/b1s/v2/Items?$skip=20")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_page_link_relative_to_the_service_root_stays_under_it(string? link, string? expected) =>
        Assert.Equal(expected, Cx01Protocol.NextPage(link));

    [Theory]
    [InlineData("2026-10-24T00:00:00Z")]
    [InlineData("2026-10-24")]
    public void A_change_marker_converts_the_server_local_stamp_to_utc(string updateDate)
    {
        using var row = JsonDocument.Parse($$"""{"DocEntry":1291,"UpdateDate":"{{updateDate}}","UpdateTime":"23:03:57"}""");
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 17, 33, 57, TimeSpan.Zero),
            Cx01Watermark.From(row.RootElement, "1291", TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).Timestamp);
        Assert.Equal("2026-10-24T230357|1291", Cx01Mapper.ChangeMarker(row.RootElement, "1291"));
    }

    [Fact]
    public void Legacy_keyed_watermarks_are_reread_as_server_local_and_new_ones_round_trip_as_utc()
    {
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // Stored before the fix: 21:32 server-local in India, written as if UTC, keyed by the document.
        var keyed = Cx01Watermark.Parse("2026-10-27T21:32:13.0000000+00:00|1294", india);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 16, 2, 13, TimeSpan.Zero), keyed.Timestamp);
        Assert.Equal("1294", keyed.Key);
        // West of UTC the same legacy form moves later, never earlier than the real change.
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 1, 32, 13, TimeSpan.Zero),
            Cx01Watermark.Parse("2026-10-27T21:32:13.0000000+00:00|1294", newYork).Timestamp);
        // A legacy registration seed has no key and was already true UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 15, 58, 9, TimeSpan.Zero),
            Cx01Watermark.Parse("2026-10-27T15:58:09.0000000+00:00|", india).Timestamp);

        var current = new Cx01Watermark(new DateTimeOffset(2026, 10, 27, 16, 2, 13, TimeSpan.Zero), "1294");
        Assert.EndsWith("|1294|utc", current.ToString(), StringComparison.Ordinal);
        Assert.Equal(current, Cx01Watermark.Parse(current.ToString(), india));
        Assert.Equal(current, Cx01Watermark.Parse(current.ToString()));
    }

    [Fact]
    public void A_server_west_of_utc_does_not_put_its_first_local_hours_before_the_registration_watermark()
    {
        using var row = JsonDocument.Parse("""{"DocEntry":1292,"UpdateDate":"2026-09-01","UpdateTime":"00:30:00"}""");
        var registration = new Cx01Watermark(new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.Zero), "");
        var changed = Cx01Watermark.From(row.RootElement, "1292",
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        Assert.True(changed.CompareTo(registration) > 0);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 4, 30, 0, TimeSpan.Zero), changed.Timestamp);
    }

    [Fact]
    public async Task India_server_stamp_and_registration_watermark_share_utc_scale()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        using var row = JsonDocument.Parse("""{"DocEntry":1294,"UpdateDate":"2026-10-27","UpdateTime":"21:32:13"}""");
        var registration = Cx01Watermark.Parse(context.Adapter.WatermarkAt(
            new DateTime(2026, 10, 27, 15, 58, 9, DateTimeKind.Utc)));
        var changed = Cx01Watermark.From(row.RootElement, "1294",
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        Assert.True(changed.CompareTo(registration) > 0);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 16, 2, 13, TimeSpan.Zero), changed.Timestamp);
    }

    [Fact]
    public async Task Migration_hand_over_converts_the_source_local_clock_with_the_connection_time_zone()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        var configured = await context.Settings.GetAsync(context.ConnectionId, CancellationToken.None);
        await context.Settings.SetAsync(context.ConnectionId,
            configured with { ErpServerTimeZone = "Asia/Kolkata" }, CancellationToken.None);

        var watermark = await context.Adapter.StartingWatermarkAsync(context.ConnectionId,
            new DateTime(2026, 10, 27, 21, 32, 13, DateTimeKind.Unspecified), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 16, 2, 13, TimeSpan.Zero),
            Cx01Watermark.Parse(watermark).Timestamp);
    }

    [Fact]
    public async Task Session_carries_both_cookies_relogs_after_expiry_and_retries_throttling()
    {
        await using var simulator = new Cx01Simulator { ExpireAfterAuthenticatedRequests = 2 };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        using (await context.Client.GetAsync(context.ConnectionId, Cx01Protocol.Root, CancellationToken.None)) { }
        simulator.RateLimitNextRequest = true;
        using (await context.Client.GetAsync(context.ConnectionId, Cx01Protocol.Root, CancellationToken.None)) { }

        Assert.Equal(2, simulator.LoginCount);
        Assert.All(simulator.ReceivedCookieHeaders.Where(x => x.Length > 0), cookie =>
        {
            Assert.Contains(Cx01Protocol.SessionCookie + "=", cookie, StringComparison.Ordinal);
            Assert.Contains(Cx01Protocol.RouteCookie + "=", cookie, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Poll_orders_identical_timestamps_pages_and_gets_every_document_by_key()
    {
        await using var simulator = new Cx01Simulator { PageSize = 1 };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 2), CancellationToken.None);

        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, item => Assert.Equal(nameof(DeliveryPosted), item.MessageType));
        Assert.EndsWith("|4102|utc", result.NextWatermark, StringComparison.Ordinal);
        Assert.All(simulator.CaptureReadPaths, path => Assert.DoesNotContain("$select", path,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(simulator.SelectEmptiedSerials);
    }

    [Fact]
    public async Task Production_receipt_accepts_null_base_line_and_links_the_parent_production_order()
    {
        await using var simulator = new Cx01Simulator { ProductionReceiptAvailable = true };
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        var configured = await context.Settings.GetAsync(context.ConnectionId, CancellationToken.None);
        await context.Settings.SetAsync(context.ConnectionId, configured with
        {
            CurrencyMap = new Dictionary<string, string> { ["$"] = "USD" },
            ErpServerTimeZone = "Asia/Kolkata",
        }, CancellationToken.None);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId,
            "production_receipts", null, TimeSpan.FromMinutes(2), 10), CancellationToken.None);

        var accepted = Assert.Single(result.Events);
        using var canonical = JsonDocument.Parse(accepted.CanonicalMessage);
        var payload = canonical.RootElement.GetProperty("payload");
        Assert.Equal("161", payload.GetProperty("production_order_ref").GetProperty("external_id").GetString());
        Assert.Equal("production_order", payload.GetProperty("production_order_ref").GetProperty("type").GetString());
        var line = Assert.Single(payload.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("base_document_ref").GetProperty("line").ValueKind);
        Assert.Equal("FIC-261027-SRV-01", Assert.Single(line.GetProperty("serials").EnumerateArray())
            .GetProperty("value").GetString());
        Assert.Equal("USD", line.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task An_unmapped_currency_parks_only_that_document_and_the_poll_advances()
    {
        await using var simulator = new Cx01Simulator
        {
            ReceiptCancelled = true,
            NonIsoGoodsReceiptCurrency = true,
        };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId,
            "goods_receipts", null, TimeSpan.FromMinutes(2), 10), CancellationToken.None);

        Assert.Single(result.Events);
        var parked = Assert.Single(context.Rejections.ParkedDocuments);
        Assert.Equal("3101", parked.ExternalId);
        Assert.Equal("erp_currency_not_mapped", parked.Reason);
        Assert.EndsWith("|3102|utc", result.NextWatermark, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ranged_reads_page_with_a_persisted_cursor_and_mark_messages_as_backfill()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        var day = new DateOnly(2026, 9, 1);

        var first = await context.Adapter.ReadRangePageAsync(context.ConnectionId, "DeliveryNotes",
            day, day, null, 1, CancellationToken.None);
        var second = await context.Adapter.ReadRangePageAsync(context.ConnectionId, "DeliveryNotes",
            day, day, first.NextCursor, 1, CancellationToken.None);

        Assert.Single(first.Messages);
        Assert.NotNull(first.NextCursor);
        Assert.Single(second.Messages);
        Assert.Null(second.NextCursor);
        Assert.NotEqual(first.Messages[0].Envelope.ErpExternalId,
            second.Messages[0].Envelope.ErpExternalId);
        Assert.All(first.Messages.Concat(second.Messages), message =>
            Assert.Equal("backfill", message.Envelope.CaptureMode.Value));
    }

    [Fact]
    public async Task Product_tree_poll_maps_only_the_production_tree_and_preserves_mixed_lines()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "product_trees",
            null, TimeSpan.FromMinutes(2), 10), CancellationToken.None);

        var accepted = Assert.Single(result.Events);
        Assert.Equal(nameof(BomUpserted), accepted.MessageType);
        using var document = JsonDocument.Parse(accepted.CanonicalMessage);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("FIC-SRV-TWIN", payload.GetProperty("parent_item_external_id").GetString());
        var lines = payload.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(8, lines.Count);
        Assert.Equal(7, lines.Count(line => line.GetProperty("kind").GetString() == "item"));
        Assert.Single(lines, line => line.GetProperty("kind").GetString() == "text");
        Assert.DoesNotContain("FIC-SALES-KIT", accepted.CanonicalMessage, StringComparison.Ordinal);
        Assert.All(simulator.ProductTreeQueries, query => Assert.DoesNotContain("UpdateDate", query,
            StringComparison.Ordinal));
        Assert.Contains(simulator.ProductTreeQueries, query => query.Contains("$filter=TreeType", StringComparison.Ordinal));
        Assert.DoesNotContain(simulator.ProductTreeQueries, query => query.Contains("$select", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_product_tree_shape_maps_every_line_number_description_and_uom()
    {
        using var tree = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Connectors", "CX01", "Fixtures", "tree-2u-real.json")));
        var mapped = Cx01Mapper.Map(new TenantId(Guid.CreateVersion7()), Guid.CreateVersion7(),
            Cx01Protocol.Entities["product_trees"], tree.RootElement,
            new Dictionary<int, IReadOnlyList<CanonicalSerial>>(),
            new Dictionary<int, IReadOnlyList<CanonicalSerial>>(), new HashSet<int>(), "poll",
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        using var canonical = JsonDocument.Parse(mapped.Acceptance.CanonicalMessage);
        var lines = canonical.RootElement.GetProperty("payload").GetProperty("lines").EnumerateArray().ToList();

        Assert.Equal(14, lines.Count);
        Assert.Equal(Enumerable.Range(0, 14), lines.Select(line => line.GetProperty("line_no").GetInt32()));
        Assert.Equal(new[]
        {
            "Kestrel server motherboard", "32-core server processor", "Processor heat sink, 2U",
            "64 GB DDR5 registered memory module", "Quill 3.84 TB NVMe drive", "Quill 16 TB hard drive",
            "8-port RAID controller", "Dual-port 25 GbE network adapter", "Kestrel hot-swap power supply",
            "Hot-swap fan module", "Front bezel with lock", "Rail kit, tool-less",
            "Power cable C13-C14, 2 m", "Labor Hours Production",
        }, lines.Select(line => line.GetProperty("description").GetString()));
        var labour = Assert.Single(lines, line => line.GetProperty("item_external_id").GetString() == "L10001");
        Assert.Equal("item", labour.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, labour.GetProperty("uom").ValueKind);
    }

    [Fact]
    public async Task Product_tree_poll_emits_only_changed_content_and_deletes_only_after_a_complete_read()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        var request = new ConnectorPollRequest(context.TenantId, context.ConnectionId, "product_trees",
            null, TimeSpan.Zero, 1_024);

        var first = await context.Adapter.PollAsync(request, CancellationToken.None);
        var accepted = Assert.Single(first.Events);
        context.BomState.Remember(accepted);
        Assert.Empty((await context.Adapter.PollAsync(request, CancellationToken.None)).Events);

        simulator.ProductionTreeNodeQuantity = 3m;
        var changed = Assert.Single((await context.Adapter.PollAsync(request, CancellationToken.None)).Events);
        context.BomState.Remember(changed);
        simulator.ProductionTreePresent = false;
        var deleted = Assert.Single((await context.Adapter.PollAsync(request, CancellationToken.None)).Events);
        using var deletion = JsonDocument.Parse(deleted.CanonicalMessage);
        Assert.True(deletion.RootElement.GetProperty("payload").GetProperty("deleted").GetBoolean());

        simulator.ProductionTreePresent = true;
        simulator.AdditionalProductionTreePresent = true;
        simulator.FailSecondProductTreePage = true;
        await Assert.ThrowsAsync<Cx01RemoteException>(() => context.Adapter.PollAsync(request,
            CancellationToken.None));
    }

    [Fact]
    public async Task Product_tree_update_date_queries_are_refused_in_the_live_error_shape()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var exception = await Assert.ThrowsAsync<Cx01RemoteException>(() => context.Client.GetAsync(
            context.ConnectionId, Cx01Protocol.Root + "ProductTrees?$select=TreeCode,UpdateDate",
            CancellationToken.None));

        Assert.Equal("-1000", exception.RemoteCode);
        Assert.Equal("Property 'UpdateDate' of 'ProductTree'is invalid", exception.RemoteMessage);
    }

    [Fact]
    public async Task Product_tree_webhook_read_uses_the_same_hash_rule_and_maps_a_gone_tree_as_deleted()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var first = Assert.IsType<Cx01MappedAcceptance>(await context.Adapter.CaptureKeyAsync(
            context.ConnectionId, "ProductTrees", "FIC-SRV-TWIN", CancellationToken.None));
        context.BomState.Remember(first.Acceptance);
        Assert.Null(await context.Adapter.CaptureKeyAsync(context.ConnectionId, "ProductTrees", "FIC-SRV-TWIN",
            CancellationToken.None));

        simulator.ProductionTreeNodeQuantity = 3m;
        var changed = Assert.IsType<Cx01MappedAcceptance>(await context.Adapter.CaptureKeyAsync(
            context.ConnectionId, "ProductTrees", "FIC-SRV-TWIN", CancellationToken.None));
        context.BomState.Remember(changed.Acceptance);
        simulator.ProductionTreePresent = false;
        var deleted = Assert.IsType<Cx01MappedAcceptance>(await context.Adapter.CaptureKeyAsync(
            context.ConnectionId, "ProductTrees", "FIC-SRV-TWIN", CancellationToken.None));
        using var canonical = JsonDocument.Parse(deleted.Acceptance.CanonicalMessage);
        Assert.True(canonical.RootElement.GetProperty("payload").GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task Delivery_serials_survive_the_same_warehouse_return_omission()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        var first = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);
        context.History.Remember(first.Events.Single());
        using (await context.Client.SendJsonAsync(context.ConnectionId, HttpMethod.Post,
            Cx01Protocol.Root + "Returns", new { }, false, CancellationToken.None)) { }

        var second = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            first.NextWatermark, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        using var canonical = JsonDocument.Parse(second.Events.Single().CanonicalMessage);
        var serials = canonical.RootElement.GetProperty("payload").GetProperty("lines")[0]
            .GetProperty("serials");
        Assert.Equal(2, serials.GetArrayLength());
        Assert.Empty(context.Tasks.Raised);
    }

    [Fact]
    public async Task Stock_transfers_poll_by_change_date_only_as_they_carry_no_change_time()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "inventory_transfers",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task First_read_after_full_return_is_partial_and_raises_attribution_work()
    {
        await using var simulator = new Cx01Simulator { SameWarehouseReturnPosted = true };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        using var canonical = JsonDocument.Parse(result.Events.Single().CanonicalMessage);
        Assert.Equal("partial", canonical.RootElement.GetProperty("envelope")
            .GetProperty("attribution_completeness").GetString());
        Assert.Single(context.Tasks.Raised);
    }

    [Fact]
    public async Task Governed_query_recovers_negative_delivery_movement_without_netting_the_return()
    {
        await using var simulator = new Cx01Simulator { SameWarehouseReturnPosted = true };
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        await context.Settings.SetAsync(context.ConnectionId,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340",
                AllowlistedTrackingTables: Cx01Simulator.TrackingTables), CancellationToken.None);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        using var canonical = JsonDocument.Parse(result.Events.Single().CanonicalMessage);
        var serials = canonical.RootElement.GetProperty("payload").GetProperty("lines")[0].GetProperty("serials");
        Assert.Equal("SN-FICTIONAL-001", Assert.Single(serials.EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal("complete", canonical.RootElement.GetProperty("envelope")
            .GetProperty("attribution_completeness").GetString());
    }

    [Fact]
    public async Task Governed_query_code_702_disables_allowlist_and_raises_one_attribution_task()
    {
        await using var simulator = new Cx01Simulator
        {
            SameWarehouseReturnPosted = true,
            TrackingTablesAllowed = false,
        };
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        await context.Settings.SetAsync(context.ConnectionId,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340",
                AllowlistedTrackingTables: Cx01Simulator.TrackingTables), CancellationToken.None);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        Assert.Single(result.Events);
        Assert.Single(context.Tasks.QueryPermissionMessages);
        Assert.Empty((await context.Settings.GetAsync(context.ConnectionId, CancellationToken.None))
            .AllowlistedTrackingTables!);
    }

    [Fact]
    public async Task Cancellation_is_emitted_as_a_canonical_reversal_message()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        using (await context.Client.SendJsonAsync(context.ConnectionId, HttpMethod.Post,
            Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true, CancellationToken.None)) { }

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 3), CancellationToken.None);

        var cancellation = Assert.Single(result.Events, value => value.MessageType == nameof(DocumentCancelled));
        using var canonical = JsonDocument.Parse(cancellation.CanonicalMessage);
        Assert.Equal("4101", canonical.RootElement.GetProperty("payload").GetProperty("document_ref")
            .GetProperty("external_id").GetString());
    }

    [Fact]
    public void Capability_probe_uses_the_metadata_and_company_setting_result()
    {
        var declaration = Cx01Capabilities.For("10.0 FP 2308", true);

        Assert.True(declaration.WebhooksAvailable);
        Assert.Equal(["poll", "webhook"], declaration.CaptureModes);
        Assert.Equal(CapabilitySupport.Manual, declaration.Messages[nameof(CreateReplacementDelivery)]);
        Assert.Equal(CapabilitySupport.Unsupported,
            declaration.Messages[nameof(ChangeStockStatusOrLocation)]);
        Assert.Equal(CapabilitySupport.Native, declaration.Messages[nameof(BomUpserted)]);
    }

    [Theory]
    [InlineData("HMACSHA256")]
    [InlineData("HMACSHA384")]
    [InlineData("HMACSHA512")]
    public void Webhook_signatures_are_verified_for_each_declared_algorithm(string algorithm)
    {
        var body = "fictional notification"u8.ToArray();
        var secret = "fictional secret"u8.ToArray();
        var signature = algorithm switch
        {
            "HMACSHA384" => HMACSHA384.HashData(secret, body),
            "HMACSHA512" => HMACSHA512.HashData(secret, body),
            _ => HMACSHA256.HashData(secret, body),
        };

        Assert.True(Cx01WebhookEndpoints.VerifySignature(body, signature, secret, algorithm));
        signature[0] ^= 1;
        Assert.False(Cx01WebhookEndpoints.VerifySignature(body, signature, secret, algorithm));
    }

    [Fact]
    public void Live_shape_fixture_parses_cloud_events_and_uses_base64_hmac()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Connectors", "CX01", "Fixtures",
            "webhook-notification.json");
        var bytes = File.ReadAllBytes(path);
        var secret = "fixture-test-secret"u8.ToArray();
        var presented = Convert.FromBase64String(Convert.ToBase64String(HMACSHA256.HashData(secret, bytes)));
        Assert.True(Cx01WebhookEndpoints.VerifySignature(bytes, presented, secret, "HMACSHA256"));
        using var payload = JsonDocument.Parse(bytes);
        var connection = Guid.CreateVersion7();
        var hints = payload.RootElement.EnumerateArray()
            .Select(value => Cx01WebhookEndpoints.Hint(value, connection)).ToList();
        Assert.All(hints, hint =>
        {
            Assert.NotNull(hint);
            Assert.Equal("DeliveryNotes", hint.Collection);
            Assert.Equal("Created", hint.Operation);
        });
        Assert.Equal(["4101", "4102"], hints.Select(value => value!.Key));
    }

    [Fact]
    public void Repeated_handshakes_are_stateless_and_keep_returning_the_case_sensitive_challenge_shape()
    {
        var secret = "fixture-test-secret"u8.ToArray();
        foreach (var token in new[] { "9f5d0d6a-fictional-token", "c46bc63b-repeated-token" })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(token);
            var signature = HMACSHA256.HashData(secret, bytes);
            Assert.True(Cx01WebhookEndpoints.VerifySignature(bytes, signature, secret, "HMACSHA256"));
            Assert.Equal("{\"Challenge\":" + JsonSerializer.Serialize(token) + "}",
                Cx01WebhookEndpoints.HandshakeResponse(token));
        }
    }

    [Fact]
    public async Task Remote_business_rejection_preserves_message_and_is_not_retried()
    {
        await using var simulator = new Cx01Simulator { RejectNextPatchWith = HttpStatusCode.BadRequest };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var exception = await Assert.ThrowsAsync<Cx01RemoteException>(() => context.Client.SendJsonAsync(
            context.ConnectionId, HttpMethod.Patch, Cx01Protocol.Root + "Returns(5101)",
            new { U_FICTIONAL_REASON = "" }, true, CancellationToken.None));

        Assert.Equal("-5002", exception.RemoteCode);
        Assert.Equal("(custom) Return needs a reason code", exception.RemoteMessage);
        Assert.True(exception.IsBusinessRejection);
        Assert.True(simulator.LastHandlerFailure is null, simulator.LastHandlerFailure);
        Assert.Equal(1, simulator.PatchAttempts);
    }

    [Fact]
    public async Task Service_unavailable_is_retried_for_an_idempotent_patch()
    {
        await using var simulator = new Cx01Simulator { ServiceUnavailableResponses = 1 };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        JsonDocument? response;
        try
        {
            response = await context.Client.SendJsonAsync(context.ConnectionId, HttpMethod.Patch,
                Cx01Protocol.Root + "Returns(5101)", new { U_FICTIONAL_REASON = "reason" }, true, timeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException($"Retry stalled after {simulator.RequestCount} requests; "
                + $"remaining simulated failures: {simulator.ServiceUnavailableResponses}.", exception);
        }
        catch (Cx01RemoteException exception) when (simulator.LastHandlerFailure is not null)
        {
            throw new InvalidOperationException(simulator.LastHandlerFailure, exception);
        }
        using (response) { }

        Assert.Equal(1, simulator.PatchAttempts);
        Assert.Equal(0, simulator.ServiceUnavailableResponses);
    }

    [Fact]
    public async Task Missing_key_is_classified_as_record_gone_with_remote_message()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var exception = await Assert.ThrowsAsync<Cx01RemoteException>(() => context.Client.GetAsync(
            context.ConnectionId, Cx01Protocol.Root + "DeliveryNotes(999999)", CancellationToken.None));

        Assert.True(exception.IsRecordGone);
        Assert.Equal("-2028", exception.RemoteCode);
        Assert.Contains("does not exist", exception.RemoteMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbound_business_rejection_is_parked_and_not_retried()
    {
        await using var simulator = new Cx01Simulator { RejectNextDeliveryReadWithBusinessError = true };
        await using var context = await Cx01TestContext.CreateAsync(simulator);

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        Assert.Empty(result.Events);
        var rejection = Assert.Single(context.Rejections.Parked);
        Assert.Equal("24001", rejection.Exception.RemoteCode);
        Assert.Equal("(custom) Read blocked by a fictional validation", rejection.Exception.RemoteMessage);
        Assert.Equal(3, simulator.RequestCount); // login, key list, one rejected key read.
    }
}

internal sealed class Cx01TestContext : IAsyncDisposable
{
    private readonly Cx01Transport transport;
    private readonly IntegrationDbContext db;

    private Cx01TestContext(TenantId tenantId, Guid connectionId, Cx01Client client, Cx01Settings settings,
        Cx01Adapter adapter, RememberingSerialHistory history, RememberingBomSyncState bomState,
        CollectingAttributionTasks tasks,
        CollectingInboundRejections rejections,
        Cx01Transport transport, IntegrationDbContext db)
    {
        TenantId = tenantId;
        ConnectionId = connectionId;
        Client = client;
        Settings = settings;
        Adapter = adapter;
        History = history;
        BomState = bomState;
        Tasks = tasks;
        Rejections = rejections;
        this.transport = transport;
        this.db = db;
    }

    public TenantId TenantId { get; }
    public Guid ConnectionId { get; }
    public Cx01Client Client { get; }
    public Cx01Settings Settings { get; }
    public Cx01Adapter Adapter { get; }
    public RememberingSerialHistory History { get; }
    public RememberingBomSyncState BomState { get; }
    public CollectingAttributionTasks Tasks { get; }
    public CollectingInboundRejections Rejections { get; }

    public static async Task<Cx01TestContext> CreateAsync(Cx01Simulator simulator)
    {
        var tenantId = new TenantId(Guid.CreateVersion7());
        var connectionId = Guid.CreateVersion7();
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var clock = new TestClock(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var tenantSettings = new TestTenantSettings();
        var settings = new Cx01Settings(tenantSettings);
        await settings.SetAsync(connectionId, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308"),
            CancellationToken.None);
        var dataKeys = new InMemoryTenantDataKeyStore();
        var keyProvider = new LocalKeyEncryptionKeyProvider(new LocalKeyEncryptionOptions
        {
            CurrentKey = new("cx01-tests", "1", Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
        });
        var protector = new ConnectionCredentialProtector(new(dataKeys, keyProvider, clock), clock);
        var credentialStore = new InMemoryConnectionCredentialStore();
        var secret = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        var credential = await protector.EncryptAsync(tenantId, connectionId, Cx01Client.CredentialPurpose,
            secret, null, clock.UtcNow.AddDays(30), CancellationToken.None);
        await credentialStore.SaveAsync(credential, CancellationToken.None);
        var profileStore = new InMemoryConnectionThrottleProfileStore();
        var profiles = new ConnectionThrottleProfiles(profileStore, clock);
        await profiles.OnboardAsync(new(tenantId, connectionId), Cx01Capabilities.For("10.0 FP 2308", false).Throttle,
            CancellationToken.None);
        var delay = new AdvancingThrottleDelay(clock);
        var isolation = new ConnectionWorkerIsolation();
        var workers = new ConnectionThrottleWorkerRegistry(isolation, new InMemoryConnectionDailyBudgetStore(),
            new NullConnectionCallBudgetAlertPublisher(), clock, delay);
        var transport = new Cx01Transport(profiles, workers, clock, delay, new RandomConnectionRetryJitter());
        var sessions = new Cx01SessionRegistry();
        var client = new Cx01Client(tenant, clock, settings, credentialStore, protector, sessions, transport);
        var options = IntegrationDbContext.DesignTimeOptions(
            "Server=localhost;Database=not_opened;Encrypt=True;TrustServerCertificate=True");
        var db = new IntegrationDbContext(options, tenant);
        var history = new RememberingSerialHistory();
        var bomState = new RememberingBomSyncState();
        var tasks = new CollectingAttributionTasks();
        var rejections = new CollectingInboundRejections();
        var adapter = new Cx01Adapter(tenant, clock, db,
            new GatewayReferenceStore(db, tenant, clock, new NoPersonalDataErasures()),
            bomState, settings, client, history, new NoCapturedDocuments(), tasks, new NoOwnWrites(), rejections,
            new EmptySurveyReader(), new EmptyTrackingPermissions(), new FixedTenantZone("UTC"));
        return new(tenantId, connectionId, client, settings, adapter, history, bomState, tasks, rejections,
            transport, db);
    }

    public ValueTask DisposeAsync()
    {
        transport.Dispose();
        db.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RememberingBomSyncState : ICx01BomSyncStateReader
{
    private readonly Dictionary<string, Cx01BomSyncState> states = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, Cx01BomSyncState>> ReadAsync(Guid connectionId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, Cx01BomSyncState>>(states);

    public void Set(string externalId, string contentHash, bool deleted = false) =>
        states[externalId] = new(contentHash, deleted);

    public void Remember(Yojana.Integration.Inbox.InboundAcceptance acceptance)
    {
        using var document = JsonDocument.Parse(acceptance.CanonicalMessage);
        var payload = document.RootElement.GetProperty("payload");
        var bom = payload.Deserialize<BomUpserted>(IntegrationContracts.Json)!;
        states[bom.ParentItemExternalId] = new(BomContentHash.Compute(bom.Lines), bom.Deleted);
    }
}

internal sealed class NoPersonalDataErasures : IPersonalDataErasureReadPort
{
    public Task<ErasedParty?> ErasedPartyAsync(Guid partyRef, CancellationToken cancellationToken) =>
        Task.FromResult<ErasedParty?>(null);
}

internal sealed class NoCapturedDocuments : ICx01DocumentCaptureStore
{
    public Task<bool> WasCapturedAsync(Guid connectionId, string entity, string externalId,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class RememberingSerialHistory : ICx01SerialHistory
{
    private readonly Dictionary<int, IReadOnlyList<CanonicalSerial>> serials = [];

    public void Remember(Yojana.Integration.Inbox.InboundAcceptance acceptance)
    {
        using var document = JsonDocument.Parse(acceptance.CanonicalMessage);
        foreach (var line in document.RootElement.GetProperty("payload").GetProperty("lines").EnumerateArray())
            serials[line.GetProperty("line_no").GetInt32()] =
                line.GetProperty("serials").Deserialize<List<CanonicalSerial>>(IntegrationContracts.Json) ?? [];
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>>> ForDocumentAsync(Guid connectionId,
        string entity, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>>>(serials);
}

internal sealed class CollectingAttributionTasks : ICx01AttributionTaskSink
{
    public List<(string Entity, string ExternalId)> Raised { get; } = [];
    public List<string> QueryPermissionMessages { get; } = [];

    public Task RaiseAsync(Guid connectionId, string entity, string externalId,
        IReadOnlyList<Cx01MissingSerialLine> lines, CancellationToken cancellationToken)
    {
        Raised.Add((entity, externalId));
        return Task.CompletedTask;
    }

    public Task RaiseQueryPermissionAsync(Guid connectionId, string remoteMessage,
        CancellationToken cancellationToken)
    {
        QueryPermissionMessages.Add(remoteMessage);
        return Task.CompletedTask;
    }
}

internal sealed class NoOwnWrites : ICx01OwnWriteStore
{
    public Task<bool> MatchesAsync(Guid connectionId, string externalId, string changeMarker,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class CollectingInboundRejections : ICx01InboundRejectionSink
{
    public List<(string Entity, string ExternalId, Cx01RemoteException Exception)> Parked { get; } = [];

    public Task ParkAsync(Guid connectionId, string entity, string externalId, Cx01RemoteException exception,
        CancellationToken cancellationToken)
    {
        Parked.Add((entity, externalId, exception));
        return Task.CompletedTask;
    }

    public List<(string Entity, string ExternalId, string Reason)> ParkedDocuments { get; } = [];

    public Task ParkDocumentAsync(Guid connectionId, string entity, string messageType, string externalId,
        JsonElement raw, string reasonCode, string failure, CancellationToken cancellationToken)
    {
        ParkedDocuments.Add((entity, externalId, reasonCode));
        return Task.CompletedTask;
    }
}

internal sealed class FixedTenantZone(string id) : Yojana.Serialisation.Contracts.ITenantTimeZoneReadPort
{
    public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(id);
}

internal sealed class EmptySurveyReader : ICx01SurveyReader
{
    public Task<IReadOnlyList<string>> MandatoryWithoutDefaultFieldsAsync(Guid connectionId, string table,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class EmptyTrackingPermissions : ICx01TrackingPermissionSink
{
    public Task RecordDeniedAsync(Guid connectionId, string remoteMessage, CancellationToken ct) => Task.CompletedTask;
}
