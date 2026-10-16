using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.Entitlements;

public static class SalesInvoicePostedHandler
{
    public static async Task<ConsumedEventResult> Handle(SalesInvoicePosted message, ISalesDocumentQueryPort sales, SerialisationDbContext db,
        SelectionSetResolver selections, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbished,
        WarrantyReferenceSettings reference, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var invoice = await sales.GetInvoiceAsync(message.Document, cancellationToken)
            ?? throw new CommandRefusedException("document_unavailable", $"Invoice {message.Document.Id:N} could not be read through the sales port.");
        var affected = 0;
        var problems = new List<string>();
        foreach (var line in invoice.Lines.Where(l => l.ItemType == "warranty_service"))
        {
            try
            {
                affected += await ApplyLineAsync(invoice, line, null, message.Envelope, db, selections, ledger, resolver, store, zones, refurbished, reference, approvals, claims, certificates, currency, tenant, clock, cancellationToken);
            }
            catch (CommandRefusedException ex)
            {
                await RaiseAsync(invoice, line, Missing(ex.Rule), ex.Message, db, tenant, clock, cancellationToken);
                problems.Add($"line {line.LineNo}: {ex.Rule}: {ex.Message}");
            }
        }

        return new("Sales.InvoicePosted", invoice.Document, affected, problems.Count, problems, affected == 0 && problems.Count == 0);
    }

