using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Reconstruction;

internal static class ReconstructionModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationReconstructionRun>(builder =>
        {
            builder.ToTable("migration_reconstruction_run", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_reconstruction_run_status",
                    "status IN ('queued','running','completed','failed')"));
            builder.HasKey(x => new { x.TenantId, x.RunId });
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.FailureCode).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.CanonicalisationRunId, x.RequestedAt, x.RunId });
            builder.HasOne<Canonicalisation.MigrationCanonRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CanonicalisationRunId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationHistoryEvent>(builder =>
        {
            builder.ToTable("migration_history_event", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_history_event_kind",
                    "movement_kind IN ('opening_balance','received','produced','consumed','shipped','customer_returned','vendor_returned','transferred','issued_out','unknown')");
                table.HasCheckConstraint("ck_migration_history_event_provenance",
                    "provenance IN ('source','reconstructed')");
            });
            builder.HasKey(x => new { x.TenantId, x.HistoryEventId });
            builder.Property(x => x.MovementKind).HasMaxLength(32);
            builder.Property(x => x.SourceDocumentTypeLabel).HasMaxLength(160);
            builder.Property(x => x.PartyCode).HasMaxLength(100);
            builder.Property(x => x.Warehouse).HasMaxLength(240);
            builder.Property(x => x.Quantity).HasPrecision(19, 6);
            builder.Property(x => x.Provenance).HasMaxLength(20);
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.CandidateId, x.Sequence }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.MovementKind });
            builder.HasOne<MigrationReconstructionRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReconstructionRunId }).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Canonicalisation.MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Canonicalisation.MigrationEventCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EventCandidateId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationOwnershipEpisode>(builder =>
        {
            builder.ToTable("migration_ownership_episode", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.OwnershipEpisodeId });
            builder.Property(x => x.PartyCode).HasMaxLength(100);
            builder.Property(x => x.OpenReason).HasMaxLength(80);
            builder.Property(x => x.CloseReason).HasMaxLength(80);
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.CandidateId,
                x.FromHistoryEventId });
            builder.HasIndex(x => new { x.TenantId, x.CandidateId, x.ToHistoryEventId });
            builder.HasOne<MigrationReconstructionRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReconstructionRunId }).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Canonicalisation.MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<MigrationHistoryEvent>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.FromHistoryEventId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<MigrationHistoryEvent>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ToHistoryEventId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationReconstructionLog>(builder =>
        {
            builder.ToTable("migration_reconstruction_log", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.LogId });
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.RelatedSourceKey).HasMaxLength(1000);
            builder.Property(x => x.ReasonCode).HasMaxLength(80);
            builder.Property(x => x.Note).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.CandidateId, x.LogId });
            builder.HasIndex(x => new { x.TenantId, x.ReconstructionRunId, x.ReasonCode, x.CandidateId });
            builder.HasOne<MigrationReconstructionRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReconstructionRunId }).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Canonicalisation.MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
