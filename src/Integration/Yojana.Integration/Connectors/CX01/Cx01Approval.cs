using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

// Approval procedures and the service interface (verified on release 1000340, 2026-10-20): documents created through
// the service interface post directly even when the company has an active approval template for that document type,
// and service-interface drafts raise no approval request. Approval therefore cannot be delegated to the remote.
// Per document type the tenant chooses: hold a remote DRAFT for a person to add in the ERP client (which runs the
// customer's approval procedure), or post directly after the customer's explicit, recorded acceptance of the bypass.

public static class Cx01ApprovalModes
{
    public const string DraftForReview = "draft_for_review";
    public const string PostDirectly = "post_directly";
}

/// <summary>
/// One document type's handling. Acceptance (who, when, evidence) is required for <c>post_directly</c> wherever the
/// survey found an active approval template for that document type.
/// </summary>
public sealed record Cx01ApprovalHandling(
    string Mode,
    string? AcceptedBy = null,
    DateTime? AcceptedAt = null,
    string? Evidence = null)
{
    public bool Accepted => !string.IsNullOrWhiteSpace(AcceptedBy) && AcceptedAt is not null
        && !string.IsNullOrWhiteSpace(Evidence);
}

public sealed record Cx01SurveyApprovalTemplate(int Code, string Name, bool Active, IReadOnlyList<string> DocumentTypes);

/// <summary>Collections the adapter may post, with their approval document type and draft object code.</summary>
internal sealed record Cx01ApprovalTarget(string Collection, string DocumentType, string? DraftObjectCode);

internal static class Cx01ApprovalCatalogue
{
    // Document type names are the remote's approval-template enumeration (read from the service metadata on
    // 1000340). Stock transfers have a separate draft entity, so no draft path is offered for them here.
    public static IReadOnlyList<Cx01ApprovalTarget> Targets { get; } =
    [
        new("DeliveryNotes", "atdtDelivery", "oDeliveryNotes"),
        new("Returns", "atdtReturns", "oReturns"),
        new("Invoices", "atdtArInvoice", "oInvoices"),
        new("CreditNotes", "atdtArCreditMemo", "oCreditNotes"),
        new("Orders", "atdtOrder", "oOrders"),
        new("PurchaseDeliveryNotes", "atdtGoodsReceiptPO", "oPurchaseDeliveryNotes"),
        new("PurchaseReturns", "atdtGoodsReturns", "oPurchaseReturns"),
        new("InventoryGenEntries", "atdtGoodsReceipt", "oInventoryGenEntry"),
        new("InventoryGenExits", "atdtGoodsIssue", "oInventoryGenExit"),
        new("StockTransfers", "atdtInventoryTransfer", null),
    ];

    public static Cx01ApprovalTarget? Find(string collection) =>
        Targets.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Collection, collection));
}

public sealed record Cx01ApprovalEntry(
    string Collection,
    string DocumentType,
    bool DraftSupported,
    IReadOnlyList<string> ActiveTemplates,
    Cx01ApprovalHandling? Configured,
    string EffectiveMode,
    string? Warning);

public sealed record Cx01ApprovalView(Guid ConnectionId, DateTime? SurveyObservedAt, bool TemplatesKnown,
    IReadOnlyList<Cx01ApprovalEntry> DocumentTypes);

/// <summary>Mode per document type; a null entry restores the default. Evidence is required to bypass approval.</summary>
public sealed record UpdateCx01ApprovalHandling(IReadOnlyDictionary<string, Cx01ApprovalHandlingInput?> DocumentTypes);

public sealed record Cx01ApprovalHandlingInput(string Mode, string? Evidence = null);

public sealed class Cx01ApprovalRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

