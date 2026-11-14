using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainQuotes;

// STARTER (Bekarys, W3). Copy to ColdChainQuotes/Fleet.cs and fill every TODO.
// See TASK.md sections 3 and 4.

// A refrigerated vehicle. The constructor is private: Vehicle.Create is the only way to get one,
// so every Vehicle in the program has passed the rules (same idea as Shipment.Create).
public sealed record Vehicle
{
    public const decimal MaxCapacityKg = 40_000m;

    public string Id { get; }
    public decimal CapacityKg { get; }
    public bool HasFreezer { get; }
    public DistanceTier MaxTier { get; }   // DistanceTier is the enum in NativeTier.cs: Local, Regional, LongHaul

    private Vehicle(string id, decimal capacityKg, bool hasFreezer, DistanceTier maxTier)
    {
        Id = id;
        CapacityKg = capacityKg;
        HasFreezer = hasFreezer;
        MaxTier = maxTier;
    }

    // The factory. Check EVERY rule and collect EVERY reason (do not stop at the first one):
    //   - id is blank                          -> "vehicle id is missing"
    //   - capacityKg <= 0                      -> "capacity must be greater than 0 kg, found X"
    //   - capacityKg > MaxCapacityKg           -> "capacity must be at most 40000 kg, found X"
    //   - maxTier is not a declared enum value -> "'7' is not a declared DistanceTier (Local, Regional or LongHaul)"
    // No reasons: VehicleCheck.Valid(new Vehicle(id.Trim(), ...)). Otherwise: VehicleCheck.Invalid(reasons).
    // Tip: copy the style of Shipment.Create in Models.cs (ImmutableArray.CreateBuilder, Enum.IsDefined).
    public static VehicleCheck Create(string id, decimal capacityKg, bool hasFreezer, DistanceTier maxTier)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(id))
            reasons.Add("vehicle id is missing");
        if (capacityKg <= 0)
            reasons.Add(Invariant($"capacity must be greater than 0 kg, found {capacityKg}"));
        if (capacityKg > MaxCapacityKg)
            reasons.Add(Invariant($"capacity must be at most 40000 kg, found {capacityKg}"));
        if (!Enum.IsDefined(maxTier))
            reasons.Add($"'{(int)maxTier}' is not a declared DistanceTier (Local, Regional or LongHaul)");

        return reasons.Count == 0
            ? new VehicleCheck.Valid(new Vehicle(id.Trim(), capacityKg, hasFreezer, maxTier))
            : new VehicleCheck.Invalid(reasons.ToImmutable());
    }

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

// The factory's outcome. The private constructor closes the hierarchy: only these two cases exist.
public abstract record VehicleCheck
{
    private VehicleCheck() { }

    public sealed record Valid(Vehicle Vehicle) : VehicleCheck;

    public sealed record Invalid(ImmutableArray<string> Reasons) : VehicleCheck;
}

// File boundary: the only place that reads vehicles.json. Exceptions become Result failures here.
public static class FleetBoundary
{
    public const string DefaultPath = "samples/vehicles.json";

    // Nullable fields, so a missing field is visible (null) instead of silently 0 or false.
    sealed record VehicleRow(string? Id, decimal? CapacityKg, bool? HasFreezer, string? MaxTier);

    static readonly JsonSerializerOptions Settings = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Reads the whole fleet, or returns the first problem. Codes:
    //   file_missing   (FileNotFoundException, DirectoryNotFoundException)
    //   malformed_data (JsonException, null array, a row with a missing field, maxTier not Local/Regional/LongHaul)
    //   invalid_vehicle (Vehicle.Create returned Invalid; message lists the reasons)
    //   duplicate_id   (same id twice, ignoring case)
    //   file_access / file_io (UnauthorizedAccessException / IOException)
    // An empty array "[]" is a valid, empty fleet.
    // Tip: JsonBoundary.Load in JsonBoundary.cs does the same job for shipments. Follow it.
    public static Result<ImmutableArray<Vehicle>> Load(string path)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<VehicleRow[]?>(File.ReadAllText(path), Settings);
            if (rows is null)
                return Bad(path, "expected a JSON array.");

            var vehicles = ImmutableArray.CreateBuilder<Vehicle>();
            foreach (var row in rows)
            {
                if (row is null || row.Id is null || row.CapacityKg is null || row.HasFreezer is null || row.MaxTier is null
                    || !Enum.TryParse<DistanceTier>(row.MaxTier, true, out var tier) || !Enum.IsDefined(tier))
                    return Bad(path, "each vehicle needs id, capacityKg, hasFreezer and a declared maxTier.");

                switch (Vehicle.Create(row.Id, row.CapacityKg.Value, row.HasFreezer.Value, tier))
                {
                    case VehicleCheck.Valid valid:
                        if (vehicles.Any(v => v.Id.Equals(valid.Vehicle.Id, StringComparison.OrdinalIgnoreCase)))
                            return Result<ImmutableArray<Vehicle>>.Fail(new("duplicate_id", $"Vehicle JSON '{path}' has duplicate id '{valid.Vehicle.Id}'."));
                        vehicles.Add(valid.Vehicle);
                        break;
                    case VehicleCheck.Invalid invalid:
                        return Result<ImmutableArray<Vehicle>>.Fail(new("invalid_vehicle", $"Vehicle JSON '{path}': {string.Join("; ", invalid.Reasons)}"));
                }
            }
            return Result<ImmutableArray<Vehicle>>.Ok(vehicles.ToImmutable());
        }
        catch (FileNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (DirectoryNotFoundException ex) { return FileError("file_missing", path, ex); }
        catch (JsonException ex) { return Bad(path, ex.Message); }
        catch (UnauthorizedAccessException ex) { return FileError("file_access", path, ex); }
        catch (IOException ex) { return FileError("file_io", path, ex); }
        catch (ArgumentException ex) { return FileError("file_path", path, ex); }
        catch (NotSupportedException ex) { return FileError("file_path", path, ex); }
    }

    static Result<ImmutableArray<Vehicle>> Bad(string path, string reason) =>
        Result<ImmutableArray<Vehicle>>.Fail(new("malformed_data", $"Cannot parse vehicles JSON '{path}': {reason}"));

    static Result<ImmutableArray<Vehicle>> FileError(string code, string path, Exception ex) =>
        Result<ImmutableArray<Vehicle>>.Fail(new(code, $"Cannot read vehicles JSON '{path}': {ex.Message}"));
}
