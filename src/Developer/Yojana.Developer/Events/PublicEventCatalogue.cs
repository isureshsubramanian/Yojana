using System.Text.Json;
using Yojana.Developer.Contracts;

namespace Yojana.Developer.Events;

/// <summary>Builds the administrator-facing catalogue from the same immutable schemas used for delivery.</summary>
public static class PublicEventCatalogue
{
    private static readonly DateTime SampleAt = new(2026, 9, 1, 9, 14, 3, DateTimeKind.Utc);
    private static readonly Guid EventId = Guid.Parse("0193f6a1-7c2e-7b44-9d10-5e2a8c1f0b37");
    private static readonly Guid RecordId = Guid.Parse("0193b2c4-90ab-7c11-8de1-a47e90045170");

    public static DeveloperCatalogue Read() => new(PublicEventTypes.All.Select(Entry).ToList(),
        PublicApiScopes.All);

    private static PublicEventCatalogueEntry Entry(string type)
    {
        using var schema = JsonDocument.Parse(PublicEventSchemas.Read(type));
        return new(type, PublicEventTypes.CurrentVersion, schema.RootElement.Clone(), Sample(type));
    }

    private static JsonElement Sample(string type)
    {
        if (type == PublicEventTypes.EndpointVerify)
            return JsonSerializer.SerializeToElement(new
            {
                type,
                challenge = "fictional-verification-challenge-0001",
            }, PublicEventTranslator.Json);

        var kind = type.Split('.')[0];
        var reference = Reference(kind);
        return JsonSerializer.SerializeToElement(new
        {
            id = EventId,
            type,
            version = PublicEventTypes.CurrentVersion,
            tenant = "t_fictional_asterra_compute_works",
            occurredAt = SampleAt,
            recordedAt = SampleAt.AddSeconds(1),
            record = new { kind, id = RecordId, reference },
            sequence = 42,
            data = new Dictionary<string, object?>
            {
                ["itemFamily"] = "servers",
                ["serviceLevel"] = "next_business_day",
                ["test"] = true,
            },
            links = new Dictionary<string, string>
            {
                ["self"] = $"/api/public/v1/{Resource(kind)}/{RecordId:D}",
            },
            test = true,
        }, PublicEventTranslator.Json);
    }

    private static string Reference(string kind) => kind switch
    {
        "claim" => "CLM-2026-1187",
        "rma" => "RMA-2026-0412",
        "replacement" => "RPL-2026-0098",
        "certificate" => "CERT-2026-4517",
        "campaign" => "CMP-SERVER-NBD-2026",
        "recovery_claim" => "REC-2026-0271",
        "import" => "IMP-2026-0094",
        "bulk" => "BULK-2026-0031",
        _ => "SRV-R760-004517",
    };

    private static string Resource(string kind) => kind switch
    {
        "cover" => "cover",
        "rma" => "rmas",
        "recovery_claim" => "recovery-claims",
        _ => kind + "s",
    };
}
