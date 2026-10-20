using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

internal static class CoverItemsModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ClaimCauseRow>(b =>
        {
            b.ToTable("claim_cause");
            b.HasKey(x => new { x.TenantId, x.Code });
            b.Property(x => x.Code).HasMaxLength(40);
            b.Property(x => x.CustomerWording).HasMaxLength(200);
            b.Property(x => x.UpdatedBy).HasMaxLength(200);
        });
        model.Entity<PolicyCoverItemRow>(b =>
        {
            b.ToTable("policy_cover_item", t =>
            {
                t.HasCheckConstraint("ck_policy_cover_item_type", "type IN ('covered','excluded')");
                t.HasCheckConstraint("ck_policy_cover_item_category", "category IS NULL OR category IN ('cause','part','condition','service')");
                t.HasCheckConstraint("ck_policy_cover_item_scope", "ISJSON(parts_scope) = 1");
            });
            b.HasKey(x => new { x.TenantId, x.PolicyVersionId, x.Code });
            b.Property(x => x.Code).HasMaxLength(24);
            b.Property(x => x.Type).HasMaxLength(8);
            b.Property(x => x.CustomerWording).HasMaxLength(500);
            b.Property(x => x.Category).HasMaxLength(10);
            b.HasOne<WarrantyPolicyVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.PolicyVersionId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PolicyCoverItemCause>(b =>
        {
            b.ToTable("policy_cover_item_cause");
            b.HasKey(x => new { x.TenantId, x.PolicyVersionId, x.ItemCode, x.CauseCode });
            b.Property(x => x.ItemCode).HasMaxLength(24);
            b.Property(x => x.CauseCode).HasMaxLength(40);
            b.HasOne<PolicyCoverItemRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PolicyVersionId, Code = x.ItemCode }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ClaimCauseRow>().WithMany().HasForeignKey(x => new { x.TenantId, Code = x.CauseCode }).OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.TenantId, x.CauseCode });
        });
        model.Entity<Claim>(b =>
        {
            b.Property(x => x.ReportedCauseCode).HasMaxLength(40);
            b.Property(x => x.ReportedDescription).HasMaxLength(4000);
            b.HasOne<ClaimCauseRow>().WithMany().HasForeignKey(x => new { x.TenantId, Code = x.ReportedCauseCode }).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
