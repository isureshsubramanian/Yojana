namespace Yojana.DemoIncumbent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var options = DemoOptions.FromEnvironment();
            await using var client = new ServiceLayerClient(options);
            await client.LoginAsync(CancellationToken.None);
            var seeder = new DemoSeeder(client);
            return args[0] switch
            {
                "plan" when args.Length == 1 => await PlanAsync(seeder),
                "apply" when args.Length == 1 => await ApplyAsync(seeder),
                "report" when args.Length == 2 => await ReportAsync(seeder, options.Company, args[1]),
                _ => Invalid(),
            };
        }
        catch (ServiceLayerException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> PlanAsync(DemoSeeder seeder)
    {
        var context = await seeder.ContextAsync(CancellationToken.None);
        var objects = await seeder.ObservePlanAsync(CancellationToken.None);
        Console.WriteLine("PLAN scenarios={0} ticket_ids={1} data={2} product_only={3}",
            ScenarioCatalogue.All.Count, ScenarioCatalogue.TicketIds.Count,
            ScenarioCatalogue.All.Count(value => value.Disposition == DemoDisposition.Data),
            ScenarioCatalogue.All.Count(value => value.Disposition == DemoDisposition.ProductOnly));
        Console.WriteLine("CONTEXT warehouse={0} item_group={1}", context.Warehouse, context.ItemGroup);
        foreach (var capability in new[] { "ServiceContracts", "ContractTemplates", "ProductionOrders",
                     "ProductTrees", "CustomerEquipmentCards", "ServiceCalls" })
            Console.WriteLine("CAPABILITY {0}={1}", capability,
                context.EntitySets.Contains(capability) ? "yes" : "no");
        Console.WriteLine("SHARED marked master data");
        foreach (var item in objects.Where(value => value.Ticket == "shared"))
            Console.WriteLine("  {0} {1} key={2} serials={3}", item.Exists ? "EXISTS" : "CREATE",
                item.Type, item.Key, item.Serials.Count);
        foreach (var scenario in ScenarioCatalogue.All)
        {
            Console.WriteLine("{0} [{1}] {2}", string.Join('/', scenario.Tickets),
                scenario.Disposition == DemoDisposition.ProductOnly ? "product-only" : "data", scenario.Scenario);
            var rows = objects.Where(value => value.Ticket == scenario.MarkerTicket).ToArray();
            if (rows.Length == 0) Console.WriteLine("  SHOW product action using shared marked units");
            foreach (var item in rows)
                Console.WriteLine("  {0} {1} key={2} serials={3}", item.Exists ? "EXISTS" : "CREATE",
                    item.Type, item.Key, item.Serials.Count);
        }
        PrintSummary("PLAN SUMMARY create", objects.Where(value => !value.Exists));
        PrintSummary("PLAN SUMMARY exists", objects.Where(value => value.Exists));
        return 0;
    }

    private static async Task<int> ApplyAsync(DemoSeeder seeder)
    {
        var context = await seeder.ContextAsync(CancellationToken.None);
        var summary = await seeder.ApplyAsync(context, CancellationToken.None);
        PrintSummary("APPLY CREATED", summary.Created);
        PrintSummary("APPLY EXISTING", summary.Existing);
        foreach (var item in summary.Unsupported) Console.WriteLine("UNSUPPORTED {0}", item);
        Console.WriteLine("APPLY SUMMARY created={0} existing={1} unsupported={2}",
            summary.Created.Values.Sum(), summary.Existing.Values.Sum(), summary.Unsupported.Count);
        return 0;
    }

    private static async Task<int> ReportAsync(DemoSeeder seeder, string company, string path)
    {
        var objects = await seeder.ObservePlanAsync(CancellationToken.None);
        var written = await ManifestWriter.WriteAsync(path, company, objects, CancellationToken.None);
        Console.WriteLine("REPORT json={0}", written.JsonPath);
        Console.WriteLine("REPORT markdown={0}", written.MarkdownPath);
        Console.WriteLine("REPORT present={0} planned={1}", objects.Count(value => value.Exists),
            objects.Count(value => !value.Exists));
        return 0;
    }

    private static int Invalid()
    {
        Usage();
        return 2;
    }

    private static void Usage()
    {
        Console.WriteLine("Usage: Yojana.DemoIncumbent plan | apply | report <manifest-path>");
        Console.WriteLine("Required environment: YOJANA_CX01_URL, YOJANA_CX01_USER, YOJANA_CX01_PASSWORD,");
        Console.WriteLine("YOJANA_CX01_CERT_SHA256 and YOJANA_DEMO_COMPANY (or a company-qualified user).");
    }

    private static void PrintSummary(string heading, IEnumerable<DemoObject> objects)
    {
        var counts = objects.GroupBy(value => value.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        PrintSummary(heading, counts);
    }

    private static void PrintSummary(string heading, IReadOnlyDictionary<string, int> counts)
    {
        Console.WriteLine("{0}: {1}", heading, counts.Count == 0 ? "none" : string.Join(", ",
            counts.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => value.Key + "=" + value.Value)));
    }
}
