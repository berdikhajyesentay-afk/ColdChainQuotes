using System.Globalization;

namespace ColdChainQuotes;

public static class Program
{
    const decimal MaxDistanceKm = 300m;

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length > 0 && args[0].Equals("result", StringComparison.OrdinalIgnoreCase))
            return ResultConsole.Run(args.Skip(1).ToArray(), Console.Out);

        if (args.Length > 0 && args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
            return ReportConsole.Run(args.Skip(1).ToArray(), Console.Out);

        // "book" switches to the booking workflow (Assignment 6); anything else is the quote report.
        if (args.Length > 0 && args[0].Equals("book", StringComparison.OrdinalIgnoreCase))
            return RunBookings(args.Skip(1).ToArray());

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
