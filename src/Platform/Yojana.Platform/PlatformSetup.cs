using System.Diagnostics;
using System.Reflection;
using JasperFx;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Shell;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Messaging;
using Yojana.Platform.Contracts.Presence;
using Yojana.Platform.Presence;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Scheduling.Smoke;
using Yojana.Platform.Security;
using Yojana.Platform.Shell;
using Yojana.Platform.Tenancy;
using Yojana.Platform.Time;

namespace Yojana.Platform;

/// <summary>
/// Composition of MOD-01. The host calls <see cref="AddYojanaPlatform"/> once, registers its modules,
/// then <see cref="UseYojanaPlatform"/>. Modules register their DbContexts through
/// <see cref="AddModuleDbContext{TContext}"/> so they inherit the conventions and are migrated in order.
/// </summary>
public static class PlatformSetup
{
    private static readonly List<Type> ModuleContexts = [];
    private static readonly List<Assembly> HandlerAssemblies = [];

    public static IHostApplicationBuilder AddYojanaPlatform(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>()
            ?? throw new InvalidOperationException($"Configuration section '{PlatformOptions.SectionName}' with ConnectionString is required.");
        options.ConnectionString = PlatformOptions.Resolve(options.ConnectionString, Environment.GetEnvironmentVariable, options.MaxPoolSize);
        builder.Services.AddSingleton(options);
        builder.Services.AddProblemDetails();

        // Hosted services start in registration order: migrations must precede the Quartz job store
        // (which validates its tables on start) and the recurring-job registrar.
        builder.Services.AddHostedService<StartupMigrator>();

        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<IPortalPartyContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<IPortalPartyContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddScoped<ITenantSettings, TenantSettings>();
        builder.Services.AddScoped<IPortalTenantResolver, PortalTenantResolver>();
        // A factory on purpose: Wolverine does not look inside one, so a module handler that takes ITenantCurrency does not
        // get PlatformDbContext (behind ITenantSettings) in its generated graph, which makes the host refuse to start
        // ("multiple DbContext types detected"). Coupled to the module handlers that read the currency (Serialisation
        // PolicyStore, AmendEntitlementHandler and SubmitClaimHandler); a plain AddScoped<ITenantCurrency, TenantCurrency>() breaks them.
        builder.Services.AddSingleton<ICurrencyCatalogue, RuntimeCurrencyCatalogue>();
        builder.Services.AddScoped<ITenantCurrency>(sp => new TenantCurrency(sp.GetRequiredService<ITenantSettings>(), sp.GetRequiredService<ICurrencyCatalogue>()));

        builder.Services.AddDbContextWithWolverineIntegration<PlatformDbContext>(
            db => db.UseSqlServer(options.ConnectionString, sql => SqlServerConventions(sql, PlatformDbContext.SchemaName)),
            options.WolverineSchema);
        ModuleContexts.Insert(0, typeof(PlatformDbContext));
        HandlerAssemblies.Add(typeof(PlatformSetup).Assembly);

        builder.Services.AddScoped<SignInService>();
        builder.Services.AddScoped<IPresenceService, PresenceService>();
        // A factory for the same reason as ITenantCurrency above: it keeps PlatformDbContext out of the generated graph of
        // any module handler that later takes IUserDirectory.
        builder.Services.AddScoped<IUserDirectory>(sp => new UserDirectory(sp.GetRequiredService<PlatformDbContext>(), sp.GetRequiredService<ITenantContext>()));
        // Shell contributions: modules add their own IOverviewContributor and IConnectionHealthSource registrations.
        builder.Services.AddScoped<IOverviewContributor, PlatformOverviewContributor>();
        builder.Services.AddSingleton<IConnectionHealthSource, NoConnectionsHealthSource>();
        builder.Services.AddScoped<ShellOverviewService>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "yojana.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(10);
                cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });
        builder.Services.AddAuthorization(auth =>
        {
            auth.AddPolicy(YojanaPolicy.TenantMember, p => p.RequireAuthenticatedUser().RequireClaim(YojanaClaim.TenantId));
            auth.AddPolicy(YojanaPolicy.CustomerPortal, p => p.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId).RequireClaim(YojanaClaim.CustomerPartyRef));
            foreach (var role in YojanaRole.All)
            {
                auth.AddPolicy(YojanaPolicy.ForRole(role), p => p.RequireAuthenticatedUser().RequireClaim(YojanaClaim.TenantId).RequireClaim(YojanaClaim.Role, role));
            }
            auth.AddPolicy(YojanaPolicy.IntegrationOperations, p => p.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator, YojanaRole.IntegrationOperator));
            auth.AddPolicy(YojanaPolicy.IntegrationIndicator, p => p.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator, YojanaRole.ServiceManager,
                    YojanaRole.IntegrationOperator));
            auth.AddPolicy(YojanaPolicy.LabelPrinter, p => p.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role,
                    YojanaRole.LabelAdministrator,
                    YojanaRole.Technician,
                    YojanaRole.InventoryController,
                    YojanaRole.ServiceDesk,
                    YojanaRole.ServiceSupervisor,
                    YojanaRole.ServiceManager,
                    YojanaRole.ProductionSupervisor,
                    YojanaRole.WarehouseLead,
                    YojanaRole.ShippingSupervisor));
            auth.AddPolicy(YojanaPolicy.CertificateIssuer, p => p.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.CertificateAdministrator, YojanaRole.Quality));
        });

        builder.Services.AddQuartz(q =>
        {
            q.ConfigureScheduler(scheduler =>
            {
                scheduler.InstanceName = options.ServiceName;
                scheduler.InstanceId = "AUTO";
            });
            q.UsePersistentStore(store =>
            {
                store.UseSqlServer(options.ConnectionString);
                store.ConfigureStore(ado => ado.TablePrefix = options.QuartzSchema + ".QRTZ_");
                store.UseSystemTextJsonSerializer(_ => { });
                // TIGHT COUPLING (recurring-job reconciliation lock)
                // WHAT: Quartz clustering and RecurringJobReconciliationLock must use this same SQL database.
                // WHY: clustering coordinates job firing but exposes no cluster-wide lock for application reconciliation.
                // WHAT BREAKS IF SEPARATED: two hosts can both observe a missing schedule and race to insert it.
                store.UseClustering(cluster => cluster.Enabled = true);
            });
        });
        builder.Services.AddQuartzHostedService(h => h.WaitForJobsToComplete = true);
        builder.Services.AddSingleton<RecurringTenantJobScheduler>();
        builder.Services.AddSingleton<RecurringJobReconciliationLock>();
        builder.Services.AddSingleton<RecurringTenantJobProvisioner>();
        builder.Services.AddSingleton(RecurringTenantJob.For<SmokeHeartbeatJob>(SmokeHeartbeatJob.JobName, SmokeHeartbeatJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<PresenceCleanupJob>(PresenceCleanupJob.JobName, PresenceCleanupJob.Cron));
        builder.Services.AddHostedService<RecurringJobsRegistrar>();

        ((IHostBuilder)new HostBuilderAdapter(builder)).UseWolverine(opts =>
        {
            opts.ServiceName = options.ServiceName;
            opts.UseRuntimeCompilation();
            // Handlers and middleware receive ITenantContext from the message scope. It is registered through a
            // factory (one TenantContext instance serves both ITenantContext and ITenantContextSetter), which
            // Wolverine can only honour by resolving from the scoped container; inline construction would give
            // the middleware and the DbContext two different instances and the tenant would never arrive.
            opts.ServiceLocationPolicy = JasperFx.CodeGeneration.Model.ServiceLocationPolicy.AlwaysAllowed;
            opts.PersistMessagesWithSqlServer(options.ConnectionString, options.WolverineSchema);
            // The tenant middleware is registered before the transaction policy on purpose: the transactional
            // middleware opens the DbContext connection, and the session-context interceptor must already know
            // the tenant at that moment, or row-level security blocks the handler's writes.
            opts.Policies.AddMiddleware(typeof(TenantEnvelopeMiddleware));
            opts.UseEntityFrameworkCoreTransactions();
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            // Optimistic concurrency (rowversion on unit_current, guarded closures in bulk ingestion) is designed as
            // "two units of work race, one fails at SaveChanges and retries from a fresh read". This is that retry, for
            // every handler, commands and background work alike: each attempt runs in a new scope with a new DbContext
            // (the generated handlers hold no DbContext between calls), the failed attempt's transaction has rolled back,
            // and commands replay by CommandId, so repeating one never acts twice. A handler may set a longer schedule of
            // its own (EntitlementCoverageChangedHandler does); chain rules are tried before this one.
            opts.OnException<DbUpdateConcurrencyException>(ConcurrencyRetryMetrics.Observe)
                .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500));
            opts.AutoBuildMessageStorageOnStartup = options.AutoBuildMessageStorage ? AutoCreate.CreateOrUpdate : AutoCreate.None;
            foreach (var assembly in HandlerAssemblies)
            {
                opts.Discovery.IncludeAssembly(assembly);
            }
        });

        return builder;
    }

    /// <summary>Registers a module's DbContext with the platform conventions and the Wolverine outbox mapping.</summary>
    public static IHostApplicationBuilder AddModuleDbContext<TContext>(this IHostApplicationBuilder builder, string schema)
        where TContext : YojanaDbContext
    {
        var options = builder.Services.Single(d => d.ServiceType == typeof(PlatformOptions)).ImplementationInstance as PlatformOptions
            ?? throw new InvalidOperationException("Call AddYojanaPlatform before AddModuleDbContext.");
        builder.Services.AddDbContextWithWolverineIntegration<TContext>(
            db => db.UseSqlServer(options.ConnectionString, sql => SqlServerConventions(sql, schema)),
            options.WolverineSchema);
        ModuleContexts.Add(typeof(TContext));
        return builder;
    }

    /// <summary>Registers a module context whose data must live outside the operational database.</summary>
    public static IHostApplicationBuilder AddModuleDbContext<TContext>(this IHostApplicationBuilder builder, string schema,
        string connectionString) where TContext : YojanaDbContext
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Database connection for {typeof(TContext).Name} is required.");
        builder.Services.AddDbContext<TContext>(db => db.UseSqlServer(connectionString,
            sql => SqlServerConventions(sql, schema)));
        ModuleContexts.Add(typeof(TContext));
        return builder;
    }

    // Collections in LINQ (ids.Contains(x)) go to the server as ONE json parameter read with OPENJSON, not one
    // parameter per element. With one parameter per element a 500-serial document compiled 1–3 s per query on every run
    // (Query Store, bench run 2026-10-16 10:53) — more than the queries took to execute; one parameter gives one small
    // plan per query shape whatever the list length.
    private static void SqlServerConventions(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder sql, string schema) => sql
        .MigrationsHistoryTable("__migrations", schema)
        .UseParameterizedCollectionMode(ParameterTranslationMode.Parameter);

    /// <summary>Includes a module assembly in Wolverine handler discovery. Call before AddYojanaPlatform.</summary>
    public static void IncludeHandlers(Assembly assembly) => HandlerAssemblies.Add(assembly);

    public static WebApplication UseYojanaPlatform(this WebApplication app)
    {
        // Only an exception that has escaped Wolverine's complete retry schedule reaches this boundary.
        app.UseMiddleware<ConcurrencyRetryExhaustionMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();
        return app;
    }

    /// <summary>Validates every registered context against MOD-01 invariant 1 and applies migrations in registration order (platform first).</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Yojana.Platform.Migrations");
        foreach (var contextType in ModuleContexts)
        {
            var db = (YojanaDbContext)scope.ServiceProvider.GetRequiredService(contextType);
            var failures = db.ValidateTenantKeyConventions();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException($"{contextType.Name} violates the tenant key convention:\n" + string.Join('\n', failures));
            }

            logger.LogInformation("Migrating {Context} (schema {Schema})", contextType.Name, db.Schema);
            // A migration may build an index over a ledger of a hundred million rows (P1-T21: minutes on a laptop, far past the
            // 30 s default that made the first attempt fail); migrations get the time they need, requests keep the default.
            db.Database.SetCommandTimeout(TimeSpan.FromHours(4));
            await db.Database.MigrateAsync(cancellationToken);
        }
        foreach (var migration in scope.ServiceProvider.GetServices<IAdditionalDatabaseMigration>())
        {
            logger.LogInformation("Migrating {Database}", migration.Name);
            await migration.MigrateAsync(cancellationToken);
        }
    }

    public static IReadOnlyList<Type> RegisteredContexts => ModuleContexts;

    /// <summary>Bridges IHostApplicationBuilder to the IHostBuilder surface Wolverine configures through.</summary>
    private sealed class HostBuilderAdapter(IHostApplicationBuilder builder) : IHostBuilder
    {
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        public IHost Build() => throw new NotSupportedException();

        public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, Microsoft.Extensions.Configuration.IConfigurationBuilder> configureDelegate) => throw new NotSupportedException();

        public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate) => throw new NotSupportedException();

        public IHostBuilder ConfigureHostConfiguration(Action<Microsoft.Extensions.Configuration.IConfigurationBuilder> configureDelegate) => throw new NotSupportedException();

        public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            var context = new HostBuilderContext(Properties) { Configuration = builder.Configuration, HostingEnvironment = builder.Environment };
            configureDelegate(context, builder.Services);
            return this;
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory) where TContainerBuilder : notnull => throw new NotSupportedException();

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory) where TContainerBuilder : notnull => throw new NotSupportedException();
    }
}

