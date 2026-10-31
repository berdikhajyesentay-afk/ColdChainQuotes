using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Estai: lazy CSV streaming, resource ownership and file boundaries.
public class CsvBoundaryTests : IDisposable
{
    const string Header = "id,customer,storageClass,weightKg,distanceKm";
    const string TwoRows = Header + "\nSH1,FreshFarm,Cold,60,20\nSH2,IceDream,Frozen,5,40\n";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainCsv-" + Guid.NewGuid());

    public CsvBoundaryTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // Test probe: counts reads and records disposal without changing the production code path.
    sealed class ProbeReader : TextReader
    {
        readonly StringReader inner;
        readonly Action onDispose;
        readonly int throwAtRead;
        int reads;

        public ProbeReader(string text, Action onDispose, int throwAtRead = -1)
        {
            inner = new StringReader(text);
            this.onDispose = onDispose;
            this.throwAtRead = throwAtRead;
        }

        public override string? ReadLine()
        {
            reads++;
            if (reads == throwAtRead)
                throw new IOException("probe read failure");
            return inner.ReadLine();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void CreatingTheSequenceRunsNoProcessing()
    {
        int opens = 0;
        var rows = CsvBoundary.StreamRows(() => { opens++; return new StringReader(TwoRows); });

        Assert.Equal(0, opens);
        Assert.NotNull(rows);
    }

    [Fact]
    public void IterationOpensOnceAndDisposesWhenFinished()
    {
        int opens = 0, disposed = 0;
        var rows = CsvBoundary.StreamRows(() => { opens++; return new ProbeReader(TwoRows, () => disposed++); });

        Assert.Equal(2, rows.Count());

        Assert.Equal(1, opens);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void StoppingEarlyStillDisposesTheReader()
    {
        int disposed = 0;
        var rows = CsvBoundary.StreamRows(() => new ProbeReader(TwoRows, () => disposed++));

        var first = rows.First();

        Assert.IsType<Result<Shipment>.Success>(first);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void AReadFailureDisposesTheReaderAndIsReportedAsAFileFailure()
    {
        int disposed = 0;
        var result = CsvBoundary.Materialise(() => new ProbeReader(TwoRows, () => disposed++, throwAtRead: 3), "probe.csv");

        var failure = Assert.IsType<Result<ImmutableArray<Shipment>>.Failure>(result);
        Assert.Equal("file_io", failure.Error.Code);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void RepeatedEnumerationReadsAgainUntilTheSnapshotIsMaterialised()
    {
        int opens = 0;
        var lazy = CsvBoundary.StreamRows(() => { opens++; return new StringReader(TwoRows); });

        lazy.Count();
        lazy.Count();
        Assert.Equal(2, opens);

        opens = 0;
        var snapshot = lazy.ToArray();
        Assert.Equal(1, opens);
        Assert.Equal(2, snapshot.Length);

        Assert.Equal(2, snapshot.Count(r => r is Result<Shipment>.Success));
        Assert.Equal(1, opens);
    }

    [Fact]
    public void TheSampleCsvLoadsSixShipmentsInFileOrder()
    {
        var loaded = Assert.IsType<Result<ImmutableArray<Shipment>>.Success>(CsvBoundary.Load(TestData.SampleCsv));

        Assert.Equal(new[] { "SH1", "SH2", "SH3", "SH4", "SH5", "SH6" }, loaded.Value.Select(s => s.Id));
        Assert.Equal(StorageClass.Frozen, loaded.Value[5].StorageClass);
    }

    [Fact]
    public void AHeaderOnlyFileIsAValidEmptyDataset()
    {
        var loaded = Assert.IsType<Result<ImmutableArray<Shipment>>.Success>(CsvBoundary.Load(Path.Combine(AppContext.BaseDirectory, "samples", "header-only.csv")));

        Assert.Empty(loaded.Value);
    }

    [Fact]
    public void AMissingFileIsASafeFailureAndCreatesNothing()
    {
        var path = Path.Combine(folder, "absent.csv");

        var failure = Assert.IsType<Result<ImmutableArray<Shipment>>.Failure>(CsvBoundary.Load(path));

        Assert.Equal("file_missing", failure.Error.Code);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("bad-header", "wrong,header\n", "malformed_data")]
    [InlineData("wrong-field-count", Header + "\nSH1,FreshFarm,Cold,60\n", "malformed_data")]
    [InlineData("weight-text", Header + "\nSH1,FreshFarm,Cold,heavy,20\n", "malformed_data")]
    [InlineData("numeric-class", Header + "\nSH1,FreshFarm,0,60,20\n", "invalid_shipment")]
    [InlineData("zero-weight", Header + "\nSH1,FreshFarm,Cold,0,20\n", "invalid_shipment")]
    [InlineData("duplicate-id", Header + "\nSH1,A,Cold,1,1\nsh1,B,Cold,1,1\n", "duplicate_id")]
    public void MalformedContentIsAControlledFailure(string name, string content, string code)
    {
        var path = Path.Combine(folder, name + ".csv");
        File.WriteAllText(path, content);

        var failure = Assert.IsType<Result<ImmutableArray<Shipment>>.Failure>(CsvBoundary.Load(path));

        Assert.Equal(code, failure.Error.Code);
    }
}
