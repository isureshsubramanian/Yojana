using System.Security.Cryptography;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;

namespace Yojana.Integration.Connectors.CX01.Testing;

// Protocol-shaped fixtures stay at the connector boundary, including when linked into database tests.
internal static class Cx01EdgeScenarios
{
    public static Cx01WebhookHint Delivery(Guid connection, bool cancellation = false) =>
        new(connection, "DeliveryNotes", "Created", cancellation ? "4103" : "4101", cancellation ? "fic-cancel" : "fic-delivery");

    public static async Task<InboundAcceptance> ItemAsync(Cx01Adapter adapter, Guid connection) =>
        (await adapter.CaptureKeyAsync(connection, "Items", "HW-EDGE-100", default))!.Acceptance;

    public static async Task<InboundAcceptance> OpeningReceiptAsync(Cx01Adapter adapter, Guid connection)
    {
        var capture = (await adapter.CaptureKeyAsync(connection, "DeliveryNotes", "4101", default))!.Acceptance;
        var delivery = JsonSerializer.Deserialize<CanonicalMessage<DeliveryPosted>>(capture.CanonicalMessage, IntegrationContracts.Json)!;
        var envelope = capture.Envelope with { MessageId = Guid.CreateVersion7(), ErpEntity = "receipt", ErpExternalId = "FIC-OPENING", OccurredAt = delivery.Envelope.OccurredAt.AddMinutes(-1) };
        var receipt = new GoodsReceiptPosted(new("receipt", "FIC-OPENING", new(2026, 9, 1), "FIC-OPENING"), "FIC-SUPPLIER", delivery.Payload.Lines);
        return new(envelope, nameof(GoodsReceiptPosted), JsonSerializer.Serialize(new CanonicalMessage<GoodsReceiptPosted>(envelope, nameof(GoodsReceiptPosted), receipt), IntegrationContracts.Json), "{\"fictional_opening\":true}");
    }

    public static async Task CancelAsync(Cx01Client client, Guid connection)
    {
        using var response = await client.SendJsonAsync(connection, HttpMethod.Post, Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true, default);
    }

    public static HttpRequestMessage Notification(Guid connection, string tenant, byte[] secret, bool cancellation = false)
    {
        var hint = Delivery(connection, cancellation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = hint.EventId, source = "/default/sap.b1/FICTIONAL_EDGE", type = "sap.b1.DeliveryNotes.Created.v1", data = new { DocEntry = hint.Key } });
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integration/connections/{connection}/notifications/cx01") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, tenant);
        request.Headers.Add(Cx01Protocol.SignatureHeader, Convert.ToBase64String(HMACSHA256.HashData(secret, bytes)));
        return request;
    }

    public static void AddCard(Cx01Simulator simulator) => simulator.EquipmentCards.Add(new()
    {
        ["EquipmentCardNum"] = 71, ["InternalSerialNum"] = "FICTIONAL-SN-1001", ["ItemCode"] = "HW-EDGE-100",
        ["CustomerCode"] = "C-FICTIONAL-01", ["StatusOfSerialNumber"] = "sns_Active", ["ContractCode"] = null,
        ["ContractEndDate"] = null, ["DeliveryCode"] = "FIC-DELIVERY", ["InvoiceCode"] = null,
    });
    public static void EditOwnedField(Cx01Simulator simulator) => simulator.EquipmentCards.Single()["CustomerCode"] = "FIC-OTHER";
    public static void RejectReplacement(Cx01Simulator simulator) => simulator.RejectNextDocumentCollection = Cx01DocumentScenarios.Collection("delivery");
}
