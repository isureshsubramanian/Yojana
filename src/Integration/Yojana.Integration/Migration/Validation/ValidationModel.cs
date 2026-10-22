using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Validation;

internal static class ValidationModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationValidationRun>(b =>
        {
            b.ToTable("migration_validation_run", MigrationModel.Schema, t => t.HasCheckConstraint(
                "ck_migration_validation_run_status", "status IN ('queued','running','completed','failed')"));
            b.HasKey(x => new { x.TenantId, x.RunId });
            b.Property(x => x.TargetKind).HasMaxLength(20);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.FailureCode).HasMaxLength(100);
            b.Property(x => x.FailureDetail).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.BatchId, x.RequestedAt, x.RunId });
            b.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.RunId });
            b.HasIndex(x => new { x.TenantId, x.MapRunId, x.RunId });
            b.HasOne<Mapping.MigrationMapRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.MapRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationValidationCheck>(b =>
        {
            b.ToTable("migration_validation_check", MigrationModel.Schema, t => t.HasCheckConstraint(
                "ck_migration_validation_check_result", "result IN ('pass','explained','fail','hard_fail','report')"));
            b.HasKey(x => new { x.TenantId, x.CheckId });
            b.Property(x => x.Code).HasMaxLength(80);
            b.Property(x => x.Label).HasMaxLength(200);
            b.Property(x => x.LimitKind).HasMaxLength(80);
            b.Property(x => x.Difference).HasPrecision(19, 6);
            b.Property(x => x.Result).HasMaxLength(20);
            b.Property(x => x.OriginalResult).HasMaxLength(20);
            b.Property(x => x.Explanation).HasMaxLength(4000);
            b.Property(x => x.HowCounted).HasMaxLength(1000);
            b.Property(x => x.ResultFingerprint).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.ExplainedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.RunId, x.Code }).IsUnique();
            b.HasOne<MigrationValidationRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.RunId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationValidationCheckPart>(b =>
        {
            b.ToTable("migration_validation_check_part", MigrationModel.Schema);
            b.HasKey(x => new { x.TenantId, x.PartId });
            b.Property(x => x.Code).HasMaxLength(80);
            b.Property(x => x.Label).HasMaxLength(200);
            b.Property(x => x.Value).HasPrecision(19, 6);
            b.HasIndex(x => new { x.TenantId, x.CheckId, x.Code }).IsUnique();
            b.HasOne<MigrationValidationCheck>().WithMany().HasForeignKey(x => new { x.TenantId, x.CheckId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationValidationDifference>(b =>
        {
            b.ToTable("migration_validation_difference", MigrationModel.Schema);
            b.HasKey(x => new { x.TenantId, x.DifferenceId });
            b.Property(x => x.Kind).HasMaxLength(40);
            b.Property(x => x.ItemCode).HasMaxLength(100);
            b.Property(x => x.WarehouseCode).HasMaxLength(100);
            b.Property(x => x.Serial).HasMaxLength(500);
            b.Property(x => x.SourceValue).HasPrecision(19, 6);
            b.Property(x => x.TargetValue).HasPrecision(19, 6);
            b.Property(x => x.Difference).HasPrecision(19, 6);
            b.Property(x => x.Detail).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.CheckId, x.DifferenceId });
            b.HasOne<MigrationValidationCheck>().WithMany().HasForeignKey(x => new { x.TenantId, x.CheckId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationValidationSample>(b =>
        {
            b.ToTable("migration_validation_sample", MigrationModel.Schema, t => t.HasCheckConstraint(
                "ck_migration_validation_sample_outcome", "outcome IS NULL OR outcome IN ('traced','mismatch')"));
            b.HasKey(x => new { x.TenantId, x.SampleId });
            b.Property(x => x.Serial).HasMaxLength(500);
            b.Property(x => x.ItemCode).HasMaxLength(100);
            b.Property(x => x.ItemFamily).HasMaxLength(200);
            b.Property(x => x.AgeBand).HasMaxLength(40);
            b.Property(x => x.SourceTrailJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.TargetTrailJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Outcome).HasMaxLength(20);
            b.Property(x => x.Note).HasMaxLength(2000);
            b.Property(x => x.TracedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.RunId, x.SampleId });
            b.HasIndex(x => new { x.TenantId, x.CheckId, x.SampleId });
            b.HasOne<MigrationValidationCheck>().WithMany().HasForeignKey(x => new { x.TenantId, x.CheckId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationValidationSignature>(b =>
        {
            b.ToTable("migration_validation_signature", MigrationModel.Schema);
            b.HasKey(x => new { x.TenantId, x.SignatureId });
            b.Property(x => x.Role).HasMaxLength(20);
            b.Property(x => x.Actor).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.CheckId, x.Role, x.Superseded, x.SignatureId });
            b.HasOne<MigrationValidationCheck>().WithMany().HasForeignKey(x => new { x.TenantId, x.CheckId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationSignoffRecord>(b =>
        {
            b.ToTable("migration_signoff_record", MigrationModel.Schema);
            b.HasKey(x => new { x.TenantId, x.RecordId });
            b.Property(x => x.CreatedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.RunId }).IsUnique();
            b.HasOne<MigrationValidationRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.RunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationSignoffLine>(b =>
        {
            b.ToTable("migration_signoff_line", MigrationModel.Schema);
            b.HasKey(x => new { x.TenantId, x.LineId });
            b.Property(x => x.ReportName).HasMaxLength(200);
            b.Property(x => x.Difference).HasPrecision(19, 6);
            b.Property(x => x.Explanation).HasMaxLength(4000);
            b.Property(x => x.Result).HasMaxLength(20);
            b.Property(x => x.Preparer).HasMaxLength(320);
            b.Property(x => x.Reviewer).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.RecordId, x.Position }).IsUnique();
            b.HasOne<MigrationSignoffRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.RecordId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
