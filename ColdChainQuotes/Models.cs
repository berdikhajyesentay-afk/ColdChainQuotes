using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

public enum StorageClass { Cold, Frozen }

// A valid shipment request. The constructor is private and the properties have no setters,
// so Create is the only way to get one: every Shipment has passed the rules below.
public sealed record Shipment
{
    public const decimal MaxWeightKg = 10_000m;
    public const decimal MaxDistanceKm = 20_000m;

    public string Id { get; }
    public string Customer { get; }
    public StorageClass StorageClass { get; }
    public decimal WeightKg { get; }
    public decimal DistanceKm { get; }

    private Shipment(string id, string customer, StorageClass storageClass, decimal weightKg, decimal distanceKm)
    {
        Id = id;
        Customer = customer;
        StorageClass = storageClass;
        WeightKg = weightKg;
        DistanceKm = distanceKm;
    }

    // The factory: checks every rule and returns Valid with a Shipment, or Invalid with all the reasons.
    public static ShipmentCheck Create(string id, string customer, StorageClass storageClass, decimal weightKg, decimal distanceKm)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();

        if (string.IsNullOrWhiteSpace(id))
            reasons.Add("shipment id is missing");
        if (string.IsNullOrWhiteSpace(customer))
            reasons.Add("customer is missing");
        if (!Enum.IsDefined(storageClass))
            reasons.Add($"'{(int)storageClass}' is not a declared StorageClass (Cold or Frozen)");

        if (weightKg <= 0)
            reasons.Add(Invariant($"weight must be greater than 0 kg, found {weightKg}"));
        else if (weightKg > MaxWeightKg)
            reasons.Add(Invariant($"weight must be at most {MaxWeightKg} kg, found {weightKg}"));

        if (distanceKm < 0)
            reasons.Add(Invariant($"distance cannot be negative, found {distanceKm}"));
        else if (distanceKm > MaxDistanceKm)
            reasons.Add(Invariant($"distance must be at most {MaxDistanceKm} km, found {distanceKm}"));

        return reasons.Count == 0
            ? new ShipmentCheck.Valid(new Shipment(id, customer, storageClass, weightKg, distanceKm))
            : new ShipmentCheck.Invalid(reasons.ToImmutable());
    }

    // Numbers in messages always use a dot, whatever language the computer is set to.
    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

// The factory's outcome. The private constructor closes the hierarchy: only these two cases exist.
public abstract record ShipmentCheck
{
    private ShipmentCheck() { }

    // Every rule passed: carries the usable Shipment.
    public sealed record Valid(Shipment Shipment) : ShipmentCheck;

    // At least one rule failed: carries every reason, and no Shipment.
    public sealed record Invalid(ImmutableArray<string> Reasons) : ShipmentCheck;
}

public sealed record PricedShipment(Shipment Shipment, decimal Price);

public sealed record Quote(string ShipmentId, string Customer, decimal Price);

// ImmutableArray is a real copy: nobody can add or remove quotes after the summary is built.
public sealed record QuoteSummary(ImmutableArray<Quote> Quotes, decimal Total);

public sealed record ParseError(int LineNumber, string RawLine, string Message);

public sealed record ImportResult(ImmutableArray<Shipment> Shipments, ImmutableArray<ParseError> Errors);
