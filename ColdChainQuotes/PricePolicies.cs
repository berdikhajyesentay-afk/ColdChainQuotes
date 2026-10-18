namespace ColdChainQuotes;

// Two policies for the same rule: how much a shipment costs. Both fit Func<Shipment, decimal>.
public static class PricePolicies
{
    public const decimal FrozenSurcharge = 500m;

    // Standard: 1000 + 50 per km + 40 per kg, +500 for Frozen.
    public static decimal Standard(Shipment shipment)
    {
        return 1000m + 50m * shipment.DistanceKm + 40m * shipment.WeightKg + Surcharge(shipment);
    }

    // Express: 2000 + 80 per km + 10 per kg, +500 for Frozen. Distance costs more, weight costs less.
    public static decimal Express(Shipment shipment)
    {
        return 2000m + 80m * shipment.DistanceKm + 10m * shipment.WeightKg + Surcharge(shipment);
    }

    // Finds one policy by its command word. null means no policy has that name.
    public static (string Name, Func<Shipment, decimal> Price)? Find(string word) => word.ToLowerInvariant() switch
    {
        "standard" => ("Standard", Standard),
        "express" => ("Express", Express),
        _ => null
    };

    static decimal Surcharge(Shipment shipment)
    {
        return shipment.StorageClass == StorageClass.Frozen ? FrozenSurcharge : 0m;
    }
}
