namespace ColdChainQuotes;

public sealed record BookingReceipt(Ledger Ledger, string ShipmentId, decimal Price, int Number, string Display);

// Composes the actual A6 transitions. The caller commits only the final successful Ledger.
public static class ResultBookings
{
    public static Option<Booking> Find(Ledger ledger, string id) =>
        ledger.Bookings.TryGetValue(id, out var booking)
            ? Option<Booking>.Some(booking) : Option<Booking>.None();

    public static Result<BookingReceipt> Book(Ledger ledger, string id, string policyName) =>
        Find(ledger, id)
            .ToResult(new AppError("missing_booking", $"No open booking for '{id}' (unknown or outside 300 km)."))
            .Bind(booking => FindPolicy(policyName)
                .Bind(policy => PriceAndConfirm(ledger, booking.Shipment.Id, policyName, policy)))
            .Map(done => new BookingReceipt(done.Ledger, done.Booking.Shipment.Id,
                done.Booking.Price, done.Booking.Number,
                $"{done.Booking.Shipment.Id} confirmed as #{done.Booking.Number}, {BookingRules.Money(done.Booking.Price)} ({done.Booking.PolicyName})"));

    static Result<Func<Shipment, decimal>> FindPolicy(string name) => name.ToLowerInvariant() switch
    {
        "standard" => Result<Func<Shipment, decimal>>.Ok(PricePolicies.Standard),
        "express" => Result<Func<Shipment, decimal>>.Ok(PricePolicies.Express),
        _ => Result<Func<Shipment, decimal>>.Fail(new("invalid_policy", $"Unknown policy '{name}'; use standard or express."))
    };

    static Result<(Ledger Ledger, Booking.Confirmed Booking)> PriceAndConfirm(
        Ledger start, string id, string policyName, Func<Shipment, decimal> policy)
    {
        var (priced, priceStep) = Ledger.Apply(start, new BookingAction.Price(id, policyName, policy));
        return Accept(priced, priceStep)
            .Bind(next =>
            {
                var (confirmed, confirmStep) = Ledger.Apply(next, new BookingAction.Confirm(id));
                return Accept(confirmed, confirmStep);
            })
            .Map(next => (next, (Booking.Confirmed)next.Bookings[id]));
    }

    static Result<Ledger> Accept(Ledger next, BookingStep step) => step switch
    {
        BookingStep.Moved => Result<Ledger>.Ok(next),
        BookingStep.Repeated => Result<Ledger>.Fail(new("state_rejected", "Booking already confirmed; nothing changed.")),
        BookingStep.Rejected rejected => Result<Ledger>.Fail(new("state_rejected", rejected.Reason)),
        _ => throw new InvalidOperationException("Unknown BookingStep case")
    };
}
