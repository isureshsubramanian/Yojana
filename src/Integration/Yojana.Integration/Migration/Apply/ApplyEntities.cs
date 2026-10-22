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
    public long EntitlementsWritten { get; set; }
    public long ReplacementLinksWritten { get; set; }
    public long ReplacementLinksUsingOverride { get; set; }
    public long CertificatesRecorded { get; set; }
    /// <summary>The entitlement gap report run as the load's last step (P9-T09), and the migrated units it found without base cover.</summary>
    public Guid? GapReportRunId { get; set; }
    public int? CoverGaps { get; set; }
    public string? RolledBackBy { get; set; }
    public DateTime? RolledBackAt { get; set; }
    // When the undo began, so the go-live record can show how long a rehearsed rollback took (P9-T11).
    public DateTime? RollbackStartedAt { get; set; }
    /// <summary><see cref="ApplyRunKind.Initial"/> writes a plan into an empty module; a delta (P9-T10b) carries what changed since <see cref="BaseLoadId"/>.</summary>
    public string Kind { get; set; } = ApplyRunKind.Initial;
    public Guid? BaseLoadId { get; set; }
    /// <summary>The source-clock time a final load handed the connector as its starting watermark (OD-60).</summary>
    public DateTime? WatermarkHandedOverFrom { get; set; }
    /// <summary>Why the hand-over left the watermarks as they were, when it did.</summary>
    public string? WatermarkNote { get; set; }
    /// <summary>
    /// The connection's poll watermarks before this final load handed over (JSON of entity type, watermark, next poll),
    /// so undoing the final load can put them back (OD-63). Null for other loads and loads before P9-T34.
    /// </summary>
    public string? WatermarksBefore { get; set; }
    public long CoverReplaced { get; set; }
    public long CoverRefused { get; set; }
    public long AlreadyInModule { get; set; }
    public long OwnerDiffers { get; set; }
    public long GoneFromSource { get; set; }
}

public static class ApplyRunKind
{
    public const string Initial = "initial";
    public const string Delta = "delta";
}

/// <summary>
/// Which module unit each serial-master source key became, per source connection (P9-T10b). Written by every load for
/// the units it applied; a delta load finds earlier units through it, and compares the cover and owner it recorded.
/// A rolled-back load removes its rows; an undone delta restores the cover it replaced from its delta items.
/// </summary>
public sealed class MigrationLoadedUnit : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string SourceKeyHash { get; set; }
    public required string SourceKey { get; set; }
    public Guid UnitId { get; set; }
    public Guid LoadId { get; set; }
    public Guid PlanUnitId { get; set; }
    /// <summary>Fingerprint of the cover last written for the unit (<see cref="MigrationCoverFingerprint"/>).</summary>
    public required string CoverHash { get; set; }
    /// <summary>The load that last wrote the unit's cover: <see cref="LoadId"/>, or a later delta that replaced it.</summary>
    public Guid CoverLoadId { get; set; }
    public string? OwnerPartyCode { get; set; }
    public DateTime LoadedAt { get; set; }
}

public static class DeltaItemKind
{
    public const string NewUnit = "new_unit";
    public const string AlreadyInModule = "already_in_module";
    public const string CoverReplaced = "cover_replaced";
    public const string CoverRefused = "cover_refused";
    public const string OwnerDiffers = "owner_differs";
    public const string GoneFromSource = "gone_from_source";

    public static readonly IReadOnlyList<string> All =
        [NewUnit, AlreadyInModule, CoverReplaced, CoverRefused, OwnerDiffers, GoneFromSource];

    public static string Label(string kind) => kind switch
    {
        NewUnit => "New in the source",
        AlreadyInModule => "Already in the module",
        CoverReplaced => "Cover replaced",
        CoverRefused => "Cover change refused",
        OwnerDiffers => "Owner differs, no document",
        GoneFromSource => "Gone from the source",
        _ => kind,
    };
}

/// <summary>One finding of a delta load (OD-61): what it did, or what it left for a person, for one source key.</summary>
public sealed class MigrationDeltaItem : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ItemId { get; set; }
    public Guid RunId { get; set; }
    public required string Kind { get; set; }
    public required string SourceKey { get; set; }
    public Guid? PlanUnitId { get; set; }
    public Guid? UnitId { get; set; }
    public string? ItemCode { get; set; }
    public string? Serial { get; set; }
    public string? Detail { get; set; }
    /// <summary>The coexistence conflict an owner difference was raised as (OD-61 c); null for other kinds.</summary>
    public Guid? ConflictId { get; set; }
    /// <summary>For a replaced cover: what the register held before, so undoing the delta restores it.</summary>
    public string? PreviousCoverHash { get; set; }
    public Guid? PreviousCoverLoadId { get; set; }
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
            {
                table.HasCheckConstraint("ck_migration_apply_run_status",
                    "status IN ('queued','running','completed','failed','rolled_back')");
                table.HasCheckConstraint("ck_migration_apply_run_kind", "kind IN ('initial','delta')");
            });
            builder.HasKey(x => new { x.TenantId, x.RunId });
            builder.Property(x => x.Status).HasMaxLength(20);
            builder.Property(x => x.RequestedBy).HasMaxLength(200);
            builder.Property(x => x.RolledBackBy).HasMaxLength(200);
            builder.Property(x => x.FailureCode).HasMaxLength(100);
            builder.Property(x => x.Kind).HasMaxLength(20).HasDefaultValue(ApplyRunKind.Initial);
            builder.Property(x => x.WatermarkNote).HasMaxLength(500);
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
        modelBuilder.Entity<MigrationLoadedUnit>(builder =>
        {
            builder.ToTable("migration_loaded_unit", MigrationModel.Schema);
            builder.HasKey(x => new { x.TenantId, x.ConnectionId, x.SourceKeyHash });
            builder.Property(x => x.SourceKeyHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.CoverHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            builder.Property(x => x.OwnerPartyCode).HasMaxLength(100);
            builder.HasIndex(x => new { x.TenantId, x.LoadId });
            builder.HasIndex(x => new { x.TenantId, x.CoverLoadId });
            builder.HasIndex(x => new { x.TenantId, x.UnitId });
        });
        modelBuilder.Entity<MigrationDeltaItem>(builder =>
        {
            builder.ToTable("migration_delta_item", MigrationModel.Schema, table =>
                table.HasCheckConstraint("ck_migration_delta_item_kind",
                    "kind IN ('new_unit','already_in_module','cover_replaced','cover_refused','owner_differs','gone_from_source')"));
            builder.HasKey(x => new { x.TenantId, x.ItemId });
            builder.Property(x => x.Kind).HasMaxLength(32);
            builder.Property(x => x.SourceKey).HasMaxLength(1000);
            builder.Property(x => x.ItemCode).HasMaxLength(100);
            builder.Property(x => x.Serial).HasMaxLength(200);
            builder.Property(x => x.Detail).HasMaxLength(2000);
            builder.Property(x => x.PreviousCoverHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.ItemId });
            builder.HasIndex(x => new { x.TenantId, x.RunId, x.Kind, x.ItemId });
            builder.HasOne<MigrationApplyRun>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.RunId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
