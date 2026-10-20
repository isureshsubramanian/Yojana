using System.Net;

namespace Yojana.Relay.Contracts;

public static class RelayWebhookUrl
{
    public static void Validate(string bind)
    {
        if (!Uri.TryCreate(bind, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !bind.EndsWith('/') || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !IPAddress.TryParse(uri.DnsSafeHost, out var ip))
            throw new InvalidOperationException("The webhook listener needs an explicit IP bind and trailing slash.");
        var bytes = ip.GetAddressBytes();
        var lan = IPAddress.IsLoopback(ip) || bytes.Length == 4 && (bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168)
            || bytes.Length == 16 && ((bytes[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal);
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || uri.Scheme == "http" && !lan)
            throw new InvalidOperationException("HTTP webhook listeners must bind a LAN or loopback address.");
    }
}

public static class RelayProductUrl
{
    public static Uri Resolve(Uri product, string path) => new(new Uri(product.AbsoluteUri.TrimEnd('/') + "/"), path.TrimStart('/'));
}
