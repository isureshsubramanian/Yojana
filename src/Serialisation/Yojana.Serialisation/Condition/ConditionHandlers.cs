using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Condition;

internal static class ConditionCommandRules
{
    public const string Source = "condition";

    public static async Task RequireUnitAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken)
    {
        if (!await db.Units.AsNoTracking().AnyAsync(u => u.TenantId == tenantId && u.UnitId == unitId, cancellationToken))
            throw new CommandRefusedException("unit_unknown", $"Unit {unitId} does not exist.");
    }

    public static string Required(string value, string field, int maximum)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maximum)
            throw new CommandRefusedException($"{field}_invalid", $"{field.Replace('_', ' ')} must contain 1 to {maximum} characters.");
        return trimmed;
    }

    public static UnitEventDraft Draft(Guid unitId, Guid eventId, Guid commandId, string eventType, DateTime eventTime,
        string actorRef, Guid legalEntityId, DocumentRef? document, object attributes, CurrentStateDelta? delta = null) => new()
    {
        UnitId = unitId,
        PresetEventId = eventId,
        EventType = eventType,
        EventTime = eventTime,
        ActorRef = actorRef,
        LegalEntityId = legalEntityId,
        DocumentType = document?.Type,
        DocumentId = document?.Id,
        DocumentLine = document?.Line,
        IdempotencyKey = IdempotencyKey.ForCommand(Source, commandId, unitId, eventType, eventTime),
        Attributes = JsonObjectText.Of(attributes),
        Delta = delta,
    };
}

public static class GradeUnitHandler
{
    public static async Task<GradeUnitResult> Handle(GradeUnit command, SerialisationDbContext db, IUnitEventWriter writer,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.ConditionEvents.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.UnitId, replay.ConditionEventId, replay.Grade, replay.ValuationRule, replay.EventId, true);

        await ConditionCommandRules.RequireUnitAsync(db, tenantId, command.UnitId, cancellationToken);
        var grade = command.Grade.Trim().ToLowerInvariant();
        if (!ConditionGrades.IsKnown(grade))
            throw new CommandRefusedException("condition_grade_unknown", $"'{command.Grade}' is not a condition grade.");
        var fCode = command.FCode?.Trim().ToUpperInvariant();
        var cCode = command.CCode?.Trim().ToUpperInvariant();
        if (!R2ConditionCodes.IsFCode(fCode)) throw new CommandRefusedException("r2_f_code_invalid", "R2v3 functionality code must be F1 to F6.");
        if (!R2ConditionCodes.IsCCode(cCode)) throw new CommandRefusedException("r2_c_code_invalid", "R2v3 cosmetic code must be C1 to C9.");
        var valuationRule = ConditionCommandRules.Required(command.ValuationRule, "valuation_rule", 100);

        var eventId = TimeOrderedId.New(command.EventTime);
        var conditionEventId = TimeOrderedId.New(command.EventTime);
        db.ConditionEvents.Add(new UnitConditionEvent
        {
            TenantId = tenantId, ConditionEventId = conditionEventId, CommandId = command.CommandId, UnitId = command.UnitId,
            Grade = grade, FCode = fCode, CCode = cCode, ValuationRule = valuationRule, InspectorRef = command.ActorRef,
            GradedAt = command.EventTime, EventId = eventId,
        });
        await writer.AppendAsync([ConditionCommandRules.Draft(command.UnitId, eventId, command.CommandId, UnitEventType.Graded,
            command.EventTime, command.ActorRef, command.LegalEntityId, command.Document,
            new { command_id = command.CommandId, condition_event_id = conditionEventId, grade, f_code = fCode, c_code = cCode, valuation_rule = valuationRule },
            new CurrentStateDelta { ConditionGrade = grade })], cancellationToken);
        return new(command.UnitId, conditionEventId, grade, valuationRule, eventId, false);
    }
}

public static class RecordSanitisationHandler
{
    public static async Task<RecordSanitisationResult> Handle(RecordSanitisation command, SerialisationDbContext db,
        IUnitEventWriter writer, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.SanitisationRecords.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.UnitId, replay.SanitisationRecordId, replay.Method, replay.EventId, true);

