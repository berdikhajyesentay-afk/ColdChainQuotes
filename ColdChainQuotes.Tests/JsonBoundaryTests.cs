using ColdChainQuotes;
namespace ColdChainQuotes.Tests;

public class JsonBoundaryTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainQuotes-" + Guid.NewGuid());
    public JsonBoundaryTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    string Write(string text)
    {
        var path = Path.Combine(folder, "shipments.json");
        File.WriteAllText(path, text);
        return path;
    }
    const string Valid = """[{"id":"SH1","customer":"FreshFarm","storageClass":"Cold","weightKg":60,"distanceKm":20},{"id":"SH5","customer":"CityLab","storageClass":"Cold","weightKg":2,"distanceKm":0}]""";

    [Fact]
    public void JsonDataRunsThroughRealBookingChain()
    {
        var ledger = Assert.IsType<Result<Ledger>.Success>(JsonBoundary.Load(Write(Valid))).Value;
        Assert.Equal(0m, ledger.Bookings["SH5"].Shipment.DistanceKm);
        Assert.Equal(4400m, Assert.IsType<Result<BookingReceipt>.Success>(ResultBookings.Book(ledger, "SH1", "standard")).Value.Price);
    }
    [Fact]
    public void MissingFileIsClearFailureAndPreservesState()
    {
        var start = TestData.SampleLedger();
        var path = Path.Combine(folder, "missing.json");
        var result = JsonBoundary.Reload(start, path);
        Assert.Same(start, result.Ledger);
        var error = Assert.IsType<Result<Ledger>.Failure>(result.Outcome).Error;
        Assert.Equal("file_missing", error.Code);
        Assert.Contains(path, error.Message);
        Assert.Equal(1, start.NextConfirmation);
    }
    [Theory]
    [InlineData("[{broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"X\",\"customer\":\"C\",\"storageClass\":\"Cold\",\"weightKg\":1}]")]
    public void MalformedDataIsFailureWithoutPartialUpdate(string text)
    {
        var path = Write(text);
        var start = TestData.SampleLedger();
        var result = JsonBoundary.Reload(start, path);
        Assert.Same(start, result.Ledger);
        var error = Assert.IsType<Result<Ledger>.Failure>(result.Outcome).Error;
        Assert.Equal("malformed_data", error.Code);
        Assert.Contains(path, error.Message);
        Assert.Equal(4, start.Bookings.Count);
    }
    [Fact]
    public void InvalidLaterRowDoesNotCommitEarlierValidRow()
    {
        string text = Valid[..^1] + "," + """{"id":"BAD","customer":"Bad","storageClass":"Cold","weightKg":0,"distanceKm":2}]""";
        var start = TestData.SampleLedger();
        var result = JsonBoundary.Reload(start, Write(text));
        Assert.Same(start, result.Ledger);
        var error = Assert.IsType<Result<Ledger>.Failure>(result.Outcome).Error;
        Assert.Equal("invalid_shipment", error.Code);
        Assert.Contains("weight", error.Message);
        Assert.Contains("BAD", error.Message);
    }
    [Fact]
    public void DuplicateIdsRejectEntireFile()
    {
        var result = JsonBoundary.Load(Write(Valid[..^1] + "," + """{"id":"sh1","customer":"Duplicate","storageClass":"Cold","weightKg":1,"distanceKm":0}]"""));
        Assert.Equal("duplicate_id", Assert.IsType<Result<Ledger>.Failure>(result).Error.Code);
    }
    [Fact]
    public void ValidReloadReplacesStateOnlyAfterAllRowsValidate()
    {
        var start = TestData.SampleLedger();
        var result = JsonBoundary.Reload(start, Write(Valid));
        Assert.IsType<Result<Ledger>.Success>(result.Outcome);
        Assert.NotSame(start, result.Ledger);
        Assert.Equal(2, result.Ledger.Bookings.Count);
        Assert.Equal(4, start.Bookings.Count);
    }
    [Fact]
    public void EmptyArrayIsValidEmptyData()
        => Assert.Empty(Assert.IsType<Result<Ledger>.Success>(JsonBoundary.Load(Write("[]"))).Value.Bookings);
}
