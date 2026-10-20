using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using Xunit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Mail;

namespace Yojana.Platform.Tests;

public sealed class MailTransportTests
{
    private static OutgoingMail Mail(string subject = "Reset your password", string to = "meera@asterra.example") =>
        new(MailKind.PasswordReset, new TenantId(Guid.NewGuid()), Guid.NewGuid(), new MailRecipient(to, "Meera Pillai"),
            subject, "<p>Use <a href=\"https://yojana.example/reset/abc\">this link</a>.</p>", "Use https://yojana.example/reset/abc\n");

    private static MailOptions Options(string transport = MailTransportKind.Pickup) => new()
    {
        Transport = transport,
        FromAddress = "no-reply@yojana.example",
        FromName = "Yojana",
        PickupDirectory = Path.Combine(Path.GetTempPath(), "yojana-mail-tests", Guid.NewGuid().ToString("N")),
    };

    [Fact]
    public void Options_refuse_clear_text_smtp_to_a_remote_host_and_half_set_credentials()
    {
        var options = Options(MailTransportKind.Smtp);
        options.Smtp.Host = "smtp.example.net";
        options.Smtp.Security = SmtpSecurity.None;
        Assert.Contains("loopback", Assert.Throws<InvalidOperationException>(options.Validate).Message);

        options.Smtp.Host = "127.0.0.1";
        options.Validate();

        options.Smtp.UserName = "relay-user";
        Assert.Contains(MailOptions.SmtpPasswordVariable, Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void Options_need_a_sender_address_and_complete_graph_client_secret_settings()
    {
        var options = Options(MailTransportKind.Graph);
        options.FromAddress = "not an address";
        Assert.Throws<InvalidOperationException>(options.Validate);

        options.FromAddress = "no-reply@yojana.example";
        options.Graph.SenderMailbox = "no-reply@yojana.example";
        options.Validate();

        options.Graph.Credential = GraphCredentialKind.ClientSecret;
        Assert.Contains(MailOptions.GraphClientSecretVariable, Assert.Throws<InvalidOperationException>(options.Validate).Message);

        new MailOptions().Validate();
    }

    [Theory]
    [InlineData("Reset\r\nBcc: someone@evil.example")]
    [InlineData("")]
    public void A_subject_on_more_than_one_line_or_empty_is_refused(string subject) =>
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(Mail(subject)));

    [Theory]
    [InlineData("two@example.com, three@example.com")]
    [InlineData("Meera <meera@example.com>")]
    [InlineData("no-at-sign")]
    public void The_recipient_must_be_exactly_one_bare_address(string to) =>
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(Mail(to: to)));

    [Fact]
    public void The_message_carries_both_parts_the_tenant_sign_off_and_the_delivery_id()
    {
        var id = Guid.NewGuid();
        var message = MailMessageFactory.Build(new PreparedMail(id, Mail(), "Asterra Service Desk", "service@asterra.example"), Options());

        var from = Assert.Single(message.From.Mailboxes);
        Assert.Equal("Asterra Service Desk", from.Name);
        Assert.Equal("no-reply@yojana.example", from.Address);
        Assert.Equal("service@asterra.example", Assert.Single(message.ReplyTo.Mailboxes).Address);
        Assert.Equal(id.ToString("D"), message.Headers[MailMessageFactory.MailIdHeader]);
        Assert.Equal("auto-generated", message.Headers["Auto-Submitted"]);
        Assert.EndsWith("@yojana.example", message.MessageId);
        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Contains("https://yojana.example/reset/abc", message.TextBody);
        Assert.Contains("<a href=", message.HtmlBody);
        Assert.Equal(2, alternative.Count);
    }

