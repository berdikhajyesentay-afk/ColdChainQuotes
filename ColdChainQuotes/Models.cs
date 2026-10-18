using System.Collections.Immutable;

namespace ColdChainQuotes;

public enum StorageClass { Cold, Frozen }

// A valid shipment request. Only Parsing creates these from text, so every field is already checked.
public sealed record Shipment(
    string Id,
    string Customer,
    StorageClass StorageClass,
    decimal WeightKg,
    decimal DistanceKm);

public sealed record PricedShipment(Shipment Shipment, decimal Price);

public sealed record Quote(string ShipmentId, string Customer, decimal Price);

// ImmutableArray is a real copy: nobody can add or remove quotes after the summary is built.
public sealed record QuoteSummary(ImmutableArray<Quote> Quotes, decimal Total);

public sealed record ParseError(int LineNumber, string RawLine, string Message);

public sealed record ImportResult(ImmutableArray<Shipment> Shipments, ImmutableArray<ParseError> Errors);
