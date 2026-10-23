using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ErpChangeNotificationDatabaseTests(IntegrationHostFixture fixture) : IDisposable
{
    private const string Actor = "fictional-notification-admin";
    private static readonly DateTime FixtureTime = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose() => fixture.Clock.UtcNow = FixtureTime;

    [DatabaseFact]
    public async Task Step_open_mail_is_sent_once_per_role_with_the_right_absolute_link()
    {
        fixture.Mail.Reset();
        fixture.Clock.UtcNow = FixtureTime;
        var setup = await SetupAsync("ntstep-", ErpChangeStep.MasterData);
        await AddStaffAsync(setup.Tenant, YojanaRole.ItemMasterOwner, "owner@northstar.example", "Isha Rao");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ErpChangeMasterDataExceptions.Add(Exception(setup, "SRV-RACK-42"));
        await db.SaveChangesAsync();
        var notifications = scope.ServiceProvider.GetRequiredService<ErpChangeNotifications>();

        await notifications.StepOpenedAsync(setup.RunId, ErpChangeStep.MasterData, default);
        await notifications.StepOpenedAsync(setup.RunId, ErpChangeStep.MasterData, default);

        var mail = Assert.Single(fixture.Mail.Snapshot());
        Assert.Equal("ERP change: master-data exceptions need you", mail.Subject);
        Assert.Contains("https://yojana.example/integration/erp-change/master-data", mail.TextBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Source.ToString(), mail.TextBody, StringComparison.OrdinalIgnoreCase);
        var notice = Assert.Single(await db.ErpChangeNotices.AsNoTracking().ToListAsync());
        Assert.Equal(YojanaRole.ItemMasterOwner, notice.RecipientRole);
        Assert.Equal(1, notice.RecipientCount);
        Assert.DoesNotContain("@", notice.CountsJson, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Morning_digest_runs_at_the_tenant_hour_while_work_is_open_then_stops()
    {
        fixture.Mail.Reset();
        fixture.Clock.UtcNow = new(2026, 10, 2, 8, 10, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("ntdig-", ErpChangeStep.Stock);
        await AddStaffAsync(setup.Tenant, YojanaRole.InventoryController,
            "inventory@silverline.example", "Nikhil Shah");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ErpChangeStockDifferences.AddRange(
            Difference(setup, "SRV-2509-0041", covered: true),
            Difference(setup, "NAS-2509-0171", covered: false));
        await db.SaveChangesAsync();
        var notifications = scope.ServiceProvider.GetRequiredService<ErpChangeNotifications>();

        await notifications.EvaluateAsync(default);
        await notifications.EvaluateAsync(default);
        Assert.Equal(2, fixture.Mail.Snapshot().Count); // one step-open mail and one same-morning digest
        Assert.Contains(fixture.Mail.Snapshot(), mail => mail.Subject == "ERP change: stock differences need you"
            && mail.TextBody.Contains("1 differences on covered units block it; 1 others need your sign-off.",
                StringComparison.Ordinal));

        var rows = await db.ErpChangeStockDifferences.ToListAsync();
        foreach (var row in rows) row.Status = ErpChangeDifferenceStatus.Resolved;
        await db.SaveChangesAsync();
        fixture.Clock.UtcNow = new(2026, 10, 3, 8, 10, 0, DateTimeKind.Utc);
        await notifications.EvaluateAsync(default);
        Assert.Equal(2, fixture.Mail.Snapshot().Count);

        var run = await db.ErpChangeRuns.SingleAsync();
        run.Status = ErpChangeRunStatus.Cancelled;
        run.ClosedAt = fixture.Clock.UtcNow;
        run.ClosedBy = Actor;
        await db.SaveChangesAsync();
        fixture.Clock.UtcNow = new(2026, 10, 4, 8, 10, 0, DateTimeKind.Utc);
        await notifications.EvaluateAsync(default);
        Assert.Equal(2, fixture.Mail.Snapshot().Count);
    }

    [DatabaseFact]
    public async Task Configured_recipient_roles_are_validated_used_and_audited_without_addresses()
    {
        fixture.Mail.Reset();
        fixture.Clock.UtcNow = new(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("ntset-", ErpChangeStep.MasterData);
        var recipient = await AddStaffAsync(setup.Tenant, YojanaRole.InventoryController,
            "stock@northstar.example", "Mira Sen");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ErpChangeMasterDataExceptions.Add(Exception(setup, "NET-SW-48"));
        await db.SaveChangesAsync();
        var settings = scope.ServiceProvider.GetRequiredService<ErpChangeNotificationSettings>();
        var defaults = await settings.GetAsync(default);
        Assert.True(defaults.DigestEnabled);
        Assert.Equal(8, defaults.DigestHour);
        Assert.Equal([YojanaRole.ItemMasterOwner], defaults.MasterDataRecipientRoles);
        await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new(true, 24, [YojanaRole.ItemMasterOwner], [YojanaRole.InventoryController],
                defaults.SettingsVersion), Actor, default));
        await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new(true, 8, ["unknown_role"], [YojanaRole.InventoryController], defaults.SettingsVersion),
            Actor, default));
        await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new(true, 8, [YojanaRole.TenantAdministrator], [YojanaRole.InventoryController],
                defaults.SettingsVersion), Actor, default));
        var withoutAccess = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new(true, 8, [YojanaRole.Quality], [YojanaRole.InventoryController],
                defaults.SettingsVersion), Actor, default));
        Assert.Equal("erp_change_notification_role_without_access", withoutAccess.Rule);

        var saved = await settings.SetAsync(new(true, 7, [YojanaRole.InventoryController],
            [YojanaRole.InventoryController], defaults.SettingsVersion), Actor, default);
        Assert.Equal([YojanaRole.InventoryController], saved.MasterDataRecipientRoles);
        await scope.ServiceProvider.GetRequiredService<ErpChangeNotifications>()
            .StepOpenedAsync(setup.RunId, ErpChangeStep.MasterData, default);
        Assert.Equal(recipient, Assert.Single(fixture.Mail.Snapshot()).To.Address);

        var events = new List<ConnectionAuditEvent>();
        await foreach (var item in scope.ServiceProvider.GetRequiredService<IConnectionAuditSink>()
            .ReadAsync(setup.Tenant, new(2026, 10, 1), new(2026, 10, 31), default)) events.Add(item);
        var changed = Assert.Single(events, item =>
            item.EventType == ConnectionAuditEventType.ErpChangeNotificationSettingsChanged);
        var batch = Assert.Single(events, item => item.EventType == ConnectionAuditEventType.ErpChangeMailBatchQueued);
        Assert.DoesNotContain("@", changed.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", batch.DetailsJson, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(batch.DetailsJson);
        Assert.Equal(YojanaRole.InventoryController, detail.RootElement.GetProperty("recipient_role").GetString());
        Assert.Equal(1, detail.RootElement.GetProperty("recipient_count").GetInt32());
    }

    [DatabaseFact]
    public async Task My_work_counts_follow_the_callers_configured_roles_and_tenant_boundary()
    {
        fixture.Clock.UtcNow = new(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("ntwork-", ErpChangeStep.Stock);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ErpChangeStockDifferences.AddRange(
                Difference(setup, "PC-2509-0001", covered: true),
                Difference(setup, "PC-2509-0002", covered: false),
                Difference(setup, "PC-2509-0003", covered: false));
            await db.SaveChangesAsync();
            var work = await scope.ServiceProvider.GetRequiredService<ErpChangeWorkService>()
                .ForRolesAsync([YojanaRole.InventoryController], default);
            Assert.NotNull(work);
            Assert.Equal((1, 2, 3), (work.CoveredStockDifferences,
                work.OtherStockDifferencesAwaitingSignOff, work.Total));
            Assert.Equal("/integration/erp-change/stock", work.ScreenPath);
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<ErpChangeWorkService>()
                .ForRolesAsync([YojanaRole.ItemMasterOwner], default))!.Total);
        }

        var other = await fixture.NewTenantAsync("ntother-");
        using var otherScope = fixture.ScopeFor(other);
        Assert.Null(await otherScope.ServiceProvider.GetRequiredService<ErpChangeWorkService>()
            .ForRolesAsync([YojanaRole.InventoryController], default));
        Assert.Empty(await otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .ErpChangeNotices.AsNoTracking().ToListAsync());
    }

    [DatabaseFact]
    public async Task First_stock_divergence_mails_once_and_document_divergences_do_not_mail()
    {
        fixture.Mail.Reset();
        fixture.Clock.UtcNow = new(2026, 10, 7, 7, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("ntdiv-", ErpChangeStep.DualRun, dualRunStarted: true);
        await AddStaffAsync(setup.Tenant, YojanaRole.InventoryController,
            "stock@northstar.example", "Aarav Mehta");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var service = scope.ServiceProvider.GetRequiredService<ErpChangeDualRunService>();
        await service.ReportAsync(setup.RunId, new(ErpChangeDivergenceKind.Document, "delivery",
            null, "DN-100", "posted", "missing"), default);
        Assert.Empty(fixture.Mail.Snapshot());
        await service.ReportAsync(setup.RunId, new(ErpChangeDivergenceKind.Stock, "unit",
            null, "STOR-2509-1", "warehouse A", "warehouse B"), default);
        await service.ReportAsync(setup.RunId, new(ErpChangeDivergenceKind.Stock, "unit",
            null, "STOR-2509-2", "warehouse A", "warehouse C"), default);
        var mail = Assert.Single(fixture.Mail.Snapshot());
        Assert.Contains("https://yojana.example/integration/erp-change/stock", mail.HtmlBody,
            StringComparison.Ordinal);
        Assert.Equal("ERP change: stock divergences need you", mail.Subject);
    }

    private async Task<Setup> SetupAsync(string prefix, int step, bool dualRunStarted = false)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var source = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var target = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var runId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        (await db.Connections.SingleAsync(row => row.ConnectionId == source)).DisplayLabel = "Northstar Office Systems";
        (await db.Connections.SingleAsync(row => row.ConnectionId == target)).DisplayLabel = "Silverline Systems";
        db.ErpChangeRuns.Add(new()
        {
            TenantId = tenant, ErpChangeRunId = runId, SourceConnectionId = source,
            TargetConnectionId = target, CurrentStep = step, Status = ErpChangeRunStatus.Active,
            StartedBy = Actor, StartedAt = fixture.Clock.UtcNow.AddDays(-1),
            DualRunStartedAt = dualRunStarted ? fixture.Clock.UtcNow.AddDays(-1) : null,
        });
        await db.SaveChangesAsync();
        return new(tenant, source, target, runId);
    }

    private async Task<string> AddStaffAsync(TenantId tenant, string role, string email, string name)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var parts = email.Split('@', 2);
        var uniqueEmail = $"{parts[0]}-{tenant.Value:N}@{parts[1]}";
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = uniqueEmail, DisplayName = name, CreatedAt = fixture.Clock.UtcNow,
        };
        user.Memberships.Add(new()
        {
            UserId = user.Id, TenantId = tenant, Roles = [role], CreatedAt = fixture.Clock.UtcNow,
        });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return uniqueEmail;
    }

    private static ErpChangeMasterDataException Exception(Setup setup, string externalId) => new()
    {
        TenantId = setup.Tenant, ErpChangeMasterDataExceptionId = Guid.CreateVersion7(),
        ErpChangeRunId = setup.RunId, EntityKind = "item", TargetExternalId = externalId,
        DisplayName = "Fictional rack server", Problem = MasterDataProblem.Unmatched,
        CandidateModuleIdsJson = "[]", CandidateLabelsJson = "[]", LiveUnitsAffected = 1,
        Status = ErpChangeExceptionStatus.Open, CreatedAt = DateTime.SpecifyKind(new(2026, 9, 1), DateTimeKind.Utc),
    };

    private static ErpChangeStockDifference Difference(Setup setup, string identifier, bool covered) => new()
    {
        TenantId = setup.Tenant, ErpChangeStockDifferenceId = Guid.CreateVersion7(),
        ErpChangeRunId = setup.RunId, Identifier = identifier, ReasonClass = StockDifferenceReason.LocationDiffers,
        HasLiveEntitlement = covered, SuggestedResolution = "change_custody", Status = ErpChangeDifferenceStatus.Open,
        IsCurrent = true, ComparedAt = DateTime.SpecifyKind(new(2026, 9, 1), DateTimeKind.Utc),
    };

    private sealed record Setup(TenantId Tenant, Guid Source, Guid Target, Guid RunId);
}
