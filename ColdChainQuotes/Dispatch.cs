using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// STARTER (Bekarys, W3). Copy over ColdChainQuotes/Dispatch.cs (it replaces the whole stub) and fill every TODO.
// See TASK.md sections 5, 6 and 7. Rules here are PURE: no Console, no files, no clock.

// One row of the fleet load report.
public sealed record VehicleLoad(string VehicleId, decimal CapacityKg, int Bookings, decimal LoadKg);

public static class DispatchRules
{
    // Option, not null: a vehicle id that is not in the fleet is a normal "Missing" case.
    // Compare ids ignoring case.
    public static Option<Vehicle> FindVehicle(ImmutableArray<Vehicle> fleet, string vehicleId)
    {
        var vehicle = fleet.FirstOrDefault(v => v.Id.Equals(vehicleId, StringComparison.OrdinalIgnoreCase));
        return vehicle is null ? Option<Vehicle>.None() : Option<Vehicle>.Some(vehicle);
    }

    // Total weight of the bookings that are ON this vehicle now: only Booking.Dispatched with this VehicleId.
    // Delivered/Spoiled/Invoiced bookings have left the vehicle, so they do not count.
    public static decimal LoadOn(Ledger ledger, string vehicleId)
    {
        return ledger.Bookings.Values.OfType<Booking.Dispatched>()
            .Where(b => b.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Shipment.WeightKg);
    }

    // W3: dispatch one Confirmed booking on one vehicle. Build it as ONE Bind/Map chain.
    // Checks in this order. The first failure stops everything after it:
    //   1. missing_booking  - ResultBookings.Find(ledger, shipmentId).ToResult(...)
    //   2. not_confirmed    - the booking is not Booking.Confirmed (use BookingRules.StateName in the message)
    //   3. missing_vehicle  - FindVehicle(...).ToResult(...)
    //   4. storage_mismatch - shipment is Frozen and the vehicle has no freezer
    //   5. over_capacity    - LoadOn(vehicle) + shipment weight > CapacityKg   (exactly full is OK)
    //   6. native tier      - classify(decimal.Ceiling(shipment.DistanceKm)); a failure (native_load_failed, ...) passes through
    //   7. out_of_range     - the tier from C is above vehicle.MaxTier
    // Success: ledger with { Bookings = ledger.Bookings.SetItem(id, new Booking.Dispatched(confirmed, vehicle.Id, tier)) }
    // Failure: the given ledger is never changed (records are immutable, you only return a new one on success).
    // Why Ceiling: the C function only takes whole km. 50.2 km must count as 51 km (Regional), never 50 (Local).
    public static Result<Ledger> Assign(Ledger ledger, ImmutableArray<Vehicle> fleet, string shipmentId, string vehicleId,
        Func<decimal, Result<DistanceTier>> classify)
    {
        return ResultBookings.Find(ledger, shipmentId)
            .ToResult(new("missing_booking", $"No booking for '{shipmentId}'."))
            .Bind(booking => booking is Booking.Confirmed confirmed
                ? Result<Booking.Confirmed>.Ok(confirmed)
                : Result<Booking.Confirmed>.Fail(new("not_confirmed", $"{booking.Shipment.Id} is {BookingRules.StateName(booking)}; only a confirmed booking can be dispatched.")))
            .Bind(confirmed => FindVehicle(fleet, vehicleId)
                .ToResult(new("missing_vehicle", $"No vehicle '{vehicleId}' in the fleet."))
                .Bind(vehicle =>
                {
                    var shipment = confirmed.Shipment;
                    if (shipment.StorageClass == StorageClass.Frozen && !vehicle.HasFreezer)
                        return Result<(Booking.Confirmed, Vehicle)>.Fail(new("storage_mismatch", $"{shipment.Id} is Frozen and {vehicle.Id} has no freezer."));
                    var load = LoadOn(ledger, vehicle.Id);
                    if (load + shipment.WeightKg > vehicle.CapacityKg)
                        return Result<(Booking.Confirmed, Vehicle)>.Fail(new("over_capacity", $"{vehicle.Id} carries {Kg(load)} kg of {Kg(vehicle.CapacityKg)} kg; {shipment.Id} adds {Kg(shipment.WeightKg)} kg."));
                    return Result<(Booking.Confirmed, Vehicle)>.Ok((confirmed, vehicle));
                }))
            .Bind(pair => classify(decimal.Ceiling(pair.Item1.Shipment.DistanceKm))
                .Bind(tier => tier > pair.Item2.MaxTier
                    ? Result<Ledger>.Fail(new("out_of_range", $"{pair.Item1.Shipment.Id} is {Kg(pair.Item1.Shipment.DistanceKm)} km, tier {tier} (native ccq_distance_tier); {pair.Item2.Id} goes up to {pair.Item2.MaxTier}."))
                    : Result<Ledger>.Ok(ledger with { Bookings = ledger.Bookings.SetItem(pair.Item1.Shipment.Id, new Booking.Dispatched(pair.Item1, pair.Item2.Id, tier)) })));
    }

