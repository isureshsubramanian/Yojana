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
