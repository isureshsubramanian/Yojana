using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// <c>AmendEntitlement</c> (P2-T08, ADR-0001 correction table, cases 129 and 140): one entitlement's start, term, end or
/// coverage changed under a reason code, approved by reason and days changed, with before and after on one
/// EntitlementAmendment row per field and one <c>entitlement.amended</c> fact published as <c>Entitlement.Amended</c>.
/// A reduction is raised to the tenant's reduction role, needs a customer notification once a certificate was issued,
/// and is refused while claims are open. A tier with an amount bound (P2-T08b) also asks finance review when the
/// amendment's value exceeds it: the value comes from the policy's value per unit of term, or, when the policy states
/// none, from the user (refused with <c>value_required</c> until given; the check is never skipped). The certificate's new version is the certificate module's answer to the
/// published event (P6); this command never touches a certificate.
/// </summary>
public static class AmendEntitlementHandler
{
    // Coupled to the factory registration of AmendmentApprovalSettings in SerialisationModule: the settings read goes
    // through PlatformDbContext, and were Wolverine to see that context in this handler's graph it would refuse to pick
    // the transactional one. Naming it with [Transactional] instead opens the connection before the tenant middleware
    // runs, so row-level security hides every row. The factory keeps the platform context out of the generated code.
    // ITenantCurrency is coupled the same way, through its factory registration in PlatformSetup.
    public static Task<AmendEntitlementResult> Handle(AmendEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims,
        IWarrantyCertificatePort certificates, ITenantCurrency currency, ITenantContext tenant, CorrectionLedger corrections, CancellationToken cancellationToken) =>
        AmendAsync(command, db, ledger, approvals, claims, certificates, currency, tenant, corrections, true, cancellationToken);

    internal static async Task<AmendEntitlementResult> AmendAsync(AmendEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims,
        IWarrantyCertificatePort certificates, ITenantCurrency currency, ITenantContext tenant, CorrectionLedger? corrections, bool recordCorrection, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        if (entitlement.Direction != EntitlementDirection.Outbound) throw new CommandRefusedException("outbound_entitlement_required", "Customer entitlement amendments do not apply to supplier cover.");
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, EntitlementEventType.Amended, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return Replay(entitlement, replayed);

        var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "An amendment names who commits it and the roles they hold.");
        var policy = await approvals.GetAsync(cancellationToken);
        if (!policy.Reasons.Any(r => r.ReasonCode == command.ReasonCode)) throw new CommandRefusedException("reason_unknown", $"'{command.ReasonCode}' is not one of {string.Join(", ", policy.Reasons.Select(r => r.ReasonCode))}.");
        EntitlementLedger.Transition(entitlement, EntitlementEventType.Amended);
        if (command.NewEnd is not null && command.NewTerm is not null)
        {
            throw new CommandRefusedException("end_and_term_together", "Give a new end or a new term, not both: the term decides the end.");
        }

        if (entitlement.StartDate is not { } start || entitlement.EndDate is not { } end) throw new CommandRefusedException("term_unresolved", "Only an entitlement with a resolved start and end is amended.");
        var coverageBefore = JsonSerializer.Deserialize<CoverageTerms>(entitlement.Coverage, LedgerJson.Options)!;
        if (command.NewCoverage is { } coverage && coverage.ServiceMode != coverageBefore.ServiceMode)
        {
            throw new CommandRefusedException("service_mode_change", $"'{coverageBefore.ServiceMode}' to '{coverage.ServiceMode}' is a coverage-level change.", "supersede the entitlement within its family (SupersedeEntitlement)");
        }

        if (command.NewTerm is { } term && (term.Value <= 0 || term.Unit is not (DurationRule.Months or DurationRule.Days)))
        {
            throw new CommandRefusedException("term_invalid", $"{term.Value} {term.Unit} is not a term; give a positive number of months or days.");
        }

