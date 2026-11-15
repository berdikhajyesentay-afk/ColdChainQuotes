using System.Collections.Immutable;
using System.Globalization;

// A breach check is a function: (the trip's readings, the storage class) -> Option<string>.
//   Some(reason) = a BREACH WAS FOUND, and reason says where.   -> the booking becomes Spoiled
//   None         = no breach, the cold chain held.              -> the booking becomes Delivered
// The alias only gives the Func a readable name; it is still Func<IReadOnlyList<...>, StorageClass, Option<string>>.
using BreachCheck = System.Func<
    System.Collections.Generic.IReadOnlyList<ColdChainQuotes.TemperatureReading>,
    ColdChainQuotes.StorageClass,
    ColdChainQuotes.Option<string>>;

namespace ColdChainQuotes;

// ---------------------------------------------------------------- pure rules

// The safe temperature range for each storage class. One place, so a range change is one edit.
public static class SafeRange
{
    // Cold: 2..8 C.  Frozen: -25..-18 C.  Both ends are inside the range (inclusive).
    public static (decimal Min, decimal Max) For(StorageClass storage) => storage switch
    {
        StorageClass.Cold => (2m, 8m),
        StorageClass.Frozen => (-25m, -18m),
        _ => throw new ArgumentOutOfRangeException(nameof(storage), $"Unknown storage class: {storage}")
    };

    public static bool IsSafe(TemperatureReading reading, StorageClass storage)
    {
        var (min, max) = For(storage);
        return reading.Celsius >= min && reading.Celsius <= max;
    }
}

// The two breach checks (tolerance policies). Same input, different business rule.
public static class TolerancePolicies
{
    // How many out-of-range readings IN A ROW make the Tolerant policy spoil a shipment.
    public const int TolerantRun = 2;

    // Strict: ANY single out-of-range reading is a breach.
    // Some("reading at minute {m} is {c} C, outside {min}..{max} C") for the FIRST bad reading, else None.
    public static Option<string> Strict(IReadOnlyList<TemperatureReading> readings, StorageClass storage)
    {
        var (min, max) = SafeRange.For(storage);
        foreach (var r in readings)
        {
            if (!SafeRange.IsSafe(r, storage))
            {
                return Option<string>.Some($"reading at minute {r.Minute} is {Number(r.Celsius)} C, outside {Number(min)}..{Number(max)} C");
            }
        }
        return Option<string>.None();
    }

