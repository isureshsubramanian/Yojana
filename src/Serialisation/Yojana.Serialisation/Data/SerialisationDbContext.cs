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

    public DbSet<CorrectionRecord> Corrections => Set<CorrectionRecord>();

    public DbSet<UnitMergeRecord> UnitMerges => Set<UnitMergeRecord>();

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
            // Identity rule 1: one open identifier per (tenant, scheme, scope, normalised value) across all units.
            b.HasIndex(i => new { i.TenantId, i.Scheme, i.ScopeKey, i.NormalisedValue })
                .IsUnique().HasFilter("[valid_to] IS NULL AND ([type] <> 'vendor_serial' OR [duplicate_acknowledged] = 0)");
            // Invariant 2: at most one open identifier per (unit, type).
            b.HasIndex(i => new { i.TenantId, i.UnitId, i.Type })
                .IsUnique().HasFilter("[valid_to] IS NULL");
            // All identifiers of a unit, open or superseded (search re-index, history). The filtered index above cannot
            // answer it, so without this the read range-scanned the tenant: 2.5 s per 100-unit chunk at 1 M units (bench
            // 2026-10-16), starving the receipt it ran beside.
            b.HasIndex(i => new { i.TenantId, i.UnitId });
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
                t.HasCheckConstraint("ck_correction_record_root_cause", "root_cause_category BETWEEN 1 AND 7");
            });
            b.HasKey(c => new { c.TenantId, c.CorrectionId });
            b.Property(c => c.Command).HasMaxLength(60);
            b.Property(c => c.ReasonCode).HasMaxLength(60);
            b.Property(c => c.InitiatedVia).HasMaxLength(20);
            b.Property(c => c.InitiatingRole).HasMaxLength(40);
            b.Property(c => c.ActorRef).HasMaxLength(200);
            b.Property(c => c.Note).HasMaxLength(2000);
            b.HasIndex(c => new { c.TenantId, c.EventTime, c.RootCauseCategory, c.InitiatingRole, c.InitiatedVia });
            b.HasIndex(c => new { c.TenantId, c.CorrectsCorrectionId });
        });

        modelBuilder.Entity<UnitMergeRecord>(b =>
        {
            b.ToTable("unit_merge_record", t => t.HasCheckConstraint("ck_unit_merge_record_reversal_data_json", "ISJSON(reversal_data) = 1"));
            b.HasKey(m => new { m.TenantId, m.CorrectionId });
            b.HasIndex(m => new { m.TenantId, m.SurvivorUnitId, m.AbsorbedUnitId, m.ReversedAt });
            b.HasIndex(m => new { m.TenantId, m.ReversedByCorrectionId });
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
