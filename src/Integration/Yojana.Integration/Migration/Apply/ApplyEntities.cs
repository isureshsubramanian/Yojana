using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Apply;

public static class ApplyRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string RolledBack = "rolled_back";
}

/// <summary>
/// One load of a completed map run into the module (P9-T10, OD-52). <see cref="RunId"/> is the load id the module
/// registers its units under, so a failed run resumes from <see cref="CursorPlanUnitId"/> and a resent chunk is a
/// no-op on the module side.
/// </summary>
public sealed class MigrationApplyRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid BatchId { get; set; }
    public bool Rehearsal { get; set; }
    public required string Status { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? FailureCode { get; set; }
    public Guid? CursorPlanUnitId { get; set; }
    public long UnitsInPlan { get; set; }
    public long Applied { get; set; }
    public long AlreadyApplied { get; set; }
    public long Refused { get; set; }
    public long EventsWritten { get; set; }
    public string? RolledBackBy { get; set; }
    public DateTime? RolledBackAt { get; set; }
}

/// <summary>A plan unit the load did not write, with the rules that stopped it (module refusals and missing references).</summary>
public sealed class MigrationApplyRefusal : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RefusalId { get; set; }
    public Guid RunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string Reasons { get; set; }
}

internal static class ApplyModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationApplyRun>(builder =>
        {
            builder.ToTable("migration_apply_run", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_apply_run_status",
                    "status IN ('queued','running','completed','failed','rolled_back')"));
            builder.HasKey(x => new { x.TenantId, x.RunId });
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.RequestedBy).HasMaxLength(200);
            builder.Property(x => x.RolledBackBy).HasMaxLength(200);
            builder.Property(x => x.FailureCode).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.BatchId, x.RequestedAt, x.RunId });
            builder.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt, x.RunId });
            builder.HasOne<Mapping.MigrationMapRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.MapRunId }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationApplyRefusal>(builder =>
        {
            builder.ToTable("migration_apply_refusal", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.RefusalId });
            builder.Property(x => x.Reasons).HasMaxLength(2000);
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.RefusalId });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.PlanUnitId }).IsUnique();
            builder.HasOne<MigrationApplyRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.RunId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
