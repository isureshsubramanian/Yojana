namespace Yojana.DemoIncumbent;

internal sealed record DemoItem(string Code, string Name, bool Serialised = true);
internal sealed record DemoPartner(string Code, string Name, bool Supplier);
internal sealed record DemoContractTemplate(string Name, string Description, int Duration,
    int ResolutionTime, string ResolutionUnit, int ResponseTime, string ResponseUnit,
    bool Parts, bool Labour, bool Travel);

internal static class DemoData
{
    public const string PrimaryCustomer = "YDCNANDIRA";
    public const string SecondaryCustomer = "YDCOSTRAVA";
    public const string UniversityCustomer = "YDCBELLMNT";
    public const string PrimarySupplier = "YDVARVEN";

    public static IReadOnlyList<DemoItem> Items { get; } =
    [
        new("YD-SRV-1U", "Kestrel Systems 1U server"),
        new("YD-SRV-15U", "Kestrel Systems 1.5U server"),
        new("YD-SRV-TWIN", "Kestrel Systems twin-node chassis"),
        new("YD-NODE-TWIN", "Kestrel Systems twin compute node"),
        new("YD-HPC-NODE", "Kestrel Systems HPC node"),
        new("YD-STOR", "Arvenna storage shelf"),
        new("YD-SWITCH", "Ferro Nets managed switch"),
        new("YD-ROUTER", "Ferro Nets branch router"),
        new("YD-SFP", "Ferro Nets optical module"),
        new("YD-LAP-14", "Lumio 14-inch laptop"),
        new("YD-LAP-15", "Lumio 15-inch laptop"),
        new("YD-WORKST", "Lumio engineering workstation"),
        new("YD-MONITOR", "Lumio 27-inch monitor"),
        new("YD-MAINBOARD", "Kestrel server motherboard"),
        new("YD-NVME", "Quill 3.84 TB NVMe drive"),
        new("YD-HDD", "Quill 16 TB hard drive"),
        new("YD-PSU", "Kestrel hot-swap power supply"),
        new("YD-3DPRINT", "Aster Forge 3D printer"),
        new("YD-KIT-ACC", "Server installation accessory kit", false),
        new("YD-CABLE", "Fibre patch cable", false),
    ];

    public static IReadOnlyList<DemoPartner> Partners { get; } =
    [
        new(PrimaryCustomer, "Nandira Health", false),
        new(SecondaryCustomer, "Ostrava Logistics", false),
        new(UniversityCustomer, "Bellmont University", false),
        new(PrimarySupplier, "Arven Components", true),
        new("YDVQUILL", "Quill Drive Supply", true),
    ];

    public static IReadOnlyList<DemoContractTemplate> ContractTemplates { get; } =
    [
        new("YD Base 12M", "Base cover for twelve months", 12, 1, "rsu_Days", 1, "boru_Day", true, true, true),
        new("YD Base 36M", "Base cover for thirty-six months", 36, 1, "rsu_Days", 1, "boru_Day", true, true, true),
        new("YD NBD Onsite", "Next-business-day on-site service", 36, 1, "rsu_Days", 1, "boru_Day", true, true, true),
        new("YD 4H Onsite", "Four-hour on-site service", 36, 4, "rsu_Hours", 4, "boru_Hour", true, true, true),
        new("YD Accident", "Accidental-damage protection", 36, 1, "rsu_Days", 1, "boru_Day", true, true, false),
        new("YD Media Retain", "Drive and media retention", 36, 1, "rsu_Days", 1, "boru_Day", false, true, false),
        new("YD No Cover", "Parts labour and travel excluded", 12, 1, "rsu_Days", 1, "boru_Day", false, false, false),
        new("YD Single Term", "One total duration with no separate term blocks", 48, 1, "rsu_Days", 1, "boru_Day", true, true, true),
    ];

    public static int UnitCount(DemoScenario scenario) => scenario.MarkerTicket switch
    {
        "T18" or "T23" or "T25" or "T26" or "T27" or "T29" or "T30" or "T32" or "T35"
            or "T36" or "T37" or "T51" or "T54" or "T55" or "T56" or "T57" or "T66b"
            or "T68" or "T70" or "T71" => 20,
        "T20" => 8,
        "T42" or "T53" or "T61" or "T66a" => 6,
        _ => 2,
    };

