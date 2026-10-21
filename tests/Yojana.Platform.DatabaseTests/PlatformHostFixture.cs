using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yojana.Platform;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Contracts.Security;
using System.Security.Cryptography;
using System.Text;
using Yojana.Platform.Mail;
using Yojana.Platform.Security;

namespace Yojana.Platform.DatabaseTests;

/// <summary>
/// One platform host per test run against the database named by YOJANA_TEST_SQL (created and migrated on
/// start). Tests are skipped, not failed, when the variable is absent so `make test` stays database-free.
/// </summary>
public sealed class PlatformHostFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");

    public WebApplication Host { get; private set; } = null!;

    public MutablePlatformClock Clock { get; } = new(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

    public ScriptedMailTransport Mail { get; } = new();

    public IServiceProvider Services => Host.Services;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Platform:ConnectionString"] = ConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:ServiceName"] = "yojana-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "platform-database-tests-only-hmac-key";
        builder.Configuration["Platform:SignIn:MaintenanceBatchSize"] = "2";
        // Every test class signs in from this one loopback address within about a minute, which the production limit
        // (120 a minute per address) would refuse with 429 part-way through the run. The limiter itself is tested on its
        // own host with its own limit (SignInSecurityTests).
        builder.Configuration["Platform:SignIn:RequestLimitPerMinute"] = "100000";
        builder.Configuration["Platform:Mail:MaxAttempts"] = "3";
        builder.Configuration["Platform:Mail:RetryDelaySeconds:0"] = "1";
        builder.Configuration["Platform:Mail:MaintenanceBatchSize"] = "2";
        builder.Configuration["Platform:Mail:LinkBaseUrl"] = "http://127.0.0.1:5080";
        // Browsers refuse an IP address as a passkey's relying party, so passkey tests sign for localhost (SoftwarePasskey).
        builder.Configuration["Platform:Passkeys:RelyingPartyId"] = "localhost";
        builder.Configuration["Platform:Passkeys:Origins:0"] = SoftwarePasskey.Origin;
        // The recorder handler for MailDeliveryRecorded lives in this test assembly (MailDeliveryRecordedHandler).
        PlatformSetup.IncludeHandlers(typeof(PlatformHostFixture).Assembly);
        builder.AddYojanaPlatform();
        // Test-only adapters: production composition supplies envelope encryption from Integration and QR rendering
        // from Serialisation. Platform database tests must never make plaintext persistence look like a valid fallback.
        builder.Services.AddSingleton<IUserSecretProtector, TestUserSecretProtector>();
        builder.Services.AddSingleton<IQrCodeRenderer, TestQrCodeRenderer>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
        builder.Services.Replace(ServiceDescriptor.Singleton<IMailTransport>(Mail));
        Host = builder.Build();
        Host.UseYojanaPlatform();
        Host.MapYojanaSecurityEndpoints();
        Host.MapYojanaSecondStepEndpoints();
        Host.MapYojanaPasskeyEndpoints();
        Host.MapYojanaPasswordResetEndpoints();
        Host.MapYojanaUserManagementEndpoints();
        Host.MapYojanaInvitationEndpoints();
        Host.MapYojanaMailEndpoints();
        await Host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await Host.StopAsync();
        await Host.DisposeAsync();
    }

    public HttpClient Client()
    {
        var address = Host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}

internal sealed class TestUserSecretProtector : IUserSecretProtector
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose,
        CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
        return Task.FromResult(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> sealedValue, string purpose,
        CancellationToken cancellationToken)
    {
        var value = sealedValue.Span;
        var plaintext = new byte[value.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(value[..12], value.Slice(28), value.Slice(12, 16), plaintext,
            Encoding.UTF8.GetBytes(purpose));
        return Task.FromResult(plaintext);
    }
}

internal sealed class TestQrCodeRenderer : IQrCodeRenderer
{
    public Task<string> RenderSvgAsync(string content, CancellationToken cancellationToken) =>
        Task.FromResult("<svg xmlns=\"http://www.w3.org/2000/svg\" aria-label=\"test QR\"></svg>");
}

/// <summary>A transport whose outcome each test scripts per recipient address; unscripted recipients are sent.</summary>
public sealed class ScriptedMailTransport : IMailTransport
{
    private readonly Dictionary<string, Queue<MailSendResult>> scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PreparedMail> attempts = [];

    public string Name => "scripted";

    public void Script(string recipient, params MailSendResult[] outcomes)
    {
        lock (scripts) scripts[recipient] = new Queue<MailSendResult>(outcomes);
    }

    public IReadOnlyList<PreparedMail> AttemptsFor(string recipient)
    {
        lock (attempts) return attempts.Where(mail => string.Equals(mail.Mail.To.Address, recipient, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public Task<MailSendResult> SendAsync(PreparedMail mail, CancellationToken cancellationToken)
    {
        lock (attempts) attempts.Add(mail);
        lock (scripts)
        {
            return Task.FromResult(scripts.TryGetValue(mail.Mail.To.Address, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : MailSendResult.Sent($"scripted-{mail.MailId:N}"));
        }
    }
}

public sealed class MutablePlatformClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
}

[CollectionDefinition("platform host")]
public sealed class PlatformHostCollection : ICollectionFixture<PlatformHostFixture>;

/// <summary>Marks a fact that needs the database; skipped when YOJANA_TEST_SQL is not set.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (PlatformHostFixture.ConnectionString is null)
        {
            Skip = "YOJANA_TEST_SQL is not set";
        }
    }
}
