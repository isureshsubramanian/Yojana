namespace Yojana.Integration.LegacyMigration.Profiling;

public static class MigrationProfileOutcome
{
    public const string StopLoad = "stop_load";
    public const string NeedsPerson = "needs_person";
    public const string FixedByRule = "fixed_by_rule";
    public const string LoadedWithNote = "loaded_with_note";

    public static string Label(string outcome) => outcome switch
    {
        StopLoad => "Stop the load",
        NeedsPerson => "Go to a person",
        FixedByRule => "Fixed by a rule",
        LoadedWithNote => "Loaded with a note",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

public sealed record MigrationProfileClass(string Code, string Name, string DefaultOutcome, bool HardStop = false);

public static class MigrationProfileClasses
{
    public static readonly MigrationProfileClass SerialInMultipleWarehouses =
        new("serial_in_multiple_warehouses", "Serial in two warehouses at once", MigrationProfileOutcome.StopLoad, true);
    public static readonly MigrationProfileClass NegativeQuantity =
        new("negative_quantity", "Negative quantity", MigrationProfileOutcome.StopLoad, true);
    public static readonly MigrationProfileClass DuplicateSerialSameItem =
        new("duplicate_serial_same_item", "Same serial on one item, twice", MigrationProfileOutcome.NeedsPerson);
    public static readonly MigrationProfileClass DuplicateSerialDifferentItems =
        new("duplicate_serial_different_items", "Same serial on different items", MigrationProfileOutcome.NeedsPerson);
    public static readonly MigrationProfileClass SerialWhitespaceOrCase =
        new("serial_whitespace_or_case", "Whitespace or mixed case", MigrationProfileOutcome.FixedByRule);
    public static readonly MigrationProfileClass SerialFormatVariants =
        new("serial_format_variants", "Hyphen or leading-zero variants", MigrationProfileOutcome.FixedByRule);
    public static readonly MigrationProfileClass PlaceholderSerial =
        new("placeholder_serial", "Placeholder serials", MigrationProfileOutcome.LoadedWithNote);
    public static readonly MigrationProfileClass EquipmentCardWithoutSerial =
        new("equipment_card_without_serial", "Equipment card with no serial", MigrationProfileOutcome.NeedsPerson);
    public static readonly MigrationProfileClass ShippedSerialWithoutCard =
        new("shipped_serial_without_card", "Shipped serial with no card", MigrationProfileOutcome.LoadedWithNote);
    public static readonly MigrationProfileClass CardCustomerDeliveryMismatch =
        new("card_customer_delivery_mismatch", "Card customer differs from last delivery", MigrationProfileOutcome.LoadedWithNote);
    public static readonly MigrationProfileClass ActiveContractPastEnd =
        new("active_contract_past_end", "Contract active past its end date", MigrationProfileOutcome.LoadedWithNote);
    public static readonly MigrationProfileClass AmbiguousDates =
        new("ambiguous_dates", "Ambiguous dates", MigrationProfileOutcome.LoadedWithNote);
    public static readonly MigrationProfileClass SerialsInUserFields =
        new("serials_in_user_fields", "Serials hiding in user fields", MigrationProfileOutcome.NeedsPerson);
    public static readonly MigrationProfileClass EmptyStatus =
        new("empty_status", "Status column empty", MigrationProfileOutcome.FixedByRule);

    public static IReadOnlyList<MigrationProfileClass> All { get; } =
    [
        SerialInMultipleWarehouses,
        NegativeQuantity,
        DuplicateSerialSameItem,
        DuplicateSerialDifferentItems,
        SerialWhitespaceOrCase,
        SerialFormatVariants,
        PlaceholderSerial,
        EquipmentCardWithoutSerial,
        ShippedSerialWithoutCard,
        CardCustomerDeliveryMismatch,
        ActiveContractPastEnd,
        AmbiguousDates,
        SerialsInUserFields,
        EmptyStatus,
    ];
}
