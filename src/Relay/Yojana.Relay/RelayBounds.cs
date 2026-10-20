namespace Yojana.Relay;

public static class RelayBounds
{
    public const int BodyLimit = Yojana.Relay.Contracts.RelayLimits.BodyBytes;
    public const int WireLimit = Yojana.Relay.Contracts.RelayLimits.WireBytes;

    public static async Task<byte[]> ReadAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var target = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return target.ToArray();
            if (target.Length + read > limit) throw new InvalidDataException("relay_body_too_large");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