    internal static async Task<int> ApplyLineAsync(InvoiceDocument invoice, InvoiceLine line, IReadOnlyList<string>? suppliedSerials, ThinEnvelope envelope,
        SerialisationDbContext db, SelectionSetResolver selections, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbished,
        WarrantyReferenceSettings reference, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (line.Target is null) throw new CommandRefusedException("target_rule_missing", "A warranty-service line needs a target rule.");
        var version = line.PolicyVersionId is { } versionId
            ? await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenant.TenantId && v.PolicyVersionId == versionId, cancellationToken)
            : await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenant.TenantId && v.PolicyCode == line.PolicyCode && v.Status == PolicyVersionStatus.Published && v.AppliesToContractsFrom <= DateOnly.FromDateTime(invoice.PostedAt)).OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
        if (version is null) throw new CommandRefusedException("no_policy", "The warranty-service line has no applicable published policy.");
        var terms = await store.ReadTermsAsync(version, cancellationToken);
        if (WarrantyLineRules.Regulation(terms, await reference.GetAsync(cancellationToken), invoice.Country, invoice.RegionGroup, invoice.HasLicensedObligor) is { } regulation)
            throw new CommandRefusedException(regulation, "The regulated plan is not permitted, or lacks required obligor evidence, in this jurisdiction.");

        var source = TargetSource(invoice, line.Target, suppliedSerials);
        var selectionId = Deterministic(invoice.Document, line.LineNo, "selection");
        // An unresolved target is deliberately re-evaluated: a stored empty selection is evidence from the earlier
        // attempt, not a permanent answer after serials or a referenced delivery arrive.
        var stale = await db.SelectionSets.SingleOrDefaultAsync(s => s.TenantId == tenant.TenantId && s.SelectionId == selectionId && (s.ResolvedCount == 0 || suppliedSerials != null), cancellationToken);
        if (stale is not null)
        {
            await db.SelectionSetMembers.Where(m => m.TenantId == tenant.TenantId && m.SelectionId == selectionId).ExecuteDeleteAsync(cancellationToken);
            db.SelectionSets.Remove(stale);
            await db.SaveChangesAsync(cancellationToken);
        }
        var resolved = await selections.ResolveAsync(new ResolveSelectionSet(selectionId, source, envelope.Source), cancellationToken);
        // The resolver tracks its member rows without saving; the members are read back from the database below.
        await db.SaveChangesAsync(cancellationToken);
        if (resolved.Resolved == 0)
        {
            var reason = line.Target.Rule == WarrantyTargetRule.ReferencedDocument ? "referenced_document_not_posted" : "serials_not_captured";
            throw new CommandRefusedException(reason, "The warranty line has no resolved serialised target units.");
        }
        if (resolved.Unknown.Count > 0 || resolved.Rejected.Count > 0) throw new CommandRefusedException("target_not_serialised", "One or more warranty targets are not known serialised units.");
        var unitIds = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenant.TenantId && m.SelectionId == selectionId).Select(m => m.UnitId).ToListAsync(cancellationToken);
        var family = await db.CoverageFamilies.AsNoTracking().SingleOrDefaultAsync(f => f.TenantId == tenant.TenantId && f.FamilyCode == version.CoverageFamily, cancellationToken)
            ?? throw new CommandRefusedException("family_unknown", $"Coverage family '{version.CoverageFamily}' is not defined.");
        var sourceDocument = invoice.Document with { Line = line.LineNo };
        var contractDate = DateOnly.FromDateTime(invoice.PostedAt);
        foreach (var unitId in unitIds)
        {
            var commandId = Deterministic(invoice.Document, line.LineNo, unitId.ToString("N"));
            var live = await db.Entitlements.FirstOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.UnitId == unitId && e.CoverageFamily == version.CoverageFamily && e.Status != EntitlementStatus.Closed, cancellationToken);
            if (family.Stacking)
            {
                await AttachExtendedWarrantyHandler.Handle(new AttachExtendedWarranty(commandId, unitId, version.CoverageFamily, contractDate, invoice.ObligorEntityId, invoice.PostedAt,
                    line.Price / Math.Max(1, line.Quantity), line.Currency, sourceDocument, invoice.PostedAt, envelope.Source, version.PolicyVersionId,
                    invoice.CustomerPartyRef, PartyType.Customer, invoice.CustomerPartyRef, PartyType.Customer, invoice.Country, invoice.SiteLocationId, invoice.CustomerPoRef),
                    db, ledger, resolver, store, zones, tenant, clock, cancellationToken);
            }
            else if (live is null)
            {
                await CreateEntitlementHandler.CreateAsync(new CreateEntitlement(commandId, unitId, version.CoverageFamily, contractDate, invoice.ObligorEntityId, invoice.PostedAt,
                    envelope.Source, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = invoice.PostedAt, [StartTrigger.PlanPurchase] = invoice.PostedAt }, version.PolicyVersionId,
                    invoice.CustomerPartyRef, PartyType.Customer, invoice.CustomerPartyRef, PartyType.Customer, Country: invoice.Country, SiteLocationId: invoice.SiteLocationId,
                    StartEvidenceRef: $"{invoice.Document.Type}:{invoice.Document.Id:N}", SourceDocument: sourceDocument, CustomerPoRef: invoice.CustomerPoRef),
                    db, ledger, resolver, store, zones, refurbished, tenant, clock, null, [], false, null, null, cancellationToken);
            }
            else
            {
                var oldCoverage = JsonSerializer.Deserialize<CoverageTerms>(live.Coverage, LedgerJson.Options);
                if (oldCoverage?.ServiceMode == terms.Coverage.ServiceMode)
                {
                    var extensionStart = (live.EndDate ?? contractDate).AddDays(1);
                    var newEnd = TermArithmetic.EndDate(extensionStart, terms.Duration);
                    await AmendEntitlementHandler.Handle(new AmendEntitlement(commandId, live.EntitlementId, AmendmentReason.Purchase, invoice.PostedAt, envelope.Source,
                        NewEnd: newEnd, SourceDocument: sourceDocument, CommittedBy: new AmendmentCommitter("invoice-posting", [YojanaRole.ServiceDesk])),
                        db, ledger, approvals, claims, certificates, currency, tenant, cancellationToken);
                    live.CustomerPoRef = invoice.CustomerPoRef;
                }
                else
                {
                    await SupersedeEntitlementHandler.SupersedeAsync(new SupersedeEntitlement(commandId, live.EntitlementId, contractDate, AmendmentReason.Purchase,
                        invoice.PostedAt, envelope.Source, version.PolicyVersionId, SourceDocument: sourceDocument,
                        CommittedBy: new AmendmentCommitter("invoice-posting", [YojanaRole.ServiceDesk])), db, ledger, store, claims, certificates, tenant, clock, cancellationToken);
                }
            }
        }
        return unitIds.Count;
    }

    private static SelectionSource TargetSource(InvoiceDocument invoice, WarrantyLineTarget target, IReadOnlyList<string>? suppliedSerials) => target.Rule switch
    {
        WarrantyTargetRule.SameOrder => SelectionSource.ByDocument(invoice.Order),
        WarrantyTargetRule.ReferencedDocument => SelectionSource.ByDocument(target.Document ?? throw new CommandRefusedException("referenced_document_required", "A referenced-document target names the document.")),
        WarrantyTargetRule.AttachedList when suppliedSerials is { Count: > 0 } => SelectionSource.ByPastedList(suppliedSerials),
        WarrantyTargetRule.AttachedList when target.Serials is { Count: > 0 } => SelectionSource.ByPastedList(target.Serials),
        WarrantyTargetRule.AttachedList => SelectionSource.BySerialRange(target.Scheme ?? "", target.RangeStart ?? "", target.RangeEnd ?? ""),
        _ => throw new CommandRefusedException("target_rule_unknown", $"Warranty target rule '{target.Rule}' is not known."),
    };

    private static async Task RaiseAsync(InvoiceDocument invoice, InvoiceLine line, string missing, string detail, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var existing = await db.WarrantyLineExceptions.FirstOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.DocumentType == invoice.Document.Type && e.DocumentId == invoice.Document.Id && e.DocumentLine == line.LineNo, cancellationToken);
        if (existing is not null) { existing.MissingInput = missing; existing.Detail = detail[..Math.Min(detail.Length, 1000)]; return; }
        db.WarrantyLineExceptions.Add(new WarrantyLineException { TenantId = tenant.TenantId, ExceptionId = TimeOrderedId.New(invoice.PostedAt), DocumentType = invoice.Document.Type,
            DocumentId = invoice.Document.Id, DocumentLine = line.LineNo, TargetDocumentType = line.Target?.Document?.Type, TargetDocumentId = line.Target?.Document?.Id,
            ItemId = line.ItemId, Status = WarrantyLineExceptionStatus.Open, MissingInput = missing,
            Detail = detail[..Math.Min(detail.Length, 1000)], TriggerAt = invoice.PostedAt, RaisedAt = clock.UtcNow });
    }

    private static string Missing(string rule) => rule switch
    {
        "no_policy" or "policy_version_unknown" => WarrantyLineMissingInput.NoPolicy,
        "referenced_document_not_posted" => WarrantyLineMissingInput.ReferencedDocumentNotPosted,
        "serials_not_captured" => WarrantyLineMissingInput.SerialsNotCaptured,
        "target_not_serialised" => WarrantyLineMissingInput.TargetNotSerialised,
        "regulated_jurisdiction" => WarrantyLineMissingInput.RegulatedJurisdiction,
        _ => WarrantyLineMissingInput.CreationFailed,
    };

    private static Guid Deterministic(DocumentRef document, int line, string suffix) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{document.Type}|{document.Id:N}|{line}|{suffix}")).AsSpan(0, 16));
}

