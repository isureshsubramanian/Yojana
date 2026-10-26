using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Certificates;

/// <summary>Previews and submits one manual correction through the certificate versioning engine.</summary>
public sealed class CertificateCorrectionService(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ICertificateUnitReadPort units,
    CertificatePayloadBuilder payloads,
    CertificateTemplateService templates,
    CertificateVersioningEngine versions)
{
    public const string ManualCorrectionTrigger = "Certificate.ManualCorrectionRequested";
    private const string NothingToCorrectDetail = "Nothing to correct: the record matches today's data.";

    public async Task<CertificateCorrectionPreview> PreviewAsync(Guid certificateId,
        CancellationToken cancellationToken)
    {
        var certificate = await CertificateAsync(certificateId, cancellationToken);
        ReleaseRecord(certificate);
        if (certificate.Status != CertificateStatus.Issued
            || await db.CertificateInstances.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                && row.UnitId == certificate.UnitId && row.DocumentKind == certificate.DocumentKind
                && row.Version > certificate.Version, cancellationToken))
            throw new CommandRefusedException(CertificateCorrectionRefusal.NotCurrent,
                "Only the current issued version can be corrected.");

        var built = await BuildTodayAsync(certificate, null, expectedCurrent: null, cancellationToken);
        var canCorrect = CertificateVersioningEngine.FactsHash(built.CanonicalPayload) != certificate.FactsHash;
        return new(certificate.CertificateId, certificate.TrackingNumber, certificate.Version,
            certificate.Version + 1, canCorrect,
            canCorrect ? null : CertificateCorrectionRefusal.NothingToCorrect,
            canCorrect ? null : NothingToCorrectDetail,
            CertificateCorrectionDiff.Compare(certificate.Payload, built.CanonicalPayload));
    }

    public async Task<ReissueCertificateResult> ReissueAsync(ReissueCertificate command,
        CancellationToken cancellationToken)
    {
        if (command.CommandId == Guid.Empty)
            throw new CommandRefusedException("command_id_required", "A command id is required.");
        if (command.CertificateId == Guid.Empty)
            throw new CommandRefusedException("certificate_id_required", "A certificate id is required.");
        if (string.IsNullOrWhiteSpace(command.ActorRef))
            throw new CommandRefusedException("actor_required", "The authenticated actor is required.");
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new CommandRefusedException(CertificateCorrectionRefusal.ReasonRequired,
                $"A correction reason must be {CertificateCorrectionRefusal.ReasonMinimumLength}–{CertificateCorrectionRefusal.ReasonMaximumLength} characters.");
        var reason = command.Reason.Trim();
        if (reason.Length < CertificateCorrectionRefusal.ReasonMinimumLength
            || reason.Length > CertificateCorrectionRefusal.ReasonMaximumLength)
            throw new CommandRefusedException(CertificateCorrectionRefusal.ReasonRequired,
                $"A correction reason must be {CertificateCorrectionRefusal.ReasonMinimumLength}–{CertificateCorrectionRefusal.ReasonMaximumLength} characters.");

        var certificate = await CertificateAsync(command.CertificateId, cancellationToken);
        ReleaseRecord(certificate);
        var issue = await CorrectionCommandAsync(certificate, reason, certificate.CertificateId,
            command.CommandId, command.ActorRef, cancellationToken);
        var issued = await versions.IssueAsync(issue, cancellationToken);
        if (!issued.Issued || issued.CertificateId is null || issued.Version is null || issued.TrackingNumber is null)
            throw new CommandRefusedException(issued.RefusalRule ?? "certificate_correction_refused",
                issued.RefusalDetail ?? "The corrected release record could not be issued.");
        return new(command.CommandId, issued.Replayed, certificate.CertificateId, issued.CertificateId.Value,
            issued.Version.Value, issued.TrackingNumber);
    }

    private async Task<PayloadBuildResult> BuildTodayAsync(CertificateInstance certificate, string? reason,
        Guid? expectedCurrent, CancellationToken cancellationToken)
    {
        var command = await CorrectionCommandAsync(certificate, reason, expectedCurrent, Guid.Empty,
            "system:certificate-correction-preview", cancellationToken);
        var unit = await units.ReadAsync(certificate.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {certificate.UnitId} does not exist.");
        var itemClass = await payloads.ItemClassAsync(unit, cancellationToken);
        var templateVersionId = await templates.ActiveVersionAsync(certificate.DocumentKind,
            certificate.FormType, itemClass, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_unavailable",
                $"No active {certificate.FormType} template exists for {certificate.DocumentKind}.");
        return await payloads.BuildAsync(command, unit, templateVersionId, cancellationToken);
    }

    private Task<IssueCertificateVersion> CorrectionCommandAsync(CertificateInstance certificate, string? reason,
        Guid? expectedCurrent, Guid commandId, string actorRef, CancellationToken cancellationToken)
    {
        var triggeringEventId = commandId == Guid.Empty ? Guid.Empty : DeterministicTrigger(commandId);
        return Task.FromResult(new IssueCertificateVersion(triggeringEventId, certificate.UnitId,
            certificate.DocumentKind, certificate.FormType, CertificateSupersedeReason.Correction,
            clock.UtcNow, actorRef, certificate.IssuerEntityId, Language: certificate.Language,
            TriggerSchema: ManualCorrectionTrigger, ExpectedCurrentCertificateId: expectedCurrent,
            TriggerNote: reason));
    }

    private async Task<CertificateInstance> CertificateAsync(Guid certificateId,
        CancellationToken cancellationToken) =>
        await db.CertificateInstances.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.CertificateId == certificateId, cancellationToken)
        ?? throw new CommandRefusedException("certificate_unknown", $"Certificate {certificateId} does not exist.");

    private static void ReleaseRecord(CertificateInstance certificate)
    {
        if (certificate.DocumentKind != CertificateDocumentKind.ReleaseRecord)
            throw new CommandRefusedException(CertificateCorrectionRefusal.ReleaseRecordsOnly,
                "Only release records can be corrected and reissued.");
    }

    private static Guid DeterministicTrigger(Guid commandId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{commandId:N}|certificate-manual-correction")).AsSpan(0, 16));
}

