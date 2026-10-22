namespace Yojana.DemoIncumbent;

public static class ScenarioCatalogue
{
    public static IReadOnlyList<DemoScenario> All { get; } =
    [
        Data(["T1"], "T1", "Crossed unit identities and a replacement naming the wrong unit", "Two physical units are crossed in the records; the replacement points at the wrong card.", "The correction workbench swaps the identifiers, corrects the replacement link and reissues the records.", "crossed", "replacement"),
        Data(["T2"], "T2", "Serial recorded against the wrong item", "A laptop serial is recorded against the neighbouring model.", "Canonicalisation flags the item mismatch; CorrectItem preserves the unit history.", "wrong-item"),
        Data(["T3"], "T3", "Reduced production quantity after serial allocation", "Allocated serials remain against a production order whose required quantity fell.", "The production review uses VoidUnit for units never completed.", "production-reduce"),
        Product(["T4"], "Installed-base export", "Use the bulk sold-unit population.", "Export system serial, description, customer purchase reference and cover expiry."),
        Product(["T5"], "Label legal wording", "Use any server unit.", "The label designer flags legally sensitive static wording for review."),
        Product(["T6"], "Blank serial label", "Use any serialised unit.", "Required bindings prevent an empty serial label from rendering."),
        Product(["T7"], "Revision-bound label", "Use any server unit.", "Revision comes from item data rather than static template text."),
        Data(["T8"], "T8", "Original unit and refurbished successor", "The original and successor cards disagree and the successor has a new serial.", "Migration reconstructs lineage; entitlements remain attached to the logical unit.", "refurbished", "replacement"),
        Data(["T9", "T11"], "T9", "Warranty on an original and refurbished successor", "Two related cards carry inconsistent dates.", "One corrected entitlement covers the reconstructed lineage and the certificate shows both serials.", "refurbished", "card-date"),
        Data(["T10", "T12"], "T10", "Card end date differs from the sold term", "The installed-base card has a manually edited end date.", "The migration review exposes the date source and AmendEntitlement records the correction.", "card-date"),
        Product(["T13"], "Certificate component correction", "Use a twin-node server with serialised drives.", "CorrectAsBuilt adds the missing component and reissues the release record."),
        Product(["T14"], "Customer serial lookup", "Use Nandira Health's sold units.", "The portal reconciles an asserted customer list and shows entitlement expiry."),
        Product(["T15"], "Serialised-component report", "Use the twin-node and storage units.", "The completeness view shows required serialised components and missing positions."),
        Data(["T16"], "T16", "Kit components were not serialised", "A kit was received as a non-serial accessory even though its field components have identities.", "LateSerialization and CorrectAsBuilt capture the missing component identities.", "kit"),
        Product(["T17"], "Customer reference export", "Use the delivered workstation population.", "The export adds order references, software licence identifiers and MAC addresses."),
        Data(["T18"], "T18", "Bulk fixed-date extension", "Many cards were edited one by one and now have slightly different dates.", "A selection set applies one governed entitlement amendment to the whole order.", "bulk-date"),
        Data(["T19"], "T19", "Service history and bulk date maintenance", "Several units have service calls and inconsistent card dates.", "The unit timeline shows service history; entitlement maintenance is a bulk command.", "service", "bulk-date"),
        Data(["T20"], "T20", "Four-unit replacement", "Four returned units and four successors have separate cards and manual warranty carry-over.", "Replacement links transfer cover without moving build facts.", "replacement-bulk"),
        Data(["T21"], "T21", "Sold units missing installed-base cards", "Sold serials have no equipment card.", "The entitlement gap report finds them and BackfillEntitlements records evidence.", "sold-no-card"),
        Data(["T22"], "T22", "Drop-shipped units missing cards", "Drop-shipped serials are sold without installed-base cards.", "The gap report identifies the units and uses vendor confirmation as the start basis.", "drop-no-card"),
        Data(["T23"], "T23", "Service-level change represented by two contracts", "The old contract was manually ended yesterday while a replacement contract is also present.", "Mapping reconstructs the service-level supersession and retains the old term.", "two-contracts"),
        Product(["T24"], "Pasted component lookup", "Use drive serials within twin-node servers.", "The back-office query returns the parent system and customer reference."),
        Data(["T25"], "T25", "Express service bought after sale", "Tens of units have a second service contract after the original sale.", "A governed bulk supersession replaces the service-level entitlement.", "addon-express"),
        Data(["T26"], "T26", "Accidental-damage add-on bought after sale", "The protection appears as a second contract or card text.", "A separately priced stacked entitlement is created for the add-on.", "addon-damage"),
        Data(["T27"], "T27", "Fixed expiry change for an order", "Cards on one order differ after one-by-one edits.", "Bulk AmendEntitlement uses the order selection set and one approval.", "bulk-date"),
        Data(["T28"], "T28", "Drop-ship installed-base gap", "Drop-shipped serials have no card, with some supplier dates only on the serial master.", "The gap report separates missing cover from serial-master-only date evidence.", "drop-no-card", "serial-date-only"),
        Data(["T29"], "T29", "Express, accidental damage and retention add-ons", "Tens of units carry mixed second-contract and card-text add-ons.", "One selection set supersedes service level and stacks damage and retention families.", "addon-bundle"),
        Data(["T30"], "T30", "Three-to-four-year term change", "Cards were changed one by one and their dates vary.", "A bulk purchase-backed entitlement amendment establishes one term.", "bulk-date"),
        Data(["T31"], "T31", "Missing refurbishment prefix", "A serial lacks its required R prefix and neighbouring cards carry a different service type.", "CorrectIdentifier records a relabel and a service-level supersession handles the cards.", "prefix", "card-date"),
        Data(["T32"], "T32", "Coverage add-ons while units await service", "Open service calls exist while add-on cover is only in card text and second contracts.", "CoverageChanged re-adjudicates the open work without reopening it.", "addon-bundle", "service"),
        Data(["T33"], "T33", "Existing manufacturer serial reused", "A second unit record carries a manufacturer serial already present.", "The reviewer chooses MergeUnits or acknowledges a vendor-scoped duplicate.", "duplicate-oem"),
        Product(["T34"], "Item-filtered serial list", "Use the laptop and workstation populations.", "The installed-base list filters by item and exports the serials."),
        Data(["T35"], "T35", "Wrong service level on a large order", "A bulk population has an expired original contract and a second express contract.", "SupersedeEntitlement corrects the whole order and exposes the root source line.", "two-contracts-bulk"),
        Data(["T36"], "T36", "Accidental damage omitted from cards", "Two orders have paid add-on cover represented inconsistently.", "The source warranty line creates stacked accidental-damage entitlements.", "addon-damage"),
        Data(["T37", "T38"], "T37", "Fixed end-date extension", "Cards were individually changed to nearby, inconsistent dates.", "A selection set applies the fixed date as one entitlement amendment.", "bulk-date"),
        Data(["T39"], "T39", "Drive where-used lookup", "Serialised drives sit inside twin-node servers.", "Where-used returns the parent unit and its live cover.", "components"),
        Data(["T40"], "T40", "Serials switched between production orders", "Two production orders refer to each other's intended serial population.", "ReassignAllocation or SwapIdentifiers corrects the appropriate lifecycle stage.", "production-switched"),
        Data(["T41"], "T41", "Components sold without serial capture", "A sales order contains serial-managed components but no serial allocation.", "The sale remains in exception until AssignIdentifiers supplies the units.", "order-no-serial"),
        Data(["T42", "T43"], "T42", "Incomplete replacement list for a work order", "Defective motherboard identities remain and the replacement list is short.", "AssignIdentifiers reports the exact missing count and awaits later rows.", "production-defects"),
        Data(["T44"], "T44", "Sticker expiry differs from sold cover", "The installed-base card end date is deliberately inconsistent.", "The product distinguishes stale print payload from an incorrect entitlement.", "card-date"),
        Data(["T45"], "T45", "Production and sales serials requested for deletion", "Never-completed allocated identities remain against an order.", "VoidUnit preserves history and unblocks the quantity contract.", "production-reduce"),
        Product(["T46"], "Certificate disclosure", "Use any delivered server.", "Internal lookup shows buyer and document chain; public and release records omit party data."),
        Data(["T47"], "T47", "Drive identity validation", "Serialised drives are installed in server parents.", "Pasted-list validation and where-used return the parent and cover end date.", "components"),
        Data(["T48"], "T48", "Extra contract line for one serial", "One unit alone has an additional service-contract line.", "Migration maps it as a stacked tenant-defined coverage family.", "extra-contract"),
        Data(["T49"], "T49", "Whitespace and case variants", "Raw serials include leading or trailing whitespace and case-only variants where the service accepts them.", "Profiling and canonicalisation preserve raw values and propose one normal identity.", "dirty-identifiers"),
        Data(["T50"], "T50", "Printed expiry differs by one month", "The card expiry is one month earlier than what was sold.", "The stored print payload separates a stale sticker from an entitlement correction.", "card-date-only"),
        Data(["T51"], "T51", "Monitor population sent for rework", "Tens of monitor units remain in the source rather than being deleted.", "A rework order preserves unit identity and reissues records only when build facts change.", "rework-bulk"),
        Data(["T52"], "T52", "Warranty terms attached to two serials", "Two unit cards were created by hand with typed one-year dates.", "The warranty service line is the entitlement source for the attached unit list.", "card-date"),
        Data(["T53"], "T53", "Production quantity reduction after reversal", "Three unused allocated identities remain after components were reversed.", "VoidUnit preserves the evidence and permits the order quantity reduction.", "production-reduce"),
        Data(["T54"], "T54", "Ninety-day to three-year bulk change", "A serial list has inconsistent manual card edits.", "Bulk AmendEntitlement routes the large increase for service-manager approval.", "bulk-date"),
        Data(["T55"], "T55", "On-site to express service change", "The units sit on the original and replacement contract, with the old end date yesterday.", "The old service-level entitlement closes when the new one begins.", "two-contracts"),
        Data(["T56"], "T56", "Extended cover on workstations", "Tens of workstations have hand-edited end dates tied to a later order.", "The referenced-document target rule creates the extensions with the customer reference.", "addon-express"),
        Data(["T57"], "T57", "Three-year to one-year reduction", "Cards on one order were reduced inconsistently.", "The reduction requires higher approval and customer-notification evidence.", "bulk-date-reduction"),
        Product(["T58"], "Spreadsheet intake", "Use the duplicate and dirty-identifier populations.", "Import comparison, matching, row results and clean-file archive are shown in the product."),
        Data(["T59"], "T59", "Asterisk serial suffix", "A serial ends with an asterisk scanner artefact.", "Profiling raises an identifier-format anomaly; correction preserves the raw value.", "asterisk"),
        Data(["T60"], "T60", "Replacement with warranty transfer", "An old customer asset and its successor have linked installed-base cards.", "Migration creates a replacement link and transfers cover, never build facts.", "replacement"),
        Data(["T61"], "T61", "Sales order missing serial allocation", "A sales order has a known serial range in its reference but no serials captured.", "The line remains an exception until the range-backed selection set is reviewed.", "order-range"),
        Data(["T62"], "T62", "Delivery posted in error then returned", "A marked delivery is fully returned in the same warehouse.", "Reconstruction recognises the reversal; the units remain reusable rather than deleted.", "delivery-return"),
        Data(["T63"], "T63", "Switch and modules missing from the kit record", "The kit line is non-serial while field modules have identities.", "LateSerialization and CorrectAsBuilt add the missing module serials.", "kit"),
        Data(["T64"], "T64", "Second contract line on one serial", "One unit carries an additional contract line beside its base cover.", "The line maps to a stacked coverage family.", "extra-contract"),
        Product(["T65"], "Certificate issue by order or serial", "Use a production batch and its individual units.", "Issue or reprint a per-unit file and bundle from a selection set."),
        Data(["T66a"], "T66a", "Delivery contains another order's serials", "The posted delivery deliberately uses the neighbouring population.", "Document comparison surfaces a conflict; CorrectDocumentSerials replaces the recorded list.", "wrong-delivery"),
        Data(["T66b"], "T66b", "Drop-ship duplicate serial file", "Tens of units include case and cross-item duplicate identities.", "Import compare reports in-file duplicates, collisions and the reviewed action per row.", "drop-duplicates"),
        Data(["T67"], "T67", "Serial is on the wrong laptop model", "A laptop serial and card use the neighbouring model.", "Canonicalisation flags the mismatch; correction history remains reversible.", "wrong-item"),
        Data(["T68"], "T68", "Defective motherboard replacements", "Three defective serials remain associated with a larger work-order population.", "The defective units are scrapped pre-release and three replacements are commissioned.", "production-defects"),
        Product(["T69"], "Internal-to-manufacturer serial cross-reference", "Use a bulk workstation order.", "The identifier report exports both identity schemes for the selected line."),
        Data(["T70"], "T70", "Units released under the neighbouring sales order", "A delivery is based on the wrong marked sales order and inherits its cover.", "CorrectSourceDocument re-derives entitlements and queues label reprints.", "wrong-order"),
        Data(["T71"], "T71", "Whole delivery uses the wrong production population", "A marked delivery carries the serials intended for its neighbouring production order.", "CorrectDocumentSerials performs a whole-list replacement without changing quantity.", "wrong-delivery-bulk"),
        Data(["T72"], "T72", "Units included on a contract in error", "Listed units carry a contract line that should not exist.", "CloseEntitlement retains the row and records included-in-error as the reason.", "extra-contract"),
        Data(["T73"], "T73", "Replacement link names the wrong successor", "The installed-base replacement pair points at a neighbouring unit.", "CorrectReplacementLink reverses and reapplies the transfer while retaining the bad link.", "replacement-wrong"),
        Data(["T74"], "T74", "Returned units awaiting re-shipment", "A delivery and return close the erroneous ownership episode but keep the unit identities.", "A later shipment opens a new episode; no serial, card or document row is deleted.", "delivery-return"),
    ];

    public static IReadOnlyList<string> TicketIds { get; } =
        All.SelectMany(x => x.Tickets).ToArray();

    public static IReadOnlyList<string> SerialsFor(DemoScenario scenario) => DemoData.Serials(scenario);

    private static DemoScenario Data(string[] tickets, string marker, string scenario, string incumbent,
        string product, params string[] recipes) =>
        new(tickets, marker, scenario, incumbent, product, DemoDisposition.Data, recipes);

    private static DemoScenario Product(string[] tickets, string scenario, string incumbent, string product) =>
        new(tickets, tickets[0], scenario, incumbent, product, DemoDisposition.ProductOnly, []);
}