public static class ResolveWarrantyLineExceptionHandler
{
    public static async Task<WarrantyLineExceptionRow> Handle(ResolveWarrantyLineException command, ISalesDocumentQueryPort sales, SerialisationDbContext db,
        SelectionSetResolver selections, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbished,
        WarrantyReferenceSettings reference, AmendmentApprovalSettings approvals, IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var row = await db.WarrantyLineExceptions.SingleOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.ExceptionId == command.ExceptionId, cancellationToken)
            ?? throw new CommandRefusedException("exception_unknown", $"Warranty-line exception {command.ExceptionId} does not exist.");
        if (row.Status != WarrantyLineExceptionStatus.Open) return ToRow(row);
        var document = new DocumentRef(row.DocumentType, row.DocumentId);
        var invoice = await sales.GetInvoiceAsync(document, cancellationToken) ?? throw new CommandRefusedException("document_unavailable", "The source invoice is no longer available through the sales port.");
        var line = invoice.Lines.SingleOrDefault(l => l.LineNo == row.DocumentLine) ?? throw new CommandRefusedException("line_unknown", "The warranty line is no longer present on the invoice.");
        var envelope = new ThinEnvelope(command.ExceptionId, invoice.ObligorEntityId, row.TriggerAt, command.ActorRef);
        await SalesInvoicePostedHandler.ApplyLineAsync(invoice with { PostedAt = row.TriggerAt }, line, command.Serials.Count == 0 ? null : command.Serials, envelope,
            db, selections, ledger, resolver, store, zones, refurbished, reference, approvals, claims, certificates, currency, tenant, clock, cancellationToken);
        row.Status = WarrantyLineExceptionStatus.Resolved;
        row.ResolvedAt = clock.UtcNow;
        row.ResolvedBy = command.ActorRef;
        row.Detail = null;
        return ToRow(row);
    }

    private static WarrantyLineExceptionRow ToRow(WarrantyLineException row) => new(row.ExceptionId, new DocumentRef(row.DocumentType, row.DocumentId, row.DocumentLine), row.ItemId,
        row.Status, row.MissingInput, row.Detail, row.TriggerAt, row.RaisedAt, row.ResolvedAt);
}