public static class ReissueCertificateHandler
{
    public static Task<ReissueCertificateResult> Handle(ReissueCertificate command,
        CertificateCorrectionService corrections, CancellationToken cancellationToken) =>
        corrections.ReissueAsync(command, cancellationToken);
}

internal static class CertificateCorrectionDiff
{
    private const int MaximumChanges = 200;

    public static IReadOnlyList<CertificateCorrectionChange> Compare(string beforeJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var raw = new List<CertificateCorrectionChange>();
        CompareObjects(before.RootElement, after.RootElement, "", false, raw);
        var grouped = raw.GroupBy(row => new { row.Path, row.Label, row.Before, row.After, row.Derived })
            .Select(group => new CertificateCorrectionChange(group.Key.Path, group.Key.Label, group.Key.Before,
                group.Key.After, group.Sum(row => row.Rows), group.Key.Derived))
            .OrderBy(row => row.Derived).ThenBy(row => row.Path, StringComparer.Ordinal)
            .ThenBy(row => row.Before, StringComparer.Ordinal).ThenBy(row => row.After, StringComparer.Ordinal)
            .ToList();
        if (grouped.Count <= MaximumChanges) return grouped;
        var shown = grouped.Take(MaximumChanges - 1).ToList();
        var remaining = grouped.Count - shown.Count;
        shown.Add(new("…", $"… {remaining} more", null, null, remaining, false));
        return shown;
    }

