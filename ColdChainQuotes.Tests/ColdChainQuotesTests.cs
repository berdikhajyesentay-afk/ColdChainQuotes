using System.Collections.Immutable;
using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

public class ColdChainQuotesTests
{
    const decimal MaxDistanceKm = 300m;
    static readonly string SuppliedFile = Path.Combine(AppContext.BaseDirectory, "shipments.txt");

    static ImportResult ImportSuppliedFile() => Parsing.ParseLines(File.ReadAllLines(SuppliedFile));

    static Shipment Ship(string id, StorageClass storageClass, decimal weightKg, decimal distanceKm) =>
        new Shipment(id, "Customer " + id, storageClass, weightKg, distanceKm);

    [Fact]
    public void SuppliedFileGivesFiveShipmentsAndFourRejectedLines()
    {
        var imported = ImportSuppliedFile();

        Assert.Equal(new[] { "SH1", "SH2", "SH3", "SH4", "SH5" }, imported.Shipments.Select(s => s.Id));
        Assert.Equal(new[] { 6, 7, 8, 9 }, imported.Errors.Select(e => e.LineNumber));
    }

    [Fact]
    public void StandardPolicyGivesDocumentedQuotes()
    {
        var summary = Workflow.Run(ImportSuppliedFile().Shipments, PricePolicies.Standard, MaxDistanceKm);

        Assert.Equal(new[] { "SH3", "SH1", "SH2", "SH5" }, summary.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(new[] { 16320m, 4400m, 3700m, 1080m }, summary.Quotes.Select(q => q.Price));
        Assert.Equal(25500m, summary.Total);
    }

    [Fact]
    public void ExpressPolicyGivesDocumentedQuotes()
    {
        var summary = Workflow.Run(ImportSuppliedFile().Shipments, PricePolicies.Express, MaxDistanceKm);

        Assert.Equal(new[] { "SH3", "SH2", "SH1", "SH5" }, summary.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(new[] { 26080m, 5750m, 4200m, 2020m }, summary.Quotes.Select(q => q.Price));
        Assert.Equal(38050m, summary.Total);
    }

    [Fact]
    public void TwoPoliciesOnSameInputChangePricesAndRanking()
    {
        var shipments = ImmutableArray.Create(
            Ship("HEAVY", StorageClass.Cold, 60m, 20m),
            Ship("FAR", StorageClass.Frozen, 5m, 40m));

        var standard = Workflow.Run(shipments, PricePolicies.Standard, MaxDistanceKm);
        var express = Workflow.Run(shipments, PricePolicies.Express, MaxDistanceKm);

        Assert.Equal(new[] { "HEAVY", "FAR" }, standard.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(new[] { 4400m, 3700m }, standard.Quotes.Select(q => q.Price));
        Assert.Equal(new[] { "FAR", "HEAVY" }, express.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(new[] { 5750m, 4200m }, express.Quotes.Select(q => q.Price));
    }

    [Fact]
    public void EmptyDatasetGivesEmptySummary()
    {
        var imported = Parsing.ParseLines(Array.Empty<string>());
        var summary = Workflow.Run(imported.Shipments, PricePolicies.Standard, MaxDistanceKm);

        Assert.Empty(imported.Shipments);
        Assert.Empty(imported.Errors);
        Assert.Empty(summary.Quotes);
        Assert.Equal(0m, summary.Total);
    }

    [Fact]
    public void MissingInputIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Parsing.ParseLines(null));
    }

    [Fact]
    public void DistanceExactlyAtLimitIsServedAndPriced()
    {
        var shipments = new[]
        {
            Ship("AT", StorageClass.Cold, 1m, 300m),
            Ship("OVER", StorageClass.Cold, 1m, 300.01m)
        };

        var selected = Stages.SelectServiceable(shipments, MaxDistanceKm);

        Assert.Equal(new[] { "AT" }, selected.Select(s => s.Id));
        Assert.Equal(16040m, PricePolicies.Standard(selected[0]));
    }

    [Fact]
    public void ZeroDistanceIsAValueButAnEmptyDistanceIsMissing()
    {
        var zeroOk = Parsing.TryParseShipment("SH5|CityLab|Cold|2|0", out var shipment, out _);
        var emptyOk = Parsing.TryParseShipment("SH5|CityLab|Cold|2|", out var missing, out var error);

        Assert.True(zeroOk);
        Assert.Equal(0m, shipment?.DistanceKm);
        Assert.False(emptyOk);
        Assert.Null(missing);
        Assert.Equal("distance is missing", error);
    }

    [Theory]
    [InlineData("SH6|BadNumber|Cold|1,5|10", "weight '1,5' is not a valid number")]
    [InlineData("SH7|Negative|Frozen|4|-5", "distance cannot be negative, found -5")]
    [InlineData("SH8||Cold|3|15", "customer is missing")]
    [InlineData("SH9|WrongClass|Warm|3|15", "'Warm' is not a declared StorageClass (Cold or Frozen)")]
    [InlineData("X|C|99|3|15", "'99' is not a declared StorageClass (Cold or Frozen)")]
    [InlineData("X|C|Cold,Frozen|3|15", "'Cold,Frozen' is not a declared StorageClass (Cold or Frozen)")]
    [InlineData("X|C|Cold|0|15", "weight must be greater than 0 kg, found 0")]
    [InlineData("X|C|Cold|3", "expected 5 fields separated by '|', found 4")]
    public void InvalidFieldsAreRejectedWithClearMessage(string line, string expectedError)
    {
        var ok = Parsing.TryParseShipment(line, out var shipment, out var error);

        Assert.False(ok);
        Assert.Null(shipment);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void DuplicateIdIsRejected()
    {
        var imported = Parsing.ParseLines(new[] { "A|One|Cold|1|1", "a|Two|Cold|1|1" });

        Assert.Equal(new[] { "A" }, imported.Shipments.Select(s => s.Id));
        Assert.Equal(2, imported.Errors.Single().LineNumber);
    }

    [Fact]
    public void SelectStageDoesNotChangeCallerList()
    {
        var near = Ship("NEAR", StorageClass.Cold, 1m, 10m);
        var far = Ship("FAR", StorageClass.Cold, 1m, 400m);
        var callerList = new List<Shipment> { near, far };

        var selected = Stages.SelectServiceable(callerList, MaxDistanceKm);

        Assert.Equal(new[] { near }, selected);
        Assert.Equal(new[] { near, far }, callerList);
        Assert.Equal(400m, far.DistanceKm);
    }

    [Fact]
    public void ChangingCallerListLaterDoesNotChangeComputedSummary()
    {
        var callerList = new List<Shipment> { Ship("A", StorageClass.Cold, 1m, 10m) };

        var summary = Workflow.Run(callerList, PricePolicies.Standard, MaxDistanceKm);
        callerList.Clear();
        callerList.Add(Ship("B", StorageClass.Frozen, 9m, 99m));

        Assert.Equal(new[] { "A" }, summary.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(1540m, summary.Total);
    }

    [Fact]
    public void PricePolicyIsCalledOncePerSelectedShipment()
    {
        var calledFor = new List<string>();
        Func<Shipment, decimal> recordingPolicy = s =>
        {
            calledFor.Add(s.Id);
            return 100m;
        };

        var summary = Workflow.Run(ImportSuppliedFile().Shipments, recordingPolicy, MaxDistanceKm);

        Assert.Equal(new[] { "SH1", "SH2", "SH3", "SH5" }, calledFor);
        Assert.Equal(400m, summary.Total);
    }

    [Fact]
    public void SameInputAndPolicyGiveSameOutput()
    {
        var shipments = ImportSuppliedFile().Shipments;

        var first = Workflow.Run(shipments, PricePolicies.Express, MaxDistanceKm);
        var second = Workflow.Run(shipments, PricePolicies.Express, MaxDistanceKm);

        // ImmutableArray's own Equals compares references, so compare the items.
        Assert.Equal(first.Quotes.ToArray(), second.Quotes.ToArray());
        Assert.Equal(first.Total, second.Total);
    }

    [Fact]
    public void EqualPricesAreOrderedById()
    {
        var priced = new[]
        {
            new PricedShipment(Ship("B", StorageClass.Cold, 1m, 1m), 500m),
            new PricedShipment(Ship("A", StorageClass.Cold, 1m, 1m), 500m),
            new PricedShipment(Ship("C", StorageClass.Cold, 1m, 1m), 900m)
        };

        var summary = Stages.BuildSummary(priced);

        Assert.Equal(new[] { "C", "A", "B" }, summary.Quotes.Select(q => q.ShipmentId));
        Assert.Equal(1900m, summary.Total);
    }
}
