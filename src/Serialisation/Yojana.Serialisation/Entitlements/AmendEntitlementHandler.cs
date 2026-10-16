using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// <c>AmendEntitlement</c> (P2-T08, ADR-0001 correction table, cases 129 and 140): one entitlement's start, term, end or
/// coverage changed under a reason code, approved by reason and days changed, with before and after on one
/// EntitlementAmendment row per field and one <c>entitlement.amended</c> fact published as <c>Entitlement.Amended</c>.
/// A reduction is raised to the tenant's reduction role, needs a customer notification once a certificate was issued,
/// and is refused while claims are open. The certificate's new version is the certificate module's answer to the
/// published event (P6); this command never touches a certificate.
/// </summary>
public static class AmendEntitlementHandler
{
    // Coupled to the factory registration of AmendmentApprovalSettings in SerialisationModule: the settings read goes
    // through PlatformDbContext, and were Wolverine to see that context in this handler's graph it would refuse to pick
    // the transactional one. Naming it with [Transactional] instead opens the connection before the tenant middleware
    // runs, so row-level security hides every row. The factory keeps the platform context out of the generated code.
    public static async Task<AmendEntitlementResult> Handle(AmendEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates, ITenantContext tenant, CancellationToken cancellationToken)
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

        if (required.SourceDocument && command.SourceDocument is null)
        {
            throw new CommandRefusedException("source_document_required", $"A '{command.ReasonCode}' amendment cites its source document.", "attach the purchase document, or choose the reason that fits");
        }

        if (!committer.IsSystem && !AmendmentRules.Satisfies(committer.Roles, required.Role))
        {
            throw new CommandRefusedException("approval_required", $"A '{command.ReasonCode}' amendment of {Math.Abs(daysChanged)} days{(reduction ? " that reduces cover" : "")} is committed by {required.Role}.", $"route it to a {required.Role} to commit");
        }

        if (required.FinanceReview && string.IsNullOrWhiteSpace(command.FinanceReviewRef))
        {
            throw new CommandRefusedException("finance_review_required", $"A '{command.ReasonCode}' amendment of {Math.Abs(daysChanged)} days needs finance review.", "record the finance review reference");
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
        if (command.NewCoverage is { } applied) entitlement.Coverage = JsonSerializer.Serialize(applied, LedgerJson.Options);

        await ledger.AppendAsync(entitlement, EntitlementEventType.Amended, key, command.EventTime, command.ActorRef, new
        {
            entitlement_id = entitlement.EntitlementId, coverage_family = entitlement.CoverageFamily, reason_code = command.ReasonCode, days_changed = daysChanged, reduction,
            approval_level = required.Role, approved_by = approvedBy, finance_review_ref = command.FinanceReviewRef, customer_notification_ref = command.CustomerNotificationRef,
            certificate_issued = certificateIssued, note = command.Note, changes,
        }, command.SourceDocument, cancellationToken, eventId);
        return new(entitlement.EntitlementId, newStart, newEnd, daysChanged, reduction, required.Role, approvedBy, changes, eventId, false);
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static AmendEntitlementResult Replay(Entitlement entitlement, UnitEvent replayed)
    {
        using var attributes = JsonDocument.Parse(replayed.Attributes ?? "{}");
        var a = attributes.RootElement;
        var changes = a.TryGetProperty("changes", out var c) ? c.Deserialize<List<EntitlementFieldChange>>(LedgerJson.Options) ?? [] : [];
        var after = (string field) => changes.FirstOrDefault(ch => ch.Field == field)?.After is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : (DateOnly?)null;
        return new(entitlement.EntitlementId, after(AmendedField.StartDate) ?? entitlement.StartDate, after(AmendedField.EndDate) ?? entitlement.EndDate, a.GetProperty("days_changed").GetInt32(),
            a.GetProperty("reduction").GetBoolean(), a.GetProperty("approval_level").GetString() ?? "", a.GetProperty("approved_by").GetString() ?? "", changes, replayed.EventId, true);
    }
}
