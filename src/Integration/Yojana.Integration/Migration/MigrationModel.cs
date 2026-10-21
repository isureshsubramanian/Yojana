using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration;

internal static class MigrationModel
{
    public const string Schema = "migration";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationSource>(builder =>
        {
            builder.ToTable("migration_source", Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_source_runtime",
                    $"runtime IN ('sql_server','{MigrationSourceRuntime.UnsupportedColumnar}')");
                table.HasCheckConstraint("ck_migration_source_route", "route IN ('separate','cross_database','linked_server')");
                table.HasCheckConstraint("ck_migration_source_licence", "licence_position IN ('unconfirmed','confirmed')");
                table.HasCheckConstraint("ck_migration_source_timeout", "command_timeout_seconds BETWEEN 1 AND 3600");
            });
            builder.HasKey(x => new { x.TenantId, x.ConnectionId });
            builder.Property(x => x.DisplayLabel).HasMaxLength(160);
            builder.Property(x => x.SourceSystem).HasMaxLength(40);
            builder.Property(x => x.Runtime).HasMaxLength(20);
            builder.Property(x => x.Route).HasMaxLength(24);
            builder.Property(x => x.DatabaseName).HasMaxLength(128);
            builder.Property(x => x.SchemaName).HasMaxLength(128);
            builder.Property(x => x.LinkedServerName).HasMaxLength(128);
            builder.Property(x => x.LicencePosition).HasMaxLength(20);
            builder.Property(x => x.LicenceConfirmedBy).HasMaxLength(320);
            builder.Property(x => x.LicenceContractReference).HasMaxLength(500);
            builder.Property(x => x.LicenceNote).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.SourceSystem }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.CreatedAt, x.ConnectionId });
        });
        modelBuilder.Entity<MigrationBatch>(builder =>
        {
            builder.ToTable("migration_batch", Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_batch_kind", "kind = 'full'");
                table.HasCheckConstraint("ck_migration_batch_status", "status IN ('queued','running','succeeded','failed','refused')");
            });
            builder.HasKey(x => new { x.TenantId, x.BatchId });
            builder.Property(x => x.SourceSystem).HasMaxLength(40);
            builder.Property(x => x.Kind).HasMaxLength(20);
            builder.Property(x => x.RequestedBy).HasMaxLength(320);
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.FailureText).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.RequestedAt, x.BatchId });
            // This filtered index is the database backstop for the worker's claim transaction. The worker and index
            // are coupled only by the literal running state; changing that state without this filter permits two
            // simultaneous full reads of one tenant/source.
            builder.HasIndex(x => new { x.TenantId, x.SourceSystem }).IsUnique()
                .HasFilter("[status] = 'running'");
            builder.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.BatchId });
            builder.HasOne<MigrationSource>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationBatchTable>(builder =>
        {
            builder.ToTable("migration_batch_table", Schema);
            builder.HasKey(x => new { x.TenantId, x.BatchId, x.SourceTable });
            builder.Property(x => x.SourceTable).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.SourceTable });
            builder.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        Staging.SourceProtocol.Configure(modelBuilder);
    }
}
