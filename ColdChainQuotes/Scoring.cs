namespace ColdChainQuotes;

// CreateScore : weightFactor -> distanceFactor -> (Shipment -> score).
// Each call fixes one configuration value. Partial application gives a configured scoring function;
// nothing is stored in a mutable setting.
public static class Scoring
{
    public static readonly Func<decimal, Func<decimal, Func<Shipment, decimal>>> CreateScore =
        weightFactor => distanceFactor => shipment => weightFactor * shipment.WeightKg + distanceFactor * shipment.DistanceKm;
}