        await ConditionCommandRules.RequireUnitAsync(db, tenantId, command.UnitId, cancellationToken);
        var method = command.Method.Trim().ToLowerInvariant();
        if (!SanitisationMethod.All.Contains(method))
            throw new CommandRefusedException("sanitisation_method_unknown", $"'{command.Method}' is not clear, purge or destroy.");
        var manufacturer = ConditionCommandRules.Required(command.Manufacturer, "manufacturer", 200);
        var model = ConditionCommandRules.Required(command.Model, "model", 200);
        var mediaSerial = ConditionCommandRules.Required(command.MediaSerial, "media_serial", 200);
        var mediaType = ConditionCommandRules.Required(command.MediaType, "media_type", 80);
        var tool = ConditionCommandRules.Required(command.Tool, "tool", 200);
        var verification = ConditionCommandRules.Required(command.VerificationMethod, "verification_method", 200);
        var certificate = ConditionCommandRules.Required(command.CertificateRef, "certificate_ref", 300);
        if (command.ToolVersion?.Trim() is { Length: > 80 }) throw new CommandRefusedException("tool_version_invalid", "tool version cannot exceed 80 characters.");
        if (command.SignatureRef?.Trim() is { Length: > 300 }) throw new CommandRefusedException("signature_ref_invalid", "signature ref cannot exceed 300 characters.");

        var eventId = TimeOrderedId.New(command.EventTime);
        var recordId = TimeOrderedId.New(command.EventTime);
        db.SanitisationRecords.Add(new SanitisationRecord
        {
            TenantId = tenantId, SanitisationRecordId = recordId, CommandId = command.CommandId, UnitId = command.UnitId,
            Manufacturer = manufacturer, Model = model, MediaSerial = mediaSerial, MediaType = mediaType, Method = method,
            Tool = tool, ToolVersion = command.ToolVersion?.Trim(), VerificationMethod = verification, OperatorRef = command.ActorRef,
            SanitisedAt = command.EventTime, CertificateRef = certificate, SignatureRef = command.SignatureRef?.Trim(), EventId = eventId,
        });
        await writer.AppendAsync([ConditionCommandRules.Draft(command.UnitId, eventId, command.CommandId, UnitEventType.SanitisationRecorded,
            command.EventTime, command.ActorRef, command.LegalEntityId, command.Document,
            new { command_id = command.CommandId, sanitisation_record_id = recordId, method, tool, tool_version = command.ToolVersion?.Trim(), verification_method = verification, certificate_ref = certificate })], cancellationToken);
        return new(command.UnitId, recordId, method, eventId, false);
    }
}

public static class RecordTestHandler
{
    public static async Task<RecordTestResult> Handle(RecordTest command, SerialisationDbContext db, IUnitEventWriter writer,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.TestRecords.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.UnitId, replay.TestRecordId, replay.TestKind, replay.Result, replay.EventId, true);

        await ConditionCommandRules.RequireUnitAsync(db, tenantId, command.UnitId, cancellationToken);
        var kind = ConditionCommandRules.Required(command.TestKind, "test_kind", 100);
        var result = command.Result.Trim().ToLowerInvariant();
        if (!UnitTestResult.All.Contains(result)) throw new CommandRefusedException("test_result_unknown", $"'{command.Result}' is not pass or fail.");
        var equipment = ConditionCommandRules.Required(command.Equipment, "equipment", 300);
        JsonElement measurementsJson;
        try
        {
            using var measurements = JsonDocument.Parse(command.MeasurementsJson);
            if (measurements.RootElement.ValueKind != JsonValueKind.Object)
                throw new CommandRefusedException("test_measurements_invalid", "Test measurements must be a JSON object.");
            measurementsJson = measurements.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CommandRefusedException("test_measurements_invalid", $"Test measurements are not valid JSON: {ex.Message}");
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var recordId = TimeOrderedId.New(command.EventTime);
        db.TestRecords.Add(new TestRecord
        {
            TenantId = tenantId, TestRecordId = recordId, CommandId = command.CommandId, UnitId = command.UnitId,
            TestKind = kind, Result = result, MeasurementsJson = command.MeasurementsJson, Equipment = equipment,
            OperatorRef = command.ActorRef, TestedAt = command.EventTime, EventId = eventId,
        });
        await writer.AppendAsync([ConditionCommandRules.Draft(command.UnitId, eventId, command.CommandId, UnitEventType.TestRecorded,
            command.EventTime, command.ActorRef, command.LegalEntityId, command.Document,
            new { command_id = command.CommandId, test_record_id = recordId, test_kind = kind, result, measurements = measurementsJson, equipment })], cancellationToken);
        return new(command.UnitId, recordId, kind, result, eventId, false);
    }
}

