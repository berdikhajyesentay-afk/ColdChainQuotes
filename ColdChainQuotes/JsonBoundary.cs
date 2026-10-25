using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainQuotes;

// File access and deserialization are confined to this application boundary.
public static class JsonBoundary
{
    sealed record ShipmentRow(string? Id, string? Customer, string? StorageClass,
        decimal? WeightKg, decimal? DistanceKm);

    static readonly JsonSerializerOptions Settings = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Result<Ledger> Load(string path)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<ShipmentRow?[]>(File.ReadAllText(path), Settings);
            if (rows is null)
                return Bad(path, "Expected an array of shipment records, found null.");

            var shipments = new List<Shipment>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row is null || row.Id is null || row.Customer is null || row.StorageClass is null
                    || row.WeightKg is null || row.DistanceKm is null)
                    return Bad(path, $"Row {i + 1}: all five shipment fields are required.");
                // Only named enum values are accepted, never numeric strings.
                if (!Enum.GetNames<StorageClass>().Any(n => n.Equals(row.StorageClass, StringComparison.OrdinalIgnoreCase)))
                    return Result<Ledger>.Fail(new("invalid_shipment", $"File '{path}', row {i + 1}, '{row.Id}': invalid storage class '{row.StorageClass}'."));
                var check = Shipment.Create(row.Id.Trim(), row.Customer.Trim(),
                    Enum.Parse<StorageClass>(row.StorageClass, true), row.WeightKg.Value, row.DistanceKm.Value);
                switch (check)
                {
                    case ShipmentCheck.Invalid invalid:
                        return Result<Ledger>.Fail(new("invalid_shipment", $"File '{path}', row {i + 1}, '{row.Id}': {string.Join("; ", invalid.Reasons)}"));
                    case ShipmentCheck.Valid valid:
                        if (!ids.Add(valid.Shipment.Id))
                            return Result<Ledger>.Fail(new("duplicate_id", $"File '{path}', row {i + 1}: duplicate shipment '{valid.Shipment.Id}'."));
                        shipments.Add(valid.Shipment);
                        break;
                }
            }
            // Build only after the entire input is valid. No external state has changed.
            return Result<Ledger>.Ok(Ledger.Open(shipments, 300m));
        }
        catch (FileNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (DirectoryNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (JsonException ex) { return Bad(path, ex.Message); }
        catch (UnauthorizedAccessException ex) { return FileError("file_access", path, ex); }
        catch (IOException ex) { return FileError("file_io", path, ex); }
        catch (ArgumentException ex) { return FileError("file_path", path, ex); }
        catch (NotSupportedException ex) { return FileError("file_path", path, ex); }
    }

    public static (Ledger Ledger, Result<Ledger> Outcome) Reload(Ledger current, string path)
    {
        var result = Load(path);
        return result switch
        {
            Result<Ledger>.Success success => (success.Value, result),
            Result<Ledger>.Failure => (current, result),
            _ => throw new InvalidOperationException("Unknown Result case")
        };
    }

    static Result<Ledger> Bad(string path, string reason) =>
        Result<Ledger>.Fail(new("malformed_data", $"Cannot parse shipment JSON '{path}': {reason}"));
    static Result<Ledger> FileError(string code, string path, Exception exception) =>
        Result<Ledger>.Fail(new(code, $"Cannot read shipment JSON '{path}': {exception.Message}"));
}
