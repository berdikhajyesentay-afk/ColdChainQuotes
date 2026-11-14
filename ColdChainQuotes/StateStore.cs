using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainQuotes;

// Saves and loads the whole Ledger (every booking with its state) as JSON.
// This is a boundary: it owns the files, catches file and parsing exceptions, and returns Results.
// Every loaded booking is rebuilt through the validated Shipment factory and the state chain,
// so a hand-edited file can never create a state that the rules could not have produced.
public static class StateStore
{
    public const string DefaultStatePath = "data/state.json";
    public const string DefaultSeedPath = "samples/shipments.json";

    // The shape of one saved booking. Fields that a state does not use are null.
    sealed record BookingRow(
        string? Id, string? Customer, string? StorageClass, decimal? WeightKg, decimal? DistanceKm,
        string? State, string? PolicyName, decimal? Price, int? Number,
        string? VehicleId, string? Tier, int? ReadingCount, string? Reason, string? InvoiceNumber);

    sealed record StateFile(int? NextConfirmation, int? NextInvoice, BookingRow?[]? Bookings);

    static readonly JsonSerializerOptions Settings = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    // Splits "--state <path>" and "--seed <path>" out of the arguments, so every command
    // (and every test) can point at its own files. Defaults are relative to the current folder.
    public static (string[] Args, string StatePath, string SeedPath) ResolvePaths(string[] args)
    {
        var rest = new List<string>();
        var state = Environment.GetEnvironmentVariable("CCQ_STATE") is { Length: > 0 } fromEnv ? fromEnv : DefaultStatePath;
        var seed = DefaultSeedPath;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--state" && i + 1 < args.Length) state = args[++i];
            else if (args[i] == "--seed" && i + 1 < args.Length) seed = args[++i];
            else rest.Add(args[i]);
        }
        return (rest.ToArray(), state, seed);
    }

    // Loads the saved state. If no state file exists yet, opens a new Ledger from the seed
    // shipments JSON and saves it, so the first run and every later run use the same file.
    public static Result<Ledger> LoadOrSeed(string statePath, string seedPath) =>
        File.Exists(statePath)
            ? Load(statePath)
            : JsonBoundary.Load(seedPath).Bind(ledger => Save(statePath, ledger).Map(_ => ledger));

    public static Result<Ledger> Load(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), Settings);
            if (file?.Bookings is null || file.NextConfirmation is null)
                return Bad(path, "expected an object with 'nextConfirmation' and 'bookings'.");
            if (file.NextConfirmation < 1)
                return Bad(path, $"nextConfirmation must be at least 1, found {file.NextConfirmation}.");

            var bookings = ImmutableSortedDictionary.CreateBuilder<string, Booking>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < file.Bookings.Length; i++)
            {
                switch (ToBooking(file.Bookings[i], i + 1, path))
                {
                    case Result<Booking>.Failure failure:
                        return Result<Ledger>.Fail(failure.Error);
                    case Result<Booking>.Success success:
                        if (bookings.ContainsKey(success.Value.Shipment.Id))
                            return Result<Ledger>.Fail(new("duplicate_id", $"State '{path}', booking {i + 1}: duplicate shipment '{success.Value.Shipment.Id}'."));
                        bookings.Add(success.Value.Shipment.Id, success.Value);
                        break;
                }
            }
            // Cross-booking rules: confirmation numbers are unique and below the counter;
            // the invoice counter is at least 1 (a file from before invoicing has none: it starts at 1).
            var numbers = bookings.Values.Select(ConfirmationNumber).OfType<int>().ToArray();
            if (numbers.Length != numbers.Distinct().Count())
                return Bad(path, "two bookings share a confirmation number.");
            if (numbers.Length > 0 && numbers.Max() >= file.NextConfirmation)
                return Bad(path, $"nextConfirmation {file.NextConfirmation} must be greater than every confirmation number (max {numbers.Max()}).");
            if (file.NextInvoice is < 1)
                return Bad(path, $"nextInvoice must be at least 1, found {file.NextInvoice}.");

            return Result<Ledger>.Ok(new Ledger(bookings.ToImmutable(), file.NextConfirmation.Value)
            {
                NextInvoice = file.NextInvoice ?? 1
            });
        }
        catch (FileNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (DirectoryNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (JsonException ex) { return Bad(path, ex.Message); }
        catch (UnauthorizedAccessException ex) { return FileError("file_access", path, ex); }
        catch (IOException ex) { return FileError("file_io", path, ex); }
        catch (ArgumentException ex) { return FileError("file_path", path, ex); }
        catch (NotSupportedException ex) { return FileError("file_path", path, ex); }
    }

    // Writes a temporary file and replaces the target only when the temporary file is complete.
    // On failure the old state file is untouched and the temporary file is removed.
    public static Result<string> Save(string path, Ledger ledger)
    {
        var temp = path + ".tmp";
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var file = new StateFile(ledger.NextConfirmation, ledger.NextInvoice, ledger.Bookings.Values.Select(ToRow).ToArray());
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, file, Settings);
            File.Move(temp, path, overwrite: true);
            return Result<string>.Ok(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DeleteIfPresent(temp);
            return Result<string>.Fail(new("save_failed", $"Cannot save state '{path}': {ex.Message}"));
        }
    }

    static BookingRow ToRow(Booking booking)
    {
        var s = booking.Shipment;
        var row = new BookingRow(s.Id, s.Customer, s.StorageClass.ToString(), s.WeightKg, s.DistanceKm,
            null, null, null, null, null, null, null, null, null);
        return booking switch
        {
            Booking.Requested => row with { State = "Requested" },
            Booking.Quoted q => row with { State = "Quoted", PolicyName = q.PolicyName, Price = q.Price },
            Booking.Confirmed c => row with { State = "Confirmed", PolicyName = c.PolicyName, Price = c.Price, Number = c.Number },
            Booking.Dispatched d => row with { State = "Dispatched", PolicyName = d.PolicyName, Price = d.Price, Number = d.Number, VehicleId = d.VehicleId, Tier = d.Tier.ToString() },
            Booking.Delivered d => row with { State = "Delivered", PolicyName = d.PolicyName, Price = d.Price, Number = d.Number, VehicleId = d.VehicleId, ReadingCount = d.ReadingCount },
            Booking.Spoiled sp => row with { State = "Spoiled", PolicyName = sp.PolicyName, Price = sp.Price, Number = sp.Number, VehicleId = sp.VehicleId, Reason = sp.Reason },
            Booking.Invoiced i => row with { State = "Invoiced", PolicyName = i.PolicyName, Price = i.Price, Number = i.Number, VehicleId = i.VehicleId, ReadingCount = i.ReadingCount, InvoiceNumber = i.InvoiceNumber },
            Booking.Credited cr => row with { State = "Credited", PolicyName = cr.PolicyName, Price = cr.Price, Number = cr.Number, VehicleId = cr.VehicleId, Reason = cr.Reason, InvoiceNumber = cr.InvoiceNumber },
            _ => throw new InvalidOperationException("Unknown Booking case")
        };
    }

    // Rebuilds one booking: the shipment through the factory, then each state from the one before it.
    static Result<Booking> ToBooking(BookingRow? row, int index, string path)
    {
        AppError Malformed(string why) => new("malformed_data", $"State '{path}', booking {index}: {why}");

        if (row is null || row.Id is null || row.Customer is null || row.StorageClass is null
            || row.WeightKg is null || row.DistanceKm is null || row.State is null)
            return Result<Booking>.Fail(Malformed("id, customer, storageClass, weightKg, distanceKm and state are required."));
        if (!Enum.GetNames<StorageClass>().Any(n => n.Equals(row.StorageClass, StringComparison.OrdinalIgnoreCase)))
            return Result<Booking>.Fail(new("invalid_shipment", $"State '{path}', booking {index}: invalid storage class '{row.StorageClass}'."));

        var shipment = Shipment.Create(row.Id.Trim(), row.Customer.Trim(),
            Enum.Parse<StorageClass>(row.StorageClass, true), row.WeightKg.Value, row.DistanceKm.Value);
        if (shipment is ShipmentCheck.Invalid invalid)
            return Result<Booking>.Fail(new("invalid_shipment", $"State '{path}', booking {index} '{row.Id}': {string.Join("; ", invalid.Reasons)}"));
        var valid = ((ShipmentCheck.Valid)shipment).Shipment;

        Result<Booking> Need(bool present, string fields, Func<Booking> build) =>
            present ? Result<Booking>.Ok(build()) : Result<Booking>.Fail(Malformed($"state {row.State} needs {fields}."));

        bool priced = row.PolicyName is not null && row.Price is > 0;
        bool numbered = priced && row.Number is > 0;
        bool dispatched = numbered && row.VehicleId is { Length: > 0 }
            && Enum.GetNames<DistanceTier>().Any(n => n.Equals(row.Tier, StringComparison.OrdinalIgnoreCase));
        bool moved = numbered && row.VehicleId is { Length: > 0 };

        Booking.Quoted Quoted() => new(valid, row.PolicyName!, row.Price!.Value);
        Booking.Confirmed Confirmed() => new(Quoted(), row.Number!.Value);
        // Only Dispatched saves its tier; later states do not carry one, so the rebuilt chain uses Local there.
        Booking.Dispatched Dispatched() => new(Confirmed(), row.VehicleId!,
            row.Tier is not null && Enum.TryParse<DistanceTier>(row.Tier, true, out var tier) ? tier : DistanceTier.Local);
        Booking.Delivered Delivered() => new(Dispatched(), row.ReadingCount!.Value);

        return row.State.ToLowerInvariant() switch
        {
            "requested" => Result<Booking>.Ok(new Booking.Requested(valid)),
            "quoted" => Need(priced, "policyName and a positive price", Quoted),
            "confirmed" => Need(numbered, "policyName, price and number", Confirmed),
            "dispatched" => Need(dispatched, "policyName, price, number, vehicleId and tier", Dispatched),
            "delivered" => Need(moved && row.ReadingCount is >= 0, "policyName, price, number, vehicleId and readingCount", Delivered),
            "spoiled" => Need(moved && row.Reason is { Length: > 0 }, "policyName, price, number, vehicleId and reason",
                () => new Booking.Spoiled(Dispatched(), row.Reason!)),
            "invoiced" => Need(moved && row.ReadingCount is >= 0 && row.InvoiceNumber is { Length: > 0 },
                "policyName, price, number, vehicleId, readingCount and invoiceNumber",
                () => new Booking.Invoiced(Delivered(), row.InvoiceNumber!)),
            "credited" => Need(moved && row.Reason is { Length: > 0 } && row.InvoiceNumber is { Length: > 0 },
                "policyName, price, number, vehicleId, reason and invoiceNumber",
                () => new Booking.Credited(new Booking.Spoiled(Dispatched(), row.Reason!), row.InvoiceNumber!)),
            _ => Result<Booking>.Fail(Malformed($"unknown state '{row.State}'."))
        };
    }

    static int? ConfirmationNumber(Booking booking) => booking switch
    {
        Booking.Confirmed b => b.Number,
        Booking.Dispatched b => b.Number,
        Booking.Delivered b => b.Number,
        Booking.Spoiled b => b.Number,
        Booking.Invoiced b => b.Number,
        Booking.Credited b => b.Number,
        _ => null
    };

    static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    static Result<Ledger> Bad(string path, string reason) =>
        Result<Ledger>.Fail(new("malformed_data", $"Cannot parse state JSON '{path}': {reason}"));

    static Result<Ledger> FileError(string code, string path, Exception exception) =>
        Result<Ledger>.Fail(new(code, $"Cannot read state JSON '{path}': {exception.Message}"));
}
