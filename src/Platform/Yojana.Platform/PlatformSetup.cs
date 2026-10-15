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
using Wolverine.SqlServer;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Shell;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Messaging;
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
        options.ConnectionString = PlatformOptions.Resolve(options.ConnectionString, Environment.GetEnvironmentVariable);
        builder.Services.AddSingleton(options);

        // Hosted services start in registration order: migrations must precede the Quartz job store
        // (which validates its tables on start) and the recurring-job registrar.
        builder.Services.AddHostedService<StartupMigrator>();

        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddSingleton<IClock, SystemClock>();

        builder.Services.AddDbContextWithWolverineIntegration<PlatformDbContext>(
            db => db.UseSqlServer(options.ConnectionString, sql => sql.MigrationsHistoryTable("__migrations", PlatformDbContext.SchemaName)),
            options.WolverineSchema);
        ModuleContexts.Insert(0, typeof(PlatformDbContext));
        HandlerAssemblies.Add(typeof(PlatformSetup).Assembly);

        builder.Services.AddScoped<SignInService>();
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
            foreach (var role in YojanaRole.All)
            {
                auth.AddPolicy(YojanaPolicy.ForRole(role), p => p.RequireAuthenticatedUser().RequireClaim(YojanaClaim.TenantId).RequireClaim(YojanaClaim.Role, role));
            }
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
                store.UseClustering(cluster => cluster.Enabled = true);
            });
        });
        builder.Services.AddQuartzHostedService(h => h.WaitForJobsToComplete = true);
        builder.Services.AddSingleton<RecurringTenantJobScheduler>();
        builder.Services.AddSingleton(RecurringTenantJob.For<SmokeHeartbeatJob>(SmokeHeartbeatJob.JobName, SmokeHeartbeatJob.Cron));
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
            db => db.UseSqlServer(options.ConnectionString, sql => sql.MigrationsHistoryTable("__migrations", schema)),
            options.WolverineSchema);
        ModuleContexts.Add(typeof(TContext));
        return builder;
    }

    /// <summary>Includes a module assembly in Wolverine handler discovery. Call before AddYojanaPlatform.</summary>
    public static void IncludeHandlers(Assembly assembly) => HandlerAssemblies.Add(assembly);

    public static WebApplication UseYojanaPlatform(this WebApplication app)
    {
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
            await db.Database.MigrateAsync(cancellationToken);
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

/// <summary>At start, after migrations and the scheduler: every registered recurring job exists for every active tenant.</summary>
public sealed class RecurringJobsRegistrar(IServiceProvider services, ISchedulerFactory schedulerFactory, RecurringTenantJobScheduler scheduler, ILogger<RecurringJobsRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(cancellationToken);
        var quartz = await schedulerFactory.GetScheduler(cancellationToken);
        await scheduler.EnsureScheduledAsync(quartz, tenants, cancellationToken);
        logger.LogInformation("Recurring jobs ensured for {Count} tenant(s)", tenants.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
