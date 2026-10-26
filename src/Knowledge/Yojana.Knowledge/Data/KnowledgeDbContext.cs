using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;

namespace Yojana.Knowledge.Data;

public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "knowledge";
    public override string Schema => SchemaName;

    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionReply> Replies => Set<QuestionReply>();
    public DbSet<QuestionReplyRevision> Revisions => Set<QuestionReplyRevision>();
    public DbSet<QuestionParticipant> Participants => Set<QuestionParticipant>();
    public DbSet<QuestionSimilarEntry> SimilarEntries => Set<QuestionSimilarEntry>();
    public DbSet<KnowledgeEntry> Entries => Set<KnowledgeEntry>();
    public DbSet<KnowledgeEntryVersion> EntryVersions => Set<KnowledgeEntryVersion>();
    public DbSet<KnowledgeEntryTag> EntryTags => Set<KnowledgeEntryTag>();
    public DbSet<KnowledgeEntryRecordLink> EntryRecordLinks => Set<KnowledgeEntryRecordLink>();
    public DbSet<KnowledgeEntryReviewRound> EntryReviewRounds => Set<KnowledgeEntryReviewRound>();
    public DbSet<KnowledgeEntryWrittenAgainst> EntryWrittenAgainst => Set<KnowledgeEntryWrittenAgainst>();
    public DbSet<KnowledgeEntryReviewReason> EntryReviewReasons => Set<KnowledgeEntryReviewReason>();
    public DbSet<KnowledgeScreenChange> ScreenChanges => Set<KnowledgeScreenChange>();
    public DbSet<TrainingPath> TrainingPaths => Set<TrainingPath>();
    public DbSet<TrainingPathVersion> TrainingPathVersions => Set<TrainingPathVersion>();
    public DbSet<TrainingPathEntry> TrainingPathEntries => Set<TrainingPathEntry>();
    public DbSet<TrainingProgress> TrainingProgress => Set<TrainingProgress>();
    public DbSet<TrainingWelcomeDismissal> TrainingWelcomeDismissals => Set<TrainingWelcomeDismissal>();
    public DbSet<QuestionNotificationPreference> NotificationPreferences => Set<QuestionNotificationPreference>();
    public DbSet<QuestionNotification> Notifications => Set<QuestionNotification>();
    public DbSet<QuestionDigest> Digests => Set<QuestionDigest>();
    public DbSet<QuestionAttachment> Attachments => Set<QuestionAttachment>();
    public DbSet<QuestionPurgeRun> PurgeRuns => Set<QuestionPurgeRun>();
    public DbSet<QuestionPersonalDataRedaction> PersonalDataRedactions => Set<QuestionPersonalDataRedaction>();
    public DbSet<QuestionFilePurgePending> PendingFilePurges => Set<QuestionFilePurgePending>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Question>(b =>
        {
            b.ToTable("question", table =>
            {
                table.HasCheckConstraint("ck_question_status", "status IN ('open','answered','closed_without_answer')");
                table.HasCheckConstraint("ck_question_directed_exactly_one",
                    "(directed_to_user IS NOT NULL AND directed_to_role IS NULL) OR (directed_to_user IS NULL AND directed_to_role IS NOT NULL)");
                table.HasCheckConstraint("ck_question_closed_state",
                    "(status = 'closed_without_answer' AND closed_reason IS NOT NULL AND closed_by IS NOT NULL AND closed_at IS NOT NULL) OR status <> 'closed_without_answer'");
                table.HasCheckConstraint("ck_question_answered_state",
                    "(status = 'answered' AND accepted_reply_id IS NOT NULL AND accepted_at IS NOT NULL) OR status <> 'answered'");
            });
            b.HasKey(x => new { x.TenantId, x.QuestionId });
            b.Property(x => x.RecordKind).HasMaxLength(64);
            b.Property(x => x.Title).HasMaxLength(240);
            b.Property(x => x.Body).HasMaxLength(8000);
            b.Property(x => x.Status).HasMaxLength(32);
            b.Property(x => x.DirectedToRole).HasMaxLength(80);
            b.Property(x => x.ClosedReason).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.RecordKind, x.RecordId, x.Status, x.AskedAt, x.QuestionId });
            b.HasIndex(x => new { x.TenantId, x.AskedBy, x.Status, x.AskedAt, x.QuestionId });
            b.HasIndex(x => new { x.TenantId, x.DirectedToUser, x.Status, x.AskedAt, x.QuestionId });
            b.HasIndex(x => new { x.TenantId, x.DirectedToRole, x.Status, x.AskedAt, x.QuestionId });
            b.HasIndex(x => new { x.TenantId, x.Status, x.UpdatedAt, x.QuestionId });
            b.HasIndex(x => new { x.TenantId, x.AskedAt, x.QuestionId })
                .IncludeProperties(x => new { x.Status, x.RecordKind, x.RecordId, x.DirectedToRole,
                    x.AcceptedAt });
            b.HasIndex(x => new { x.TenantId, x.Status, x.QuestionId });
        });
        modelBuilder.Entity<QuestionReply>(b =>
        {
            b.ToTable("question_reply");
            b.HasKey(x => new { x.TenantId, x.ReplyId });
            b.Property(x => x.Body).HasMaxLength(8000);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.QuestionId, x.At, x.ReplyId }).IsUnique();
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionReplyRevision>(b =>
        {
            b.ToTable("question_reply_revision", table => table.HasCheckConstraint("ck_question_reply_revision_target",
                "(question_body = 1 AND reply_id IS NULL) OR (question_body = 0 AND reply_id IS NOT NULL)"));
            b.HasKey(x => new { x.TenantId, x.RevisionId });
            b.Property(x => x.Body).HasMaxLength(8000);
            b.HasIndex(x => new { x.TenantId, x.QuestionId, x.ReplyId, x.RevisedAt, x.RevisionId });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionParticipant>(b =>
        {
            b.ToTable("question_participant");
            b.HasKey(x => new { x.TenantId, x.QuestionId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.LastReadAt, x.QuestionId });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionSimilarEntry>(b =>
        {
            b.ToTable("question_similar_entry", table => table.HasCheckConstraint(
                "ck_question_similar_entry_rank", "rank BETWEEN 1 AND 1000"));
            b.HasKey(x => new { x.TenantId, x.QuestionId, x.EntryId });
            b.HasIndex(x => new { x.TenantId, x.EntryId, x.MatchedAt, x.QuestionId });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntry>(b =>
        {
            b.ToTable("knowledge_entry", table =>
            {
                table.HasCheckConstraint("ck_knowledge_entry_status",
                    "status IN ('draft','in_review','published','retired')");
                table.HasCheckConstraint("ck_knowledge_entry_version", "current_version >= 1");
                table.HasCheckConstraint("ck_knowledge_entry_published_version",
                    "published_version IS NULL OR (published_version >= 1 AND published_version <= current_version)");
                table.HasCheckConstraint("ck_knowledge_entry_needs_review_state",
                    "(needs_review = 1 AND needs_review_since IS NOT NULL AND published_version IS NOT NULL) OR (needs_review = 0 AND needs_review_since IS NULL)");
                table.HasCheckConstraint("ck_knowledge_entry_published_state",
                    "(status IN ('published','retired') AND published_by IS NOT NULL AND published_at IS NOT NULL) OR status NOT IN ('published','retired')");
                table.HasCheckConstraint("ck_knowledge_entry_retired_state",
                    "(status = 'retired' AND retired_by IS NOT NULL AND retired_at IS NOT NULL AND retirement_note IS NOT NULL) OR status <> 'retired'");
            });
            b.HasKey(x => new { x.TenantId, x.EntryId });
            b.Property(x => x.Title).HasMaxLength(240);
            b.Property(x => x.Problem).HasMaxLength(8000);
            b.Property(x => x.Answer).HasMaxLength(8000);
            b.Property(x => x.Steps).HasMaxLength(8000);
            b.Property(x => x.WrittenAgainstJson).HasMaxLength(8000);
            b.Property(x => x.Status).HasMaxLength(32);
            b.Property(x => x.RetirementNote).HasMaxLength(1000);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.SourceQuestionId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.LastSavedAt, x.EntryId });
            b.HasIndex(x => new { x.TenantId, x.NeedsReview, x.NeedsReviewSince, x.EntryId });
            b.HasIndex(x => new { x.TenantId, x.PublishedAt, x.EntryId });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.SourceQuestionId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryVersion>(b =>
        {
            b.ToTable("knowledge_entry_version", table =>
                table.HasCheckConstraint("ck_knowledge_entry_version_number", "version >= 1"));
            b.HasKey(x => new { x.TenantId, x.EntryId, x.Version });
            b.Property(x => x.Title).HasMaxLength(240);
            b.Property(x => x.Problem).HasMaxLength(8000);
            b.Property(x => x.Answer).HasMaxLength(8000);
            b.Property(x => x.Steps).HasMaxLength(8000);
            b.Property(x => x.WrittenAgainstJson).HasMaxLength(8000);
            b.HasIndex(x => new { x.TenantId, x.EntryId, x.SavedAt });
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryTag>(b =>
        {
            b.ToTable("knowledge_entry_tag", table => table.HasCheckConstraint("ck_knowledge_entry_tag_kind",
                "kind IN ('role','task','item_family','claim_cause')"));
            b.HasKey(x => new { x.TenantId, x.EntryId, x.Version, x.Kind, x.Value });
            b.Property(x => x.Kind).HasMaxLength(32);
            b.Property(x => x.Value).HasMaxLength(200);
            b.Property(x => x.Label).HasMaxLength(240);
            b.HasIndex(x => new { x.TenantId, x.Kind, x.Value, x.EntryId, x.Version });
            b.HasOne<KnowledgeEntryVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId, x.Version })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryRecordLink>(b =>
        {
            b.ToTable("knowledge_entry_record_link");
            b.HasKey(x => new { x.TenantId, x.EntryId, x.Version, x.RecordKind, x.RecordId });
            b.Property(x => x.RecordKind).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.RecordKind, x.RecordId, x.EntryId, x.Version });
            b.HasOne<KnowledgeEntryVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId, x.Version })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryReviewRound>(b =>
        {
            b.ToTable("knowledge_entry_review_round", table =>
            {
                table.HasCheckConstraint("ck_knowledge_entry_review_round_number", "round >= 1");
                table.HasCheckConstraint("ck_knowledge_entry_review_decision",
                    "decision IS NULL OR decision IN ('approve','send_back')");
                table.HasCheckConstraint("ck_knowledge_entry_review_state",
                    "(decision IS NULL AND reviewer IS NULL AND reviewed_at IS NULL) OR (decision IS NOT NULL AND reviewer IS NOT NULL AND reviewed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_knowledge_entry_send_back_note",
                    "decision <> 'send_back' OR note IS NOT NULL");
            });
            b.HasKey(x => new { x.TenantId, x.EntryId, x.Round });
            b.Property(x => x.Decision).HasMaxLength(32);
            b.Property(x => x.Note).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Decision, x.SubmittedAt, x.EntryId, x.Round });
            b.HasOne<KnowledgeEntryVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId, x.Version })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryWrittenAgainst>(b =>
        {
            b.ToTable("knowledge_entry_written_against", table => table.HasCheckConstraint(
                "ck_knowledge_entry_written_against_kind", "kind IN ('policy_version','screen')"));
            b.HasKey(x => new { x.TenantId, x.EntryId, x.Version, x.Kind, x.Reference });
            b.Property(x => x.Kind).HasMaxLength(32);
            b.Property(x => x.Reference).HasMaxLength(500);
            b.Property(x => x.Label).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.Kind, x.Reference, x.EntryId, x.Version });
            b.HasOne<KnowledgeEntryVersion>().WithMany().HasForeignKey(x =>
                new { x.TenantId, x.EntryId, x.Version }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeEntryReviewReason>(b =>
        {
            b.ToTable("knowledge_entry_review_reason", table =>
            {
                table.HasCheckConstraint("ck_knowledge_entry_review_reason_kind",
                    "kind IN ('policy_version_superseded','screen_changed')");
                table.HasCheckConstraint("ck_knowledge_entry_review_reason_clearance",
                    "(cleared_at IS NULL AND cleared_by IS NULL AND cleared_at_version IS NULL AND clearance IS NULL) OR (cleared_at IS NOT NULL AND cleared_by IS NOT NULL AND cleared_at_version IS NOT NULL AND clearance IS NOT NULL)");
            });
            b.HasKey(x => new { x.TenantId, x.ReasonId });
            b.Property(x => x.Kind).HasMaxLength(40);
            b.Property(x => x.Reference).HasMaxLength(500);
            b.Property(x => x.Label).HasMaxLength(500);
            b.Property(x => x.Detail).HasMaxLength(1000);
            b.Property(x => x.Clearance).HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.EntryId, x.ClearedAt, x.FlaggedAt, x.ReasonId });
            b.HasIndex(x => new { x.TenantId, x.EntryId, x.Kind, x.Reference, x.TriggerId }).IsUnique();
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeScreenChange>(b =>
        {
            b.ToTable("knowledge_screen_change");
            b.HasKey(x => new { x.TenantId, x.ChangeId });
            b.Property(x => x.ScreenReference).HasMaxLength(500);
            b.Property(x => x.ScreenLabel).HasMaxLength(500);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.ScreenReference, x.ChangedAt, x.ChangeId });
        });
        modelBuilder.Entity<TrainingPath>(b =>
        {
            b.ToTable("training_path", table => table.HasCheckConstraint("ck_training_path_version",
                "current_version >= 1"));
            b.HasKey(x => new { x.TenantId, x.PathId });
            b.Property(x => x.Role).HasMaxLength(80);
            b.Property(x => x.Introduction).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.Role }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LastChangedAt, x.PathId });
        });
        modelBuilder.Entity<TrainingPathVersion>(b =>
        {
            b.ToTable("training_path_version", table => table.HasCheckConstraint(
                "ck_training_path_version_number", "version >= 1"));
            b.HasKey(x => new { x.TenantId, x.PathId, x.Version });
            b.Property(x => x.Role).HasMaxLength(80);
            b.Property(x => x.Introduction).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.PathId, x.ChangedAt });
            b.HasOne<TrainingPath>().WithMany().HasForeignKey(x => new { x.TenantId, x.PathId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TrainingPathEntry>(b =>
        {
            b.ToTable("training_path_entry", table => table.HasCheckConstraint(
                "ck_training_path_entry_position", "position >= 1"));
            b.HasKey(x => new { x.TenantId, x.PathId, x.Version, x.Position });
            b.HasIndex(x => new { x.TenantId, x.PathId, x.Version, x.EntryId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EntryId, x.PathId, x.Version });
            b.HasOne<TrainingPathVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PathId, x.Version })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TrainingProgress>(b =>
        {
            b.ToTable("training_progress", table => table.HasCheckConstraint(
                "ck_training_progress_entry_version", "read_entry_version >= 1"));
            b.HasKey(x => new { x.TenantId, x.PathId, x.EntryId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt, x.PathId, x.EntryId });
            b.HasIndex(x => new { x.TenantId, x.ReadAt, x.EntryId, x.UserId });
            b.HasOne<TrainingPath>().WithMany().HasForeignKey(x => new { x.TenantId, x.PathId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TrainingWelcomeDismissal>(b =>
        {
            b.ToTable("training_welcome_dismissal");
            b.HasKey(x => new { x.TenantId, x.PathId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.DismissedAt, x.PathId });
            b.HasOne<TrainingPath>().WithMany().HasForeignKey(x => new { x.TenantId, x.PathId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionNotificationPreference>(b =>
        {
            b.ToTable("question_notification_preference", table => table.HasCheckConstraint(
                "ck_question_notification_preference_mode", "mode IN ('immediately','daily_digest','off')"));
            b.HasKey(x => new { x.TenantId, x.UserId });
            b.Property(x => x.Mode).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.Mode, x.UserId });
        });
        modelBuilder.Entity<QuestionNotification>(b =>
        {
            b.ToTable("question_notification", table =>
            {
                table.HasCheckConstraint("ck_question_notification_delivery_mode",
                    "delivery_mode IN ('immediately','daily_digest')");
                table.HasCheckConstraint("ck_question_notification_target",
                    "(question_id IS NOT NULL AND entry_id IS NULL) OR (question_id IS NULL AND entry_id IS NOT NULL)");
            });
            b.HasKey(x => new { x.TenantId, x.NotificationId });
            b.Property(x => x.EventKind).HasMaxLength(24);
            b.Property(x => x.DeliveryMode).HasMaxLength(24);
            // Coupled to QuestionNotificationHandler's locked insert: this key is the database arbiter for concurrent
            // redelivery. A read-before-write seam cannot prevent two handlers from queuing the same mail; changing this
            // key without changing that insert removes the exactly-once notification decision.
            b.HasIndex(x => new { x.TenantId, x.RecipientUserId, x.EventKind, x.EventId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.DeliveryMode, x.DigestDate, x.RecipientUserId, x.OccurredAt });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<KnowledgeEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntryId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionDigest>(b =>
        {
            b.ToTable("question_digest");
            b.HasKey(x => new { x.TenantId, x.UserId, x.DigestDate });
            b.HasIndex(x => new { x.TenantId, x.DigestDate, x.UserId });
        });
        modelBuilder.Entity<QuestionAttachment>(b =>
        {
            b.ToTable("question_attachment", table => table.HasCheckConstraint("ck_question_attachment_target",
                "(question_body = 1 AND reply_id IS NULL) OR (question_body = 0 AND reply_id IS NOT NULL)"));
            b.HasKey(x => new { x.TenantId, x.FileId });
            b.Property(x => x.FileName).HasMaxLength(260);
            b.Property(x => x.ContentType).HasMaxLength(100).IsUnicode(false);
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.HasIndex(x => new { x.TenantId, x.QuestionId, x.ReplyId, x.UploadedAt, x.FileId });
            b.HasOne<Question>().WithMany().HasForeignKey(x => new { x.TenantId, x.QuestionId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuestionPurgeRun>(b =>
        {
            b.ToTable("question_purge_run", table =>
            {
                table.HasCheckConstraint("ck_question_purge_run_trigger", "triggered_by_kind IN ('system','user')");
                table.HasCheckConstraint("ck_question_purge_run_years", "retention_years BETWEEN 1 AND 100");
            });
            b.HasKey(x => new { x.TenantId, x.RunId });
            b.Property(x => x.TriggeredByKind).HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.TriggeredByLabel).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.CompletedAt, x.RunId });
        });
        modelBuilder.Entity<QuestionPersonalDataRedaction>(b =>
        {
            b.ToTable("question_personal_data_redaction");
            b.HasKey(x => new { x.TenantId, x.RedactionId });
            b.Property(x => x.ScopeJson).HasMaxLength(8000);
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.PersonId, x.PerformedAt, x.RedactionId });
        });
        modelBuilder.Entity<QuestionFilePurgePending>(b =>
        {
            b.ToTable("question_file_purge_pending");
            b.HasKey(x => new { x.TenantId, x.FileId });
            b.Property(x => x.Reason).HasMaxLength(32).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.QueuedAt, x.FileId });
        });
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class KnowledgeDesignTimeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL")
            ?? "Server=localhost,1433;Database=yojana_design;User Id=sa;Password=Design_time_only1!;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__migrations", KnowledgeDbContext.SchemaName))
            .Options;
        return new KnowledgeDbContext(options, new TenantContext());
    }
}
