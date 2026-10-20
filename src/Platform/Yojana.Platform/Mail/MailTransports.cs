using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Azure.Core;
using Azure.Identity;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using Yojana.Platform.Contracts.Mail;

namespace Yojana.Platform.Mail;

/// <summary>A queued message with what the queueing tenant chose for its sign-off, as the transports receive it.</summary>
public sealed record PreparedMail(Guid MailId, OutgoingMail Mail, string? SenderName, string? ReplyTo);

public enum MailSendOutcome
{
    Sent,
    /// <summary>Worth trying again later: the server or network failed, or the transport is misconfigured and can be fixed.</summary>
    Transient,
    /// <summary>Trying again cannot help: the server refused this message or recipient, or no transport is configured.</summary>
    Permanent,
}

public sealed record MailSendResult(MailSendOutcome Outcome, string? ProviderMessageId = null, string? Error = null)
{
    public static MailSendResult Sent(string? providerMessageId) => new(MailSendOutcome.Sent, providerMessageId);
    public static MailSendResult Transient(string error) => new(MailSendOutcome.Transient, Error: error);
    public static MailSendResult Permanent(string error) => new(MailSendOutcome.Permanent, Error: error);
}

/// <summary>Hands one message to a mail system. Implementations report failures as results and never throw for them.</summary>
public interface IMailTransport
{
    string Name { get; }

    Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken);
}

/// <summary>Builds the MIME message every transport except Graph sends: text and HTML alternatives, from the deployment address.</summary>
public static class MailMessageFactory
{
    public const string MailIdHeader = "X-Yojana-Mail-Id";

    public static MimeMessage Build(PreparedMail prepared, MailOptions options)
    {
        var mail = prepared.Mail;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(prepared.SenderName ?? options.FromName, options.FromAddress));
        if (prepared.ReplyTo is not null) message.ReplyTo.Add(MailboxAddress.Parse(prepared.ReplyTo));
        message.To.Add(new MailboxAddress(mail.To.DisplayName ?? "", mail.To.Address));
        message.Subject = mail.Subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeUtils.GenerateMessageId(options.FromAddress[(options.FromAddress.IndexOf('@') + 1)..]);
        message.Headers.Add(MailIdHeader, prepared.MailId.ToString("D"));
        // RFC 3834: tells mail systems not to send out-of-office replies back to an address nobody reads.
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Body = new BodyBuilder { TextBody = mail.TextBody, HtmlBody = mail.HtmlBody }.ToMessageBody();
        return message;
    }
}

/// <summary>Records and sends nothing: the deployment has not configured mail.</summary>
public sealed class DisabledMailTransport : IMailTransport
{
    public string Name => MailTransportKind.None;

    public Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken) =>
        Task.FromResult(MailSendResult.Permanent($"No mail transport is configured ({MailOptions.SectionName}:Transport is none)."));
}

/// <summary>Development and test transport: writes each message as an .eml file that any mail client opens.</summary>
public sealed class PickupDirectoryMailTransport(MailOptions options) : IMailTransport
{
    public string Name => MailTransportKind.Pickup;

    public async Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken)
    {
        try
        {
            var message = MailMessageFactory.Build(mail, options);
            Directory.CreateDirectory(options.PickupDirectory);
            var final = Path.Combine(options.PickupDirectory, $"{mail.MailId:N}.eml");
            var partial = final + ".partial";
            await message.WriteToAsync(partial, cancellationToken);
            File.Move(partial, final, overwrite: true);
            return MailSendResult.Sent(message.MessageId);
        }
        catch (IOException exception)
        {
            return MailSendResult.Transient($"pickup directory: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return MailSendResult.Transient($"pickup directory: {exception.Message}");
        }
    }
}

public sealed class SmtpMailTransport(MailOptions options) : IMailTransport
{
    public string Name => MailTransportKind.Smtp;

    public async Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken)
    {
        var smtp = options.Smtp;
        var message = MailMessageFactory.Build(mail, options);
        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security switch
            {
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            }, cancellationToken);
            if (smtp.UserName.Length > 0) await client.AuthenticateAsync(smtp.UserName, smtp.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            return MailSendResult.Sent(message.MessageId);
        }
        catch (SmtpCommandException exception) when ((int)exception.StatusCode >= 500)
        {
            return MailSendResult.Permanent($"smtp {(int)exception.StatusCode} {exception.ErrorCode}: {exception.Message}");
        }
        catch (SmtpCommandException exception)
        {
            return MailSendResult.Transient($"smtp {(int)exception.StatusCode} {exception.ErrorCode}: {exception.Message}");
        }
        catch (AuthenticationException exception)
        {
            // A rejected login is a configuration problem an operator can fix inside the retry window, so keep trying.
            return MailSendResult.Transient($"smtp authentication failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is ProtocolException or IOException or SocketException or TimeoutException
                                              or SslHandshakeException or ServiceNotConnectedException
                                          || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return MailSendResult.Transient($"smtp {exception.GetType().Name}: {exception.Message}");
        }
    }
}

/// <summary>
/// Sends through the Graph <c>sendMail</c> action over plain HttpClient (OD-37: no Graph SDK). Graph accepts one body, so
/// the HTML part is sent and the plain-text part is not.
/// </summary>
public sealed class GraphMailTransport(MailOptions options, HttpClient http, TokenCredential credential) : IMailTransport
{
    public const string HttpClientName = "yojana-mail-graph";
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    public string Name => MailTransportKind.Graph;

    public static TokenCredential CredentialFor(GraphMailOptions graph) => graph.Credential == GraphCredentialKind.ClientSecret
        ? new ClientSecretCredential(graph.TenantId, graph.ClientId, graph.ClientSecret)
        : graph.ClientId.Length > 0
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(graph.ClientId))
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

    public async Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken)
    {
        var content = mail.Mail;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Graph.TimeoutSeconds));
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.Graph.SenderMailbox)}/sendMail");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = JsonContent.Create(new
            {
                message = new
                {
                    subject = content.Subject,
                    body = new { contentType = "HTML", content = content.HtmlBody },
                    toRecipients = new[] { new { emailAddress = new { address = content.To.Address, name = content.To.DisplayName } } },
                    replyTo = mail.ReplyTo is null ? null : new[] { new { emailAddress = new { address = mail.ReplyTo } } },
                    internetMessageHeaders = new[] { new { name = MailMessageFactory.MailIdHeader, value = mail.MailId.ToString("D") } },
                },
                saveToSentItems = false,
            });
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode) return MailSendResult.Sent(response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null);
            var detail = $"graph {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync(timeout.Token), 200)}";
            // 401/403 are permissions an administrator can grant; 408/429/5xx are the service's own trouble.
            return (int)response.StatusCode is 401 or 403 or 408 or 429 or >= 500
                ? MailSendResult.Transient(detail)
                : MailSendResult.Permanent(detail);
        }
        catch (AuthenticationFailedException exception)
        {
            return MailSendResult.Transient($"graph token: {Truncate(exception.Message, 200)}");
        }
        catch (HttpRequestException exception)
        {
            return MailSendResult.Transient($"graph {exception.GetType().Name}: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MailSendResult.Transient($"graph: no answer within {options.Graph.TimeoutSeconds} s");
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
