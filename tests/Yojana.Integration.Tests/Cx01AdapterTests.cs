using System.Text.Json;
using System.Security.Cryptography;
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
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class Cx01AdapterTests
{
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
        Assert.EndsWith("|4102", result.NextWatermark, StringComparison.Ordinal);
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
    public async Task Cancellation_is_emitted_as_a_canonical_reversal_message()
    {
        await using var simulator = new Cx01Simulator();
        await using var context = await Cx01TestContext.CreateAsync(simulator);
        using (await context.Client.SendJsonAsync(context.ConnectionId, HttpMethod.Post,
            Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true, CancellationToken.None)) { }

        var result = await context.Adapter.PollAsync(new(context.TenantId, context.ConnectionId, "deliveries",
            null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);

        Assert.Equal(nameof(DocumentCancelled), result.Events.Single().MessageType);
    }

    [Fact]
    public void Capability_probe_keeps_the_first_round_release_polling_only()
    {
        var declaration = Cx01Capabilities.For("10.0 FP 2308", true);

        Assert.False(declaration.WebhooksAvailable);
        Assert.Equal(["poll"], declaration.CaptureModes);
        Assert.Equal(CapabilitySupport.Manual, declaration.Messages[nameof(CreateReplacementDelivery)]);
        Assert.Equal(CapabilitySupport.Unsupported,
            declaration.Messages[nameof(ChangeStockStatusOrLocation)]);
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
}

internal sealed class Cx01TestContext : IAsyncDisposable
{
    private readonly Cx01Transport transport;
    private readonly IntegrationDbContext db;

    private Cx01TestContext(TenantId tenantId, Guid connectionId, Cx01Client client,
        Cx01Adapter adapter, RememberingSerialHistory history, CollectingAttributionTasks tasks,
        Cx01Transport transport, IntegrationDbContext db)
    {
        TenantId = tenantId;
        ConnectionId = connectionId;
        Client = client;
        Adapter = adapter;
        History = history;
        Tasks = tasks;
        this.transport = transport;
        this.db = db;
    }

    public TenantId TenantId { get; }
    public Guid ConnectionId { get; }
    public Cx01Client Client { get; }
    public Cx01Adapter Adapter { get; }
    public RememberingSerialHistory History { get; }
    public CollectingAttributionTasks Tasks { get; }

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
        var delay = new SystemConnectionThrottleDelay();
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
        var tasks = new CollectingAttributionTasks();
        var adapter = new Cx01Adapter(tenant, clock, db, new GatewayReferenceStore(db, tenant, clock),
            settings, client, history, tasks);
        return new(tenantId, connectionId, client, adapter, history, tasks, transport, db);
    }

    public ValueTask DisposeAsync()
    {
        transport.Dispose();
        db.Dispose();
        return ValueTask.CompletedTask;
    }
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

    public Task RaiseAsync(Guid connectionId, string entity, string externalId,
        IReadOnlyList<Cx01MissingSerialLine> lines, CancellationToken cancellationToken)
    {
        Raised.Add((entity, externalId));
        return Task.CompletedTask;
    }
}
