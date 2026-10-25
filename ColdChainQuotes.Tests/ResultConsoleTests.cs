using ColdChainQuotes;
namespace ColdChainQuotes.Tests;

public class ResultConsoleTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainConsole-" + Guid.NewGuid());
    readonly string input;
    public ResultConsoleTests()
    {
        Directory.CreateDirectory(folder);
        input = Path.Combine(folder, "shipments.json");
        File.WriteAllText(input, """[{"id":"SH1","customer":"FreshFarm","storageClass":"Cold","weightKg":60,"distanceKm":20},{"id":"SH5","customer":"CityLab","storageClass":"Cold","weightKg":2,"distanceKm":0}]""");
    }
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void ConsoleReportsSuccessAndAllThreeDomainFailures()
    {
        using var output = new StringWriter();
        Assert.Equal(0, ResultConsole.Run([input], output));
        var text = output.ToString();
        Assert.Contains("SH1 confirmed as #1, 4400", text);
        Assert.Contains("missing_booking", text);
        Assert.Contains("invalid_policy", text);
        Assert.Contains("state_rejected", text);
        Assert.Contains("SH5: present, distance 0 km", text);
        Assert.Contains("UNKNOWN: missing", text);
    }
    [Fact]
    public void ConsoleMissingFileReturnsFailureWithoutException()
    {
        using var output = new StringWriter();
        var path = Path.Combine(folder, "missing.json");
        Assert.Equal(1, ResultConsole.Run([path], output));
        Assert.Contains("file_missing", output.ToString());
        Assert.Contains(path, output.ToString());
    }
    [Fact]
    public void ConsoleMalformedFileReturnsFailureWithoutException()
    {
        File.WriteAllText(input, "[{bad");
        using var output = new StringWriter();
        Assert.Equal(1, ResultConsole.Run([input], output));
        Assert.Contains("malformed_data", output.ToString());
        Assert.DoesNotContain("SUCCESS", output.ToString());
    }
    [Fact]
    public void ConsoleSingleRequestUsesResultChain()
    {
        using var output = new StringWriter();
        Assert.Equal(0, ResultConsole.Run([input, "SH1", "express"], output));
        Assert.Contains("4200", output.ToString());
    }
    [Fact]
    public void ConsoleSingleDomainFailureHasNonzeroExitCode()
    {
        using var output = new StringWriter();
        Assert.Equal(1, ResultConsole.Run([input, "UNKNOWN", "standard"], output));
        Assert.Contains("missing_booking", output.ToString());
    }
}
