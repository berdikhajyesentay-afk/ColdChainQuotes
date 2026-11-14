using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Yesyentai: shared foundation. Lifecycle states, saved state, and the state commands.
public class StateStoreTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainState-" + Guid.NewGuid());
    readonly string state;
    readonly string seed;

    public StateStoreTests()
    {
        Directory.CreateDirectory(folder);
        state = Path.Combine(folder, "data", "state.json");
        seed = Path.Combine(AppContext.BaseDirectory, "samples", "shipments.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static Ledger AllStates() =>
        TestStates.LedgerOf(
            new Booking.Requested(TestStates.Ship("A1", "FreshFarm")),
            TestStates.Confirmed(TestStates.Ship("A2", "FreshFarm"), number: 1),
            TestStates.Dispatched(TestStates.Ship("A3", "IceDream", StorageClass.Frozen), "V2", DistanceTier.Regional, number: 2),
            TestStates.Delivered(TestStates.Ship("A4", "IceDream"), readings: 4, number: 3),
            TestStates.Spoiled(TestStates.Ship("A5", "CityLab"), number: 4),
            TestStates.Invoiced(TestStates.Ship("A6", "CityLab"), "INV-0001", number: 5),
            TestStates.Credited(TestStates.Ship("A7", "CityLab"), "INV-0001", number: 6))
        with { NextConfirmation = 7, NextInvoice = 2 };

    string[] With(params string[] args) => [.. args, "--state", state, "--seed", seed];

    static AppError ErrorOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Failure>(result).Error;

    [Fact]
    public void LaterStatesCarryPriceAndNumberForward()
    {
        var shipment = TestStates.Ship("A1", "FreshFarm", weightKg: 60m, distanceKm: 20m);
        var dispatched = TestStates.Dispatched(shipment, "V7", DistanceTier.Local, number: 4);

        Assert.Equal(4400m, dispatched.Price);
        Assert.Equal(4, dispatched.Number);
        Assert.Equal("V7", dispatched.VehicleId);
        var credited = new Booking.Credited(new Booking.Spoiled(dispatched, "too warm"), "INV-0003");
        Assert.Equal((4400m, 4, "too warm", "INV-0003"), (credited.Price, credited.Number, credited.Reason, credited.InvoiceNumber));
    }

    [Fact]
    public void PricingOrConfirmingALaterStateIsRejectedAndChangesNothing()
    {
        var ledger = TestStates.LedgerOf(TestStates.Dispatched(TestStates.Ship("A1", "FreshFarm")));

        var (afterPrice, priceStep) = Ledger.Apply(ledger, new BookingAction.Price("A1", "Express", PricePolicies.Express));
        var (afterConfirm, confirmStep) = Ledger.Apply(ledger, new BookingAction.Confirm("A1"));

        Assert.Contains("already dispatched", Assert.IsType<BookingStep.Rejected>(priceStep).Reason);
        Assert.Contains("already dispatched", Assert.IsType<BookingStep.Rejected>(confirmStep).Reason);
        Assert.Same(ledger, afterPrice);
        Assert.Same(ledger, afterConfirm);
    }

    [Fact]
    public void ConfirmingKeepsTheInvoiceCounter()
    {
        var start = TestData.SampleLedger() with { NextInvoice = 5 };

        var (priced, _) = Ledger.Apply(start, new BookingAction.Price("SH1", "Standard", PricePolicies.Standard));
        var (confirmed, _) = Ledger.Apply(priced, new BookingAction.Confirm("SH1"));

        Assert.Equal(2, confirmed.NextConfirmation);
        Assert.Equal(5, confirmed.NextInvoice);
    }

    [Fact]
    public void SaveThenLoadGivesBackEveryStateAndBothCounters()
    {
        var ledger = AllStates();

        Assert.IsType<Result<string>.Success>(StateStore.Save(state, ledger));
        var loaded = Assert.IsType<Result<Ledger>.Success>(StateStore.Load(state)).Value;

        Assert.Equal(ledger.Bookings.Keys, loaded.Bookings.Keys);
        Assert.Equal(ledger.Bookings.Values, loaded.Bookings.Values);
        Assert.Equal(7, loaded.NextConfirmation);
        Assert.Equal(2, loaded.NextInvoice);
        Assert.False(File.Exists(state + ".tmp"));
    }

    [Fact]
    public void LoadOrSeedCreatesTheStateFileFromTheSeedOnFirstRun()
    {
        Assert.False(File.Exists(state));

        var ledger = Assert.IsType<Result<Ledger>.Success>(StateStore.LoadOrSeed(state, seed)).Value;

        Assert.True(File.Exists(state));
        Assert.Equal(new[] { "SH1", "SH2", "SH3", "SH5" }, ledger.Bookings.Keys);
        Assert.All(ledger.Bookings.Values, b => Assert.IsType<Booking.Requested>(b));
    }

    [Fact]
    public void MissingStateAndMissingSeedAreFileMissing()
    {
        Assert.Equal("file_missing", ErrorOf(StateStore.Load(state)).Code);
        Assert.Equal("file_missing", ErrorOf(StateStore.LoadOrSeed(state, Path.Combine(folder, "nope.json"))).Code);
        Assert.False(File.Exists(state));
    }

    [Theory]
    [InlineData("{ not json", "malformed_data")]
    [InlineData("[]", "malformed_data")]
    [InlineData("""{"nextConfirmation":1,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Lost"}]}""", "malformed_data")]
    [InlineData("""{"nextConfirmation":1,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":-1,"distanceKm":1,"state":"Requested"}]}""", "invalid_shipment")]
    [InlineData("""{"nextConfirmation":1,"bookings":[{"id":"A","customer":"C","storageClass":"Warm","weightKg":1,"distanceKm":1,"state":"Requested"}]}""", "invalid_shipment")]
    [InlineData("""{"nextConfirmation":2,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Dispatched","policyName":"Standard","price":10,"number":1}]}""", "malformed_data")]
    [InlineData("""{"nextConfirmation":1,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Confirmed","policyName":"Standard","price":10,"number":1}]}""", "malformed_data")]
    [InlineData("""{"nextConfirmation":3,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Confirmed","policyName":"Standard","price":10,"number":1},{"id":"B","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Confirmed","policyName":"Standard","price":10,"number":1}]}""", "malformed_data")]
    [InlineData("""{"nextConfirmation":1,"bookings":[{"id":"A","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Requested"},{"id":"a","customer":"C","storageClass":"Cold","weightKg":1,"distanceKm":1,"state":"Requested"}]}""", "duplicate_id")]
    public void ABadStateFileIsRejectedWithACode(string json, string code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, json);

        Assert.Equal(code, ErrorOf(StateStore.Load(state)).Code);
    }

    [Fact]
    public void AFailedSaveLeavesTheOldStateFileAsItWas()
    {
        Assert.IsType<Result<string>.Success>(StateStore.Save(state, TestData.SampleLedger()));
        var before = File.ReadAllText(state);
        Directory.CreateDirectory(state + ".tmp"); // the temporary file cannot be created

        var saved = StateStore.Save(state, AllStates());

        Assert.Equal("save_failed", ErrorOf(saved).Code);
        Assert.Equal(before, File.ReadAllText(state));
    }

    [Fact]
    public void ResolvePathsTakesStateAndSeedOutOfTheArguments()
    {
        var (args, statePath, seedPath) = StateStore.ResolvePaths(["SH1", "--state", "x.json", "express", "--seed", "s.json"]);

        Assert.Equal(new[] { "SH1", "express" }, args);
        Assert.Equal(("x.json", "s.json"), (statePath, seedPath));
    }

    [Fact]
    public void ConfirmCommandSavesTheConfirmedBookingThenStatusShowsIt()
    {
        var output = new StringWriter();

        var code = Program.Run(With("confirm", "SH1", "express"), TextReader.Null, output);
        var status = Program.Run(With("status"), TextReader.Null, output);

        Assert.Equal((0, 0), (code, status));
        Assert.Contains("SUCCESS SH1 confirmed as #1, 4200 (Express)", output.ToString());
        var saved = Assert.IsType<Result<Ledger>.Success>(StateStore.Load(state)).Value;
        Assert.IsType<Booking.Confirmed>(saved.Bookings["SH1"]);
        Assert.Contains("Confirmed #1", output.ToString());
    }

    [Fact]
    public void AFailedConfirmCommandLeavesTheSavedStateUnchanged()
    {
        Program.Run(With("confirm", "SH1"), TextReader.Null, new StringWriter());
        var before = File.ReadAllText(state);
        var output = new StringWriter();

        var again = Program.Run(With("confirm", "SH1", "express"), TextReader.Null, output);
        var unknown = Program.Run(With("confirm", "NOPE"), TextReader.Null, output);

        Assert.Equal((1, 1), (again, unknown));
        Assert.Contains("ERROR [state_rejected]", output.ToString());
        Assert.Contains("ERROR [missing_booking]", output.ToString());
        Assert.Equal(before, File.ReadAllText(state));
    }

    [Fact]
    public void ResetRestoresTheSeedAndMenuRunsTheSameCommands()
    {
        Program.Run(With("confirm", "SH1"), TextReader.Null, new StringWriter());
        var output = new StringWriter();

        Assert.Equal(0, Program.Run(With("reset"), TextReader.Null, output));
        Assert.IsType<Booking.Requested>(Assert.IsType<Result<Ledger>.Success>(StateStore.Load(state)).Value.Bookings["SH1"]);

        // Menu: 7 = status (no arguments), 9 = not a menu number, 0 = exit.
        Environment.SetEnvironmentVariable("CCQ_STATE", state);
        try
        {
            var menuOut = new StringWriter();
            Assert.Equal(0, Program.Run(["menu"], new StringReader("7\n9\n0\n"), menuOut));
            Assert.Contains("SH1   FreshFarm  Requested", menuOut.ToString());
            Assert.Contains("'9' is not a menu number.", menuOut.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CCQ_STATE", null);
        }
    }
}
