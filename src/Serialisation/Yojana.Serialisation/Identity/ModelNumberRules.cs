using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Identity;

/// <summary>Pure comparison and refusal rules shared by every serial-capture boundary.</summary>
public static class ModelNumberRules
{
    public const string Rule = "serial_is_model_number";
    public const string Message = "This looks like the model number. Scan the S/N barcode.";
    public const string SameItemDuplicateHint = "Two units of the same item share this value. It may be the model number.";

    private static readonly HashSet<string> SerialTypes = new(StringComparer.Ordinal)
    {
        IdentifierType.ManufacturerSerial,
        IdentifierType.InternalSerial,
        IdentifierType.VendorSerial,
        IdentifierType.CustomerSerial,
        IdentifierType.Imei,
        IdentifierType.Mac,
        IdentifierType.UdiPi,
        IdentifierType.Uii,
        IdentifierType.Vin,
        IdentifierType.Sgtin,
        IdentifierType.Giai,
    };

    public static string Normalise(string value) =>
        new(value.Where(character => character != ' ').Select(char.ToUpperInvariant).ToArray());

    public static bool Matches(ItemSerialisationProfile profile, string identifierType, string value)
    {
        if (!SerialTypes.Contains(identifierType)) return false;
        var captured = Normalise(value);
        if (captured.Length == 0) return false;
        return captured == Normalise(profile.ItemCode)
            || (profile.ModelNumbers ?? []).Any(model => captured == Normalise(model));
    }

    public static void RefuseUnlessConfirmed(bool match, bool confirmedNotModelNumber, string? reason)
    {
        if (!match) return;
        if (confirmedNotModelNumber && !string.IsNullOrWhiteSpace(reason)) return;
        throw new CommandRefusedException(Rule, Message, "scan the S/N barcode, or confirm that this really is the serial and give a reason");
    }
}
