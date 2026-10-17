using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Tenancy;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// Selection-set boundary for the entitlement commands (P2-T11, ADR-0001 "Bulk selection sets"). The handlers stay the
/// only writers and the only judges: <see cref="PrecheckAsync"/> holds the skip rules a set needs (no live entitlement in
/// the family, already at the target), and the preview of a unit that would change is a dry run of the real command in a
/// scope of its own whose transaction is always rolled back, so a refusal there is the command's own (rule and message)
/// and preview and execution can never disagree on a rule. Execution checks the skip rules only and applies; a refusal
/// then fails the whole set, which is one transaction.
/// </summary>
public abstract class EntitlementBulkOperation(SerialisationDbContext db, ITenantContext tenant, IServiceScopeFactory scopes) : IBulkOperation
{
    protected SerialisationDbContext Db { get; } = db;
    protected ITenantContext Tenant { get; } = tenant;
    protected TenantId TenantId => Tenant.TenantId;
    public abstract string Name { get; }

    /// <summary>Skip (with the reason), Block on arguments that cannot work for any unit, or Change when the command should be tried.</summary>
    protected abstract Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken);

    public abstract Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken);

    public async Task<UnitOutcome> PreviewAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        UnitOutcome pre;
        try { pre = await PrecheckAsync(unitId, serial, arguments, cancellationToken); }
        catch (CommandRefusedException ex) { return Block(unitId, serial, ex.Rule, ex.Message, ex.UnblockingStep); }
        return pre.Outcome == UnitOutcome.Outcomes.Change ? await DryRunAsync(unitId, serial, arguments, pre, cancellationToken) : pre;
    }

    public Task<UnitOutcome> CheckBeforeApplyAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken) =>
        PrecheckAsync(unitId, serial, arguments, cancellationToken);

    // The rehearsal runs in its own scope (own DbContext, connection and transaction) so nothing it writes or tracks can
    // reach the caller's unit of work. Coupled to PublishedEventMapper through LedgerPublication: that scope's bus is not
    // the transactional outbox, so publication is switched off there. Sequence numbers drawn by the rehearsal are lost
    // (a sequence is not transactional); event sequence numbers are ordered, never gap-free.
    private async Task<UnitOutcome> DryRunAsync(Guid unitId, string? serial, BulkOperationArguments arguments, UnitOutcome pre, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(TenantId);
        scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
        var twin = scope.ServiceProvider.GetServices<IBulkOperation>().OfType<EntitlementBulkOperation>().Single(o => o.Name == Name);
        var rehearsal = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await rehearsal.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await twin.ApplyAsync(unitId, Guid.NewGuid(), arguments, cancellationToken);
            return pre;
        }
        catch (CommandRefusedException ex)
        {
            return Block(unitId, serial, ex.Rule, MergeDetail(pre.Detail, ex.Message), ex.UnblockingStep);
        }
        catch (DbUpdateException ex)
        {
            return Block(unitId, serial, "write_conflict", ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    protected async Task<Entitlement?> Live(Guid unitId, string family, CancellationToken cancellationToken) => await Db.Entitlements.AsNoTracking()
        .FirstOrDefaultAsync(e => e.TenantId == TenantId && e.UnitId == unitId && e.CoverageFamily == family && e.Status != EntitlementStatus.Closed, cancellationToken);

    protected static UnitOutcome Change(Guid unitId, string? serial, string detail) => new(unitId, serial, UnitOutcome.Outcomes.Change, null, detail);
    protected static UnitOutcome Skip(Guid unitId, string? serial, string reason) => new(unitId, serial, UnitOutcome.Outcomes.Skip, reason);
    protected static UnitOutcome Block(Guid unitId, string? serial, string reason, string? detail = null, string? unblockingStep = null) => new(unitId, serial, UnitOutcome.Outcomes.Block, reason, detail, unblockingStep);
    protected static string PreviewDetail(string? now, DateOnly? nowEnd, string? after, DateOnly? afterEnd, int? days, string? summary = null) =>
        JsonSerializer.Serialize(new { now, nowEnd, after, afterEnd, days, summary }, LedgerJson.Options);
    private static string MergeDetail(string? detail, string message)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail[0] != '{') return message;
        using var document = JsonDocument.Parse(detail);
        var root = document.RootElement;
        return PreviewDetail(
            root.TryGetProperty("now", out var now) ? now.GetString() : null,
            root.TryGetProperty("now_end", out var nowEnd) && nowEnd.ValueKind == JsonValueKind.String && DateOnly.TryParse(nowEnd.GetString(), out var parsedNowEnd) ? parsedNowEnd : null,
            root.TryGetProperty("after", out var after) ? after.GetString() : null,
            root.TryGetProperty("after_end", out var afterEnd) && afterEnd.ValueKind == JsonValueKind.String && DateOnly.TryParse(afterEnd.GetString(), out var parsedAfterEnd) ? parsedAfterEnd : null,
            root.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Number ? days.GetInt32() : null,
            message);
    }
    protected static AmendmentCommitter Committer(BulkOperationArguments arguments) => new(
        arguments.Extra?.GetValueOrDefault(BulkApprovalArguments.ApproverRef) ?? arguments.ActorRef,
        (arguments.Extra?.GetValueOrDefault(BulkApprovalArguments.ApproverRoles) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    protected static CorrectionMetadata Correction(Guid commandId, string reason, BulkOperationArguments arguments, IReadOnlyList<string>? evidenceRefs = null)
    {
        var committer = Committer(arguments);
        var approvers = BulkApprovalArguments.Approvers(arguments);
        var evidence = (evidenceRefs ?? []).Concat(BulkApprovalArguments.EvidenceRefs(arguments)).Distinct(StringComparer.Ordinal).ToList();
        return new(commandId, reason, arguments.Note, evidence, arguments.InitiatedVia, arguments.CorrectsCorrectionId,
            arguments.InitiatingRole ?? CorrectionRules.InitiatingRole(committer.Roles),
            approvers.Count > 0 ? approvers : [new CorrectionApprover(committer.UserRef, committer.Roles)]);
    }
}

public sealed class BulkCreateEntitlementOperation(SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store,
    ITimeZonePort zones, RefurbishedGoodsSettings refurbished, ITenantContext tenant, IClock clock, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.create";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments);
        if (await Live(unitId, x.Family, cancellationToken) is not null) return Skip(unitId, serial, "already_has_family");
        if (!await Db.Current.AsNoTracking().AnyAsync(u => u.TenantId == TenantId && u.UnitId == unitId, cancellationToken)) return Block(unitId, serial, "unit_unknown");
        if (x.ContractDate is null || x.ObligorEntityId is null) return Block(unitId, serial, "bulk_arguments_invalid", "contract_date and obligor_entity_id are required");
        return Change(unitId, serial, $"create {x.Family} entitlement");
    }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments);
        await CreateEntitlementHandler.CreateAsync(new(commandId, unitId, x.Family, x.ContractDate!.Value, x.ObligorEntityId!.Value, arguments.EventTime, arguments.ActorRef, x.Evidence,
            x.PolicyVersionId, x.OwnerPartyRef, x.OwnerPartyType, x.PayerPartyRef, x.PayerPartyType, Country: x.Country, SiteLocationId: x.SiteLocationId,
            SourceDocument: arguments.SourceDocument), Db, ledger, resolver, store, zones, refurbished, Tenant, clock, null, [], false, null, null, null, cancellationToken);
    }
}