    [Fact]
    public void Retries_start_quickly_and_spread_to_about_four_hours_over_eight_attempts()
    {
        var options = new MailOptions();
        Assert.Equal(TimeSpan.FromMinutes(1), MailRetrySchedule.After(1, options));
        var total = Enumerable.Range(1, options.MaxAttempts - 1)
            .Aggregate(TimeSpan.Zero, (sum, attempt) => sum + MailRetrySchedule.After(attempt, options));
        Assert.InRange(total, TimeSpan.FromHours(3.5), TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.FromHours(4), MailRetrySchedule.After(12, options));

        options.RetryDelaySeconds = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task The_pickup_transport_writes_one_complete_eml_file()
    {
        var options = Options();
        var id = Guid.NewGuid();
        var result = await new PickupDirectoryMailTransport(options).SendAsync(new PreparedMail(id, Mail(), null, null), CancellationToken.None);

        Assert.Equal(MailSendOutcome.Sent, result.Outcome);
        var file = Assert.Single(Directory.GetFiles(options.PickupDirectory));
        Assert.Equal($"{id:N}.eml", Path.GetFileName(file));
        var loaded = await MimeMessage.LoadAsync(file);
        Assert.Equal("Reset your password", loaded.Subject);
        Directory.Delete(options.PickupDirectory, recursive: true);
    }

    [Fact]
    public async Task Smtp_hands_the_message_to_the_server()
    {
        await using var server = FakeSmtpServer.Start(rcptReply: "250 OK");
        var result = await Smtp(server.Port).SendAsync(new PreparedMail(Guid.NewGuid(), Mail(), null, null), CancellationToken.None);

        Assert.Equal(MailSendOutcome.Sent, result.Outcome);
        var data = await server.ReceivedData;
        Assert.Contains("Subject: Reset your password", data);
        Assert.Contains("RCPT TO:<meera@asterra.example>", server.Commands);
    }

    [Fact]
    public async Task Smtp_treats_a_refused_recipient_as_final_and_a_busy_server_as_worth_retrying()
    {
        await using (var refusing = FakeSmtpServer.Start(rcptReply: "550 5.1.1 No such user"))
        {
            var refused = await Smtp(refusing.Port).SendAsync(new PreparedMail(Guid.NewGuid(), Mail(), null, null), CancellationToken.None);
            Assert.Equal(MailSendOutcome.Permanent, refused.Outcome);
            Assert.Contains("550", refused.Error);
        }

        await using (var busy = FakeSmtpServer.Start(rcptReply: "451 4.3.0 Try again later"))
        {
            var deferred = await Smtp(busy.Port).SendAsync(new PreparedMail(Guid.NewGuid(), Mail(), null, null), CancellationToken.None);
            Assert.Equal(MailSendOutcome.Transient, deferred.Outcome);
        }
    }

    [Fact]
    public async Task Smtp_treats_an_unreachable_server_as_worth_retrying()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Smtp(port).SendAsync(new PreparedMail(Guid.NewGuid(), Mail(), null, null), CancellationToken.None);
        Assert.Equal(MailSendOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task No_transport_records_a_final_failure_instead_of_sending()
    {
        var result = await new DisabledMailTransport().SendAsync(new PreparedMail(Guid.NewGuid(), Mail(), null, null), CancellationToken.None);
        Assert.Equal(MailSendOutcome.Permanent, result.Outcome);
    }

    private static SmtpMailTransport Smtp(int port)
    {
        var options = Options(MailTransportKind.Smtp);
        options.Smtp.Host = "127.0.0.1";
        options.Smtp.Port = port;
        options.Smtp.Security = SmtpSecurity.None;
        options.Smtp.TimeoutSeconds = 5;
        options.Validate();
        return new SmtpMailTransport(options);
    }

    /// <summary>Just enough of an SMTP server on loopback to accept or refuse one message.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stop = new();
        private Task? loop;

        public List<string> Commands { get; } = [];
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<string> ReceivedData => data.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public static FakeSmtpServer Start(string rcptReply)
        {
            var server = new FakeSmtpServer();
            server.listener.Start();
            server.loop = server.ServeAsync(rcptReply);
            return server;
        }

        private async Task ServeAsync(string rcptReply)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                lock (Commands) Commands.Add(line);
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "MAIL":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        await writer.WriteLineAsync(rcptReply);
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var body = new StringBuilder();
                        while (await reader.ReadLineAsync(stop.Token) is { } dataLine && dataLine != ".")
                            body.AppendLine(dataLine);
                        data.TrySetResult(body.ToString());
                        await writer.WriteLineAsync("250 Queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            try { if (loop is not null) await loop; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            stop.Dispose();
        }
    }
}