    public static DemoItem ItemFor(DemoScenario scenario)
    {
        var recipes = scenario.RecipeKeys;
        if (recipes.Contains("wrong-item")) return Item("YD-LAP-15");
        if (recipes.Contains("components")) return Item("YD-NVME");
        if (recipes.Contains("production-defects")) return Item("YD-MAINBOARD");
        if (recipes.Any(value => value.StartsWith("production", StringComparison.Ordinal))) return Item("YD-SRV-1U");
        if (recipes.Contains("rework-bulk")) return Item("YD-MONITOR");
        if (recipes.Contains("kit")) return Item("YD-KIT-ACC");
        if (recipes.Contains("order-no-serial")) return Item("YD-MAINBOARD");
        if (recipes.Contains("drop-duplicates")) return Item("YD-LAP-14");
        if (recipes.Any(value => value.StartsWith("drop", StringComparison.Ordinal))) return Item("YD-ROUTER");
        if (recipes.Contains("prefix") || recipes.Contains("refurbished")) return Item("YD-SRV-15U");
        if (recipes.Contains("asterisk")) return Item("YD-3DPRINT");
        if (scenario.MarkerTicket is "T25" or "T26" or "T29" or "T32" or "T35" or "T36" or "T56")
            return Item("YD-WORKST");
        return Item("YD-SRV-1U");
    }

    public static IReadOnlyList<string> Serials(DemoScenario scenario)
    {
        var count = UnitCount(scenario);
        var stem = "YD-" + scenario.MarkerTicket.ToUpperInvariant() + "-";
        var serials = Enumerable.Range(1, count).Select(index => stem + index.ToString("D3")).ToArray();
        if (scenario.RecipeKeys.Contains("asterisk")) serials[0] += "*";
        if (scenario.RecipeKeys.Contains("prefix")) serials[0] = "YD-T31-NEEDS-R";
        if (scenario.RecipeKeys.Contains("dirty-identifiers"))
        {
            serials[0] = " yd-t49-001";
            serials[1] = "YD-T49-002 ";
        }
        if (scenario.RecipeKeys.Contains("duplicate-oem"))
        {
            serials[0] = "YD-T33-A";
            serials[1] = "YD-T33-B";
        }
        if (scenario.RecipeKeys.Contains("drop-duplicates")) serials[0] = "YD-T2-001";
        return serials;
    }

    public static string ManufacturerSerial(DemoScenario scenario, string serial, int index) =>
        scenario.RecipeKeys.Contains("duplicate-oem") ? "OEM-YD-T33-SAME"
            : scenario.RecipeKeys.Contains("drop-duplicates") ? "OEM-YD-T66B-" + index
            : "OEM-" + serial.Trim() + "-" + index;

    public static string CustomerFor(DemoScenario scenario) => (scenario.MarkerTicket.Sum(character => character) % 3) switch
    {
        0 => PrimaryCustomer,
        1 => SecondaryCustomer,
        _ => UniversityCustomer,
    };

    public static bool NeedsOrder(DemoScenario scenario) => scenario.RecipeKeys.Any(value => value is
        "order-no-serial" or "order-range" or "wrong-order");

    public static bool NeedsDelivery(DemoScenario scenario) => !scenario.RecipeKeys.Any(value =>
        value.StartsWith("production", StringComparison.Ordinal) || value is "rework-bulk" or "kit"
            or "order-no-serial" or "order-range");

    public static bool NeedsReturn(DemoScenario scenario) => scenario.RecipeKeys.Contains("delivery-return");

    public static bool NeedsCards(DemoScenario scenario) => NeedsDelivery(scenario)
        && !scenario.RecipeKeys.Any(value => value is "sold-no-card" or "drop-no-card");

    public static bool NeedsServiceCall(DemoScenario scenario) => scenario.RecipeKeys.Contains("service")
        || scenario.MarkerTicket is "T19" or "T32" or "T60";

    public static bool NeedsContract(DemoScenario scenario) => scenario.RecipeKeys.Any(value =>
        value.Contains("contract", StringComparison.Ordinal) || value.StartsWith("addon", StringComparison.Ordinal)
            || value.StartsWith("bulk-date", StringComparison.Ordinal) || value is "card-date" or "two-contracts"
            or "two-contracts-bulk");

    public static DemoContractTemplate ContractTemplateFor(DemoScenario scenario, bool addOn)
    {
        var name = !addOn && (scenario.MarkerTicket is "T30" or "T56") ? "YD Single Term"
            : !addOn ? "YD Base 36M"
            : scenario.RecipeKeys.Contains("addon-damage") ? "YD Accident"
            : scenario.RecipeKeys.Contains("addon-bundle") ? "YD Media Retain"
            : scenario.MarkerTicket is "T48" or "T64" or "T72" ? "YD No Cover"
            : scenario.MarkerTicket is "T30" or "T56" ? "YD Single Term"
            : scenario.MarkerTicket is "T32" or "T35" ? "YD 4H Onsite"
            : "YD NBD Onsite";
        return ContractTemplates.Single(template => template.Name == name);
    }

    public static bool NeedsSecondContract(DemoScenario scenario) => scenario.RecipeKeys.Any(value =>
        value.StartsWith("addon", StringComparison.Ordinal) || value is "two-contracts" or "two-contracts-bulk"
            or "extra-contract");

    private static DemoItem Item(string code) => Items.Single(item => item.Code == code);
}
