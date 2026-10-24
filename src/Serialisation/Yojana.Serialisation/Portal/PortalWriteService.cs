using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Portal;

public sealed class PortalRegistrationSettings(ITenantSettings settings)
{
    public const string Key = "portal.registration";
    public const int DefaultGraceDays = 60;

    public async Task<PortalRegistrationSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        if (stored is null) return new(DefaultGraceDays, ITenantSettings.MissingVersion);
        var value = JsonSerializer.Deserialize<RegistrationSetting>(stored.Value)
            ?? throw new InvalidOperationException("The portal registration setting is empty.");
        return new(value.GraceDays, stored.Version);
    }

    public async Task<PortalRegistrationSettingsView> SetAsync(UpdatePortalRegistrationSettings update,
        CancellationToken cancellationToken)
    {
        if (update.GraceDays is < 0 or > 3650)
            throw new CommandRefusedException("registration_grace_invalid",
                "The registration grace period must be between 0 and 3,650 days.");
        var stored = await settings.SetAsync(Key, JsonSerializer.Serialize(new RegistrationSetting(update.GraceDays)),
            update.ExpectedVersion, cancellationToken);
        return new(update.GraceDays, stored.Version);
    }

    private sealed record RegistrationSetting(int GraceDays);
}

/// <summary>
/// Portal write orchestration. The browser supplies neither party nor actor; ownership is checked once through the
/// same scope as portal reads, then the existing entitlement and RMA commands remain the only mutation seams.
/// </summary>
public sealed class PortalWriteService(
    SerialisationDbContext db,
    PortalOwnershipScope ownership,
    PortalRegistrationSettings registrationSettings,
    ITenantCurrency currency,
    ITenantContext tenant,
    IClock clock,
    IMessageBus bus)
{
    public const int MaximumAttachmentBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AttachmentTypes = new(
        ["application/pdf", "image/png", "image/jpeg", "text/plain"], StringComparer.OrdinalIgnoreCase);

    public async Task<PortalRegistrationResult?> RegisterAsync(PortalPrincipal principal, Guid unitId,
        PortalRegistrationRequest request, string actorRef, CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        if (!await ownership.CanReadAsync(principal, unitId, cancellationToken)) return null;
        if (await db.PortalRegistrations.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.CommandId == request.CommandId, cancellationToken) is { } replay)
            return await ResultAsync(replay, true, cancellationToken);

        if (!PortalRegistrationEvidenceKind.All.Contains(request.EvidenceKind))
            throw new CommandRefusedException("registration_evidence_kind_unknown",
                "Registration evidence must be a delivery date or an installation date.");
        if (request.EvidenceAt > clock.UtcNow.AddMinutes(5))
            throw new CommandRefusedException("registration_evidence_future",
                "Registration evidence cannot be dated in the future.");
        ValidateAttachment(request.Attachment);

        var tenantId = tenant.TenantId;
        var episode = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == unitId && row.Kind == EpisodeKind.Ownership
                && row.PartyRef == principal.PartyRef && row.ValidFrom <= clock.UtcNow
                && (row.ValidTo == null || row.ValidTo > clock.UtcNow))
            .OrderByDescending(row => row.ValidFrom)
            .Select(row => new { row.ValidFrom })
            .FirstOrDefaultAsync(cancellationToken);
        if (episode is null) return null;
        if (request.EvidenceAt < episode.ValidFrom)
            throw new CommandRefusedException("registration_before_delivery",
                "The evidence date cannot be before this owner's delivery date.");

        var entitlement = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == unitId && row.Direction == EntitlementDirection.Outbound
                && row.Status != EntitlementStatus.Closed)
            .OrderBy(row => row.CoverageFamily == CoverageFamilyCode.Base ? 0 : 1)
            .ThenBy(row => row.EntitlementId)
            .Select(row => new { row.EntitlementId, row.StartDate })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("registration_entitlement_missing",
                "This unit has no live customer entitlement to register.");

        var setting = await registrationSettings.GetAsync(cancellationToken);
        var graceEnd = episode.ValidFrom.AddDays(setting.GraceDays);
        var status = request.EvidenceKind == PortalRegistrationEvidenceKind.Delivery
            || request.EvidenceAt <= graceEnd
                ? PortalRegistrationStatus.Recorded
                : PortalRegistrationStatus.PendingReview;
        var row = new PortalRegistration
        {
            TenantId = tenantId,
            RegistrationId = Guid.CreateVersion7(),
            CommandId = request.CommandId,
            UnitId = unitId,
            EntitlementId = entitlement.EntitlementId,
            PartyRef = principal.PartyRef,
            EvidenceKind = request.EvidenceKind,
            DeliveryAt = episode.ValidFrom,
            EvidenceAt = request.EvidenceAt,
            EvidenceReference = Clean(request.EvidenceReference, 500),
            AttachmentFileName = Clean(request.Attachment?.FileName, 260),
            AttachmentContentType = Clean(request.Attachment?.ContentType, 100),
            AttachmentContent = request.Attachment?.Content,
            GraceDays = setting.GraceDays,
            Status = status,
            SubmittedAt = clock.UtcNow,
            SubmittedBy = actorRef,
        };
        db.PortalRegistrations.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return new(row.RegistrationId, row.UnitId, row.EntitlementId,
            status == PortalRegistrationStatus.Recorded ? PortalRegistrationOutcome.RegistrationRecorded
                : PortalRegistrationOutcome.ReviewPending,
            status, row.DeliveryAt, row.EvidenceAt, row.GraceDays, entitlement.StartDate);
    }

    public async Task<PortalRmaResult?> RequestRmaAsync(PortalPrincipal principal, Guid unitId,
        PortalRmaRequest request, string actorRef, CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        if (!await ownership.CanReadAsync(principal, unitId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description))
            throw new CommandRefusedException("rma_description_required", "Describe the fault before starting the request.");
        if (request.ServiceCountry.Trim().Length != 2)
            throw new CommandRefusedException("rma_service_country_invalid", "Choose a two-letter service country.");

        var entitlement = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId && row.Direction == EntitlementDirection.Outbound
                && row.Status != EntitlementStatus.Closed
                && row.CoverageFamily == request.CoverageFamily)
            .Select(row => new { row.ObligorEntityId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("rma_coverage_family_unavailable",
                "The selected coverage family is not live for this unit.");
        var tenantCurrency = await currency.GetAsync(cancellationToken)
            ?? throw new CommandRefusedException("tenant_currency_required",
                "The company's home currency must be configured before portal return intake.");
        var result = await bus.InvokeAsync<RequestRmaResult>(new RequestRma(
                request.CommandId,
                request.RequestDate,
                principal.PartyRef,
                principal.PartyRef,
                entitlement.ObligorEntityId,
                entitlement.ObligorEntityId,
                request.ServiceCountry.Trim().ToUpperInvariant(),
                tenantCurrency,
                [new RmaRequestLineInput(UnitId: unitId, CoverageFamily: request.CoverageFamily,
                    ClaimLineKind: request.ClaimLineKind, Description: request.Description.Trim())],
                clock.UtcNow,
                actorRef),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
        return new(result.RmaId, result.RmaNumber, result.Stage, result.Replayed);
    }

    public async Task<PortalRegistrationReviewPage> ReviewsAsync(string? status, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var wanted = string.IsNullOrWhiteSpace(status) ? PortalRegistrationStatus.PendingReview : status;
        if (wanted is not (PortalRegistrationStatus.Recorded or PortalRegistrationStatus.PendingReview or PortalRegistrationStatus.Approved))
            throw new CommandRefusedException("registration_status_unknown", "The registration review status is unknown.");
        var query = db.PortalRegistrations.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Status == wanted);
        if (DecodeCursor(cursor) is { } after)
            query = query.Where(row => row.SubmittedAt > after.At
                || row.SubmittedAt == after.At && row.RegistrationId.CompareTo(after.Id) > 0);
        var rows = await query.OrderBy(row => row.SubmittedAt).ThenBy(row => row.RegistrationId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows.Select(ReviewRow).ToList(), hasMore && rows.Count > 0 ? EncodeCursor(rows[^1]) : null);
    }

    public async Task<PortalRegistrationResult> ApproveAsync(Guid registrationId,
        ApprovePortalRegistrationRequest request, string actorRef, CancellationToken cancellationToken)
    {
        var row = await db.PortalRegistrations.SingleOrDefaultAsync(item => item.TenantId == tenant.TenantId
                && item.RegistrationId == registrationId, cancellationToken)
            ?? throw new CommandRefusedException("portal_registration_unknown", "The registration review item was not found.");
        if (row.ApprovalCommandId == request.CommandId)
            return await ResultAsync(row, true, cancellationToken);
        if (row.Status != PortalRegistrationStatus.PendingReview)
            throw new CommandRefusedException("portal_registration_not_pending",
                "Only a pending registration can be approved.");

        var evidenceRef = row.EvidenceReference;
        if (!string.IsNullOrWhiteSpace(row.AttachmentFileName))
            evidenceRef = string.IsNullOrWhiteSpace(evidenceRef) ? row.AttachmentFileName
                : $"{evidenceRef}; {row.AttachmentFileName}";
        var changed = await bus.InvokeAsync<EntitlementChangeResult>(new RebaseEntitlementStart(
                request.CommandId, row.EntitlementId,
                row.EvidenceKind == PortalRegistrationEvidenceKind.Installation ? StartTrigger.Install : StartTrigger.Deliver,
                row.EvidenceAt, evidenceRef, request.EventTime, actorRef),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
        row.Status = PortalRegistrationStatus.Approved;
        row.ApprovalCommandId = request.CommandId;
        row.ApprovedAt = clock.UtcNow;
        row.ApprovedBy = actorRef;
        row.RebaseEventId = changed.EventId;
        await db.SaveChangesAsync(cancellationToken);
        return new(row.RegistrationId, row.UnitId, row.EntitlementId,
            PortalRegistrationOutcome.EntitlementRebased, row.Status, row.DeliveryAt, row.EvidenceAt,
            row.GraceDays, changed.StartDate, changed.Replayed);
    }

    private async Task<PortalRegistrationResult> ResultAsync(PortalRegistration row, bool replayed,
        CancellationToken cancellationToken)
    {
        var start = await db.Entitlements.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.EntitlementId == row.EntitlementId)
            .Select(item => item.StartDate).SingleAsync(cancellationToken);
        var outcome = row.Status == PortalRegistrationStatus.Recorded
            ? PortalRegistrationOutcome.RegistrationRecorded
            : row.Status == PortalRegistrationStatus.PendingReview
                ? PortalRegistrationOutcome.ReviewPending
                : PortalRegistrationOutcome.EntitlementRebased;
        return new(row.RegistrationId, row.UnitId, row.EntitlementId, outcome, row.Status,
            row.DeliveryAt, row.EvidenceAt, row.GraceDays, start, replayed);
    }

    private static PortalRegistrationReviewRow ReviewRow(PortalRegistration row) => new(
        row.RegistrationId, row.UnitId, row.EntitlementId, row.EvidenceKind, row.DeliveryAt,
        row.EvidenceAt, row.EvidenceReference, row.AttachmentFileName, row.Status, row.SubmittedAt,
        row.SubmittedBy);

    private static string EncodeCursor(PortalRegistration row) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        row.SubmittedAt.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + row.RegistrationId.ToString("N")));

    private static (DateTime At, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                && Guid.TryParseExact(parts[1], "N", out var id) ? (new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException) { return null; }
    }

    private static void ValidateAttachment(PortalEvidenceAttachment? attachment)
    {
        if (attachment is null) return;
        if (attachment.Content.Length == 0 || attachment.Content.Length > MaximumAttachmentBytes)
            throw new CommandRefusedException("registration_attachment_size",
                "A registration attachment must be between 1 byte and 10 MB.");
        if (!AttachmentTypes.Contains(attachment.ContentType))
            throw new CommandRefusedException("registration_attachment_type",
                "Registration evidence must be PDF, PNG, JPEG or plain text.");
        if (string.IsNullOrWhiteSpace(attachment.FileName))
            throw new CommandRefusedException("registration_attachment_name", "The attachment needs a file name.");
    }

    private static string? Clean(string? value, int maximum)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (clean.Length > maximum) throw new CommandRefusedException("registration_evidence_too_long",
            $"Registration evidence text may contain at most {maximum} characters.");
        return clean;
    }
}
