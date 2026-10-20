using System.Net;
using MimeKit;

namespace Yojana.Platform.Mail;

/// <summary>
/// Deployment-wide mail transport (<c>Platform:Mail</c>). One transport serves every tenant (OD-38); a tenant chooses only
/// how its mail is signed off (<see cref="TenantMailSettings"/>). The sending address stays the deployment's so the
/// domain's SPF and DKIM records keep matching.
/// </summary>
public sealed class MailOptions
{
    public const string SectionName = "Platform:Mail";
    public const string SmtpPasswordVariable = "YOJANA_MAIL_SMTP_PASSWORD";
    public const string GraphClientSecretVariable = "YOJANA_MAIL_GRAPH_CLIENT_SECRET";

    /// <summary><c>none</c> (record and send nothing), <c>pickup</c> (write .eml files), <c>smtp</c> or <c>graph</c>.</summary>
    public string Transport { get; set; } = MailTransportKind.None;
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Yojana";
    /// <summary>
    /// Where recipients open the product (for example https://yojana.example.com): every link in a mail is built from
    /// it with <see cref="Link"/>, never from the request's Host header, which a client controls.
    /// </summary>
    public string LinkBaseUrl { get; set; } = "";
    public int MaxAttempts { get; set; } = 8;
    /// <summary>
    /// Wait after each failed attempt, in seconds; attempts past the end of the list wait as long as its last entry. Null
    /// means <see cref="DefaultRetryDelaySeconds"/> (no initial value here: configuration binding appends to an array).
    /// </summary>
    public int[]? RetryDelaySeconds { get; set; }

    /// <summary>Quick at first for brief outages, then the eight attempts spread over about four hours.</summary>
    public static readonly IReadOnlyList<int> DefaultRetryDelaySeconds = [60, 120, 300, 600, 1_800, 3_600, 7_200, 14_400];

    public IReadOnlyList<int> RetryDelays => RetryDelaySeconds ?? DefaultRetryDelaySeconds;
    public int LogRetentionDays { get; set; } = 90;
    public int MaintenanceBatchSize { get; set; } = 1_000;
    public string PickupDirectory { get; set; } = "";
    public SmtpMailOptions Smtp { get; set; } = new();
    public GraphMailOptions Graph { get; set; } = new();

    public void Validate()
    {
        if (!MailTransportKind.All.Contains(Transport))
            throw Invalid($"Transport must be one of {string.Join(", ", MailTransportKind.All)}.");
        if (MaxAttempts is < 1 or > 20 || LogRetentionDays < 1 || MaintenanceBatchSize is < 1 or > 10_000)
            throw Invalid("MaxAttempts (1–20), LogRetentionDays and MaintenanceBatchSize (1–10000) are out of range.");
        if (RetryDelays.Count == 0 || RetryDelays.Any(delay => delay is < 1 or > 86_400))
            throw Invalid("RetryDelaySeconds needs at least one delay, each 1 to 86400 seconds.");
        if (Transport == MailTransportKind.None) return;
        if (!Uri.TryCreate(LinkBaseUrl, UriKind.Absolute, out var linkBase) || linkBase.Query.Length > 0 || linkBase.Fragment.Length > 0
            || !(linkBase.Scheme == Uri.UriSchemeHttps || (linkBase.Scheme == Uri.UriSchemeHttp && linkBase.IsLoopback)))
            throw Invalid("LinkBaseUrl must be an absolute https address (http only for a loopback host) without query or fragment.");
        if (!MailboxAddress.TryParse(FromAddress, out var from) || from.Address != FromAddress.Trim())
            throw Invalid("FromAddress must be a single e-mail address.");
        if (FromName.Any(char.IsControl)) throw Invalid("FromName must not contain control characters.");
        switch (Transport)
        {
            case MailTransportKind.Pickup when string.IsNullOrWhiteSpace(PickupDirectory):
                throw Invalid("PickupDirectory is required for the pickup transport.");
            case MailTransportKind.Smtp:
                Smtp.Validate();
                break;
            case MailTransportKind.Graph:
                Graph.Validate();
                break;
        }
    }

