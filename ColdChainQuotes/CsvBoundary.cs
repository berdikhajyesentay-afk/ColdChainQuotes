using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// Lazy CSV reader. Creating the sequence opens nothing; each MoveNext reads and validates one row.
// The reader belongs to the iterator and is disposed when iteration finishes, fails or stops early.
public static class CsvBoundary
{
    const string Header = "id,customer,storageClass,weightKg,distanceKm";

    public static IEnumerable<Result<Shipment>> StreamRows(Func<TextReader> open)
    {
        using var reader = open();
        var header = reader.ReadLine();
        if (header is null)
            yield break;
        if (!header.Trim().Equals(Header, StringComparison.OrdinalIgnoreCase))
        {
            yield return Result<Shipment>.Fail(new("malformed_data", $"Unexpected CSV header '{header.Trim()}'; expected '{Header}'."));
            yield break;
        }

        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Trim().Length == 0)
                continue;
            yield return ParseRow(line, lineNumber);
        }
    }

    public static Result<Shipment> ParseRow(string line, int lineNumber)
    {
        var fields = line.Split(',');
        if (fields.Length != 5)
            return Result<Shipment>.Fail(new("malformed_data", $"Line {lineNumber}: expected 5 comma-separated fields, found {fields.Length}."));

        var id = fields[0].Trim();
        var customer = fields[1].Trim();
        var storage = fields[2].Trim();
        if (!decimal.TryParse(fields[3].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var weight))
            return Result<Shipment>.Fail(new("malformed_data", $"Line {lineNumber}: weight '{fields[3].Trim()}' is not a number."));
        if (!decimal.TryParse(fields[4].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var distance))
            return Result<Shipment>.Fail(new("malformed_data", $"Line {lineNumber}: distance '{fields[4].Trim()}' is not a number."));
        // Named storage classes only; numeric text such as "0" is refused, as in the JSON boundary.
        if (!Enum.GetNames<StorageClass>().Any(n => n.Equals(storage, StringComparison.OrdinalIgnoreCase)))
            return Result<Shipment>.Fail(new("invalid_shipment", $"Line {lineNumber} '{id}': '{storage}' is not a declared StorageClass."));

        return Shipment.Create(id, customer, Enum.Parse<StorageClass>(storage, true), weight, distance) switch
        {
            ShipmentCheck.Valid valid => Result<Shipment>.Ok(valid.Shipment),
            ShipmentCheck.Invalid invalid => Result<Shipment>.Fail(new("invalid_shipment", $"Line {lineNumber} '{id}': {string.Join("; ", invalid.Reasons)}")),
            _ => throw new InvalidOperationException("Unknown factory outcome")
        };
    }

    // Single materialisation point: the file is read once into an immutable snapshot, or the first failure is returned.
    public static Result<ImmutableArray<Shipment>> Load(string path) =>
        Materialise(() => File.OpenText(path), path);

    public static Result<ImmutableArray<Shipment>> Materialise(Func<TextReader> open, string source)
    {
        try
        {
            var rows = StreamRows(open).ToArray();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shipments = ImmutableArray.CreateBuilder<Shipment>(rows.Length);
            foreach (var row in rows)
            {
                switch (row)
                {
                    case Result<Shipment>.Failure failure:
                        return Result<ImmutableArray<Shipment>>.Fail(new(failure.Error.Code, $"File '{source}': {failure.Error.Message}"));
                    case Result<Shipment>.Success success:
                        if (!ids.Add(success.Value.Id))
                            return Result<ImmutableArray<Shipment>>.Fail(new("duplicate_id", $"File '{source}': duplicate shipment '{success.Value.Id}'."));
                        shipments.Add(success.Value);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Result case");
                }
            }
            return Result<ImmutableArray<Shipment>>.Ok(shipments.ToImmutable());
        }
        catch (FileNotFoundException ex) { return FileError("file_missing", source, ex); }
        catch (DirectoryNotFoundException ex) { return FileError("file_missing", source, ex); }
        catch (UnauthorizedAccessException ex) { return FileError("file_access", source, ex); }
        catch (IOException ex) { return FileError("file_io", source, ex); }
    }

    static Result<ImmutableArray<Shipment>> FileError(string code, string source, Exception exception) =>
        Result<ImmutableArray<Shipment>>.Fail(new(code, $"Cannot read shipment CSV '{source}': {exception.Message}"));
}
