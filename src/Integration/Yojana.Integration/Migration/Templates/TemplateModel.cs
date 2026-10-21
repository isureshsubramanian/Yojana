using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Templates;

internal static class TemplateModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TemplateMapping>(builder =>
        {
            builder.ToTable("template_mapping", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_template_mapping_state",
                    "state IN ('draft','approved','superseded')"));
            builder.HasKey(row => new { row.TenantId, row.TemplateMappingId });
            builder.Property(row => row.TemplateKey).HasMaxLength(64).IsUnicode(false);
            builder.Property(row => row.SourceSystem).HasMaxLength(40);
            builder.Property(row => row.TemplateSourceKey).HasMaxLength(1000);
            builder.Property(row => row.TemplateName).HasMaxLength(500);
            builder.Property(row => row.SourceFingerprint).HasMaxLength(64).IsUnicode(false);
            builder.Property(row => row.SourceTermsJson).HasColumnType("nvarchar(max)");
            builder.Property(row => row.WordingToReviewJson).HasColumnType("nvarchar(max)");
            builder.Property(row => row.PatternKey).HasMaxLength(64).IsUnicode(false);
            builder.Property(row => row.PatternLabel).HasMaxLength(1000);
            builder.Property(row => row.DecompositionFingerprint).HasMaxLength(64).IsUnicode(false);
            builder.Property(row => row.State).HasMaxLength(20).IsUnicode(false);
            builder.Property(row => row.ReviewReason).HasMaxLength(1000);
            builder.Property(row => row.ApprovedBy).HasMaxLength(320);
            builder.Property(row => row.RowVersion).IsRowVersion();
            builder.HasIndex(row => new { row.TenantId, row.TemplateKey, row.State });
            builder.HasIndex(row => new { row.TenantId, row.TemplateKey }).IsUnique()
                .HasFilter("[state] <> 'superseded'");
            builder.HasIndex(row => new { row.TenantId, row.PatternKey, row.State, row.TemplateKey });
        });
        modelBuilder.Entity<TemplateMappingBlock>(builder =>
        {
            builder.ToTable("template_mapping_block", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_template_mapping_block_resolution",
                    "resolution IN ('existing_policy','policy_to_create')"));
            builder.HasKey(row => new { row.TenantId, row.TemplateMappingBlockId });
            builder.Property(row => row.FamilyCode).HasMaxLength(40);
            builder.Property(row => row.FamilyName).HasMaxLength(100);
            builder.Property(row => row.Resolution).HasMaxLength(24).IsUnicode(false);
            builder.Property(row => row.PolicyCode).HasMaxLength(60);
            builder.Property(row => row.PolicyName).HasMaxLength(200);
            builder.Property(row => row.ProposedTermsJson).HasColumnType("nvarchar(max)");
            builder.HasIndex(row => new { row.TenantId, row.TemplateMappingId, row.Position }).IsUnique();
            builder.HasIndex(row => new { row.TenantId, row.TemplateMappingId, row.FamilyCode }).IsUnique();
            builder.HasOne<TemplateMapping>().WithMany().HasForeignKey(row =>
                    new { row.TenantId, row.TemplateMappingId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
