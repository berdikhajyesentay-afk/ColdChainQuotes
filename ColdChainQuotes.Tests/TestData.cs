using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Shared helpers. Tests build shipments through the factory like the application does.
static class TestData
{
    public const decimal MaxDistanceKm = 300m;

    public static Shipment Ship(string id, StorageClass storageClass, decimal weightKg, decimal distanceKm) =>
        Shipment.Create(id, "Customer " + id, storageClass, weightKg, distanceKm) switch
        {
            ShipmentCheck.Valid valid => valid.Shipment,
            ShipmentCheck.Invalid invalid => throw new InvalidOperationException(string.Join("; ", invalid.Reasons)),
            _ => throw new InvalidOperationException("unexpected factory result")
        };

    public static ImportResult SuppliedShipments() =>
        Parsing.ParseLines(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "shipments.txt")));

}