internal static class Cx01ApprovalPolicy
{
    /// <summary>
    /// Unknown templates (no survey, or the integration user may not read them) count as "approval may apply":
    /// a draft costs a person one click, an unnoticed bypass costs the customer's control.
    /// </summary>
    public static string EffectiveMode(string collection, Cx01ConnectionSettings configured, Cx01SurveyResult? survey)
    {
        var target = Cx01ApprovalCatalogue.Find(collection);
        if (target is null) return Cx01ApprovalModes.PostDirectly;
        var governed = ActiveTemplates(target, survey) is not { } active || active.Count > 0;
        var handling = configured.ApprovalHandling?.GetValueOrDefault(collection);
        return handling?.Mode switch
        {
            Cx01ApprovalModes.DraftForReview => Cx01ApprovalModes.DraftForReview,
            Cx01ApprovalModes.PostDirectly when !governed || handling.Accepted => Cx01ApprovalModes.PostDirectly,
            _ => governed ? Cx01ApprovalModes.DraftForReview : Cx01ApprovalModes.PostDirectly,
        };
    }

    /// <summary>Null when the templates are unknown.</summary>
    public static IReadOnlyList<string>? ActiveTemplates(Cx01ApprovalTarget target, Cx01SurveyResult? survey) =>
        survey?.ApprovalTemplates?.Where(x => x.Active && x.DocumentTypes.Contains(target.DocumentType,
            StringComparer.Ordinal)).Select(x => x.Name).Order(StringComparer.Ordinal).ToList();

    public static void Validate(IReadOnlyDictionary<string, Cx01ApprovalHandling>? handling)
    {
        foreach (var (collection, value) in handling ?? new Dictionary<string, Cx01ApprovalHandling>())
        {
            if (Cx01ApprovalCatalogue.Find(collection) is null)
                throw new InvalidOperationException($"Approval handling names an unknown document type '{collection}'.");
            if (value.Mode is not (Cx01ApprovalModes.DraftForReview or Cx01ApprovalModes.PostDirectly))
                throw new InvalidOperationException($"Approval handling mode '{value.Mode}' is not supported.");
            if (value.Evidence is { Length: > 500 })
                throw new InvalidOperationException("Approval acceptance evidence is limited to 500 characters.");
        }
    }
}

public sealed class Cx01ApprovalService(Cx01Settings settings, Cx01SurveyService survey, IClock clock)
{
    public async Task<Cx01ApprovalView> GetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var latest = await survey.LatestAsync(connectionId, cancellationToken);
        return new(connectionId, latest?.ObservedAt, latest?.ApprovalTemplates is not null,
            Cx01ApprovalCatalogue.Targets.Select(target =>
            {
                var active = Cx01ApprovalPolicy.ActiveTemplates(target, latest);
                var handling = configured.ApprovalHandling?.GetValueOrDefault(target.Collection);
                var effective = Cx01ApprovalPolicy.EffectiveMode(target.Collection, configured, latest);
                return new Cx01ApprovalEntry(target.Collection, target.DocumentType, target.DraftObjectCode is not null,
                    active ?? [], handling, effective, Warning(target, active, handling, effective));
            }).ToList());
    }

    public async Task<Cx01ApprovalView> SetAsync(Guid connectionId, UpdateCx01ApprovalHandling request, string actor,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var latest = await survey.LatestAsync(connectionId, cancellationToken);
        var handling = new Dictionary<string, Cx01ApprovalHandling>(
            configured.ApprovalHandling ?? new Dictionary<string, Cx01ApprovalHandling>(), StringComparer.Ordinal);
        foreach (var (collection, input) in request.DocumentTypes ?? new Dictionary<string, Cx01ApprovalHandlingInput?>())
        {
            var target = Cx01ApprovalCatalogue.Find(collection)
                ?? throw new Cx01ApprovalRuleException("approval_unknown_document_type",
                    $"Unknown document type '{collection}'.");
            if (input is null)
            {
                handling.Remove(collection);
                continue;
            }
            if (input.Mode == Cx01ApprovalModes.DraftForReview)
            {
                handling[collection] = new(input.Mode);
                continue;
            }
            if (input.Mode != Cx01ApprovalModes.PostDirectly)
                throw new Cx01ApprovalRuleException("approval_mode_invalid", $"Mode '{input.Mode}' is not supported.");
            var governed = Cx01ApprovalPolicy.ActiveTemplates(target, latest) is not { } active || active.Count > 0;
            var evidence = string.IsNullOrWhiteSpace(input.Evidence) ? null : input.Evidence.Trim();
            if (governed && evidence is null)
                throw new Cx01ApprovalRuleException("approval_bypass_needs_acceptance",
                    "An approval procedure may apply to this document type. Posting directly bypasses it; record "
                    + "the customer's acceptance (who agreed and where) as evidence.");
            handling[collection] = evidence is null ? new(input.Mode)
                : new(input.Mode, actor, clock.UtcNow, evidence);
        }
        try
        {
            await settings.SetAsync(connectionId, configured with
            {
                ApprovalHandling = handling.Count == 0 ? null : handling,
            }, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not Cx01ApprovalRuleException)
        {
            throw new Cx01ApprovalRuleException("approval_handling_invalid", ex.Message);
        }
        return await GetAsync(connectionId, cancellationToken);
    }

    private static string? Warning(Cx01ApprovalTarget target, IReadOnlyList<string>? active,
        Cx01ApprovalHandling? handling, string effective)
    {
        if (active is null)
            return "Approval templates are not known (no survey yet, or the integration user may not read them); "
                + "documents are held as drafts for review.";
        if (handling?.Mode == Cx01ApprovalModes.PostDirectly && effective == Cx01ApprovalModes.DraftForReview)
            return "An approval template became active after posting directly was chosen without acceptance; "
                + "documents are held as drafts until the bypass is accepted.";
        if (effective == Cx01ApprovalModes.DraftForReview && target.DraftObjectCode is null)
            return "This document type cannot be held as a draft; each one becomes a hand-entry task.";
        return null;
    }
}

