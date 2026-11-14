using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Shared foundation helpers: build any booking state directly (tests only, via InternalsVisibleTo),
// so each workflow can be tested without running the workflows before it.
static class TestStates
{
    public static Booking.Confirmed Confirmed(Shipment shipment, int number = 1, string policy = "Standard") =>
        new(new Booking.Quoted(shipment, policy,
            policy == "Express" ? PricePolicies.Express(shipment) : PricePolicies.Standard(shipment)), number);

    public static Booking.Dispatched Dispatched(Shipment shipment, string vehicleId = "V1",
        DistanceTier tier = DistanceTier.Local, int number = 1) =>
        new(Confirmed(shipment, number), vehicleId, tier);

    public static Booking.Delivered Delivered(Shipment shipment, int readings = 3, int number = 1, string vehicleId = "V1") =>
        new(Dispatched(shipment, vehicleId, DistanceTier.Local, number), readings);

    public static Booking.Spoiled Spoiled(Shipment shipment, string reason = "reading 2 at 9.5 C is above 8 C", int number = 1) =>
        new(Dispatched(shipment, "V1", DistanceTier.Local, number), reason);

    public static Booking.Invoiced Invoiced(Shipment shipment, string invoice = "INV-0001", int number = 1) =>
        new(Delivered(shipment, 3, number), invoice);

    public static Booking.Credited Credited(Shipment shipment, string invoice = "INV-0001", int number = 1) =>
        new(Spoiled(shipment, number: number), invoice);

    // A ledger holding exactly these bookings. Counters start after the highest confirmation number.
    public static Ledger LedgerOf(params Booking[] bookings) =>
        new(bookings.ToImmutableSortedDictionary(b => b.Shipment.Id, b => b, StringComparer.OrdinalIgnoreCase),
            bookings.Length + 1);

    public static Shipment Ship(string id, string customer, StorageClass storage = StorageClass.Cold,
        decimal weightKg = 10m, decimal distanceKm = 20m) =>
        Shipment.Create(id, customer, storage, weightKg, distanceKm) switch
        {
            ShipmentCheck.Valid valid => valid.Shipment,
            ShipmentCheck.Invalid invalid => throw new InvalidOperationException(string.Join("; ", invalid.Reasons)),
            _ => throw new InvalidOperationException("unexpected factory result")
        };
}
