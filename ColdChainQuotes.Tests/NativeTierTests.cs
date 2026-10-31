using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Bekarys: the real native call and the controlled adapter failures.
public class NativeTierTests
{
    [Theory]
    [InlineData(0, DistanceTier.Local)]
    [InlineData(20, DistanceTier.Local)]
    [InlineData(50, DistanceTier.Local)]
    [InlineData(51, DistanceTier.Regional)]
    [InlineData(100, DistanceTier.Regional)]
    [InlineData(150, DistanceTier.Regional)]
    [InlineData(151, DistanceTier.LongHaul)]
    [InlineData(300, DistanceTier.LongHaul)]
    public void TheRealNativeCallReturnsTheHandCalculatedTier(int km, DistanceTier expected)
    {
        var result = NativeTier.Classify(km);

        Assert.Equal(expected, Assert.IsType<Result<DistanceTier>.Success>(result).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    [InlineData(2.5)]
    public void InvalidDistancesAreRejectedBeforeTheNativeCall(double km)
    {
        int calls = 0;

        var result = NativeTier.Classify((decimal)km, _ => { calls++; return 0; });

        Assert.Equal("invalid_distance", Assert.IsType<Result<DistanceTier>.Failure>(result).Error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AMissingLibraryBecomesALoadFailure()
    {
        var result = NativeTier.Classify(20m, _ => throw new DllNotFoundException("ccq_native.dll"));

        Assert.Equal("native_load_failed", Assert.IsType<Result<DistanceTier>.Failure>(result).Error.Code);
    }

    [Fact]
    public void ALibraryOfTheWrongArchitectureBecomesALoadFailure()
    {
        var result = NativeTier.Classify(20m, _ => throw new BadImageFormatException("wrong architecture"));

        Assert.Equal("native_load_failed", Assert.IsType<Result<DistanceTier>.Failure>(result).Error.Code);
    }

    [Fact]
    public void AMissingEntryPointBecomesABindingFailure()
    {
        var result = NativeTier.Classify(20m, _ => throw new EntryPointNotFoundException("ccq_distance_tier"));

        Assert.Equal("native_binding_failed", Assert.IsType<Result<DistanceTier>.Failure>(result).Error.Code);
    }

    [Fact]
    public void ANativeRejectionIsReportedWithItsOwnCode()
    {
        var result = NativeTier.Classify(20m, _ => -1);

        Assert.Equal("native_rejected", Assert.IsType<Result<DistanceTier>.Failure>(result).Error.Code);
    }
}
