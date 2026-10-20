using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Messaging.Smoke;
using Yojana.Platform.Presence;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Scheduling.Smoke;
using Yojana.Platform.Security;
using Yojana.Platform.Seeding;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class MessagingAndSchedulingTests(PlatformHostFixture fixture)
{
    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private async Task<TenantId> NewTenantAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = new TenantId(Guid.NewGuid());
        db.Tenants.Add(new Tenant { Id = id, Code = ("m" + Guid.NewGuid().ToString("N"))[..20], Name = "messaging test", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<List<SmokeLog>> WaitForAsync(TenantId tenant, string source, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.SmokeLogs.Where(s => s.Source == source).ToListAsync();
            if (rows.Count > 0)
            {
                return rows;
            }

            await Task.Delay(250);
        }

        return [];
    }

    [DatabaseFact]
    public async Task A_message_published_through_the_outbox_is_handled_under_its_tenant()
    {
        var tenant = await NewTenantAsync();
        using (var scope = ScopeFor(tenant))
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<PlatformDbContext>>();
            outbox.DbContext.SmokeLogs.Add(new SmokeLog { TenantId = tenant, Id = TimeOrderedId.New(), Source = "request", Note = "ping", RecordedAt = DateTime.UtcNow });
            await outbox.PublishAsync(new SmokePing("ping"), new DeliveryOptions { TenantId = tenant.ToString() });
            await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);
        }

        var handled = await WaitForAsync(tenant, "handler", TimeSpan.FromSeconds(30));
        var row = Assert.Single(handled);
        Assert.Equal("ping", row.Note);
        Assert.Equal(tenant, row.TenantId);
    }

    [DatabaseFact]
    public async Task The_smoke_job_runs_from_the_sql_server_job_store_for_its_tenant()
    {
        var tenant = await NewTenantAsync();
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await fixture.Services.GetRequiredService<RecurringTenantJobScheduler>().EnsureScheduledAsync(scheduler, [tenant], CancellationToken.None);
        var key = TenantJob.KeyFor(SmokeHeartbeatJob.JobName, tenant);
        Assert.True(await scheduler.Exists(key, CancellationToken.None));
        var fireNow = TriggerBuilder.Create(TimeProvider.System).WithIdentity("now:" + tenant, key.Group).ForJob(key).StartNow().Build();
        await scheduler.ScheduleJob(fireNow, ScheduleJobOptions.Replacing, CancellationToken.None);

        var rows = await WaitForAsync(tenant, "job", TimeSpan.FromSeconds(60));
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(tenant, r.TenantId));
    }

    [DatabaseFact]
    public async Task Recurring_job_reconciliation_writes_only_missing_or_changed_schedules_at_tenant_volume()
    {
        const string heartbeatName = "test.recurring_heartbeat";
        const string cleanupName = "test.recurring_cleanup";
        const string changedCron = "0 1/5 * * * ?";
        var run = Guid.NewGuid().ToString("N")[..6];
        var tenants = Enumerable.Range(1, 1000).Select(index => new Tenant
        {
            Id = new TenantId(Guid.NewGuid()),
            Code = $"sched-{index:D4}-{run}",
            Name = $"Aster scheduling tenant {index}",
            CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        }).ToList();
        var allTenantIds = tenants.Select(tenant => tenant.Id).ToList();
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var reconciler = new RecurringTenantJobScheduler(
        [
            RecurringTenantJob.For<SmokeHeartbeatJob>(heartbeatName, SmokeHeartbeatJob.Cron),
            RecurringTenantJob.For<PresenceCleanupJob>(cleanupName, PresenceCleanupJob.Cron),
        ]);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenants.AddRange(tenants);
            await db.SaveChangesAsync();
        }

        try
        {
            var first = await reconciler.EnsureScheduledAsync(scheduler, allTenantIds, CancellationToken.None);
            Assert.Equal(2000, first.CreatedJobCount);
            Assert.Equal(0, first.UpdatedJobCount);

            var second = await reconciler.EnsureScheduledAsync(scheduler, allTenantIds, CancellationToken.None);
            Assert.Equal(0, second.ScheduledJobCount);
            Assert.Equal(2000, second.UnchangedJobCount);

            var changed = new RecurringTenantJobScheduler(
            [
                RecurringTenantJob.For<SmokeHeartbeatJob>(heartbeatName, changedCron),
                RecurringTenantJob.For<PresenceCleanupJob>(cleanupName, PresenceCleanupJob.Cron),
            ]);
            var cronResult = await changed.EnsureScheduledAsync(scheduler, allTenantIds, CancellationToken.None);
            Assert.Equal(1000, cronResult.UpdatedJobCount);
            Assert.Equal(1000, cronResult.UnchangedJobCount);
            var sample = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(
                new TriggerKey(heartbeatName, TenantJob.KeyFor(heartbeatName, allTenantIds[0]).Group)));
            Assert.Equal(changedCron, sample.CronExpressionString);

            var newTenant = new Tenant
            {
                Id = new TenantId(Guid.NewGuid()),
                Code = $"sched-new-{run}",
                Name = "Aster newly provisioned tenant",
                CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Tenants.Add(newTenant);
                await db.SaveChangesAsync();
            }

            allTenantIds.Add(newTenant.Id);
            var newTenantResult = await changed.EnsureScheduledAsync(scheduler, [newTenant.Id], CancellationToken.None);
            Assert.Equal(2, newTenantResult.CreatedJobCount);
            Assert.True(await scheduler.Exists(TenantJob.KeyFor(heartbeatName, newTenant.Id)));
            Assert.True(await scheduler.Exists(TenantJob.KeyFor(cleanupName, newTenant.Id)));
        }
        finally
        {
            var keys = allTenantIds.SelectMany(tenant => new[]
            {
                TenantJob.KeyFor(heartbeatName, tenant),
                TenantJob.KeyFor(cleanupName, tenant),
            }).ToList();
            await scheduler.DeleteJobs(keys, CancellationToken.None);
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Tenants.Where(tenant => allTenantIds.Contains(tenant.Id)).ExecuteDeleteAsync();
        }
    }

    [DatabaseFact]
    public async Task Seeding_is_idempotent_and_sign_in_yields_tenant_and_role_claims()
    {
        await DevelopmentSeeder.SeedAsync(fixture.Services, "Test!Pass2026", CancellationToken.None);
        await DevelopmentSeeder.SeedAsync(fixture.Services, "Test!Pass2026", CancellationToken.None);
        using var scope = fixture.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var request = new SecurityRequestContext($"seed-test-{Guid.NewGuid():N}", "test-agent");
        var user = (await signIn.AuthenticateAsync("service_manager@" + DevelopmentSeeder.EmailDomain,
            "Test!Pass2026", request, CancellationToken.None)).User;
        Assert.NotNull(user);
        Assert.Null((await signIn.AuthenticateAsync(user!.Email, "wrong", request, CancellationToken.None)).User);
        var tenants = await signIn.TenantsForAsync(user.Id, CancellationToken.None);
        var choice = Assert.Single(tenants);
        Assert.Equal(DevelopmentSeeder.DevTenantCode, choice.Code);
        var principal = SignInService.PrincipalFor(user, choice);
        Assert.Equal(DevelopmentSeeder.DevTenantId.ToString(), principal.FindFirst(Contracts.Security.YojanaClaim.TenantId)!.Value);
        Assert.Contains(principal.FindAll(Contracts.Security.YojanaClaim.Role), c => c.Value == Contracts.Security.YojanaRole.ServiceManager);
        Assert.True(principal.IsInRole(Contracts.Security.YojanaRole.ServiceManager));
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        Assert.True(await scheduler.Exists(TenantJob.KeyFor(SmokeHeartbeatJob.JobName, DevelopmentSeeder.DevTenantId)));
        Assert.True(await scheduler.Exists(TenantJob.KeyFor(PresenceCleanupJob.JobName, DevelopmentSeeder.DevTenantId)));
    }

    [DatabaseFact]
    public async Task Membership_party_reference_is_the_only_source_of_the_portal_party_claim()
    {
        var withParty = new TenantId(Guid.NewGuid());
        var withoutParty = new TenantId(Guid.NewGuid());
        var prefixedRole = new TenantId(Guid.NewGuid());
        var partyRef = Guid.NewGuid();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = $"portal-{Guid.NewGuid():N}@tests.local",
            DisplayName = "Fictional Portal Member",
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        };
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = withParty, Code = ("p" + Guid.NewGuid().ToString("N"))[..20], Name = "Party membership", CreatedAt = user.CreatedAt },
                new Tenant { Id = withoutParty, Code = ("n" + Guid.NewGuid().ToString("N"))[..20], Name = "No party membership", CreatedAt = user.CreatedAt },
                new Tenant { Id = prefixedRole, Code = ("r" + Guid.NewGuid().ToString("N"))[..20], Name = "Role marker membership", CreatedAt = user.CreatedAt });
            user.Memberships.Add(new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = withParty,
                CustomerPartyRef = partyRef,
                Roles = [YojanaRole.ServiceDesk],
            });
            user.Memberships.Add(new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = withoutParty,
                Roles = [YojanaRole.ServiceDesk],
            });
            user.Memberships.Add(new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = prefixedRole,
                Roles = [$"customer_party:{Guid.NewGuid():D}"],
            });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        using var signInScope = fixture.Services.CreateScope();
        var signIn = signInScope.ServiceProvider.GetRequiredService<SignInService>();
        var choices = await signIn.TenantsForAsync(user.Id, CancellationToken.None);

        var partyPrincipal = SignInService.PrincipalFor(user, choices.Single(choice => choice.TenantId == withParty));
        Assert.Equal(partyRef.ToString("D"), partyPrincipal.FindFirst(YojanaClaim.CustomerPartyRef)?.Value);
        Assert.Equal(YojanaRole.ServiceDesk, partyPrincipal.FindFirst(YojanaClaim.Role)?.Value);

        var noPartyPrincipal = SignInService.PrincipalFor(user, choices.Single(choice => choice.TenantId == withoutParty));
        Assert.Null(noPartyPrincipal.FindFirst(YojanaClaim.CustomerPartyRef));

        var prefixedRolePrincipal = SignInService.PrincipalFor(user, choices.Single(choice => choice.TenantId == prefixedRole));
        Assert.Null(prefixedRolePrincipal.FindFirst(YojanaClaim.CustomerPartyRef));
        Assert.DoesNotContain(prefixedRolePrincipal.FindAll(YojanaClaim.Role),
            claim => claim.Value.StartsWith("customer_party:", StringComparison.Ordinal));
    }
}
