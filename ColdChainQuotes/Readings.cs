using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// One temperature reading from a truck's cold box: which shipment, how many minutes into the trip,
// and the temperature in degrees Celsius.
// The constructor is private, so Create is the only way to make one: every reading has passed the rules.
public sealed record TemperatureReading
{
    public const decimal MinCelsius = -60m;
    public const decimal MaxCelsius = 60m;

    public string ShipmentId { get; }
    public int Minute { get; }
    public decimal Celsius { get; }

    private TemperatureReading(string shipmentId, int minute, decimal celsius)
    {
        ShipmentId = shipmentId;
        Minute = minute;
        Celsius = celsius;
    }

    // Rules (failure code "invalid_reading"):
    //   - shipmentId is not empty or blank
    //   - minute >= 0
    //   - celsius is between MinCelsius and MaxCelsius (a sensor fault outside this range is refused)
    // On success: Result.Ok(new TemperatureReading(shipmentId.Trim(), minute, celsius)).
    public static Result<TemperatureReading> Create(string shipmentId, int minute, decimal celsius)
    {
        if (string.IsNullOrWhiteSpace(shipmentId))
            return Result<TemperatureReading>.Fail(new AppError("invalid_reading", "Shipment id cannot be empty or blank."));

        if (minute < 0)
            return Result<TemperatureReading>.Fail(new AppError("invalid_reading", $"Minute cannot be negative (found {minute})."));

        if (celsius < MinCelsius || celsius > MaxCelsius)
            return Result<TemperatureReading>.Fail(new AppError("invalid_reading", $"Temperature {celsius} C is outside sensor range {MinCelsius}..{MaxCelsius} C."));

        return Result<TemperatureReading>.Ok(new TemperatureReading(shipmentId.Trim(), minute, celsius));
    }
}

// Lazy CSV reader for trip readings, built like CsvBoundary (A8).
// File format: header "shipmentId,minute,celsius", then one reading per line. Blank lines are skipped.
public static class Readings
{
    public const string Header = "shipmentId,minute,celsius";

    // Turns one CSV line into a reading. Pure: no file access.
    //   - not exactly 3 fields                 -> "malformed_data"  "Line {n}: expected 3 comma-separated fields, found {k}."
    //   - minute is not a whole number         -> "malformed_data"  "Line {n}: minute '{text}' is not a whole number."
    //   - celsius is not a number (use a dot)  -> "malformed_data"  "Line {n}: celsius '{text}' is not a number."
    //   - otherwise                            -> TemperatureReading.Create(...) and add the line number to its error message
    // Hint: decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
    //       CultureInfo.InvariantCulture, out var celsius). Without AllowLeadingSign, "-20" fails.
    public static Result<TemperatureReading> ParseRow(string line, int lineNumber)
    {
        var parts = line.Split(',');
        if (parts.Length != 3)
            return Result<TemperatureReading>.Fail(new AppError("malformed_data", $"Line {lineNumber}: expected 3 comma-separated fields, found {parts.Length}."));

        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
            return Result<TemperatureReading>.Fail(new AppError("malformed_data", $"Line {lineNumber}: minute '{parts[1]}' is not a whole number."));

        if (!decimal.TryParse(parts[2].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var celsius))
            return Result<TemperatureReading>.Fail(new AppError("malformed_data", $"Line {lineNumber}: celsius '{parts[2]}' is not a number."));

        var created = TemperatureReading.Create(parts[0], minute, celsius);
        return created switch
        {
            Result<TemperatureReading>.Failure failure =>
                Result<TemperatureReading>.Fail(new AppError(failure.Error.Code, $"Line {lineNumber}: {failure.Error.Message}")),
            Result<TemperatureReading>.Success success =>
                success,
            _ => throw new InvalidOperationException("Unknown Result case")
        };
    }

    // Deferred: creating the sequence opens nothing. The reader is opened on the first MoveNext,
    // and it is disposed when the loop finishes, throws, or the caller stops early (using var + yield).
    //   - empty file (no header)  -> yield nothing
    //   - wrong header            -> yield one Fail("malformed_data", "Unexpected readings header ...") and stop
    //   - every other non-blank line -> yield return ParseRow(line, lineNumber)   (the header is line 1)
    public static IEnumerable<Result<TemperatureReading>> Stream(Func<TextReader> open)
    {
        using var reader = open();
        var header = reader.ReadLine();
        if (header is null)
            yield break;

        if (!string.Equals(header.Trim(), Header, StringComparison.Ordinal))
        {
            yield return Result<TemperatureReading>.Fail(new AppError("malformed_data", $"Unexpected readings header '{header}'; expected '{Header}'."));
            yield break;
        }

        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            yield return ParseRow(line, lineNumber);
        }
    }

    // The single materialisation point: Stream(open).ToArray() runs ONCE, so the file is read once.
    // The first failed row stops the load and returns that failure, with the source added to the message.
    // On success, returns an immutable snapshot that can be enumerated many times without touching the file.
    // Catch file problems HERE (the boundary), not in the pure code:
    //   FileNotFoundException, DirectoryNotFoundException -> "file_missing"
    //   UnauthorizedAccessException                       -> "file_access"
    //   IOException                                       -> "file_io"
    public static Result<ImmutableArray<TemperatureReading>> Materialise(Func<TextReader> open, string source)
    {
        try
        {
            var list = ImmutableArray.CreateBuilder<TemperatureReading>();
            foreach (var item in Stream(open))
            {
                switch (item)
                {
                    case Result<TemperatureReading>.Failure failure:
                        return Result<ImmutableArray<TemperatureReading>>.Fail(
                            new AppError(failure.Error.Code, $"File '{source}': {failure.Error.Message}"));
                    case Result<TemperatureReading>.Success success:
                        list.Add(success.Value);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Result case");
                }
            }
            return Result<ImmutableArray<TemperatureReading>>.Ok(list.ToImmutable());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result<ImmutableArray<TemperatureReading>>.Fail(new AppError("file_missing", $"Cannot read readings CSV '{source}': {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ImmutableArray<TemperatureReading>>.Fail(new AppError("file_access", $"Cannot read readings CSV '{source}': {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Result<ImmutableArray<TemperatureReading>>.Fail(new AppError("file_io", $"Cannot read readings CSV '{source}': {ex.Message}"));
        }
    }

    // Opens a real file. This is done for you: it only connects File.OpenText to Materialise.
    public static Result<ImmutableArray<TemperatureReading>> Load(string path) =>
        Materialise(() => File.OpenText(path), path);
}
