using System.Globalization;

namespace ColdChainQuotes;

public static class Program
{
    const decimal MaxDistanceKm = 300m;

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return Run(args, Console.In, Console.Out);
    }

    // One entry point for every command. The first word picks the workflow; the rest goes to it.
    // Lifecycle: book (price, confirm) -> dispatch -> deliver -> invoice. "status" shows every booking.
    public static int Run(string[] args, TextReader input, TextWriter output)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();
        return command switch
        {
            "quote" => RunQuotes(rest),
            "book" => RunBookings(rest),
            "result" => ResultConsole.Run(rest, output),
            "report" => ReportConsole.Run(rest, output),
            "dispatch" => DispatchConsole.Run(rest, output),
            "deliver" => DeliveryConsole.Run(rest, output),
            "invoice" => InvoiceConsole.Run(rest, output),
            "confirm" => StateConsole.Confirm(rest, output),
            "status" => StateConsole.Status(rest, output),
            "reset" => StateConsole.Reset(rest, output),
            "menu" => Menu.Run(input, output, a => Run(a, input, output)),
            "help" or "--help" or "-h" => Help(output),
            // Assignment 5 compatibility: "dotnet run -- shipments.txt express" still runs the quote report.
            _ => RunQuotes(args)
        };
    }

    public static int Help(TextWriter output)
    {
        output.WriteLine("ColdChainQuotes: refrigerated delivery quotes, bookings, dispatch, delivery checks and invoices");
        output.WriteLine();
        output.WriteLine("  quote [file] [standard|express|both]   W1 quote pipeline (default shipments.txt, both)");
        output.WriteLine("  book [shipments] [actions]             W2 apply price/confirm actions from a file");
        output.WriteLine("  result [json] [id] [policy]            W2 price+confirm one booking with Result (demo without id)");
        output.WriteLine("  confirm <id> [standard|express]       W2 price and confirm one booking in the saved state");
        output.WriteLine("  dispatch <id> <vehicle> | dispatch auto <id> [firstfit|bestfit] | dispatch fleet   W3");
        output.WriteLine("  deliver <id> [strict|tolerant] [readings] | deliver report                      W4");
        output.WriteLine("  invoice <customer> [loyalty|bulk] [--export file]                               W5");
        output.WriteLine("  report [csv] [--export file]           revenue and cold-store priority reports");
        output.WriteLine("  status                                 every saved booking and its state");
        output.WriteLine("  reset                                  start again from samples/shipments.json");
        output.WriteLine("  menu                                   numbered menu for the commands above");
        output.WriteLine();
        output.WriteLine("Saved-state commands (confirm, dispatch, deliver, invoice, status, reset) read and write");
        output.WriteLine($"{StateStore.DefaultStatePath}; add --state <path> or set CCQ_STATE to use another file.");
        return 0;
    }

    static int RunQuotes(string[] args)
    {
        var inputPath = args.Length > 0 ? args[0] : "shipments.txt";
        var policyName = args.Length > 1 ? args[1].ToLowerInvariant() : "both";

        // The policy is chosen here, outside the pipeline, and passed in as an argument.
        var policies = SelectPolicies(policyName);
        if (policies.Count == 0)
        {
            Console.WriteLine($"Unknown policy '{policyName}'. Use standard, express or both.");
            return 1;
        }

        // A missing file is an error. An existing empty file is a valid dataset with no shipments.
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Input file not found: {Path.GetFullPath(inputPath)}");
            return 1;
        }

        // I/O: read the raw text.
        var lines = File.ReadAllLines(inputPath);

        // Pure core: parse, then run the pipeline once per policy.
        var imported = Parsing.ParseLines(lines);
        var results = policies
            .Select(p => (p.Name, Summary: Workflow.Run(imported.Shipments, p.Price, MaxDistanceKm)))
            .ToList();
        var notServed = imported.Shipments.Where(s => !Stages.IsServiceable(s, MaxDistanceKm)).ToList();

        // I/O: show the finished results.
        Console.WriteLine("Cold chain delivery quotes");
        Console.WriteLine($"Input: {inputPath}   Max distance: {MaxDistanceKm} km");

        Console.WriteLine();
        Console.WriteLine("Rejected lines");
        if (imported.Errors.Length == 0)
            Console.WriteLine("  none");
        foreach (var e in imported.Errors)
            Console.WriteLine($"  line {e.LineNumber,-3} {e.Message}");

        Console.WriteLine();
        Console.WriteLine($"Not served (over {MaxDistanceKm} km)");
        if (notServed.Count == 0)
            Console.WriteLine("  none");
        foreach (var s in notServed)
            Console.WriteLine($"  {s.Id,-4} {s.Customer,-10} {s.DistanceKm} km");

        foreach (var (name, summary) in results)
        {
            Console.WriteLine();
            Console.WriteLine($"{name} policy");
            if (summary.Quotes.Length == 0)
                Console.WriteLine("  no quotes");
            foreach (var q in summary.Quotes)
                Console.WriteLine($"  {q.ShipmentId,-4} {q.Customer,-10} {q.Price,8:0.##}");
            Console.WriteLine($"  Total           {summary.Total,8:0.##}   ({summary.Quotes.Length} quotes)");
        }

        return 0;
    }

    // dotnet run -- book [shipments file] [actions file]
    static int RunBookings(string[] args)
    {
        var shipmentsPath = args.Length > 0 ? args[0] : "shipments.txt";
        var actionsPath = args.Length > 1 ? args[1] : "actions.txt";

        foreach (var path in new[] { shipmentsPath, actionsPath })
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Input file not found: {Path.GetFullPath(path)}");
                return 1;
            }
        }

        // I/O: read both files.
        var shipmentLines = File.ReadAllLines(shipmentsPath);
        var actionLines = File.ReadAllLines(actionsPath);

        // Pure core: validated shipments -> open bookings -> every action applied in order.
        var imported = Parsing.ParseLines(shipmentLines);
        var start = Ledger.Open(imported.Shipments, MaxDistanceKm);
        var (final, results) = Actions.Run(start, actionLines);

        // I/O: show the finished results.
        Console.WriteLine("Cold chain bookings");
        Console.WriteLine($"Shipments: {shipmentsPath}   Actions: {actionsPath}   Max distance: {MaxDistanceKm} km");
        Console.WriteLine($"Rejected shipment lines: {imported.Errors.Length}");
        Console.WriteLine();
        Console.WriteLine("Open bookings: " + (start.Bookings.Count == 0 ? "none" : string.Join(", ", start.Bookings.Keys)));

        Console.WriteLine();
        Console.WriteLine("Actions");
        if (results.Length == 0)
            Console.WriteLine("  none");
        foreach (var result in results)
        {
            var (lineNumber, text) = result switch
            {
                ActionResult.Applied applied => (applied.LineNumber, applied.Text),
                ActionResult.NotUnderstood notUnderstood => (notUnderstood.LineNumber, notUnderstood.Text),
                _ => (0, "")
            };
            Console.WriteLine($"  line {lineNumber,-3} {text,-20} {Actions.Describe(result)}");
        }

        Console.WriteLine();
        Console.WriteLine("Final bookings");
        if (final.Bookings.Count == 0)
            Console.WriteLine("  none");
        foreach (var booking in final.Bookings.Values)
            Console.WriteLine($"  {booking.Shipment.Id,-4} {Actions.DescribeState(booking)}");

        return 0;
    }

    static IReadOnlyList<(string Name, Func<Shipment, decimal> Price)> SelectPolicies(string name) => name switch
    {
        "standard" => [("Standard", PricePolicies.Standard)],
        "express" => [("Express", PricePolicies.Express)],
        "both" => [("Standard", PricePolicies.Standard), ("Express", PricePolicies.Express)],
        _ => []
    };
}
