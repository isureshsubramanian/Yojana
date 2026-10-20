using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

internal static class CoverDeviationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<UnitCoverDeviation>(b =>
        {
            b.ToTable("unit_cover_deviation");
            b.HasKey(r => new { r.TenantId, r.UnitId });
            b.Property(r => r.SaleDocumentType).HasMaxLength(40);
            b.Property(r => r.ItemFamily).HasMaxLength(100);
            b.HasIndex(r => new { r.TenantId, r.Changed, r.LastChangeAt, r.UnitId }).IsDescending(false, false, true, true);
            b.HasIndex(r => new { r.TenantId, r.SaleDate, r.UnitId });
            b.HasIndex(r => new { r.TenantId, r.CustomerPartyRef, r.LastChangeAt, r.UnitId });
            b.HasIndex(r => new { r.TenantId, r.ItemFamily, r.LastChangeAt, r.UnitId });
        });
        model.Entity<UnitCoverDeviationChange>(b =>
        {
            b.ToTable("unit_cover_deviation_change");
            b.HasKey(r => new { r.TenantId, r.UnitId, r.EventId });
            b.Property(r => r.Kind).HasMaxLength(20);
            b.Property(r => r.Currency).HasColumnType("char(3)");
            b.Property(r => r.Amount).HasPrecision(19, 4);
            b.HasIndex(r => new { r.TenantId, r.Kind, r.UnitId, r.MonthsAfterSale });
            b.HasOne<UnitCoverDeviation>().WithMany().HasForeignKey(r => new { r.TenantId, r.UnitId });
        });
        model.Entity<UnitCoverDeviationValue>(b =>
        {
            b.ToTable("unit_cover_deviation_value");
            b.HasKey(r => new { r.TenantId, r.UnitId, r.Currency });
            b.Property(r => r.Currency).HasColumnType("char(3)");
            b.Property(r => r.Amount).HasPrecision(19, 4);
            b.HasOne<UnitCoverDeviation>().WithMany().HasForeignKey(r => new { r.TenantId, r.UnitId });
        });
        model.Entity<CoverDeviationBackfillCheckpoint>(b =>
        {
            b.ToTable("cover_deviation_backfill_checkpoint");
            b.HasKey(r => r.TenantId);
        });
    }
}
