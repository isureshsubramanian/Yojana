using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Documents;

/// <summary>Host-level behaviour of the document handlers (section <c>Serialisation:Documents</c>).</summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Serialisation:Documents";

    /// <summary>When ownership passes to the customer on a sale: <c>ship</c>, <c>deliver</c> (default) or <c>invoice</c> (ADR-0001 ownership transitions "trigger per policy").</summary>
    public string OwnershipTrigger { get; set; } = "deliver";

    /// <summary>True when a delivery confirmation follows the shipment; false moves custody straight to the customer site on shipment.</summary>
    public bool ConfirmDeliverySeparately { get; set; } = true;

    /// <summary>Documents that append at least this many events write them through the bulk path (P1-T26) instead of SaveChanges batches.</summary>
    public int BulkWriteThreshold { get; set; } = 100;
}

/// <summary>The tenant's scan-or-copy rule; missing or invalid data resolves to the safe ADR default.</summary>
public sealed class SerialConfirmationSettings(ITenantSettings settings)
{
    public const string Key = "serial_confirmation_mode";
    public const string Default = SerialConfirmationMode.ScanRequired;

    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings directly through
    // DocumentContext gives Wolverine both PlatformDbContext and SerialisationDbContext and prevents host startup.
    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        Resolve(await settings.GetAsync(Key, cancellationToken));

    public static string Resolve(string? value) =>
        value is not null && SerialConfirmationMode.All.Contains(value) ? value : Default;

    public async Task<SerialConfirmationSetting> SetAsync(SerialConfirmationSetting setting, CancellationToken cancellationToken)
    {
        if (!SerialConfirmationMode.All.Contains(setting.Mode))
        {
            throw new CommandRefusedException("serial_confirmation_mode_invalid",
                $"'{setting.Mode}' is not a serial confirmation mode.",
                $"choose '{SerialConfirmationMode.ScanRequired}' or '{SerialConfirmationMode.CopyAllowed}'");
        }

        await settings.SetAsync(Key, setting.Mode, cancellationToken);
        return setting;
    }
}

public sealed record SerialConfirmationDecision(string CaptureMethod, bool Accepted, bool SerialsCopiedWithoutScan, CommandRefusal? Refusal = null);

/// <summary>Pure line-level scan rule shared by delivery, transfer and delayed shipment confirmation.</summary>
public static class SerialConfirmationRules
{
    public const string ScanRequiredRule = "serial_confirmation_scan_required";
    public const string InvalidCaptureMethodRule = "serial_capture_method_invalid";
    public const string ScanUnblockingStep = "scan the physical serials and reconcile the line against its pick or allocation list before confirming it";

    public static SerialConfirmationDecision Evaluate(string mode, string? captureMethod)
    {
        var method = captureMethod ?? SerialCaptureMethodValue.Scanned;
        if (!SerialCaptureMethodValue.All.Contains(method))
        {
            return new(method, false, false,
                new(InvalidCaptureMethodRule, $"Serial capture method '{method}' is not recognised.",
                    $"mark the line as '{SerialCaptureMethodValue.Scanned}', '{SerialCaptureMethodValue.Copied}' or '{SerialCaptureMethodValue.Keyed}'"));
        }

        if (mode == SerialConfirmationMode.ScanRequired && method != SerialCaptureMethodValue.Scanned)
        {
            return new(method, false, false,
                new(ScanRequiredRule, $"The line used '{method}' serial capture while the tenant requires scanning.", ScanUnblockingStep));
        }

        return new(method, true, method != SerialCaptureMethodValue.Scanned);
    }
}
