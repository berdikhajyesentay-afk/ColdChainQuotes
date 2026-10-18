using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// The states of one booking. Each state carries only the data that is true in that state.
// The private constructor closes the hierarchy, and the internal constructors mean that
// code outside this project can reach a state only through BookingRules.
public abstract record Booking
{
    private Booking(Shipment shipment) => Shipment = shipment;

    public Shipment Shipment { get; }

    // Opened for a served shipment. No price yet.
    public sealed record Requested : Booking
    {
        internal Requested(Shipment shipment) : base(shipment) { }
    }

    // Priced once with one policy. Can now be confirmed.
    public sealed record Quoted : Booking
    {
        internal Quoted(Shipment shipment, string policyName, decimal price) : base(shipment)
        {
            PolicyName = policyName;
            Price = price;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
    }

    // Confirmed: the price is final and the booking has a confirmation number.
    public sealed record Confirmed : Booking
    {
        internal Confirmed(Quoted quote, int number) : base(quote.Shipment)
        {
            PolicyName = quote.PolicyName;
            Price = quote.Price;
            Number = number;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
    }
}

// What a caller asks for. Price names the policy; Confirm needs nothing else.
public abstract record BookingAction
{
    private BookingAction() { }

    public sealed record Price(string ShipmentId, string PolicyName, Func<Shipment, decimal> Policy) : BookingAction;

    public sealed record Confirm(string ShipmentId) : BookingAction;
}

// The result of one transition.
public abstract record BookingStep
{
    private BookingStep() { }

    // The transition happened: carries the new state.
    public sealed record Moved(Booking Next) : BookingStep;

    // The same operation was already done: carries the existing outcome, nothing changes.
    public sealed record Repeated(Booking Existing) : BookingStep;

    // A prerequisite is missing or the operation is not allowed: carries the reason, nothing changes.
    public sealed record Rejected(string Reason) : BookingStep;
}

// Pure transitions. They never change the booking they get; they return a step with the next state.
public static class BookingRules
{
    public static Booking Open(Shipment shipment) => new Booking.Requested(shipment);

    // Requested -> Quoted. A booking is priced once; a second price is rejected.
    public static BookingStep Price(Booking current, string policyName, Func<Shipment, decimal> policy) =>
        (current ?? throw new ArgumentNullException(nameof(current))) switch
        {
            Booking.Requested requested =>
                new BookingStep.Moved(new Booking.Quoted(requested.Shipment, policyName, policy(requested.Shipment))),
            Booking.Quoted quoted =>
                new BookingStep.Rejected($"{quoted.Shipment.Id} is already priced ({quoted.PolicyName}, {Money(quoted.Price)})"),
            Booking.Confirmed confirmed =>
                new BookingStep.Rejected($"{confirmed.Shipment.Id} is confirmed; its price can't change"),
            _ => new BookingStep.Rejected($"{current.Shipment.Id} is in an unknown state")
        };

    // Quoted -> Confirmed. Confirming a Requested booking is rejected: it has no price yet.
    // Confirming again returns the existing confirmation unchanged.
    public static BookingStep Confirm(Booking current, int nextNumber) =>
        (current ?? throw new ArgumentNullException(nameof(current))) switch
        {
            Booking.Requested requested =>
                new BookingStep.Rejected($"{requested.Shipment.Id} has no price yet: price it before confirming"),
            Booking.Quoted quoted => new BookingStep.Moved(new Booking.Confirmed(quoted, nextNumber)),
            Booking.Confirmed confirmed => new BookingStep.Repeated(confirmed),
            _ => new BookingStep.Rejected($"{current.Shipment.Id} is in an unknown state")
        };

    public static string Money(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
}

// All bookings plus the next confirmation number. Every change returns a new Ledger;
// a rejected or repeated action returns the same Ledger, so nothing is half-updated.
public sealed record Ledger(ImmutableSortedDictionary<string, Booking> Bookings, int NextConfirmation)
{
    // One Requested booking for every shipment that stage 1 serves.
    public static Ledger Open(IEnumerable<Shipment> shipments, decimal maxDistanceKm) =>
        new Ledger(
            Stages.SelectServiceable(shipments, maxDistanceKm)
                .ToImmutableSortedDictionary(s => s.Id, BookingRules.Open, StringComparer.OrdinalIgnoreCase),
            1);

    public static (Ledger Next, BookingStep Step) Apply(Ledger ledger, BookingAction action)
    {
        var id = action switch
        {
            BookingAction.Price price => price.ShipmentId,
            BookingAction.Confirm confirm => confirm.ShipmentId,
            _ => ""
        };

        if (!ledger.Bookings.TryGetValue(id, out var current))
            return (ledger, new BookingStep.Rejected($"no open booking for {id}"));

        var step = action switch
        {
            BookingAction.Price price => BookingRules.Price(current, price.PolicyName, price.Policy),
            BookingAction.Confirm => BookingRules.Confirm(current, ledger.NextConfirmation),
            _ => new BookingStep.Rejected("unknown action")
        };

        // Commit only a completed transition; everything else leaves the ledger as it was.
        return step switch
        {
            BookingStep.Moved { Next: Booking.Confirmed confirmed } =>
                (new Ledger(ledger.Bookings.SetItem(current.Shipment.Id, confirmed), ledger.NextConfirmation + 1), step),
            BookingStep.Moved moved =>
                (ledger with { Bookings = ledger.Bookings.SetItem(current.Shipment.Id, moved.Next) }, step),
            _ => (ledger, step)
        };
    }
}
