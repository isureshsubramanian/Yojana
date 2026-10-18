using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Claims;

public sealed class NumberingSettings(SerialisationDbContext db)
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [DocumentNumbers.Rma] = "Returns",
        [DocumentNumbers.Claim] = "Claims",
        [DocumentNumbers.WarrantyReplacement] = "Warranty replacements",
        [DocumentNumbers.Refurbishment] = "Refurbishments",
        [DocumentNumbers.Recovery] = "Supplier recoveries",
        [DocumentNumbers.Certificate] = "Certificates",
        [DocumentNumbers.PrintJob] = "Print jobs",
    };

    public async Task<NumberingSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var configured = await db.NumberSeriesSettings.AsNoTracking().ToDictionaryAsync(s => s.Series, s => s.Mode, cancellationToken);
        return new NumberingSettingsView(DocumentNumbers.Series.Select(series => View(series,
            configured.GetValueOrDefault(series, NumberingMode.Sequence))).ToList());
    }

    internal static NumberingSeriesSetting View(string series, string mode) => new(
        series,
        Labels[series],
        mode,
        DocumentNumbers.Format(series, DateTime.UtcNow.Year, 1),
        mode == NumberingMode.Sequence
            ? "Faster under concurrency; a rollback may leave a gap."
            : "Committed documents stay consecutive; creators queue on this series counter.");
}

public sealed class SetNumberingModeHandler(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    DocumentNumbers numbers)
{
    public async Task<SetNumberingModeResult> Handle(SetNumberingMode command, CancellationToken cancellationToken)
    {
        if (!DocumentNumbers.Series.Contains(command.Series, StringComparer.Ordinal))
            throw new CommandRefusedException("number_series_invalid", $"'{command.Series}' is not a configurable document-number series.");
        if (!NumberingMode.All.Contains(command.Mode, StringComparer.Ordinal))
            throw new CommandRefusedException("numbering_mode_invalid", "Mode must be 'sequence' or 'gap_free'.");
        if (command.CommandId == Guid.Empty)
            throw new CommandRefusedException("command_id_required", "CommandId is required.");

        await db.Database.OpenConnectionAsync(cancellationToken);
        // TIGHT COUPLING (document-number lock order)
        // WHAT: mode changes take the exclusive mode lock before reading or synchronising counters/sequences; allocation
        // takes the shared form of this lock before its per-year counter or sequence-initialisation lock. WHY: the mode
        // and its backing allocator must change as one invariant. WHAT BREAKS IF SEPARATED: an allocation can use the old
        // allocator after synchronisation, duplicate a number, or reverse the order and deadlock a concurrent switch.
        await numbers.AcquireModeChangeLockAsync(command.Series, cancellationToken);
        var tenantId = tenant.TenantId;
        var row = await db.NumberSeriesSettings.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Series == command.Series, cancellationToken);
        if (row?.LastCommandId == command.CommandId)
            return new SetNumberingModeResult(NumberingSettings.View(command.Series, row.Mode), true);

        var oldMode = row?.Mode ?? NumberingMode.Sequence;
        await numbers.SynchroniseAsync(command.Series, oldMode, command.Mode, cancellationToken);
        if (row is null)
        {
            row = new NumberSeriesSetting
            {
                TenantId = tenantId,
                Series = command.Series,
                Mode = command.Mode,
                LastCommandId = command.CommandId,
                UpdatedBy = command.ActorRef,
                UpdatedAt = clock.UtcNow,
            };
            db.NumberSeriesSettings.Add(row);
        }
        else
        {
            row.Mode = command.Mode;
            row.LastCommandId = command.CommandId;
            row.UpdatedBy = command.ActorRef;
            row.UpdatedAt = clock.UtcNow;
        }

        return new SetNumberingModeResult(NumberingSettings.View(command.Series, command.Mode), false);
    }
}
