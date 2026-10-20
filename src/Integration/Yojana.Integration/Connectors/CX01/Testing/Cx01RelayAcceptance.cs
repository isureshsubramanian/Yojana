using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Relay;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01RelayAcceptance
{
    internal static async Task AssertOutboundAsync(IServiceProvider services, Guid connection, Cx01Client client, Cx01Simulator simulator)
    {
        using var result = await client.SendJsonAsync(connection, HttpMethod.Patch,
            Cx01Protocol.Root + "Returns(5101)", new { U_RmaRef = "FICTIONAL-RMA-RELAY" }, true, CancellationToken.None);
        Assert.NotNull(simulator.LastPatchBody);
    }

    internal static async Task AssertWebhooksAsync(IServiceProvider services, TenantId tenant, Guid connection,
        RelayAgentConfiguration configuration, HttpClient http)
    {
        var settings = services.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, (await settings.GetAsync(connection, CancellationToken.None)) with
            { RelayWebhookUrl = configuration.WebhookBind }, CancellationToken.None);
        var secret = Encoding.UTF8.GetBytes("fictional-webhook-signing-key");
        var protector = services.GetRequiredService<ConnectionCredentialProtector>();
        var stored = await protector.EncryptAsync(tenant, connection, Cx01WebhookEndpoints.SigningCredentialPurpose,
            secret, null, new DateTime(2027, 9, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await services.GetRequiredService<IConnectionCredentialStore>().SaveAsync(stored, CancellationToken.None);
        await services.GetRequiredService<Cx01WebhookMaintenance>().SubscribeAsync(connection,
            new("https://product.example/callback"), CancellationToken.None);
        var token = "fictional-handshake-challenge";
        using var handshake = new HttpRequestMessage(HttpMethod.Get, configuration.WebhookBind);
        handshake.Headers.Add(Cx01Protocol.HandshakeTokenHeader, token);
        handshake.Headers.Add(Cx01Protocol.SignatureHeader, Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(token))));
        using var response = await http.SendAsync(handshake);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Cx01WebhookEndpoints.HandshakeResponse(token), await response.Content.ReadAsStringAsync());
        var bytes = Encoding.UTF8.GetBytes("{\"source\":\"/default/sap.b1/FICTIONAL_DEMO\",\"id\":\"relay-event-1\",\"type\":\"Created\",\"data\":{\"BusinessObject\":\"DeliveryNotes\",\"DocEntry\":4101}}");
        using var delivery = new HttpRequestMessage(HttpMethod.Post, configuration.WebhookBind) { Content = new ByteArrayContent(bytes) };
        delivery.Headers.Add(Cx01Protocol.SignatureHeader, Convert.ToBase64String(HMACSHA256.HashData(secret, bytes)));
        using var accepted = await http.SendAsync(delivery);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var altered = new HttpRequestMessage(HttpMethod.Post, configuration.WebhookBind) { Content = new ByteArrayContent(bytes.Concat(new byte[] { 32 }).ToArray()) };
        altered.Headers.Add(Cx01Protocol.SignatureHeader, Convert.ToBase64String(HMACSHA256.HashData(secret, bytes)));
        using var rejected = await http.SendAsync(altered);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public void Declared_allow_list_covers_protocol_collections_and_session_boundaries()
    {
        var settings = new Cx01ConnectionSettings(new("http://127.0.0.1:9000"), "FICTIONAL_DEMO", "test", Transport: "site_relay");
        var declared = new RelayAllowList(Cx01RelayProfile.Declaration(settings));
        foreach (var entity in Cx01Protocol.Entities.Values)
        {
            Assert.True(declared.Allows("GET", Cx01Protocol.Collection(entity), ""));
            Assert.True(declared.Allows("GET", Cx01Protocol.ByKey(entity, "123"), "?query=value"));
        }
        Assert.True(declared.Allows("GET", Cx01Protocol.Metadata, ""));
        Assert.False(declared.Allows("POST", Cx01Protocol.Login, ""));
        Assert.False(declared.Allows("POST", Cx01Protocol.Logout, ""));
        var cloud = new RelayAllowList(Cx01RelayProfile.Declaration(settings with { RelayCredentials = "cloud" }));
        Assert.True(cloud.Allows("POST", Cx01Protocol.Login, ""));
        Assert.False(cloud.Allows("DELETE", Cx01Protocol.Root + "Items('example')", ""));
    }
}