/// <summary>
/// Posts one document according to the approval policy: directly, or as a remote draft that a person adds in the
/// ERP client. The caller supplies the complete document body; this class owns only the draft/direct decision,
/// the reference and query-before-create on the draft. The opaque pending key carries draft, collection and field.
/// </summary>
public sealed class Cx01DocumentPoster(Cx01Settings settings, Cx01SurveyService survey, Cx01Client client)
{
    public async Task<OutboundCreateResult> PostAsync(Guid connectionId, string collection,
        IReadOnlyDictionary<string, object?> body, string referenceField, string referenceText,
        CancellationToken cancellationToken, bool qualifyId = false)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        await Cx01ReferenceRules.CheckAsync(survey, connectionId, collection, referenceText, configured, cancellationToken);
        var latest = await survey.LatestAsync(connectionId, cancellationToken);
        var reference = configured.OwnReferencePrefix + referenceText;
        var document = new Dictionary<string, object?>(body, StringComparer.Ordinal) { [referenceField] = reference };
        try
        {
            if (Cx01ApprovalPolicy.EffectiveMode(collection, configured, latest) == Cx01ApprovalModes.PostDirectly)
            {
                using var created = await client.SendJsonAsync(connectionId, HttpMethod.Post,
                    Cx01Protocol.Root + collection, document, false, cancellationToken)
                    ?? throw new InvalidOperationException("CX01 create returned no document.");
                var key = Cx01ReviewKey.Text(created.RootElement, Cx01DocumentCatalogue.KeyField(collection))
                    ?? throw new InvalidOperationException("CX01 create returned no document key.");
                using var observed = await client.GetAsync(connectionId, Cx01Protocol.Root + collection
                    + "(" + Cx01Protocol.KeyLiteral(key) + ")", cancellationToken);
                return OutboundCreateResult.Created(qualifyId ? Cx01DocumentCatalogue.ExternalId(collection, key) : key,
                    Cx01Mapper.ChangeMarker(observed.RootElement, key));
            }

            var target = Cx01ApprovalCatalogue.Find(collection);
            if (target?.DraftObjectCode is null)
                return OutboundCreateResult.Unsupported(
                    "This document type is under approval and cannot be held as a draft; enter it in the ERP by hand.");
            var draft = await ExistingDraftAsync(connectionId, collection, referenceField, reference, cancellationToken);
            if (draft is null)
            {
                document["DocObjectCode"] = target.DraftObjectCode;
                using var created = await client.SendJsonAsync(connectionId, HttpMethod.Post,
                    Cx01Protocol.Root + Cx01Protocol.Drafts, document, false, cancellationToken)
                    ?? throw new InvalidOperationException("CX01 draft create returned no draft.");
                draft = Cx01ReviewKey.Text(created.RootElement, "DocEntry")
                    ?? throw new InvalidOperationException("CX01 draft create returned no draft key.");
            }
            return OutboundCreateResult.AwaitingReview(Cx01ReviewKey.Format(draft, collection, referenceField) + (qualifyId ? "|qualified" : ""),
                $"Draft {draft} waits in the ERP. Open it there, review it and add it; the customer's approval "
                + "procedure then applies. This task closes itself when the document is posted.");
        }
        catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
        {
            return OutboundCreateResult.Rejected(exception.BusinessMessage());
        }
    }

    internal async Task<string> UploadAsync(Guid connectionId, Cx01MultipartUpload upload, CancellationToken ct)
    {
        using var created = await client.SendJsonAsync(connectionId, HttpMethod.Post,
            Cx01Protocol.Root + "Attachments2", upload, false, ct)
            ?? throw new InvalidOperationException("CX01 upload returned no attachment.");
        return Cx01ReviewKey.Text(created.RootElement, "AbsoluteEntry")
            ?? throw new InvalidOperationException("CX01 upload returned no attachment key.");
    }

    private async Task<string?> ExistingDraftAsync(Guid connectionId, string collection, string field, string reference,
        CancellationToken cancellationToken)
    {
        // Releases expose the object code as either an enumeration name or its numeric string. Filter by our
        // reference remotely, then check the family on each returned header so two halves never adopt one draft.
        var target = Cx01ApprovalCatalogue.Find(collection)!;
        var objectCode = Cx01Protocol.ObjectType(new(collection, "DocEntry", ""));
        string? path = Cx01Protocol.Root + Cx01Protocol.Drafts + "?$select=DocEntry,DocObjectCode&$filter="
            + Cx01ReviewKey.FilterEquals(field, reference);
        while (path is not null)
        {
            using var response = await client.GetAsync(connectionId, path, cancellationToken);
            foreach (var row in response.RootElement.GetProperty("value").EnumerateArray())
            {
                var code = Cx01ReviewKey.Text(row, "DocObjectCode");
                if (code == target.DraftObjectCode || code == objectCode) return Cx01ReviewKey.Text(row, "DocEntry");
            }
            path = Cx01Protocol.NextPage(Cx01ReviewKey.Text(response.RootElement, "@odata.nextLink"));
        }
        return null;
    }
}

