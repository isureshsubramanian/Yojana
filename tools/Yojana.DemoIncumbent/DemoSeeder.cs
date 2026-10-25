using System.Text.RegularExpressions;
using System.Text.Json;

namespace Yojana.DemoIncumbent;

internal sealed partial class DemoSeeder(ServiceLayerClient client)
{
    private const string Date = "2026-09-01";
    private readonly List<RemoteObject> observed = [];

    public IReadOnlyList<RemoteObject> Observed => observed;

    public async Task<ServiceContext> ContextAsync(CancellationToken cancellationToken)
    {
        var metadata = await client.GetTextAsync("$metadata", cancellationToken);
        var entitySets = EntitySetRegex().Matches(metadata).Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        using var warehouses = await client.GetAsync("Warehouses?$select=WarehouseCode,Inactive&$top=50",
            cancellationToken);
        var warehouse = warehouses.RootElement.GetProperty("value").EnumerateArray()
            .FirstOrDefault(value => ServiceLayerClient.Scalar(value, "Inactive") is not "tYES" and not "Y");
        var warehouseCode = ServiceLayerClient.Scalar(warehouse, "WarehouseCode")
            ?? throw new InvalidOperationException("The demo company has no active warehouse.");
        using var groups = await client.GetAsync("ItemGroups?$select=Number&$top=1", cancellationToken);
        var group = groups.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (!int.TryParse(ServiceLayerClient.Scalar(group, "Number"), out var itemGroup))
            throw new InvalidOperationException("The demo company has no item group.");
        using var admin = await client.ActionAsync("CompanyService_GetAdminInfo", cancellationToken);
        var localCurrency = ServiceLayerClient.Scalar(admin.RootElement, "LocalCurrency")
            ?? throw new InvalidOperationException("The demo company reports no local currency.");
        return new(warehouseCode, itemGroup, localCurrency, entitySets);
    }

    public IReadOnlyList<DemoObject> PlannedObjects()
    {
        var objects = new List<DemoObject>();
        objects.AddRange(DemoData.Items.Select(item => new DemoObject("shared", "Items", item.Code,
            item.Name, [])));
        objects.AddRange(DemoData.Partners.Select(partner => new DemoObject("shared", "BusinessPartners",
            partner.Code, partner.Name, [])));
        objects.AddRange(DemoData.ContractTemplates.Select(template => new DemoObject("shared",
            "ContractTemplates", template.Name, template.Description, [])));
        objects.AddRange(DemoData.ProductTrees.Select(tree => new DemoObject("shared", "ProductTrees", tree.Code,
            tree.Description, [])));
        foreach (var scenario in ScenarioCatalogue.All)
        {
            if (scenario.Disposition == DemoDisposition.ProductOnly) continue;
            var serials = DemoData.Serials(scenario);
            var item = DemoData.ItemFor(scenario);
            objects.Add(new(scenario.MarkerTicket, "PurchaseDeliveryNotes", Marker(scenario, 1),
                "Receive the marked scenario population", item.Serialised ? serials : []));
            if (DemoData.NeedsOrder(scenario))
                objects.Add(new(scenario.MarkerTicket, "Orders", Marker(scenario, 2),
                    "Order with the scenario's source reference", []));
            if (DemoData.NeedsDelivery(scenario))
                objects.Add(new(scenario.MarkerTicket, "DeliveryNotes", Marker(scenario, 3),
                    "Deliver the marked scenario population", serials));
            if (DemoData.NeedsReturn(scenario))
                objects.Add(new(scenario.MarkerTicket, "Returns", Marker(scenario, 4),
                    "Return the marked population for later re-shipment", serials));
            if (DemoData.NeedsCards(scenario))
            {
                objects.AddRange(serials.Select(serial => new DemoObject(scenario.MarkerTicket,
                    "CustomerEquipmentCards", serial.Trim(), "Installed-base card", [serial])));
                if (scenario.RecipeKeys.Any(value => value is "replacement" or "replacement-bulk"
                    or "replacement-wrong" or "refurbished"))
                {
                    var pairs = scenario.RecipeKeys.Contains("replacement-bulk") ? serials.Count / 2 : 1;
                    for (var index = 0; index < pairs; index++)
                        objects.Add(new(scenario.MarkerTicket, "ReplacementLinks",
                            serials[index].Trim() + "->" + serials[index + pairs].Trim(),
                            "Installed-base predecessor and successor link",
                            [serials[index], serials[index + pairs]]));
                }
            }
            if (DemoData.NeedsContract(scenario))
            {
                objects.Add(new(scenario.MarkerTicket, "ServiceContracts", Marker(scenario, 5),
                    "Marked base or add-on service contract", serials));
                if (DemoData.NeedsSecondContract(scenario))
                    objects.Add(new(scenario.MarkerTicket, "ServiceContracts", Marker(scenario, 8),
                        "Marked second service contract", serials));
            }
            if (DemoData.NeedsServiceCall(scenario))
                objects.Add(new(scenario.MarkerTicket, "ServiceCalls", Marker(scenario, 6),
                    "Marked service call with resolution text", [serials[0]]));
            if (scenario.RecipeKeys.Any(value => value.StartsWith("production", StringComparison.Ordinal)))
                objects.Add(new(scenario.MarkerTicket, "ProductionOrders", Marker(scenario, 7),
                    "Marked production order", serials));
        }
        return objects;
    }

