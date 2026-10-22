using Microsoft.EntityFrameworkCore;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.LinkOverrides;

public sealed class MigrationLinkOverride : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string SourceKey { get; set; }
    public string? Direction { get; set; }
    public string? ReplacementReason { get; set; }
    public string? State { get; set; }
    public string? ReplacementConditionAllowed { get; set; }
    public string? PricePolicy { get; set; }
    public decimal? DifferenceAmount { get; set; }
    public string? Currency { get; set; }
    public DateTime UploadedAt { get; set; }
    public required string UploadedBy { get; set; }
    public required string UploadFileName { get; set; }

    public ReplacementLinkValues Effective(ReplacementLinkValues defaults) => new(
        Direction ?? defaults.Direction,
        ReplacementReason ?? defaults.ReplacementReason,
        State ?? defaults.State,
        ReplacementConditionAllowed ?? defaults.ReplacementConditionAllowed,
        PricePolicy ?? defaults.PricePolicy,
        DifferenceAmount ?? defaults.DifferenceAmount,
        Currency ?? defaults.Currency);
}

internal static class LinkOverrideModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationLinkOverride>(builder =>
        {
            builder.ToTable("migration_link_override", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.SourceKey });
            builder.Property(x => x.SourceKey).HasMaxLength(320);
            builder.Property(x => x.Direction).HasMaxLength(24);
            builder.Property(x => x.ReplacementReason).HasMaxLength(32);
            builder.Property(x => x.State).HasMaxLength(24);
            builder.Property(x => x.ReplacementConditionAllowed).HasMaxLength(24);
            builder.Property(x => x.PricePolicy).HasMaxLength(16);
            builder.Property(x => x.DifferenceAmount).HasPrecision(19, 4);
            builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            builder.Property(x => x.UploadedBy).HasMaxLength(320);
            builder.Property(x => x.UploadFileName).HasMaxLength(260);
            builder.HasIndex(x => new { x.TenantId, x.SourceKey }).IsUnique();
            builder.HasIndex(x => new { x.TenantId, x.UploadedAt, x.SourceKey });
        });
    }
}
