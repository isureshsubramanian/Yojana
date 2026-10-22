using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Mapping;

internal static class MappingModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationMapRun>(builder =>
        {
            builder.ToTable("migration_map_run", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_map_run_status", "status IN ('queued','running','completed','failed')"));
            builder.HasKey(x => new { x.TenantId, x.RunId });
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.FailureCode).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.RequestedAt, x.RunId });
            builder.HasOne<Reconstruction.MigrationReconstructionRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReconstructionRunId }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationPlanUnit>(builder =>
        {
            builder.ToTable("migration_plan_unit", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanUnitId });
            builder.Property(x => x.ItemCode).HasMaxLength(100);
            builder.Property(x => x.ItemName).HasMaxLength(500);
            builder.Property(x => x.Origin).HasMaxLength(32);
            builder.Property(x => x.HistoryDepth).HasMaxLength(24);
            builder.Property(x => x.LocationState).HasMaxLength(24);
            builder.Property(x => x.WarehouseCode).HasMaxLength(100);
            builder.Property(x => x.CustodyLocationKind).HasMaxLength(40);
            builder.Property(x => x.ConditionGrade).HasMaxLength(40);
            builder.Property(x => x.ConditionProvenance).HasMaxLength(20);
            builder.Property(x => x.Note).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.CandidateId }).IsUnique();
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.SourceKeyHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.SourceKeyHash });
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.ItemCode, x.PlanUnitId });
            builder.HasOne<MigrationMapRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.MapRunId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationPlanIdentifier>(builder =>
        {
            builder.ToTable("migration_plan_identifier", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanIdentifierId });
            builder.Property(x => x.IdentifierType).HasMaxLength(40);
            builder.Property(x => x.RawValue).HasMaxLength(500);
            builder.Property(x => x.IssuerKind).HasMaxLength(32);
            builder.Property(x => x.SourceDataValue).HasMaxLength(500);
            builder.Property(x => x.Provenance).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.PlanIdentifierId });
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanEvent>(builder =>
        {
            builder.ToTable("migration_plan_event", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanEventId });
            builder.Property(x => x.MovementKind).HasMaxLength(32);
            builder.Property(x => x.DocumentTypeLabel).HasMaxLength(160);
            builder.Property(x => x.PartyCode).HasMaxLength(100);
            builder.Property(x => x.WarehouseCode).HasMaxLength(240);
            builder.Property(x => x.Quantity).HasPrecision(19, 6);
            builder.Property(x => x.Provenance).HasMaxLength(20);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.Sequence }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.Provenance, x.PlanEventId });
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanOwnership>(builder =>
        {
            builder.ToTable("migration_plan_ownership", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanOwnershipId });
            builder.Property(x => x.Tier).HasMaxLength(32);
            builder.Property(x => x.PartyCode).HasMaxLength(100);
            builder.Property(x => x.PartyName).HasMaxLength(320);
            builder.Property(x => x.PartyReference).HasMaxLength(100);
            builder.Property(x => x.EvidenceSource).HasMaxLength(32);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.PlanOwnershipId });
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanReplacementLink>(builder =>
        {
            builder.ToTable("migration_plan_replacement_link", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanReplacementLinkId });
            builder.Property(x => x.PolicyApplied).HasMaxLength(40);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.ReplacedPlanUnitId, x.ReplacementPlanUnitId }).IsUnique();
            builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacedPlanUnitId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacementPlanUnitId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationPlanEntitlement>(builder =>
        {
            builder.ToTable("migration_plan_entitlement", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanEntitlementId });
            builder.Property(x => x.CoverageFamily).HasMaxLength(100);
            builder.Property(x => x.Direction).HasMaxLength(24);
            builder.Property(x => x.Provenance).HasMaxLength(24);
            builder.Property(x => x.DateSource).HasMaxLength(32);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.CoverageFamily, x.PlanEntitlementId });
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanWarrantyEvidence>(builder =>
        {
            builder.ToTable("migration_plan_warranty_evidence", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanWarrantyEvidenceId });
            builder.Property(x => x.SourceKind).HasMaxLength(32);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.Direction).HasMaxLength(24);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.SourceKind, x.PlanWarrantyEvidenceId });
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanAttachment>(builder =>
        {
            builder.ToTable("migration_plan_attachment", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanAttachmentId });
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.Path).HasMaxLength(2000);
            builder.Property(x => x.FileName).HasMaxLength(500);
            builder.Property(x => x.ContentHash).HasMaxLength(128);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.PlanAttachmentId });
            builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanUnitId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationPlanCertificate>(builder =>
        {
            builder.ToTable("migration_plan_certificate", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanCertificateId });
            builder.Property(x => x.Provenance).HasMaxLength(24);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId }).IsUnique();
            UnitForeignKey(builder, x => new { x.TenantId, x.PlanUnitId });
        });
        modelBuilder.Entity<MigrationPlanGap>(builder =>
        {
            builder.ToTable("migration_plan_gap", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanGapId });
            builder.Property(x => x.Kind).HasMaxLength(80);
            builder.Property(x => x.Reason).HasMaxLength(2000);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.Kind, x.PlanGapId });
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.PlanGapId });
            builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanUnitId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationPlanLog>(builder =>
        {
            builder.ToTable("migration_plan_log", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.PlanLogId });
            builder.Property(x => x.Kind).HasMaxLength(80);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.RelatedSourceKey).HasMaxLength(1000);
            builder.Property(x => x.Note).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.MapRunId, x.PlanUnitId, x.PlanLogId });
            builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlanUnitId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void UnitForeignKey<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, object?>> foreignKey) where TEntity : class =>
        builder.HasOne<MigrationPlanUnit>().WithMany().HasForeignKey(foreignKey).OnDelete(DeleteBehavior.Restrict);
}
