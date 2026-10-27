using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

/// <summary>The one audit-and-render contract used by selection reprints and a viewer's single-copy action.</summary>
public sealed class CertificateCopyService(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ICertificateUnitReadPort units,
    ICertificateRenderer renderer,
    IUnitEventWriter events)
{
    public static string ReprintReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new CommandRefusedException("reprint_reason_required", "One reprint reason is required for the run.");
        return reason.Trim();
    }

    public async Task<bool> RecordAsync(CertificateInstance certificate, Guid reprintId, string actorRef,
        string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorRef))
            throw new CommandRefusedException("actor_required", "The authenticated actor is required.");
        reason = ReprintReason(reason);
        if (await db.ReprintLogs.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                && row.ReprintId == reprintId, cancellationToken))
            return false;

        var at = clock.UtcNow;
        db.ReprintLogs.Add(new ReprintLog
        {
            TenantId = tenant.TenantId,
            ReprintId = reprintId,
            CertificateId = certificate.CertificateId,
            Version = certificate.Version,
            RequestedBy = actorRef,
            At = at,
        });
        await events.AppendAsync([new UnitEventDraft
        {
            UnitId = certificate.UnitId,
            EventTime = at,
            EventType = CertificateEventType.Reprinted,
            LegalEntityId = certificate.IssuerEntityId,
            ActorRef = actorRef,
            IdempotencyKey = $"cert:reprint:{reprintId:N}",
            Attributes = JsonObjectText.Of(new
            {
                reprint_id = reprintId,
                certificate_id = certificate.CertificateId,
                certificate_version = certificate.Version,
                tracking_number = certificate.TrackingNumber,
                reason,
                copy = true,
                reprinted_at = at,
            }),
            ProjectCurrentState = false,
        }], cancellationToken);
        return true;
    }

    public async Task<CertificateCopyFile> RenderAsync(CertificateInstance certificate, string serial,
        CertificateRenderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentTrackingNumberAsync(certificate, cancellationToken);
        var metadata = current is null || request.Metadata is null ? request.Metadata
            : request.Metadata with { SupersededByTrackingNumber = current };
        var rendered = await renderer.RenderAsync(request with { Copy = true, Metadata = metadata }, cancellationToken);
        var extension = rendered.MediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase)
            ? "pdf" : "html";
        return new(rendered.Bytes, rendered.MediaType,
            $"{Safe(serial)}-{certificate.TrackingNumber}-v{certificate.Version}-copy.{extension}");
    }

    public async Task<CertificateCopyFile> PrintAsync(PrintCertificateCopy command,
        CancellationToken cancellationToken)
    {
        if (command.CommandId == Guid.Empty)
            throw new CommandRefusedException("command_id_required", "A command id is required.");
        if (command.CertificateId == Guid.Empty)
            throw new CommandRefusedException("certificate_id_required", "A certificate id is required.");
        var reason = ReprintReason(command.Reason);
        var certificate = await db.CertificateInstances.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.CertificateId == command.CertificateId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_unknown",
                $"Certificate {command.CertificateId} does not exist.");
        var reprintId = Deterministic(command.CommandId, certificate.UnitId);
        await RecordAsync(certificate, reprintId, command.ActorRef, reason, cancellationToken);

        var signature = await db.CertificateSignatures.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.CertificateId == certificate.CertificateId)
            .OrderByDescending(row => row.SignedAt).FirstOrDefaultAsync(cancellationToken);
        var supersedes = certificate.SupersedesCertificateId is not { } predecessorId ? null
            : await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && row.CertificateId == predecessorId).Select(row => row.TrackingNumber)
                .SingleOrDefaultAsync(cancellationToken);
        var serials = await units.PrimarySerialsAsync([certificate.UnitId], cancellationToken);
        var serial = serials.GetValueOrDefault(certificate.UnitId) ?? "unlabelled-unit";
        return await RenderAsync(certificate, serial,
            CertificateQueries.RenderRequest(certificate, signature, supersedes), cancellationToken);
    }

    /// <summary>For a superseded version, the tracking number of the version that is current now (following the
    /// supersede chain forward); null for a version that is itself current.</summary>
    private async Task<string?> CurrentTrackingNumberAsync(CertificateInstance certificate,
        CancellationToken cancellationToken)
    {
        if (certificate.Status != CertificateStatus.Superseded) return null;
        var currentId = certificate.CertificateId;
        string? tracking = null;
        var seen = new HashSet<Guid> { currentId };
        while (await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                       && row.SupersedesCertificateId == currentId)
                   .Select(row => new { row.CertificateId, row.TrackingNumber })
                   .FirstOrDefaultAsync(cancellationToken) is { } next && seen.Add(next.CertificateId))
        {
            currentId = next.CertificateId;
            tracking = next.TrackingNumber;
        }
        return tracking;
    }

    public static Guid Deterministic(Guid commandId, Guid unitId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{commandId:N}|{unitId:N}|certificate-selection|reprint")).AsSpan(0, 16));

    private static string Safe(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
}

public sealed record CertificateCopyFile(byte[] Bytes, string MediaType, string FileName)
{
    public bool HtmlFallback => MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}

public static class PrintCertificateCopyHandler
{
    public static Task<CertificateCopyFile> Handle(PrintCertificateCopy command, CertificateCopyService copies,
        CancellationToken cancellationToken) => copies.PrintAsync(command, cancellationToken);
}
