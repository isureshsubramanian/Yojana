using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

internal static class CanonicalisationModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(CanonicalSerialNormalisation).GetMethod(
                nameof(CanonicalSerialNormalisation.Apply), [typeof(string), typeof(bool), typeof(bool), typeof(bool)])!)
            .HasName("canonical_serial").HasSchema(MigrationModel.Schema);
        modelBuilder.Entity<MigrationCanonRun>(builder =>
        {
            builder.ToTable("migration_canon_run", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_canon_run_status",
                    "status IN ('queued','running','completed','failed')"));
            builder.HasKey(x => new { x.TenantId, x.RunId });
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.FailureCode).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.ProfileRunId, x.RunId });
            builder.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<MigrationProfileRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationUnitCandidate>(builder =>
        {
            builder.ToTable("migration_unit_candidate", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_unit_candidate_rule",
                    "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                table.HasCheckConstraint("ck_migration_unit_candidate_provenance",
                    "provenance IN ('source','unverified')");
                table.HasCheckConstraint("ck_migration_unit_candidate_disposition",
                    "disposition IN ('active','review','merged','excluded')");
                table.HasCheckConstraint("ck_migration_unit_candidate_history_depth",
                    "history_depth IS NULL OR history_depth IN ('full','partial','opening_balance_only')");
            });
            builder.HasKey(x => new { x.TenantId, x.CandidateId });
            builder.Property(x => x.PrimarySourceKey).HasMaxLength(1000);
            builder.Property(x => x.ItemCode).HasMaxLength(100);
            builder.Property(x => x.ItemName).HasMaxLength(500);
            builder.Property(x => x.Family).HasMaxLength(100);
            builder.Property(x => x.CanonicalSerial).HasMaxLength(500);
            builder.Property(x => x.StoredSerial).HasMaxLength(500);
            builder.Property(x => x.TrimmedSerial).HasMaxLength(500);
            builder.Property(x => x.FamilyNormalisedSerial).HasMaxLength(500);
            builder.Property(x => x.MatchRule).HasMaxLength(32);
            builder.Property(x => x.Provenance).HasMaxLength(20);
            builder.Property(x => x.Disposition).HasMaxLength(20);
            builder.Property(x => x.HistoryDepth).HasMaxLength(24);
            builder.Property(x => x.Note).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.PrimarySourceKey }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.ItemCode, x.CanonicalSerial });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.ItemCode, x.StoredSerial });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.ItemCode, x.TrimmedSerial });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.ItemCode, x.FamilyNormalisedSerial });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.Family, x.FamilyNormalisedSerial });
            builder.HasOne<MigrationCanonRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.RunId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationUnitCandidateSource>(builder =>
        {
            builder.ToTable("migration_unit_candidate_source", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_unit_candidate_source_kind",
                    "source_kind IN ('serial_master','equipment_card','contract','contract_line')");
                table.HasCheckConstraint("ck_migration_unit_candidate_source_role",
                    "role IN ('kept','merged')");
            });
            builder.HasKey(x => new { x.TenantId, x.CandidateSourceId });
            builder.Property(x => x.MatchedSerialSourceKey).HasMaxLength(1000);
            builder.Property(x => x.SourceKind).HasMaxLength(32);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.Role).HasMaxLength(20);
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.CandidateId, x.SourceKind, x.SourceKey }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.CandidateId });
            builder.HasIndex(x => new { x.TenantId, x.CandidateId, x.SourceKind, x.Role })
                .IncludeProperties(x => x.SourceKey);
            builder.HasOne<MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationEventCandidate>(builder =>
        {
            builder.ToTable("migration_event_candidate", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_event_candidate_rule",
                    "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                table.HasCheckConstraint("ck_migration_event_candidate_provenance",
                    "provenance IN ('source','unverified')");
            });
            builder.HasKey(x => new { x.TenantId, x.EventCandidateId });
            builder.Property(x => x.SerialMasterSourceKey).HasMaxLength(1000);
            builder.Property(x => x.TransactionSourceKey).HasMaxLength(1000);
            builder.Property(x => x.MatchRule).HasMaxLength(32);
            builder.Property(x => x.Provenance).HasMaxLength(20);
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.TransactionSourceKey }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.CandidateId });
            builder.HasOne<MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationEntitlementCandidate>(builder =>
        {
            builder.ToTable("migration_entitlement_candidate", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_entitlement_candidate_rule",
                    "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                table.HasCheckConstraint("ck_migration_entitlement_candidate_provenance",
                    "provenance IN ('source','unverified')");
                table.HasCheckConstraint("ck_migration_entitlement_candidate_source_kind",
                    "source_kind IN ('equipment_card','contract','contract_line')");
            });
            builder.HasKey(x => new { x.TenantId, x.EntitlementCandidateId });
            builder.Property(x => x.SerialMasterSourceKey).HasMaxLength(1000);
            builder.Property(x => x.SourceKind).HasMaxLength(32);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.MatchRule).HasMaxLength(32);
            builder.Property(x => x.Provenance).HasMaxLength(20);
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.CandidateId, x.SourceKind, x.SourceKey }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.CandidateId });
            builder.HasOne<MigrationUnitCandidate>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CandidateId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MigrationReviewItem>(builder =>
        {
            builder.ToTable("migration_review_item", MigrationModel.Schema, table =>
            {
                table.HasCheckConstraint("ck_migration_review_item_kind",
                    "kind IN ('same_serial_one_item','same_serial_different_items','orphan_equipment_card','serial_in_user_field','history_needs_review')");
                table.HasCheckConstraint("ck_migration_review_item_status",
                    "status IN ('waiting','decided','carried_over')");
            });
            builder.HasKey(x => new { x.TenantId, x.ReviewItemId });
            builder.Property(x => x.SourceSystem).HasMaxLength(40);
            builder.Property(x => x.Kind).HasMaxLength(40);
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            builder.Property(x => x.CorrelationKey).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            builder.Property(x => x.CanonicalKey).HasMaxLength(500);
            builder.Property(x => x.ItemCode).HasMaxLength(100);
            builder.Property(x => x.ItemName).HasMaxLength(500);
            builder.Property(x => x.Family).HasMaxLength(100);
            builder.Property(x => x.StoredSerial).HasMaxLength(500);
            builder.Property(x => x.RuleTried).HasMaxLength(32);
            builder.Property(x => x.SourceKeysJson).HasColumnType("nvarchar(max)");
            builder.Property(x => x.CandidateSourceKeysJson).HasColumnType("nvarchar(max)");
            builder.Property(x => x.CandidateSummary).HasMaxLength(2000);
            builder.Property(x => x.Suggestion).HasMaxLength(32);
            builder.Property(x => x.SuggestedKeepSourceKey).HasMaxLength(1000);
            builder.Property(x => x.SuggestionReason).HasMaxLength(2000);
            builder.Property(x => x.Hint).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.Status, x.CreatedAt, x.ReviewItemId });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.Kind, x.Family, x.Status, x.CreatedAt,
                x.ReviewItemId });
            builder.HasIndex(x => new { x.TenantId, x.Fingerprint, x.CreatedAt, x.ReviewItemId });
            builder.HasIndex(x => new { x.TenantId, x.CorrelationKey, x.CreatedAt, x.ReviewItemId });
            builder.HasOne<MigrationCanonRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.RunId })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationReviewDecision>(builder =>
        {
            builder.ToTable("migration_review_decision", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_review_decision_kind",
                    "decision IN ('merge_into','keep_apart','fix_in_incumbent','accept_as_recorded','exclude_from_history')"));
            builder.HasKey(x => new { x.TenantId, x.DecisionId });
            builder.Property(x => x.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            builder.Property(x => x.CorrelationKey).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            builder.Property(x => x.Decision).HasMaxLength(32);
            builder.Property(x => x.KeepSourceKey).HasMaxLength(1000);
            builder.Property(x => x.Reason).HasMaxLength(2000);
            builder.Property(x => x.Note).HasMaxLength(2000);
            builder.Property(x => x.DecidedBy).HasMaxLength(320);
            builder.HasIndex(x => new { x.TenantId, x.ReviewItemId, x.DecidedAt, x.DecisionId });
            builder.HasIndex(x => new { x.TenantId, x.Fingerprint, x.DecidedAt, x.DecisionId });
            builder.HasIndex(x => new { x.TenantId, x.CorrelationKey, x.DecidedAt, x.DecisionId });
            builder.HasOne<MigrationReviewItem>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReviewItemId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