    // W3 auto: the POLICY chooses the vehicle.
    //   1. missing_booking / not_confirmed exactly like Assign (check the booking first)
    //   2. candidates = the vehicles for which Assign(...) would succeed (keep fleet order)
    //   3. policy(candidates, shipment) -> Option<Vehicle>; Missing becomes no_vehicle_fits
    //   4. Assign with the chosen vehicle, and return (new ledger, chosen vehicle)
    public static Result<(Ledger Ledger, Vehicle Vehicle)> AssignAuto(Ledger ledger, ImmutableArray<Vehicle> fleet, string shipmentId,
        Func<IEnumerable<Vehicle>, Shipment, Option<Vehicle>> policy, Func<decimal, Result<DistanceTier>> classify)
    {
        return ResultBookings.Find(ledger, shipmentId)
            .ToResult(new("missing_booking", $"No booking for '{shipmentId}'."))
            .Bind(booking => booking is Booking.Confirmed confirmed
                ? Result<Booking.Confirmed>.Ok(confirmed)
                : Result<Booking.Confirmed>.Fail(new("not_confirmed", $"{booking.Shipment.Id} is {BookingRules.StateName(booking)}; only a confirmed booking can be dispatched.")))
            .Bind(confirmed =>
            {
                var candidates = fleet.Where(vehicle => Assign(ledger, fleet, confirmed.Shipment.Id, vehicle.Id, classify) is Result<Ledger>.Success);
                return policy(candidates, confirmed.Shipment)
                    .ToResult(new("no_vehicle_fits", $"No vehicle can take {confirmed.Shipment.Id}."))
                    .Bind(vehicle => Assign(ledger, fleet, confirmed.Shipment.Id, vehicle.Id, classify)
                        .Map(next => (next, vehicle)));
            });
    }