    /// <summary>
    /// An absolute link into the product. <paramref name="fragment"/> (a single-use token, for instance) goes after '#'
    /// so it never reaches a server or proxy log; the page reads it and posts it.
    /// </summary>
    public string Link(string path, string? fragment = null)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("A link path starts with a single '/'.", nameof(path));
        var link = LinkBaseUrl.TrimEnd('/') + path;
        return fragment is null ? link : $"{link}#{fragment}";
    }

    internal static InvalidOperationException Invalid(string detail) => new($"{SectionName}: {detail}");
}

public sealed class SmtpMailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary><c>starttls</c> (required upgrade), <c>tls</c> (TLS from connect, usually port 465) or <c>none</c> (loopback only).</summary>
    public string Security { get; set; } = SmtpSecurity.StartTls;
    public string UserName { get; set; } = "";
    /// <summary>Normally empty here and supplied by <see cref="MailOptions.SmtpPasswordVariable"/>.</summary>
    public string Password { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw MailOptions.Invalid("Smtp:Host is required.");
        if (Port is < 1 or > 65535 || TimeoutSeconds is < 1 or > 300) throw MailOptions.Invalid("Smtp:Port or Smtp:TimeoutSeconds is out of range.");
        if (!SmtpSecurity.All.Contains(Security)) throw MailOptions.Invalid($"Smtp:Security must be one of {string.Join(", ", SmtpSecurity.All)}.");
        // Credentials and reset links must never cross a network in clear text; an unencrypted connection is accepted
        // only to a relay on the same machine (a local catcher in development, or a host-local relay that encrypts onward).
        if (Security == SmtpSecurity.None && !IsLoopback(Host))
            throw MailOptions.Invalid("Smtp:Security none is allowed only for a loopback host.");
        if (string.IsNullOrEmpty(UserName) != string.IsNullOrEmpty(Password))
            throw MailOptions.Invalid($"Smtp:UserName and the password ({MailOptions.SmtpPasswordVariable}) must be set together.");
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

public sealed class GraphMailOptions
{
    /// <summary>The directory (Entra ID) tenant that owns the sending mailbox.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>The mailbox mail is sent from: its user principal name or object id.</summary>
    public string SenderMailbox { get; set; } = "";
    /// <summary><c>managed_identity</c> (default) or <c>client_secret</c>.</summary>
    public string Credential { get; set; } = GraphCredentialKind.ManagedIdentity;
    public string ClientId { get; set; } = "";
    /// <summary>Normally empty here and supplied by <see cref="MailOptions.GraphClientSecretVariable"/>.</summary>
    public string ClientSecret { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SenderMailbox)) throw MailOptions.Invalid("Graph:SenderMailbox is required.");
        if (TimeoutSeconds is < 1 or > 300) throw MailOptions.Invalid("Graph:TimeoutSeconds is out of range.");
        switch (Credential)
        {
            case GraphCredentialKind.ManagedIdentity:
                break;
            case GraphCredentialKind.ClientSecret when string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId)
                                                       || string.IsNullOrWhiteSpace(ClientSecret):
                throw MailOptions.Invalid($"Graph client_secret needs TenantId, ClientId and {MailOptions.GraphClientSecretVariable}.");
            case GraphCredentialKind.ClientSecret:
                break;
            default:
                throw MailOptions.Invalid("Graph:Credential must be managed_identity or client_secret.");
        }
    }
}

public static class MailTransportKind
{
    public const string None = "none";
    public const string Pickup = "pickup";
    public const string Smtp = "smtp";
    public const string Graph = "graph";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { None, Pickup, Smtp, Graph };
}

public static class SmtpSecurity
{
    public const string StartTls = "starttls";
    public const string Tls = "tls";
    public const string None = "none";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { StartTls, Tls, None };
}

public static class GraphCredentialKind
{
    public const string ManagedIdentity = "managed_identity";
    public const string ClientSecret = "client_secret";
}
