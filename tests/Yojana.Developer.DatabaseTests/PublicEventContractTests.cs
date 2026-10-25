using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yojana.Developer.Contracts;
using Yojana.Developer.Delivery;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.DatabaseTests;

public sealed class PublicEventContractTests
{
    private static readonly IReadOnlyDictionary<string, string> PinnedSchemas =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bulk.completed"] = "80fb9f2ed01d24fdf697d586f1ff23d4d9e306d9b70eb08368a493cb07d91f92",
            ["campaign.ended"] = "f40cda5caadb0684cf147b811da02dc1c9e3d1f6a6b2048f6d8398667758d793",
            ["campaign.started"] = "ba16725fb2081c265b90886f0d93b456e865e41bc37d5465dc1b2738b7684272",
            ["certificate.issued"] = "b37c1b67c05d4dd84b70421d629f3f92ff30da883e16e0d22732a555835ea9c6",
            ["certificate.superseded"] = "0549ff93a85fd2633aff01e1aed0fe6dc4690bfd775336b5b090518929bbe5e1",
            ["claim.adjudicated"] = "020a1e3364a01d2627dfefc02ee30e7f241be0930eb81caaedbe049b12985e20",
            ["claim.submitted"] = "91ed176145776fedace4389e3db43c04e6eb03513f6f0b964b15981ad347622c",
            ["cover.amended"] = "0b660a805655b2b8ee163308081d0860da5d49ec6e96a7ff6e7182880af2ca07",
            ["cover.closed"] = "7c830d1bdf77be433bb1c16881dfa7cd9c9809e5670209baba15030d213b8a05",
            ["cover.created"] = "74920ea7fdb5a3e21fdc399070911ef39a9c65b9e29792b0789f29ebd70b0c6f",
            ["cover.transferred"] = "cb6751fdbb1a4f7ced2df9cb1bbb4e513f9998dfbfe93f6842431088fb9c68e4",
            ["endpoint.verify"] = "adfa9b5259738014eb0df6fb44f146b1f379d300e74c63ddc0f467aa0544ec08",
            ["import.completed"] = "797aec0a22fa71cd8b878627d7b1852c4bed878abccc7692b5dcd2fecda672ba",
            ["recovery_claim.answered"] = "e69f6b867c0e4b5e74c4609fcd89f269435dc20181da65f06ecdf4b68064d846",
            ["recovery_claim.raised"] = "a5489fadc8b63254bfc072b714fa1a3a402c51d51741fa6330a022e42b64ca8a",
            ["refurbishment.released"] = "5006b402635c3fa07ce9864585042d2712c7459e15f70a297b57e973ca33bf39",
            ["replacement.returned"] = "23b18db8930662f5921c987a675ce1723e97c6f22456430448b205a54aa193be",
            ["replacement.shipped"] = "bb799097012ad352c63e0f23999d9325f5929b69c7d2047455b0d06b1543d86d",
            ["rma.stage_changed"] = "ac0de2000db15e33551ea159f5007d02c52da9e2a25fff55b13d135c85fcd581",
            ["unit.created"] = "89ac03244b076a4c4f3969e10e141fc333981ca3e1c4062cc270de6375155ef1",
            ["unit.custody_changed"] = "c423e71920e5274773c29d134c03debe892bd994329e9fc1cac5c45b70c60d61",
            ["unit.identifier_corrected"] = "dc06c18c972b90c63c0064fb0d232cebfd37f6cfdc3d8e796e44178ce0d6a9c5",
            ["unit.ownership_changed"] = "423340379666c6cf05157fde6f1a0f3ba9324ce3a2df61f0684cebbe29221376",
            ["unit.shipped"] = "20182c247dda255e5405254bb5e9f83917e6c56a7cf8a2404211f1f9d9ec216e",
            ["unit.voided"] = "0f87b68bf1207f54988948222e4c38c073a18110d1a3b02f41632c1b07391be1",
        };

    [Fact]
    public void Every_public_schema_is_present_and_pinned()
    {
        Assert.Equal(PublicEventTypes.All.Order(), PinnedSchemas.Keys.Order());
        foreach (var (type, expected) in PinnedSchemas)
        {
            var schema = Yojana.Developer.Contracts.PublicEventSchemas.Read(type);
            using var document = JsonDocument.Parse(schema);
            Assert.Equal(type, document.RootElement.GetProperty("title").GetString());
            Assert.Equal(type, document.RootElement.GetProperty("properties").GetProperty("type")
                .GetProperty("const").GetString());
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema))).ToLowerInvariant();
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(ClaimSubmitted.Schema, ClaimSubmitted.Version)]
    [InlineData(ReplacementReturned.Schema, ReplacementReturned.Version)]
    [InlineData(CampaignStarted.Schema, CampaignStarted.Version)]
    [InlineData(CampaignEnded.Schema, CampaignEnded.Version)]
    [InlineData(BulkOperationCompleted.Schema, BulkOperationCompleted.Version)]
    public void New_internal_event_schema_is_embedded(string schema, string version)
    {
        using var document = JsonDocument.Parse(Yojana.Serialisation.Contracts.PublishedEventSchemas.Read(schema, version));
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Signer_matches_the_published_vector_and_keeps_rotation_secrets_in_order()
    {
        const string current = "yjw_7Jq2c9VYbX1mT4rK0pLs8dWfE3nHuQaZgRyCvBoMiNk";
        const string previous = "yjw_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const long now = 1_794_044_832;
        var body = Encoding.UTF8.GetBytes("{\"id\":\"0193f6a1-7c2e-7b44-9d10-5e2a8c1f0b37\",\"type\":\"unit.shipped\",\"version\":\"1\"}");
        var currentValue = "d3efb7f84924fffb323a622ec14c7cdf5ef7ccd9a5311bb05caf981f16f60afc";

        Assert.Equal($"t={now},v1={currentValue}", WebhookSigner.Header(now, body, [current]));
        var header = WebhookSigner.Header(now, body, [current, previous]);
        Assert.Equal([currentValue, Signature(previous, now, body)], header.Split(',').Skip(1)
            .Select(field => field[3..]).ToArray());
        Assert.True(Verify(header, body, current, now));
        Assert.True(Verify(header, body, previous, now + 299));
        Assert.False(Verify(header, body, current, now + 301));
    }

    public static TheoryData<string> BlockedAddresses => new()
    {
        "0.1.2.3", "10.0.0.1", "100.64.0.1", "100.127.255.255", "127.0.0.1",
        "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.0.0.1", "192.0.0.255",
        "192.168.1.1", "198.18.0.1", "198.19.255.255", "224.0.0.1", "239.255.255.255",
        "240.0.0.1", "255.255.255.255", "::", "::1", "fc00::1", "fdff::1", "fe80::1",
        "fec0::1", "feff::1", "ff02::1", "::ffff:127.0.0.1", "::ffff:169.254.169.254",
        "::ffff:192.168.1.1", "::10.0.0.1", "::192.0.0.1", "64:ff9b::a00:1",
        "64:ff9b:1:a00:0:100::", "2002:0a00:0001::",
    };

    [Theory]
    [MemberData(nameof(BlockedAddresses))]
    public void Request_forgery_guard_blocks_private_and_special_ranges(string text) =>
        Assert.True(WebhookTargetGuard.IsPrivate(IPAddress.Parse(text)));

    [Theory]
    [InlineData("64:ff9b::808:808")]
    [InlineData("64:ff9b:1:808:0:808::")]
    [InlineData("2002:0808:0808::")]
    public void Request_forgery_guard_allows_embedded_public_addresses(string text) =>
        Assert.False(WebhookTargetGuard.IsPrivate(IPAddress.Parse(text)));

    [Fact]
    public async Task Request_forgery_guard_rechecks_dns_and_rejects_a_private_delivery_address()
    {
        var resolver = new ChangingResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.8"));
        var guard = new WebhookTargetGuard(resolver, new(new EmptySettings()),
            Options.Create(new DeveloperWebhookOptions()));
        Assert.Equal(IPAddress.Parse("93.184.216.34"),
            (await guard.VetAsync("https://events.fictional.example/hook", default)).Address);
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            guard.VetAsync("https://events.fictional.example/hook", default));
        Assert.Equal("webhook_private_target_needs_relay", refused.Rule);
    }

    [Theory]
    [InlineData(1, 29.999)]
    [InlineData(2, 59.999)]
    [InlineData(20, 3599.999)]
    public void Retry_schedule_uses_full_jitter_inside_the_exponential_cap(int attempt, double maximum)
    {
        Assert.Equal(TimeSpan.Zero, WebhookRetrySchedule.After(attempt, 0));
        Assert.InRange(WebhookRetrySchedule.After(attempt, 0.999999).TotalSeconds, 0, maximum + 0.001);
    }

    private static bool Verify(string header, byte[] body, string secret, long now)
    {
        var fields = header.Split(',');
        var timestamp = long.Parse(fields[0][2..], System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(now - timestamp) > 300) return false;
        var expected = Signature(secret, timestamp, body);
        return fields.Skip(1).Where(field => field.StartsWith("v1=", StringComparison.Ordinal))
            .Select(field => field[3..]).Any(candidate => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(expected)));
    }

    private static string Signature(string secret, long timestamp, byte[] body)
    {
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed))
            .ToLowerInvariant();
    }

    private sealed class ChangingResolver(params IPAddress[] addresses) : IWebhookDnsResolver
    {
        private int index;
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([addresses[Math.Min(index++, addresses.Length - 1)]]);
    }

    private sealed class EmptySettings : ITenantSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