/// <summary>
/// A database outside the module contexts that a module still has to migrate (for example one of several configured
/// archive databases sharing one model). Runs in <see cref="PlatformSetup.MigrateAsync"/> after the module contexts,
/// so it follows the same switch as everything else: MigrateOnStartup in development and tests, --migrate on deploy.
/// </summary>
public interface IAdditionalDatabaseMigration
{
    string Name { get; }
    Task MigrateAsync(CancellationToken cancellationToken);
}

/// <summary>First hosted service: applies migrations when <see cref="PlatformOptions.MigrateOnStartup"/> is set (development and tests).</summary>
public sealed class StartupMigrator(IServiceProvider services, PlatformOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.MigrateOnStartup)
        {
            await PlatformSetup.MigrateAsync(services, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>After the host starts accepting requests, reconciles recurring jobs for every active tenant.</summary>
public sealed class RecurringJobsRegistrar(
    IServiceProvider services,
    ISchedulerFactory schedulerFactory,
    RecurringTenantJobScheduler scheduler,
    RecurringJobReconciliationLock reconciliationLock,
    IHostApplicationLifetime applicationLifetime,
    ILogger<RecurringJobsRegistrar> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(applicationLifetime, stoppingToken);
            var started = Stopwatch.GetTimestamp();
            await using var lease = await reconciliationLock.AcquireAsync(stoppingToken);
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenants = await db.Tenants.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(stoppingToken);
            var quartz = await schedulerFactory.GetScheduler(stoppingToken);
            var result = await scheduler.EnsureScheduledAsync(quartz, tenants, stoppingToken);
            logger.LogInformation(
                "Recurring jobs reconciled for {TenantCount} tenant(s) in {ElapsedMilliseconds} ms: {CreatedCount} created, {UpdatedCount} updated, {UnchangedCount} unchanged",
                result.TenantCount,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.CreatedJobCount,
                result.UpdatedJobCount,
                result.UnchangedJobCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown cancels an in-flight reconciliation.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Recurring job reconciliation failed after host startup");
        }
    }

    private static async Task WaitForApplicationStartedAsync(IHostApplicationLifetime lifetime, CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), started);
        using var stoppingRegistration = stoppingToken.Register(static state =>
            ((TaskCompletionSource)state!).TrySetCanceled(), started);
        await started.Task;
    }
}