        // The end follows the rule from the (new) start and term; an explicit new end is recorded as the adjustment beyond it.
        var newStart = command.NewStart ?? start;
        var newTerm = command.NewTerm ?? new AmendedTerm(entitlement.DurationValue, entitlement.DurationUnit);
        var ruleEnd = TermArithmetic.EndDate(newStart, new DurationRule(newTerm.Value, newTerm.Unit, entitlement.EndOfMonthRule, entitlement.EndInclusive));
        var adjustment = command.NewEnd is { } explicitEnd ? explicitEnd.DayNumber - ruleEnd.DayNumber : entitlement.EndAdjustmentDays;
        var newEnd = ruleEnd.AddDays(adjustment);
        if (newEnd < newStart) throw new CommandRefusedException("end_before_start", $"The end {newEnd:yyyy-MM-dd} would fall before the start {newStart:yyyy-MM-dd}.");

        var changes = new List<EntitlementFieldChange>();
        if (newStart != start) changes.Add(new(AmendedField.StartDate, Day(start), Day(newStart)));
        if (newTerm.Value != entitlement.DurationValue || newTerm.Unit != entitlement.DurationUnit)
        {
            changes.Add(new(AmendedField.Term, $"{entitlement.DurationValue} {entitlement.DurationUnit}", $"{newTerm.Value} {newTerm.Unit}"));
        }

        if (newEnd != end) changes.Add(new(AmendedField.EndDate, Day(end), Day(newEnd)));
        if (command.NewCoverage is { } changed && changed != coverageBefore)
        {
            changes.Add(new(AmendedField.Coverage, JsonSerializer.Serialize(coverageBefore, LedgerJson.Options), JsonSerializer.Serialize(changed, LedgerJson.Options)));
        }

        if (changes.Count == 0) throw new CommandRefusedException("no_change", "The amendment changes nothing on the entitlement.");

        var daysChanged = newEnd.DayNumber - end.DayNumber;
        var termBefore = TermArithmetic.EndDate(start, new DurationRule(entitlement.DurationValue, entitlement.DurationUnit, entitlement.EndOfMonthRule, entitlement.EndInclusive));
        var termAfter = TermArithmetic.EndDate(start, new DurationRule(newTerm.Value, newTerm.Unit, entitlement.EndOfMonthRule, entitlement.EndInclusive));
        var reduction = daysChanged < 0 || termAfter < termBefore || (command.NewCoverage is { } next && AmendmentRules.CoverageReduced(coverageBefore, next));
        var certificateIssued = reduction && await certificates.CertificateIssuedAsync(entitlement.EntitlementId, cancellationToken);
        var required = AmendmentRules.Required(policy, command.ReasonCode, daysChanged, reduction, certificateIssued);

        if (required.SystemOnly != committer.IsSystem)
        {
            throw required.SystemOnly
                ? new CommandRefusedException("system_applied_only", $"'{command.ReasonCode}' is applied by the system, never by hand.")
                : new CommandRefusedException("reason_not_system", $"The system applies only system reasons; '{command.ReasonCode}' is committed by a person.");
        }

