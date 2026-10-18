using System.Collections.Immutable;

namespace ColdChainQuotes;

// One line of the actions file, read into a typed action or a reason why it can't be used.
public abstract record ActionLine
{
    private ActionLine() { }

    public sealed record Parsed(BookingAction Action) : ActionLine;

    public sealed record Unreadable(string Reason) : ActionLine;
}

// What happened to one line of the actions file.
public abstract record ActionResult
{
    private ActionResult() { }

    // The line was a valid action and went to the ledger: carries the step the ledger returned.
    public sealed record Applied(int LineNumber, string Text, BookingStep Step) : ActionResult;

    // The line could not be read as an action: carries the reason. The ledger never saw it.
    public sealed record NotUnderstood(int LineNumber, string Text, string Reason) : ActionResult;
}

public static class Actions
{
    // Line format: "price <id> <standard|express>" or "confirm <id>".
    public static ActionLine Parse(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return new ActionLine.Unreadable("the line is empty");

        return words[0].ToLowerInvariant() switch
        {
            "price" when words.Length != 3 => new ActionLine.Unreadable("price needs a shipment id and a policy: price <id> <standard|express>"),
            "price" => PricePolicies.Find(words[2]) is { } found
                ? new ActionLine.Parsed(new BookingAction.Price(words[1], found.Name, found.Price))
                : new ActionLine.Unreadable($"unknown policy '{words[2]}'. Use standard or express."),
            "confirm" when words.Length != 2 => new ActionLine.Unreadable("confirm needs exactly one shipment id: confirm <id>"),
            "confirm" => new ActionLine.Parsed(new BookingAction.Confirm(words[1])),
            _ => new ActionLine.Unreadable($"unknown action '{words[0]}'. Use price or confirm.")
        };
    }

    // Runs every action line against the ledger, in order. Blank lines and lines starting with #
    // are skipped but still counted, so line numbers match the file. Pure: returns the final ledger
    // and one result per action; the starting ledger is not changed.
    public static (Ledger Final, ImmutableArray<ActionResult> Results) Run(Ledger start, IEnumerable<string> lines)
    {
        var ledger = start;
        var results = ImmutableArray.CreateBuilder<ActionResult>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;

            switch (Parse(text))
            {
                case ActionLine.Parsed parsed:
                    var (next, step) = Ledger.Apply(ledger, parsed.Action);
                    ledger = next;
                    results.Add(new ActionResult.Applied(lineNumber, text, step));
                    break;
                case ActionLine.Unreadable unreadable:
                    results.Add(new ActionResult.NotUnderstood(lineNumber, text, unreadable.Reason));
                    break;
            }
        }

        return (ledger, results.ToImmutable());
    }

    // One readable line per result. Every case is named; anything else is reported, never shown as OK.
    public static string Describe(ActionResult result) => result switch
    {
        ActionResult.Applied { Step: BookingStep.Moved { Next: Booking.Quoted quoted } } =>
            $"OK        {quoted.Shipment.Id} quoted {BookingRules.Money(quoted.Price)} ({quoted.PolicyName})",
        ActionResult.Applied { Step: BookingStep.Moved { Next: Booking.Confirmed confirmed } } =>
            $"OK        {confirmed.Shipment.Id} confirmed as #{confirmed.Number}, {BookingRules.Money(confirmed.Price)}",
        ActionResult.Applied { Step: BookingStep.Repeated { Existing: Booking.Confirmed confirmed } } =>
            $"REPEATED  {confirmed.Shipment.Id} was already confirmed as #{confirmed.Number}; nothing changed",
        ActionResult.Applied { Step: BookingStep.Rejected rejected } =>
            $"REJECTED  {rejected.Reason}",
        ActionResult.NotUnderstood notUnderstood =>
            $"INVALID   {notUnderstood.Reason}",
        _ => "UNEXPECTED result: nothing was applied"
    };

    // The current state of one booking, for the final table.
    public static string DescribeState(Booking booking) => booking switch
    {
        Booking.Requested => "Requested",
        Booking.Quoted quoted => $"Quoted        {BookingRules.Money(quoted.Price)} ({quoted.PolicyName})",
        Booking.Confirmed confirmed => $"Confirmed #{confirmed.Number}  {BookingRules.Money(confirmed.Price)} ({confirmed.PolicyName})",
        _ => "Unknown state"
    };
}
