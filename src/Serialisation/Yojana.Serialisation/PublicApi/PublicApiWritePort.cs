using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Portal;

namespace Yojana.Serialisation.PublicApi;

public sealed class PublicApiWritePort(
    SerialisationDbContext db,
    PortalOwnershipScope ownership,
    PortalWriteService portalWrites,
    ITenantCurrency currency,
    ITenantContext tenant,
    IMessageBus bus) : IPublicApiWritePort
{
    public async Task<PublicRmaResult?> RequestRmaAsync(Guid commandId, PublicRmaRequest request,
        Guid? boundCustomerPartyId, string actorRef, DateTime eventTime, CancellationToken cancellationToken)
    {
        var party = await PartyAsync(request.UnitId, request.CustomerPartyId, boundCustomerPartyId,
            cancellationToken);
        if (party is null) return null;
        var result = await portalWrites.RequestRmaAsync(new PortalPrincipal(tenant.TenantId, party.Value),
            request.UnitId, new PortalRmaRequest(commandId, request.RequestDate, request.ServiceCountry,
                request.CoverageFamily, request.ClaimLineKind, request.Description), actorRef, cancellationToken);
        return result is null ? null : new(result.RmaId, result.RmaNumber, result.Stage);
    }

    public async Task<PublicClaimResult?> SubmitClaimAsync(Guid commandId, PublicClaimRequest request,
        Guid? boundCustomerPartyId, string actorRef, DateTime eventTime, CancellationToken cancellationToken)
    {
        var party = await PartyAsync(request.UnitId, request.CustomerPartyId, boundCustomerPartyId,
            cancellationToken);
        if (party is null) return null;
        ValidateAttachments(request.Evidence.Select(row => row.Attachment));
        var cover = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == request.UnitId && row.OwnerPartyRef == party && row.Status != EntitlementStatus.Closed)
            .OrderBy(row => row.CoverageFamily == CoverageFamilyCode.Base ? 0 : 1)
            .ThenBy(row => row.EntitlementId)
            .Select(row => new { row.ObligorEntityId }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("claim_cover_unavailable",
                "The unit has no live customer cover for claim intake.");
        var homeCurrency = await currency.GetAsync(cancellationToken)
            ?? throw new CommandRefusedException("tenant_currency_required",
                "The company's home currency must be configured before claim intake.");
        var result = await bus.InvokeAsync<SubmitClaimResult>(new SubmitClaim(commandId, request.UnitId, null,
                request.ClaimDate, party.Value, party.Value, cover.ObligorEntityId, cover.ObligorEntityId,
                request.ServiceCountry, homeCurrency,
                request.Lines.Select(row => new ClaimLineInput(row.Kind, row.CoverageFamily,
                    row.RequestedAmount, row.UnitId, row.Description, row.Measurements)).ToList(),
                request.Evidence.Select(row => new ClaimEvidenceInput(row.Kind, row.Reference,
                    row.Description)).ToList(), request.Remedy, request.ConsumerChoiceEvidenceRef, eventTime,
                actorRef, ReportedCauseCode: request.ReportedCauseCode,
                ReportedDescription: request.ReportedDescription, OccurredDate: request.OccurredDate),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);

        foreach (var supplied in request.Evidence.Where(row => row.Attachment is not null))
        {
            var stored = await db.ClaimEvidence.Where(row => row.TenantId == tenant.TenantId
                    && row.ClaimId == result.ClaimId && row.Kind == supplied.Kind
                    && row.Reference == supplied.Reference)
                .OrderBy(row => row.RecordedAt).ThenBy(row => row.EvidenceId)
                .FirstAsync(cancellationToken);
            stored.AttachmentFileName = Clean(supplied.Attachment!.FileName, 260);
            stored.AttachmentContentType = Clean(supplied.Attachment.ContentType, 100);
            stored.AttachmentContent = supplied.Attachment.Content;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(result.ClaimId, result.UnitId, result.Status, result.ClaimLineIds);
    }

    public async Task<PublicInstallationDateResult?> SetInstallationDateAsync(Guid commandId, Guid unitId,
        PublicInstallationDateRequest request, Guid? boundCustomerPartyId, string actorRef,
        CancellationToken cancellationToken)
    {
        var party = await PartyAsync(unitId, request.CustomerPartyId, boundCustomerPartyId, cancellationToken);
        if (party is null) return null;
        ValidateAttachments([request.Attachment]);
        var attachment = request.Attachment is null ? null : new PortalEvidenceAttachment(
            request.Attachment.FileName, request.Attachment.ContentType, request.Attachment.Content);
        var result = await portalWrites.RegisterAsync(new PortalPrincipal(tenant.TenantId, party.Value), unitId,
            new PortalRegistrationRequest(commandId, PortalRegistrationEvidenceKind.Installation,
                request.EvidenceAt, request.EvidenceReference, attachment), actorRef, cancellationToken);
        return result is null ? null : new(result.RegistrationId, result.UnitId, result.Outcome, result.Status,
            result.StartDate);
    }

    public async Task<PublicAssetTagResult?> AddAssetTagAsync(Guid commandId, Guid unitId,
        PublicAssetTagRequest request, Guid? boundCustomerPartyId, string actorRef, DateTime eventTime,
        CancellationToken cancellationToken)
    {
        if (await PartyAsync(unitId, request.CustomerPartyId, boundCustomerPartyId, cancellationToken) is null)
            return null;
        var legalEntityId = await db.Events.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId).OrderByDescending(row => row.Seq)
            .Select(row => row.LegalEntityId).FirstOrDefaultAsync(cancellationToken);
        if (legalEntityId == Guid.Empty)
            throw new CommandRefusedException("unit_legal_entity_unknown",
                "The unit has no legal entity for an asset-tag event.");
        var result = await bus.InvokeAsync<AssignIdentifierResult>(new AssignIdentifier(commandId, unitId,
                new IdentifierInput(IdentifierType.CustomerAssetTag, request.AssetTag), eventTime, actorRef,
                legalEntityId), new DeliveryOptions { TenantId = tenant.TenantId.ToString() },
            cancellationToken);
        return new(result.UnitId, result.IdentifierId, result.NormalisedValue, result.LifecycleStatus);
    }

    private async Task<Guid?> PartyAsync(Guid unitId, Guid? requested, Guid? bound,
        CancellationToken cancellationToken)
    {
        if (bound is { } boundParty && requested is { } requestedParty && boundParty != requestedParty)
            return null;
        var party = bound ?? requested;
        if (party is null)
            throw new CommandRefusedException("customer_party_required",
                "An unbound back-office client must identify the customer party.");
        return await ownership.CanReadAsync(new PortalPrincipal(tenant.TenantId, party.Value), unitId,
            cancellationToken) ? party : null;
    }

    private static void ValidateAttachments(IEnumerable<PublicEvidenceAttachment?> attachments)
    {
        foreach (var attachment in attachments.OfType<PublicEvidenceAttachment>())
        {
            if (attachment.Content.Length is 0 or > PortalWriteService.MaximumAttachmentBytes)
                throw new CommandRefusedException("evidence_attachment_size",
                    "An evidence attachment must be between 1 byte and 10 MB.");
            if (!AllowedAttachmentTypes.Contains(attachment.ContentType))
                throw new CommandRefusedException("evidence_attachment_type",
                    "Evidence must be PDF, PNG, JPEG or plain text.");
            if (string.IsNullOrWhiteSpace(attachment.FileName))
                throw new CommandRefusedException("evidence_attachment_name", "Evidence needs a file name.");
        }
    }

    private static string Clean(string value, int maximum)
    {
        var clean = value.Trim();
        if (clean.Length > maximum)
            throw new CommandRefusedException("evidence_attachment_text_too_long",
                $"Evidence attachment text may contain at most {maximum} characters.");
        return clean;
    }

    private static readonly HashSet<string> AllowedAttachmentTypes = new(
        ["application/pdf", "image/png", "image/jpeg", "text/plain"], StringComparer.OrdinalIgnoreCase);
}