public sealed class BulkAttachExtendedOperation(SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store,
    ITimeZonePort zones, ITenantContext tenant, IClock clock, CorrectionLedger corrections, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.attach_extended";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments);
        if (await Live(unitId, x.Family, cancellationToken) is not null) return Skip(unitId, serial, "already_has_family");
        if (x.ContractDate is null || x.ObligorEntityId is null || x.Price is null || x.Currency is null) return Block(unitId, serial, "bulk_arguments_invalid", "contract_date, obligor_entity_id, price and currency are required");
        return Change(unitId, serial, $"attach {x.Family} plan");
    }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments);
        await AttachExtendedWarrantyHandler.Handle(new(commandId, unitId, x.Family, x.ContractDate!.Value, x.ObligorEntityId!.Value, arguments.EventTime,
            x.Price!.Value, x.Currency!, arguments.SourceDocument, arguments.EventTime, arguments.ActorRef, x.PolicyVersionId, x.PayerPartyRef, x.PayerPartyType,
            x.OwnerPartyRef, x.OwnerPartyType, x.Country, x.SiteLocationId, Correction: Correction(commandId, arguments.Reason!, arguments)), Db, ledger, resolver, store, zones, Tenant, clock, corrections, cancellationToken);
    }
}

public sealed class BulkAmendEntitlementOperation(SerialisationDbContext db, EntitlementLedger ledger, AmendmentApprovalSettings approvals,
    IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates, ITenantCurrency currency, CorrectionLedger corrections, ITenantContext tenant, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.amend";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments); var entitlement = await Live(unitId, x.Family, cancellationToken);
        if (entitlement is null) return Skip(unitId, serial, "no_live_entitlement_in_family");
        if (x.NewStart is null && x.NewEnd is null && x.NewTermValue is null && x.Coverage is null) return Block(unitId, serial, "no_change");
        if (x.NewEnd == entitlement.EndDate && x.NewStart == entitlement.StartDate && x.NewTermValue is null && x.Coverage is null) return Skip(unitId, serial, "already_at_target");
        return Change(unitId, serial, $"amend {x.Family} entitlement");
    }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments); var entitlement = (await Live(unitId, x.Family, cancellationToken))!;
        var term = x.NewTermValue is { } value ? new AmendedTerm(value, x.NewTermUnit ?? throw new CommandRefusedException("bulk_arguments_invalid", "new_term_unit is required with new_term_value.")) : null;
        await AmendEntitlementHandler.Handle(new(commandId, entitlement.EntitlementId, arguments.Reason ?? throw new CommandRefusedException("bulk_arguments_invalid", "A reason is required."),
            arguments.EventTime, arguments.ActorRef, x.NewStart, x.NewEnd, term, x.Coverage, arguments.SourceDocument, x.CustomerNotificationRef, x.FinanceReviewRef, arguments.Note, Committer(arguments), x.EnteredValue,
            Correction(commandId, arguments.Reason!, arguments)), Db, ledger, approvals, claims, certificates, currency, Tenant, corrections, cancellationToken);
    }
}

