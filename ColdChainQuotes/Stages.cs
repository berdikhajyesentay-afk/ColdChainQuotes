using System.Collections.Immutable;

namespace ColdChainQuotes;

// The three pure stages of the quote pipeline. None of them reads or writes files or the console,
// changes its input, or uses a clock, random value or mutable static field.
public static class Stages
{
    public static bool IsServiceable(Shipment shipment, decimal maxDistanceKm)
    {
        return shipment.DistanceKm <= maxDistanceKm;
    }

    // Stage 1: shipments -> shipments inside the service area (distance <= maxDistanceKm).
    public static ImmutableArray<Shipment> SelectServiceable(IEnumerable<Shipment> shipments, decimal maxDistanceKm)
    {
        return shipments
            .Where(s => IsServiceable(s, maxDistanceKm))
            .ToImmutableArray();
    }

    // Stage 2 (higher-order): shipments -> priced shipments. The price rule is supplied by the caller.
    public static ImmutableArray<PricedShipment> PriceShipments(
        IEnumerable<Shipment> shipments, Func<Shipment, decimal> pricePolicy)
    {
        return shipments
            .Select(s => new PricedShipment(s, pricePolicy(s)))
            .ToImmutableArray();
    }

    // Stage 3: priced shipments -> summary, most expensive first, equal prices by Id.
    public static QuoteSummary BuildSummary(IEnumerable<PricedShipment> priced)
    {
        var quotes = priced
            .OrderByDescending(p => p.Price)
            .ThenBy(p => p.Shipment.Id, StringComparer.Ordinal)
            .Select(p => new Quote(p.Shipment.Id, p.Shipment.Customer, p.Price))
            .ToImmutableArray();

        return new QuoteSummary(quotes, quotes.Sum(q => q.Price));
    }
}
