using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Webhooks;

public interface IWebhookDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class WebhookDnsResolver : IWebhookDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed record VettedWebhookTarget(Uri Uri, IPAddress Address);

public sealed class WebhookTargetGuard(IWebhookDnsResolver dns, DeveloperWebhookSettings settings,
    IOptions<DeveloperWebhookOptions> options)
{
    public async Task<VettedWebhookTarget> VetAsync(string target, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new CommandRefusedException("webhook_url_invalid", "Enter an absolute webhook URL.");
        var testsMayUseHttp = options.Value.AllowLoopbackTargetsForTests;
        if (uri.Scheme != Uri.UriSchemeHttps && !(testsMayUseHttp && uri.Scheme == Uri.UriSchemeHttp))
            throw new CommandRefusedException("webhook_url_not_https", "Webhook targets must use HTTPS.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new CommandRefusedException("webhook_target_not_allowed", "Webhook URLs cannot contain credentials.");
        if (uri.Port != 443 && !(testsMayUseHttp || await settings.AllowNonStandardPortsAsync(cancellationToken)))
            throw new CommandRefusedException("webhook_target_not_allowed", "Webhook targets use port 443 unless the tenant enables another port.");
        IReadOnlyList<IPAddress> addresses;
        try { addresses = await dns.ResolveAsync(uri.IdnHost, cancellationToken); }
        catch (SocketException error)
        {
            throw new CommandRefusedException("webhook_target_unresolved", "The webhook host could not be resolved.", error.Message);
        }
        if (addresses.Count == 0)
            throw new CommandRefusedException("webhook_target_unresolved", "The webhook host returned no addresses.");
        foreach (var address in addresses)
            if (IsPrivate(address) && !(testsMayUseHttp && IPAddress.IsLoopback(Normalise(address))))
                throw new CommandRefusedException("webhook_private_target_needs_relay", "Private webhook targets require a registered site relay.");
        var vetted = addresses.Select(Normalise).First(address => !IsPrivate(address)
            || testsMayUseHttp && IPAddress.IsLoopback(address));
        return new(uri, vetted);
    }

    public static bool IsPrivate(IPAddress supplied)
    {
        var address = Normalise(supplied);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPrivateIpv4(address.GetAddressBytes());
        var bytes = address.GetAddressBytes();
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast
            || (bytes[0] & 0xfe) == 0xfc
            || bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
            return true;
        if (HasPrefix(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]))
            return IsPrivateIpv4(bytes.AsSpan(12, 4));
        if (HasPrefix(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01]))
            return IsPrivateIpv4([bytes[6], bytes[7], bytes[9], bytes[10]]);
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPrivateIpv4(bytes.AsSpan(2, 4));
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            return IsPrivateIpv4(bytes.AsSpan(12, 4));
        return false;
    }

    private static bool IsPrivateIpv4(ReadOnlySpan<byte> b) =>
        b[0] == 0 || b[0] == 10 || b[0] == 127
        || b[0] == 169 && b[1] == 254
        || b[0] == 172 && b[1] is >= 16 and <= 31
        || b[0] == 192 && (b[1] == 168 || b[1] == 0)
        || b[0] == 100 && b[1] is >= 64 and <= 127
        || b[0] == 198 && b[1] is 18 or 19
        || b[0] >= 224;

    private static bool HasPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix) =>
        address[..prefix.Length].SequenceEqual(prefix);

    private static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public static class WebhookSigner
{
    public static string EncodeSecret(ReadOnlySpan<byte> secret) => "yjw_" +
        Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Header(long unixSeconds, ReadOnlySpan<byte> body,
        IReadOnlyList<string> secrets)
    {
        var prefix = Encoding.UTF8.GetBytes(unixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return "t=" + unixSeconds + string.Concat(secrets.Select(secret =>
        {
            var key = Encoding.UTF8.GetBytes(secret);
            try
            {
                return ",v1=" + Convert.ToHexString(HMACSHA256.HashData(key, signed)).ToLowerInvariant();
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }));
    }
}

public sealed record WebhookSendResult(bool Success, int? StatusCode, int DurationMs,
    string? ResponseExcerpt, string? ErrorCode, string? ResponseBody = null);

public sealed class WebhookTransport(WebhookTargetGuard guard, IUserSecretProtector protector,
    DeveloperWebhookSettings settings, IClock clock)
{
    public const string SecretPurpose = "developer.webhook-signing";

    public async Task<WebhookSendResult> SendAsync(WebhookSubscription subscription, byte[] body,
        Guid eventId, string eventType, int attempt, CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        var target = await guard.VetAsync(subscription.Url, cancellationToken);
        var unsealed = new List<byte[]>
        {
            await protector.UnprotectAsync(subscription.SecretSealed, SecretPurpose, cancellationToken),
        };
        if (subscription.PreviousSecretSealed is { } previous
            && subscription.PreviousSecretExpiresAt > clock.UtcNow)
            unsealed.Add(await protector.UnprotectAsync(previous, SecretPurpose, cancellationToken));
        try
        {
            var secrets = unsealed.Select(secret => WebhookSigner.EncodeSecret(secret)).ToList();
            var unix = new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, target.Uri)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.TryAddWithoutValidation("Yojana-Signature", WebhookSigner.Header(unix, body, secrets));
            request.Headers.TryAddWithoutValidation("Yojana-Event-Id", eventId.ToString("D"));
            request.Headers.TryAddWithoutValidation("Yojana-Event-Type", eventType);
            request.Headers.TryAddWithoutValidation("Yojana-Delivery-Attempt", attempt.ToString());
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, ct) =>
                {
                    var socket = new Socket(target.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(target.Address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds
                    ?? await settings.TimeoutSecondsAsync(cancellationToken)),
            };
            var timer = Stopwatch.StartNew();
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var excerpt = await ReadExcerptAsync(response, cancellationToken);
                return new((int)response.StatusCode is >= 200 and <= 299, (int)response.StatusCode,
                    checked((int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds)), excerpt,
                    (int)response.StatusCode is >= 200 and <= 299 ? null : "http_" + (int)response.StatusCode,
                    excerpt);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(false, null, checked((int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds)),
                    null, "timeout");
            }
            catch (HttpRequestException)
            {
                return new(false, null, checked((int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds)),
                    null, "transport_error");
            }
        }
        finally
        {
            foreach (var secret in unsealed) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static async Task<string?> ReadExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer, ct);
        return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }
}

public sealed class DeveloperWebhookOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<DeveloperWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, DeveloperWebhookOptions options) =>
        options.AllowLoopbackTargetsForTests && !environment.IsDevelopment()
            ? ValidateOptionsResult.Fail("Developer:Webhooks:AllowLoopbackTargetsForTests is allowed only in Development/test hosts.")
            : options.SubscriptionParallelism is < 1 or > 32
                ? ValidateOptionsResult.Fail("Developer:Webhooks:SubscriptionParallelism must be from 1 to 32.")
                : ValidateOptionsResult.Success;
}
