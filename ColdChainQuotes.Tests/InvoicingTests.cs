using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Byerdikhajy: W5 Invoice. Curried discounts, the pure Build rule, and the invoice command.
public class InvoicingTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainInvoice-" + Guid.NewGuid());
    readonly string state;
    readonly string seed = Path.Combine(AppContext.BaseDirectory, "samples", "shipments.json");

    public InvoicingTests()
    {
        Directory.CreateDirectory(folder);
        state = Path.Combine(folder, "state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static readonly DiscountRule Loyalty = new("Loyalty", Discounts.Loyalty);
    static readonly DiscountRule Bulk = new("Bulk", Discounts.Bulk);

    // FreshFarm: two Delivered (4400 + 2400), one Spoiled (2400), one Confirmed (not billable yet).
    // IceDream: only Invoiced. CityLab: only Confirmed.
    static Ledger Sample() =>
        TestStates.LedgerOf(
            TestStates.Delivered(TestStates.Ship("A1", "FreshFarm", weightKg: 60m), number: 1),
            TestStates.Spoiled(TestStates.Ship("A2", "FreshFarm"), number: 2),
            TestStates.Confirmed(TestStates.Ship("A3", "FreshFarm"), number: 3),
            TestStates.Delivered(TestStates.Ship("A4", "FreshFarm"), number: 4),
            TestStates.Invoiced(TestStates.Ship("B1", "IceDream"), "INV-0001", number: 5),
            TestStates.Confirmed(TestStates.Ship("C1", "CityLab"), number: 6))
        with { NextConfirmation = 7, NextInvoice = 2 };

    static AppError ErrorOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Failure>(result).Error;

    [Fact]
    public void CurriedDiscountIsConfiguredOneSettingAtATime()
    {
        var halfFrom100 = Discounts.Create(100m)(50m);

        Assert.Equal(100m, halfFrom100(200m));
        Assert.Equal(0m, halfFrom100(99.99m));
        Assert.Equal((0m, 250m), (Discounts.Loyalty(4_999.99m), Discounts.Loyalty(5_000m)));
        Assert.Equal((0m, 1_000m), (Discounts.Bulk(9_999.99m), Discounts.Bulk(10_000m)));
    }

    [Theory]
    [InlineData(6_000, 300, 0)]
    [InlineData(12_000, 600, 1_200)]
    public void LoyaltyAndBulkGiveADocumentedDifferenceOnTheSameSubtotal(decimal subtotal, decimal loyalty, decimal bulk)
    {
        Assert.Equal(loyalty, Discounts.Loyalty(subtotal));
        Assert.Equal(bulk, Discounts.Bulk(subtotal));
    }

    [Fact]
    public void BuildBillsDeliveredCreditsSpoiledAndMovesBothOn()
    {
        var ledger = Sample();

        var (next, invoice) = Assert.IsType<Result<(Ledger, Invoice)>.Success>(Invoicing.Build(ledger, "freshfarm", Loyalty)).Value;

        Assert.Equal("INV-0002", invoice.Number);
        Assert.Equal("FreshFarm", invoice.Customer);
        Assert.Equal(new[] { "A1", "A2", "A4" }, invoice.Lines.Select(l => l.ShipmentId));
        Assert.Equal(new[] { 4400m, -2400m, 2400m }, invoice.Lines.Select(l => l.Amount));
        Assert.Equal((6800m, 2400m, 340m, 4060m), (invoice.Subtotal, invoice.Credit, invoice.Discount, invoice.Total));

        Assert.Equal("INV-0002", Assert.IsType<Booking.Invoiced>(next.Bookings["A1"]).InvoiceNumber);
        Assert.Equal("INV-0002", Assert.IsType<Booking.Credited>(next.Bookings["A2"]).InvoiceNumber);
        Assert.IsType<Booking.Confirmed>(next.Bookings["A3"]);
        Assert.Equal(3, next.NextInvoice);
        // The caller's ledger is unchanged.
        Assert.IsType<Booking.Delivered>(ledger.Bookings["A1"]);
        Assert.Equal(2, ledger.NextInvoice);
    }

    [Fact]
    public void TheSameBookingsGiveDifferentTotalsUnderLoyaltyAndBulk()
    {
        var loyalty = Assert.IsType<Result<(Ledger, Invoice)>.Success>(Invoicing.Build(Sample(), "FreshFarm", Loyalty)).Value.Item2;
        var bulk = Assert.IsType<Result<(Ledger, Invoice)>.Success>(Invoicing.Build(Sample(), "FreshFarm", Bulk)).Value.Item2;

        Assert.Equal(4060m, loyalty.Total);
        Assert.Equal(4400m, bulk.Total);
    }

    [Fact]
    public void ASecondInvoiceIsRejectedSoACreditIsNeverAppliedTwice()
    {
        var first = Assert.IsType<Result<(Ledger, Invoice)>.Success>(Invoicing.Build(Sample(), "FreshFarm", Loyalty)).Value.Item1;

        var second = Invoicing.Build(first, "FreshFarm", Loyalty);

        Assert.Equal("already_invoiced", ErrorOf(second).Code);
    }

    [Theory]
    [InlineData("Nobody", "missing_customer")]
    [InlineData("CityLab", "nothing_to_bill")]
    [InlineData("IceDream", "already_invoiced")]
    public void EachDomainFailureHasItsOwnCode(string customer, string code)
    {
        Assert.Equal(code, ErrorOf(Invoicing.Build(Sample(), customer, Loyalty)).Code);
    }

    [Fact]
    public void FindCustomerIsExplicitAboutMissingCustomers()
    {
        Assert.Equal("FreshFarm", Assert.IsType<Option<string>.Found>(Invoicing.FindCustomer(Sample(), " FRESHFARM ")).Value);
        Assert.IsType<Option<string>.Missing>(Invoicing.FindCustomer(Sample(), "Nobody"));
        Assert.IsType<Option<DiscountRule>.Missing>(Discounts.Find("half-price"));
    }

    [Fact]
    public void InvoiceCommandSavesTheStateAndExportsTheSameLines()
    {
        StateStore.Save(state, Sample());
        var export = Path.Combine(folder, "inv.txt");
        var output = new StringWriter();

        var code = Program.Run(["invoice", "FreshFarm", "loyalty", "--export", export, "--state", state, "--seed", seed], TextReader.Null, output);

        Assert.Equal(0, code);
        Assert.Contains("SUCCESS INV-0002 saved, exported to", output.ToString());
        var saved = Assert.IsType<Result<Ledger>.Success>(StateStore.Load(state)).Value;
        Assert.IsType<Booking.Invoiced>(saved.Bookings["A1"]);
        Assert.IsType<Booking.Credited>(saved.Bookings["A2"]);
        Assert.Equal(3, saved.NextInvoice);
        var exported = File.ReadAllLines(export);
        Assert.Equal("Invoice INV-0002 for FreshFarm (Loyalty discount)", exported[0]);
        Assert.Equal("  Total                       4060", exported[^1]);
    }

    [Fact]
    public void FailedInvoiceCommandsLeaveTheSavedStateUnchanged()
    {
        StateStore.Save(state, Sample());
        var before = File.ReadAllText(state);
        var output = new StringWriter();
        string[] With(params string[] a) => [.. a, "--state", state, "--seed", seed];

        var codes = new[]
        {
            Program.Run(With("invoice", "Nobody"), TextReader.Null, output),
            Program.Run(With("invoice", "FreshFarm", "half-price"), TextReader.Null, output),
            Program.Run(With("invoice", "FreshFarm", "bulk", "--export", Path.Combine(folder, "no-folder", "x.txt")), TextReader.Null, output),
            Program.Run(With("invoice"), TextReader.Null, output)
        };

        Assert.All(codes, c => Assert.Equal(1, c));
        var text = output.ToString();
        Assert.Contains("ERROR [missing_customer]", text);
        Assert.Contains("ERROR [invalid_discount]", text);
        Assert.Contains("ERROR [export_failed]", text);
        Assert.Contains("ERROR [usage]", text);
        Assert.Equal(before, File.ReadAllText(state));
    }
}
