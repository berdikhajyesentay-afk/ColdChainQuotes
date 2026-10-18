using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ColdChainQuotes;

public static class Parsing
{
    const NumberStyles NumberStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public static bool TryParseStorageClass(string text, out StorageClass storageClass)
    {
        // Enum.TryParse turns "Cold,Frozen" into Cold | Frozen, which equals the declared Frozen.
        if (text.Contains(','))
        {
            storageClass = default;
            return false;
        }

        return Enum.TryParse(text, ignoreCase: true, out storageClass) && Enum.IsDefined(storageClass);
    }

    public static bool TryParseNumber(string text, out decimal value)
    {
        return decimal.TryParse(text, NumberStyle, CultureInfo.InvariantCulture, out value);
    }

    // Line format: Id|Customer|StorageClass|WeightKg|DistanceKm
    public static bool TryParseShipment(string line, [NotNullWhen(true)] out Shipment? shipment, out string error)
    {
        shipment = null;

        var parts = line.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length != 5)
        {
            error = $"expected 5 fields separated by '|', found {parts.Length}";
            return false;
        }

        var (id, customer, classText, weightText, distanceText) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        if (id.Length == 0)
        {
            error = "shipment id is missing";
            return false;
        }

        if (customer.Length == 0)
        {
            error = "customer is missing";
            return false;
        }

        if (classText.Length == 0)
        {
            error = "storage class is missing";
            return false;
        }

        if (!TryParseStorageClass(classText, out var storageClass))
        {
            error = $"'{classText}' is not a declared StorageClass (Cold or Frozen)";
            return false;
        }

        if (!TryReadNumber("weight", weightText, out var weightKg, out error))
            return false;

        if (weightKg <= 0)
        {
            error = $"weight must be greater than 0 kg, found {weightKg}";
            return false;
        }

        if (!TryReadNumber("distance", distanceText, out var distanceKm, out error))
            return false;

        // 0 km is a real value (pickup and delivery at the same site), so only negatives are rejected.
        if (distanceKm < 0)
        {
            error = $"distance cannot be negative, found {distanceKm}";
            return false;
        }

        shipment = new Shipment(id, customer, storageClass, weightKg, distanceKm);
        error = "";
        return true;
    }

    // null means the input itself is missing, which is an error.
    // An empty sequence is a valid dataset with no shipments.
    public static ImportResult ParseLines(IEnumerable<string>? lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var shipments = ImmutableArray.CreateBuilder<Shipment>();
        var errors = ImmutableArray.CreateBuilder<ParseError>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!TryParseShipment(line, out var shipment, out var error))
                errors.Add(new ParseError(lineNumber, line, error));
            else if (!seenIds.Add(shipment.Id))
                errors.Add(new ParseError(lineNumber, line, $"shipment id {shipment.Id} appears more than once"));
            else
                shipments.Add(shipment);
        }

        return new ImportResult(shipments.ToImmutable(), errors.ToImmutable());
    }

    static bool TryReadNumber(string fieldName, string text, out decimal value, out string error)
    {
        value = 0;

        if (text.Length == 0)
        {
            error = $"{fieldName} is missing";
            return false;
        }

        if (!TryParseNumber(text, out value))
        {
            error = $"{fieldName} '{text}' is not a valid number";
            return false;
        }

        error = "";
        return true;
    }
}