    // Tolerant: a breach only if there is a run of TolerantRun (2) or more CONSECUTIVE out-of-range readings.
    // A single spike between safe readings is tolerated. A run at the very end of the list also counts.
    // Some("readings from minute {first} to {last} are outside {min}..{max} C") for the first such run, else None.
    public static Option<string> Tolerant(IReadOnlyList<TemperatureReading> readings, StorageClass storage)
    {
        var (min, max) = SafeRange.For(storage);
        int currentRun = 0;
        int firstMinute = 0;

        foreach (var r in readings)
        {
            if (!SafeRange.IsSafe(r, storage))
            {
                if (currentRun == 0)
                    firstMinute = r.Minute;

                currentRun++;
                if (currentRun >= TolerantRun)
                {
                    return Option<string>.Some($"readings from minute {firstMinute} to {r.Minute} are outside {Number(min)}..{Number(max)} C");
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        return Option<string>.None();
    }

    // Finds a policy by its command word ("strict" or "tolerant", any case). Missing = unknown word.
    public static Option<(string Name, BreachCheck FindBreach)> Find(string word) =>
        word.ToLowerInvariant() switch
        {
            "strict" => Option<(string Name, BreachCheck FindBreach)>.Some(("Strict", Strict)),
            "tolerant" => Option<(string Name, BreachCheck FindBreach)>.Some(("Tolerant", Tolerant)),
            _ => Option<(string Name, BreachCheck FindBreach)>.None()
        };

    static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

// W4: close a Dispatched booking as Delivered or Spoiled. Pure: no Console, no files.
public static class DeliveryRules
{
    // Checks, in this order. The first failure stops everything after it (Bind), and the ledger is unchanged.
    //   1. missing_booking     the id has no booking          (use ResultBookings.Find(...).ToResult(...))
    //   2. not_dispatched      the booking is not Dispatched  (pattern match; say its state with BookingRules.StateName)
    //   3. missing_readings    no reading has this ShipmentId (compare ids ignoring case)
    //   4. unordered_readings  minutes are not strictly increasing  (use the Require extension)
    // Then findBreach(readings, storage):
    //   Some(reason) -> new Booking.Spoiled(dispatched, reason)
    //   None         -> new Booking.Delivered(dispatched, readings.Count)
    // Both are SUCCESS: the delivery was closed. Return ledger with { Bookings = ledger.Bookings.SetItem(id, closed) }.
    // Readings of OTHER shipments in the list are ignored.
    public static Result<Ledger> Complete(
        Ledger ledger, string shipmentId, IEnumerable<TemperatureReading> readings, BreachCheck findBreach)
    {
        return ResultBookings.Find(ledger, shipmentId)
            .ToResult(new AppError("missing_booking", $"No booking for '{shipmentId}'."))
            .Bind(booking => booking is Booking.Dispatched dispatched
                ? Result<Booking.Dispatched>.Ok(dispatched)
                : Result<Booking.Dispatched>.Fail(new AppError("not_dispatched", $"{shipmentId} is {BookingRules.StateName(booking)}; only a dispatched booking can be delivered.")))
            .Bind(dispatched =>
            {
                var ownReadings = readings
                    .Where(r => string.Equals(r.ShipmentId, shipmentId, StringComparison.OrdinalIgnoreCase))
                    .ToImmutableArray();

                return ownReadings.Length == 0
                    ? Result<(Booking.Dispatched, ImmutableArray<TemperatureReading>)>.Fail(
                        new AppError("missing_readings", $"No readings for shipment '{shipmentId}'."))
                    : Result<(Booking.Dispatched, ImmutableArray<TemperatureReading>)>.Ok((dispatched, ownReadings));
            })
            .Require(pair => AreMinutesStrictlyIncreasing(pair.Item2),
                new AppError("unordered_readings", $"Readings for '{shipmentId}' are not in strictly increasing minute order."))
            .Map(pair =>
            {
                var (dispatched, ownReadings) = pair;
                var breach = findBreach(ownReadings, dispatched.Shipment.StorageClass);
                Booking closed = breach is Option<string>.Found found
                    ? new Booking.Spoiled(dispatched, found.Value)
                    : new Booking.Delivered(dispatched, ownReadings.Length);
                return closed;
            })
            .Map(closed => ledger with { Bookings = ledger.Bookings.SetItem(shipmentId, closed) });
    }

    static bool AreMinutesStrictlyIncreasing(ImmutableArray<TemperatureReading> list)
    {
        for (int i = 1; i < list.Length; i++)
        {
            if (list[i].Minute <= list[i - 1].Minute)
                return false;
        }
        return true;
    }
}

// One row of the breach report.
public sealed record DeliveryLine(StorageClass Storage, int Delivered, int Spoiled, int Total);

public static class DeliveryReports
{
    // Closed deliveries per storage class. "Closed" means Delivered or Invoiced (counted as delivered)
    // and Spoiled or Credited (counted as spoiled). Other states are left out.
    // GroupBy storage class, Count each outcome, most Spoiled first, ties by storage class name (ordinal).
    // No closed deliveries -> an empty list (the console prints "no closed deliveries").
    public static IReadOnlyList<DeliveryLine> ByStorageClass(Ledger ledger)
    {
        var closed = ledger.Bookings.Values
            .Where(b => b is Booking.Delivered or Booking.Invoiced or Booking.Spoiled or Booking.Credited)
            .ToArray();

        if (closed.Length == 0)
            return Array.Empty<DeliveryLine>();

        return closed
            .GroupBy(b => b.Shipment.StorageClass)
            .Select(g => new DeliveryLine(
                g.Key,
                g.Count(b => b is Booking.Delivered or Booking.Invoiced),
                g.Count(b => b is Booking.Spoiled or Booking.Credited),
                g.Count()))
            .OrderByDescending(l => l.Spoiled)
            .ThenBy(l => l.Storage.ToString(), StringComparer.Ordinal)
            .ToArray();
    }
}

// ---------------------------------------------------------------- console boundary

// deliver <id> [strict|tolerant] [readingsPath]   (defaults: strict, samples/readings.csv)
// deliver report
// Both also accept --state <path> and --seed <path> (handled by StateStore.ResolvePaths).
public static class DeliveryConsole
{
    public const string DefaultReadingsPath = "samples/readings.csv";

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
            return Report(args.Skip(1).ToArray(), output);

        // StateConsole.Execute loads the ledger, runs the step, saves ONLY on success, prints SUCCESS or ERROR.
        return StateConsole.Execute(args, output, (ledger, rest) =>
        {
            if (rest.Length is < 1 or > 3)
                return Result<(Ledger, string)>.Fail(new AppError("usage", "deliver <id> [strict|tolerant] [readings]"));

            string id = rest[0];
            string policyWord = rest.Length > 1 ? rest[1] : "strict";
            string path = rest.Length > 2 ? rest[2] : DefaultReadingsPath;

            return TolerancePolicies.Find(policyWord)
                .ToResult(new AppError("invalid_policy", $"Unknown tolerance policy '{policyWord}'; use strict or tolerant."))
                .Bind(policy => Readings.Load(path)
                    .Bind(readings => DeliveryRules.Complete(ledger, id, readings, policy.FindBreach))
                    .Map(next => (next, Describe(next.Bookings[id], policy.Name))));
        });
    }

    // "SH1 delivered (Strict): 5 readings within 2..8 C"
    // "SH2 spoiled (Strict): reading at minute 30 is -15 C, outside -25..-18 C"
    static string Describe(Booking booking, string policyName) => booking switch
    {
        Booking.Delivered d =>
            $"{d.Shipment.Id} delivered ({policyName}): {d.ReadingCount} readings within {SafeRangeStr(d.Shipment.StorageClass)}",
        Booking.Spoiled s =>
            $"{s.Shipment.Id} spoiled ({policyName}): {s.Reason}",
        _ =>
            $"{booking.Shipment.Id} ({policyName})"
    };

    static string SafeRangeStr(StorageClass storage)
    {
        var (min, max) = SafeRange.For(storage);
        return $"{Number(min)}..{Number(max)} C";
    }

    static int Report(string[] args, TextWriter output)
    {
        var (_, statePath, seedPath) = StateStore.ResolvePaths(args);
        var loaded = StateStore.LoadOrSeed(statePath, seedPath);
        switch (loaded)
        {
            case Result<Ledger>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            case Result<Ledger>.Success success:
                var rows = DeliveryReports.ByStorageClass(success.Value);
                output.WriteLine("Report: closed deliveries by storage class");
                output.WriteLine($"{"Storage",-8} {"Delivered",10} {"Spoiled",8} {"Total",6}");
                if (rows.Count == 0)
                {
                    output.WriteLine("  no closed deliveries");
                }
                else
                {
                    foreach (var r in rows)
                        output.WriteLine($"{r.Storage,-8} {r.Delivered,10} {r.Spoiled,8} {r.Total,6}");
                }
                return 0;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