/// <summary>Reads the outcome of a draft held for review. Read-only.</summary>
internal sealed class Cx01ReviewTracker(Cx01Settings settings, Cx01Client client, Cx01OutboundDocuments documents) : IOutboundReviewTracker
{
    public async Task<OutboundReviewState> CheckAsync(OutboundReviewLookup lookup,
        CancellationToken cancellationToken)
    {
        if (lookup.PendingRemoteKey.StartsWith("pair:", StringComparison.Ordinal))
        {
            var half = await CheckAsync(lookup with { PendingRemoteKey = lookup.PendingRemoteKey[5..] }, cancellationToken);
            if (half.State is OutboundReviewStates.Rejected or OutboundReviewStates.Withdrawn)
                return await documents.PairReviewFailureAsync(lookup, half, cancellationToken);
            if (half.State != OutboundReviewStates.Posted) return half;
            await documents.RecordReviewedAsync(lookup, half.Document!, cancellationToken);
            var resumed = await documents.CreateAsync(new(lookup.ConnectionId, lookup.OutboundCommandId, lookup.CommandType,
                lookup.ErpReferenceText, lookup.Payload!, null), cancellationToken);
            return resumed.Outcome switch
            {
                OutboundConnectorOutcome.Created => OutboundReviewState.Posted(resumed.Document!),
                OutboundConnectorOutcome.AwaitingReview => new(OutboundReviewStates.Pending, Reason: resumed.Reason, PendingRemoteKey: resumed.PendingRemoteKey),
                _ => OutboundReviewState.Rejected(resumed.Reason ?? "The stock posting pair could not be completed."),
            };
        }
        var (draft, collection, field) = Cx01ReviewKey.Parse(lookup.PendingRemoteKey);
        var configured = await settings.GetAsync(lookup.ConnectionId, cancellationToken);
        var reference = configured.OwnReferencePrefix + lookup.ErpReferenceText;
        // The posted document is found by our reference, which the remote copies from the draft when it is added.
        using (var posted = await client.GetAsync(lookup.ConnectionId, Cx01Protocol.Root + collection
            + "?$select=DocEntry&$filter=" + Cx01ReviewKey.FilterEquals(field, reference) + "&$top=1", cancellationToken))
        {
            if (Cx01ReviewKey.First(posted.RootElement, "DocEntry") is { } key)
            {
                // No change marker on purpose: the reviewer may have edited the draft before adding it, so the posted
                // document must NOT be recognised as our own write; inbound capture reads it like any remote change.
                return OutboundReviewState.Posted(new(lookup.PendingRemoteKey.EndsWith("|qualified", StringComparison.Ordinal)
                    ? Cx01DocumentCatalogue.ExternalId(collection, key) : key, null));
            }
        }
        try
        {
            using var held = await client.GetAsync(lookup.ConnectionId, Cx01Protocol.Root + Cx01Protocol.Drafts
                + "(" + Cx01Protocol.KeyLiteral(draft) + ")?$select=DocEntry,AuthorizationStatus", cancellationToken);
            return Cx01ReviewKey.Text(held.RootElement, "AuthorizationStatus") switch
            {
                "dasRejected" => OutboundReviewState.Rejected(
                    $"The ERP approval procedure rejected draft {draft}."),
                "dasCancelled" => OutboundReviewState.Withdrawn($"Draft {draft} was cancelled in the ERP."),
                _ => OutboundReviewState.Pending,
            };
        }
        catch (Cx01RemoteException exception) when (exception.IsRecordGone)
        {
            return OutboundReviewState.Withdrawn(
                $"Draft {draft} was removed in the ERP and no posted document carries the reference.");
        }
    }
}

