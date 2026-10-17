namespace Yojana.Serialisation.Contracts;

public static class R2ConditionCodes
{
    public static bool IsFCode(string? value) => value is null || value.Length == 2 && value[0] == 'F' && value[1] is >= '1' and <= '6';
    public static bool IsCCode(string? value) => value is null || value.Length == 2 && value[0] == 'C' && value[1] is >= '1' and <= '9';
}

public static class SanitisationMethod
{
    public const string Clear = "clear";
    public const string Purge = "purge";
    public const string Destroy = "destroy";
    public static readonly IReadOnlyList<string> All = [Clear, Purge, Destroy];
}

public static class UnitTestResult
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public static readonly IReadOnlyList<string> All = [Pass, Fail];
}

public static class ConditionRequirement
{
    public const string Any = "any";
    public const string Refurbished = "refurbished";
    public static bool IsKnown(string value) => value is Any or Refurbished || ConditionGrades.IsKnown(value);
}

public static class LifeEpisodeKind
{
    public const string Refurbish = "refurbish";
    public const string Remanufacture = "remanufacture";
    public const string Rework = "rework";
    public static readonly IReadOnlyList<string> All = [Refurbish, Remanufacture, Rework];
}

public sealed record GradeUnit(
    Guid CommandId, Guid UnitId, string Grade, string ValuationRule, DateTime EventTime, string ActorRef, Guid LegalEntityId,
    string? FCode = null, string? CCode = null, DocumentRef? Document = null);

public sealed record GradeUnitRequest(
    Guid CommandId, string Grade, string ValuationRule, DateTime EventTime, Guid LegalEntityId,
    string? FCode = null, string? CCode = null, DocumentRef? Document = null);

public sealed record GradeUnitResult(Guid UnitId, Guid ConditionEventId, string Grade, string ValuationRule, Guid EventId, bool Replayed);

public sealed record RecordSanitisation(
    Guid CommandId, Guid UnitId, string Manufacturer, string Model, string MediaSerial, string MediaType,
    string Method, string Tool, string VerificationMethod, string CertificateRef, DateTime EventTime, string ActorRef, Guid LegalEntityId,
    string? ToolVersion = null, string? SignatureRef = null, DocumentRef? Document = null);

public sealed record RecordSanitisationRequest(
    Guid CommandId, string Manufacturer, string Model, string MediaSerial, string MediaType,
    string Method, string Tool, string VerificationMethod, string CertificateRef, DateTime EventTime, Guid LegalEntityId,
    string? ToolVersion = null, string? SignatureRef = null, DocumentRef? Document = null);

public sealed record RecordSanitisationResult(Guid UnitId, Guid SanitisationRecordId, string Method, Guid EventId, bool Replayed);

public sealed record RecordTest(
    Guid CommandId, Guid UnitId, string TestKind, string Result, string MeasurementsJson, string Equipment,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, DocumentRef? Document = null);

public sealed record RecordTestRequest(
    Guid CommandId, string TestKind, string Result, string MeasurementsJson, string Equipment,
    DateTime EventTime, Guid LegalEntityId, DocumentRef? Document = null);

public sealed record RecordTestResult(Guid UnitId, Guid TestRecordId, string TestKind, string Result, Guid EventId, bool Replayed);

public sealed record RecordUsageReading(
    Guid CommandId, Guid UnitId, string MeterKind, decimal Value, string Unit, DateTime ReadAt,
    string ActorRef, Guid LegalEntityId, bool MeterReplacement = false, DocumentRef? Document = null);

public sealed record RecordUsageReadingRequest(
    Guid CommandId, string MeterKind, decimal Value, string Unit, DateTime ReadAt,
    Guid LegalEntityId, bool MeterReplacement = false, DocumentRef? Document = null);

public sealed record RecordUsageReadingResult(Guid UnitId, Guid UsageReadingId, string MeterKind, decimal Value, string Unit, Guid EventId, bool Replayed);

public sealed record UnitConditionRow(Guid ConditionEventId, string Grade, string? FCode, string? CCode, string ValuationRule, string InspectorRef, DateTime GradedAt, Guid EventId);
public sealed record SanitisationRow(Guid SanitisationRecordId, string Manufacturer, string Model, string MediaSerial, string MediaType, string Method, string Tool, string? ToolVersion, string VerificationMethod, string OperatorRef, DateTime SanitisedAt, string CertificateRef, string? SignatureRef, Guid EventId);
public sealed record UnitTestRow(Guid TestRecordId, string TestKind, string Result, string MeasurementsJson, string Equipment, string OperatorRef, DateTime TestedAt, Guid EventId);
public sealed record UsageReadingRow(Guid UsageReadingId, string MeterKind, decimal Value, string Unit, DateTime ReadAt, bool MeterReplacement, string OperatorRef, Guid EventId);
public sealed record UnitAttributeHistoryRow(Guid AttributeHistoryId, string Key, string? Value, DateTime EffectiveFrom, string ActorRef, Guid EventId);

public sealed record ConditionRequirementResult(Guid UnitId, string Requirement, string? CurrentGrade, bool Satisfied);
public sealed record ConditionDisclosurePolicy(bool DiscloseCondition, Guid? RefurbisherPartyRef = null, string? WarrantyStatement = null);
public sealed record ConditionDisclosure(Guid UnitId, string Grade, bool Disclosed, string? ConditionTerm, Guid? RefurbisherPartyRef, string? WarrantyStatement);

public sealed record UnitGraded(PublishedEventHeader Header, string Grade, string? FCode, string? CCode, string ValuationRule) : IPublishedUnitEvent
{
    public const string Schema = "Unit.Graded";
    public const string Version = "1.0";
}
