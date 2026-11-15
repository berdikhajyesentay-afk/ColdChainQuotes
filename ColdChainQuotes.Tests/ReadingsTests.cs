// STARTER TESTS for Estai. Copy to: ColdChainQuotes.Tests/ReadingsTests.cs
// These are real tests. They fail until Readings.cs is finished. Do not weaken them to make them pass.
using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Estai: validated readings, lazy CSV stream, single materialisation point, file boundary.
public class ReadingsTests : IDisposable
{
    const string TwoReadings = Readings.Header + "\nSH1,0,4.0\nSH1,15,-4.5\n";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainReadings-" + Guid.NewGuid());

    public ReadingsTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static AppError ErrorOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Failure>(result).Error;
    static T ValueOf<T>(Result<T> result) where T : notnull => Assert.IsType<Result<T>.Success>(result).Value;

    // Counts how many times the file is opened and whether the reader was disposed.
    sealed class Probe
    {
        public int Opens;
        public int Disposes;
        public Func<TextReader> Open(string text) => () => { Opens++; return new ProbeReader(text, () => Disposes++); };
    }

    sealed class ProbeReader(string text, Action onDispose) : StringReader(text)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose();
            base.Dispose(disposing);
        }
    }

    // ---- validated construction (pure)

    [Fact]
    public void CreateAcceptsAValidReadingAndTrimsTheId()
    {
        var reading = ValueOf(TemperatureReading.Create(" SH1 ", 15, -19.5m));
        Assert.Equal("SH1", reading.ShipmentId);
        Assert.Equal(15, reading.Minute);
        Assert.Equal(-19.5m, reading.Celsius);
    }

    [Theory]
    [InlineData("", 0, 4.0)]
    [InlineData("SH1", -1, 4.0)]
    [InlineData("SH1", 0, 61.0)]
    [InlineData("SH1", 0, -60.5)]
    public void CreateRefusesInvalidValues(string id, int minute, double celsius)
    {
        Assert.Equal("invalid_reading", ErrorOf(TemperatureReading.Create(id, minute, (decimal)celsius)).Code);
    }

    // ---- row parsing (pure)

    [Fact]
    public void ParseRowReadsNegativeDecimalsWithADot()
    {
        var reading = ValueOf(Readings.ParseRow("SH2,30,-15.0", 4));
        Assert.Equal(-15.0m, reading.Celsius);
        Assert.Equal(30, reading.Minute);
    }

    [Theory]
    [InlineData("SH1,0", "malformed_data")]
    [InlineData("SH1,ten,4.0", "malformed_data")]
    [InlineData("SH1,0,warm", "malformed_data")]
    [InlineData("SH1,0,4,5", "malformed_data")]
    [InlineData(",0,4.0", "invalid_reading")]
    public void ParseRowRejectsBadRowsWithTheLineNumber(string line, string code)
    {
        var error = ErrorOf(Readings.ParseRow(line, 7));
        Assert.Equal(code, error.Code);
        Assert.Contains("Line 7", error.Message);
    }

    // ---- deferred execution and resource ownership

    [Fact]
    public void CreatingTheStreamOpensNothing()
    {
        var probe = new Probe();
        var stream = Readings.Stream(probe.Open(TwoReadings));
        Assert.Equal(0, probe.Opens);           // deferred: nothing has run yet

        var first = stream.First();             // pulls one row, then stops early
        Assert.IsType<Result<TemperatureReading>.Success>(first);
        Assert.Equal(1, probe.Opens);
        Assert.Equal(1, probe.Disposes);        // stopping early still disposes the reader
    }

    [Fact]
    public void EnumeratingTheStreamTwiceOpensTheFileTwice()
    {
        var probe = new Probe();
        var stream = Readings.Stream(probe.Open(TwoReadings));
        Assert.Equal(2, stream.Count());
        Assert.Equal(2, stream.Count());
        Assert.Equal(2, probe.Opens);           // this is why Materialise calls ToArray once
    }

    [Fact]
    public void TheMaterialisedSnapshotIsReusedWithoutReopeningTheFile()
    {
        var probe = new Probe();
        var snapshot = ValueOf(Readings.Materialise(probe.Open(TwoReadings), "probe"));
        Assert.Equal(1, probe.Opens);
        Assert.Equal(1, probe.Disposes);

        // Use the snapshot several times: no new opens, the same data each time.
        Assert.Equal(2, snapshot.Length);
        Assert.Equal(2, snapshot.Count(r => r.ShipmentId == "SH1"));
        Assert.Equal(-4.5m, snapshot.Last().Celsius);
        Assert.Equal(1, probe.Opens);
    }

    [Fact]
    public void AWrongHeaderIsMalformedAndTheReaderIsDisposed()
    {
        var probe = new Probe();
        var error = ErrorOf(Readings.Materialise(probe.Open("id,when,temp\nSH1,0,4.0\n"), "probe"));
        Assert.Equal("malformed_data", error.Code);
        Assert.Equal(1, probe.Disposes);
    }

    [Fact]
    public void AnEmptyFileIsAnEmptySnapshot()
    {
        var probe = new Probe();
        Assert.Empty(ValueOf(Readings.Materialise(probe.Open(""), "probe")));
    }

    // ---- file boundary (real files)

    [Fact]
    public void LoadReadsTheSampleFile()
    {
        var readings = ValueOf(Readings.Load(Path.Combine(AppContext.BaseDirectory, "samples", "readings.csv")));
        Assert.Equal(18, readings.Length);
        Assert.Equal(5, readings.Count(r => r.ShipmentId == "SH2"));
    }

    [Fact]
    public void LoadReportsAMissingFile()
    {
        var error = ErrorOf(Readings.Load(Path.Combine(folder, "nope.csv")));
        Assert.Equal("file_missing", error.Code);
    }

    [Fact]
    public void LoadReportsMalformedContentWithFileAndLine()
    {
        var path = Path.Combine(folder, "bad.csv");
        File.WriteAllText(path, Readings.Header + "\nSH1,0,4.0\nSH1,15,four\n");
        var error = ErrorOf(Readings.Load(path));
        Assert.Equal("malformed_data", error.Code);
        Assert.Contains("bad.csv", error.Message);
        Assert.Contains("Line 3", error.Message);
    }
}