    public async Task<ApplySummary> ApplyAsync(ServiceContext context, CancellationToken cancellationToken)
    {
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var existing = new Dictionary<string, int>(StringComparer.Ordinal);
        var unsupported = new List<string>();
        foreach (var item in DemoData.Items)
            await EnsureItemAsync(item, context, created, existing, cancellationToken);
        foreach (var partner in DemoData.Partners)
            await EnsurePartnerAsync(partner, context, created, existing, cancellationToken);
        foreach (var template in DemoData.ContractTemplates)
            await EnsureContractTemplateAsync(template, context, created, existing, unsupported,
                cancellationToken);
        foreach (var tree in DemoData.ProductTrees)
            await EnsureProductTreeAsync(tree, context, created, existing, unsupported, cancellationToken);

        foreach (var scenario in ScenarioCatalogue.All.Where(value => value.Disposition == DemoDisposition.Data))
        {
            var serials = DemoData.Serials(scenario);
            var item = DemoData.ItemFor(scenario);
            await EnsureReceiptAsync(scenario, item, serials, context, created, existing, cancellationToken);
            CreateResult? order = null;
            if (DemoData.NeedsOrder(scenario))
                order = await EnsureOrderAsync(scenario, item, context, created, existing, cancellationToken);
            CreateResult? delivery = null;
            if (DemoData.NeedsDelivery(scenario))
                delivery = await EnsureDeliveryAsync(scenario, item, serials, order, context, created, existing,
                    cancellationToken);
            if (DemoData.NeedsReturn(scenario) && delivery is not null)
                await EnsureReturnAsync(scenario, item, serials, delivery, context, created, existing,
                    cancellationToken);
            IReadOnlyList<CardState> cards = [];
            if (DemoData.NeedsDelivery(scenario) && !DemoData.NeedsCards(scenario) && item.Serialised)
                unsupported.Add($"{scenario.MarkerTicket}: the incumbent creates a card on delivery and refuses to "
                    + "delete one linked to a delivery; these units keep a card with no dates and no contract, and "
                    + "the existing demo data supplies serials shipped without a card");
            if (DemoData.NeedsCards(scenario))
                cards = await EnsureCardsAsync(scenario, item, serials, created, existing, cancellationToken);
            if (cards.Count > 1 && scenario.RecipeKeys.Any(value => value is "replacement" or "replacement-bulk"
                or "replacement-wrong" or "refurbished"))
                await EnsureReplacementLinksAsync(scenario, cards, created, existing, cancellationToken);
            if (DemoData.NeedsServiceCall(scenario))
                await EnsureServiceCallAsync(scenario, item, serials[0], created, existing, cancellationToken);
            if (DemoData.NeedsContract(scenario))
            {
                await EnsureContractAsync(scenario, item, serials, false, context, created, existing,
                    unsupported, cancellationToken);
                if (DemoData.NeedsSecondContract(scenario))
                    await EnsureContractAsync(scenario, item, serials, true, context, created, existing,
                        unsupported, cancellationToken);
            }
            if (scenario.RecipeKeys.Any(value => value.StartsWith("production", StringComparison.Ordinal)))
                await EnsureProductionOrderAsync(scenario, item, context, created, existing, unsupported,
                    cancellationToken);
        }
        return new(created, existing, observed.ToArray(), unsupported);
    }

    public async Task<IReadOnlyList<DemoObject>> ObservePlanAsync(CancellationToken cancellationToken)
    {
        var result = new List<DemoObject>();
        foreach (var planned in PlannedObjects())
        {
            var found = await FindPlannedAsync(planned, cancellationToken);
            result.Add(planned with { Exists = found is not null, Number = found?.Number });
        }
        return result;
    }

