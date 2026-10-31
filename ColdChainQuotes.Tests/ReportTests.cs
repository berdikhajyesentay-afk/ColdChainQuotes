using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Byerdikhajy: LINQ reports, curried scoring, console workflow and export failures.
public class ReportTests : IDisposable
{
    const decimal MaxKm = 300m;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ColdChainReports-" + Guid.NewGuid());

    public ReportTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static ImmutableArray<Shipment> Sample() =>
        Assert.IsType<Result<ImmutableArray<Shipment>>.Success>(CsvBoundary.Load(TestData.SampleCsv)).Value;

    [Fact]
    public void RevenueByCustomerGroupsTotalsAndOrdersByRevenue()
    {
        var rows = Reports.RevenueByCustomer(Sample(), MaxKm, PricePolicies.Standard);

        Assert.Equal(new[] { "GreenLeaf", "FreshFarm", "IceDream", "CityLab" }, rows.Select(r => r.Customer));
        var freshFarm = rows[1];
        Assert.Equal(2, freshFarm.Shipments);
        Assert.Equal(70m, freshFarm.WeightKg);
        Assert.Equal(11300m, freshFarm.Revenue);
        Assert.Equal(16320m, rows[0].Revenue);
    }

    [Fact]
    public void RevenueByCustomerForAnEmptyDatasetIsEmpty()
    {
        Assert.Empty(Reports.RevenueByCustomer(Array.Empty<Shipment>(), MaxKm, PricePolicies.Standard));
    }

    [Fact]
    public void RevenueByCustomerWithNoServedShipmentsIsEmpty()
    {
        var farAway = new[] { TestData.Ship("FAR", StorageClass.Cold, 1m, 301m) };

        Assert.Empty(Reports.RevenueByCustomer(farAway, MaxKm, PricePolicies.Standard));
    }

    [Fact]
    public void EqualRevenueIsOrderedByCustomerNameWhateverTheInputOrder()
    {
        var shipments = new[]
        {
            TestData.Ship("B", StorageClass.Cold, 1m, 0m),
            TestData.Ship("A", StorageClass.Cold, 1m, 0m)
        };

        var rows = Reports.RevenueByCustomer(shipments, MaxKm, PricePolicies.Standard);

        Assert.Equal(new[] { "Customer A", "Customer B" }, rows.Select(r => r.Customer));
        Assert.Equal(rows[0].Revenue, rows[1].Revenue);
    }

    [Fact]
    public void ColdPriorityRanksWithTheWeightFirstConfiguration()
    {
        var weightFirst = Scoring.CreateScore(10m)(1m);

        var rows = Reports.ColdPriority(Sample(), MaxKm, weightFirst);

        Assert.Equal(new[] { "SH1", "SH3", "SH5" }, rows.Select(r => r.Id));
        Assert.Equal(new[] { 620m, 380m, 20m }, rows.Select(r => r.Score));
    }

    [Fact]
    public void ColdPriorityRanksWithTheDistanceFirstConfigurationAndTheResultDiffers()
    {
        var distanceFirst = Scoring.CreateScore(1m)(10m);

        var rows = Reports.ColdPriority(Sample(), MaxKm, distanceFirst);

        Assert.Equal(new[] { "SH3", "SH1", "SH5" }, rows.Select(r => r.Id));
    }

    [Fact]
    public void TheTwoPartialApplicationsGiveDifferentScoresForTheSameShipment()
    {
        var sh1 = Sample()[0];

        Assert.Equal(620m, Scoring.CreateScore(10m)(1m)(sh1));
        Assert.Equal(260m, Scoring.CreateScore(1m)(10m)(sh1));
    }

    [Fact]
    public void EqualScoresAreOrderedByShipmentId()
    {
        var score = Scoring.CreateScore(0m)(0m);
        var shipments = new[]
        {
            TestData.Ship("SH9", StorageClass.Cold, 1m, 1m),
            TestData.Ship("SH2", StorageClass.Cold, 1m, 1m)
        };

        var rows = Reports.ColdPriority(shipments, MaxKm, score);

        Assert.Equal(new[] { "SH2", "SH9" }, rows.Select(r => r.Id));
    }

    [Fact]
    public void ColdPriorityOnEmptyOrNoColdServedShipmentsIsEmpty()
    {
        var score = Scoring.CreateScore(1m)(1m);

        Assert.Empty(Reports.ColdPriority(Array.Empty<Shipment>(), MaxKm, score));
        Assert.Empty(Reports.ColdPriority(new[] { TestData.Ship("F", StorageClass.Frozen, 1m, 1m) }, MaxKm, score));
    }

    [Fact]
    public void ConsoleRunWritesBothReportsAndTheNativeTierOnTheSample()
    {
        var output = new StringWriter();

        var exit = ReportConsole.Run(new[] { TestData.SampleCsv }, output);

        var text = output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("Total", text);
        Assert.Contains("32400", text);
        Assert.Contains("   2 SH3   GreenLeaf    LongHaul", text);
        Assert.Contains("   1 SH3   GreenLeaf    LongHaul", text);
    }

    [Fact]
    public void ConsoleRunExportsExactlyTheRenderedLinesToAFile()
    {
        var exportPath = Path.Combine(folder, "report.txt");
        var expected = ReportConsole.Render(Sample());

        var exit = ReportConsole.Run(new[] { TestData.SampleCsv, "--export", exportPath }, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal(expected, File.ReadAllLines(exportPath));
    }

    [Fact]
    public void AMissingInputFileReturnsAnExplicitErrorAndExportsNothing()
    {
        var exportPath = Path.Combine(folder, "never.txt");
        var output = new StringWriter();

        var exit = ReportConsole.Run(new[] { Path.Combine(folder, "absent.csv"), "--export", exportPath }, output);

        Assert.Equal(1, exit);
        Assert.Contains("ERROR [file_missing]", output.ToString());
        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public void AHeaderOnlyInputIsReportedAsAnEmptyDatasetError()
    {
        var output = new StringWriter();

        var exit = ReportConsole.Run(new[] { Path.Combine(AppContext.BaseDirectory, "samples", "header-only.csv") }, output);

        Assert.Equal(1, exit);
        Assert.Contains("ERROR [empty_dataset]", output.ToString());
    }

    [Fact]
    public void ExportToAMissingFolderFailsWithoutLeavingATemporaryFile()
    {
        var exportPath = Path.Combine(folder, "missing-folder", "report.txt");
        var output = new StringWriter();

        var exit = ReportConsole.Run(new[] { TestData.SampleCsv, "--export", exportPath }, output);

        Assert.Equal(1, exit);
        Assert.Contains("ERROR [export_failed]", output.ToString());
        Assert.False(File.Exists(exportPath + ".tmp"));
        Assert.False(File.Exists(exportPath));
    }
}
