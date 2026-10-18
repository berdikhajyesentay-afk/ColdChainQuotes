using ColdChainQuotes;

namespace ColdChainQuotes.Tests;

// Assignment 6: the validated Shipment factory and its two cases.
public class ShipmentFactoryTests
{
    [Fact]
    public void ValidInputCreatesShipmentWithExpectedData()
    {
        var check = Shipment.Create("SH1", "FreshFarm", StorageClass.Cold, 60m, 20m);

        var valid = Assert.IsType<ShipmentCheck.Valid>(check);
        Assert.Equal("SH1", valid.Shipment.Id);
        Assert.Equal("FreshFarm", valid.Shipment.Customer);
        Assert.Equal(StorageClass.Cold, valid.Shipment.StorageClass);
        Assert.Equal(60m, valid.Shipment.WeightKg);
        Assert.Equal(20m, valid.Shipment.DistanceKm);
    }

    [Theory]
    [InlineData("", "C", 0, 1, 1, "shipment id is missing")]
    [InlineData("X", " ", 0, 1, 1, "customer is missing")]
    [InlineData("X", "C", 99, 1, 1, "'99' is not a declared StorageClass (Cold or Frozen)")]
    [InlineData("X", "C", 0, 0, 1, "weight must be greater than 0 kg, found 0")]
    [InlineData("X", "C", 0, 10000.01, 1, "weight must be at most 10000 kg, found 10000.01")]
    [InlineData("X", "C", 0, 1, -5, "distance cannot be negative, found -5")]
    [InlineData("X", "C", 0, 1, 20000.5, "distance must be at most 20000 km, found 20000.5")]
    public void EachRuleRejectsInvalidInputWithItsReason(string id, string customer, int storageClass, double weightKg, double distanceKm, string reason)
    {
        var check = Shipment.Create(id, customer, (StorageClass)storageClass, (decimal)weightKg, (decimal)distanceKm);

        var invalid = Assert.IsType<ShipmentCheck.Invalid>(check);
        Assert.Equal(new[] { reason }, invalid.Reasons);
    }

    [Fact]
    public void EveryBrokenRuleIsReportedAtOnce()
    {
        var check = Shipment.Create("", "", StorageClass.Frozen, 0m, -1m);

        var invalid = Assert.IsType<ShipmentCheck.Invalid>(check);
        Assert.Equal(4, invalid.Reasons.Length);
    }

    [Fact]
    public void LimitsThemselvesAreAccepted()
    {
        Assert.IsType<ShipmentCheck.Valid>(Shipment.Create("W", "C", StorageClass.Cold, Shipment.MaxWeightKg, 1m));
        Assert.IsType<ShipmentCheck.Valid>(Shipment.Create("D", "C", StorageClass.Cold, 1m, Shipment.MaxDistanceKm));
        Assert.IsType<ShipmentCheck.Valid>(Shipment.Create("Z", "C", StorageClass.Cold, 1m, 0m));
    }

    [Fact]
    public void BothCasesAreReachableAndMatchedToTheirPayload()
    {
        static string Describe(ShipmentCheck check) => check switch
        {
            ShipmentCheck.Valid valid => "valid " + valid.Shipment.Id,
            ShipmentCheck.Invalid invalid => "invalid: " + string.Join("; ", invalid.Reasons),
            _ => "unexpected"
        };

        Assert.Equal("valid A", Describe(Shipment.Create("A", "C", StorageClass.Cold, 1m, 1m)));
        Assert.Equal("invalid: customer is missing", Describe(Shipment.Create("A", "", StorageClass.Cold, 1m, 1m)));
    }

    [Fact]
    public void HugeWeightIsRejectedInsteadOfCrashingThePricePolicy()
    {
        var imported = Parsing.ParseLines(new[] { "B1|Big|Cold|79228162514264337593543950335|10" });
        var summary = Workflow.Run(imported.Shipments, PricePolicies.Standard, TestData.MaxDistanceKm);

        Assert.Empty(imported.Shipments);
        Assert.Equal("weight must be at most 10000 kg, found 79228162514264337593543950335", imported.Errors.Single().Message);
        Assert.Empty(summary.Quotes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("+1")]
    public void NumericStorageClassTextIsRejected(string classText)
    {
        var ok = Parsing.TryParseShipment($"X|C|{classText}|1|1", out var shipment, out var error);

        Assert.False(ok);
        Assert.Null(shipment);
        Assert.Equal($"'{classText}' is not a declared StorageClass (Cold or Frozen)", error);
    }
}