    private static void CompareObjects(JsonElement before, JsonElement after, string path, bool derived,
        List<CertificateCorrectionChange> changes)
    {
        if (before.ValueKind != after.ValueKind)
        {
            Add(path, before, after, derived, changes);
            return;
        }
        if (before.ValueKind == JsonValueKind.Object)
        {
            var left = before.EnumerateObject().ToDictionary(row => row.Name, row => row.Value);
            var right = after.EnumerateObject().ToDictionary(row => row.Name, row => row.Value);
            foreach (var name in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (path.Length == 0 && name == "trigger") continue;
                var memberPath = path.Length == 0 ? name : $"{path}.{name}";
                var hasLeft = left.TryGetValue(name, out var leftValue);
                var hasRight = right.TryGetValue(name, out var rightValue);
                if (!hasLeft || !hasRight)
                {
                    var present = hasLeft ? leftValue : rightValue;
                    if (memberPath == "installed_parts" && present.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in present.EnumerateArray())
                            changes.Add(new(memberPath, "Installed parts",
                                hasLeft ? Part(row, includeQuantity: true) : null,
                                hasRight ? Part(row, includeQuantity: true) : null, 1, true));
                        continue;
                    }
                    Add(memberPath, hasLeft ? leftValue : default, hasRight ? rightValue : default,
                        derived || memberPath == "installed_parts", changes);
                    continue;
                }
                CompareObjects(leftValue, rightValue, memberPath,
                    derived || memberPath == "installed_parts", changes);
            }
            return;
        }
        if (before.ValueKind == JsonValueKind.Array)
        {
            CompareArrays(before, after, path, derived, changes);
            return;
        }
        if (before.GetRawText() != after.GetRawText()) Add(path, before, after, derived, changes);
    }

    private static void CompareArrays(JsonElement before, JsonElement after, string path, bool derived,
        List<CertificateCorrectionChange> changes)
    {
        var left = before.EnumerateArray().ToList();
        var right = after.EnumerateArray().ToList();
        var count = Math.Max(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var hasLeft = index < left.Count;
            var hasRight = index < right.Count;
            if (path is "as_maintained" or "bom_lines" && hasLeft && hasRight
                && left[index].ValueKind == JsonValueKind.Object && right[index].ValueKind == JsonValueKind.Object)
            {
                ComparePartRow(left[index], right[index], path, derived, changes);
                continue;
            }
            if (path == "installed_parts")
            {
                var beforeValue = hasLeft ? Part(left[index], includeQuantity: true) : null;
                var afterValue = hasRight ? Part(right[index], includeQuantity: true) : null;
                if (beforeValue != afterValue)
                    changes.Add(new(path, "Installed parts", beforeValue, afterValue, 1, true));
                continue;
            }
            if (!hasLeft || !hasRight)
            {
                Add(path, hasLeft ? left[index] : default, hasRight ? right[index] : default, derived, changes);
                continue;
            }
            CompareObjects(left[index], right[index], path, derived, changes);
        }
    }

    private static void ComparePartRow(JsonElement before, JsonElement after, string path, bool derived,
        List<CertificateCorrectionChange> changes)
    {
        var beforePart = Part(before, includeQuantity: false);
        var afterPart = Part(after, includeQuantity: false);
        if (beforePart != afterPart)
            changes.Add(new($"{path}.part", "Part", beforePart, afterPart, 1, derived));
        var skipped = path == "as_maintained"
            ? new HashSet<string>(["item_code", "item_name"], StringComparer.Ordinal)
            : new HashSet<string>(["item_code", "description"], StringComparer.Ordinal);
        var left = before.EnumerateObject().Where(row => !skipped.Contains(row.Name))
            .ToDictionary(row => row.Name, row => row.Value);
        var right = after.EnumerateObject().Where(row => !skipped.Contains(row.Name))
            .ToDictionary(row => row.Name, row => row.Value);
        foreach (var name in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var memberPath = $"{path}.{name}";
            var hasLeft = left.TryGetValue(name, out var leftValue);
            var hasRight = right.TryGetValue(name, out var rightValue);
            if (!hasLeft || !hasRight)
                Add(memberPath, hasLeft ? leftValue : default, hasRight ? rightValue : default, derived, changes);
            else
                CompareObjects(leftValue, rightValue, memberPath, derived, changes);
        }
    }

    private static string? Part(JsonElement row, bool includeQuantity)
    {
        if (row.ValueKind != JsonValueKind.Object) return Display(row);
        var code = Text(row, "item_code");
        var name = Text(row, "item_name") ?? Text(row, "description");
        var value = string.IsNullOrWhiteSpace(name) || string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
            ? code ?? name : string.IsNullOrWhiteSpace(code) ? name : $"{name} · {code}";
        if (!includeQuantity || !row.TryGetProperty("quantity", out var quantity)) return value;
        return value is null ? Display(quantity) : $"{value} × {Display(quantity)}";
    }

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void Add(string path, JsonElement before, JsonElement after, bool derived,
        List<CertificateCorrectionChange> changes)
    {
        var left = before.ValueKind == JsonValueKind.Undefined ? null : Display(before);
        var right = after.ValueKind == JsonValueKind.Undefined ? null : Display(after);
        if (left == right) return;
        changes.Add(new(path, Label(path), left, right, 1, derived));
    }

    private static string? Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText(),
    };

    private static string Label(string path)
    {
        if (path == "model.name") return "Model";
        var member = path.Split('.').LastOrDefault() ?? path;
        return member switch
        {
            "item_name" or "item_code" or "description" => "Part",
            "build_date" => "Release date",
            "release_status" => "Release status",
            "serial" => "Serial number",
            "bom_synced_at" => "BOM synced at",
            _ => string.Join(' ', member.Split('_').Select(word => word.Length == 0 ? word
                : char.ToUpperInvariant(word[0]) + word[1..])),
        };
    }
}