public static class RecordUsageReadingHandler
{
    public static async Task<RecordUsageReadingResult> Handle(RecordUsageReading command, SerialisationDbContext db,
        IUnitEventWriter writer, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.UsageReadings.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.UnitId, replay.UsageReadingId, replay.MeterKind, replay.Value, replay.Unit, replay.EventId, true);

        await ConditionCommandRules.RequireUnitAsync(db, tenantId, command.UnitId, cancellationToken);
        var meterKind = ConditionCommandRules.Required(command.MeterKind, "meter_kind", 80);
        var unit = ConditionCommandRules.Required(command.Unit, "usage_unit", 40);
        if (command.Value < 0) throw new CommandRefusedException("usage_value_invalid", "Usage readings cannot be negative.");

        // UPDLOCK/HOLDLOCK protects the meter's range until the Wolverine transaction commits, so two concurrent
        // readings cannot both pass the monotonic check against the same predecessor.
        var readings = await db.UsageReadings.FromSqlInterpolated($$"""
            SELECT * FROM [serialisation].[usage_reading] WITH (UPDLOCK,HOLDLOCK)
            WHERE [tenant_id] = {{tenantId.Value}} AND [unit_id] = {{command.UnitId}} AND [meter_kind] = {{meterKind}}
            """).AsNoTracking().ToListAsync(cancellationToken);
        var previous = readings.Where(r => r.ReadAt < command.ReadAt).OrderByDescending(r => r.ReadAt).ThenByDescending(r => r.UsageReadingId).FirstOrDefault();
        var next = readings.Where(r => r.ReadAt > command.ReadAt).OrderBy(r => r.ReadAt).ThenBy(r => r.UsageReadingId).FirstOrDefault();
        if (readings.Any(r => r.ReadAt == command.ReadAt))
            throw new CommandRefusedException("usage_reading_time_duplicate", $"{meterKind} already has a reading at {command.ReadAt:O}.");
        if ((previous is not null && previous.Unit != unit) || (next is not null && next.Unit != unit))
            throw new CommandRefusedException("usage_unit_mismatch", $"{meterKind} is recorded in {previous?.Unit ?? next!.Unit}, not {unit}.");
        if (!command.MeterReplacement && previous is not null && command.Value < previous.Value)
            throw new CommandRefusedException("usage_not_monotonic", $"{meterKind} reading {command.Value} {unit} is below the previous {previous.Value} {previous.Unit}.", "flag the reading as a meter replacement when the physical meter was replaced");
        if (!command.MeterReplacement && next is { MeterReplacement: false } && command.Value > next.Value)
            throw new CommandRefusedException("usage_not_monotonic", $"{meterKind} reading {command.Value} {unit} is above the following {next.Value} {next.Unit}.");

        var eventId = TimeOrderedId.New(command.ReadAt);
        var readingId = TimeOrderedId.New(command.ReadAt);
        db.UsageReadings.Add(new UsageReading
        {
            TenantId = tenantId, UsageReadingId = readingId, CommandId = command.CommandId, UnitId = command.UnitId,
            MeterKind = meterKind, Value = command.Value, Unit = unit, ReadAt = command.ReadAt,
            MeterReplacement = command.MeterReplacement, OperatorRef = command.ActorRef, EventId = eventId,
        });
        await writer.AppendAsync([ConditionCommandRules.Draft(command.UnitId, eventId, command.CommandId, UnitEventType.UsageRecorded,
            command.ReadAt, command.ActorRef, command.LegalEntityId, command.Document,
            new { command_id = command.CommandId, usage_reading_id = readingId, meter_kind = meterKind, value = command.Value, unit, read_at = command.ReadAt, meter_replacement = command.MeterReplacement })], cancellationToken);
        return new(command.UnitId, readingId, meterKind, command.Value, unit, eventId, false);
    }
}
