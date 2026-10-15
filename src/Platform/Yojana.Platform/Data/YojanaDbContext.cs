using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Data;

/// <summary>
/// Base context for every module. It applies the SQL Server conventions of MOD-01: snake_case names,
/// <c>datetime2(3)</c> for every <see cref="DateTime"/>, the <see cref="TenantId"/> converter, a global
/// query filter on every <see cref="ITenantScoped"/> entity, the rule that <c>tenant_id</c> leads every
/// primary key and index, and the session-context interceptor that feeds row-level security.
/// Modules derive from this class, own one schema each, and never map another module's tables.
/// </summary>
public abstract class YojanaDbContext : DbContext
{
    private readonly ITenantContext tenant;

    protected YojanaDbContext(DbContextOptions options, ITenantContext tenant) : base(options)
    {
        this.tenant = tenant;
    }

    /// <summary>The schema this context owns. Every table of the context lives in it.</summary>
    public abstract string Schema { get; }

    /// <summary>
    /// Tenant used by the query filters. <see cref="TenantId"/> with an empty value when the scope has
    /// no tenant, so a tenant-less query matches nothing instead of everything.
    /// </summary>
    public TenantId CurrentTenantId => tenant.HasTenant ? tenant.TenantId : default;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(new TenantSessionContextInterceptor(tenant));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("datetime2(3)");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            ApplyNaming(entity);
            ApplyTemporalPeriodColumns(entity);
            if (typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            {
                ApplyTenantFilter(modelBuilder, entity);
            }
        }
    }

    /// <summary>
    /// Enforces MOD-01 invariant 1 on the compiled model: every tenant-scoped entity's primary key and
    /// every index leads with <c>tenant_id</c>. Called by the host at startup for each context.
    /// </summary>
    public IReadOnlyList<string> ValidateTenantKeyConventions()
    {
        var failures = new List<string>();
        foreach (var entity in Model.GetEntityTypes().Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType)))
        {
            var key = entity.FindPrimaryKey();
            if (key is null || key.Properties[0].Name != nameof(ITenantScoped.TenantId))
            {
                failures.Add($"{entity.DisplayName()}: primary key must lead with TenantId.");
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.Properties[0].Name != nameof(ITenantScoped.TenantId))
                {
                    failures.Add($"{entity.DisplayName()}: index ({string.Join(", ", index.Properties.Select(p => p.Name))}) must lead with TenantId.");
                }
            }
        }

        return failures;
    }

    private static void ApplyNaming(IMutableEntityType entity)
    {
        if (entity.IsOwned() || entity.GetTableName() is not { } table)
        {
            return;
        }

        entity.SetTableName(Naming.ToSnakeCase(table));
        foreach (var property in entity.GetProperties())
        {
            property.SetColumnName(Naming.ToSnakeCase(property.Name));
        }

        foreach (var key in entity.GetKeys())
        {
            var prefix = key.IsPrimaryKey() ? "pk_" : "ak_";
            key.SetName(prefix + Naming.ToSnakeCase(table));
        }

        foreach (var foreignKey in entity.GetForeignKeys())
        {
            foreignKey.SetConstraintName("fk_" + Naming.ToSnakeCase(table) + "_" + string.Join("_", foreignKey.Properties.Select(p => Naming.ToSnakeCase(p.Name))));
        }

        foreach (var index in entity.GetIndexes())
        {
            index.SetDatabaseName((index.IsUnique ? "ux_" : "ix_") + Naming.ToSnakeCase(table) + "_" + string.Join("_", index.Properties.Select(p => Naming.ToSnakeCase(p.Name))));
        }
    }

    /// <summary>System-versioned tables need plain <c>datetime2</c> period columns; the datetime2(3) convention must not touch them.</summary>
    private static void ApplyTemporalPeriodColumns(IMutableEntityType entity)
    {
        if (!entity.IsTemporal())
        {
            return;
        }

        foreach (var name in new[] { entity.GetPeriodStartPropertyName(), entity.GetPeriodEndPropertyName() })
        {
            if (name is not null && entity.FindProperty(name) is { } period)
            {
                period.SetColumnType("datetime2");
                period.SetColumnName(name == entity.GetPeriodStartPropertyName() ? "sys_start" : "sys_end");
            }
        }
    }

    private void ApplyTenantFilter(ModelBuilder modelBuilder, IMutableEntityType entity)
    {
        var parameter = Expression.Parameter(entity.ClrType, "e");
        var body = Expression.Equal(
            Expression.Property(parameter, nameof(ITenantScoped.TenantId)),
            Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
        modelBuilder.Entity(entity.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
    }
}