internal static class Cx01ReviewKey
{
    private static string CollectionCode(string collection) => collection switch
    {
        "DeliveryNotes" => "d", "Returns" => "r", "Invoices" => "i", "CreditNotes" => "c", "Orders" => "o",
        "PurchaseDeliveryNotes" => "p", "PurchaseReturns" => "v", "InventoryGenEntries" => "e", "InventoryGenExits" => "x",
        "StockTransfers" => "t", _ => throw new Cx01DocumentRuleException("This collection cannot be held for review."),
    };

    public static string Format(string draft, string collection, string field) => $"{draft}|{CollectionCode(collection)}|{field}";

    public static (string Draft, string Collection, string Field) Parse(string value)
    {
        var parts = value.Split('|');
        if (parts.Length >= 3) parts[1] = parts[1] switch
        {
            "d" => "DeliveryNotes", "r" => "Returns", "i" => "Invoices", "c" => "CreditNotes", "o" => "Orders",
            "p" => "PurchaseDeliveryNotes", "v" => "PurchaseReturns", "e" => "InventoryGenEntries", "x" => "InventoryGenExits",
            "t" => "StockTransfers", _ => parts[1],
        };
        if ((parts.Length != 3 && !(parts.Length == 4 && parts[3] == "qualified")) || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || Cx01ApprovalCatalogue.Find(parts[1]) is null || !Cx01FieldMap.IsValidFieldName(parts[2]))
            throw new InvalidOperationException("The CX01 review key is not valid.");
        return (parts[0], parts[1], parts[2]);
    }

    public static string FilterEquals(string field, string value) =>
        Uri.EscapeDataString(field + " eq '" + value.Replace("'", "''", StringComparison.Ordinal) + "'");

    public static string? First(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var values)
        && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
            ? Text(values[0], property) : null;

    public static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}
