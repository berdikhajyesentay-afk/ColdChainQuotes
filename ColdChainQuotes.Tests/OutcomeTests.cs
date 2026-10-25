using ColdChainQuotes;
namespace ColdChainQuotes.Tests;

public class OutcomeTests
{
    [Fact]
    public void MapTransformsSuccess() => Assert.Equal(8, Assert.IsType<Result<int>.Success>(Result<int>.Ok(4).Map(x => x * 2)).Value);

    [Fact]
    public void BindPreservesReturnedResult()
    {
        var next = Result<string>.Ok("eight");
        Assert.Same(next, Result<int>.Ok(8).Bind(x => x == 8 ? next : Result<string>.Fail(new("wrong", "wrong value"))));
    }

    [Fact]
    public void FailureSkipsMapAndBind()
    {
        int calls = 0;
        var error = new AppError("missing_booking", "No booking X");
        var result = Result<int>.Fail(error).Map(x => { calls++; return x + 1; })
            .Bind(x => { calls++; return Result<int>.Ok(x); });
        Assert.Same(error, Assert.IsType<Result<int>.Failure>(result).Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RequiredPayloadsCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string>.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Fail(null!));
        Assert.Throws<ArgumentNullException>(() => Option<string>.Some(null!));
    }

    [Fact]
    public void PrimitiveZeroAndFalseArePresent()
    {
        Assert.Equal(0, Assert.IsType<Option<int>.Found>(Option<int>.Some(0)).Value);
        Assert.False(Assert.IsType<Option<bool>.Found>(Option<bool>.Some(false)).Value);
        Assert.Equal("", Assert.IsType<Option<string>.Found>(Option<string>.Some("")).Value);
    }

    [Fact]
    public void RealLookupIsCaseInsensitiveAndHandlesMissingAndZero()
    {
        var ledger = TestData.SampleLedger();
        Assert.Equal(0m, Assert.IsType<Option<Booking>.Found>(ResultBookings.Find(ledger, "sh5")).Value.Shipment.DistanceKm);
        Assert.IsType<Option<Booking>.Missing>(ResultBookings.Find(ledger, "unknown"));
    }

    [Fact]
    public void CompleteChainQuotesConfirmsAndKeepsOriginalLedger()
    {
        var start = TestData.SampleLedger();
        var done = Assert.IsType<Result<BookingReceipt>.Success>(ResultBookings.Book(start, "SH1", "standard")).Value;
        Assert.Equal(4400m, done.Price);
        Assert.Equal(1, done.Number);
        Assert.Contains("SH1", done.Display);
        Assert.IsType<Booking.Confirmed>(done.Ledger.Bookings["SH1"]);
        Assert.Equal(2, done.Ledger.NextConfirmation);
        Assert.IsType<Booking.Requested>(start.Bookings["SH1"]);
        Assert.Equal(1, start.NextConfirmation);
    }

    [Theory]
    [InlineData("absent", "standard", "missing_booking")]
    [InlineData("SH1", "unknown", "invalid_policy")]
    public void DomainFailuresHaveUsefulCodesAndKeepState(string id, string policy, string code)
    {
        var start = TestData.SampleLedger();
        var error = Assert.IsType<Result<BookingReceipt>.Failure>(ResultBookings.Book(start, id, policy)).Error;
        Assert.Equal(code, error.Code);
        Assert.Contains(code == "missing_booking" ? id : policy, error.Message);
        Assert.IsType<Booking.Requested>(start.Bookings["SH1"]);
        Assert.Equal(1, start.NextConfirmation);
    }

    [Fact]
    public void AlreadyPricedBookingIsRejectedWithoutChangingIt()
    {
        var start = TestData.SampleLedger();
        var (priced, _) = Ledger.Apply(start, new BookingAction.Price("SH1", "Standard", PricePolicies.Standard));
        var failure = Assert.IsType<Result<BookingReceipt>.Failure>(ResultBookings.Book(priced, "SH1", "express"));
        Assert.Equal("state_rejected", failure.Error.Code);
        Assert.Contains("SH1", failure.Error.Message);
        Assert.Equal(4400m, Assert.IsType<Booking.Quoted>(priced.Bookings["SH1"]).Price);
        Assert.Equal(1, priced.NextConfirmation);
    }

    [Fact]
    public void MissingBookingSkipsInvalidPolicy()
    {
        Assert.Equal("missing_booking", Assert.IsType<Result<BookingReceipt>.Failure>(ResultBookings.Book(TestData.SampleLedger(), "absent", "bad")).Error.Code);
    }
}
