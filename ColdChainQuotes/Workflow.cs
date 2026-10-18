namespace ColdChainQuotes;

public static class PipeExtensions
{
    // Passes a value to the next function, so stages read top to bottom in the order they run.
    public static TOut Pipe<TIn, TOut>(this TIn input, Func<TIn, TOut> next) => next(input);
}

public static class Workflow
{
    // The whole pipeline. The policy and the distance limit are arguments, never global settings.
    public static QuoteSummary Run(
        IEnumerable<Shipment> shipments, Func<Shipment, decimal> pricePolicy, decimal maxDistanceKm)
    {
        return shipments
            .Pipe(s => Stages.SelectServiceable(s, maxDistanceKm))
            .Pipe(s => Stages.PriceShipments(s, pricePolicy))
            .Pipe(p => Stages.BuildSummary(p));
    }
}
