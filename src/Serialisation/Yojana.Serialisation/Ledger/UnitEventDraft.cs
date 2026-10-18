using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// What a command hands to <see cref="IUnitEventWriter"/>: the fact to record and, when the fact changes what is
/// true now about the unit, the change to <c>unit_current</c>. The writer assigns <c>seq</c> and the id; the
/// projector applies the delta inline (coupling 2). The event's meaning stays with the module that writes it: the
/// ledger never interprets <see cref="EventType"/>, it only stores the delta the writer was given, so a replay
/// (the healer, P1-T05) folds deltas in <c>seq</c> order without knowing any event semantics.
/// </summary>
public sealed record UnitEventDraft
{
    public required Guid UnitId { get; init; }

    /// <summary>Business time, UTC.</summary>
    public required DateTime EventTime { get; init; }

    public required string EventType { get; init; }

    public string? BizStep { get; init; }

    public string? Disposition { get; init; }

    public Guid? LocationId { get; init; }

    public required Guid LegalEntityId { get; init; }

    public required string ActorRef { get; init; }

    public string? DocumentType { get; init; }

    public Guid? DocumentId { get; init; }

    public int? DocumentLine { get; init; }

    public string? PostingPeriod { get; init; }

    /// <summary>Deterministic key from <see cref="IdempotencyKey"/>; a repeat of the same key at the same business time is a no-op.</summary>
    public required string IdempotencyKey { get; init; }

    public Guid? ReversesEventId { get; init; }

    /// <summary>
    /// The event id to use instead of a generated one. A command sets it when dependent rows (unit, identifier,
    /// episode) must name their writing event before the append runs; it must be a time-ordered id for the event's
    /// business time (<c>TimeOrderedId.New(EventTime)</c>).
    /// </summary>
    public Guid? PresetEventId { get; init; }

    /// <summary>Extension data as a JSON object (ILMD, correction references, before/after values); null when none.</summary>
    public JsonObjectText? Attributes { get; init; }

    /// <summary>The change to current state this fact implies; null when the fact changes nothing operational.</summary>
    public CurrentStateDelta? Delta { get; init; }

    /// <summary>
    /// Whether this fact advances <c>unit_current.last_event_*</c>. Set false only for explicitly state-neutral
    /// audit facts, which stay in the ledger but must not create a rowversion write against operational commands.
    /// The marker is stored with the event so the replay healer makes the identical decision.
    /// </summary>
    public bool ProjectCurrentState { get; init; } = true;
}

/// <summary>A string that is a JSON object; constructed only through <see cref="Parse"/> or <see cref="Of"/> so the check constraint never fires.</summary>
public sealed record JsonObjectText
{
    private JsonObjectText(string json) => Json = json;

    public string Json { get; }

    public static JsonObjectText Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Attributes must be a JSON object.", nameof(json));
        }

        return new JsonObjectText(json);
    }

    public static JsonObjectText Of<T>(T value) => new(JsonSerializer.Serialize(value, LedgerJson.Options));
}

/// <summary>
/// Partial update of <c>unit_current</c>: a null member means "unchanged". <see cref="Create"/> is set by the event
/// that brings the unit into existence (commissioning or a pending-identifier creation) and carries the fields that
/// are required on the row. Serialised into the event's attributes under <c>_state</c> so a replay can rebuild the
/// projection without interpreting event types.
/// </summary>
public sealed record CurrentStateDelta
{
    public CreateCurrentState? Create { get; init; }

    public string? LifecycleStatus { get; init; }

    public string? Disposition { get; init; }

    public string? ConditionGrade { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? ModelVersionId { get; init; }

    /// <summary>Explicitly clears a model version; null <see cref="ModelVersionId"/> otherwise means unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClearModelVersionId { get; init; }

    public Guid? LotId { get; init; }

    /// <summary>Custody side; <see cref="Optional{T}"/> distinguishes "unchanged" from "cleared".</summary>
    public Optional<Guid?>? LocationId { get; init; }

    public Optional<string?>? LocationKind { get; init; }

    public Optional<Guid?>? CustodyPartyRef { get; init; }

    public Optional<string?>? CustodyKind { get; init; }

    public Optional<Guid?>? OpenCustodyEpisodeId { get; init; }

    /// <summary>Ownership side.</summary>
    public Optional<Guid?>? OwnerPartyRef { get; init; }

    public Optional<Guid?>? OpenOwnershipEpisodeId { get; init; }

    /// <summary>Full replacement of the overlay flag set.</summary>
    public IReadOnlyList<string>? OverlayFlags { get; init; }

    /// <summary>Full replacement of the live entitlement id list (MOD-06 from P2).</summary>
    public IReadOnlyList<Guid>? LiveEntitlementIds { get; init; }

    public Optional<DateOnly?>? WarrantyEndMax { get; init; }

    /// <summary>Keys merged into the row's attributes JSON object (a null value removes the key).</summary>
    public IReadOnlyDictionary<string, JsonElement?>? Attributes { get; init; }
}

public sealed record CreateCurrentState(Guid ItemId, string LifecycleStatus, string Disposition)
{
    public Guid? ModelVersionId { get; init; }

    public Guid? LotId { get; init; }
}

/// <summary>Wraps a value so that "set to null" and "leave unchanged" are different things in a delta.</summary>
public readonly record struct Optional<T>(T Value)
{
    public static implicit operator Optional<T>(T value) => new(value);
}

public static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
