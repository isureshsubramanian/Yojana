namespace Yojana.Serialisation.Contracts;

// Selection sets (ADR-0001 "Bulk selection sets", AI 28; MOD-11): how a bulk command addresses units. Resolution turns a
// source into unit ids once and stores them; a preview computes every unit's outcome through the command itself; the
// executor applies the set in one transaction with one source document and one approval.

/// <summary>Where the units of a selection come from. One of the members is set.</summary>
public sealed record SelectionSource(
    string Kind,
    DocumentRef? Document = null,
    Guid? PartyRef = null,
    Guid? ItemId = null,
    DateTime? From = null,
    DateTime? To = null,
    string? EventType = null,
    IReadOnlyList<string>? Serials = null,
    string? Scheme = null,
    string? RangeStart = null,
    string? RangeEnd = null)
{
    public static class Kinds
    {
        public const string Document = "document";
        public const string Customer = "customer";
        public const string Item = "item";
        public const string DateRange = "date_range";
        public const string PastedList = "pasted_list";
        public const string SerialRange = "serial_range";
    }

    public static SelectionSource ByDocument(DocumentRef document) => new(Kinds.Document, Document: document);
    public static SelectionSource ByCustomer(Guid partyRef) => new(Kinds.Customer, PartyRef: partyRef);
    public static SelectionSource ByItem(Guid itemId) => new(Kinds.Item, ItemId: itemId);
    public static SelectionSource ByDateRange(DateTime from, DateTime to, string? eventType = null) => new(Kinds.DateRange, From: from, To: to, EventType: eventType);
    public static SelectionSource ByPastedList(IReadOnlyList<string> serials) => new(Kinds.PastedList, Serials: serials);
    public static SelectionSource BySerialRange(string scheme, string start, string end) => new(Kinds.SerialRange, Scheme: scheme, RangeStart: start, RangeEnd: end);
}

/// <summary>Resolve a source into a stored selection set; the result names what resolved and what did not.</summary>
public sealed record ResolveSelectionSet(Guid CommandId, SelectionSource Source, string ActorRef);

public sealed record ResolveSelectionSetResult(Guid SelectionId, int Resolved, IReadOnlyList<string> Unknown, IReadOnlyList<string> Rejected, SelectionSource Source);

/// <summary>Per-unit result of a preview or an execution: what the command would do or did, and why not when it would not.</summary>
public sealed record UnitOutcome(Guid UnitId, string? Serial, string Outcome, string? Reason, string? Detail = null, string? UnblockingStep = null)
{
    public static class Outcomes
    {
        public const string Change = "change";
        public const string Skip = "skip";
        public const string Block = "block";
        public const string Done = "done";
        public const string Failed = "failed";
    }
}

/// <summary>The common correction preview used by command, workbench and approver views.</summary>
public sealed record CorrectionPreview(
    Guid? SelectionId,
    string Command,
    int UnitCount,
    int Change,
    int Skip,
    int Block,
    IReadOnlyList<UnitOutcome> Units,
    IReadOnlyList<CorrectionEventPreview> UnitEvents,
    IReadOnlyList<CorrectionEntitlementPreview> EntitlementChanges,
    IReadOnlyList<CorrectionCertificatePreview> CertificateVersions,
    IReadOnlyList<CorrectionPrintJobPreview> PrintJobs,
    CommandRefusal? Refusal = null);

public sealed record CorrectionEventPreview(Guid UnitId, string EventType, DateTime EffectiveAt, string? Summary = null);
public sealed record CorrectionEntitlementPreview(Guid UnitId, string Change, Guid? EntitlementId = null, string? Summary = null);
public sealed record CorrectionCertificatePreview(Guid UnitId, string CertificateType, string Action, string Reason);
public sealed record CorrectionPrintJobPreview(Guid UnitId, string JobType, string Reason);

/// <summary>Run the named bulk operation over a stored selection without writing; the outcomes are the command's own.</summary>
/// <see cref="Approval"/> is who previews and the roles they hold (the endpoint sets it from the claims), so the preview judges the set as that person would commit it.
public sealed record PreviewBulkOperation(Guid SelectionId, string Operation, BulkOperationArguments Arguments, int Skip = 0, int Take = 50, string? Find = null, string? OutcomeFilter = null, BulkApproval? Approval = null);

public sealed record BulkPreviewResult(Guid SelectionId, string Operation, int Total, int Change, int Skip, int Block, IReadOnlyList<UnitOutcome> Page, int PageSkip, int PageTake);

/// <summary>Arguments every bulk operation shares; operation-specific ones travel in <see cref="Extra"/>.</summary>
public sealed record BulkOperationArguments(
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    DocumentRef SourceDocument,
    string? Reason = null,
    string? Note = null,
    IReadOnlyDictionary<string, string>? Extra = null,
    string InitiatedVia = CorrectionInitiatedVia.Api,
    string? InitiatingRole = null,
    Guid? CorrectsCorrectionId = null);

/// <summary>
/// Approval of a bulk execution: who approved, or that the policy needed none (the policy as tenant data is P3; P1 records
/// what was given). <see cref="Roles"/> are the approver's roles in the tenant; the HTTP endpoints always set approver and
/// roles from the signed-in user's claims, whatever the body says.
/// </summary>
public sealed record BulkApproval(string? ApproverRef, bool Required = false, IReadOnlyList<string>? Roles = null);

/// <summary>Execute the operation over the selection in one transaction, one source document, one approval. Blocked and skipped units stay as they are; the report lists every unit.</summary>
public sealed record ExecuteBulkOperation(Guid CommandId, Guid SelectionId, string Operation, BulkOperationArguments Arguments, BulkApproval Approval);

public sealed record BulkExecutionResult(Guid CommandId, Guid SelectionId, string Operation, int Total, int Done, int Skipped, int Blocked, Guid ValidationReportId, bool Replayed);

/// <summary>Find within a stored selection's last preview or execution report (server-side; matches become a selection set).</summary>
public sealed record FindInSelection(Guid SelectionId, string Query, int Skip = 0, int Take = 50);
