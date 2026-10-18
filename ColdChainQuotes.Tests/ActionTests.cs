using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Assignment 6: reading the actions file and describing every result case.
public class ActionTests
{
    [Fact]
    public void ValidLinesBecomeTypedActions()
    {
        var price = Assert.IsType<ActionLine.Parsed>(Actions.Parse("PRICE sh1 Express"));
        var confirm = Assert.IsType<ActionLine.Parsed>(Actions.Parse("confirm SH1"));

        var priceAction = Assert.IsType<BookingAction.Price>(price.Action);
        Assert.Equal("sh1", priceAction.ShipmentId);
        Assert.Equal("Express", priceAction.PolicyName);
        Assert.Equal("SH1", Assert.IsType<BookingAction.Confirm>(confirm.Action).ShipmentId);
    }

    [Theory]
    [InlineData("cancel SH5", "unknown action 'cancel'. Use price or confirm.")]
    [InlineData("price SH1 cheap", "unknown policy 'cheap'. Use standard or express.")]
    [InlineData("price SH1", "price needs a shipment id and a policy: price <id> <standard|express>")]
    [InlineData("confirm", "confirm needs exactly one shipment id: confirm <id>")]
    public void BadLinesAreUnreadableWithAReason(string line, string reason)
    {
        Assert.Equal(reason, Assert.IsType<ActionLine.Unreadable>(Actions.Parse(line)).Reason);
    }

    [Fact]
    public void SampleActionsGiveTheDocumentedOutcomes()
    {
        var (final, results) = Actions.Run(TestData.SampleLedger(), TestData.SuppliedActions());

        Assert.Equal(new[]
        {
            "OK        SH1 quoted 4400 (Standard)",
            "OK        SH1 confirmed as #1, 4400",
            "REPEATED  SH1 was already confirmed as #1; nothing changed",
            "REJECTED  SH2 has no price yet: price it before confirming",
            "REJECTED  no open booking for SH4",
            "OK        SH3 quoted 26080 (Express)",
            "REJECTED  SH3 is already priced (Express, 26080)",
            "INVALID   unknown action 'cancel'. Use price or confirm."
        }, results.Select(Actions.Describe));
        Assert.Equal(new[] { "Confirmed #1  4400 (Standard)", "Requested", "Quoted        26080 (Express)", "Requested" },
            final.Bookings.Values.Select(Actions.DescribeState));
    }

    [Fact]
    public void CommentsAndBlankLinesAreSkippedButCounted()
    {
        var (_, results) = Actions.Run(TestData.SampleLedger(), new[] { "# note", "", "confirm SH1" });

        var only = Assert.IsType<ActionResult.Applied>(Assert.Single(results));
        Assert.Equal(3, only.LineNumber);
    }

    [Fact]
    public void RunDoesNotChangeTheStartingLedger()
    {
        var start = TestData.SampleLedger();

        Actions.Run(start, TestData.SuppliedActions());

        Assert.All(start.Bookings.Values, b => Assert.IsType<Booking.Requested>(b));
        Assert.Equal(1, start.NextConfirmation);
    }

    [Fact]
    public void EmptyActionsFileLeavesAllBookingsRequested()
    {
        var (final, results) = Actions.Run(TestData.SampleLedger(), Array.Empty<string>());

        Assert.Empty(results);
        Assert.All(final.Bookings.Values, b => Assert.Equal("Requested", Actions.DescribeState(b)));
    }
}