public sealed class BulkSupersedeEntitlementOperation(SerialisationDbContext db, EntitlementLedger ledger, PolicyStore store, IWarrantyClaimsPort claims,
    IWarrantyCertificatePort certificates, CorrectionLedger corrections, ITenantContext tenant, IClock clock, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.supersede";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments); var entitlement = await Live(unitId, x.Family, cancellationToken);
        if (entitlement is null) return new(unitId, serial, UnitOutcome.Outcomes.Skip, "no_live_entitlement_in_family", PreviewDetail("none live", null, "unchanged", null, null, $"No live {x.Family} entitlement."));
        if (x.EffectiveDate is null || (x.PolicyVersionId is null && x.PolicyCode is null)) return Block(unitId, serial, "bulk_arguments_invalid", "effective_date and arguments target policy are required");
        var oldPolicy = await Db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == TenantId && v.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        var oldLabel = $"{oldPolicy.Name} · v{oldPolicy.Version}";
        if (x.PolicyVersionId == entitlement.PolicyVersionId) return new(unitId, serial, UnitOutcome.Outcomes.Skip, "already_at_target", PreviewDetail(oldLabel, entitlement.EndDate, "unchanged", entitlement.EndDate, 0, "The target policy is already in force."));
        var target = x.PolicyVersionId is { } versionId
            ? await Db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == TenantId && v.PolicyVersionId == versionId, cancellationToken)
            : await Db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == TenantId && v.PolicyCode == x.PolicyCode && v.Status == PolicyVersionStatus.Published && v.AppliesToContractsFrom <= x.EffectiveDate)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
        if (target is null) return Block(unitId, serial, "policy_version_unknown", PreviewDetail(oldLabel, entitlement.EndDate, "unchanged", entitlement.EndDate, null, "No published target policy applies on the effective date."));
        var terms = await store.ReadTermsAsync(target, cancellationToken);
        var itemId = await Db.Current.AsNoTracking().Where(c => c.TenantId == TenantId && c.UnitId == unitId).Select(c => c.ItemId).SingleAsync(cancellationToken);
        var duration = TermArithmetic.ForCondition(terms, itemId, entitlement.ConditionAtCreation, null, null).Duration;
        var afterEnd = TermArithmetic.EndDate(x.EffectiveDate.Value, duration);
        int? days = entitlement.EndDate is { } oldEnd ? afterEnd.DayNumber - oldEnd.DayNumber : null;
        return new(unitId, serial, UnitOutcome.Outcomes.Change, null, PreviewDetail(oldLabel, entitlement.EndDate, $"{target.Name} · v{target.Version}", afterEnd, days, $"Supersede {x.Family} entitlement."));
    }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments); var entitlement = (await Live(unitId, x.Family, cancellationToken))!;
        await SupersedeEntitlementHandler.SupersedeAsync(new(commandId, entitlement.EntitlementId, x.EffectiveDate!.Value,
            arguments.Reason ?? throw new CommandRefusedException("bulk_arguments_invalid", "A reason is required."), arguments.EventTime, arguments.ActorRef, x.PolicyVersionId,
            x.PolicyCode, arguments.SourceDocument, x.CustomerNotificationRef, Committer(arguments), Correction(commandId, arguments.Reason!, arguments)), Db, ledger, store, claims, certificates, Tenant, clock, corrections, true, cancellationToken);
    }
}

