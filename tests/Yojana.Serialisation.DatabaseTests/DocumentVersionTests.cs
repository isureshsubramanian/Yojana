using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.DatabaseTests;

public sealed record EditVersionedDocument(Guid CommandId, string Kind, string Key, string ExpectedVersion,
    string ActorRef, DateTime ChangedAt, bool PauseAfterRead = false);

public sealed record EditVersionedDocumentResult(string Kind, string Key);

public sealed record TransitionRmaProbe(Guid CommandId, Guid RmaId, string ActorRef, DateTime ChangedAt);

public sealed record TransitionRmaProbeResult(Guid RmaId, string Stage);

public static class DocumentRaceGate
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();
    public static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Loaded = new();
    public static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Continue = new();

    public static void Prepare(Guid commandId)
    {
        Attempts[commandId] = 0;
        Loaded[commandId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Continue[commandId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public static class EditVersionedDocumentHandler
{
    public static async Task<EditVersionedDocumentResult> Handle(EditVersionedDocument command,
        SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var attempt = DocumentRaceGate.Attempts.AddOrUpdate(command.CommandId, 1, (_, count) => count + 1);
        var tenantId = tenant.TenantId;
        switch (command.Kind)
        {
            case "rma":
                var rma = await db.Rmas.SingleAsync(x => x.TenantId == tenantId && x.RmaId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, rma.RowVersion, rma.LastModifiedBy, rma.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                Touch(rma, command);
                break;
            case "claim":
                var claim = await db.Claims.SingleAsync(x => x.TenantId == tenantId && x.ClaimId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, claim.RowVersion, claim.LastModifiedBy, claim.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                claim.LastModifiedBy = command.ActorRef; claim.LastModifiedAt = command.ChangedAt;
                break;
            case "recovery_claim":
                var recovery = await db.RecoveryClaims.SingleAsync(x => x.TenantId == tenantId && x.RecoveryClaimId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, recovery.RowVersion, recovery.LastModifiedBy, recovery.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                recovery.LastModifiedBy = command.ActorRef; recovery.LastModifiedAt = command.ChangedAt;
                break;
            case "warranty_replacement":
                var replacement = await db.WarrantyReplacements.SingleAsync(x => x.TenantId == tenantId && x.WarrantyReplacementId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, replacement.RowVersion, replacement.LastModifiedBy, replacement.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                replacement.LastModifiedBy = command.ActorRef; replacement.LastModifiedAt = command.ChangedAt;
                break;
            case "refurbishment_work_order":
                var workOrder = await db.RefurbishmentWorkOrders.SingleAsync(x => x.TenantId == tenantId && x.WorkOrderId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                workOrder.LastModifiedBy = command.ActorRef; workOrder.LastModifiedAt = command.ChangedAt;
                break;
            case "campaign":
                var campaign = await db.Campaigns.SingleAsync(x => x.TenantId == tenantId && x.CampaignId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, campaign.RowVersion, campaign.UpdatedBy, campaign.UpdatedAt);
                await PauseAsync(command, attempt, cancellationToken);
                campaign.UpdatedBy = command.ActorRef; campaign.UpdatedAt = command.ChangedAt;
                break;
            case "policy_draft":
                var policy = await db.PolicyVersions.SingleAsync(x => x.TenantId == tenantId && x.PolicyVersionId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, policy.RowVersion, policy.LastModifiedBy, policy.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                policy.LastModifiedBy = command.ActorRef; policy.LastModifiedAt = command.ChangedAt;
                break;
            case "return_reason":
                var reason = await db.ReturnReasons.SingleAsync(x => x.TenantId == tenantId && x.Code == command.Key, cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, reason.RowVersion, reason.LastModifiedBy, reason.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                reason.LastModifiedBy = command.ActorRef; reason.LastModifiedAt = command.ChangedAt;
                break;
            case "coverage_family":
                var family = await db.CoverageFamilies.SingleAsync(x => x.TenantId == tenantId && x.FamilyCode == command.Key, cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, family.RowVersion, family.LastModifiedBy, family.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                family.LastModifiedBy = command.ActorRef; family.LastModifiedAt = command.ChangedAt;
                break;
            case "replacement_policy":
                var replacementPolicy = await db.ReplacementPolicies.SingleAsync(x => x.TenantId == tenantId && x.ReplacementPolicyId == Guid.Parse(command.Key), cancellationToken);
                DocumentVersion.RequireExpected(command.ExpectedVersion, replacementPolicy.RowVersion, replacementPolicy.LastModifiedBy, replacementPolicy.LastModifiedAt);
                await PauseAsync(command, attempt, cancellationToken);
                replacementPolicy.LastModifiedBy = command.ActorRef; replacementPolicy.LastModifiedAt = command.ChangedAt;
                break;
            default:
                throw new InvalidOperationException(command.Kind);
        }

        return new(command.Kind, command.Key);
    }

    private static async Task PauseAsync(EditVersionedDocument command, int attempt, CancellationToken cancellationToken)
    {
        if (!command.PauseAfterRead || attempt != 1) return;
        DocumentRaceGate.Loaded[command.CommandId].TrySetResult();
        await DocumentRaceGate.Continue[command.CommandId].Task.WaitAsync(cancellationToken);
    }

    private static void Touch(Rma rma, EditVersionedDocument command)
    {
        rma.LastModifiedBy = command.ActorRef;
        rma.LastModifiedAt = command.ChangedAt;
    }
}

public static class TransitionRmaProbeHandler
{
    public static async Task<TransitionRmaProbeResult> Handle(TransitionRmaProbe command, SerialisationDbContext db,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var attempt = DocumentRaceGate.Attempts.AddOrUpdate(command.CommandId, 1, (_, count) => count + 1);
        var rma = await db.Rmas.SingleAsync(x => x.TenantId == tenant.TenantId && x.RmaId == command.RmaId, cancellationToken);
        if (rma.Stage != RmaStage.Requested)
            throw new CommandRefusedException("rma_transition_not_allowed", $"RMA {rma.RmaId} is '{rma.Stage}'.");
        if (attempt == 1)
        {
            DocumentRaceGate.Loaded[command.CommandId].TrySetResult();
            await DocumentRaceGate.Continue[command.CommandId].Task.WaitAsync(cancellationToken);
        }

        rma.Stage = RmaStage.Authorised;
        rma.LastModifiedBy = command.ActorRef;
        rma.LastModifiedAt = command.ChangedAt;
        return new(rma.RmaId, rma.Stage);
    }
}

[Collection("serialisation host")]
public sealed class DocumentVersionTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime LoadedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstAt = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> DocumentKinds => new()
    {
        "rma", "claim", "recovery_claim", "warranty_replacement", "refurbishment_work_order",
        "campaign", "policy_draft", "return_reason", "coverage_family", "replacement_policy",
    };

    [Fact]
    public void Append_only_refurbishment_commands_do_not_claim_to_be_document_edits()
    {
        var appendOnly = new[]
        {
            typeof(AddRefurbishmentOperation), typeof(RecordRefurbishmentLabour),
            typeof(RecordRefurbishmentCost), typeof(RemoveRefurbishmentPart),
            typeof(InstallRefurbishmentPart),
        };

        Assert.All(appendOnly, type => Assert.False(typeof(IExpectedDocumentVersion).IsAssignableFrom(type), type.Name));
        Assert.Contains("refurbishment_work_order", DocumentKinds);
    }

    [DatabaseTheory]
    [MemberData(nameof(DocumentKinds))]
    public async Task Two_editors_with_one_loaded_version_keep_the_first_change_and_refuse_the_second(string kind)
    {
        var tenant = await fixture.NewTenantAsync("dv");
        var key = await SeedAsync(tenant, kind);
        var loadedVersion = await VersionAsync(tenant, kind, key);

        await InvokeAsync<EditVersionedDocumentResult>(tenant,
            new EditVersionedDocument(Guid.NewGuid(), kind, key, loadedVersion, "engineer-one", FirstAt));
        var refusal = await Assert.ThrowsAsync<DocumentChangedException>(() => InvokeAsync<EditVersionedDocumentResult>(tenant,
            new EditVersionedDocument(Guid.NewGuid(), kind, key, loadedVersion, "engineer-two", FirstAt.AddMinutes(5))));

        Assert.Equal(DocumentVersionRules.Changed, refusal.Rule);
        Assert.Equal("engineer-one", refusal.ChangedBy);
        Assert.Equal(FirstAt, refusal.ChangedAt);
        Assert.Equal(DocumentVersionRules.Reload, refusal.UnblockingStep);
        Assert.Equal(("engineer-one", FirstAt), await AuditAsync(tenant, kind, key));
    }

    [DatabaseFact]
    public async Task Lost_edit_race_retries_from_a_fresh_scope_and_becomes_document_changed()
    {
        var tenant = await fixture.NewTenantAsync("dvr");
        var key = await SeedAsync(tenant, "campaign");
        var expected = await VersionAsync(tenant, "campaign", key);
        var commandId = Guid.NewGuid();
        DocumentRaceGate.Prepare(commandId);

        var losing = InvokeAsync<EditVersionedDocumentResult>(tenant,
            new EditVersionedDocument(commandId, "campaign", key, expected, "stale-engineer", FirstAt.AddMinutes(10), PauseAfterRead: true));
        await DocumentRaceGate.Loaded[commandId].Task.WaitAsync(TimeSpan.FromSeconds(10));
        await UpdateCampaignDirectlyAsync(tenant, Guid.Parse(key), "winning-engineer", FirstAt);
        DocumentRaceGate.Continue[commandId].TrySetResult();

        var refusal = await Assert.ThrowsAsync<DocumentChangedException>(() => losing);
        Assert.Equal("winning-engineer", refusal.ChangedBy);
        Assert.Equal(FirstAt, refusal.ChangedAt);
        Assert.Equal(2, DocumentRaceGate.Attempts[commandId]);
        Assert.Equal(("winning-engineer", FirstAt), await AuditAsync(tenant, "campaign", key));
    }

    [DatabaseFact]
    public async Task State_machine_retry_refuses_on_the_fresh_business_state()
    {
        var tenant = await fixture.NewTenantAsync("dvs");
        var key = await SeedAsync(tenant, "rma");
        var commandId = Guid.NewGuid();
        DocumentRaceGate.Prepare(commandId);
        var losing = InvokeAsync<TransitionRmaProbeResult>(tenant,
            new TransitionRmaProbe(commandId, Guid.Parse(key), "stale-engineer", FirstAt.AddMinutes(10)));
        await DocumentRaceGate.Loaded[commandId].Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rma = await db.Rmas.SingleAsync(x => x.RmaId == Guid.Parse(key));
            rma.Stage = RmaStage.Closed;
            rma.LastModifiedBy = "winning-engineer";
            rma.LastModifiedAt = FirstAt;
            await db.SaveChangesAsync();
        }
        DocumentRaceGate.Continue[commandId].TrySetResult();

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => losing);
        Assert.Equal("rma_transition_not_allowed", refusal.Rule);
        Assert.Equal(2, DocumentRaceGate.Attempts[commandId]);
        Assert.Equal(("winning-engineer", FirstAt), await AuditAsync(tenant, "rma", key));
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<string> SeedAsync(TenantId tenant, string kind)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var id = Guid.NewGuid();
        switch (kind)
        {
            case "rma":
                db.Rmas.Add(new Rma { TenantId = tenant, RmaId = id, CommandId = Guid.NewGuid(), Stage = RmaStage.Requested,
                    RequestDate = DateOnly.FromDateTime(LoadedAt), ReturningPartyRef = Guid.NewGuid(), SettlementPartyRef = Guid.NewGuid(),
                    ServiceEntityId = Guid.NewGuid(), ObligorEntityId = Guid.NewGuid(), ServiceCountry = "GB", Currency = "GBP",
                    RequestedAt = LoadedAt, RequestedBy = "loader", LastEventId = Guid.NewGuid(), LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                break;
            case "claim":
                db.Claims.Add(Claim(tenant, id));
                break;
            case "recovery_claim":
                var claim = Claim(tenant, Guid.NewGuid()); db.Claims.Add(claim);
                db.RecoveryClaims.Add(new RecoveryClaim { TenantId = tenant, RecoveryClaimId = id, RecoveryNumber = "RCV-2026-0001",
                    CustomerClaimId = claim.ClaimId, ClaimLineId = Guid.NewGuid(), InboundEntitlementId = Guid.NewGuid(), ChildUnitId = Guid.NewGuid(),
                    VendorPartyRef = Guid.NewGuid(), RequestedAmount = 10m, Currency = "GBP", Status = RecoveryClaimStatus.Raised,
                    RaisedAt = LoadedAt, RaisedEventId = Guid.NewGuid(), LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                break;
            case "warranty_replacement":
                db.WarrantyReplacements.Add(new WarrantyReplacement { TenantId = tenant, WarrantyReplacementId = id, CommandId = Guid.NewGuid(),
                    ReplacementNumber = "WR-2026-0001", SourceKind = ReplacementSourceKind.Claim, SourceId = Guid.NewGuid(), SourceNumber = "CLM-2026-00001",
                    LegalEntityId = Guid.NewGuid(), ServiceCountry = "GB", Status = WarrantyReplacementStatus.Proposed,
                    RestockingFeeAmount = 0m, CreatedAt = LoadedAt, CreatedBy = "loader", LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                break;
            case "refurbishment_work_order":
                db.RefurbishmentWorkOrders.Add(new RefurbishmentWorkOrder { TenantId = tenant, WorkOrderId = id, OpenCommandId = Guid.NewGuid(),
                    WorkOrderNumber = "RFW-2026-0001", Kind = RefurbishmentKind.Refurbish, TargetUnitId = Guid.NewGuid(), ItemId = Guid.NewGuid(),
                    Status = RefurbishmentStatus.Open, CycleNumber = 1, IdentityPolicy = RefurbishmentIdentity.SameUnitSameSerial, MaxCycles = 3,
                    LifeEpisodeId = Guid.NewGuid(), OpenedAt = LoadedAt, OpenedBy = "loader", OpeningEventId = Guid.NewGuid(),
                    LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                break;
            case "campaign":
                db.Campaigns.Add(new Campaign { TenantId = tenant, CampaignId = id, Code = "CMP-1", Name = "Campaign one",
                    CoverageSource = CampaignCoverageSource.Recall, ReserveTag = "reserve", ReasonCode = CampaignReason.Safety,
                    Status = CampaignStatus.Draft, CreatedAt = LoadedAt, CreatedBy = "loader", UpdatedAt = LoadedAt, UpdatedBy = "loader" });
                break;
            case "policy_draft":
                db.PolicyVersions.Add(Policy(tenant, id));
                break;
            case "return_reason":
                db.ReturnReasons.Add(new ReturnReason { TenantId = tenant, Code = "defective", Name = "Defective", RestockFeeRule = RestockFeeRule.None,
                    Active = true, CreatedAt = LoadedAt, LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                await db.SaveChangesAsync(); return "defective";
            case "coverage_family":
                db.CoverageFamilies.Add(new CoverageFamily { TenantId = tenant, FamilyCode = "base", Name = "Base", AccountingTag = AssuranceOrService.Assurance,
                    CreatedAt = LoadedAt, LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                await db.SaveChangesAsync(); return "base";
            case "replacement_policy":
                db.ReplacementPolicies.Add(new ReplacementPolicy { TenantId = tenant, ReplacementPolicyId = id, Code = "RP-1", Rule = ReplacementRule.CarryRemaining,
                    CreatedAt = LoadedAt, LastModifiedAt = LoadedAt, LastModifiedBy = "loader" });
                break;
        }
        await db.SaveChangesAsync();
        return id.ToString();
    }

    private static Claim Claim(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, ClaimId = id, ClaimNumber = "CLM-2026-00001", CommandId = Guid.NewGuid(), UnitId = Guid.NewGuid(),
        ClaimDate = DateOnly.FromDateTime(LoadedAt), ClaimantPartyRef = Guid.NewGuid(), SettlementPartyRef = Guid.NewGuid(),
        ServiceEntityId = Guid.NewGuid(), ObligorEntityId = Guid.NewGuid(), ServiceCountry = "GB", Currency = "GBP",
        CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair, Status = ClaimStatus.Submitted,
        SubmittedAt = LoadedAt, SubmittedBy = "loader", LastEventId = Guid.NewGuid(), LastModifiedAt = LoadedAt, LastModifiedBy = "loader",
    };

    private static WarrantyPolicyVersion Policy(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, PolicyVersionId = id, PolicyCode = "POL-1", Version = 1, Name = "Policy one", CoverageFamily = "base",
        EntitlementType = EntitlementType.Manufacturer, AppliesToContractsFrom = DateOnly.FromDateTime(LoadedAt), Status = PolicyVersionStatus.Draft,
        RuleVersion = "v1", DurationValue = 12, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp, DurationBasis = "time",
        StartTrigger = StartTrigger.Invoice, Transferable = "yes", SettlementFormula = "full", AssuranceOrService = AssuranceOrService.Assurance,
        ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, CreatedAt = LoadedAt, CreatedBy = "loader", LastModifiedAt = LoadedAt, LastModifiedBy = "loader",
    };

    private async Task<string> VersionAsync(TenantId tenant, string kind, string key)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var id = Guid.TryParse(key, out var parsed) ? parsed : Guid.Empty;
        var version = kind switch
        {
            "rma" => await db.Rmas.Where(x => x.RmaId == id).Select(x => x.RowVersion).SingleAsync(),
            "claim" => await db.Claims.Where(x => x.ClaimId == id).Select(x => x.RowVersion).SingleAsync(),
            "recovery_claim" => await db.RecoveryClaims.Where(x => x.RecoveryClaimId == id).Select(x => x.RowVersion).SingleAsync(),
            "warranty_replacement" => await db.WarrantyReplacements.Where(x => x.WarrantyReplacementId == id).Select(x => x.RowVersion).SingleAsync(),
            "refurbishment_work_order" => await db.RefurbishmentWorkOrders.Where(x => x.WorkOrderId == id).Select(x => x.RowVersion).SingleAsync(),
            "campaign" => await db.Campaigns.Where(x => x.CampaignId == id).Select(x => x.RowVersion).SingleAsync(),
            "policy_draft" => await db.PolicyVersions.Where(x => x.PolicyVersionId == id).Select(x => x.RowVersion).SingleAsync(),
            "return_reason" => await db.ReturnReasons.Where(x => x.Code == key).Select(x => x.RowVersion).SingleAsync(),
            "coverage_family" => await db.CoverageFamilies.Where(x => x.FamilyCode == key).Select(x => x.RowVersion).SingleAsync(),
            "replacement_policy" => await db.ReplacementPolicies.Where(x => x.ReplacementPolicyId == id).Select(x => x.RowVersion).SingleAsync(),
            _ => throw new InvalidOperationException(kind),
        };
        return DocumentVersion.Encode(version);
    }

    private async Task<(string Actor, DateTime At)> AuditAsync(TenantId tenant, string kind, string key)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var id = Guid.TryParse(key, out var parsed) ? parsed : Guid.Empty;
        if (kind == "campaign")
        {
            var row = await db.Campaigns.AsNoTracking().SingleAsync(x => x.CampaignId == id);
            return (row.UpdatedBy, row.UpdatedAt);
        }
        if (kind == "rma") { var row = await db.Rmas.AsNoTracking().SingleAsync(x => x.RmaId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "claim") { var row = await db.Claims.AsNoTracking().SingleAsync(x => x.ClaimId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "recovery_claim") { var row = await db.RecoveryClaims.AsNoTracking().SingleAsync(x => x.RecoveryClaimId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "warranty_replacement") { var row = await db.WarrantyReplacements.AsNoTracking().SingleAsync(x => x.WarrantyReplacementId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "refurbishment_work_order") { var row = await db.RefurbishmentWorkOrders.AsNoTracking().SingleAsync(x => x.WorkOrderId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "policy_draft") { var row = await db.PolicyVersions.AsNoTracking().SingleAsync(x => x.PolicyVersionId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "return_reason") { var row = await db.ReturnReasons.AsNoTracking().SingleAsync(x => x.Code == key); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "coverage_family") { var row = await db.CoverageFamilies.AsNoTracking().SingleAsync(x => x.FamilyCode == key); return (row.LastModifiedBy, row.LastModifiedAt); }
        if (kind == "replacement_policy") { var row = await db.ReplacementPolicies.AsNoTracking().SingleAsync(x => x.ReplacementPolicyId == id); return (row.LastModifiedBy, row.LastModifiedAt); }
        throw new InvalidOperationException(kind);
    }

    private async Task UpdateCampaignDirectlyAsync(TenantId tenant, Guid id, string actor, DateTime at)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var campaign = await db.Campaigns.SingleAsync(x => x.CampaignId == id);
        campaign.UpdatedBy = actor;
        campaign.UpdatedAt = at;
        await db.SaveChangesAsync();
    }
}
