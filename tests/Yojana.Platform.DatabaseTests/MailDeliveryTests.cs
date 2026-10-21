using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Mail;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class MailDeliveryTests(PlatformHostFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    [DatabaseFact]
    public async Task Queued_mail_is_delivered_with_the_tenant_sign_off_and_logged_without_its_body()
    {
        var tenant = await AddTenantAsync("mail");
        var to = Recipient();
        using (var scope = ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
            await settings.SetAsync(TenantMailSettings.SenderNameKey, "Fictional Service Desk", CancellationToken.None);
            await settings.SetAsync(TenantMailSettings.ReplyToKey, "desk@example.invalid", CancellationToken.None);
        }

        var id = await QueueAsync(tenant, to);
        var row = await WaitForSettledAsync(id);

        Assert.Equal(MailStatus.Sent, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(tenant, row.TenantId);
        Assert.Equal(MailKind.PasswordReset, row.Kind);
        Assert.Equal($"scripted-{id:N}", row.ProviderMessageId);
        var sent = Assert.Single(fixture.Mail.AttemptsFor(to));
        Assert.Equal("Fictional Service Desk", sent.SenderName);
        Assert.Equal("desk@example.invalid", sent.ReplyTo);

        using var check = fixture.Services.CreateScope();
        var columns = await check.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.SqlQueryRaw<string>(
            "SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('platform.mail_message')").ToListAsync();
        Assert.DoesNotContain(columns, column => column.Contains("body", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("reset/fictional-token", string.Join("|", columns));
    }

    [DatabaseFact]
    public async Task Copies_and_attachments_reach_the_transport_and_the_body_still_stays_out_of_the_log()
    {
        var tenant = await AddTenantAsync("mailcc");
        var to = Recipient();
        var pdf = "%PDF-1.7 fictional"u8.ToArray();
        Guid id;
        using (var scope = ScopeFor(tenant))
        {
            id = await scope.ServiceProvider.GetRequiredService<IMailOutbox>().QueueAsync(Mail(tenant, to) with
            {
                Cc = [new MailRecipient("facilities@example.invalid", "Facilities desk")],
                Attachments = [new MailAttachment("What's covered · RS11-0001.pdf", "application/pdf", pdf)],
            }, CancellationToken.None);
        }

        Assert.Equal(MailStatus.Sent, (await WaitForSettledAsync(id)).Status);
        var sent = Assert.Single(fixture.Mail.AttemptsFor(to));
        Assert.Equal("facilities@example.invalid", Assert.Single(sent.Mail.Cc!).Address);
        Assert.Equal(pdf, Assert.Single(sent.Mail.Attachments!).Content);
        var message = MailMessageFactory.Build(sent, fixture.Services.GetRequiredService<MailOptions>());
        Assert.Equal("facilities@example.invalid", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Equal("What's covered · RS11-0001.pdf", Assert.Single(message.Attachments).ContentDisposition!.FileName);
    }

    [DatabaseFact]
    public async Task Every_attempt_of_a_tenant_mail_is_announced_in_that_tenant_and_global_mail_is_not()
    {
        var tenant = await AddTenantAsync("mailannounce");
        var to = Recipient();
        fixture.Mail.Script(to, MailSendResult.Transient("smtp 421: try later"));
        var id = await QueueAsync(tenant, to);
        await WaitForSettledAsync(id);

        var deadline = DateTime.UtcNow + Settle;
        while (MailDeliveryRecordedHandler.For(id).Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(100);
        var announced = MailDeliveryRecordedHandler.For(id);
        Assert.Equal([MailStatus.Retrying, MailStatus.Sent], announced.Select(x => x.Event.Status));
        Assert.Equal([1, 2], announced.Select(x => x.Event.Attempts));
        Assert.All(announced, x => Assert.Equal(tenant, x.ScopeTenant));
        Assert.All(announced, x => Assert.Equal(tenant, x.Event.Tenant));
        Assert.Equal("smtp 421: try later", announced[0].Event.Error);
        Assert.NotNull(announced[1].Event.SentAt);

        var global = Recipient();
        Guid globalId;
        using (var scope = fixture.Services.CreateScope())
            globalId = await scope.ServiceProvider.GetRequiredService<IMailOutbox>().QueueAsync(Mail(null, global), CancellationToken.None);
        await WaitForSettledAsync(globalId);
        await Task.Delay(500);
        Assert.Empty(MailDeliveryRecordedHandler.For(globalId));
    }

    [Fact]
    public void Copies_and_attachments_are_checked_before_queueing()
    {
        var mail = Mail(null, "person@example.invalid");
        MailOutbox.Validate(mail with { Attachments = [new("sheet.pdf", "application/pdf", [1])] });
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with { Cc = [new("not an address")] }));
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with
        {
            Cc = Enumerable.Range(0, MailLimits.MaxCc + 1).Select(i => new MailRecipient($"cc{i}@example.invalid")).ToList(),
        }));
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with { Attachments = [new("run.exe", "application/octet-stream", [1])] }));
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with { Attachments = [new("../sheet.pdf", "application/pdf", [1])] }));
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with { Attachments = [new("empty.pdf", "application/pdf", [])] }));
        Assert.Throws<ArgumentException>(() => MailOutbox.Validate(mail with
        {
            Attachments = [new("big.pdf", "application/pdf", new byte[MailLimits.MaxAttachmentBytes + 1])],
        }));
    }

    [DatabaseFact]
    public async Task A_transient_failure_is_retried_and_the_attempts_are_recorded()
    {
        var tenant = await AddTenantAsync("mailretry");
        var to = Recipient();
        fixture.Mail.Script(to, MailSendResult.Transient("smtp 421: try later"));

        var row = await WaitForSettledAsync(await QueueAsync(tenant, to));

        Assert.Equal(MailStatus.Sent, row.Status);
        Assert.Equal(2, row.Attempts);
        Assert.Null(row.LastError);
        Assert.Equal(2, fixture.Mail.AttemptsFor(to).Count);
    }

    [DatabaseFact]
    public async Task Retrying_stops_at_the_attempt_limit_and_records_why()
    {
        var tenant = await AddTenantAsync("mailgiveup");
        var to = Recipient();
        fixture.Mail.Script(to, MailSendResult.Transient("down"), MailSendResult.Transient("down"),
            MailSendResult.Transient("still down"), MailSendResult.Transient("never reached"));

        var row = await WaitForSettledAsync(await QueueAsync(tenant, to));

        Assert.Equal(MailStatus.Failed, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal("gave up after 3 attempts: still down", row.LastError);
        Assert.Null(row.NextAttemptAt);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(3, fixture.Mail.AttemptsFor(to).Count);
    }

    [DatabaseFact]
    public async Task A_refused_message_is_not_retried()
    {
        var tenant = await AddTenantAsync("mailrefused");
        var to = Recipient();
        fixture.Mail.Script(to, MailSendResult.Permanent("smtp 550 RecipientNotAccepted"));

        var row = await WaitForSettledAsync(await QueueAsync(tenant, to));

        Assert.Equal(MailStatus.Failed, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("smtp 550 RecipientNotAccepted", row.LastError);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.Mail.AttemptsFor(to));
    }

    [DatabaseFact]
    public async Task A_redelivered_attempt_does_not_send_the_mail_again()
    {
        var tenant = await AddTenantAsync("mailtwice");
        var to = Recipient();
        var prepared = new PreparedMail(Guid.CreateVersion7(), Mail(tenant, to), null, null);
        var delivery = new DeliverMail(prepared, fixture.Clock.UtcNow, 1);

        for (var copy = 0; copy < 2; copy++)
        {
            using var scope = ScopeFor(tenant);
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(delivery);
        }

        Assert.Single(fixture.Mail.AttemptsFor(to));
        Assert.Equal(MailStatus.Sent, (await WaitForSettledAsync(prepared.MailId)).Status);
    }

    [DatabaseFact]
    public async Task A_tenant_mail_must_be_queued_inside_that_tenant_and_a_global_mail_needs_no_tenant()
    {
        var mine = await AddTenantAsync("mailmine");
        var other = await AddTenantAsync("mailother");
        using (var scope = ScopeFor(mine))
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IMailOutbox>();
            await Assert.ThrowsAsync<ArgumentException>(() => outbox.QueueAsync(Mail(other, Recipient()), CancellationToken.None));
        }

        var to = Recipient();
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            id = await scope.ServiceProvider.GetRequiredService<IMailOutbox>()
                .QueueAsync(Mail(null, to), CancellationToken.None);
        }

        var row = await WaitForSettledAsync(id);
        Assert.Equal(MailStatus.Sent, row.Status);
        Assert.Null(row.TenantId);
        Assert.Null(Assert.Single(fixture.Mail.AttemptsFor(to)).SenderName);
    }

    [DatabaseFact]
    public async Task An_administrator_sends_a_test_message_to_themselves_and_another_tenant_cannot_see_it()
    {
        var (_, user, password) = await AddAdministratorAsync("mailtest");
        using var client = fixture.Client();
        (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password })).EnsureSuccessStatusCode();

        var accepted = await client.PostAsync("/api/platform/mail/test", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.Content.ReadFromJsonAsync<TestSendView>())!.MailId;
        await WaitForSettledAsync(id);
        var view = await client.GetFromJsonAsync<MailMessageView>($"/api/platform/mail/messages/{id}");
        Assert.Equal(MailStatus.Sent, view!.Status);
        Assert.Equal(user.Email, view.Recipient);
        Assert.Equal(MailKind.TransportTest, view.Kind);
        Assert.Contains("Fictional mailtest company", Assert.Single(fixture.Mail.AttemptsFor(user.Email)).Mail.TextBody);

        var (_, outsider, outsiderPassword) = await AddAdministratorAsync("mailpeek");
        using var other = fixture.Client();
        (await other.PostAsJsonAsync("/api/auth/sign-in", new { email = outsider.Email, password = outsiderPassword })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/platform/mail/messages/{id}")).StatusCode);
    }

    [DatabaseFact]
    public async Task Retention_removes_settled_rows_past_the_limit_in_batches_and_keeps_rows_still_retrying()
    {
        var old = fixture.Clock.UtcNow.AddDays(-91);
        var marker = $"retention-{Guid.NewGuid():N}";
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var (status, queuedAt) in new[]
                 {
                     (MailStatus.Sent, old), (MailStatus.Failed, old), (MailStatus.Sent, old),
                     (MailStatus.Retrying, old), (MailStatus.Sent, fixture.Clock.UtcNow),
                 })
        {
            db.MailMessages.Add(new MailMessage
            {
                Id = Guid.CreateVersion7(), QueuedAt = queuedAt, Kind = MailKind.TransportTest, Recipient = "x@example.invalid",
                Subject = marker, Status = status, Transport = "scripted",
            });
        }
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MailLogMaintenance>().PurgeAsync(CancellationToken.None);

        var left = await db.MailMessages.AsNoTracking().Where(row => row.Subject == marker)
            .Select(row => row.Status).OrderBy(status => status).ToListAsync();
        Assert.Equal([MailStatus.Retrying, MailStatus.Sent], left);
    }

    private sealed record TestSendView(Guid MailId);

    private static string Recipient() => $"mail-{Guid.NewGuid():N}@example.invalid";

    private static OutgoingMail Mail(TenantId? tenant, string to) => new(MailKind.PasswordReset, tenant, null,
        new MailRecipient(to, "Fictional Person"), "Reset your password",
        "<p><a href=\"https://yojana.example/reset/fictional-token\">Reset</a></p>", "https://yojana.example/reset/fictional-token\n");

    private async Task<Guid> QueueAsync(TenantId tenant, string to)
    {
        using var scope = ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMailOutbox>().QueueAsync(Mail(tenant, to), CancellationToken.None);
    }

    private async Task<MailMessage> WaitForSettledAsync(Guid id)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().MailMessages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (row is { Status: not MailStatus.Retrying }) return row;
            await Task.Delay(250);
        }

        throw new TimeoutException($"mail {id} did not settle within {Settle}");
    }

    private async Task<(TenantId Tenant, AppUser User, string Password)> AddAdministratorAsync(string prefix)
    {
        const string password = "Fictional-Test-Password-2026";
        var tenant = await AddTenantAsync(prefix);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{prefix}-{Guid.NewGuid():N}@example.invalid",
            DisplayName = $"Fictional {prefix} administrator", PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = fixture.Clock.UtcNow,
        };
        user.Memberships.Add(new UserTenantMembership { UserId = user.Id, TenantId = tenant, Roles = [YojanaRole.TenantAdministrator] });
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (tenant, user, password);
    }

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = tenant, Code = (prefix + Guid.NewGuid().ToString("N"))[..20],
            Name = $"Fictional {prefix} company", CreatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }
}

/// <summary>Test-only subscriber: records each announcement with the tenant its handler scope ran under.</summary>
public static class MailDeliveryRecordedHandler
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(MailDeliveryRecorded Event, TenantId? ScopeTenant)> Seen = new();

    public static void Handle(MailDeliveryRecorded message, ITenantContext tenant) =>
        Seen.Enqueue((message, tenant.HasTenant ? tenant.TenantId : null));

    public static IReadOnlyList<(MailDeliveryRecorded Event, TenantId? ScopeTenant)> For(Guid mailId) =>
        Seen.Where(x => x.Event.MailId == mailId).OrderBy(x => x.Event.Attempts).ToList();
}
