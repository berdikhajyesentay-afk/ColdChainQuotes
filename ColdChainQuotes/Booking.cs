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

    // Final-exam lifecycle (shared foundation). Each later state is built from the state before it,
    // so it can exist only after that state existed. The rules that create them live with their owners:
    // Dispatched in DispatchRules (Bekarys), Delivered and Spoiled in DeliveryRules (Estai),
    // Invoiced in Invoicing (Byerdikhajy).

    // Assigned to a refrigerated vehicle. Carries the vehicle and the native distance tier.
    public sealed record Dispatched : Booking
    {
        internal Dispatched(Confirmed confirmed, string vehicleId, DistanceTier tier) : base(confirmed.Shipment)
        {
            PolicyName = confirmed.PolicyName;
            Price = confirmed.Price;
            Number = confirmed.Number;
            VehicleId = vehicleId;
            Tier = tier;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
        public string VehicleId { get; }
        public DistanceTier Tier { get; }
    }

    // Delivered with the cold chain intact (under the chosen tolerance policy).
    public sealed record Delivered : Booking
    {
        internal Delivered(Dispatched dispatched, int readingCount) : base(dispatched.Shipment)
        {
            PolicyName = dispatched.PolicyName;
            Price = dispatched.Price;
            Number = dispatched.Number;
            VehicleId = dispatched.VehicleId;
            ReadingCount = readingCount;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
        public string VehicleId { get; }
        public int ReadingCount { get; }
    }

    // Delivered, but the cold chain broke. Carries the breach reason. Credited on the invoice.
    public sealed record Spoiled : Booking
    {
        internal Spoiled(Dispatched dispatched, string reason) : base(dispatched.Shipment)
        {
            PolicyName = dispatched.PolicyName;
            Price = dispatched.Price;
            Number = dispatched.Number;
            VehicleId = dispatched.VehicleId;
            Reason = reason;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
        public string VehicleId { get; }
        public string Reason { get; }
    }

    // Billed on an invoice. Only a Delivered booking can become Invoiced, and only once.
    public sealed record Invoiced : Booking
    {
        internal Invoiced(Delivered delivered, string invoiceNumber) : base(delivered.Shipment)
        {
            PolicyName = delivered.PolicyName;
            Price = delivered.Price;
            Number = delivered.Number;
            VehicleId = delivered.VehicleId;
            ReadingCount = delivered.ReadingCount;
            InvoiceNumber = invoiceNumber;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
        public string VehicleId { get; }
        public int ReadingCount { get; }
        public string InvoiceNumber { get; }
    }

    // A Spoiled booking whose price was credited on an invoice. Only a Spoiled booking can become
    // Credited, and only once, so the same credit can never appear on two invoices.
    public sealed record Credited : Booking
    {
        internal Credited(Spoiled spoiled, string invoiceNumber) : base(spoiled.Shipment)
        {
            PolicyName = spoiled.PolicyName;
            Price = spoiled.Price;
            Number = spoiled.Number;
            VehicleId = spoiled.VehicleId;
            Reason = spoiled.Reason;
            InvoiceNumber = invoiceNumber;
        }

        public string PolicyName { get; }
        public decimal Price { get; }
        public int Number { get; }
        public string VehicleId { get; }
        public string Reason { get; }
        public string InvoiceNumber { get; }
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
            Booking.Dispatched or Booking.Delivered or Booking.Spoiled or Booking.Invoiced or Booking.Credited =>
                new BookingStep.Rejected($"{current.Shipment.Id} is already {StateName(current)}; its price can't change"),
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
            Booking.Dispatched or Booking.Delivered or Booking.Spoiled or Booking.Invoiced or Booking.Credited =>
                new BookingStep.Rejected($"{current.Shipment.Id} is already {StateName(current)}; it was confirmed earlier"),
            _ => new BookingStep.Rejected($"{current.Shipment.Id} is in an unknown state")
        };

    // Lower-case state name for messages, for example "dispatched".
    public static string StateName(Booking booking) => booking switch
    {
        Booking.Requested => "requested",
        Booking.Quoted => "quoted",
        Booking.Confirmed => "confirmed",
        Booking.Dispatched => "dispatched",
        Booking.Delivered => "delivered",
        Booking.Spoiled => "spoiled",
        Booking.Invoiced => "invoiced",
        Booking.Credited => "credited",
        _ => "unknown"
    };

    public static string Money(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
}

// All bookings plus the next confirmation and invoice numbers. Every change returns a new Ledger;
// a rejected or repeated action returns the same Ledger, so nothing is half-updated.
public sealed record Ledger(ImmutableSortedDictionary<string, Booking> Bookings, int NextConfirmation)
{
    // Next invoice number (persisted by StateStore). Changed only with `ledger with { ... }`.
    public int NextInvoice { get; init; } = 1;

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
                (ledger with { Bookings = ledger.Bookings.SetItem(current.Shipment.Id, confirmed), NextConfirmation = ledger.NextConfirmation + 1 }, step),
            BookingStep.Moved moved =>
                (ledger with { Bookings = ledger.Bookings.SetItem(current.Shipment.Id, moved.Next) }, step),
            _ => (ledger, step)
        };
    }
}