    private async Task EnsureItemAsync(DemoItem item, ServiceContext context,
        Dictionary<string, int> created, Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var found = await client.FindOneAsync("Items", "ItemCode eq " + ServiceLayerClient.Quote(item.Code),
            cancellationToken, "ItemCode");
        if (found is not null)
        {
            TrackExisting("Items", item.Code, null, [], existing);
            return;
        }
        await client.PostAsync("Items", new
        {
            ItemCode = item.Code,
            ItemName = item.Name,
            ItemsGroupCode = context.ItemGroup,
            InventoryItem = "tYES",
            SalesItem = "tYES",
            PurchaseItem = "tYES",
            ManageSerialNumbers = item.Serialised ? "tYES" : "tNO",
            ManageBatchNumbers = "tNO",
            ManageSerialNumbersOnReleaseOnly = "tNO",
            InventoryUOM = "EA",
            SalesUnit = "EA",
            PurchaseUnit = "EA",
            DefaultWarehouse = context.Warehouse,
        }, "ItemCode", null, cancellationToken);
        TrackCreated("Items", item.Code, null, [], created);
    }

    private async Task EnsurePartnerAsync(DemoPartner partner, ServiceContext context, Dictionary<string, int> created,
        Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var found = await client.FindOneAsync("BusinessPartners",
            "CardCode eq " + ServiceLayerClient.Quote(partner.Code), cancellationToken, "CardCode");
        if (found is not null)
        {
            TrackExisting("BusinessPartners", partner.Code, null, [], existing);
            return;
        }
        await client.PostAsync("BusinessPartners", new
        {
            CardCode = partner.Code,
            CardName = partner.Name,
            CardType = partner.Supplier ? "cSupplier" : "cCustomer",
            Currency = context.LocalCurrency,
            FreeText = "Yojana demo marker YD-",
        }, "CardCode", null, cancellationToken);
        TrackCreated("BusinessPartners", partner.Code, null, [], created);
    }

    private async Task EnsureContractTemplateAsync(DemoContractTemplate template, ServiceContext context,
        Dictionary<string, int> created, Dictionary<string, int> existing, List<string> unsupported,
        CancellationToken cancellationToken)
    {
        if (!context.EntitySets.Contains("ContractTemplates"))
        {
            unsupported.Add($"{template.Name}: the supported API does not expose contract templates");
            return;
        }
        var found = await client.FindOneAsync("ContractTemplates",
            "TemplateName eq " + ServiceLayerClient.Quote(template.Name), cancellationToken, "TemplateName");
        if (found is not null)
        {
            TrackExisting("ContractTemplates", template.Name, null, [], existing);
            return;
        }
        await client.PostAsync("ContractTemplates", new
        {
            TemplateName = template.Name,
            ContractType = "ct_SerialNumber",
            Description = template.Name,
            Remarks = template.Description,
            DurationOfCoverage = template.Duration,
            ResolutionTime = template.ResolutionTime,
            ResolutionUnit = template.ResolutionUnit,
            ResponseValue = template.ResponseTime,
            ResponseUnit = template.ResponseUnit,
            IncludeParts = template.Parts ? "tYES" : "tNO",
            IncludeLabor = template.Labour ? "tYES" : "tNO",
            IncludeTravel = template.Travel ? "tYES" : "tNO",
            MondayEnabled = "tYES",
            TuesdayEnabled = "tYES",
            WednesdayEnabled = "tYES",
            ThursdayEnabled = "tYES",
            FridayEnabled = "tYES",
            SaturdayEnabled = "tNO",
            SundayEnabled = "tNO",
            MondayStart = 800,
            MondayEnd = 1700,
            TuesdayStart = 800,
            TuesdayEnd = 1700,
            WednesdayStart = 800,
            WednesdayEnd = 1700,
            ThursdayStart = 800,
            ThursdayEnd = 1700,
            FridayStart = 800,
            FridayEnd = 1700,
        }, "TemplateName", null, cancellationToken);
        TrackCreated("ContractTemplates", template.Name, null, [], created);
    }

    private async Task EnsureProductTreeAsync(DemoTree tree, ServiceContext context, Dictionary<string, int> created,
        Dictionary<string, int> existing, List<string> unsupported, CancellationToken cancellationToken)
    {
        var code = tree.Code;
        if (!context.EntitySets.Contains("ProductTrees"))
        {
            unsupported.Add($"{code}: the supported API does not expose bills of material");
            return;
        }
        var lines = tree.Lines
            .Select(line => new { ItemCode = line.ItemCode, Quantity = line.Quantity, Warehouse = context.Warehouse })
            .ToArray();
        var found = await client.FindOneAsync("ProductTrees",
            "TreeCode eq " + ServiceLayerClient.Quote(code), cancellationToken, "TreeCode,ProductTreeLines");
        if (found is not null)
        {
            var present = found.Value.TryGetProperty("ProductTreeLines", out var current)
                ? current.EnumerateArray().Select(line => line.GetProperty("ItemCode").GetString()).ToHashSet()
                : [];
            if (tree.Lines.All(line => present.Contains(line.ItemCode)))
            {
                TrackExisting("ProductTrees", code, null, [], existing);
                return;
            }
            // A tree seeded before the full-BOM lines existed: send the whole line list so the result is exact.
            await client.PatchAsync("ProductTrees(" + ServiceLayerClient.Quote(code) + ")",
                new { ProductTreeLines = lines }, cancellationToken, replaceCollections: true);
            TrackCreated("ProductTrees", code + " (lines added)", null, [], created);
            return;
        }
        await client.PostAsync("ProductTrees", new
        {
            TreeCode = code,
            TreeType = "iProductionTree",
            Quantity = 1,
            Warehouse = context.Warehouse,
            ProductTreeLines = lines,
        }, "TreeCode", null, cancellationToken);
        TrackCreated("ProductTrees", code, null, [], created);
    }

