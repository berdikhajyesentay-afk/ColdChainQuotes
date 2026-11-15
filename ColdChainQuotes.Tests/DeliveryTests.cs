// STARTER TESTS for Estai. Copy to: ColdChainQuotes.Tests/DeliveryTests.cs
// These are real tests. They fail until Delivery.cs (and Readings.cs) are finished. Do not weaken them.
using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Estai: W4 delivery check. Safe ranges, Strict vs Tolerant, DeliveryRules.Complete, breach report, deliver command.
public class DeliveryTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainDelivery-" + Guid.NewGuid());
    readonly string state;
    readonly string seed = Path.Combine(AppContext.BaseDirectory, "samples", "shipments.json");
    readonly string sampleReadings = Path.Combine(AppContext.BaseDirectory, "samples", "readings.csv");

    public DeliveryTests()
    {
        Directory.CreateDirectory(folder);
        state = Path.Combine(folder, "state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static AppError ErrorOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Failure>(result).Error;
    static T ValueOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Success>(result).Value;

    static TemperatureReading R(string id, int minute, decimal celsius) => ValueOf(TemperatureReading.Create(id, minute, celsius));

    // The SH2 trip from samples/readings.csv: Frozen, one spike to -15 C at minute 30.
    static readonly ImmutableArray<TemperatureReading> SingleSpike =
        [R("SH2", 0, -20m), R("SH2", 15, -19.5m), R("SH2", 30, -15m), R("SH2", 45, -19m), R("SH2", 60, -20m)];

    // The SH5 trip: Cold, three readings in a row above 8 C.
    static readonly ImmutableArray<TemperatureReading> SustainedBreach =
        [R("SH5", 0, 5m), R("SH5", 15, 9.5m), R("SH5", 30, 10.5m), R("SH5", 45, 11m), R("SH5", 60, 7m)];

    static Shipment Cold(string id) => TestStates.Ship(id, "FreshFarm", StorageClass.Cold);
    static Shipment Frozen(string id) => TestStates.Ship(id, "IceDream", StorageClass.Frozen);

    // ---- safe ranges (pure, boundaries)

    [Theory]
    [InlineData(StorageClass.Cold, 2.0, true)]
    [InlineData(StorageClass.Cold, 8.0, true)]
    [InlineData(StorageClass.Cold, 8.1, false)]
    [InlineData(StorageClass.Cold, 1.9, false)]
    [InlineData(StorageClass.Frozen, -18.0, true)]
    [InlineData(StorageClass.Frozen, -25.0, true)]
    [InlineData(StorageClass.Frozen, -17.9, false)]
    public void SafeRangeIncludesBothEnds(StorageClass storage, double celsius, bool safe)
    {
        Assert.Equal(safe, SafeRange.IsSafe(R("X", 0, (decimal)celsius), storage));
    }

    // ---- the two policies: same input, different result

    [Fact]
    public void StrictFindsTheSingleSpike()
    {
        var breach = Assert.IsType<Option<string>.Found>(TolerancePolicies.Strict(SingleSpike, StorageClass.Frozen));
        Assert.Contains("minute 30", breach.Value);
    }

    [Fact]
    public void TolerantIgnoresTheSingleSpike()
    {
        Assert.IsType<Option<string>.Missing>(TolerancePolicies.Tolerant(SingleSpike, StorageClass.Frozen));
    }

    [Fact]
    public void BothPoliciesFindASustainedBreach()
    {
        Assert.Contains("minute 15", Assert.IsType<Option<string>.Found>(TolerancePolicies.Strict(SustainedBreach, StorageClass.Cold)).Value);
        Assert.Contains("minute 15 to 30", Assert.IsType<Option<string>.Found>(TolerancePolicies.Tolerant(SustainedBreach, StorageClass.Cold)).Value);
    }

    [Fact]
    public void TolerantCountsARunAtTheVeryEnd()
    {
        ImmutableArray<TemperatureReading> endRun = [R("A", 0, 4m), R("A", 10, 9m), R("A", 20, 9m)];
        Assert.IsType<Option<string>.Found>(TolerancePolicies.Tolerant(endRun, StorageClass.Cold));
    }

    [Fact]
    public void FindKnowsBothPoliciesAndNothingElse()
    {
        Assert.Equal("Strict", Assert.IsType<Option<(string Name, Func<IReadOnlyList<TemperatureReading>, StorageClass, Option<string>> FindBreach)>.Found>(TolerancePolicies.Find("STRICT")).Value.Name);
        Assert.Equal("Tolerant", Assert.IsType<Option<(string Name, Func<IReadOnlyList<TemperatureReading>, StorageClass, Option<string>> FindBreach)>.Found>(TolerancePolicies.Find("tolerant")).Value.Name);
        Assert.IsType<Option<(string Name, Func<IReadOnlyList<TemperatureReading>, StorageClass, Option<string>> FindBreach)>.Missing>(TolerancePolicies.Find("lenient"));
    }

    // ---- DeliveryRules.Complete (pure)

    [Fact]
    public void SameReadingsSpoiledUnderStrictDeliveredUnderTolerant()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Frozen("SH2")));

        var strict = ValueOf(DeliveryRules.Complete(ledger, "SH2", SingleSpike, TolerancePolicies.Strict));
        var tolerant = ValueOf(DeliveryRules.Complete(ledger, "SH2", SingleSpike, TolerancePolicies.Tolerant));

        Assert.Contains("minute 30", Assert.IsType<Booking.Spoiled>(strict.Bookings["SH2"]).Reason);
        Assert.Equal(5, Assert.IsType<Booking.Delivered>(tolerant.Bookings["SH2"]).ReadingCount);
        Assert.IsType<Booking.Dispatched>(ledger.Bookings["SH2"]);   // the caller's ledger is unchanged
    }

    [Fact]
    public void ReadingsOfOtherShipmentsAreIgnored()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Cold("SH1")));
        ImmutableArray<TemperatureReading> mixed = [.. SustainedBreach, R("SH1", 0, 4m), R("SH1", 15, 5m)];

        var next = ValueOf(DeliveryRules.Complete(ledger, "SH1", mixed, TolerancePolicies.Strict));
        Assert.Equal(2, Assert.IsType<Booking.Delivered>(next.Bookings["SH1"]).ReadingCount);
    }

    [Fact]
    public void UnknownShipmentIsMissingBooking()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Cold("SH1")));
        Assert.Equal("missing_booking", ErrorOf(DeliveryRules.Complete(ledger, "SH9", SingleSpike, TolerancePolicies.Strict)).Code);
    }

    [Fact]
    public void ConfirmedButNotDispatchedIsRejectedAndNoPolicyRuns()
    {
        var ledger = TestStates.LedgerOf(TestStates.Confirmed(Frozen("SH2")));
        int calls = 0;
        Option<string> Spy(IReadOnlyList<TemperatureReading> r, StorageClass s) { calls++; return Option<string>.None(); }

        var error = ErrorOf(DeliveryRules.Complete(ledger, "SH2", SingleSpike, Spy));
        Assert.Equal("not_dispatched", error.Code);
        Assert.Equal(0, calls);                 // Bind stopped: the later step never ran
    }

    [Fact]
    public void ABookingCannotBeDeliveredTwice()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Frozen("SH2")));
        var once = ValueOf(DeliveryRules.Complete(ledger, "SH2", SingleSpike, TolerancePolicies.Tolerant));
        Assert.Equal("not_dispatched", ErrorOf(DeliveryRules.Complete(once, "SH2", SingleSpike, TolerancePolicies.Tolerant)).Code);
    }

    [Fact]
    public void NoReadingsForTheShipmentIsMissingReadings()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Cold("SH3")));
        Assert.Equal("missing_readings", ErrorOf(DeliveryRules.Complete(ledger, "SH3", SingleSpike, TolerancePolicies.Strict)).Code);
    }

    [Fact]
    public void MinutesOutOfOrderAreRejected()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(Frozen("SH6")));
        ImmutableArray<TemperatureReading> unordered = [R("SH6", 0, -20m), R("SH6", 30, -19m), R("SH6", 15, -19.5m)];
        Assert.Equal("unordered_readings", ErrorOf(DeliveryRules.Complete(ledger, "SH6", unordered, TolerancePolicies.Strict)).Code);
    }

    // ---- breach report (LINQ)

    [Fact]
    public void ReportGroupsClosedDeliveriesByStorageClass()
    {
        var ledger = TestStates.LedgerOf(
            TestStates.Delivered(Cold("C1"), number: 1),
            TestStates.Spoiled(Cold("C2"), number: 2),
            TestStates.Invoiced(Frozen("F1"), number: 3),
            TestStates.Credited(Frozen("F2"), number: 4),
            TestStates.Confirmed(Cold("C3"), number: 5));   // not closed: left out

        var lines = DeliveryReports.ByStorageClass(ledger);

        // Both have 1 spoiled: the tie is broken by name, Cold before Frozen.
        Assert.Equal([new DeliveryLine(StorageClass.Cold, 1, 1, 2), new DeliveryLine(StorageClass.Frozen, 1, 1, 2)], lines);
    }

    [Fact]
    public void ReportOfALedgerWithNoClosedDeliveriesIsEmpty()
    {
        Assert.Empty(DeliveryReports.ByStorageClass(TestStates.LedgerOf(TestStates.Dispatched(Cold("SH1")))));
        Assert.Empty(DeliveryReports.ByStorageClass(TestStates.LedgerOf()));
    }

    // ---- complete workflows through the deliver command (real files in a temp folder)

    string[] With(params string[] args) => [.. args, "--state", state, "--seed", seed];

    void SaveState(Ledger ledger) => ValueOf(StateStore.Save(state, ledger));

    [Fact]
    public void DeliverCommandSpoilsSh2UnderStrictAndSavesIt()
    {
        SaveState(TestStates.LedgerOf(TestStates.Dispatched(Frozen("SH2"))));
        var output = new StringWriter();

        var exit = DeliveryConsole.Run(With("SH2", "strict", sampleReadings), output);

        Assert.Equal(0, exit);
        Assert.StartsWith("SUCCESS SH2 spoiled (Strict)", output.ToString());
        Assert.IsType<Booking.Spoiled>(ValueOf(StateStore.Load(state)).Bookings["SH2"]);
    }

    [Fact]
    public void DeliverCommandDeliversSh2UnderTolerantAndTheReportCountsIt()
    {
        SaveState(TestStates.LedgerOf(TestStates.Dispatched(Frozen("SH2"))));

        var deliver = new StringWriter();
        Assert.Equal(0, DeliveryConsole.Run(With("SH2", "tolerant", sampleReadings), deliver));
        Assert.StartsWith("SUCCESS SH2 delivered (Tolerant): 5 readings", deliver.ToString());

        var report = new StringWriter();
        Assert.Equal(0, DeliveryConsole.Run(With("report"), report));
        Assert.Contains("Frozen", report.ToString());
    }

    [Fact]
    public void AFailedDeliveryLeavesTheSavedStateUnchanged()
    {
        SaveState(TestStates.LedgerOf(TestStates.Confirmed(Cold("SH1"))));
        var before = File.ReadAllText(state);
        var output = new StringWriter();

        var exit = DeliveryConsole.Run(With("SH1", "strict", sampleReadings), output);

        Assert.Equal(1, exit);
        Assert.StartsWith("ERROR [not_dispatched]", output.ToString());
        Assert.Equal(before, File.ReadAllText(state));
    }

    [Fact]
    public void DeliverCommandReportsAMissingReadingsFile()
    {
        SaveState(TestStates.LedgerOf(TestStates.Dispatched(Cold("SH1"))));
        var output = new StringWriter();
        Assert.Equal(1, DeliveryConsole.Run(With("SH1", "strict", Path.Combine(folder, "nope.csv")), output));
        Assert.StartsWith("ERROR [file_missing]", output.ToString());
    }

    [Fact]
    public void DeliverCommandRejectsAnUnknownPolicy()
    {
        SaveState(TestStates.LedgerOf(TestStates.Dispatched(Cold("SH1"))));
        var output = new StringWriter();
        Assert.Equal(1, DeliveryConsole.Run(With("SH1", "lenient", sampleReadings), output));
        Assert.StartsWith("ERROR [invalid_policy]", output.ToString());
    }
}
