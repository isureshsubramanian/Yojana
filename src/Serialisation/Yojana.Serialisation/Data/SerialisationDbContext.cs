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

    /// <summary>
    /// Filter of every merged-from index. Only rows moved by a unit merge carry the column, and the merge reads it only
    /// by equality with the absorbed unit, so the indexes hold merged rows alone instead of one entry per row of the
    /// table (11.5 GB on the benchmark ledger, maintained by every append).
    /// </summary>
    public const string MergedFromFilter = "[merged_from_unit_id] IS NOT NULL";
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

    public DbSet<LedgerBaseOrderReference> LedgerBaseOrderReferences => Set<LedgerBaseOrderReference>();

    public DbSet<LedgerBaseOrderBackfillCheckpoint> LedgerBaseOrderBackfillCheckpoints => Set<LedgerBaseOrderBackfillCheckpoint>();

    public DbSet<UnitCurrent> Current => Set<UnitCurrent>();

    public DbSet<PendingShipmentLine> PendingShipmentLines => Set<PendingShipmentLine>();

    public DbSet<FinancePeriod> FinancePeriods => Set<FinancePeriod>();

    public DbSet<SelectionSet> SelectionSets => Set<SelectionSet>();

    public DbSet<SelectionSetMember> SelectionSetMembers => Set<SelectionSetMember>();

    // P7 imports block (MOD-12). Files are accessed only through IImportFileStore; these DbSets own persistence.
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportRowResult> ImportRowResults => Set<ImportRowResult>();
    public DbSet<ColumnMapping> ColumnMappings => Set<ColumnMapping>();
    public DbSet<ThreePlFeedMessage> ThreePlFeedMessages => Set<ThreePlFeedMessage>();
    public DbSet<ImportFileDownloadAudit> ImportFileDownloadAudits => Set<ImportFileDownloadAudit>();

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

    public DbSet<UnitCoverDeviation> CoverDeviations => Set<UnitCoverDeviation>();
    public DbSet<UnitCoverDeviationChange> CoverDeviationChanges => Set<UnitCoverDeviationChange>();
    public DbSet<UnitCoverDeviationValue> CoverDeviationValues => Set<UnitCoverDeviationValue>();
    public DbSet<CoverDeviationBackfillCheckpoint> CoverDeviationBackfillCheckpoints => Set<CoverDeviationBackfillCheckpoint>();

    public DbSet<Entitlement> Entitlements => Set<Entitlement>();

    public DbSet<EntitlementRequirement> EntitlementRequirements => Set<EntitlementRequirement>();

    public DbSet<EntitlementGapRun> EntitlementGapRuns => Set<EntitlementGapRun>();

    public DbSet<WarrantyLineException> WarrantyLineExceptions => Set<WarrantyLineException>();

    public DbSet<EntitlementSuspension> EntitlementSuspensions => Set<EntitlementSuspension>();

    public DbSet<EntitlementRebase> EntitlementRebases => Set<EntitlementRebase>();

    public DbSet<EntitlementAmendment> EntitlementAmendments => Set<EntitlementAmendment>();

    public DbSet<EntitlementUsageLimit> EntitlementUsageLimits => Set<EntitlementUsageLimit>();

    public DbSet<WarrantyCurrent> WarrantyCurrent => Set<WarrantyCurrent>();

    public DbSet<PolicyCoverItemRow> PolicyCoverItems => Set<PolicyCoverItemRow>();
    public DbSet<PolicyCoverItemCause> PolicyCoverItemCauses => Set<PolicyCoverItemCause>();
    public DbSet<ClaimCauseRow> ClaimCauses => Set<ClaimCauseRow>();
    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();

    public DbSet<ClaimEvidence> ClaimEvidence => Set<ClaimEvidence>();

    public DbSet<CoverageDecision> CoverageDecisions => Set<CoverageDecision>();

    public DbSet<RecoveryClaim> RecoveryClaims => Set<RecoveryClaim>();
    public DbSet<NumberSeries> NumberSeries => Set<NumberSeries>();
    public DbSet<NumberSeriesSetting> NumberSeriesSettings => Set<NumberSeriesSetting>();

    public DbSet<ReplacementLink> ReplacementLinks => Set<ReplacementLink>();

    public DbSet<WarrantyReplacement> WarrantyReplacements => Set<WarrantyReplacement>();

    public DbSet<WarrantyReplacementLine> WarrantyReplacementLines => Set<WarrantyReplacementLine>();

    public DbSet<PartyReplacementHold> PartyReplacementHolds => Set<PartyReplacementHold>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignCriterion> CampaignCriteria => Set<CampaignCriterion>();

    public DbSet<CampaignUnit> CampaignUnits => Set<CampaignUnit>();

    public DbSet<Rma> Rmas => Set<Rma>();
    public DbSet<RmaLine> RmaLines => Set<RmaLine>();
    public DbSet<RmaStageCounter> RmaStageCounters => Set<RmaStageCounter>();
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

    // P6 certificate block. Keep sibling-owned label tables outside this block for a mechanical merge.
    public DbSet<CertificateInstance> CertificateInstances => Set<CertificateInstance>();
    public DbSet<CertificateTrigger> CertificateTriggers => Set<CertificateTrigger>();
    public DbSet<CertificateSignature> CertificateSignatures => Set<CertificateSignature>();
    public DbSet<ReprintLog> ReprintLogs => Set<ReprintLog>();
    public DbSet<CertificateRun> CertificateRuns => Set<CertificateRun>();
    public DbSet<CertificateRunUnit> CertificateRunUnits => Set<CertificateRunUnit>();
    public DbSet<CertificateTemplate> CertificateTemplates => Set<CertificateTemplate>();
    public DbSet<CertificateTemplateVersion> CertificateTemplateVersions => Set<CertificateTemplateVersion>();
    public DbSet<TenantAsset> TenantAssets => Set<TenantAsset>();
    // End P6 certificate block.
    // P6 labels block. Kept together so the certificate sibling can merge its DbSets mechanically.
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();
    public DbSet<LabelTemplateVersion> LabelTemplateVersions => Set<LabelTemplateVersion>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    // End P6 labels block.
    // P6 jurisdiction-rules block. These tables are tenant-owned configuration; application code consumes only ports.
    public DbSet<RegionGroup> RegionGroups => Set<RegionGroup>();
    public DbSet<JurisdictionRule> JurisdictionRules => Set<JurisdictionRule>();
    public DbSet<RulePack> RulePacks => Set<RulePack>();
    public DbSet<RulePackRule> RulePackRules => Set<RulePackRule>();
    // End P6 jurisdiction-rules block.
    // P7 portal block. Customer assertions are intake evidence only; PortalQueries always authorises through ownership episodes.
    public DbSet<DisclosurePolicy> DisclosurePolicies => Set<DisclosurePolicy>();
    public DbSet<PortalRegistration> PortalRegistrations => Set<PortalRegistration>();
    public DbSet<CustomerAssertion> CustomerAssertions => Set<CustomerAssertion>();
    public DbSet<CustomerReconciliation> CustomerReconciliations => Set<CustomerReconciliation>();
    public DbSet<CustomerReconciliationRow> CustomerReconciliationRows => Set<CustomerReconciliationRow>();
    // End P7 portal block.

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
            b.Property(u => u.CreatedEventTime).HasPrecision(3);
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
            b.HasIndex(i => new { i.TenantId, i.MergedFromUnitId }).HasFilter(MergedFromFilter);
            // Lookup by any identifier, open or superseded; prefix search uses LIKE 'value%' and the browse path can
            // stop after TOP(n+1) without sorting the tenant's whole identifier set.
            b.HasIndex(i => new { i.TenantId, i.NormalisedValue, i.Type, i.UnitId })
                .IncludeProperties(i => new { i.Scheme, i.RawValue, i.ScopeKey, i.ValidTo, i.SupersededByIdentifierId });
        });

        modelBuilder.Entity<UnitItemAssignment>(b =>
        {
            b.ToTable("unit_item_assignment");
            b.HasKey(a => new { a.TenantId, a.AssignmentId });
            b.Property(a => a.Reason).HasMaxLength(20);
            b.HasIndex(a => new { a.TenantId, a.UnitId }).IsUnique().HasFilter("[valid_to] IS NULL");
            b.HasIndex(a => new { a.TenantId, a.ItemId, a.ValidTo });
            b.HasIndex(a => new { a.TenantId, a.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(l => new { l.TenantId, l.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(r => new { r.TenantId, r.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(m => new { m.TenantId, m.MergedFromUnitId }).HasFilter(MergedFromFilter);
        });

        modelBuilder.Entity<DisclosurePolicy>(b =>
        {
            b.ToTable("disclosure_policy", t => t.HasCheckConstraint("ck_disclosure_policy_tier",
                "[tier] IN ('public','owner','internal')"));
            b.HasKey(row => new { row.TenantId, row.Field });
            b.Property(row => row.Field).HasMaxLength(100);
            b.Property(row => row.Tier).HasMaxLength(20);
            b.Property(row => row.UpdatedAt).HasPrecision(3);
            b.Property(row => row.UpdatedBy).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.Tier, row.Enabled });
        });

        modelBuilder.Entity<PortalRegistration>(b =>
        {
            b.ToTable("portal_registration", t =>
            {
                t.HasCheckConstraint("ck_portal_registration_evidence_kind", "evidence_kind IN ('delivery', 'installation')");
                t.HasCheckConstraint("ck_portal_registration_status", "status IN ('recorded', 'pending_review', 'approved')");
            });
            b.HasKey(row => new { row.TenantId, row.RegistrationId });
            b.Property(row => row.EvidenceKind).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.EvidenceReference).HasMaxLength(500);
            b.Property(row => row.AttachmentFileName).HasMaxLength(260);
            b.Property(row => row.AttachmentContentType).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.Status).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.SubmittedBy).HasMaxLength(200);
            b.Property(row => row.ApprovedBy).HasMaxLength(200);
            b.HasOne<Unit>().WithMany().HasForeignKey(row => new { row.TenantId, row.UnitId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Entitlement>().WithMany().HasForeignKey(row => new { row.TenantId, row.EntitlementId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ApprovalCommandId }).IsUnique().HasFilter("[approval_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.Status, row.SubmittedAt, row.RegistrationId });
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.SubmittedAt });
            b.HasIndex(row => new { row.TenantId, row.PartyRef, row.SubmittedAt });
        });

        modelBuilder.Entity<CustomerAssertion>(b =>
        {
            b.ToTable("customer_assertion", t => t.HasCheckConstraint("ck_customer_assertion_status",
                "[status] IN ('pending_verification','verified','rejected')"));
            b.HasKey(row => new { row.TenantId, row.AssertionId });
            b.Property(row => row.Status).HasMaxLength(20);
            b.Property(row => row.IdentifierType).HasMaxLength(40);
            b.Property(row => row.RawIdentifier).HasMaxLength(100);
            b.Property(row => row.NormalisedIdentifier).HasMaxLength(100);
            b.Property(row => row.CustomerPoRef).HasMaxLength(100);
            b.Property(row => row.CustomerItemRef).HasMaxLength(100);
            b.Property(row => row.Source).HasMaxLength(30);
            b.Property(row => row.Provenance).HasMaxLength(30);
            b.Property(row => row.EvidenceRef).HasMaxLength(500);
            b.Property(row => row.AssertedBy).HasMaxLength(200);
            b.Property(row => row.VerifiedBy).HasMaxLength(200);
            b.Property(row => row.RejectionReason).HasMaxLength(500);
            b.Property(row => row.RejectedBy).HasMaxLength(200);
            b.Property(row => row.AssertedAt).HasPrecision(3);
            b.Property(row => row.VerifiedAt).HasPrecision(3);
            b.Property(row => row.EvidenceDate).HasPrecision(3);
            b.Property(row => row.RejectedAt).HasPrecision(3);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.PartyRef, row.AssertedAt, row.AssertionId });
            b.HasIndex(row => new { row.TenantId, row.NormalisedIdentifier, row.IdentifierType });
        });

        modelBuilder.Entity<CustomerReconciliation>(b =>
        {
            b.ToTable("customer_reconciliation", t =>
                t.HasCheckConstraint("ck_customer_reconciliation_source_kind",
                    "[source_kind] IN ('pasted_list','import_job','selection_set')"));
            b.HasKey(row => new { row.TenantId, row.ReconciliationId });
            b.Property(row => row.SourceKind).HasMaxLength(30);
            b.Property(row => row.ReconciledAt).HasPrecision(3);
            b.Property(row => row.ReconciledBy).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.PartyRef, row.ReconciledAt, row.ReconciliationId });
        });

        modelBuilder.Entity<CustomerReconciliationRow>(b =>
        {
            b.ToTable("customer_reconciliation_row", t =>
            {
                t.HasCheckConstraint("ck_customer_reconciliation_row_outcome",
                    "[outcome] IN ('owned','owned_by_another_party','not_found','invalid')");
                t.HasCheckConstraint("ck_customer_reconciliation_row_reasons_json", "ISJSON([validation_reasons_json]) = 1");
            });
            b.HasKey(row => new { row.TenantId, row.ReconciliationId, row.Ordinal });
            b.Property(row => row.RawIdentifier).HasMaxLength(100);
            b.Property(row => row.RequestedIdentifierType).HasMaxLength(40);
            b.Property(row => row.MatchedIdentifierType).HasMaxLength(40);
            b.Property(row => row.NormalisedIdentifier).HasMaxLength(100);
            b.Property(row => row.Outcome).HasMaxLength(30);
            b.Property(row => row.ValidationReasonsJson).HasDefaultValue("[]");
            b.Property(row => row.CustomerPoRef).HasMaxLength(100);
            b.Property(row => row.CustomerItemRef).HasMaxLength(100);
            b.HasOne<CustomerReconciliation>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ReconciliationId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CustomerAssertion>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssertionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.UnitId });
            b.HasIndex(row => new { row.TenantId, row.AssertionId }).IsUnique()
                .HasFilter("[assertion_id] IS NOT NULL");
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
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId }).HasFilter(MergedFromFilter);
        });

        ConfigureLedger<UnitEvent>(modelBuilder, "unit_event");
        ConfigureLedger<UnitEventArchive>(modelBuilder, "unit_event_archive");
        ConfigureLedger<UnitEventArchiveStage>(modelBuilder, "unit_event_archive_stage");

        modelBuilder.Entity<LedgerBaseOrderReference>(b =>
        {
            b.ToTable("ledger_base_order_ref");
            b.HasKey(reference => new { reference.TenantId, reference.EventTime, reference.EventId });
            b.Property(reference => reference.BaseDocumentType).HasMaxLength(40);
            b.Property(reference => reference.EventTime).HasPrecision(3);
            b.HasIndex(reference => new
                {
                    reference.TenantId,
                    reference.BaseDocumentType,
                    reference.BaseDocumentId,
                    reference.BaseDocumentLine,
                })
                .IncludeProperties(reference => reference.UnitId);
        });

        modelBuilder.Entity<LedgerBaseOrderBackfillCheckpoint>(b =>
        {
            // TIGHT COUPLING (SQL Server checkpoint transaction): LedgerBaseOrderBackfill serialises each tenant's
            // first read/insert and later cursor advance with a transaction-owned application lock. Removing that lock
            // while this tenant primary key remains the sole checkpoint permits duplicate first-run inserts.
            b.ToTable("ledger_base_order_backfill_checkpoint");
            b.HasKey(checkpoint => checkpoint.TenantId);
            b.Property(checkpoint => checkpoint.Status).HasMaxLength(20);
            b.Property(checkpoint => checkpoint.CheckpointMonth).HasPrecision(3);
            b.Property(checkpoint => checkpoint.NewestMonth).HasPrecision(3);
            b.Property(checkpoint => checkpoint.OldestMonth).HasPrecision(3);
            b.Property(checkpoint => checkpoint.CursorEventTime).HasPrecision(3);
            b.Property(checkpoint => checkpoint.StartedAt).HasPrecision(3);
            b.Property(checkpoint => checkpoint.UpdatedAt).HasPrecision(3);
            b.Property(checkpoint => checkpoint.CompletedAt).HasPrecision(3);
        });

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
            b.HasIndex(m => new { m.TenantId, m.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(t => new { t.TenantId, t.MergedFromUnitId }).HasFilter(MergedFromFilter);
        });

        // MOD-06 (P2-T02).
        modelBuilder.Entity<CoverageFamily>(b =>
        {
            b.ToTable("coverage_family");
            b.HasKey(f => new { f.TenantId, f.FamilyCode });
            b.Property(f => f.FamilyCode).HasMaxLength(40);
            b.Property(f => f.Name).HasMaxLength(100);
            b.Property(f => f.AccountingTag).HasMaxLength(20);
            b.Property(f => f.LastModifiedBy).HasMaxLength(200);
            b.Property(f => f.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<ReplacementPolicy>(b =>
        {
            b.ToTable("replacement_policy");
            b.HasKey(r => new { r.TenantId, r.ReplacementPolicyId });
            b.Property(r => r.Code).HasMaxLength(60);
            b.Property(r => r.Rule).HasMaxLength(40);
            b.Property(r => r.Description).HasMaxLength(400);
            b.Property(r => r.LastModifiedBy).HasMaxLength(200);
            b.Property(r => r.RowVersion).IsRowVersion();
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
            b.Property(v => v.LastModifiedBy).HasMaxLength(200);
            b.Property(v => v.RowVersion).IsRowVersion();
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

        Yojana.Serialisation.Entitlements.CoverDeviationModel.Configure(modelBuilder);
        Yojana.Serialisation.Entitlements.CoverItemsModel.Configure(modelBuilder);

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
            b.Property(e => e.SalePrice).HasPrecision(19, 4);
            b.Property(e => e.SaleCurrency).HasColumnType("char(3)");
            b.Property(e => e.AssuranceOrService).HasMaxLength(20);
            b.Property(e => e.Status).HasMaxLength(20);
            b.Property(e => e.ClosedReason).HasMaxLength(40);
            b.Property(e => e.Confidence).HasMaxLength(10);
            b.Property(e => e.Provenance).HasMaxLength(20);
            b.Property(e => e.SourceDocumentType).HasMaxLength(40);
            b.Property(e => e.CustomerPoRef).HasMaxLength(100);
            // Invariant 1: at most one live (not closed) entitlement per unit and coverage family; families stack beside each other.
            // Covers the sale-time owner lookup (EntitlementRequirements.PreloadAsync): a shipment reads the live base
            // cover's owner for every unit, which without the include is a key lookup into the clustered index per unit.
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.CoverageFamily }).IsUnique().HasFilter("[status] <> 'closed'")
                .IncludeProperties(e => e.OwnerPartyRef);
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
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.HasIndex(r => new { r.TenantId, r.MergedFromUnitId }).HasFilter(MergedFromFilter);
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
            b.Property(c => c.LastModifiedBy).HasMaxLength(200);
            b.Property(c => c.ListOrderBucket)
                .HasComputedColumnSql("CASE WHEN [status] IN ('settled', 'closed') THEN 1 ELSE 0 END", stored: true);
            b.Property(c => c.RowVersion).IsRowVersion();
            b.Property(c => c.ClaimNumber).HasMaxLength(20).IsUnicode(false);
            b.HasIndex(c => new { c.TenantId, c.CommandId }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.ClaimNumber }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.UnitId, c.Status, c.ClaimDate });
            b.HasIndex(c => new { c.TenantId, c.CampaignId });
            b.HasIndex(c => new { c.TenantId, c.Status, c.SubmittedAt });
            b.HasIndex(c => new { c.TenantId, c.ListOrderBucket, c.SubmittedAt, c.ClaimId })
                .HasDatabaseName("ix_claim_tenant_id_list_order_bucket_submitted_at_claim_id");
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
            b.Property(l => l.WaitingPeriodNotAppliedReason).HasMaxLength(60);
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
            b.Property(r => r.LastModifiedBy).HasMaxLength(200);
            b.Property(r => r.RowVersion).IsRowVersion();
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

        modelBuilder.Entity<NumberSeriesSetting>(b =>
        {
            b.ToTable("number_series_setting", t => t.HasCheckConstraint("ck_number_series_setting_mode", "mode IN ('sequence', 'gap_free')"));
            b.HasKey(n => new { n.TenantId, n.Series });
            b.Property(n => n.Series).HasMaxLength(10).IsUnicode(false);
            b.Property(n => n.Mode).HasMaxLength(16).IsUnicode(false);
            b.Property(n => n.UpdatedBy).HasMaxLength(200);
            b.HasIndex(n => new { n.TenantId, n.Mode, n.Series });
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
            b.Property(c => c.RowVersion).IsRowVersion();
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
            b.ToTable("rma", t =>
            {
                t.HasCheckConstraint("ck_rma_stage", "stage IN ('requested', 'authorised', 'in_transit', 'received', 'dispositioned', 'closed')");
                t.HasCheckConstraint("ck_rma_exception_count", "exception_count >= 0 AND has_exception = CASE WHEN exception_count > 0 THEN 1 ELSE 0 END");
            });
            b.HasKey(r => new { r.TenantId, r.RmaId });
            b.Property(r => r.RmaNumber).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.Stage).HasMaxLength(20).IsUnicode(false);
            b.Property(r => r.ServiceCountry).HasMaxLength(2).IsUnicode(false);
            b.Property(r => r.Currency).HasMaxLength(3).IsUnicode(false);
            b.Property(r => r.RequestedBy).HasMaxLength(200);
            b.Property(r => r.LastActorRef).HasMaxLength(200);
            b.Property(r => r.LastModifiedBy).HasMaxLength(200);
            b.Property(r => r.RowVersion).IsRowVersion();
            b.HasIndex(r => new { r.TenantId, r.CommandId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.RmaNumber }).IsUnique().HasFilter("[rma_number] IS NOT NULL");
            b.HasIndex(r => new { r.TenantId, r.HasException, r.RequestedAt, r.RmaId })
                .IsDescending(false, true, false, false)
                .IncludeProperties(r => new { r.RmaNumber, r.Stage, r.RequestDate, r.ReturningPartyRef, r.ClosedAt,
                    r.DispositionedAt, r.ReceivedAt, r.InTransitAt, r.AuthorisedAt, r.RequestedBy, r.LastActorRef, r.ExceptionCount });
            b.HasIndex(r => new { r.TenantId, r.Stage, r.HasException, r.RequestedAt, r.RmaId })
                .IsDescending(false, false, true, false, false)
                .IncludeProperties(r => new { r.RmaNumber, r.RequestDate, r.ReturningPartyRef, r.ClosedAt,
                    r.DispositionedAt, r.ReceivedAt, r.InTransitAt, r.AuthorisedAt, r.RequestedBy, r.LastActorRef, r.ExceptionCount });
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
            b.HasIndex(l => new { l.TenantId, l.IsException, l.RmaId, l.Sequence })
                .HasFilter("[is_exception] = CAST(1 AS bit)")
                .IncludeProperties(l => new { l.ProvisionalUnitId, l.ReceiptOutcome });
            b.HasIndex(l => new { l.TenantId, l.RequestedUnitId, l.OwnershipEpisodeId });
            b.HasIndex(l => new { l.TenantId, l.AuthorisedUnitId, l.ReceiptOutcome });
            b.HasIndex(l => new { l.TenantId, l.ParentUnitId, l.ParentIncomplete });
            b.HasOne<Rma>().WithMany().HasForeignKey(l => new { l.TenantId, l.RmaId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RmaStageCounter>(b =>
        {
            b.ToTable("rma_stage_counter", t =>
            {
                t.HasCheckConstraint("ck_rma_stage_counter_stage", "stage IN ('requested', 'authorised', 'in_transit', 'received', 'dispositioned', 'closed')");
                t.HasCheckConstraint("ck_rma_stage_counter_slot", "slot BETWEEN 0 AND 15");
                t.HasCheckConstraint("ck_rma_stage_counter_counts", "document_count >= 0 AND exception_count >= 0 AND exception_count <= document_count");
            });
            b.HasKey(row => new { row.TenantId, row.Stage, row.Slot });
            b.Property(row => row.Stage).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.LastRebuiltBy).HasMaxLength(200);
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
            b.Property(r => r.LastModifiedBy).HasMaxLength(200);
            b.Property(r => r.RowVersion).IsRowVersion();
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
            b.Property(w => w.LastModifiedBy).HasMaxLength(200);
            b.Property(w => w.RowVersion).IsRowVersion();
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
            b.Property(row => row.LastModifiedBy).HasMaxLength(200);
            b.Property(row => row.RowVersion).IsRowVersion();
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
            b.HasOne<ReplacementLink>().WithMany().HasForeignKey(row => new { row.TenantId, row.CorrectedByLinkId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.WarrantyReplacementId });
            b.HasIndex(row => new { row.TenantId, row.OriginalUnitId, row.CreatedAt });
            b.HasIndex(row => new { row.TenantId, row.ReplacementUnitId, row.CreatedAt });
            b.HasIndex(row => new { row.TenantId, row.SourceKind, row.SourceId });
            b.HasIndex(row => new { row.TenantId, row.State, row.ReturnDue });
            b.HasIndex(row => new { row.TenantId, row.PreviousReplacementLinkId });
            b.HasIndex(row => new { row.TenantId, row.OverdueEventId }).IsUnique().HasFilter("[overdue_event_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.ReturnDueCommandId }).IsUnique().HasFilter("[return_due_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.BillCommandId }).IsUnique().HasFilter("[bill_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.CorrectedByLinkId });
            b.HasIndex(row => new { row.TenantId, row.CorrectionCommandId }).IsUnique().HasFilter("[correction_command_id] IS NOT NULL");
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

        // P6 certificate block. Certificate payloads are immutable; the migration adds a database trigger which permits
        // only the status transition performed when a successor is issued.
        modelBuilder.Entity<CertificateInstance>(b =>
        {
            b.ToTable("certificate_instance", t =>
            {
                t.HasTrigger("tr_certificate_instance_immutable");
                t.HasCheckConstraint("ck_certificate_instance_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                t.HasCheckConstraint("ck_certificate_instance_form_type", "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'warranty_certificate', 'custom')");
                t.HasCheckConstraint("ck_certificate_instance_payload_json", "ISJSON(payload) = 1");
                t.HasCheckConstraint("ck_certificate_instance_issuer_json", "ISJSON(issuer_snapshot) = 1");
                t.HasCheckConstraint("ck_certificate_instance_owner_json", "owner_snapshot IS NULL OR ISJSON(owner_snapshot) = 1");
                t.HasCheckConstraint("ck_certificate_instance_branding_json", "ISJSON(branding_snapshot) = 1");
                t.HasCheckConstraint("ck_certificate_instance_unknowns_json", "ISJSON(unknowns) = 1");
                t.HasCheckConstraint("ck_certificate_instance_evidence_json", "ISJSON(evidence) = 1");
                t.HasCheckConstraint("ck_certificate_instance_provenance", "provenance IN ('system_generated', 'migrated', 'reconstructed')");
                t.HasCheckConstraint("ck_certificate_instance_status", "status IN ('draft', 'issued', 'superseded')");
                t.HasCheckConstraint("ck_certificate_instance_owner_kind", "(document_kind = 'release_record' AND owner_snapshot IS NULL) OR document_kind = 'warranty_certificate'");
                t.HasCheckConstraint("ck_certificate_instance_reconstruction", "provenance = 'system_generated' OR (JSON_QUERY(unknowns) <> '[]' AND JSON_QUERY(evidence) <> '[]')");
            });
            b.HasKey(row => new { row.TenantId, row.CertificateId });
            b.Property(row => row.DocumentKind).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.FormType).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.TrackingNumber).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.PayloadHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.FactsHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.SupersedeReason).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.Language).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.Provenance).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.HasOne<CertificateTemplateVersion>().WithMany().HasForeignKey(row => new { row.TenantId, row.TemplateVersionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CertificateInstance>().WithMany().HasForeignKey(row => new { row.TenantId, row.SupersedesCertificateId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.TrackingNumber }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.DocumentKind, row.Version }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.DocumentKind, row.TriggeringEventId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.DocumentKind, row.FormType, row.Status, row.IssuedAt });
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.DocumentKind, row.IssuedAt });
            b.HasIndex(row => new { row.TenantId, row.SupersedesCertificateId });
        });

        // TIGHT COUPLING (certificate chain serialization)
        // WHAT: CertificateVersioningEngine takes a transaction-owned application lock for this entity's
        // (tenant, unit, document kind) key before it reads or writes this receipt and the version chain.
        // WHY: a chain may have no row to range-lock, while different triggers must still allocate one gap-free sequence.
        // WHAT BREAKS IF SEPARATED: two consumers can choose the same next version and one message is dead-lettered by
        // the unique version index. Keep the matching engine comment and resource-key format in step.
        modelBuilder.Entity<CertificateTrigger>(b =>
        {
            b.ToTable("certificate_trigger", t =>
            {
                t.HasCheckConstraint("ck_certificate_trigger_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                t.HasCheckConstraint("ck_certificate_trigger_outcome", "outcome IN ('issued', 'absorbed', 'refused')");
            });
            b.HasKey(row => new { row.TenantId, row.UnitId, row.DocumentKind, row.TriggeringEventId });
            b.Property(row => row.DocumentKind).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.Outcome).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.RefusalRule).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.RefusalDetail).HasMaxLength(500);
            b.HasOne<CertificateInstance>().WithMany().HasForeignKey(row => new { row.TenantId, row.CertificateId })
                .OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            b.HasIndex(row => new { row.TenantId, row.CertificateId });
        });

        modelBuilder.Entity<CertificateSignature>(b =>
        {
            b.ToTable("certificate_signature", t =>
            {
                t.HasTrigger("tr_certificate_signature_immutable");
                t.HasCheckConstraint("ck_certificate_signature_meaning", "meaning IN ('release', 'review', 'approval', 'authorship')");
            });
            b.HasKey(row => new { row.TenantId, row.SignatureId });
            b.Property(row => row.SignerIdentity).HasMaxLength(200);
            b.Property(row => row.PrintedName).HasMaxLength(200);
            b.Property(row => row.Meaning).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.HashSigned).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.SignatureMethod).HasMaxLength(40).IsUnicode(false);
            b.HasOne<CertificateInstance>().WithMany().HasForeignKey(row => new { row.TenantId, row.CertificateId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(row => new { row.TenantId, row.CertificateId, row.SignedAt });
        });

        modelBuilder.Entity<ReprintLog>(b =>
        {
            b.ToTable("reprint_log");
            b.HasKey(row => new { row.TenantId, row.ReprintId });
            b.Property(row => row.RequestedBy).HasMaxLength(200);
            b.HasOne<CertificateInstance>().WithMany().HasForeignKey(row => new { row.TenantId, row.CertificateId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(row => new { row.TenantId, row.CertificateId, row.At });
        });

        modelBuilder.Entity<CertificateRun>(b =>
        {
            b.ToTable("certificate_run");
            b.HasKey(row => new { row.TenantId, row.CommandId });
            b.Property(row => row.RequestHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.HasOne<SelectionSet>().WithMany().HasForeignKey(row => new { row.TenantId, row.SelectionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.SelectionId, row.CreatedAt });
        });

        modelBuilder.Entity<CertificateRunUnit>(b =>
        {
            b.ToTable("certificate_run_unit", t => t.HasCheckConstraint("ck_certificate_run_unit_outcome",
                "outcome IN ('issued', 'copied', 'skipped', 'failed')"));
            b.HasKey(row => new { row.TenantId, row.CommandId, row.UnitId });
            b.Property(row => row.Serial).HasMaxLength(200);
            b.Property(row => row.ItemCode).HasMaxLength(100);
            b.Property(row => row.ItemName).HasMaxLength(200);
            b.Property(row => row.Revision).HasMaxLength(100);
            b.Property(row => row.TrackingNumber).HasMaxLength(50).IsUnicode(false);
            b.Property(row => row.Outcome).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.Rule).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.Detail).HasMaxLength(500);
            b.Property(row => row.UnblockingStep).HasMaxLength(500);
            b.HasOne<CertificateRun>().WithMany().HasForeignKey(row => new { row.TenantId, row.CommandId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CertificateInstance>().WithMany().HasForeignKey(row => new { row.TenantId, row.CertificateId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.CommandId, row.Outcome });
        });

        modelBuilder.Entity<CertificateTemplate>(b =>
        {
            b.ToTable("certificate_template", t =>
            {
                t.HasCheckConstraint("ck_certificate_template_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                t.HasCheckConstraint("ck_certificate_template_form_type", "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'warranty_certificate', 'custom')");
            });
            b.HasKey(row => new { row.TenantId, row.TemplateId });
            b.Property(row => row.Code).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.Name).HasMaxLength(200);
            b.Property(row => row.DocumentKind).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.FormType).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.AppliesToItemClass).HasMaxLength(100);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            // Coupled to CertificateTemplateService's starter application lock: this key is the final guard which makes
            // lazy starter creation insert-if-absent. Removing either side permits duplicate built-ins on concurrent GETs.
            b.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.DocumentKind, row.FormType, row.AppliesToItemClass });
        });

        modelBuilder.Entity<CertificateTemplateVersion>(b =>
        {
            b.ToTable("certificate_template_version", t =>
            {
                t.HasTrigger("tr_certificate_template_version_immutable");
                t.HasCheckConstraint("ck_certificate_template_version_status", "status IN ('draft', 'active', 'retired')");
                t.HasCheckConstraint("ck_certificate_template_version_layout", "layout_kind = 'html'");
                t.HasCheckConstraint("ck_certificate_template_version_static_text_json", "ISJSON(static_text) = 1");
                t.HasCheckConstraint("ck_certificate_template_version_fields_json", "ISJSON(fields) = 1");
                t.HasCheckConstraint("ck_certificate_template_version_dates", "effective_to IS NULL OR (effective_from IS NOT NULL AND effective_to > effective_from)");
            });
            b.HasKey(row => new { row.TenantId, row.TemplateVersionId });
            b.Property(row => row.Language).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.PayloadSchemaVersion).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.LayoutKind).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.PageSize).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.Orientation).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.MarginTopMm).HasPrecision(7, 2);
            b.Property(row => row.MarginRightMm).HasPrecision(7, 2);
            b.Property(row => row.MarginBottomMm).HasPrecision(7, 2);
            b.Property(row => row.MarginLeftMm).HasPrecision(7, 2);
            b.Property(row => row.ApprovedBy).HasMaxLength(200);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.Property(row => row.UpdatedBy).HasMaxLength(200);
            b.HasOne<CertificateTemplate>().WithMany().HasForeignKey(row => new { row.TenantId, row.TemplateId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TenantAsset>().WithMany().HasForeignKey(row => new { row.TenantId, row.BrandingAssetId })
                .OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            b.HasIndex(row => new { row.TenantId, row.TemplateId, row.Version }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.TemplateId, row.Status, row.EffectiveFrom });
            b.HasIndex(row => new { row.TenantId, row.BrandingAssetId });
        });

        modelBuilder.Entity<TenantAsset>(b =>
        {
            b.ToTable("tenant_asset", t =>
            {
                t.HasCheckConstraint("ck_tenant_asset_kind", "kind = 'logo'");
                t.HasCheckConstraint("ck_tenant_asset_dimensions", "width > 0 AND height > 0");
            });
            b.HasKey(row => new { row.TenantId, row.AssetId });
            b.Property(row => row.Kind).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.MediaType).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.OriginalFileName).HasMaxLength(255);
            b.Property(row => row.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.UploadedBy).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.Kind, row.LegalEntityId, row.UploadedAt });
            b.HasIndex(row => new { row.TenantId, row.Sha256 });
        });
        // End P6 certificate block.
        // P6 labels block. Label versions carry both business-effective dates and SQL system time: the former selects
        // what was printable at a business instant; the latter audits every draft/lifecycle save.
        modelBuilder.Entity<LabelTemplate>(b =>
        {
            b.ToTable("label_template", t =>
                t.HasCheckConstraint("ck_label_template_applies_to", "applies_to IN ('item_family', 'identifier_type')"));
            b.HasKey(row => new { row.TenantId, row.TemplateId });
            b.Property(row => row.Name).HasMaxLength(200);
            b.Property(row => row.AppliesTo).HasMaxLength(30).IsUnicode(false);
            b.Property(row => row.AppliesToValue).HasMaxLength(100);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.CreateCommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.Name }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.AppliesTo, row.AppliesToValue });
        });

        modelBuilder.Entity<LabelTemplateVersion>(b =>
        {
            b.ToTable("label_template_version", t =>
            {
                t.IsTemporal(history => history.UseHistoryTable("label_template_version_history"));
                t.HasCheckConstraint("ck_label_template_version_layout_kind", "layout_kind IN ('zpl', 'html')");
                t.HasCheckConstraint("ck_label_template_version_symbology", "symbology IN ('code128', 'datamatrix', 'qr', 'code39')");
                t.HasCheckConstraint("ck_label_template_version_status", "status IN ('draft', 'active', 'retired')");
                t.HasCheckConstraint("ck_label_template_version_effective", "effective_to IS NULL OR effective_to > effective_from");
                t.HasCheckConstraint("ck_label_template_version_static_text_json", "ISJSON(static_text) = 1");
                t.HasCheckConstraint("ck_label_template_version_bindings_json", "ISJSON(bindings) = 1");
                t.HasCheckConstraint("ck_label_template_version_command_ids_json", "ISJSON(command_ids) = 1");
            });
            b.HasKey(row => new { row.TenantId, row.TemplateVersionId });
            b.Property(row => row.LayoutKind).HasMaxLength(10).IsUnicode(false);
            b.Property(row => row.Symbology).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ContentHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.LegalReviewContentHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.ApprovalContentHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.PreviewedContentHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.PreviewedBy).HasMaxLength(200);
            b.Property(row => row.LegalReviewRequestedBy).HasMaxLength(200);
            b.Property(row => row.LegalReviewedBy).HasMaxLength(200);
            b.Property(row => row.ApprovedBy).HasMaxLength(200);
            b.Property(row => row.ActorRef).HasMaxLength(200);
            b.Property(row => row.ActivatedBy).HasMaxLength(200);
            b.Property(row => row.RetiredBy).HasMaxLength(200);
            b.HasOne<LabelTemplate>().WithMany().HasForeignKey(row => new { row.TenantId, row.TemplateId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.TemplateId, row.Version }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.TemplateId, row.Status, row.EffectiveFrom });
            b.HasIndex(row => new { row.TenantId, row.PreviewSampleUnitId });
        });

        // P6 jurisdiction-rules block. Effective-to is exclusive throughout the resolver, which permits one version
        // to close on the exact day its successor starts without an overlap.
        modelBuilder.Entity<RegionGroup>(b =>
        {
            b.ToTable("region_group", table => table.HasCheckConstraint("ck_region_group_members_json", "ISJSON(members) = 1"));
            b.HasKey(row => new { row.TenantId, row.RegionGroupId });
            b.Property(row => row.Code).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.Name).HasMaxLength(200);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.Property(row => row.LastModifiedBy).HasMaxLength(200);
            b.Property(row => row.RowVersion).IsRowVersion();
            b.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.LastCommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.Name });
        });

        modelBuilder.Entity<JurisdictionRule>(b =>
        {
            b.ToTable("jurisdiction_rule", table =>
            {
                table.HasCheckConstraint("ck_jurisdiction_rule_scope", "scope IN ('any', 'country', 'subdivision', 'region_group')");
                table.HasCheckConstraint("ck_jurisdiction_rule_buyer", "buyer_class IN ('any', 'consumer', 'business')");
                table.HasCheckConstraint("ck_jurisdiction_rule_date_tested", "date_tested IN ('contract', 'purchase', 'delivery', 'first_sale')");
                table.HasCheckConstraint("ck_jurisdiction_rule_value_json", "ISJSON(value, VALUE) = 1");
                table.HasCheckConstraint("ck_jurisdiction_rule_effective", "effective_to IS NULL OR effective_to > effective_from");
                table.HasCheckConstraint("ck_jurisdiction_rule_price", "price_min IS NULL OR price_max IS NULL OR price_max >= price_min");
                table.HasCheckConstraint("ck_jurisdiction_rule_currency", "(price_min IS NULL AND price_max IS NULL) OR currency IS NOT NULL");
                table.HasCheckConstraint("ck_jurisdiction_rule_scope_fields", "(scope = 'any' AND country_code IS NULL AND subdivision_code IS NULL AND region_group_id IS NULL) OR (scope = 'country' AND country_code IS NOT NULL AND subdivision_code IS NULL AND region_group_id IS NULL) OR (scope = 'subdivision' AND country_code IS NULL AND subdivision_code IS NOT NULL AND region_group_id IS NULL) OR (scope = 'region_group' AND country_code IS NULL AND subdivision_code IS NULL AND region_group_id IS NOT NULL)");
            });
            b.HasKey(row => new { row.TenantId, row.RuleId });
            b.Property(row => row.Kind).HasMaxLength(80).IsUnicode(false);
            b.Property(row => row.Scope).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.CountryCode).HasMaxLength(2).IsUnicode(false).IsFixedLength();
            b.Property(row => row.SubdivisionCode).HasMaxLength(7).IsUnicode(false);
            b.Property(row => row.BuyerClass).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ItemClass).HasMaxLength(100);
            b.Property(row => row.PriceMin).HasPrecision(19, 4);
            b.Property(row => row.PriceMax).HasPrecision(19, 4);
            b.Property(row => row.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            b.Property(row => row.DateTested).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.SourceCitation).HasMaxLength(1000);
            b.Property(row => row.SourceNote).HasMaxLength(2000);
            b.Property(row => row.CreatedBy).HasMaxLength(200);
            b.Property(row => row.ApprovedBy).HasMaxLength(200);
            b.Property(row => row.ClosedBy).HasMaxLength(200);
            b.Property(row => row.RowVersion).IsRowVersion();
            b.HasOne<RegionGroup>().WithMany().HasForeignKey(row => new { row.TenantId, row.RegionGroupId })
                .OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ClosedCommandId }).IsUnique().HasFilter("[closed_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.RootRuleId, row.Version }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.Kind, row.Scope, row.CountryCode, row.SubdivisionCode, row.RegionGroupId, row.EffectiveFrom });
            b.HasIndex(row => new { row.TenantId, row.RegionGroupId });
        });

        modelBuilder.Entity<RulePack>(b =>
        {
            b.ToTable("rule_pack");
            b.HasKey(row => new { row.TenantId, row.RulePackId });
            b.Property(row => row.Code).HasMaxLength(80).IsUnicode(false);
            b.Property(row => row.Name).HasMaxLength(200);
            b.Property(row => row.Warning).HasMaxLength(500);
            b.Property(row => row.ImportedBy).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ImportCommandId }).IsUnique().HasFilter("[import_command_id] IS NOT NULL");
        });

        modelBuilder.Entity<RulePackRule>(b =>
        {
            b.ToTable("rule_pack_rule", table => table.HasCheckConstraint("ck_rule_pack_rule_template_json", "ISJSON(template) = 1"));
            b.HasKey(row => new { row.TenantId, row.RulePackRuleId });
            b.HasOne<RulePack>().WithMany().HasForeignKey(row => new { row.TenantId, row.RulePackId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(row => new { row.TenantId, row.RulePackId, row.Sequence }).IsUnique();
        });
        // End P6 jurisdiction-rules block.

        modelBuilder.Entity<PrintJob>(b =>
        {
            b.ToTable("print_job", t =>
                t.HasCheckConstraint("ck_print_job_status", "status IN ('rendered', 'sent', 'confirmed', 'rejected')"));
            b.HasKey(row => new { row.TenantId, row.JobId });
            b.Property(row => row.JobNumber).HasMaxLength(32).IsUnicode(false);
            b.Property(row => row.PayloadHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.PrinterRef).HasMaxLength(200);
            b.Property(row => row.RequestedSerial).HasMaxLength(100);
            b.Property(row => row.RequestedBy).HasMaxLength(200);
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.RejectionRule).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.RejectionReason).HasMaxLength(500);
            b.Property(row => row.EmptyRequiredField).HasMaxLength(100).IsUnicode(false);
            b.HasOne<LabelTemplateVersion>().WithMany().HasForeignKey(row => new { row.TenantId, row.TemplateVersionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PrintJob>().WithMany().HasForeignKey(row => new { row.TenantId, row.OriginalJobId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.JobNumber }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.BatchCommandId, row.BatchSequence }).IsUnique()
                .HasFilter("[batch_command_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.SourceEventId }).IsUnique().HasFilter("[source_event_id] IS NOT NULL");
            b.HasIndex(row => new { row.TenantId, row.Status, row.RequestedAt });
            b.HasIndex(row => new { row.TenantId, row.TemplateVersionId, row.RequestedAt });
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.RequestedAt });
            b.HasIndex(row => new { row.TenantId, row.IdentifierId });
            b.HasIndex(row => new { row.TenantId, row.OriginalJobId });
            b.HasIndex(row => new { row.TenantId, row.RequestedAt })
                .IncludeProperties(row => new { row.Status, row.PrinterRef, row.OriginalJobId, row.SourceEventId });
        });
        // End P6 labels block.

        modelBuilder.Entity<ImportJob>(b =>
        {
            b.ToTable("import_job", t =>
            {
                t.HasCheckConstraint("ck_import_job_status", "status IN ('intake', 'compared', 'reviewed', 'handed_off', 'applied', 'partial', 'rejected')");
                t.HasCheckConstraint("ck_import_job_mode", "mode IN ('apply', 'compare')");
            });
            b.HasKey(row => new { row.TenantId, row.JobId });
            b.Property(row => row.Source).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.FileName).HasMaxLength(260);
            b.Property(row => row.OriginalHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.ConvertedHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.Format).HasMaxLength(10).IsUnicode(false);
            b.Property(row => row.Mode).HasMaxLength(10).IsUnicode(false);
            b.Property(row => row.TargetType).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.OriginalArchiveReference).HasMaxLength(500);
            b.Property(row => row.ConvertedArchiveReference).HasMaxLength(500);
            b.Property(row => row.InboxReference).HasMaxLength(500);
            b.Property(row => row.OriginalFileReference).HasMaxLength(500);
            b.Property(row => row.ConvertedFileReference).HasMaxLength(500);
            b.Property(row => row.OriginalFileCleanupReference).HasMaxLength(500);
            b.Property(row => row.ConvertedFileCleanupReference).HasMaxLength(500);
            b.Property(row => row.ActorRef).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.Source, row.OriginalHash, row.Mode, row.TargetType, row.TargetId, row.TargetLine })
                .IsUnique().HasFilter(null).HasDatabaseName("ux_import_job_replay_key");
            b.HasIndex(row => new { row.TenantId, row.Status, row.RecordedTime });
            b.HasIndex(row => new { row.TenantId, row.RecordedTime, row.JobId });
            b.HasIndex(row => new { row.TenantId, row.TargetType, row.TargetId, row.TargetLine });
        });

        modelBuilder.Entity<ImportFileDownloadAudit>(b =>
        {
            b.ToTable("import_file_download_audit", t =>
                t.HasCheckConstraint("ck_import_file_download_audit_kind", "copy_kind IN ('original', 'converted')"));
            b.HasKey(row => new { row.TenantId, row.AuditId });
            b.Property(row => row.CopyKind).HasMaxLength(12).IsUnicode(false);
            b.Property(row => row.ActorRef).HasMaxLength(200);
            b.HasOne<ImportJob>().WithMany().HasForeignKey(row => new { row.TenantId, row.JobId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(row => new { row.TenantId, row.JobId, row.DownloadedAt });
        });

        modelBuilder.Entity<ImportRowResult>(b =>
        {
            b.ToTable("import_row_result", t => t.HasCheckConstraint("ck_import_row_result_outcome", "outcome IN ('applied', 'skipped', 'rejected')"));
            b.HasKey(row => new { row.TenantId, row.RowResultId });
            b.Property(row => row.Sheet).HasMaxLength(128);
            b.Property(row => row.RawValue).HasMaxLength(500);
            b.Property(row => row.NormalisedValue).HasMaxLength(500);
            b.Property(row => row.Outcome).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.Rule).HasMaxLength(100).IsUnicode(false);
            b.Property(row => row.OrderNumber).HasMaxLength(100);
            b.Property(row => row.Lot).HasMaxLength(100);
            b.Property(row => row.CustomerAssetTag).HasMaxLength(100);
            b.Property(row => row.ComparisonPartition).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.ProposedAction).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ReviewedAction).HasMaxLength(20).IsUnicode(false);
            b.HasOne<ImportJob>().WithMany().HasForeignKey(row => new { row.TenantId, row.JobId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(row => new { row.TenantId, row.JobId, row.RowNumber, row.RowResultId });
            b.HasIndex(row => new { row.TenantId, row.NormalisedValue, row.JobId });
            b.HasIndex(row => new { row.TenantId, row.UnitId, row.JobId });
        });

        modelBuilder.Entity<ColumnMapping>(b =>
        {
            b.ToTable("column_mapping", t => t.HasCheckConstraint("ck_column_mapping_json", "ISJSON(mapping_json) = 1"));
            b.HasKey(row => new { row.TenantId, row.MappingId });
            b.Property(row => row.Source).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.Name).HasMaxLength(100);
            b.Property(row => row.ActorRef).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.Source, row.Name }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.Source, row.IsDefault });
        });

        modelBuilder.Entity<ThreePlFeedMessage>(b =>
        {
            b.ToTable("three_pl_feed_message");
            b.HasKey(row => new { row.TenantId, row.FeedMessageId });
            b.Property(row => row.MessageReference).HasMaxLength(200);
            b.Property(row => row.DocumentType).HasMaxLength(40).IsUnicode(false);
            b.Property(row => row.PayloadHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(row => row.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(row => row.ActorRef).HasMaxLength(200);
            b.HasIndex(row => new { row.TenantId, row.CommandId }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.MessageReference }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.DocumentType, row.DocumentId, row.DocumentLine });
            b.HasOne<ImportJob>().WithMany().HasForeignKey(row => new { row.TenantId, row.ImportJobId }).OnDelete(DeleteBehavior.Restrict);
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
            // TIGHT COUPLING (partitioned idempotency seek)
            // WHAT: UnitEventIdempotencyReader FORCESEEKs this convention-derived name and exact key order.
            // WHY: exact (key, event_time) JSON pairs must drive one aligned seek per requested partition at ledger
            // scale. WHAT BREAKS IF SEPARATED: renaming/reordering fails the hinted query, while dropping the hint can
            // restore a scan across every monthly partition. Keep the reader-side comment in step with this one.
            b.HasIndex(e => new { e.TenantId, e.IdempotencyKey, e.EventTime }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.UnitId, e.EventTime }).IsDescending(false, false, true)
                .IncludeProperties(e => new { e.EventType, e.BizStep, e.Disposition, e.LocationId, e.Seq, e.RecordedTime });
            b.HasIndex(e => new { e.TenantId, e.MergedFromUnitId, e.EventTime }).HasFilter(MergedFromFilter);
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
