using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Assignment 6: booking states, transitions and the ledger.
public class BookingTests
{
    static BookingAction.Price PriceStandard(string id) => new(id, "Standard", PricePolicies.Standard);

    [Fact]
    public void LedgerOpensARequestedBookingForEachServedShipment()
    {
        var ledger = TestData.SampleLedger();

        Assert.Equal(new[] { "SH1", "SH2", "SH3", "SH5" }, ledger.Bookings.Keys);
        Assert.All(ledger.Bookings.Values, b => Assert.IsType<Booking.Requested>(b));
        Assert.Equal(1, ledger.NextConfirmation);
    }

    [Fact]
    public void PriceThenConfirmGivesQuotedThenConfirmed()
    {
        var (priced, step1) = Ledger.Apply(TestData.SampleLedger(), PriceStandard("SH1"));
        var (confirmed, step2) = Ledger.Apply(priced, new BookingAction.Confirm("SH1"));

        var quoted = Assert.IsType<Booking.Quoted>(Assert.IsType<BookingStep.Moved>(step1).Next);
        Assert.Equal(4400m, quoted.Price);
        Assert.Equal("Standard", quoted.PolicyName);

        var done = Assert.IsType<Booking.Confirmed>(Assert.IsType<BookingStep.Moved>(step2).Next);
        Assert.Equal(1, done.Number);
        Assert.Equal(4400m, done.Price);
        Assert.Same(done, confirmed.Bookings["SH1"]);
        Assert.Equal(2, confirmed.NextConfirmation);
    }

    [Fact]
    public void ConfirmBeforePriceIsRejectedAndChangesNothing()
    {
        var ledger = TestData.SampleLedger();

        var (after, step) = Ledger.Apply(ledger, new BookingAction.Confirm("SH2"));

        Assert.Equal("SH2 has no price yet: price it before confirming", Assert.IsType<BookingStep.Rejected>(step).Reason);
        Assert.Same(ledger, after);
        Assert.IsType<Booking.Requested>(after.Bookings["SH2"]);
        Assert.Equal(1, after.NextConfirmation);
    }

    [Fact]
    public void SecondConfirmReturnsTheExistingConfirmation()
    {
        var (priced, _) = Ledger.Apply(TestData.SampleLedger(), PriceStandard("SH1"));
        var (once, _) = Ledger.Apply(priced, new BookingAction.Confirm("SH1"));

        var (twice, step) = Ledger.Apply(once, new BookingAction.Confirm("SH1"));

        var existing = Assert.IsType<Booking.Confirmed>(Assert.IsType<BookingStep.Repeated>(step).Existing);
        Assert.Equal(1, existing.Number);
        Assert.Same(once, twice);
        Assert.Equal(2, twice.NextConfirmation);
    }

    [Fact]
    public void SecondPriceIsRejectedAndKeepsTheFirstPrice()
    {
        var (priced, _) = Ledger.Apply(TestData.SampleLedger(), PriceStandard("SH3"));

        var (after, step) = Ledger.Apply(priced, new BookingAction.Price("SH3", "Express", PricePolicies.Express));

        Assert.Equal("SH3 is already priced (Standard, 16320)", Assert.IsType<BookingStep.Rejected>(step).Reason);
        Assert.Equal(16320m, Assert.IsType<Booking.Quoted>(after.Bookings["SH3"]).Price);
    }

    [Fact]
    public void ShipmentOverTheLimitHasNoBookingToPrice()
    {
        var ledger = TestData.SampleLedger();

        var (after, step) = Ledger.Apply(ledger, PriceStandard("SH4"));

        Assert.Equal("no open booking for SH4", Assert.IsType<BookingStep.Rejected>(step).Reason);
        Assert.Same(ledger, after);
    }

    [Fact]
    public void FailedActionsLeaveEveryBookingUnchanged()
    {
        var (start, _) = Ledger.Apply(TestData.SampleLedger(), PriceStandard("SH1"));
        var before = start.Bookings.ToArray();

        var (a, _) = Ledger.Apply(start, new BookingAction.Confirm("SH5"));
        var (b, _) = Ledger.Apply(a, PriceStandard("SH1"));
        var (c, _) = Ledger.Apply(b, new BookingAction.Confirm("NOPE"));

        Assert.Same(start, c);
        Assert.Equal(before, c.Bookings.ToArray());
    }

    [Fact]
    public void EveryStateAndStepIsReachableThroughTheRules()
    {
        var shipment = TestData.Ship("A", StorageClass.Frozen, 5m, 40m);
        var requested = BookingRules.Open(shipment);
        var moved = BookingRules.Price(requested, "Express", PricePolicies.Express);
        var quoted = Assert.IsType<Booking.Quoted>(Assert.IsType<BookingStep.Moved>(moved).Next);
        var confirmed = Assert.IsType<Booking.Confirmed>(Assert.IsType<BookingStep.Moved>(BookingRules.Confirm(quoted, 7)).Next);

        Assert.IsType<Booking.Requested>(requested);
        Assert.Equal(5750m, quoted.Price);
        Assert.Equal(7, confirmed.Number);
        Assert.IsType<BookingStep.Repeated>(BookingRules.Confirm(confirmed, 8));
        Assert.IsType<BookingStep.Rejected>(BookingRules.Confirm(requested, 1));
        Assert.IsType<BookingStep.Rejected>(BookingRules.Price(confirmed, "Standard", PricePolicies.Standard));
    }
}