        if (command.ReasonCode == AmendmentReason.StatutoryExtension)
        {
            var rootId = entitlement.RootEntitlementId ?? entitlement.EntitlementId;
            var maximum = command.StatutoryExtensionMaximum ?? 1;
            var chainIds = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId
                && (e.EntitlementId == rootId || e.RootEntitlementId == rootId)).Select(e => e.EntitlementId).ToListAsync(cancellationToken);
            var applications = await db.EntitlementAmendments.AsNoTracking().Where(a => a.TenantId == tenantId
                && chainIds.Contains(a.EntitlementId) && a.ReasonCode == AmendmentReason.StatutoryExtension)
                .Select(a => a.EventId).Distinct().CountAsync(cancellationToken);
            if (applications >= maximum)
                throw new CommandRefusedException("statutory_extension_already_applied", "The statutory repair extension was already applied on this entitlement chain.");
        }

        if (required.SourceDocument && command.SourceDocument is null)
        {
            throw new CommandRefusedException("source_document_required", $"A '{command.ReasonCode}' amendment cites its source document.", "attach the purchase document, or choose the reason that fits");
        }

        if (!committer.IsSystem && !AmendmentRules.Satisfies(committer.Roles, required.Role))
        {
            throw new CommandRefusedException("approval_required", $"A '{command.ReasonCode}' amendment of {Math.Abs(daysChanged)} days{(reduction ? " that reduces cover" : "")} is committed by {required.Role}.", $"route it to a {required.Role} to commit");
        }

        var monthsChanged = command.NewTerm is { Unit: DurationRule.Months } monthTerm && entitlement.DurationUnit == DurationRule.Months && command.NewStart is null && command.NewEnd is null
            ? monthTerm.Value - entitlement.DurationValue
            : (int?)null;
        var (value, valueSource, valueCurrency) = await ValueAsync(command, entitlement, required, daysChanged, monthsChanged, db, currency, tenantId, cancellationToken);
        if (required.FinanceReview && string.IsNullOrWhiteSpace(command.FinanceReviewRef))
        {
            throw new CommandRefusedException("finance_review_required", $"A '{command.ReasonCode}' amendment of {Math.Abs(daysChanged)} days needs finance review.", "record the finance review reference");
        }

        if (value is { } worth && AmendmentRules.AboveAmount(required, worth) && string.IsNullOrWhiteSpace(command.FinanceReviewRef))
        {
            throw new CommandRefusedException("finance_review_required", $"A '{command.ReasonCode}' amendment worth {worth} {valueCurrency} is above {required.FinanceReviewAboveAmount} {valueCurrency} and needs finance review.", "record the finance review reference");
        }

        if (required.CustomerNotification && string.IsNullOrWhiteSpace(command.CustomerNotificationRef))
        {
            throw new CommandRefusedException("customer_notification_required", "The customer holds a certificate for the cover this reduces.", "notify the customer and record the notification reference");
        }

        // Case 140. Read through the claims port at commit; claims adjudicate against the term in force on the claim date,
        // so a claim opened after this check is still judged on the history, not on the shortened term alone.
        if (reduction && await claims.HasOpenClaimsAsync(entitlement.UnitId, entitlement.EntitlementId, cancellationToken))
        {
            throw new CommandRefusedException("open_claims", "Cover is not reduced while claims against it are open.", "close the open claims first");
        }

        CorrectionMetadata? metadata = null;
        if (recordCorrection)
        {
            metadata = await corrections!.PrepareAsync(CorrectionCommand.AmendEntitlement, command.Correction, command.CommandId, command.ReasonCode, command.Note,
                committer.UserRef, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var approvedBy = committer.UserRef;
        foreach (var change in changes)
        {
            db.EntitlementAmendments.Add(new EntitlementAmendment
            {
                TenantId = tenantId, AmendmentId = TimeOrderedId.New(command.EventTime), EntitlementId = entitlement.EntitlementId, Field = change.Field, BeforeValue = change.Before, AfterValue = change.After,
                ReasonCode = command.ReasonCode, SourceDocumentType = command.SourceDocument?.Type, SourceDocumentId = command.SourceDocument?.Id, DaysChanged = daysChanged, ApprovedBy = approvedBy,
                CustomerNotificationRef = command.CustomerNotificationRef, ActorRef = command.ActorRef, At = command.EventTime, EventId = eventId,
            });
        }

        entitlement.StartDate = newStart;
        entitlement.DurationValue = newTerm.Value;
        entitlement.DurationUnit = newTerm.Unit;
        entitlement.EndAdjustmentDays = adjustment;
        entitlement.EndDate = newEnd;
        if (command.ReasonCode == AmendmentReason.StatutoryExtension) entitlement.StatutoryExtensionApplied = true;
        if (command.NewCoverage is { } applied) entitlement.Coverage = JsonSerializer.Serialize(applied, LedgerJson.Options);

        await ledger.AppendAsync(entitlement, EntitlementEventType.Amended, key, command.EventTime, command.ActorRef, new
        {
            entitlement_id = entitlement.EntitlementId, coverage_family = entitlement.CoverageFamily, reason_code = command.ReasonCode, days_changed = daysChanged, reduction,
            approval_level = required.Role, approved_by = approvedBy, finance_review_ref = command.FinanceReviewRef, customer_notification_ref = command.CustomerNotificationRef,
            certificate_issued = certificateIssued, note = command.Note, changes, value, value_source = valueSource, value_currency = valueCurrency,
        }, command.SourceDocument, cancellationToken, eventId, metadata);
        if (metadata is not null)
            await corrections!.RecordAsync(CorrectionCommand.AmendEntitlement, metadata, command.ActorRef, command.EventTime, [entitlement.UnitId], cancellationToken);
        return new(entitlement.EntitlementId, newStart, newEnd, daysChanged, reduction, required.Role, approvedBy, changes, eventId, false, value, valueSource);
    }

    /// <summary>
    /// The amendment's value (P2-T08b): from the entitlement's policy version when it states one, else what the user
    /// entered. An amount bound on the tier makes the value mandatory and needs the tenant currency; a policy value in
    /// another currency (the tenant's changed after it was published) is never converted, the amendment is refused.
    /// </summary>
    private static async Task<(decimal? Value, string? Source, string? Currency)> ValueAsync(AmendEntitlement command, Entitlement entitlement, AmendmentRequirement required, int daysChanged, int? monthsChanged,
        SerialisationDbContext db, ITenantCurrency currency, TenantId tenantId, CancellationToken cancellationToken)
    {
        var bounded = required.FinanceReviewAboveAmount is not null;
        var json = await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.PolicyVersionId == entitlement.PolicyVersionId).Select(v => v.AmendmentValue).FirstOrDefaultAsync(cancellationToken);
        var term = json is null ? null : JsonSerializer.Deserialize<AmendmentValueTerm>(json, LedgerJson.Options);
        if (term is null && command.EnteredValue is null && !bounded) return (null, null, null);

        var tenantCurrency = await currency.GetAsync(cancellationToken);
        if (tenantCurrency is null)
        {
            if (bounded) throw new CommandRefusedException("currency_not_configured", "The approval tier has an amount bound but the tenant currency is not set up.", "set up the tenant currency");
            return (null, null, null);
        }

        if (term is not null)
        {
            if (string.Equals(term.Currency, tenantCurrency, StringComparison.Ordinal)) return (AmendmentRules.Value(term, daysChanged, monthsChanged), AmendmentValueSource.Policy, tenantCurrency);
            if (bounded) throw new CommandRefusedException("value_currency_differs", $"The policy values its term in {term.Currency}; the tenant works in {tenantCurrency}.", "publish a policy version valued in the tenant currency");
        }

        if (command.EnteredValue is { } entered)
        {
            if (entered < 0) throw new CommandRefusedException("value_negative", "An amendment's value cannot be negative.");
            return (Math.Round(entered, 2, MidpointRounding.AwayFromZero), AmendmentValueSource.Entered, tenantCurrency);
        }

        if (bounded) throw new CommandRefusedException("value_required", $"The policy states no value for its term; enter the value of this amendment in {tenantCurrency}.", "enter the amendment's value");
        return (null, null, null);
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static AmendEntitlementResult Replay(Entitlement entitlement, UnitEvent replayed)
    {
        using var attributes = JsonDocument.Parse(replayed.Attributes ?? "{}");
        var a = attributes.RootElement;
        var changes = a.TryGetProperty("changes", out var c) ? c.Deserialize<List<EntitlementFieldChange>>(LedgerJson.Options) ?? [] : [];
        var after = (string field) => changes.FirstOrDefault(ch => ch.Field == field)?.After is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : (DateOnly?)null;
        return new(entitlement.EntitlementId, after(AmendedField.StartDate) ?? entitlement.StartDate, after(AmendedField.EndDate) ?? entitlement.EndDate, a.GetProperty("days_changed").GetInt32(),
            a.GetProperty("reduction").GetBoolean(), a.GetProperty("approval_level").GetString() ?? "", a.GetProperty("approved_by").GetString() ?? "", changes, replayed.EventId, true,
            a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null,
            a.TryGetProperty("value_source", out var vs) && vs.ValueKind == JsonValueKind.String ? vs.GetString() : null);
    }
}
