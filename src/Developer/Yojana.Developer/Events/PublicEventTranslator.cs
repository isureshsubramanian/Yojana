using System.Text.Json;
using Yojana.Developer.Contracts;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Events;

public sealed record PublicEventTranslation(PublicEventEnvelope? Envelope, string? ItemFamily,
    Guid? CustomerPartyRef, Guid? LegalEntityId, Guid? BulkOperationId = null,
    string? BulkOperationKind = null, int? BulkOperationSize = null,
    IReadOnlyList<Guid>? BulkUnitIds = null);

/// <summary>
/// COUPLING with Serialisation.Contracts published-event records: their internal shapes map into the stable public
/// catalogue here. The boundary is necessary because customers must not observe internal renames. A breaking internal
/// change without a matching translator change fails the public contract tests and would otherwise break customers.
/// </summary>
public sealed class PublicEventTranslator(IPublicEventContextPort context, ITenantContext tenant,
    DeveloperWebhookSettings settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PublicEventTranslation?> TranslateAsync(object message,
        CancellationToken cancellationToken)
    {
        if (message is BulkOperationCompleted bulk)
        {
            if (bulk.UnitCount <= await settings.BulkThresholdAsync(cancellationToken)) return null;
            var envelope = Summary(bulk.Header, PublicEventTypes.BulkCompleted, "operation", bulk.OperationId,
                bulk.OperationId.ToString("N"), 1, new()
                {
                    ["operationId"] = bulk.OperationId, ["kind"] = bulk.Kind,
                    ["unitCount"] = bulk.UnitCount,
                }, $"/api/public/v1/operations/{bulk.OperationId:D}/units");
            return new(envelope, null, null, bulk.Header.LegalEntityId, bulk.OperationId, bulk.Kind,
                bulk.UnitCount, bulk.UnitIds);
        }

        if (message is ImportCompleted import
            && import.Applied > await settings.BulkThresholdAsync(cancellationToken))
        {
            var ids = await context.OperationUnitsAsync("import", import.JobId, cancellationToken);
            var envelope = Summary(import.Header, PublicEventTypes.BulkCompleted, "operation", import.JobId,
                import.JobId.ToString("N"), 1, new()
                {
                    ["operationId"] = import.JobId, ["kind"] = "import",
                    ["unitCount"] = import.Applied,
                }, $"/api/public/v1/operations/{import.JobId:D}/units");
            return new(envelope, null, null, import.Header.LegalEntityId, import.JobId, "import",
                import.Applied, ids);
        }

        if (message is IPublishedUnitEvent unitEvent)
        {
            var header = unitEvent.Header;
            var unitContext = await context.ForUnitAsync(header.UnitId, cancellationToken)
                ?? new PublicEventContext(null, null, null);
            if (header.OperationSize is { } operationSize
                && operationSize > await settings.BulkThresholdAsync(cancellationToken))
                return new(null, unitContext.ItemFamily, unitContext.CurrentOwnerPartyRef,
                    header.LegalEntityId, header.OperationId, header.OperationKind, operationSize,
                    [header.UnitId]);
            var envelope = await UnitAsync(message, header, unitContext, cancellationToken);
            return envelope is null ? null : new(envelope, unitContext.ItemFamily,
                unitContext.CurrentOwnerPartyRef, header.LegalEntityId);
        }

        var summary = await SummaryAsync(message, cancellationToken);
        return summary is null ? null : new(summary.Value.Envelope, null, summary.Value.Customer,
            summary.Value.LegalEntity);
    }

    private async Task<PublicEventEnvelope?> UnitAsync(object message, PublishedEventHeader h,
        PublicEventContext unit, CancellationToken ct)
    {
        var serial = unit.Serial;
        return message switch
        {
            UnitCommissioned x => Unit(h, PublicEventTypes.UnitCreated, "unit", h.UnitId, serial, h.Seq,
                Data(("itemId", x.ItemId), ("origin", x.Origin), ("status", x.LifecycleStatus))),
            CustodyChanged x when x.TriggerEventType == UnitEventType.Shipped => Unit(h,
                PublicEventTypes.UnitShipped, "unit", h.UnitId, serial, h.Seq,
                Data(("toCustomer", x.ToPartyRef), ("toLocation", x.ToLocationId),
                    ("reason", x.Reason), ("document", Document(h.Document)))),
            CustodyChanged x => Unit(h, PublicEventTypes.UnitCustodyChanged, "unit", h.UnitId, serial,
                h.Seq, Data(("episodeId", x.EpisodeId), ("toLocation", x.ToLocationId),
                    ("toCustomer", x.ToPartyRef), ("reason", x.Reason))),
            OwnershipOpened x => Unit(h, PublicEventTypes.UnitOwnershipChanged, "unit", h.UnitId,
                serial, h.Seq, Data(("episodeId", x.EpisodeId), ("customer", x.PartyRef),
                    ("reason", x.Reason))),
            UnitIdentifierCorrected x => Unit(h, PublicEventTypes.UnitIdentifierCorrected, "unit",
                h.UnitId, serial, h.Seq, Data(("identifierType", x.IdentifierType),
                    ("before", x.BeforeValue), ("after", x.AfterValue), ("reason", x.Reason))),
            UnitVoided x => Unit(h, PublicEventTypes.UnitVoided, "unit", h.UnitId, serial, h.Seq,
                Data(("reason", x.Correction.ReasonCode), ("closedIdentifierCount", x.ClosedIdentifierIds.Count))),
            EntitlementCreated x => Unit(h, PublicEventTypes.CoverCreated, "cover", x.EntitlementId,
                serial, h.Seq, Data(("unitId", h.UnitId), ("coverageFamily", x.CoverageFamily),
                    ("status", x.Status), ("startDate", x.StartDate), ("endDate", x.EndDate))),
            EntitlementAmended x => Unit(h, PublicEventTypes.CoverAmended, "cover", x.EntitlementId,
                serial, h.Seq, Data(("unitId", h.UnitId), ("reason", x.ReasonCode),
                    ("changedFields", x.Changes.Select(c => c.Field).ToList()))),
            EntitlementTransferred x => Unit(h, PublicEventTypes.CoverTransferred, "cover",
                x.TargetEntitlementId, serial, h.Seq, Data(("sourceCoverId", x.SourceEntitlementId),
                    ("sourceUnitId", x.SourceUnitId), ("targetUnitId", x.TargetUnitId),
                    ("status", "transferred"))),
            EntitlementClosed x => Unit(h, PublicEventTypes.CoverClosed, "cover", x.EntitlementId,
                serial, h.Seq, Data(("unitId", h.UnitId), ("reason", x.ClosedReason),
                    ("status", "closed"))),
            ClaimSubmitted x => Unit(h, PublicEventTypes.ClaimSubmitted, "claim", x.ClaimId,
                x.ClaimNumber, 1, Data(("unitId", h.UnitId), ("status", x.Status),
                    ("remedy", x.Remedy), ("lineIds", x.ClaimLineIds))),
            ClaimAdjudicated x => Unit(h, PublicEventTypes.ClaimAdjudicated, "claim", x.ClaimId,
                await ReferenceAsync("claim", x.ClaimId, ct), 2,
                Data(("unitId", h.UnitId), ("status", "adjudicated"),
                    ("coverageSource", x.CoverageSource),
                    ("lineIds", x.ApprovedAmountsByLine.Select(line => line.ClaimLineId).ToList()))),
            RecoveryClaimRaised x => Unit(h, PublicEventTypes.RecoveryClaimRaised, "recovery_claim",
                x.RecoveryClaimId, null, 1, Data(("claimId", x.CustomerClaimId),
                    ("claimLineId", x.ClaimLineId), ("status", "raised"))),
            RecoveryClaimApproved x => Unit(h, PublicEventTypes.RecoveryClaimAnswered, "recovery_claim",
                x.RecoveryClaimId, null, 2, Data(("status", "approved"))),
            RmaAuthorised x => Unit(h, PublicEventTypes.RmaStageChanged, "rma", x.RmaId,
                x.RmaNumber, 1, Data(("stage", "authorised"), ("unitId", h.UnitId),
                    ("lineId", x.RmaLineId))),
            RmaReceived x => Unit(h, PublicEventTypes.RmaStageChanged, "rma", x.RmaId,
                await ReferenceAsync("rma", x.RmaId, ct), 2,
                Data(("stage", "received"), ("unitId", h.UnitId), ("lineId", x.RmaLineId),
                    ("outcome", x.Outcome))),
            RmaDispositioned x => Unit(h, PublicEventTypes.RmaStageChanged, "rma", x.RmaId,
                await ReferenceAsync("rma", x.RmaId, ct), 3,
                Data(("stage", "dispositioned"), ("unitId", h.UnitId),
                    ("lineId", x.RmaLineId), ("disposition", x.Disposition))),
            ReplacementShipped x => Unit(h, PublicEventTypes.ReplacementShipped, "replacement",
                x.WarrantyReplacementId, x.ReplacementNumber, h.Seq,
                Data(("originalUnitId", x.OriginalUnitId), ("replacementUnitId", x.ReplacementUnitId),
                    ("status", "shipped"))),
            ReplacementReturned x => Unit(h, PublicEventTypes.ReplacementReturned, "replacement",
                x.WarrantyReplacementId,
                x.ReplacementNumber, h.Seq, Data(("originalUnitId", x.OriginalUnitId),
                    ("replacementUnitId", x.ReplacementUnitId), ("rmaId", x.RmaId),
                    ("status", x.Status))),
            RefurbishmentReleased x => Unit(h, PublicEventTypes.RefurbishmentReleased, "refurbishment",
                x.WorkOrderId, x.WorkOrderNumber, h.Seq, Data(("unitId", x.ReleasedUnitId),
                    ("grade", x.Grade), ("status", "released"))),
            CertificateIssued x => Unit(h, PublicEventTypes.CertificateIssued, "certificate",
                x.CertificateId, x.TrackingNumber, x.CertificateVersion,
                Data(("unitId", h.UnitId), ("documentKind", x.DocumentKind),
                    ("certificateVersion", x.CertificateVersion))),
            CertificateSuperseded x => Unit(h, PublicEventTypes.CertificateSuperseded, "certificate",
                x.CertificateId, await ReferenceAsync("certificate", x.CertificateId, ct),
                x.CertificateVersion, Data(("unitId", h.UnitId),
                    ("supersedingCertificateId", x.SupersedingCertificateId), ("reason", x.Reason))),
            _ => null,
        };
    }

    private async Task<(PublicEventEnvelope Envelope, Guid? Customer, Guid LegalEntity)?> SummaryAsync(
        object message, CancellationToken ct)
    {
        return message switch
        {
            ImportCompleted x => (Summary(x.Header, PublicEventTypes.ImportCompleted, "import", x.JobId,
                    await ReferenceAsync("import", x.JobId, ct), 1,
                    Data(("source", x.Source), ("status", x.Status), ("applied", x.Applied),
                        ("skipped", x.Skipped), ("rejected", x.Rejected)),
                    $"/api/public/v1/imports/{x.JobId:D}"), null, x.Header.LegalEntityId),
            CampaignStarted x => (Summary(x.Header, PublicEventTypes.CampaignStarted, "campaign",
                    x.CampaignId, x.CampaignCode, 1,
                    Data(("status", "started"), ("unitCount", x.UnitCount)),
                    $"/api/public/v1/campaigns/{x.CampaignId:D}"), null, x.Header.LegalEntityId),
            CampaignEnded x => (Summary(x.Header, PublicEventTypes.CampaignEnded, "campaign",
                    x.CampaignId, x.CampaignCode, 2,
                    Data(("status", "ended"), ("unitCount", x.UnitCount)),
                    $"/api/public/v1/campaigns/{x.CampaignId:D}"), null, x.Header.LegalEntityId),
            _ => null,
        };
    }

    private Task<string?> ReferenceAsync(string kind, Guid id, CancellationToken ct) =>
        context.ReferenceAsync(kind, id, ct);

    private PublicEventEnvelope Unit(PublishedEventHeader h, string type, string kind, Guid id,
        string? reference, long sequence, Dictionary<string, object?> data) => new(h.EventId, type,
        PublicEventTypes.CurrentVersion, TenantCode(), h.EventTime, h.RecordedTime,
        new(kind, id, reference), sequence, data,
        new Dictionary<string, string> { ["self"] = $"/api/public/v1/{Plural(kind)}/{id:D}" });

    private PublicEventEnvelope Summary(PublishedSummaryHeader h, string type, string kind, Guid id,
        string? reference, long sequence, Dictionary<string, object?> data, string self) => new(h.EventId,
        type, PublicEventTypes.CurrentVersion, TenantCode(), h.EventTime, h.RecordedTime,
        new(kind, id, reference), sequence, data, new Dictionary<string, string> { ["self"] = self });

    private string TenantCode() => "t_" + tenant.TenantId.Value.ToString("N");

    private static string Plural(string kind) => kind switch
    {
        "rma" => "rmas", "recovery_claim" => "recovery-claims", "cover" => "cover",
        _ => kind.Replace('_', '-') + "s",
    };

    private static Dictionary<string, object?>? Document(DocumentRef? document) => document is null ? null
        : new Dictionary<string, object?> { ["kind"] = document.Type, ["id"] = document.Id };

    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
}
