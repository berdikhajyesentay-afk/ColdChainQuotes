using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Bekarys: W3 Dispatch. Vehicle factory, fleet loading, the pure dispatch rule, the two policies,
// the fleet load report, and two complete workflows through Program.Run.
public class DispatchTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainDispatch-" + Guid.NewGuid());
    readonly string state;
    readonly string seed = Path.Combine(AppContext.BaseDirectory, "samples", "shipments.json");
    readonly string vehicles = Path.Combine(AppContext.BaseDirectory, "samples", "vehicles.json");

    public DispatchTests()
    {
        Directory.CreateDirectory(folder);
        state = Path.Combine(folder, "data", "state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // ---------- helpers ----------

    static Vehicle Car(string id, decimal capacityKg, bool hasFreezer, DistanceTier maxTier) =>
        Vehicle.Create(id, capacityKg, hasFreezer, maxTier) switch
        {
            VehicleCheck.Valid valid => valid.Vehicle,
            VehicleCheck.Invalid invalid => throw new InvalidOperationException(string.Join("; ", invalid.Reasons)),
            _ => throw new InvalidOperationException("unexpected factory result")
        };

    // The same four vehicles as samples/vehicles.json, in the same order.
    static ImmutableArray<Vehicle> SampleFleet() =>
    [
        Car("V1", 50m, false, DistanceTier.Local),
        Car("V2", 100m, true, DistanceTier.Regional),
        Car("V3", 500m, true, DistanceTier.LongHaul),
        Car("V4", 80m, false, DistanceTier.LongHaul)
    ];

    // SH1 Cold 60 kg 20 km, SH2 Frozen 5 kg 40 km, SH3 Cold 8 kg 300 km (all Confirmed), SH5 Cold 2 kg 0 km (Requested).
    static Ledger SampleLedger() =>
        TestStates.LedgerOf(
            TestStates.Confirmed(TestStates.Ship("SH1", "FreshFarm", StorageClass.Cold, 60m, 20m), number: 1),
            TestStates.Confirmed(TestStates.Ship("SH2", "IceDream", StorageClass.Frozen, 5m, 40m), number: 2),
            TestStates.Confirmed(TestStates.Ship("SH3", "GreenLeaf", StorageClass.Cold, 8m, 300m), number: 3),
            new Booking.Requested(TestStates.Ship("SH5", "CityLab", StorageClass.Cold, 2m, 0m)));

    static AppError ErrorOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Failure>(result).Error;

    static T ValueOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Success>(result).Value;

    // Runs one command against a temporary state file. Only dispatch commands get --vehicles.
    (int Code, string Text) Run(params string[] args)
    {
        string[] full = args[0] == "dispatch"
            ? [.. args, "--state", state, "--seed", seed, "--vehicles", vehicles]
            : [.. args, "--state", state, "--seed", seed];
        var writer = new StringWriter();
        var code = Program.Run(full, TextReader.Null, writer);
        return (code, writer.ToString());
    }

    // ---------- Vehicle factory and fleet loading ----------

    [Fact]
    public void CreateAcceptsAValidVehicle()
    {
        var vehicle = Assert.IsType<VehicleCheck.Valid>(Vehicle.Create("V9", 120m, true, DistanceTier.Regional)).Vehicle;

        Assert.Equal(("V9", 120m, true, DistanceTier.Regional), (vehicle.Id, vehicle.CapacityKg, vehicle.HasFreezer, vehicle.MaxTier));
    }

    [Fact]
    public void CreateCollectsEveryReason()
    {
        var invalid = Assert.IsType<VehicleCheck.Invalid>(Vehicle.Create(" ", 0m, false, (DistanceTier)7));

        Assert.Equal(3, invalid.Reasons.Length);
    }

    [Theory]
    [InlineData(40000, true)]
    [InlineData(40001, false)]
    [InlineData(0.5, true)]
    [InlineData(-1, false)]
    public void CapacityBoundaries(double capacityKg, bool valid)
    {
        var check = Vehicle.Create("V9", (decimal)capacityKg, false, DistanceTier.Local);

        Assert.Equal(valid, check is VehicleCheck.Valid);
    }

    [Fact]
    public void LoadReadsTheSampleFleet()
    {
        var fleet = ValueOf(FleetBoundary.Load(vehicles));

        Assert.Equal(["V1", "V2", "V3", "V4"], fleet.Select(v => v.Id));
        Assert.False(fleet[0].HasFreezer);
        Assert.Equal(DistanceTier.Local, fleet[0].MaxTier);
        Assert.Equal(DistanceTier.LongHaul, fleet[2].MaxTier);
    }

    [Fact]
    public void LoadOfAMissingFileIsFileMissing()
    {
        Assert.Equal("file_missing", ErrorOf(FleetBoundary.Load(Path.Combine(folder, "nope.json"))).Code);
    }

    [Theory]
    [InlineData("[{")]
    [InlineData("null")]
    [InlineData("[{\"id\":\"V1\",\"capacityKg\":50,\"hasFreezer\":false}]")]
    [InlineData("[{\"id\":\"V1\",\"capacityKg\":50,\"hasFreezer\":false,\"maxTier\":\"Space\"}]")]
    public void LoadOfMalformedContentIsMalformedData(string json)
    {
        var path = Path.Combine(folder, "bad.json");
        File.WriteAllText(path, json);

        Assert.Equal("malformed_data", ErrorOf(FleetBoundary.Load(path)).Code);
    }

    [Fact]
    public void LoadRejectsInvalidAndDuplicateVehicles()
    {
        var invalid = Path.Combine(folder, "invalid.json");
        File.WriteAllText(invalid, "[{\"id\":\"V1\",\"capacityKg\":0,\"hasFreezer\":false,\"maxTier\":\"Local\"}]");
        var duplicate = Path.Combine(folder, "duplicate.json");
        File.WriteAllText(duplicate,
            "[{\"id\":\"V1\",\"capacityKg\":50,\"hasFreezer\":false,\"maxTier\":\"Local\"}," +
            " {\"id\":\"v1\",\"capacityKg\":60,\"hasFreezer\":true,\"maxTier\":\"Local\"}]");

        Assert.Equal("invalid_vehicle", ErrorOf(FleetBoundary.Load(invalid)).Code);
        Assert.Equal("duplicate_id", ErrorOf(FleetBoundary.Load(duplicate)).Code);
    }

    [Fact]
    public void LoadOfAnEmptyArrayIsAnEmptyFleet()
    {
        var path = Path.Combine(folder, "empty.json");
        File.WriteAllText(path, "[]");

        Assert.Empty(ValueOf(FleetBoundary.Load(path)));
    }

    // ---------- the pure dispatch rule ----------

    [Fact]
    public void AssignMovesAConfirmedBookingToDispatchedAndKeepsTheOldLedger()
    {
        var before = SampleLedger();

        var after = ValueOf(DispatchRules.Assign(before, SampleFleet(), "SH1", "V2", NativeTier.Classify));

        var dispatched = Assert.IsType<Booking.Dispatched>(after.Bookings["SH1"]);
        Assert.Equal(("V2", DistanceTier.Local, 4400m, 1), (dispatched.VehicleId, dispatched.Tier, dispatched.Price, dispatched.Number));
        Assert.IsType<Booking.Confirmed>(before.Bookings["SH1"]);
    }

    [Fact]
    public void OnlyAConfirmedBookingCanBeDispatched()
    {
        var ledger = SampleLedger();
        var once = ValueOf(DispatchRules.Assign(ledger, SampleFleet(), "SH1", "V2", NativeTier.Classify));

        Assert.Equal("not_confirmed", ErrorOf(DispatchRules.Assign(ledger, SampleFleet(), "SH5", "V2", NativeTier.Classify)).Code);
        Assert.Equal("not_confirmed", ErrorOf(DispatchRules.Assign(once, SampleFleet(), "SH1", "V3", NativeTier.Classify)).Code);
    }

    [Fact]
    public void UnknownBookingAndUnknownVehicleAreMissing()
    {
        Assert.Equal("missing_booking", ErrorOf(DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH9", "V2", NativeTier.Classify)).Code);
        Assert.Equal("missing_vehicle", ErrorOf(DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH1", "V9", NativeTier.Classify)).Code);
    }

    [Fact]
    public void FrozenNeedsAFreezer()
    {
        Assert.Equal("storage_mismatch", ErrorOf(DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH2", "V1", NativeTier.Classify)).Code);
    }

    [Fact]
    public void CapacityCountsOnlyTheLoadStillOnTheVehicle()
    {
        var truck = Car("T1", 10m, true, DistanceTier.LongHaul);
        var ledger = TestStates.LedgerOf(
            TestStates.Dispatched(TestStates.Ship("A1", "FreshFarm", weightKg: 8m), "T1", DistanceTier.Local, number: 1),
            TestStates.Delivered(TestStates.Ship("A2", "FreshFarm", weightKg: 50m), number: 2, vehicleId: "T1"),
            TestStates.Confirmed(TestStates.Ship("B1", "CityLab", weightKg: 2m), number: 3),
            TestStates.Confirmed(TestStates.Ship("B2", "CityLab", weightKg: 1m), number: 4));

        var full = ValueOf(DispatchRules.Assign(ledger, [truck], "B1", "T1", NativeTier.Classify));   // 8 + 2 = 10, exactly full

        Assert.Equal(10m, DispatchRules.LoadOn(full, "T1"));
        Assert.Equal("over_capacity", ErrorOf(DispatchRules.Assign(full, [truck], "B2", "T1", NativeTier.Classify)).Code);
    }

    [Fact]
    public void TheNativeTierDecidesTheRange()
    {
        // SH3 is 300 km: ccq_distance_tier says LongHaul, and V2 only goes up to Regional.
        var error = ErrorOf(DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH3", "V2", NativeTier.Classify));

        Assert.Equal("out_of_range", error.Code);
        Assert.Equal(DistanceTier.LongHaul,
            Assert.IsType<Booking.Dispatched>(ValueOf(DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH3", "V3", NativeTier.Classify)).Bookings["SH3"]).Tier);
    }

    [Fact]
    public void ADecimalDistanceIsRoundedUpBeforeTheNativeCall()
    {
        var ledger = TestStates.LedgerOf(TestStates.Confirmed(TestStates.Ship("D1", "CityLab", weightKg: 5m, distanceKm: 50.2m)));
        var localVan = Car("L1", 100m, true, DistanceTier.Local);
        var seen = new List<decimal>();

        var result = DispatchRules.Assign(ledger, [localVan], "D1", "L1", km => { seen.Add(km); return NativeTier.Classify(km); });

        Assert.Equal([51m], seen);                         // 50.2 km is sent to C as 51 km
        Assert.Equal("out_of_range", ErrorOf(result).Code); // 51 km is Regional, the van is Local only
    }

    [Fact]
    public void AnEarlyFailureNeverCallsTheNativeTier()
    {
        int calls = 0;
        Func<decimal, Result<DistanceTier>> counting = km => { calls++; return NativeTier.Classify(km); };

        DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH9", "V2", counting);   // missing_booking
        DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH5", "V2", counting);   // not_confirmed
        DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH2", "V1", counting);   // storage_mismatch
        DispatchRules.Assign(SampleLedger(), SampleFleet(), "SH1", "V1", counting);   // over_capacity

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ANativeFailureStopsTheDispatchAndChangesNothing()
    {
        var ledger = SampleLedger();
        Func<decimal, Result<DistanceTier>> broken = _ =>
            Result<DistanceTier>.Fail(new("native_load_failed", "Cannot load ccq_native.dll (test)"));

        var result = DispatchRules.Assign(ledger, SampleFleet(), "SH1", "V2", broken);

        Assert.Equal("native_load_failed", ErrorOf(result).Code);
        Assert.IsType<Booking.Confirmed>(ledger.Bookings["SH1"]);
    }

    // ---------- the two policies ----------

    [Fact]
    public void FirstFitAndBestFitChooseDifferentVehiclesForTheSameInput()
    {
        // SH1 (Cold, 60 kg, 20 km) fits V2 (100 kg), V3 (500 kg) and V4 (80 kg). V1 (50 kg) is too small.
        var first = ValueOf(DispatchRules.AssignAuto(SampleLedger(), SampleFleet(), "SH1", DispatchPolicies.FirstFit, NativeTier.Classify));
        var best = ValueOf(DispatchRules.AssignAuto(SampleLedger(), SampleFleet(), "SH1", DispatchPolicies.BestFit, NativeTier.Classify));

        Assert.Equal("V2", first.Vehicle.Id);   // first in file order
        Assert.Equal("V4", best.Vehicle.Id);    // least spare capacity: 80 - 60 = 20 kg
    }

    [Fact]
    public void AutoDispatchFailsWhenNoVehicleFits()
    {
        var ledger = TestStates.LedgerOf(TestStates.Confirmed(TestStates.Ship("H1", "FreshFarm", weightKg: 600m)));

        Assert.Equal("no_vehicle_fits",
            ErrorOf(DispatchRules.AssignAuto(ledger, SampleFleet(), "H1", DispatchPolicies.BestFit, NativeTier.Classify)).Code);
    }

    // ---------- fleet load report ----------

    [Fact]
    public void FleetLoadGroupsDispatchedBookingsPerVehicleWithTiesById()
    {
        var ledger = TestStates.LedgerOf(
            TestStates.Dispatched(TestStates.Ship("A1", "FreshFarm", weightKg: 30m), "V3", number: 1),
            TestStates.Dispatched(TestStates.Ship("A2", "FreshFarm", weightKg: 10m), "V3", number: 2),
            TestStates.Dispatched(TestStates.Ship("A3", "CityLab", weightKg: 40m), "V2", number: 3),
            TestStates.Delivered(TestStates.Ship("A4", "CityLab", weightKg: 99m), number: 4, vehicleId: "V4"));

        var rows = DispatchRules.FleetLoad(ledger, SampleFleet());

        Assert.Equal(
            [("V2", 1, 40m), ("V3", 2, 40m), ("V1", 0, 0m), ("V4", 0, 0m)],
            rows.Select(r => (r.VehicleId, r.Bookings, r.LoadKg)));
    }

    [Fact]
    public void FleetLoadOfAnEmptyLedgerOrEmptyFleet()
    {
        var empty = TestStates.LedgerOf();

        Assert.All(DispatchRules.FleetLoad(empty, SampleFleet()), r => Assert.Equal(0m, r.LoadKg));
        Assert.Empty(DispatchRules.FleetLoad(SampleLedger(), ImmutableArray<Vehicle>.Empty));
    }

    // ---------- complete workflows (Program.Run, saved state) ----------

    [Fact]
    public void DispatchWorkflowConfirmsThenDispatchesAndSaves()
    {
        Assert.Equal(0, Run("confirm", "SH1").Code);

        var (code, text) = Run("dispatch", "SH1", "V2");

        Assert.Equal(0, code);
        Assert.Contains("SUCCESS SH1 dispatched on V2", text);
        var saved = ValueOf(StateStore.Load(state));
        Assert.Equal("V2", Assert.IsType<Booking.Dispatched>(saved.Bookings["SH1"]).VehicleId);
    }

    [Fact]
    public void FailedDispatchWorkflowLeavesTheStateFileUnchanged()
    {
        Assert.Equal(0, Run("confirm", "SH2").Code);
        var before = File.ReadAllText(state);

        var (code, text) = Run("dispatch", "SH2", "V1");          // Frozen into a van without a freezer
        var (earlyCode, earlyText) = Run("dispatch", "SH5", "V2"); // SH5 was never confirmed

        Assert.Equal((1, 1), (code, earlyCode));
        Assert.Contains("ERROR [storage_mismatch]", text);
        Assert.Contains("ERROR [not_confirmed]", earlyText);
        Assert.Equal(before, File.ReadAllText(state));
    }

    [Fact]
    public void AutoAndFleetWorkflowsUseTheChosenPolicy()
    {
        Run("confirm", "SH1");

        var (code, text) = Run("dispatch", "auto", "SH1", "bestfit");
        var (fleetCode, fleet) = Run("dispatch", "fleet");

        Assert.Equal((0, 0), (code, fleetCode));
        Assert.Contains("SUCCESS SH1 dispatched on V4 by BestFit", text);
        Assert.Contains("V4", fleet.Split('\n')[2]);   // heaviest vehicle is the first data row
    }
}
