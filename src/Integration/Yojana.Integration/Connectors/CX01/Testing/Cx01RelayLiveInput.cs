using Yojana.Relay.Contracts;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal static class Cx01RelayLiveInput
{
    public static (Uri Service, RelaySecrets Secrets) Read()
    {
        var user = Environment.GetEnvironmentVariable("YOJANA_CX01_USER")!;
        var parts = user.Split('\\', 2);
        var company = Environment.GetEnvironmentVariable("YOJANA_CX01_COMPANY")
            ?? (parts.Length == 2 ? parts[0] : throw new InvalidOperationException("Supply the company separately or in the user name."));
        return (new(Environment.GetEnvironmentVariable("YOJANA_CX01_URL")!),
            new("", company, parts[^1], Environment.GetEnvironmentVariable("YOJANA_CX01_PASSWORD")!));
    }

    public static async Task ReadOneAsync(Cx01Client client, Guid connection)
    {
        using var document = await client.GetAsync(connection, Cx01Protocol.Root + "Items?$top=1", CancellationToken.None);
        if (!document.RootElement.TryGetProperty("value", out _)) throw new InvalidOperationException("The read did not return a collection.");
    }
}
