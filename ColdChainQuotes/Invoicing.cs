using System.Collections.Immutable;
using System.Globalization;

namespace ColdChainQuotes;

// W5 Invoice (Yesyentai). Pure: no Console, files or clock. The caller saves the returned Ledger.

// One line of an invoice. A Delivered booking is a charge; a Spoiled booking is a credit (negative amount).
public sealed record InvoiceLine(string ShipmentId, int Number, string Kind, decimal Amount, string Note);

public sealed record Invoice(
    string Number, string Customer, string DiscountName, ImmutableArray<InvoiceLine> Lines,
    decimal Subtotal, decimal Credit, decimal Discount, decimal Total);

// A named discount rule: subtotal of charges -> discount amount.
public sealed record DiscountRule(string Name, Func<decimal, decimal> Apply);

public static class Discounts
{
    // Curried: threshold -> percent -> (subtotal -> discount amount).
    // Each call fixes one setting; the last function is the configured rule the invoice uses.
    public static readonly Func<decimal, Func<decimal, Func<decimal, decimal>>> Create =
        threshold => percent => subtotal =>
            subtotal >= threshold ? Math.Round(subtotal * percent / 100m, 2, MidpointRounding.AwayFromZero) : 0m;

    // Two configured functions made by partial application of Create.
    // Loyalty: 5% from a 5,000 subtotal. Bulk: 10% but only from a 10,000 subtotal.
    // On a 6,000 subtotal Loyalty gives 300 and Bulk gives 0; on 12,000 Loyalty gives 600 and Bulk 1,200.
    public static readonly Func<decimal, decimal> Loyalty = Create(5_000m)(5m);
    public static readonly Func<decimal, decimal> Bulk = Create(10_000m)(10m);
    public static readonly Func<decimal, decimal> None = _ => 0m;

    public static Option<DiscountRule> Find(string name) => name.ToLowerInvariant() switch
    {
        "loyalty" => Option<DiscountRule>.Some(new("Loyalty", Loyalty)),
        "bulk" => Option<DiscountRule>.Some(new("Bulk", Bulk)),
        "none" => Option<DiscountRule>.Some(new("None", None)),
        _ => Option<DiscountRule>.None()
    };
}

public static class Invoicing
{
    // Lookup by customer name, case-insensitive. Found carries the name as saved.
    public static Option<string> FindCustomer(Ledger ledger, string customer) =>
        ledger.Bookings.Values
            .Select(b => b.Shipment.Customer)
            .FirstOrDefault(c => c.Equals(customer.Trim(), StringComparison.OrdinalIgnoreCase)) is { } found
            ? Option<string>.Some(found)
            : Option<string>.None();

    // Bills every Delivered booking of the customer and credits every Spoiled one, once.
    // Delivered -> Invoiced, Spoiled -> Credited, and the invoice counter moves on.
    // Failures (each stops the later steps): missing_customer, nothing_to_bill, already_invoiced.
    public static Result<(Ledger Ledger, Invoice Invoice)> Build(Ledger ledger, string customer, DiscountRule discount) =>
        FindCustomer(ledger, customer)
            .ToResult(new AppError("missing_customer", $"No booking belongs to customer '{customer}'."))
            .Bind(name => Billable(ledger, name))
            .Map(open => Compose(ledger.NextInvoice, open.Customer, open.Bookings, discount))
            .Require(invoice => invoice.Lines.Length > 0,
                new AppError("nothing_to_bill", $"Customer '{customer}' has nothing to bill."))
            .Map(invoice => (Close(ledger, invoice), invoice));

    // The customer's open (Delivered or Spoiled) bookings, in shipment-id order.
    static Result<(string Customer, ImmutableArray<Booking> Bookings)> Billable(Ledger ledger, string customer)
    {
        var mine = ledger.Bookings.Values
            .Where(b => b.Shipment.Customer.Equals(customer, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        var open = mine.Where(b => b is Booking.Delivered or Booking.Spoiled).ToImmutableArray();

        return (open.Length, mine.Any(b => b is Booking.Invoiced or Booking.Credited)) switch
        {
            (> 0, _) => Result<(string, ImmutableArray<Booking>)>.Ok((customer, open)),
            (0, true) => Result<(string, ImmutableArray<Booking>)>.Fail(new("already_invoiced",
                $"Every delivered booking of '{customer}' is already invoiced.")),
            _ => Result<(string, ImmutableArray<Booking>)>.Fail(new("nothing_to_bill",
                $"'{customer}' has no delivered or spoiled booking yet ({string.Join(", ", mine.Select(b => $"{b.Shipment.Id} {BookingRules.StateName(b)}"))})."))
        };
    }

    // Pure calculation of the invoice from the open bookings.
    public static Invoice Compose(int invoiceCounter, string customer, IEnumerable<Booking> open, DiscountRule discount)
    {
        var lines = open
            .Select(b => b switch
            {
                Booking.Delivered d => new InvoiceLine(d.Shipment.Id, d.Number, "charge", d.Price, $"delivered by {d.VehicleId}"),
                Booking.Spoiled s => new InvoiceLine(s.Shipment.Id, s.Number, "credit", -s.Price, s.Reason),
                _ => null
            })
            .OfType<InvoiceLine>()
            .OrderBy(l => l.ShipmentId, StringComparer.Ordinal)
            .ToImmutableArray();

        var subtotal = lines.Where(l => l.Kind == "charge").Sum(l => l.Amount);
        var credit = -lines.Where(l => l.Kind == "credit").Sum(l => l.Amount);
        var discountAmount = discount.Apply(subtotal);
        return new Invoice(NumberFor(invoiceCounter), customer, discount.Name, lines,
            subtotal, credit, discountAmount, subtotal - credit - discountAmount);
    }

    public static string NumberFor(int counter) => $"INV-{counter.ToString("0000", CultureInfo.InvariantCulture)}";

    // The new Ledger: each billed booking moves on, the counter moves on, nothing else changes.
    static Ledger Close(Ledger ledger, Invoice invoice) =>
        ledger with
        {
            Bookings = invoice.Lines.Aggregate(ledger.Bookings, (bookings, line) => bookings[line.ShipmentId] switch
            {
                Booking.Delivered d => bookings.SetItem(line.ShipmentId, new Booking.Invoiced(d, invoice.Number)),
                Booking.Spoiled s => bookings.SetItem(line.ShipmentId, new Booking.Credited(s, invoice.Number)),
                _ => bookings
            }),
            NextInvoice = ledger.NextInvoice + 1
        };

    // Text form of an invoice, for the console and the export file.
    public static IReadOnlyList<string> Render(Invoice invoice)
    {
        var lines = new List<string>
        {
            $"Invoice {invoice.Number} for {invoice.Customer} ({invoice.DiscountName} discount)",
            $"  {"Shipment",-9} {"Conf",4} {"Kind",-7} {"Amount",9}  Note"
        };
        lines.AddRange(invoice.Lines.Select(l =>
            $"  {l.ShipmentId,-9} {"#" + l.Number,4} {l.Kind,-7} {BookingRules.Money(l.Amount),9}  {l.Note}"));
        lines.Add($"  {"Subtotal",-22} {BookingRules.Money(invoice.Subtotal),9}");
        lines.Add($"  {"Credit",-22} {BookingRules.Money(-invoice.Credit),9}");
        lines.Add($"  {"Discount",-22} {BookingRules.Money(-invoice.Discount),9}");
        lines.Add($"  {"Total",-22} {BookingRules.Money(invoice.Total),9}");
        return lines;
    }
}
