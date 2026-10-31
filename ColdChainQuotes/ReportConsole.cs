using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// Console and export boundary for the two reports. The queries stay pure; the native tier lookup,
// text rendering and file output happen here.
public static class ReportConsole
{
    const decimal MaxDistanceKm = 300m;

    public static int Run(string[] args, TextWriter output)
    {
        var path = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "samples/shipments.csv";
        var exportAt = Array.IndexOf(args, "--export");
        var exportPath = exportAt >= 0 && exportAt + 1 < args.Length ? args[exportAt + 1] : null;

        var loaded = CsvBoundary.Load(path)
            .Require(rows => rows.Length > 0, new AppError("empty_dataset", $"'{path}' has no shipment rows."));
        switch (loaded)
        {
            case Result<ImmutableArray<Shipment>>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            case Result<ImmutableArray<Shipment>>.Success success:
                var lines = Render(success.Value);
                foreach (var line in lines)
                    output.WriteLine(line);
                return exportPath is null ? 0 : Export(exportPath, lines, output);
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    public static IReadOnlyList<string> Render(IEnumerable<Shipment> shipments)
    {
        var all = shipments.ToArray();
        var lines = new List<string>();

        var revenue = Reports.RevenueByCustomer(all, MaxDistanceKm, PricePolicies.Standard);
        lines.Add("Report 1: revenue by customer (Standard policy, served shipments up to 300 km)");
        lines.Add($"{"Customer",-12} {"Shipments",9} {"WeightKg",9} {"Revenue",9}");
        foreach (var row in revenue)
            lines.Add($"{row.Customer,-12} {row.Shipments,9} {Money(row.WeightKg),9} {Money(row.Revenue),9}");
        lines.Add($"{"Total",-12} {revenue.Sum(r => r.Shipments),9} {Money(revenue.Sum(r => r.WeightKg)),9} {Money(revenue.Sum(r => r.Revenue)),9}");
        lines.Add("");

        // The same dataset, two configured scoring functions made by partial application.
        var weightFirst = Scoring.CreateScore(10m)(1m);
        var distanceFirst = Scoring.CreateScore(1m)(10m);
        lines.AddRange(PriorityReport("Report 2a: cold-store priority, weight first (10 per kg + 1 per km)", all, weightFirst));
        lines.AddRange(PriorityReport("Report 2b: cold-store priority, distance first (1 per kg + 10 per km)", all, distanceFirst));
        return lines;
    }

    static IReadOnlyList<string> PriorityReport(string title, IEnumerable<Shipment> shipments, Func<Shipment, decimal> score)
    {
        var lines = new List<string>
        {
            title,
            $"{"Rank",4} {"Id",-5} {"Customer",-12} {"Tier",-10} {"Score",8}"
        };
        foreach (var row in Reports.ColdPriority(shipments, MaxDistanceKm, score))
        {
            // Native call, made at the boundary: the tier comes from C, not from the pure query.
            var tier = NativeTier.Classify(row.DistanceKm) switch
            {
                Result<DistanceTier>.Success ok => ok.Value.ToString(),
                Result<DistanceTier>.Failure failed => $"[{failed.Error.Code}]",
                _ => throw new InvalidOperationException("Unknown Result case")
            };
            lines.Add($"{row.Rank,4} {row.Id,-5} {row.Customer,-12} {tier,-10} {Money(row.Score),8}");
        }
        lines.Add("");
        return lines;
    }

    static int Export(string path, IReadOnlyList<string> lines, TextWriter output)
    {
        switch (ReportExport.Write(path, lines))
        {
            case Result<string>.Success written:
                output.WriteLine($"Exported to {written.Value}");
                return 0;
            case Result<string>.Failure failed:
                output.WriteLine($"ERROR [{failed.Error.Code}] {failed.Error.Message}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
