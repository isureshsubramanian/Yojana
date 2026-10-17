using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Data;

/// <summary>
/// The <c>serialisation</c> schema: unit identity (MOD-03), the event ledger and current state (MOD-04) and
/// custody episodes (MOD-05). Table shapes follow ADR-0001 "Scale and storage". The two ledger tables are
/// partitioned, which EF Core cannot express, so their DDL lives in the Initial migration as T-SQL; the
/// model below still maps them so queries, the snapshot and later migrations see the same shape.
/// </summary>
public sealed class SerialisationDbContext(DbContextOptions<SerialisationDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "serialisation";

    /// <summary>Partition function and scheme shared by <c>unit_event</c> and <c>unit_event_archive</c>.</summary>
    public const string PartitionFunction = "pf_event_month";
    public const string PartitionScheme = "ps_event_month";
    public const string EventSequence = "seq_unit_event";

    public override string Schema => SchemaName;

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<UnitIdentifier> Identifiers => Set<UnitIdentifier>();

    public DbSet<UnitItemAssignment> ItemAssignments => Set<UnitItemAssignment>();

    public DbSet<AssemblyLink> AssemblyLinks => Set<AssemblyLink>();

    public DbSet<BuildRecord> BuildRecords => Set<BuildRecord>();

    public DbSet<SerialRange> SerialRanges => Set<SerialRange>();

    public DbSet<SerialRangeMember> SerialRangeMembers => Set<SerialRangeMember>();

    public DbSet<CustodyEpisode> Episodes => Set<CustodyEpisode>();

    public DbSet<UnitEvent> Events => Set<UnitEvent>();

    public DbSet<UnitEventArchive> ArchivedEvents => Set<UnitEventArchive>();

    public DbSet<UnitEventArchiveStage> ArchiveStage => Set<UnitEventArchiveStage>();

    public DbSet<UnitCurrent> Current => Set<UnitCurrent>();

    public DbSet<PendingShipmentLine> PendingShipmentLines => Set<PendingShipmentLine>();

    public DbSet<FinancePeriod> FinancePeriods => Set<FinancePeriod>();

    public DbSet<SelectionSet> SelectionSets => Set<SelectionSet>();

    public DbSet<SelectionSetMember> SelectionSetMembers => Set<SelectionSetMember>();

    public DbSet<BulkApprovalRequest> BulkApprovalRequests => Set<BulkApprovalRequest>();

    public DbSet<WorkbenchRequest> WorkbenchRequests => Set<WorkbenchRequest>();

    public DbSet<CorrectionRecord> Corrections => Set<CorrectionRecord>();

    public DbSet<UnitMergeRecord> UnitMerges => Set<UnitMergeRecord>();
    public DbSet<SerialAllocationReassignment> AllocationReassignments => Set<SerialAllocationReassignment>();

    public DbSet<SerialAllocationOverride> AllocationOverrides => Set<SerialAllocationOverride>();
    public DbSet<ItemTrackingModeChange> ItemTrackingModeChanges => Set<ItemTrackingModeChange>();
    public DbSet<ManufacturingMessageReceipt> ManufacturingMessageReceipts => Set<ManufacturingMessageReceipt>();
    public DbSet<CorrectionRecordUnit> CorrectionUnits => Set<CorrectionRecordUnit>();

    public DbSet<SearchEntry> SearchEntries => Set<SearchEntry>();

    public DbSet<SearchTerm> SearchTerms => Set<SearchTerm>();

    public DbSet<CoverageFamily> CoverageFamilies => Set<CoverageFamily>();

    public DbSet<ReplacementPolicy> ReplacementPolicies => Set<ReplacementPolicy>();

    public DbSet<WarrantyPolicyVersion> PolicyVersions => Set<WarrantyPolicyVersion>();

    public DbSet<AccrualCohort> AccrualCohorts => Set<AccrualCohort>();

    public DbSet<Entitlement> Entitlements => Set<Entitlement>();

    public DbSet<EntitlementRequirement> EntitlementRequirements => Set<EntitlementRequirement>();

    public DbSet<EntitlementGapRun> EntitlementGapRuns => Set<EntitlementGapRun>();

    public DbSet<WarrantyLineException> WarrantyLineExceptions => Set<WarrantyLineException>();

    public DbSet<EntitlementSuspension> EntitlementSuspensions => Set<EntitlementSuspension>();

    public DbSet<EntitlementRebase> EntitlementRebases => Set<EntitlementRebase>();

    public DbSet<EntitlementAmendment> EntitlementAmendments => Set<EntitlementAmendment>();

    public DbSet<EntitlementUsageLimit> EntitlementUsageLimits => Set<EntitlementUsageLimit>();

    public DbSet<WarrantyCurrent> WarrantyCurrent => Set<WarrantyCurrent>();

    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();

    public DbSet<ClaimEvidence> ClaimEvidence => Set<ClaimEvidence>();

    public DbSet<CoverageDecision> CoverageDecisions => Set<CoverageDecision>();

    public DbSet<RecoveryClaim> RecoveryClaims => Set<RecoveryClaim>();
    public DbSet<NumberSeries> NumberSeries => Set<NumberSeries>();

    public DbSet<ReplacementLink> ReplacementLinks => Set<ReplacementLink>();

    public DbSet<WarrantyReplacement> WarrantyReplacements => Set<WarrantyReplacement>();

    public DbSet<WarrantyReplacementLine> WarrantyReplacementLines => Set<WarrantyReplacementLine>();

    public DbSet<PartyReplacementHold> PartyReplacementHolds => Set<PartyReplacementHold>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignCriterion> CampaignCriteria => Set<CampaignCriterion>();

    public DbSet<CampaignUnit> CampaignUnits => Set<CampaignUnit>();

    public DbSet<Rma> Rmas => Set<Rma>();
    public DbSet<RmaLine> RmaLines => Set<RmaLine>();
    public DbSet<ReturnReceipt> ReturnReceipts => Set<ReturnReceipt>();
    public DbSet<ReturnReason> ReturnReasons => Set<ReturnReason>();
    public DbSet<UnitConditionEvent> ConditionEvents => Set<UnitConditionEvent>();
    public DbSet<SanitisationRecord> SanitisationRecords => Set<SanitisationRecord>();
    public DbSet<TestRecord> TestRecords => Set<TestRecord>();
    public DbSet<UsageReading> UsageReadings => Set<UsageReading>();
    public DbSet<UnitAttributeHistory> AttributeHistory => Set<UnitAttributeHistory>();
    public DbSet<LifeEpisode> LifeEpisodes => Set<LifeEpisode>();
    public DbSet<RefurbishmentWorkOrder> RefurbishmentWorkOrders => Set<RefurbishmentWorkOrder>();
    public DbSet<RefurbishmentOperation> RefurbishmentOperations => Set<RefurbishmentOperation>();
    public DbSet<RefurbishmentPart> RefurbishmentParts => Set<RefurbishmentPart>();
    public DbSet<RefurbishmentLabour> RefurbishmentLabour => Set<RefurbishmentLabour>();
    public DbSet<RefurbishmentCost> RefurbishmentCosts => Set<RefurbishmentCost>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(EventSequence, SchemaName).StartsAt(1).IncrementsBy(1);

        modelBuilder.Entity<Unit>(b =>
        {
            b.ToTable("unit", t =>
            {
                t.IsTemporal(h => h.UseHistoryTable("unit_history"));
                t.HasCheckConstraint("ck_unit_overlay_flags_json", "ISJSON(overlay_flags) = 1");
            });
            b.HasKey(u => new { u.TenantId, u.UnitId });
            b.Property(u => u.LifecycleStatus).HasMaxLength(40);
            b.Property(u => u.OverlayFlags).HasDefaultValue("[]");
            b.Property(u => u.Origin).HasMaxLength(40);
            b.Property(u => u.SerialisationPoint).HasMaxLength(40);
            b.Property(u => u.RetentionClass).HasMaxLength(40);
            b.Property(u => u.HistoryDepth).HasMaxLength(20).HasDefaultValue("full");
            b.HasIndex(u => new { u.TenantId, u.LotId });
            b.HasIndex(u => new { u.TenantId, u.DerivedFromUnitId });
            b.HasIndex(u => new { u.TenantId, u.LifecycleStatus });
        });

        modelBuilder.Entity<UnitIdentifier>(b =>
        {
            b.ToTable("unit_identifier");
            b.HasKey(i => new { i.TenantId, i.IdentifierId });
            b.Property(i => i.Type).HasMaxLength(40);
            b.Property(i => i.Scheme).HasMaxLength(20);
            b.Property(i => i.EnterpriseIdentifier).HasMaxLength(50);
            b.Property(i => i.Qualifier).HasMaxLength(100);
            b.Property(i => i.RawValue).HasMaxLength(100);
            b.Property(i => i.NormalisedValue).HasMaxLength(100);
            b.Property(i => i.ScopeKey).HasMaxLength(100);
            b.Property(i => i.CorrectionReason).HasMaxLength(40);
            b.Property(i => i.ActorRef).HasMaxLength(200);
            // TIGHT COUPLING (P3-T03 pairwise identifier swap)
            // WHAT: these immediate filtered unique indexes are coupled to SwapIdentifiersHandler's close-flush-open
            // ordering. WHY: SQL Server cannot defer a unique constraint until transaction commit, so both old rows must
            // be closed before either replacement is inserted. WHAT BREAKS IF SEPARATED: the first insert in a valid
            // pairwise swap collides with the still-open row on the counterpart. Keep the handler comment in sync.
            // Identity rule 1: one open identifier per (tenant, scheme, scope, normalised value) across all units.
            b.HasIndex(i => new { i.TenantId, i.Scheme, i.ScopeKey, i.NormalisedValue })
                // A filtered index cannot hold OR: the check constraint ck_unit_identifier_duplicate_vendor_only (raw SQL in
                // migration P3T09MergeUnits) keeps duplicate_acknowledged to vendor serials, so every other type stays unique.
                .IsUnique().HasFilter("[valid_to] IS NULL AND [duplicate_acknowledged] = 0");
            // Invariant 2: at most one open identifier per (unit, type).
            b.HasIndex(i => new { i.TenantId, i.UnitId, i.Type })
                .IsUnique().HasFilter("[valid_to] IS NULL");
            // All identifiers of a unit, open or superseded (search re-index, history). The filtered index above cannot
            // answer it, so without this the read range-scanned the tenant: 2.5 s per 100-unit chunk at 1 M units (bench
            // 2026-10-16), starving the receipt it ran beside.
            b.HasIndex(i => new { i.TenantId, i.UnitId });
            b.HasIndex(i => new { i.TenantId, i.SwappedWithIdentifierId });
            b.HasIndex(i => new { i.TenantId, i.MergedFromUnitId });
            // Lookup by any identifier, open or superseded; prefix search uses LIKE 'value%' on it.
            b.HasIndex(i => new { i.TenantId, i.NormalisedValue })
                .IncludeProperties(i => new { i.UnitId, i.Type, i.ScopeKey, i.ValidTo });
        });

        modelBuilder.Entity<UnitItemAssignment>(b =>
        {
            b.ToTable("unit_item_assignment");
            b.HasKey(a => new { a.TenantId, a.AssignmentId });
            b.Property(a => a.Reason).HasMaxLength(20);
            b.HasIndex(a => new { a.TenantId, a.UnitId }).IsUnique().HasFilter("[valid_to] IS NULL");
            b.HasIndex(a => new { a.TenantId, a.ItemId, a.ValidTo });
            b.HasIndex(a => new { a.TenantId, a.MergedFromUnitId });
        });

        modelBuilder.Entity<AssemblyLink>(b =>
        {
            b.ToTable("assembly_link", t => t.HasCheckConstraint("ck_assembly_link_child",
                "(child_unit_id IS NOT NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND quantity IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL AND quantity IS NOT NULL)"));
            b.HasKey(l => new { l.TenantId, l.LinkId });
            b.Property(l => l.Quantity).HasPrecision(18, 6);
            b.Property(l => l.Relation).HasMaxLength(20);
            b.Property(l => l.Slot).HasMaxLength(40);
            b.Property(l => l.ReleaseStatus).HasMaxLength(20);
            b.Property(l => l.ReleaseDocRef).HasMaxLength(100);
            b.HasIndex(l => new { l.TenantId, l.ParentUnitId, l.RemovedAt });
            b.HasIndex(l => new { l.TenantId, l.MergedFromUnitId });
            // Tree integrity (case 37): a serialised child has at most one open parent.
            b.HasIndex(l => new { l.TenantId, l.ChildUnitId }).IsUnique()
                .HasFilter("[removed_at] IS NULL AND [child_unit_id] IS NOT NULL");
        });

        modelBuilder.Entity<BuildRecord>(b =>
        {
            b.ToTable("build_record", t => t.HasCheckConstraint("ck_build_record_payload_json", "ISJSON(payload) = 1"));
            b.HasKey(r => new { r.TenantId, r.BuildRecordId });
            b.Property(r => r.Revision).HasMaxLength(40);
            b.Property(r => r.PayloadHash).HasMaxLength(64).IsUnicode(false);
            b.Property(r => r.ReleaseSignatureRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.UnitId, r.Version }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.MergedFromUnitId });
        });

        modelBuilder.Entity<SerialRange>(b =>
        {
            b.ToTable("serial_range", t => t.HasCheckConstraint("ck_serial_range_bounds",
                "(start_ordinal IS NULL AND end_ordinal IS NULL) OR (start_ordinal <= end_ordinal AND end_ordinal - start_ordinal + 1 = count)"));
            b.HasKey(r => new { r.TenantId, r.RangeId });
            b.Property(r => r.Scheme).HasMaxLength(20);
            b.Property(r => r.EnterpriseIdentifier).HasMaxLength(50);
            b.Property(r => r.Qualifier).HasMaxLength(100);
            b.Property(r => r.StartValue).HasMaxLength(100);
            b.Property(r => r.EndValue).HasMaxLength(100);
            b.Property(r => r.RandomisedListRef).HasMaxLength(100);
            b.Property(r => r.AllocatedToDocumentType).HasMaxLength(40);
            b.Property(r => r.ReservedBy).HasMaxLength(200);
            b.Property(r => r.Status).HasMaxLength(20);
            b.Property(r => r.Note).HasMaxLength(400);
            // Overlap detection scans the scheme's block by ordinal (P1-T10) under an update lock on this index.
            b.HasIndex(r => new { r.TenantId, r.Scheme, r.Qualifier, r.StartOrdinal });
            b.HasIndex(r => new { r.TenantId, r.AllocatedToDocumentId });
        });

        modelBuilder.Entity<SerialRangeMember>(b =>
        {
            b.ToTable("serial_range_member");
            b.HasKey(m => new { m.TenantId, m.RangeId, m.Ordinal });
            b.Property(m => m.Value).HasMaxLength(100);
            b.HasIndex(m => new { m.TenantId, m.CommissionedUnitId });
            b.HasIndex(m => new { m.TenantId, m.MergedFromUnitId });
        });

        modelBuilder.Entity<CustodyEpisode>(b =>
        {
            b.ToTable("custody_episode", t => t.IsTemporal(h => h.UseHistoryTable("custody_episode_history")));
            b.HasKey(e => new { e.TenantId, e.EpisodeId });
            b.Property(e => e.Kind).HasMaxLength(20);
            b.Property(e => e.PartyType).HasMaxLength(30);
            b.Property(e => e.LocationKind).HasMaxLength(30);
            b.Property(e => e.Reason).HasMaxLength(40);
            b.Property(e => e.Source).HasMaxLength(40);
            b.Property(e => e.Confidence).HasMaxLength(20);
            b.Property(e => e.OpeningDocumentType).HasMaxLength(40);
            b.Property(e => e.ClosingDocumentType).HasMaxLength(40);
            b.Property(e => e.ExternalOrderRef).HasMaxLength(100);
            b.Property(e => e.CustomerPoRef).HasMaxLength(100);
            b.Property(e => e.CustomerItemRef).HasMaxLength(100);
            // One open ownership row and one open custody row per unit.
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.Kind }).IsUnique().HasFilter("[valid_to] IS NULL");
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.ValidFrom });
            b.HasIndex(e => new { e.TenantId, e.Kind, e.PartyType, e.ValidFrom })
                .IncludeProperties(e => new { e.UnitId, e.EpisodeId, e.PartyRef, e.Source, e.OpeningDocumentType });
            b.HasIndex(e => new { e.TenantId, e.PartyRef, e.Kind, e.ValidTo });
            b.HasIndex(e => new { e.TenantId, e.LocationId, e.ValidTo });
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId });
        });

        ConfigureLedger<UnitEvent>(modelBuilder, "unit_event");
        ConfigureLedger<UnitEventArchive>(modelBuilder, "unit_event_archive");
        ConfigureLedger<UnitEventArchiveStage>(modelBuilder, "unit_event_archive_stage");

        modelBuilder.Entity<UnitCurrent>(b =>
        {
            b.ToTable("unit_current", t =>
            {
                t.HasCheckConstraint("ck_unit_current_live_entitlement_ids_json", "ISJSON(live_entitlement_ids) = 1");
                t.HasCheckConstraint("ck_unit_current_overlay_flags_json", "ISJSON(overlay_flags) = 1");
                t.HasCheckConstraint("ck_unit_current_attributes_json", "attributes IS NULL OR ISJSON(attributes) = 1");
            });
            b.HasKey(c => new { c.TenantId, c.UnitId });
            b.Property(c => c.LifecycleStatus).HasMaxLength(40);
            b.Property(c => c.Disposition).HasMaxLength(40);
            b.Property(c => c.ConditionGrade).HasMaxLength(40);
            b.Property(c => c.LocationKind).HasMaxLength(30);
            b.Property(c => c.CustodyKind).HasMaxLength(20);
            b.Property(c => c.LiveEntitlementIds).HasDefaultValue("[]");
            b.Property(c => c.OverlayFlags).HasDefaultValue("[]");
            b.Property(c => c.RowVersion).IsRowVersion();
            b.HasIndex(c => new { c.TenantId, c.ItemId, c.LocationId, c.Disposition });
            b.HasIndex(c => new { c.TenantId, c.OwnerPartyRef });
            b.HasIndex(c => new { c.TenantId, c.CustodyPartyRef, c.CustodyKind });
        });

        modelBuilder.Entity<PendingShipmentLine>(b =>
        {
            b.ToTable("pending_shipment_line");
            b.HasKey(p => new { p.TenantId, p.PendingId });
            b.Property(p => p.DocumentType).HasMaxLength(40);
            b.Property(p => p.Quantity).HasPrecision(18, 6);
            b.Property(p => p.Source).HasMaxLength(40);
            b.HasIndex(p => new { p.TenantId, p.DocumentType, p.DocumentId, p.DocumentLine }).IsUnique();
            b.HasIndex(p => new { p.TenantId, p.ResolvedAt });
        });

        modelBuilder.Entity<FinancePeriod>(b =>
        {
            b.ToTable("finance_period");
            b.HasKey(p => new { p.TenantId, p.Period });
            b.Property(p => p.Period).HasMaxLength(7).IsUnicode(false).IsFixedLength();
            b.Property(p => p.Source).HasMaxLength(40);
        });

        modelBuilder.Entity<SelectionSet>(b =>
        {
            b.ToTable("selection_set", t => t.HasCheckConstraint("ck_selection_set_source_json", "ISJSON(source_json) = 1"));
            b.HasKey(s => new { s.TenantId, s.SelectionId });
            b.Property(s => s.SourceKind).HasMaxLength(20);
            b.Property(s => s.CreatedBy).HasMaxLength(200);
            b.Property(s => s.ExecutedOperation).HasMaxLength(60);
            b.Property(s => s.SourceDocumentType).HasMaxLength(40);
            b.Property(s => s.ApproverRef).HasMaxLength(200);
            b.HasIndex(s => new { s.TenantId, s.CreatedAt });
            b.HasIndex(s => new { s.TenantId, s.ExecutedCommandId });
        });

        modelBuilder.Entity<SelectionSetMember>(b =>
        {
            b.ToTable("selection_set_member");
            b.HasKey(m => new { m.TenantId, m.SelectionId, m.UnitId });
            b.Property(m => m.Serial).HasMaxLength(100);
            b.Property(m => m.Outcome).HasMaxLength(20);
            b.Property(m => m.Reason).HasMaxLength(100);
            b.Property(m => m.Detail).HasMaxLength(400);
            b.HasIndex(m => new { m.TenantId, m.SelectionId, m.Serial });
            b.HasIndex(m => new { m.TenantId, m.MergedFromUnitId });
        });

        modelBuilder.Entity<CorrectionRecord>(b =>
        {
            b.ToTable("correction_record", t =>
            {
                t.HasCheckConstraint("ck_correction_record_evidence_refs_json", "ISJSON(evidence_refs) = 1");
                t.HasCheckConstraint("ck_correction_record_approvers_json", "ISJSON(approvers) = 1");
                t.HasCheckConstraint("ck_correction_record_root_cause", "root_cause_category IS NULL OR root_cause_category BETWEEN 1 AND 7");
            });
            b.HasKey(c => new { c.TenantId, c.CorrectionId });
            b.Property(c => c.Command).HasMaxLength(60);
            b.Property(c => c.ReasonCode).HasMaxLength(60);
            b.Property(c => c.RequestStatus).HasMaxLength(30);
            b.Property(c => c.InitiatedVia).HasMaxLength(20);
            b.Property(c => c.InitiatingRole).HasMaxLength(40);
            b.Property(c => c.ActorRef).HasMaxLength(200);
            b.Property(c => c.Note).HasMaxLength(2000);
            b.HasIndex(c => new { c.TenantId, c.EventTime, c.RootCauseCategory, c.InitiatingRole, c.InitiatedVia });
            b.HasIndex(c => new { c.TenantId, c.EventTime, c.RootCauseCategory, c.RequestStatus });
            b.HasIndex(c => new { c.TenantId, c.CorrectsCorrectionId });
        });

        modelBuilder.Entity<UnitMergeRecord>(b =>
        {
            b.ToTable("unit_merge_record", t => t.HasCheckConstraint("ck_unit_merge_record_reversal_data_json", "ISJSON(reversal_data) = 1"));
            b.HasKey(m => new { m.TenantId, m.CorrectionId });
            b.HasIndex(m => new { m.TenantId, m.SurvivorUnitId, m.AbsorbedUnitId, m.ReversedAt });
            b.HasIndex(m => new { m.TenantId, m.ReversedByCorrectionId });
        });

        modelBuilder.Entity<SerialAllocationReassignment>(b =>
        {
            b.ToTable("serial_allocation_reassignment", t => t.HasCheckConstraint("ck_serial_allocation_reassignment_attributes_json", "ISJSON(attributes) = 1"));
            b.HasKey(r => new { r.TenantId, r.ReassignmentId });
            b.Property(r => r.Mode).HasMaxLength(30);
            b.Property(r => r.Scheme).HasMaxLength(80);
            b.Property(r => r.SerialValue).HasMaxLength(200);
            b.Property(r => r.PreviousProductionOrderType).HasMaxLength(40);
            b.Property(r => r.NewProductionOrderType).HasMaxLength(40);
            b.Property(r => r.ActorRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId, r.Scheme, r.SerialOrdinal }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.CorrectionId, r.Scheme, r.SerialOrdinal }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.SerialRangeId, r.SerialOrdinal, r.EventTime });
        });

        modelBuilder.Entity<SerialAllocationOverride>(b =>
        {
            b.ToTable("serial_allocation_override");
            b.HasKey(r => new { r.TenantId, r.Scheme, r.SerialOrdinal });
            b.Property(r => r.Scheme).HasMaxLength(80);
            b.Property(r => r.SerialValue).HasMaxLength(200);
            b.Property(r => r.ProductionOrderType).HasMaxLength(40);
            b.HasIndex(r => new { r.TenantId, r.ProductionOrderType, r.ProductionOrderId, r.ProductionOrderLine });
            b.HasIndex(r => new { r.TenantId, r.SerialRangeId, r.SerialOrdinal });
            b.HasIndex(r => new { r.TenantId, r.CommissionedUnitId });
        });

        modelBuilder.Entity<ItemTrackingModeChange>(b =>
        {
            b.ToTable("item_tracking_mode_change", t => t.HasCheckConstraint("ck_item_tracking_mode_change_stock_coverage_json", "ISJSON(stock_coverage) = 1"));
            b.HasKey(c => new { c.TenantId, c.ChangeId });
            b.Property(c => c.PreviousTrackingMode).HasMaxLength(20);
            b.Property(c => c.TrackingMode).HasMaxLength(20);
            b.Property(c => c.ActorRef).HasMaxLength(200);
            b.HasIndex(c => new { c.TenantId, c.CorrectionId }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.ItemId, c.EffectiveAt });
        });

        modelBuilder.Entity<ManufacturingMessageReceipt>(b =>
        {
            b.ToTable("manufacturing_message_receipt", t => t.HasCheckConstraint("ck_manufacturing_message_receipt_result_json", "ISJSON(result_json) = 1"));
            b.HasKey(r => new { r.TenantId, r.MessageId });
            b.Property(r => r.MessageType).HasMaxLength(100);
            b.Property(r => r.PayloadHash).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(r => new { r.TenantId, r.ProcessedAt });
        });

        modelBuilder.Entity<CorrectionRecordUnit>(b =>
        {
            b.ToTable("correction_record_unit");
            b.HasKey(c => new { c.TenantId, c.CorrectionId, c.UnitId });
            b.HasOne<CorrectionRecord>().WithMany().HasForeignKey(c => new { c.TenantId, c.CorrectionId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => new { c.TenantId, c.ItemId, c.CorrectionId });
        });

        modelBuilder.Entity<SearchEntry>(b =>
        {
            b.ToTable("search_entry", t => t.HasCheckConstraint("ck_search_entry_metadata_json", "ISJSON(metadata) = 1"));
            b.HasKey(e => new { e.TenantId, e.EntityType, e.EntityId });
            b.Property(e => e.EntityType).HasMaxLength(40);
            b.Property(e => e.Title).HasMaxLength(200);
            b.Property(e => e.Subtitle).HasMaxLength(400);
            b.Property(e => e.Href).HasMaxLength(200);
            // entry_id carries a single-column unique index (ux_search_entry_entry_id) that the platform's tenant-key
            // convention would reject: SQL Server full-text needs exactly that shape as its KEY INDEX. The index is
            // created by SQL in the Search migration, not here; tenant isolation still comes from the RLS policy.
            b.HasIndex(e => new { e.TenantId, e.UpdatedAt });
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId });
        });

        modelBuilder.Entity<SearchTerm>(b =>
        {
            b.ToTable("search_term");
            b.HasKey(t => new { t.TenantId, t.Term, t.EntityType, t.EntityId, t.Kind });
            b.Property(t => t.Term).HasMaxLength(200);
            b.Property(t => t.EntityType).HasMaxLength(40);
            b.Property(t => t.Kind).HasMaxLength(20);
            b.Property(t => t.Detail).HasMaxLength(60);
            b.HasIndex(t => new { t.TenantId, t.EntityType, t.EntityId });
            b.HasIndex(t => new { t.TenantId, t.MergedFromUnitId });
        });

        // MOD-06 (P2-T02).
        modelBuilder.Entity<CoverageFamily>(b =>
        {
            b.ToTable("coverage_family");
            b.HasKey(f => new { f.TenantId, f.FamilyCode });
            b.Property(f => f.FamilyCode).HasMaxLength(40);
            b.Property(f => f.Name).HasMaxLength(100);
            b.Property(f => f.AccountingTag).HasMaxLength(20);
        });

        modelBuilder.Entity<ReplacementPolicy>(b =>
        {
            b.ToTable("replacement_policy");
            b.HasKey(r => new { r.TenantId, r.ReplacementPolicyId });
            b.Property(r => r.Code).HasMaxLength(60);
            b.Property(r => r.Rule).HasMaxLength(40);
            b.Property(r => r.Description).HasMaxLength(400);
            b.HasIndex(r => new { r.TenantId, r.Code }).IsUnique();
        });

        modelBuilder.Entity<WarrantyPolicyVersion>(b =>
        {
            b.ToTable("warranty_policy_version", t =>
            {
                t.IsTemporal(h => h.UseHistoryTable("warranty_policy_version_history"));
                foreach (var column in new[] { "usage_limits", "fallback_chain", "coverage", "exclusions", "region_scope", "refurbished_policies" })
                {
                    t.HasCheckConstraint($"ck_warranty_policy_version_{column}_json", $"ISJSON({column}) = 1");
                }

                t.HasCheckConstraint("ck_warranty_policy_version_eligibility_json", "eligibility IS NULL OR ISJSON(eligibility) = 1");
                t.HasCheckConstraint("ck_warranty_policy_version_amendment_value_json", "amendment_value IS NULL OR ISJSON(amendment_value) = 1");
            });
            b.HasKey(v => new { v.TenantId, v.PolicyVersionId });
            b.Property(v => v.PolicyCode).HasMaxLength(60);
            b.Property(v => v.Name).HasMaxLength(200);
            b.Property(v => v.CoverageFamily).HasMaxLength(40);
            b.Property(v => v.EntitlementType).HasMaxLength(30);
            b.Property(v => v.Status).HasMaxLength(20);
            b.Property(v => v.RuleVersion).HasMaxLength(20);
            b.Property(v => v.DurationUnit).HasMaxLength(10);
            b.Property(v => v.EndOfMonthRule).HasMaxLength(10);
            b.Property(v => v.DurationBasis).HasMaxLength(10);
            b.Property(v => v.StartTrigger).HasMaxLength(40);
            b.Property(v => v.Transferable).HasMaxLength(30);
            b.Property(v => v.SettlementFormula).HasMaxLength(60);
            b.Property(v => v.AssuranceOrService).HasMaxLength(20);
            b.Property(v => v.ReferenceTimeZoneRule).HasMaxLength(20);
            b.Property(v => v.CreatedBy).HasMaxLength(200);
            b.Property(v => v.PublishedBy).HasMaxLength(200);
            // A policy's versions are numbered once; resolution by contract date walks (code, applies_to_contracts_from).
            b.HasIndex(v => new { v.TenantId, v.PolicyCode, v.Version }).IsUnique();
            b.HasIndex(v => new { v.TenantId, v.PolicyCode, v.AppliesToContractsFrom });
            b.HasIndex(v => new { v.TenantId, v.CoverageFamily, v.Status });
        });

        modelBuilder.Entity<AccrualCohort>(b =>
        {
            b.ToTable("accrual_cohort");
            b.HasKey(c => new { c.TenantId, c.CohortId });
            b.Property(c => c.Period).HasMaxLength(7).IsFixedLength().IsUnicode(false);
            b.Property(c => c.AssuranceOrService).HasMaxLength(20);
            b.HasIndex(c => new { c.TenantId, c.ObligorEntityId, c.ItemId, c.Period, c.AssuranceOrService }).IsUnique();
        });

        modelBuilder.Entity<Entitlement>(b =>
        {
            b.ToTable("entitlement", t =>
            {
                t.IsTemporal(h => h.UseHistoryTable("entitlement_history"));
                t.HasCheckConstraint("ck_entitlement_coverage_json", "ISJSON(coverage) = 1");
                t.HasCheckConstraint("ck_entitlement_region_scope_json", "ISJSON(region_scope) = 1");
                // A closed entitlement names why; a live one names nothing (MOD-06 state machine).
                t.HasCheckConstraint("ck_entitlement_closed_reason", "(status = 'closed' AND closed_reason IS NOT NULL) OR (status <> 'closed' AND closed_reason IS NULL)");
                t.HasCheckConstraint("ck_entitlement_term", "end_date IS NULL OR (start_date IS NOT NULL AND end_date >= start_date)");
            });
            b.HasKey(e => new { e.TenantId, e.EntitlementId });
            b.Property(e => e.Scope).HasMaxLength(20);
            b.Property(e => e.ScopeItemClass).HasMaxLength(60);
            b.Property(e => e.Type).HasMaxLength(30);
            b.Property(e => e.CoverageFamily).HasMaxLength(40);
            b.Property(e => e.Direction).HasMaxLength(20);
            b.Property(e => e.OwnerPartyType).HasMaxLength(30);
            b.Property(e => e.PayerPartyType).HasMaxLength(30);
            b.Property(e => e.StartTrigger).HasMaxLength(40);
            b.Property(e => e.StartBasis).HasMaxLength(40);
            b.Property(e => e.StartEvidenceRef).HasMaxLength(200);
            b.Property(e => e.StartTimeZone).HasMaxLength(64);
            b.Property(e => e.ComputedByRuleVersion).HasMaxLength(20);
            b.Property(e => e.DurationUnit).HasMaxLength(10);
            b.Property(e => e.EndOfMonthRule).HasMaxLength(10);
            b.Property(e => e.DurationBasis).HasMaxLength(10);
            b.Property(e => e.Transferable).HasMaxLength(30);
            b.Property(e => e.ConditionAtCreation).HasMaxLength(20);
            b.Property(e => e.RefurbishedMatchedRow).HasMaxLength(1000);
            b.Property(e => e.RefurbishedFloorSource).HasMaxLength(20);
            b.Property(e => e.RefurbishedStatementKind).HasMaxLength(30);
            b.Property(e => e.RevenueScheduleRef).HasMaxLength(100);
            b.Property(e => e.AssuranceOrService).HasMaxLength(20);
            b.Property(e => e.Status).HasMaxLength(20);
            b.Property(e => e.ClosedReason).HasMaxLength(40);
            b.Property(e => e.Confidence).HasMaxLength(10);
            b.Property(e => e.Provenance).HasMaxLength(20);
            b.Property(e => e.SourceDocumentType).HasMaxLength(40);
            b.Property(e => e.CustomerPoRef).HasMaxLength(100);
            // Invariant 1: at most one live (not closed) entitlement per unit and coverage family; families stack beside each other.
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.CoverageFamily }).IsUnique().HasFilter("[status] <> 'closed'");
            // Every entitlement a unit has carried, newest first, for the Entitlements panel and the policy stack.
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.CreatedAt });
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.CoverageFamily, e.Status, e.ClosedReason });
            b.HasIndex(e => new { e.TenantId, e.RootEntitlementId });
            b.HasIndex(e => new { e.TenantId, e.SourceEventId, e.ServiceParentEntitlementId }).IsUnique()
                .HasFilter("[source_event_id] IS NOT NULL AND [service_parent_entitlement_id] IS NOT NULL");
            b.HasIndex(e => new { e.TenantId, e.InstalledParentUnitId, e.InstallationLinkId });
            b.HasIndex(e => new { e.TenantId, e.OwnerPartyRef, e.Status });
            b.HasIndex(e => new { e.TenantId, e.PolicyVersionId });
            b.HasIndex(e => new { e.TenantId, e.SourceDocumentType, e.SourceDocumentId });
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId });
        });

        modelBuilder.Entity<EntitlementRequirement>(b =>
        {
            b.ToTable("entitlement_requirement", t => t.HasCheckConstraint("ck_entitlement_requirement_status", "status IN ('open', 'satisfied', 'exception', 'waived')"));
            b.HasKey(r => new { r.TenantId, r.RequirementId });
            b.Property(r => r.SourceDocumentType).HasMaxLength(40);
            b.Property(r => r.Status).HasMaxLength(20);
            b.Property(r => r.MissingInput).HasMaxLength(40);
            b.Property(r => r.Detail).HasMaxLength(1000);
            b.Property(r => r.WaivedBy).HasMaxLength(200);
            b.Property(r => r.WaiverReason).HasMaxLength(500);
            b.HasIndex(r => new { r.TenantId, r.UnitId, r.OwnershipEpisodeId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.Status, r.RaisedAt });
            b.HasIndex(r => new { r.TenantId, r.SourceDocumentType, r.SourceDocumentId, r.Status });
            b.HasIndex(r => new { r.TenantId, r.MergedFromUnitId });
        });

        modelBuilder.Entity<EntitlementGapRun>(b =>
        {
            b.ToTable("entitlement_gap_run", t =>
            {
                t.HasCheckConstraint("ck_entitlement_gap_run_filters_json", "ISJSON(filters_json) = 1");
                t.HasCheckConstraint("ck_entitlement_gap_run_counts_json", "ISJSON(counts_json) = 1");
                t.HasCheckConstraint("ck_entitlement_gap_run_trigger", "run_trigger IN ('migration_load', 'on_demand', 'daily_job')");
            });
            b.HasKey(r => new { r.TenantId, r.RunId });
            b.Property(r => r.RunTrigger).HasMaxLength(20);
            b.HasIndex(r => new { r.TenantId, r.RunAt });
        });

        modelBuilder.Entity<BulkApprovalRequest>(b =>
        {
            b.ToTable("bulk_approval_request", t =>
            {
                t.HasCheckConstraint("ck_bulk_approval_request_status", "status IN ('pending', 'approved', 'rejected', 'withdrawn', 'stale')");
                t.HasCheckConstraint("ck_bulk_approval_request_arguments_json", "ISJSON(arguments_json) = 1");
            });
            b.HasKey(r => new { r.TenantId, r.RequestId });
            b.Property(r => r.Operation).HasMaxLength(60);
            b.Property(r => r.SourceDocumentType).HasMaxLength(40);
            b.Property(r => r.Status).HasMaxLength(20);
            b.Property(r => r.Fingerprint).HasMaxLength(64).IsFixedLength();
            b.Property(r => r.Rules).HasMaxLength(400);
            b.Property(r => r.RequestedBy).HasMaxLength(200);
            b.Property(r => r.RequestedRoles).HasMaxLength(400);
            b.Property(r => r.Note).HasMaxLength(1000);
            b.Property(r => r.DecidedBy).HasMaxLength(200);
            b.Property(r => r.DecisionNote).HasMaxLength(1000);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.Status, r.RequestedAt });
            // One open request per selection and operation; a decided one never blocks a new request.
            b.HasIndex(r => new { r.TenantId, r.SelectionId, r.Operation }).IsUnique().HasFilter("[status] = 'pending'");
        });

        modelBuilder.Entity<WorkbenchRequest>(b =>
        {
            b.ToTable("workbench_request", t =>
            {
                t.HasCheckConstraint("ck_workbench_request_status", "status IN ('draft', 'awaiting_approval', 'approved', 'rejected', 'committed', 'superseded', 'unclassified', 'no_command_fits')");
                foreach (var column in new[] { "target_json", "answers_json", "evidence_json", "preview_json", "initiator_roles_json", "required_approver_roles_json", "approvals_json" })
                {
                    t.HasCheckConstraint($"ck_workbench_request_{column}", $"ISJSON({column}) = 1");
                }

                t.HasCheckConstraint("ck_workbench_request_result_json", "result_json IS NULL OR ISJSON(result_json) = 1");
            });
            b.HasKey(r => new { r.TenantId, r.RequestId });
            b.Property(r => r.Choice).HasMaxLength(60);
            b.Property(r => r.Command).HasMaxLength(60);
            b.Property(r => r.Reason).HasMaxLength(60);
            b.Property(r => r.Note).HasMaxLength(2000);
            b.Property(r => r.PreviewHash).HasMaxLength(64).IsFixedLength();
            b.Property(r => r.Status).HasMaxLength(30);
            b.Property(r => r.InitiatedBy).HasMaxLength(200);
            b.Property(r => r.RejectedBy).HasMaxLength(200);
            b.Property(r => r.DecisionNote).HasMaxLength(2000);
            b.Property(r => r.CommittedBy).HasMaxLength(200);
            b.Property(r => r.UnclassifiedDescription).HasMaxLength(2000);
            b.Property(r => r.ClassifiedBy).HasMaxLength(200);
            b.Property(r => r.EscalatedBy).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.Status, r.InitiatedAt });
        });

        modelBuilder.Entity<WarrantyLineException>(b =>
        {
            b.ToTable("warranty_line_exception", t => t.HasCheckConstraint("ck_warranty_line_exception_status", "status IN ('open', 'resolved', 'waived')"));
            b.HasKey(e => new { e.TenantId, e.ExceptionId });
            b.Property(e => e.DocumentType).HasMaxLength(40);
            b.Property(e => e.TargetDocumentType).HasMaxLength(40);
            b.Property(e => e.Status).HasMaxLength(20);
            b.Property(e => e.MissingInput).HasMaxLength(40);
            b.Property(e => e.Detail).HasMaxLength(1000);
            b.Property(e => e.ResolvedBy).HasMaxLength(200);
            b.HasIndex(e => new { e.TenantId, e.DocumentType, e.DocumentId, e.DocumentLine }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.Status, e.RaisedAt });
            b.HasIndex(e => new { e.TenantId, e.TargetDocumentType, e.TargetDocumentId, e.Status });
        });

        modelBuilder.Entity<EntitlementSuspension>(b =>
        {
            b.ToTable("entitlement_suspension", t => t.HasCheckConstraint("ck_entitlement_suspension_dates", "to_date IS NULL OR to_date >= from_date"));
            b.HasKey(s => new { s.TenantId, s.SuspensionId });
            b.Property(s => s.Reason).HasMaxLength(60);
            b.Property(s => s.ActorRef).HasMaxLength(200);
            // One open suspension per entitlement at a time.
            b.HasIndex(s => new { s.TenantId, s.EntitlementId }).IsUnique().HasFilter("[to_date] IS NULL");
            b.HasIndex(s => new { s.TenantId, s.EntitlementId, s.FromDate });
        });

        modelBuilder.Entity<EntitlementRebase>(b =>
        {
            b.ToTable("entitlement_rebase");
            b.HasKey(r => new { r.TenantId, r.RebaseId });
            b.Property(r => r.EvidenceRef).HasMaxLength(200);
            b.Property(r => r.ActorRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.EntitlementId, r.At });
        });

        modelBuilder.Entity<EntitlementAmendment>(b =>
        {
            b.ToTable("entitlement_amendment");
            b.HasKey(a => new { a.TenantId, a.AmendmentId });
            b.Property(a => a.Field).HasMaxLength(40);
            b.Property(a => a.BeforeValue).HasMaxLength(400);
            b.Property(a => a.AfterValue).HasMaxLength(400);
            b.Property(a => a.ReasonCode).HasMaxLength(30);
            b.Property(a => a.SourceDocumentType).HasMaxLength(40);
            b.Property(a => a.ApprovedBy).HasMaxLength(200);
            b.Property(a => a.CustomerNotificationRef).HasMaxLength(100);
            b.Property(a => a.ActorRef).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.EntitlementId, a.At });
        });

        modelBuilder.Entity<EntitlementUsageLimit>(b =>
        {
            b.ToTable("entitlement_usage_limit", t => t.HasCheckConstraint("ck_entitlement_usage_limit_values", "limit_value >= 0 AND used_value >= 0"));
            b.HasKey(u => new { u.TenantId, u.EntitlementId, u.MeterType });
            b.Property(u => u.MeterType).HasMaxLength(40);
            b.Property(u => u.Period).HasMaxLength(20);
            b.Property(u => u.LimitValue).HasPrecision(18, 3);
            b.Property(u => u.UsedValue).HasPrecision(18, 3);
        });

        // The view reads only RLS-protected unit_current and entitlement rows. SQL Server applies those tables'
        // tenant predicates while expanding the view; the explicit tenant key remains leading for query plans.
        modelBuilder.Entity<WarrantyCurrent>(b =>
        {
            b.HasNoKey();
            // YojanaDbContext applies the schema's table convention before this configuration. Keep that mapping out
            // of migrations: the object is created explicitly as a view in WarrantyCurrent.
            b.ToTable("warranty_current", t => t.ExcludeFromMigrations());
            b.ToView("warranty_current");
            b.Property(w => w.CoverageFamily).HasMaxLength(40);
            b.Property(w => w.Type).HasMaxLength(30);
            b.Property(w => w.Status).HasMaxLength(20);
            b.Property(w => w.DurationBasis).HasMaxLength(10);
        });

        // MOD-07 claim half (P4-T02).
        modelBuilder.Entity<Claim>(b =>
        {
            b.ToTable("claim", t =>
            {
                t.HasCheckConstraint("ck_claim_entitlement_ids_json", "ISJSON(entitlement_ids) = 1");
                t.HasCheckConstraint("ck_claim_status", "status IN ('submitted', 'adjudicating', 'approved', 'partially_approved', 'denied', 'settled', 'closed')");
                t.HasCheckConstraint("ck_claim_coverage_source", "coverage_source IN ('warranty', 'recall', 'goodwill', 'contract', 'chargeable')");
            });
            b.HasKey(c => new { c.TenantId, c.ClaimId });
            b.Property(c => c.ServiceCountry).HasMaxLength(2).IsUnicode(false);
            b.Property(c => c.Currency).HasMaxLength(3).IsUnicode(false);
            b.Property(c => c.CoverageSource).HasMaxLength(20);
            b.Property(c => c.Remedy).HasMaxLength(20);
            b.Property(c => c.Status).HasMaxLength(30);
            b.Property(c => c.DenialReason).HasMaxLength(100);
            b.Property(c => c.RecoveryNotRaisedReason).HasMaxLength(40).IsUnicode(false);
            b.Property(c => c.ReserveTag).HasMaxLength(100);
            b.Property(c => c.CampaignCoverReason).HasMaxLength(40).IsUnicode(false);
            b.Property(c => c.SubmittedBy).HasMaxLength(200);
            b.Property(c => c.AdjudicatedBy).HasMaxLength(200);
            b.Property(c => c.ReviewerRole).HasMaxLength(80);
            b.Property(c => c.ClaimNumber).HasMaxLength(20).IsUnicode(false);
            b.HasIndex(c => new { c.TenantId, c.CommandId }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.ClaimNumber }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.UnitId, c.Status, c.ClaimDate });
            b.HasIndex(c => new { c.TenantId, c.CampaignId });
            b.HasIndex(c => new { c.TenantId, c.Status, c.SubmittedAt });
        });

        modelBuilder.Entity<ClaimLine>(b =>
        {
            b.ToTable("claim_line", t =>
            {
                t.HasCheckConstraint("ck_claim_line_measurements_json", "ISJSON(measurements) = 1");
                t.HasCheckConstraint("ck_claim_line_amounts", "requested_amount >= 0 AND approved_amount >= 0 AND approved_amount <= requested_amount");
                t.HasCheckConstraint("ck_claim_line_coverage_pct", "coverage_pct >= 0 AND coverage_pct <= 100");
                t.HasCheckConstraint("ck_claim_line_coverage_source", "coverage_source IN ('warranty', 'recall', 'goodwill', 'contract', 'chargeable')");
            });
            b.HasKey(l => new { l.TenantId, l.ClaimLineId });
            b.Property(l => l.Kind).HasMaxLength(20);
            b.Property(l => l.CoverageFamily).HasMaxLength(40);
            b.Property(l => l.Description).HasMaxLength(500);
            b.Property(l => l.RequestedAmount).HasPrecision(18, 2);
            b.Property(l => l.CoveragePct).HasPrecision(5, 2);
            b.Property(l => l.ApprovedAmount).HasPrecision(18, 2);
            b.Property(l => l.CoverageSource).HasMaxLength(20);
            b.Property(l => l.SettlementFormula).HasMaxLength(60);
            b.Property(l => l.DenialReason).HasMaxLength(100);
            b.HasIndex(l => new { l.TenantId, l.ClaimId, l.Sequence }).IsUnique();
            b.HasIndex(l => new { l.TenantId, l.UnitId, l.EntitlementId });
            b.HasOne<Claim>().WithMany().HasForeignKey(l => new { l.TenantId, l.ClaimId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClaimEvidence>(b =>
        {
            b.ToTable("claim_evidence");
            b.HasKey(e => new { e.TenantId, e.EvidenceId });
            b.Property(e => e.Kind).HasMaxLength(40);
            b.Property(e => e.Reference).HasMaxLength(500);
            b.Property(e => e.Description).HasMaxLength(1000);
            b.Property(e => e.ActorRef).HasMaxLength(200);
            b.HasIndex(e => new { e.TenantId, e.ClaimId, e.RecordedAt });
            b.HasOne<Claim>().WithMany().HasForeignKey(e => new { e.TenantId, e.ClaimId }).OnDelete(DeleteBehavior.Cascade);
        });

        // P5-T15: policy re-adjudication history. Work references remain logical because RMA, Claims and the future
        // Service module own their lifecycles independently; the source event plus work line is the replay boundary.
        modelBuilder.Entity<CoverageDecision>(b =>
        {
            b.ToTable("coverage_decision", t =>
            {
                t.HasCheckConstraint("ck_coverage_decision_before_json", "ISJSON(before_decision) = 1");
                t.HasCheckConstraint("ck_coverage_decision_after_json", "ISJSON(after_decision) = 1");
                t.HasCheckConstraint("ck_coverage_decision_reason", "reason IN ('entitlement_changed')");
                t.HasCheckConstraint("ck_coverage_decision_work_type", "work_type IN ('rma', 'claim', 'service_order')");
            });
            b.HasKey(d => new { d.TenantId, d.CoverageDecisionId });
            b.Property(d => d.Reason).HasMaxLength(40).IsUnicode(false);
            b.Property(d => d.WorkType).HasMaxLength(30).IsUnicode(false);
            b.Property(d => d.ActorRef).HasMaxLength(200);
            b.HasIndex(d => new { d.TenantId, d.SourceEventId, d.WorkType, d.WorkId, d.WorkLineKey }).IsUnique();
            b.HasIndex(d => new { d.TenantId, d.UnitId, d.DecidedAt });
            b.HasIndex(d => new { d.TenantId, d.WorkType, d.WorkId, d.WorkLineId, d.DecidedAt });
        });

        modelBuilder.Entity<RecoveryClaim>(b =>
        {
            b.ToTable("recovery_claim", t =>
            {
                t.HasCheckConstraint("ck_recovery_claim_amounts", "requested_amount >= 0 AND (approved_amount IS NULL OR (approved_amount >= 0 AND approved_amount <= requested_amount))");
                t.HasCheckConstraint("ck_recovery_claim_status", "status IN ('raised', 'approved')");
            });
            b.HasKey(r => new { r.TenantId, r.RecoveryClaimId });
            b.Property(r => r.RequestedAmount).HasPrecision(18, 2);
            b.Property(r => r.ApprovedAmount).HasPrecision(18, 2);
            b.Property(r => r.Currency).HasMaxLength(3).IsUnicode(false);
            b.Property(r => r.Status).HasMaxLength(20);
            b.Property(r => r.ApprovedBy).HasMaxLength(200);
            b.Property(r => r.VendorReference).HasMaxLength(100);
            b.Property(r => r.VendorNote).HasMaxLength(500);
            b.Property(r => r.RecoveryNumber).HasMaxLength(20).IsUnicode(false);
            b.HasIndex(r => new { r.TenantId, r.RecoveryNumber }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.CustomerClaimId, r.ClaimLineId, r.InboundEntitlementId, r.ChildUnitId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.Status, r.RaisedAt });
            b.HasIndex(r => new { r.TenantId, r.VendorPartyRef, r.Status });
            b.HasOne<Claim>().WithMany().HasForeignKey(r => new { r.TenantId, r.CustomerClaimId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NumberSeries>(b =>
        {
            b.ToTable("number_series", t => t.HasCheckConstraint("ck_number_series_last_value", "last_value >= 0"));
            b.HasKey(n => new { n.TenantId, n.Series, n.Year });
            b.Property(n => n.Series).HasMaxLength(10).IsUnicode(false);
        });

        // MOD-07 campaign half (P4-T02/P4-T07). Selected recipients are snapshots, not navigation-backed live values:
        // changing ownership later must not silently rewrite who received an already-selected campaign notice.
        modelBuilder.Entity<Campaign>(b =>
        {
            b.ToTable("campaign", t =>
            {
                t.HasCheckConstraint("ck_campaign_coverage_source", "coverage_source IN ('recall', 'goodwill')");
                t.HasCheckConstraint("ck_campaign_cover_pct", "parts_pct BETWEEN 0 AND 100 AND labour_pct BETWEEN 0 AND 100 AND travel_pct BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_campaign_reason_code", "reason_code IN ('safety', 'reliability', 'firmware', 'regulatory', 'goodwill')");
                t.HasCheckConstraint("ck_campaign_status", "status IN ('draft', 'selected', 'active', 'closed')");
            });
            b.HasKey(c => new { c.TenantId, c.CampaignId });
            b.Property(c => c.Code).HasMaxLength(60);
            b.Property(c => c.Name).HasMaxLength(200);
            b.Property(c => c.CoverageSource).HasMaxLength(20);
            b.Property(c => c.ReserveTag).HasMaxLength(100);
            // -1 is outside the accepted contract range and is the EF sentinel, so a legitimate 0% is written rather
            // than mistaken for an unset CLR decimal and replaced by the database's 100% default.
            b.Property(c => c.PartsPct).HasPrecision(5, 2).HasDefaultValue(100m).HasSentinel(-1m);
            b.Property(c => c.LabourPct).HasPrecision(5, 2).HasDefaultValue(100m).HasSentinel(-1m);
            b.Property(c => c.TravelPct).HasPrecision(5, 2).HasDefaultValue(100m).HasSentinel(-1m);
            b.Property(c => c.ReasonCode).HasMaxLength(20).IsUnicode(false);
            b.Property(c => c.ReasonNote).HasMaxLength(500);
            b.Property(c => c.RunsUntil).HasColumnType("date");
            b.Property(c => c.Status).HasMaxLength(20);
            b.Property(c => c.CreatedBy).HasMaxLength(200);
            b.Property(c => c.UpdatedBy).HasMaxLength(200);
            b.HasIndex(c => new { c.TenantId, c.Code }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.Status, c.UpdatedAt });
        });

        modelBuilder.Entity<CampaignCriterion>(b =>
        {
            b.ToTable("campaign_criteria", t => t.HasCheckConstraint("ck_campaign_criteria_shape", """
                (kind = 'identifier_range' AND scheme IS NOT NULL AND identifier_type IS NOT NULL AND range_start IS NOT NULL AND range_end IS NOT NULL AND range_start_ordinal IS NOT NULL AND range_end_ordinal IS NOT NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)
                OR (kind = 'lot' AND lot_id IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)
                OR (kind = 'child' AND ((child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)) AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)
                OR (kind = 'firmware' AND firmware_key IS NOT NULL AND firmware_value IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL)
                """));
            b.HasKey(c => new { c.TenantId, c.CriterionId });
            b.Property(c => c.Kind).HasMaxLength(30);
            b.Property(c => c.Scheme).HasMaxLength(80);
            b.Property(c => c.IdentifierType).HasMaxLength(40);
            b.Property(c => c.RangeStart).HasMaxLength(100);
            b.Property(c => c.RangeEnd).HasMaxLength(100);
            b.Property(c => c.FirmwareKey).HasMaxLength(100);
            b.Property(c => c.FirmwareValue).HasMaxLength(200);
            b.HasOne<Campaign>().WithMany().HasForeignKey(c => new { c.TenantId, c.CampaignId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => new { c.TenantId, c.CampaignId, c.Kind });
            b.HasIndex(c => new { c.TenantId, c.LotId, c.CampaignId });
            b.HasIndex(c => new { c.TenantId, c.ChildUnitId, c.CampaignId });
            b.HasIndex(c => new { c.TenantId, c.ChildItemId, c.CampaignId });
        });

        modelBuilder.Entity<CampaignUnit>(b =>
        {
            b.ToTable("campaign_unit");
            b.HasKey(u => new { u.TenantId, u.CampaignId, u.UnitId });
            b.Property(u => u.OwnerPartyType).HasMaxLength(30);
            b.Property(u => u.CustodianPartyType).HasMaxLength(30);
            b.Property(u => u.SelectedBy).HasMaxLength(200);
            b.HasOne<Campaign>().WithMany().HasForeignKey(u => new { u.TenantId, u.CampaignId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(u => new { u.TenantId, u.CampaignId, u.SelectedAt });
            b.HasIndex(u => new { u.TenantId, u.OwnerPartyRef, u.CampaignId });
            b.HasIndex(u => new { u.TenantId, u.CustodianPartyRef, u.CampaignId });
            b.HasIndex(u => new { u.TenantId, u.SelectionCommandId });
        });

        modelBuilder.Entity<Rma>(b =>
        {
            b.ToTable("rma", t => t.HasCheckConstraint("ck_rma_stage", "stage IN ('requested', 'authorised', 'in_transit', 'received', 'dispositioned', 'closed')"));
            b.HasKey(r => new { r.TenantId, r.RmaId });
            b.Property(r => r.RmaNumber).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.Stage).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.ServiceCountry).HasMaxLength(2).IsUnicode(false);
            b.Property(r => r.Currency).HasMaxLength(3).IsUnicode(false);
            b.Property(r => r.RequestedBy).HasMaxLength(200);
            b.Property(r => r.LastActorRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.RmaNumber }).IsUnique().HasFilter("[rma_number] IS NOT NULL");
            b.HasIndex(r => new { r.TenantId, r.Stage, r.RequestedAt });
            b.HasIndex(r => new { r.TenantId, r.ReturningPartyRef, r.Stage });
        });

        modelBuilder.Entity<RmaLine>(b =>
        {
            b.ToTable("rma_line", t =>
            {
                t.HasCheckConstraint("ck_rma_line_amounts", "requested_amount >= 0 AND (coverage_pct IS NULL OR coverage_pct BETWEEN 0 AND 100) AND (approved_amount IS NULL OR (approved_amount >= 0 AND approved_amount <= requested_amount))");
                t.HasCheckConstraint("ck_rma_line_outcome", "receipt_outcome IS NULL OR receipt_outcome IN ('accepted', 'mismatch', 'unauthorised', 'counterfeit')");
                t.HasCheckConstraint("ck_rma_line_disposition", "disposition IS NULL OR disposition IN ('return_to_stock', 'refurbish', 'repair_and_return', 'scrap', 'return_to_vendor', 'credit', 'refund', 'replace', 'reject', 'restock_as_new')");
            });
            b.HasKey(l => new { l.TenantId, l.RmaLineId });
            b.Property(l => l.OriginalSaleDocumentType).HasMaxLength(60);
            b.Property(l => l.PriceAllocation).HasPrecision(18, 2);
            b.Property(l => l.CoverageFamily).HasMaxLength(40);
            b.Property(l => l.ClaimLineKind).HasMaxLength(20);
            b.Property(l => l.RequestedAmount).HasPrecision(18, 2);
            b.Property(l => l.Description).HasMaxLength(500);
            b.Property(l => l.CoveragePct).HasPrecision(5, 2);
            b.Property(l => l.ApprovedAmount).HasPrecision(18, 2);
            b.Property(l => l.CoverageSource).HasMaxLength(20);
            b.Property(l => l.DenialReason).HasMaxLength(100);
            b.Property(l => l.ReceiptOutcome).HasMaxLength(20);
            b.Property(l => l.ConditionGrade).HasMaxLength(30);
            b.Property(l => l.ValuationRule).HasMaxLength(60);
            b.Property(l => l.Disposition).HasMaxLength(30);
            b.Property(l => l.EntitlementClosedReason).HasMaxLength(40);
            b.HasIndex(l => new { l.TenantId, l.RmaId, l.Sequence }).IsUnique();
            b.HasIndex(l => new { l.TenantId, l.RequestedUnitId, l.OwnershipEpisodeId });
            b.HasIndex(l => new { l.TenantId, l.AuthorisedUnitId, l.ReceiptOutcome });
            b.HasIndex(l => new { l.TenantId, l.ParentUnitId, l.ParentIncomplete });
            b.HasOne<Rma>().WithMany().HasForeignKey(l => new { l.TenantId, l.RmaId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReturnReceipt>(b =>
        {
            b.ToTable("return_receipt", t =>
            {
                t.HasCheckConstraint("ck_return_receipt_outcome", "outcome IN ('accepted', 'mismatch', 'unauthorised', 'counterfeit')");
                t.HasCheckConstraint("ck_return_receipt_identity", "(received_unit_id IS NOT NULL AND provisional_unit_id IS NULL) OR (received_unit_id IS NULL AND provisional_unit_id IS NOT NULL)");
            });
            b.HasKey(r => new { r.TenantId, r.ReturnReceiptId });
            b.Property(r => r.Outcome).HasMaxLength(20);
            b.Property(r => r.ConditionGrade).HasMaxLength(30);
            b.Property(r => r.ValuationRule).HasMaxLength(60);
            b.Property(r => r.ExceptionNote).HasMaxLength(1000);
            b.Property(r => r.PostingPeriod).HasMaxLength(7).IsUnicode(false).IsFixedLength();
            b.Property(r => r.ReceivedBy).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId, r.RmaLineId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.RmaId, r.BusinessTime });
            b.HasIndex(r => new { r.TenantId, r.ReceivedUnitId, r.BusinessTime });
            b.HasOne<Rma>().WithMany().HasForeignKey(r => new { r.TenantId, r.RmaId }).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<RmaLine>().WithMany().HasForeignKey(r => new { r.TenantId, r.RmaLineId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReturnReason>(b =>
        {
            b.ToTable("return_reason", t =>
            {
                t.HasCheckConstraint("ck_return_reason_restock_fee", "restock_fee_rule IN ('none', 'fixed', 'percentage') AND (restock_fee_value IS NULL OR restock_fee_value >= 0)");
                t.HasCheckConstraint("ck_return_reason_doa_window", "doa_window_days IS NULL OR doa_window_days >= 0");
            });
            b.HasKey(r => new { r.TenantId, r.Code });
            b.Property(r => r.Code).HasMaxLength(40).IsUnicode(false);
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.RestockFeeRule).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.RestockFeeValue).HasPrecision(18, 2);
            b.HasIndex(r => new { r.TenantId, r.Active, r.Name });
        });

        // MOD-08 condition and service history (P5-T02/T09/T11). These immutable rows sit beside the unit ledger;
        // unit_current remains the operational current-grade projection written by Unit.Graded.
        modelBuilder.Entity<UnitConditionEvent>(b =>
        {
            b.ToTable("unit_condition_event", t => t.HasCheckConstraint("ck_unit_condition_event_grade", "grade IN ('new', 'open_box', 'refurbished_a', 'refurbished_b', 'refurbished_c', 'used', 'remanufactured', 'defective', 'scrap')"));
            b.HasKey(e => new { e.TenantId, e.ConditionEventId });
            b.Property(e => e.Grade).HasMaxLength(30).IsUnicode(false);
            b.Property(e => e.FCode).HasMaxLength(2).IsUnicode(false);
            b.Property(e => e.CCode).HasMaxLength(2).IsUnicode(false);
            b.Property(e => e.ValuationRule).HasMaxLength(100);
            b.Property(e => e.InspectorRef).HasMaxLength(200);
            b.HasIndex(e => new { e.TenantId, e.CommandId }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.GradedAt });
        });

        modelBuilder.Entity<SanitisationRecord>(b =>
        {
            b.ToTable("sanitisation_record", t => t.HasCheckConstraint("ck_sanitisation_record_method", "method IN ('clear', 'purge', 'destroy')"));
            b.HasKey(r => new { r.TenantId, r.SanitisationRecordId });
            b.Property(r => r.Manufacturer).HasMaxLength(200);
            b.Property(r => r.Model).HasMaxLength(200);
            b.Property(r => r.MediaSerial).HasMaxLength(200);
            b.Property(r => r.MediaType).HasMaxLength(80);
            b.Property(r => r.Method).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.Tool).HasMaxLength(200);
            b.Property(r => r.ToolVersion).HasMaxLength(80);
            b.Property(r => r.VerificationMethod).HasMaxLength(200);
            b.Property(r => r.OperatorRef).HasMaxLength(200);
            b.Property(r => r.CertificateRef).HasMaxLength(300);
            b.Property(r => r.SignatureRef).HasMaxLength(300);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.UnitId, r.SanitisedAt });
        });

        modelBuilder.Entity<TestRecord>(b =>
        {
            b.ToTable("test_record", t =>
            {
                t.HasCheckConstraint("ck_test_record_result", "result IN ('pass', 'fail')");
                t.HasCheckConstraint("ck_test_record_measurements_json", "ISJSON(measurements_json) = 1");
            });
            b.HasKey(r => new { r.TenantId, r.TestRecordId });
            b.Property(r => r.TestKind).HasMaxLength(100);
            b.Property(r => r.Result).HasMaxLength(10).IsUnicode(false);
            b.Property(r => r.Equipment).HasMaxLength(300);
            b.Property(r => r.OperatorRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.UnitId, r.TestedAt });
        });

        modelBuilder.Entity<UsageReading>(b =>
        {
            b.ToTable("usage_reading", t => t.HasCheckConstraint("ck_usage_reading_value", "value >= 0"));
            b.HasKey(r => new { r.TenantId, r.UsageReadingId });
            b.Property(r => r.MeterKind).HasMaxLength(80);
            b.Property(r => r.Value).HasPrecision(18, 6);
            b.Property(r => r.Unit).HasMaxLength(40);
            b.Property(r => r.OperatorRef).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.UnitId, r.MeterKind, r.ReadAt });
        });

        modelBuilder.Entity<UnitAttributeHistory>(b =>
        {
            b.ToTable("unit_attribute_history");
            b.HasKey(h => new { h.TenantId, h.AttributeHistoryId });
            b.Property(h => h.Key).HasMaxLength(100);
            b.Property(h => h.Value).HasMaxLength(2000);
            b.Property(h => h.ActorRef).HasMaxLength(200);
            b.HasIndex(h => new { h.TenantId, h.CommandId, h.Key }).IsUnique();
            b.HasIndex(h => new { h.TenantId, h.UnitId, h.Key, h.EffectiveFrom });
        });

        modelBuilder.Entity<LifeEpisode>(b =>
        {
            b.ToTable("life_episode", t =>
            {
                t.HasCheckConstraint("ck_life_episode_kind", "kind IN ('refurbish', 'remanufacture', 'rework')");
                t.HasCheckConstraint("ck_life_episode_dates", "ended_at IS NULL OR ended_at >= started_at");
            });
            b.HasKey(e => new { e.TenantId, e.LifeEpisodeId });
            b.Property(e => e.Kind).HasMaxLength(20).IsUnicode(false);
            b.Property(e => e.ActorRef).HasMaxLength(200);
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.StartedAt });
            b.HasIndex(e => new { e.TenantId, e.WorkOrderId });
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.Kind }).IsUnique().HasFilter("[ended_at] IS NULL");
        });

        // P5 refurbishment block — kept together so sibling DbContext changes merge mechanically.
        modelBuilder.Entity<RefurbishmentWorkOrder>(b =>
        {
            b.ToTable("refurbishment_work_order", t =>
            {
                t.HasCheckConstraint("ck_refurbishment_work_order_kind", "kind IN ('refurbish', 'remanufacture', 'rework')");
                t.HasCheckConstraint("ck_refurbishment_work_order_status", "status IN ('open', 'released', 'cancelled')");
                t.HasCheckConstraint("ck_refurbishment_work_order_identity", "identity_policy IN ('same_unit_same_serial', 'same_unit_new_serial', 'new_unit')");
                t.HasCheckConstraint("ck_refurbishment_work_order_cycles", "cycle_number >= 1 AND max_cycles >= 1");
            });
            b.HasKey(w => new { w.TenantId, w.WorkOrderId });
            b.Property(w => w.WorkOrderNumber).HasMaxLength(24).IsUnicode(false);
            b.Property(w => w.Kind).HasMaxLength(20).IsUnicode(false);
            b.Property(w => w.SourceDocumentType).HasMaxLength(40);
            b.Property(w => w.SelectionKind).HasMaxLength(30).IsUnicode(false);
            b.Property(w => w.SelectionReference).HasMaxLength(200);
            b.Property(w => w.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(w => w.IdentityPolicy).HasMaxLength(40).IsUnicode(false);
            b.Property(w => w.ReplacementPolicyCode).HasMaxLength(60);
            b.Property(w => w.OpenedBy).HasMaxLength(200);
            b.Property(w => w.ReleasedBy).HasMaxLength(200);
            b.Property(w => w.Grade).HasMaxLength(30).IsUnicode(false);
            b.Property(w => w.CancelledBy).HasMaxLength(200);
            b.Property(w => w.CancellationReason).HasMaxLength(500);
            b.HasIndex(w => new { w.TenantId, w.OpenCommandId, w.TargetUnitId }).IsUnique();
            b.HasIndex(w => new { w.TenantId, w.WorkOrderNumber }).IsUnique();
            b.HasIndex(w => new { w.TenantId, w.TargetUnitId, w.OpenedAt });
            b.HasIndex(w => new { w.TenantId, w.Status, w.OpenedAt });
            b.HasIndex(w => new { w.TenantId, w.SourceRmaLineId });
            b.HasIndex(w => new { w.TenantId, w.ReleaseCommandId }).IsUnique().HasFilter("[release_command_id] IS NOT NULL");
            b.HasIndex(w => new { w.TenantId, w.CancelCommandId }).IsUnique().HasFilter("[cancel_command_id] IS NOT NULL");
        });

        modelBuilder.Entity<RefurbishmentOperation>(b =>
        {
            b.ToTable("refurbishment_operation", t => t.HasCheckConstraint("ck_refurbishment_operation_status", "status IN ('open', 'completed')"));
            b.HasKey(o => new { o.TenantId, o.OperationId });
            b.Property(o => o.Description).HasMaxLength(500);
            b.Property(o => o.TechnicianRef).HasMaxLength(200);
            b.Property(o => o.PlannedHours).HasPrecision(9, 2);
            b.Property(o => o.ActualHours).HasPrecision(9, 2);
            b.Property(o => o.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(o => o.SignedBy).HasMaxLength(200);
            b.HasIndex(o => new { o.TenantId, o.CommandId }).IsUnique();
            b.HasIndex(o => new { o.TenantId, o.CompleteCommandId }).IsUnique().HasFilter("[complete_command_id] IS NOT NULL");
            b.HasIndex(o => new { o.TenantId, o.WorkOrderId, o.Sequence }).IsUnique();
            b.HasOne<RefurbishmentWorkOrder>().WithMany().HasForeignKey(o => new { o.TenantId, o.WorkOrderId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefurbishmentPart>(b =>
        {
            b.ToTable("refurbishment_part", t =>
            {
                t.HasCheckConstraint("ck_refurbishment_part_action", "action IN ('removed', 'installed')");
                t.HasCheckConstraint("ck_refurbishment_part_destination", "destination IS NULL OR destination IN ('spares', 'scrap', 'vendor_return')");
                t.HasCheckConstraint("ck_refurbishment_part_identity", "(child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)");
            });
            b.HasKey(p => new { p.TenantId, p.PartId });
            b.Property(p => p.Action).HasMaxLength(20).IsUnicode(false);
            b.Property(p => p.Quantity).HasPrecision(18, 6);
            b.Property(p => p.Slot).HasMaxLength(40);
            b.Property(p => p.Destination).HasMaxLength(30).IsUnicode(false);
            b.Property(p => p.UnitCost).HasPrecision(18, 2);
            b.Property(p => p.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.HasIndex(p => new { p.TenantId, p.CommandId }).IsUnique();
            b.HasIndex(p => new { p.TenantId, p.WorkOrderId, p.RecordedAt });
            b.HasIndex(p => new { p.TenantId, p.ChildUnitId, p.RecordedAt });
            b.HasOne<RefurbishmentWorkOrder>().WithMany().HasForeignKey(p => new { p.TenantId, p.WorkOrderId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefurbishmentLabour>(b =>
        {
            b.ToTable("refurbishment_labour", t => t.HasCheckConstraint("ck_refurbishment_labour_amounts", "hours > 0 AND hourly_rate >= 0 AND amount >= 0"));
            b.HasKey(l => new { l.TenantId, l.LabourId });
            b.Property(l => l.Hours).HasPrecision(9, 2);
            b.Property(l => l.HourlyRate).HasPrecision(18, 2);
            b.Property(l => l.Amount).HasPrecision(18, 2);
            b.Property(l => l.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.Property(l => l.TechnicianRef).HasMaxLength(200);
            b.HasIndex(l => new { l.TenantId, l.CommandId }).IsUnique();
            b.HasIndex(l => new { l.TenantId, l.WorkOrderId, l.RecordedAt });
            b.HasOne<RefurbishmentWorkOrder>().WithMany().HasForeignKey(l => new { l.TenantId, l.WorkOrderId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefurbishmentCost>(b =>
        {
            b.ToTable("refurbishment_cost", t =>
            {
                t.HasCheckConstraint("ck_refurbishment_cost_kind", "kind IN ('graded_receipt', 'part', 'labour', 'other')");
                t.HasCheckConstraint("ck_refurbishment_cost_amount", "amount >= 0");
            });
            b.HasKey(c => new { c.TenantId, c.CostId });
            b.Property(c => c.Kind).HasMaxLength(30).IsUnicode(false);
            b.Property(c => c.Amount).HasPrecision(18, 2);
            b.Property(c => c.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.Property(c => c.Description).HasMaxLength(500);
            b.Property(c => c.PostedBy).HasMaxLength(200);
            b.HasIndex(c => new { c.TenantId, c.CommandId }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.WorkOrderId, c.PostedAt });
            b.HasOne<RefurbishmentWorkOrder>().WithMany().HasForeignKey(c => new { c.TenantId, c.WorkOrderId }).OnDelete(DeleteBehavior.Cascade);
        });
        // End P5 refurbishment block.

        modelBuilder.Entity<WarrantyReplacement>(b =>
        {
            b.ToTable("warranty_replacement", t =>
            {
                t.HasCheckConstraint("ck_warranty_replacement_source_kind", "source_kind IN ('claim', 'rma', 'loan')");
                t.HasCheckConstraint("ck_warranty_replacement_status", "status IN ('proposed', 'shipped', 'cancelled')");
                t.HasCheckConstraint("ck_warranty_replacement_fee", "restocking_fee_amount >= 0");
            });
            b.HasKey(row => new { row.TenantId, row.WarrantyReplacementId });
            b.Property(row => row.ReplacementNumber).HasMaxLength(24).IsUnicode(false);
            b.Property(row => row.SourceKind).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.SourceNumber).HasMaxLength(40);
            b.Property(row => row.OriginalInvoiceType).HasMaxLength(40);
            b.Property(row => row.ServiceCountry).HasMaxLength(2).IsUnicode(false).IsFixedLength();
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ReplacementPolicyCode).HasMaxLength(60);
            b.Property(row => row.ReturnReasonCode).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.RestockingFeeRule).HasMaxLength(100);
            b.Property(row => row.RestockingFeeAmount).HasPrecision(18, 2);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.Property(row => row.ShippedBy).HasMaxLength(200);
            b.Property(row => row.StockPostingReference).HasMaxLength(100);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ReplacementNumber }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.SourceKind, row.SourceId });
            b.HasIndex(row => new { row.TenantId, row.Status, row.CreatedAt });
            b.HasIndex(row => new { row.TenantId, row.ShipCommandId }).IsUnique().HasFilter("[ship_command_id] IS NOT NULL");
        });

        modelBuilder.Entity<ReplacementLink>(b =>
        {
            b.ToTable("replacement_link", t =>
            {
                t.HasCheckConstraint("ck_replacement_link_direction", "direction IN ('customer_facing', 'vendor_facing')");
                t.HasCheckConstraint("ck_replacement_link_reason", "replacement_reason IN ('warranty_defect', 'dead_on_arrival', 'seller_error', 'goodwill', 'recall')");
                t.HasCheckConstraint("ck_replacement_link_state", "state IN ('proposed', 'awaiting_return', 'closed_returned', 'closed_billed', 'cancelled', 'corrected')");
                t.HasCheckConstraint("ck_replacement_link_source_kind", "source_kind IN ('claim', 'rma', 'loan')");
                t.HasCheckConstraint("ck_replacement_link_units", "original_unit_id <> replacement_unit_id");
                t.HasCheckConstraint("ck_replacement_link_price_policy", "price_policy IN ('zero', 'difference', 'full')");
                t.HasCheckConstraint("ck_replacement_link_zero_price", "price_policy <> 'zero' OR difference_amount = 0");
                t.HasCheckConstraint("ck_replacement_link_condition_allowed", "replacement_condition_allowed IN ('new', 'new_or_refurbished', 'any')");
            });
            b.HasKey(row => new { row.TenantId, row.ReplacementLinkId });
            b.Property(row => row.Direction).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ReplacementReason).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.State).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.SourceKind).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.SourceNumber).HasMaxLength(40);
            b.Property(row => row.PolicyApplied).HasMaxLength(60);
            b.Property(row => row.PricePolicy).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.DifferenceAmount).HasPrecision(18, 2);
            b.Property(row => row.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.Property(row => row.NonReturnPriceBasis).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.NonReturnAmount).HasPrecision(18, 2);
            b.Property(row => row.BilledPriceBasis).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.BilledAmount).HasPrecision(18, 2);
            b.Property(row => row.BilledBy).HasMaxLength(200);
            b.Property(row => row.ReplacementConditionAllowed).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.ReplacementCondition).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.Property(row => row.ShippedBy).HasMaxLength(200);
            b.HasOne<WarrantyReplacement>().WithMany().HasForeignKey(row => new { row.TenantId, row.WarrantyReplacementId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(row => new { row.TenantId, row.WarrantyReplacementId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.OriginalUnitId, row.CreatedAt });
            b.HasIndex(row => new { row.TenantId, row.ReplacementUnitId, row.CreatedAt });
            b.HasIndex(row => new { row.TenantId, row.SourceKind, row.SourceId });
            b.HasIndex(row => new { row.TenantId, row.State, row.ReturnDue });
            b.HasIndex(row => new { row.TenantId, row.PreviousReplacementLinkId });
            b.HasIndex(row => new { row.TenantId, row.OverdueEventId }).IsUnique().HasFilter("[overdue_event_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.ReturnDueCommandId }).IsUnique().HasFilter("[return_due_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.BillCommandId }).IsUnique().HasFilter("[bill_command_id] IS NOT NULL");
        });

        modelBuilder.Entity<PartyReplacementHold>(b =>
        {
            b.ToTable("party_replacement_hold");
            b.HasKey(row => new { row.TenantId, row.PartyReplacementHoldId });
            b.Property(row => row.Reason).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.SetBy).HasMaxLength(200);
            b.Property(row => row.LiftedBy).HasMaxLength(200);
            b.Property(row => row.LiftReason).HasMaxLength(500);
            b.HasIndex(row => new { row.TenantId, row.PartyRef, row.LiftedAt });
            b.HasIndex(row => new { row.TenantId, row.PartyRef }).IsUnique().HasFilter("[lifted_at] IS NULL");
            b.HasIndex(row => new { row.TenantId, row.SetCommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.LiftCommandId }).IsUnique().HasFilter("[lift_command_id] IS NOT NULL");
        });

        modelBuilder.Entity<WarrantyReplacementLine>(b =>
        {
            b.ToTable("warranty_replacement_line", t =>
            {
                t.HasCheckConstraint("ck_warranty_replacement_line_kind", "kind IN ('replacement', 'service_revenue')");
                t.HasCheckConstraint("ck_warranty_replacement_line_amounts", "quantity > 0 AND unit_amount >= 0 AND line_amount >= 0");
            });
            b.HasKey(row => new { row.TenantId, row.LineId });
            b.Property(row => row.Kind).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.Description).HasMaxLength(300);
            b.Property(row => row.Quantity).HasPrecision(18, 6);
            b.Property(row => row.UnitAmount).HasPrecision(18, 2);
            b.Property(row => row.LineAmount).HasPrecision(18, 2);
            b.Property(row => row.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.Property(row => row.RevenueRule).HasMaxLength(100);
            b.HasOne<WarrantyReplacement>().WithMany().HasForeignKey(row => new { row.TenantId, row.WarrantyReplacementId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ReplacementLink>().WithMany().HasForeignKey(row => new { row.TenantId, row.ReplacementLinkId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.WarrantyReplacementId, row.Sequence }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ReplacementLinkId });
            b.HasIndex(row => new { row.TenantId, row.ItemId });
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Shape of both ledger tables. The clustered key is (tenant, event_time, event_id) so inserts append by
    /// business time; every nonclustered index carries <c>event_time</c> so it can be aligned to the partition
    /// scheme (a requirement for a unique index on a partitioned table, and for SWITCH). Index names follow the
    /// platform convention (<c>ix_</c>/<c>ux_</c> + table + columns); LedgerDdl creates them with the same names.
    /// </summary>
    private static void ConfigureLedger<T>(ModelBuilder modelBuilder, string table)
        where T : UnitEventRow
    {
        modelBuilder.Entity<T>(b =>
        {
            b.ToTable(table, t => t.HasCheckConstraint($"ck_{table}_attributes_json", "attributes IS NULL OR ISJSON(attributes) = 1"));
            b.HasKey(e => new { e.TenantId, e.EventTime, e.EventId });
            b.Property(e => e.EventType).HasMaxLength(60);
            b.Property(e => e.BizStep).HasMaxLength(40);
            b.Property(e => e.Disposition).HasMaxLength(40);
            b.Property(e => e.ActorRef).HasMaxLength(200);
            b.Property(e => e.DocumentType).HasMaxLength(40);
            b.Property(e => e.PostingPeriod).HasMaxLength(7).IsUnicode(false).IsFixedLength();
            b.Property(e => e.IdempotencyKey).HasMaxLength(64).IsUnicode(false);
            b.Property(e => e.RecordedTime).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasIndex(e => new { e.TenantId, e.IdempotencyKey, e.EventTime }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.EventTime }).IsDescending(false, false, true)
                .IncludeProperties(e => new { e.EventType, e.BizStep, e.Disposition, e.LocationId, e.Seq, e.RecordedTime });
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId, e.EventTime });
        });
    }
}

/// <summary>Design-time factory for `dotnet ef`; the connection string is never opened at design time.</summary>
public sealed class SerialisationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SerialisationDbContext>
{
    public SerialisationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SerialisationDbContext>()
            .UseSqlServer("Server=localhost;Database=design_time;Encrypt=True;TrustServerCertificate=True",
                sql => sql.MigrationsHistoryTable("__migrations", SerialisationDbContext.SchemaName))
            .Options;
        return new SerialisationDbContext(options, NoTenantContext.Instance);
    }
}
