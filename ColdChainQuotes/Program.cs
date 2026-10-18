using System.Globalization;

namespace ColdChainQuotes;

public static class Program
{
    const decimal MaxDistanceKm = 300m;

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

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

    static IReadOnlyList<(string Name, Func<Shipment, decimal> Price)> SelectPolicies(string name) => name switch
    {
        "standard" => [("Standard", PricePolicies.Standard)],
        "express" => [("Express", PricePolicies.Express)],
        "both" => [("Standard", PricePolicies.Standard), ("Express", PricePolicies.Express)],
        _ => []
    };
}