    private async Task EnsureReceiptAsync(DemoScenario scenario, DemoItem item, IReadOnlyList<string> serials,
        ServiceContext context, Dictionary<string, int> created, Dictionary<string, int> existing,
        CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 1);
        var found = await FindDocumentAsync("PurchaseDeliveryNotes", marker, cancellationToken);
        if (found is not null)
        {
            TrackExisting("PurchaseDeliveryNotes", found.Key, found.Number, item.Serialised ? serials : [], existing);
            return;
        }
        var serialRows = serials.Select((serial, index) =>
        {
            var row = new Dictionary<string, object?>
            {
                ["InternalSerialNumber"] = serial,
                ["ManufacturerSerialNumber"] = DemoData.ManufacturerSerial(scenario, serial, index + 1),
                ["Quantity"] = 1,
                ["BaseLineNumber"] = 0,
            };
            if (scenario.RecipeKeys.Contains("serial-date-only"))
            {
                row["WarrantyStart"] = Date;
                row["WarrantyEnd"] = "2029-08-31";
            }
            return row;
        }).ToArray();
        var documentLine = new Dictionary<string, object?>
        {
            ["ItemCode"] = item.Code,
            ["Quantity"] = serials.Count,
            ["WarehouseCode"] = context.Warehouse,
            ["UnitPrice"] = 100,
        };
        if (item.Serialised) documentLine["SerialNumbers"] = serialRows;
        var result = await client.PostAsync("PurchaseDeliveryNotes", new
        {
            CardCode = DemoData.PrimarySupplier,
            DocDate = Date,
            DocDueDate = Date,
            NumAtCard = marker,
            Comments = Comment(scenario),
            DocumentLines = new[] { documentLine },
        }, "DocEntry", "DocNum", cancellationToken);
        TrackCreated("PurchaseDeliveryNotes", result.Key, result.Number, item.Serialised ? serials : [], created);
    }

    private async Task<CreateResult> EnsureOrderAsync(DemoScenario scenario, DemoItem item,
        ServiceContext context, Dictionary<string, int> created, Dictionary<string, int> existing,
        CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 2);
        var found = await FindDocumentAsync("Orders", marker, cancellationToken);
        if (found is not null)
        {
            TrackExisting("Orders", found.Key, found.Number, [], existing);
            return found;
        }
        var comments = scenario.RecipeKeys.Contains("order-range")
            ? Comment(scenario) + "; known range YD-T61-001 through YD-T61-006"
            : Comment(scenario) + "; serials deliberately not captured";
        var result = await client.PostAsync("Orders", new
        {
            CardCode = DemoData.CustomerFor(scenario),
            DocDate = Date,
            DocDueDate = "2026-09-15",
            NumAtCard = marker,
            Comments = comments,
            DocumentLines = new[]
            {
                new { ItemCode = item.Code, Quantity = DemoData.UnitCount(scenario), WarehouseCode = context.Warehouse, UnitPrice = 150 },
            },
        }, "DocEntry", "DocNum", cancellationToken);
        TrackCreated("Orders", result.Key, result.Number, [], created);
        return result;
    }

    private async Task<CreateResult> EnsureDeliveryAsync(DemoScenario scenario, DemoItem item,
        IReadOnlyList<string> serials, CreateResult? order, ServiceContext context,
        Dictionary<string, int> created, Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 3);
        var found = await FindDocumentAsync("DeliveryNotes", marker, cancellationToken);
        if (found is not null)
        {
            TrackExisting("DeliveryNotes", found.Key, found.Number, serials, existing);
            return found;
        }
        var line = new Dictionary<string, object?>
        {
            ["ItemCode"] = item.Code,
            ["Quantity"] = serials.Count,
            ["WarehouseCode"] = context.Warehouse,
            ["UnitPrice"] = 150,
            ["SerialNumbers"] = serials.Select(serial => new
            {
                InternalSerialNumber = serial,
                Quantity = 1,
                BaseLineNumber = 0,
            }).ToArray(),
        };
        if (order is not null && scenario.RecipeKeys.Contains("wrong-order"))
        {
            line["BaseType"] = 17;
            line["BaseEntry"] = int.Parse(order.Key, System.Globalization.CultureInfo.InvariantCulture);
            line["BaseLine"] = 0;
            line.Remove("ItemCode");
            line.Remove("UnitPrice");
        }
        var result = await client.PostAsync("DeliveryNotes", new
        {
            CardCode = DemoData.CustomerFor(scenario),
            DocDate = Date,
            DocDueDate = Date,
            NumAtCard = marker,
            Comments = Comment(scenario),
            DocumentLines = new[] { line },
        }, "DocEntry", "DocNum", cancellationToken);
        TrackCreated("DeliveryNotes", result.Key, result.Number, serials, created);
        return result;
    }

    private async Task EnsureReturnAsync(DemoScenario scenario, DemoItem item, IReadOnlyList<string> serials,
        CreateResult delivery, ServiceContext context, Dictionary<string, int> created,
        Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 4);
        var found = await FindDocumentAsync("Returns", marker, cancellationToken);
        if (found is not null)
        {
            TrackExisting("Returns", found.Key, found.Number, serials, existing);
            return;
        }
        var result = await client.PostAsync("Returns", new
        {
            CardCode = DemoData.CustomerFor(scenario),
            DocDate = "2026-09-03",
            NumAtCard = marker,
            Comments = Comment(scenario),
            DocumentLines = new[]
            {
                new
                {
                    BaseType = 15,
                    BaseEntry = int.Parse(delivery.Key, System.Globalization.CultureInfo.InvariantCulture),
                    BaseLine = 0,
                    ItemCode = item.Code,
                    Quantity = serials.Count,
                    WarehouseCode = context.Warehouse,
                    SerialNumbers = serials.Select(serial => new
                    {
                        InternalSerialNumber = serial,
                        Quantity = 1,
                        BaseLineNumber = 0,
                    }).ToArray(),
                },
            },
        }, "DocEntry", "DocNum", cancellationToken);
        TrackCreated("Returns", result.Key, result.Number, serials, created);
    }

    private async Task<IReadOnlyList<CardState>> EnsureCardsAsync(DemoScenario scenario, DemoItem item,
        IReadOnlyList<string> serials,
        Dictionary<string, int> created, Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var cards = new List<CardState>();
        for (var index = 0; index < serials.Count; index++)
        {
            var serial = serials[index];
            var found = await client.FindOneAsync("CustomerEquipmentCards",
                "ItemCode eq " + ServiceLayerClient.Quote(item.Code) + " and InternalSerialNum eq "
                + ServiceLayerClient.Quote(serial) + " or (ItemCode eq " + ServiceLayerClient.Quote(item.Code)
                + " and InternalSerialNum eq " + ServiceLayerClient.Quote(serial.Trim()) + ")",
                cancellationToken, "EquipmentCardNum,ReplaceSN,ReplacedBySN,InstallLocation");
            var start = Date;
            var end = scenario.RecipeKeys.Contains("bulk-date") || scenario.RecipeKeys.Contains("card-date")
                ? new DateOnly(2027 + index % 2, 8, 31 - index % 3).ToString("yyyy-MM-dd")
                : "2029-08-31";
            if (found is not null)
            {
                var key = ServiceLayerClient.Scalar(found.Value, "EquipmentCardNum")!;
                // The incumbent creates a card itself when a serial is delivered; give that card the
                // scenario's marker and dates. Only cards of the tool's own YD- items reach this point.
                if (ServiceLayerClient.Scalar(found.Value, "InstallLocation") != Marker(scenario, 9))
                {
                    await client.PatchAsync("CustomerEquipmentCards(" + key + ")", new
                    {
                        ManufacturerSerialNum = DemoData.ManufacturerSerial(scenario, serial, index + 1),
                        InstallLocation = Marker(scenario, 9),
                        ContractStartDate = start,
                        ContractEndDate = end,
                    }, cancellationToken);
                    TrackCreated("CardsShaped", key, null, [serial], created);
                }
                else
                    TrackExisting("CustomerEquipmentCards", key, null, [serial], existing);
                cards.Add(new(serial, key,
                    ServiceLayerClient.Scalar(found.Value, "ReplaceSN"),
                    ServiceLayerClient.Scalar(found.Value, "ReplacedBySN")));
                continue;
            }
            var result = await client.PostAsync("CustomerEquipmentCards", new
            {
                CustomerCode = DemoData.CustomerFor(scenario),
                ItemCode = item.Code,
                InternalSerialNum = serial,
                ManufacturerSerialNum = DemoData.ManufacturerSerial(scenario, serial, index + 1),
                StatusOfSerialNumber = "sns_Active",
                InstallLocation = Marker(scenario, 9),
                ContractStartDate = start,
                ContractEndDate = end,
            }, "EquipmentCardNum", null, cancellationToken);
            TrackCreated("CustomerEquipmentCards", result.Key, null, [serial], created);
            cards.Add(new(serial, result.Key, null, null));
        }
        return cards;
    }

    private async Task EnsureReplacementLinksAsync(DemoScenario scenario, IReadOnlyList<CardState> cards,
        Dictionary<string, int> created, Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var pairs = scenario.RecipeKeys.Contains("replacement-bulk") ? cards.Count / 2 : 1;
        for (var index = 0; index < pairs; index++)
        {
            var oldCard = cards[index];
            var replacement = cards[index + pairs];
            if (oldCard.ReplacedBy == replacement.Key && replacement.Replaces == oldCard.Key)
            {
                TrackExisting("ReplacementLinks", oldCard.Key + "->" + replacement.Key, null,
                    [oldCard.Serial, replacement.Serial], existing);
                continue;
            }
            await client.PatchAsync("CustomerEquipmentCards(" + oldCard.Key + ")",
                new { ReplacedBySN = int.Parse(replacement.Key, System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken);
            await client.PatchAsync("CustomerEquipmentCards(" + replacement.Key + ")",
                new { ReplaceSN = int.Parse(oldCard.Key, System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken);
            TrackCreated("ReplacementLinks", oldCard.Key + "->" + replacement.Key, null,
                [oldCard.Serial, replacement.Serial], created);
        }
    }

    private async Task EnsureContractAsync(DemoScenario scenario, DemoItem item, IReadOnlyList<string> serials,
        bool addOn, ServiceContext context, Dictionary<string, int> created, Dictionary<string, int> existing,
        List<string> unsupported, CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, addOn ? 8 : 5);
        if (!context.EntitySets.Contains("ServiceContracts"))
        {
            unsupported.Add($"{marker}: the supported API does not expose service contracts");
            return;
        }
        // The incumbent replaces a contract's Description with its template name, so the marker lives in Remarks;
        // Remarks is long text, which the incumbent's interface can pattern-match but not compare for equality.
        var remarks = Comment(scenario) + " " + marker;
        var found = await client.FindOneAsync("ServiceContracts",
            "endswith(Remarks, " + ServiceLayerClient.Quote(" " + marker) + ")", cancellationToken, "ContractID");
        if (found is not null)
        {
            var key = ServiceLayerClient.Scalar(found.Value, "ContractID")!;
            TrackExisting("ServiceContracts", key, key, serials, existing);
            return;
        }
        var template = DemoData.ContractTemplateFor(scenario, addOn);
        var end = !addOn && DemoData.NeedsSecondContract(scenario) ? "2026-10-21"
            : template.Duration == 12 ? "2027-08-31" : template.Duration == 48 ? "2030-08-31" : "2029-08-31";
        var contractSerials = (scenario.MarkerTicket is "T48" or "T64" or "T72") && addOn
            ? serials.Take(1).ToArray() : serials.ToArray();
        var result = await client.PostAsync("ServiceContracts", new
        {
            CustomerCode = DemoData.CustomerFor(scenario),
            Description = marker,
            Remarks = remarks,
            ContractTemplate = template.Name,
            ContractType = "ct_SerialNumber",
            ServiceType = "bst_Warranty",
            Status = "scs_Approved",
            DurationOfCoverage = template.Duration,
            StartDate = addOn ? "2026-10-01" : Date,
            EndDate = end,
            ResolutionTime = template.ResolutionTime,
            ResolutionUnit = template.ResolutionUnit,
            ResponseTime = template.ResponseTime,
            ResponseUnit = template.ResponseUnit,
            IncludeParts = template.Parts ? "tYES" : "tNO",
            IncludeLabor = template.Labour ? "tYES" : "tNO",
            IncludeTravel = template.Travel ? "tYES" : "tNO",
            MondayEnabled = "tYES",
            TuesdayEnabled = "tYES",
            WednesdayEnabled = "tYES",
            ThursdayEnabled = "tYES",
            FridayEnabled = "tYES",
            ServiceContract_Lines = contractSerials.Select((serial, index) => new
            {
                ItemCode = item.Code,
                InternalSerialNum = serial,
                ManufacturerSerialNum = DemoData.ManufacturerSerial(scenario, serial, index + 1),
                StartDate = addOn ? "2026-10-01" : Date,
                EndDate = end,
            }).ToArray(),
        }, "ContractID", "ContractID", cancellationToken);
        TrackCreated("ServiceContracts", result.Key, result.Number, contractSerials, created);
    }

    private async Task EnsureProductionOrderAsync(DemoScenario scenario, DemoItem item,
        ServiceContext context, Dictionary<string, int> created, Dictionary<string, int> existing,
        List<string> unsupported, CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 7);
        if (!context.EntitySets.Contains("ProductionOrders"))
        {
            unsupported.Add($"{marker}: the supported API does not expose production orders");
            return;
        }
        var remarks = Comment(scenario) + " " + marker;
        var found = await client.FindOneAsync("ProductionOrders",
            "Remarks eq " + ServiceLayerClient.Quote(remarks), cancellationToken,
            "AbsoluteEntry,DocumentNumber");
        if (found is not null)
        {
            var key = ServiceLayerClient.Scalar(found.Value, "AbsoluteEntry")!;
            TrackExisting("ProductionOrders", key,
                ServiceLayerClient.Scalar(found.Value, "DocumentNumber"), DemoData.Serials(scenario), existing);
            return;
        }
        var result = await client.PostAsync("ProductionOrders", new
        {
            ItemNo = item.Code,
            PlannedQuantity = DemoData.UnitCount(scenario),
            PostingDate = Date,
            DueDate = "2026-09-15",
            Warehouse = context.Warehouse,
            ProductionOrderType = "bopotSpecial",
            Remarks = remarks,
            // A special order needs component rows; manual issue so nothing is consumed while it stays planned.
            ProductionOrderLines = (item.Code == "YD-MAINBOARD" ? new[] { "YD-PSU" } : new[] { "YD-MAINBOARD", "YD-PSU" })
                .Select(component => new
                {
                    ItemNo = component,
                    BaseQuantity = 1,
                    PlannedQuantity = DemoData.UnitCount(scenario),
                    ProductionOrderIssueType = "im_Manual",
                    Warehouse = context.Warehouse,
                }).ToArray(),
        }, "AbsoluteEntry", "DocumentNumber", cancellationToken);
        TrackCreated("ProductionOrders", result.Key, result.Number, DemoData.Serials(scenario), created);
    }

    private async Task EnsureServiceCallAsync(DemoScenario scenario, DemoItem item, string serial,
        Dictionary<string, int> created, Dictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var marker = Marker(scenario, 6);
        var found = await client.FindOneAsync("ServiceCalls", "Subject eq " + ServiceLayerClient.Quote(marker),
            cancellationToken, "ServiceCallID");
        if (found is not null)
        {
            var key = ServiceLayerClient.Scalar(found.Value, "ServiceCallID")!;
            TrackExisting("ServiceCalls", key, null, [serial], existing);
            return;
        }
        var result = await client.PostAsync("ServiceCalls", new
        {
            CustomerCode = DemoData.CustomerFor(scenario),
            Subject = marker,
            Description = Comment(scenario),
            ItemCode = item.Code,
            InternalSerialNum = serial,
            Resolution = "Inspected the unit, confirmed the marked scenario and recorded the corrective action.",
        }, "ServiceCallID", null, cancellationToken);
        TrackCreated("ServiceCalls", result.Key, null, [serial], created);
    }

    private async Task<CreateResult?> FindDocumentAsync(string collection, string marker,
        CancellationToken cancellationToken)
    {
        var found = await client.FindOneAsync(collection, "NumAtCard eq " + ServiceLayerClient.Quote(marker),
            cancellationToken, "DocEntry,DocNum");
        if (found is null) return null;
        return new(ServiceLayerClient.Scalar(found.Value, "DocEntry")!,
            ServiceLayerClient.Scalar(found.Value, "DocNum"));
    }

    private async Task<RemoteObject?> FindPlannedAsync(DemoObject planned, CancellationToken cancellationToken)
    {
        JsonElement? found;
        switch (planned.Type)
        {
            case "Items":
                found = await client.FindOneAsync("Items", "ItemCode eq " + ServiceLayerClient.Quote(planned.Key),
                    cancellationToken, "ItemCode");
                return found is null ? null : new(planned.Type, planned.Key, null, []);
            case "BusinessPartners":
                found = await client.FindOneAsync("BusinessPartners", "CardCode eq " + ServiceLayerClient.Quote(planned.Key),
                    cancellationToken, "CardCode");
                return found is null ? null : new(planned.Type, planned.Key, null, []);
            case "ContractTemplates":
                found = await client.FindOneAsync("ContractTemplates",
                    "TemplateName eq " + ServiceLayerClient.Quote(planned.Key), cancellationToken,
                    "TemplateName");
                return found is null ? null : new(planned.Type, planned.Key, null, []);
            case "ProductTrees":
                found = await client.FindOneAsync("ProductTrees",
                    "TreeCode eq " + ServiceLayerClient.Quote(planned.Key), cancellationToken, "TreeCode");
                return found is null ? null : new(planned.Type, planned.Key, null, []);
            case "PurchaseDeliveryNotes" or "Orders" or "DeliveryNotes" or "Returns":
                var document = await FindDocumentAsync(planned.Type, planned.Key, cancellationToken);
                return document is null ? null : new(planned.Type, document.Key, document.Number, planned.Serials);
            case "CustomerEquipmentCards":
                found = await client.FindOneAsync("CustomerEquipmentCards",
                    "InternalSerialNum eq " + ServiceLayerClient.Quote(planned.Key), cancellationToken,
                    "EquipmentCardNum");
                return found is null ? null : new(planned.Type,
                    ServiceLayerClient.Scalar(found.Value, "EquipmentCardNum")!, null, planned.Serials);
            case "ReplacementLinks":
                var oldCard = await client.FindOneAsync("CustomerEquipmentCards",
                    "InternalSerialNum eq " + ServiceLayerClient.Quote(planned.Serials[0].Trim()),
                    cancellationToken, "EquipmentCardNum,ReplacedBySN");
                var newCard = await client.FindOneAsync("CustomerEquipmentCards",
                    "InternalSerialNum eq " + ServiceLayerClient.Quote(planned.Serials[1].Trim()),
                    cancellationToken, "EquipmentCardNum,ReplaceSN");
                if (oldCard is null || newCard is null) return null;
                var oldKey = ServiceLayerClient.Scalar(oldCard.Value, "EquipmentCardNum")!;
                var newKey = ServiceLayerClient.Scalar(newCard.Value, "EquipmentCardNum")!;
                return ServiceLayerClient.Scalar(oldCard.Value, "ReplacedBySN") == newKey
                    && ServiceLayerClient.Scalar(newCard.Value, "ReplaceSN") == oldKey
                    ? new(planned.Type, oldKey + "->" + newKey, null, planned.Serials) : null;
            case "ServiceCalls":
                found = await client.FindOneAsync("ServiceCalls", "Subject eq " + ServiceLayerClient.Quote(planned.Key),
                    cancellationToken, "ServiceCallID");
                return found is null ? null : new(planned.Type,
                    ServiceLayerClient.Scalar(found.Value, "ServiceCallID")!, null, planned.Serials);
            case "ServiceContracts":
                found = await client.FindOneAsync("ServiceContracts",
                    "endswith(Remarks, " + ServiceLayerClient.Quote(" " + planned.Key) + ")", cancellationToken,
                    "ContractID");
                return found is null ? null : new(planned.Type,
                    ServiceLayerClient.Scalar(found.Value, "ContractID")!,
                    ServiceLayerClient.Scalar(found.Value, "ContractID"), planned.Serials);
            case "ProductionOrders":
                found = await client.FindOneAsync("ProductionOrders",
                    "Remarks eq " + ServiceLayerClient.Quote(Comment(ScenarioCatalogue.All.Single(value =>
                        value.MarkerTicket == planned.Ticket)) + " " + planned.Key), cancellationToken,
                    "AbsoluteEntry,DocumentNumber");
                return found is null ? null : new(planned.Type,
                    ServiceLayerClient.Scalar(found.Value, "AbsoluteEntry")!,
                    ServiceLayerClient.Scalar(found.Value, "DocumentNumber"), planned.Serials);
            default:
                return null;
        }
    }

    private void TrackCreated(string type, string key, string? number, IReadOnlyList<string> serials,
        Dictionary<string, int> counts)
    {
        counts[type] = counts.GetValueOrDefault(type) + 1;
        observed.Add(new(type, key, number, serials));
    }

    private void TrackExisting(string type, string key, string? number, IReadOnlyList<string> serials,
        Dictionary<string, int> counts)
    {
        counts[type] = counts.GetValueOrDefault(type) + 1;
        observed.Add(new(type, key, number, serials));
    }

    private static string Marker(DemoScenario scenario, int number) =>
        "YD-" + scenario.MarkerTicket + "-" + number;
    private static string Comment(DemoScenario scenario) => "Yojana demo scenario " + scenario.MarkerTicket;

    private sealed record CardState(string Serial, string Key, string? Replaces, string? ReplacedBy);

    [GeneratedRegex("<EntitySet\\b[^>]*\\bName=\"([^\"]+)\"")]
    private static partial Regex EntitySetRegex();
}