    // Report: one row per vehicle in the fleet (vehicles with no load show 0 bookings, 0 kg).
    // GroupBy VehicleId over the Dispatched bookings, Count and Sum(weight).
    // Order: LoadKg descending, then VehicleId (ordinal) as the tie-breaker. Materialise with ToList().
    public static IReadOnlyList<VehicleLoad> FleetLoad(Ledger ledger, ImmutableArray<Vehicle> fleet)
    {
        var loads = ledger.Bookings.Values.OfType<Booking.Dispatched>()
            .GroupBy(b => b.VehicleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Bookings: g.Count(), Load: g.Sum(b => b.Shipment.WeightKg)), StringComparer.OrdinalIgnoreCase);
        return fleet.Select(vehicle =>
            loads.TryGetValue(vehicle.Id, out var load)
                ? new VehicleLoad(vehicle.Id, vehicle.CapacityKg, load.Bookings, load.Load)
                : new VehicleLoad(vehicle.Id, vehicle.CapacityKg, 0, 0m))
            .OrderByDescending(row => row.LoadKg)
            .ThenBy(row => row.VehicleId, StringComparer.Ordinal)
            .ToList();
    }

    public static string Kg(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

// Two policies with the SAME shape, so the caller can pass either one (higher-order function).
public static class DispatchPolicies
{
    // The first candidate, in fleet file order.
    public static Option<Vehicle> FirstFit(IEnumerable<Vehicle> candidates, Shipment shipment)
    {
        var first = candidates.FirstOrDefault();
        return first is null ? Option<Vehicle>.None() : Option<Vehicle>.Some(first);
    }

    // The candidate with the least spare capacity left: (CapacityKg - shipment.WeightKg) smallest. Ties by Id.
    public static Option<Vehicle> BestFit(IEnumerable<Vehicle> candidates, Shipment shipment)
    {
        var best = candidates.OrderBy(v => v.CapacityKg - shipment.WeightKg)
            .ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();
        return best is null ? Option<Vehicle>.None() : Option<Vehicle>.Some(best);
    }

    // The command word picks a policy. null means no policy has that name (like PricePolicies.Find).
    public static (string Name, Func<IEnumerable<Vehicle>, Shipment, Option<Vehicle>> Choose)? Find(string word) => word.ToLowerInvariant() switch
    {
        "firstfit" => ("FirstFit", FirstFit),
        "bestfit" => ("BestFit", BestFit),
        _ => null
    };
}

// Console boundary. Commands (args arrive WITHOUT the word "dispatch"):
//   dispatch <id> <vehicle>                    -> SUCCESS SH1 dispatched on V2 (tier Local, 60 kg)
//   dispatch auto <id> [firstfit|bestfit]      -> SUCCESS SH1 dispatched on V4 by BestFit (tier Local, 60 kg)
//   dispatch fleet                             -> the fleet load table (TASK.md section 7)
//   options anywhere: --vehicles <path> (default samples/vehicles.json), --state <path>, --seed <path>
// For the two state-changing commands use StateConsole.Execute: it loads the state, runs your step,
// saves ONLY on success, and prints "SUCCESS ..." or "ERROR [code] ...".
public static class DispatchConsole
{
    public static int Run(string[] args, TextWriter output)
    {
        var (words, vehiclesPath) = VehiclesPath(args);

        var (stateWords, statePath, seedPath) = StateStore.ResolvePaths(words);
        if (stateWords is ["fleet"])
        {
            var report = StateStore.LoadOrSeed(statePath, seedPath)
                .Bind(ledger => FleetBoundary.Load(vehiclesPath).Map(fleet => FormatFleet(DispatchRules.FleetLoad(ledger, fleet))));
            return StateConsole.Show(report, output);
        }

        return StateConsole.Execute(words, output, (ledger, rest) => FleetBoundary.Load(vehiclesPath).Bind(fleet => rest switch
        {
            ["auto", var id] => Auto(ledger, fleet, id, "firstfit"),
            ["auto", var id, var policy] => Auto(ledger, fleet, id, policy),
            [var id, var vehicleId] => DispatchRules.Assign(ledger, fleet, id, vehicleId, NativeTier.Classify)
                .Map(next => (next, $"{id} dispatched on {vehicleId} (tier {((Booking.Dispatched)next.Bookings[id]).Tier}, {DispatchRules.Kg(next.Bookings[id].Shipment.WeightKg)} kg)")),
            _ => Result<(Ledger, string)>.Fail(new("usage", "dispatch <id> <vehicle> | dispatch auto <id> [firstfit|bestfit] | dispatch fleet"))
        }));
    }

    static Result<(Ledger, string)> Auto(Ledger ledger, ImmutableArray<Vehicle> fleet, string id, string word) =>
        DispatchPolicies.Find(word) is not { } selected
            ? Result<(Ledger, string)>.Fail(new("invalid_policy", $"Unknown dispatch policy '{word}'; use firstfit or bestfit."))
            : DispatchRules.AssignAuto(ledger, fleet, id, selected.Choose, NativeTier.Classify)
                .Map(done => (done.Ledger, $"{id} dispatched on {done.Vehicle.Id} by {selected.Name} (tier {((Booking.Dispatched)done.Ledger.Bookings[id]).Tier}, {DispatchRules.Kg(done.Ledger.Bookings[id].Shipment.WeightKg)} kg)"));

    static string FormatFleet(IReadOnlyList<VehicleLoad> rows)
    {
        var writer = new StringWriter();
        writer.WriteLine("Fleet load (dispatched bookings per vehicle, heaviest first, ties by id)");
        writer.WriteLine($"{"Vehicle",-8} {"Capacity",8} {"Bookings",8} {"LoadKg",8}");
        if (rows.Count == 0) writer.WriteLine("  no records");
        foreach (var row in rows)
            writer.WriteLine($"{row.VehicleId,-8} {DispatchRules.Kg(row.CapacityKg),8} {row.Bookings,8} {DispatchRules.Kg(row.LoadKg),8}");
        if (rows.Count > 0)
            writer.WriteLine($"{"Total",-8} {"",8} {rows.Sum(r => r.Bookings),8} {DispatchRules.Kg(rows.Sum(r => r.LoadKg)),8}");
        return writer.ToString().TrimEnd();
    }

    // Takes "--vehicles <path>" out of the arguments. Everything else stays, in order.
    static (string[] Words, string VehiclesPath) VehiclesPath(string[] args)
    {
        var rest = new List<string>();
        var path = FleetBoundary.DefaultPath;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vehicles" && i + 1 < args.Length) path = args[++i];
            else rest.Add(args[i]);
        }
        return (rest.ToArray(), path);
    }
}