public sealed class BulkVoidEntitlementOperation(SerialisationDbContext db, EntitlementLedger ledger, CorrectionLedger corrections, ITenantContext tenant, IClock clock, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.void";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    { var x = EntitlementBulkArguments.Parse(arguments); return await Live(unitId, x.Family, cancellationToken) is null ? Skip(unitId, serial, "no_live_entitlement_in_family") : x.EffectiveDate is null || x.EvidenceRefs.Count == 0 ? Block(unitId, serial, "evidence_required") : Change(unitId, serial, $"void {x.Family} entitlement"); }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    { var x = EntitlementBulkArguments.Parse(arguments); var reason = arguments.Reason ?? throw new CommandRefusedException("bulk_arguments_invalid", "A reason is required."); var e = (await Live(unitId, x.Family, cancellationToken))!; await VoidEntitlementHandler.Handle(new(commandId, e.EntitlementId, reason, arguments.Note ?? "", x.EvidenceRefs, x.EffectiveDate!.Value, arguments.EventTime, arguments.ActorRef, Correction(commandId, reason, arguments, x.EvidenceRefs)), ledger, clock, corrections, cancellationToken); }
}

public sealed class BulkCloseEntitlementOperation(SerialisationDbContext db, EntitlementLedger ledger, CorrectionLedger corrections, ITenantContext tenant, IClock clock, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.close";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    { var x = EntitlementBulkArguments.Parse(arguments); return await Live(unitId, x.Family, cancellationToken) is null ? Skip(unitId, serial, "no_live_entitlement_in_family") : x.EffectiveDate is null ? Block(unitId, serial, "effective_date_required") : Change(unitId, serial, $"close {x.Family} entitlement"); }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    { var x = EntitlementBulkArguments.Parse(arguments); var e = (await Live(unitId, x.Family, cancellationToken))!; var reason = arguments.Reason ?? throw new CommandRefusedException("bulk_arguments_invalid", "A reason is required."); await CloseEntitlementHandler.Handle(new(commandId, e.EntitlementId, reason, x.EffectiveDate!.Value, arguments.EventTime, arguments.ActorRef, arguments.SourceDocument, arguments.Note, Correction(commandId, reason, arguments)), ledger, clock, corrections, cancellationToken); }
}

public sealed class BulkBackfillOperation(SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones,
    RefurbishedGoodsSettings refurbished, CorrectionLedger corrections, ITenantContext tenant, IClock clock, IServiceScopeFactory scopes) : EntitlementBulkOperation(db, tenant, scopes)
{
    public override string Name => "entitlement.backfill";
    protected override async Task<UnitOutcome> PrecheckAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    { var x = EntitlementBulkArguments.Parse(arguments); return await Live(unitId, CoverageFamilyCode.Base, cancellationToken) is not null ? Skip(unitId, serial, "already_has_base") : x.ObligorEntityId is null || x.EvidenceRefs.Count == 0 ? Block(unitId, serial, "bulk_arguments_invalid", "obligor_entity_id and evidence_refs are required") : Change(unitId, serial, "backfill base entitlement"); }
    public override async Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var x = EntitlementBulkArguments.Parse(arguments);
        await BackfillEntitlementsHandler.Handle(new(commandId, [unitId], (arguments.Reason ?? throw new CommandRefusedException("bulk_arguments_invalid", "A reason is required.")), x.EvidenceRefs, arguments.SourceDocument, x.ObligorEntityId!.Value, arguments.EventTime, arguments.ActorRef,
            Committer(arguments), false, x.FinanceReviewRef, x.Country, x.SiteLocationId, Correction(commandId, arguments.Reason!, arguments, x.EvidenceRefs)), Db, ledger, resolver, store, zones, refurbished, Tenant, clock, corrections, cancellationToken);
    }
}
