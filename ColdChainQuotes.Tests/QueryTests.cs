using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Estai: the ByStorageClass extension and the Require Result extension.
public class QueryTests
{
    [Fact]
    public void ByStorageClassKeepsMatchingShipmentsInInputOrder()
    {
        var shipments = new[]
        {
            TestData.Ship("A", StorageClass.Cold, 1m, 1m),
            TestData.Ship("B", StorageClass.Frozen, 1m, 1m),
            TestData.Ship("C", StorageClass.Cold, 1m, 1m)
        };

        Assert.Equal(new[] { "A", "C" }, shipments.ByStorageClass(StorageClass.Cold).Select(s => s.Id));
    }

    [Fact]
    public void ByStorageClassReturnsEmptyForEmptyInputOrNoMatch()
    {
        Assert.Empty(Array.Empty<Shipment>().ByStorageClass(StorageClass.Cold));
        Assert.Empty(new[] { TestData.Ship("A", StorageClass.Cold, 1m, 1m) }.ByStorageClass(StorageClass.Frozen));
    }

    [Fact]
    public void RequireKeepsASuccessThatPassesTheRule()
    {
        var result = Result<int>.Ok(5).Require(v => v > 0, new AppError("too_small", "must be positive"));

        Assert.Equal(5, Assert.IsType<Result<int>.Success>(result).Value);
    }

    [Fact]
    public void RequireTurnsAFailingRuleIntoTheGivenFailure()
    {
        var result = Result<int>.Ok(0).Require(v => v > 0, new AppError("too_small", "must be positive"));

        Assert.Equal("too_small", Assert.IsType<Result<int>.Failure>(result).Error.Code);
    }

    [Fact]
    public void RequirePassesAnUpstreamFailureThroughWithoutEvaluatingTheRule()
    {
        int calls = 0;
        var upstream = Result<int>.Fail(new AppError("upstream", "earlier failure"));

        var result = upstream.Require(v => { calls++; return true; }, new AppError("rule", "unused"));

        Assert.Equal(0, calls);
        Assert.Equal("upstream", Assert.IsType<Result<int>.Failure>(result).Error.Code);
    }
}
