namespace ColdChainQuotes;

public sealed record CustomerRevenue(string Customer, int Shipments, decimal WeightKg, decimal Revenue);

public sealed record PriorityLine(int Rank, string Id, string Customer, decimal DistanceKm, decimal Score);

// Pure report queries: any sequence in, a materialised and deterministically ordered list out.
public static class Reports
{
    // Served shipments grouped by customer. Largest revenue first; equal revenue sorted by customer name.
    public static IReadOnlyList<CustomerRevenue> RevenueByCustomer(
        IEnumerable<Shipment> shipments, decimal maxDistanceKm, Func<Shipment, decimal> policy) =>
        shipments
            .Where(s => Stages.IsServiceable(s, maxDistanceKm))
            .GroupBy(s => s.Customer, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CustomerRevenue(g.Key, g.Count(), g.Sum(s => s.WeightKg), g.Sum(policy)))
            .OrderByDescending(r => r.Revenue)
            .ThenBy(r => r.Customer, StringComparer.Ordinal)
            .ToList();

    // Served Cold shipments ranked by a configured score. Equal scores sorted by shipment id.
    public static IReadOnlyList<PriorityLine> ColdPriority(
        IEnumerable<Shipment> shipments, decimal maxDistanceKm, Func<Shipment, decimal> score) =>
        shipments
            .ByStorageClass(StorageClass.Cold)
            .Where(s => Stages.IsServiceable(s, maxDistanceKm))
            .Select(s => (Shipment: s, Score: score(s)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Shipment.Id, StringComparer.Ordinal)
            .Select((x, index) => new PriorityLine(index + 1, x.Shipment.Id, x.Shipment.Customer, x.Shipment.DistanceKm, x.Score))
            .ToList();
}
