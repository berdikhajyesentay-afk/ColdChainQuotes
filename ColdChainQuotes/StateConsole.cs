namespace ColdChainQuotes;

// Console boundary for the saved state: load, run one pure step, save only if the step succeeded.
public static class StateConsole
{
    // Shared helper for state-changing commands (confirm, dispatch, deliver, invoice).
    // 1. strips --state/--seed, 2. loads (or seeds) the Ledger, 3. runs the pure step,
    // 4. saves the new Ledger only on success, 5. prints SUCCESS or ERROR [code].
    // Exit code 0 = success, 1 = expected failure. On any failure the state file is unchanged.
    public static int Execute(string[] args, TextWriter output,
        Func<Ledger, string[], Result<(Ledger Ledger, string Message)>> step)
    {
        var (rest, statePath, seedPath) = StateStore.ResolvePaths(args);
        var outcome = StateStore.LoadOrSeed(statePath, seedPath)
            .Bind(ledger => step(ledger, rest))
            .Bind(done => StateStore.Save(statePath, done.Ledger).Map(_ => done.Message));
        return Show(outcome, output);
    }

    public static int Show(Result<string> outcome, TextWriter output)
    {
        switch (outcome)
        {
            case Result<string>.Success success:
                output.WriteLine("SUCCESS " + success.Value);
                return 0;
            case Result<string>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    // confirm <id> [standard|express]: W2 on the saved state (price, then confirm, both or nothing).
    public static int Confirm(string[] args, TextWriter output) =>
        Execute(args, output, (ledger, rest) =>
            rest.Length is < 1 or > 2
                ? Result<(Ledger, string)>.Fail(new("usage", "confirm <id> [standard|express]"))
                : ResultBookings.Book(ledger, rest[0], PolicyName(rest.Length > 1 ? rest[1] : "standard"))
                    .Map(receipt => (receipt.Ledger, receipt.Display)));

    // "express" -> "Express", so saved bookings show the same policy names as the rest of the app.
    static string PolicyName(string word) => PricePolicies.Find(word) is { } found ? found.Name : word;

    // status: every saved booking with its state, in id order.
    public static int Status(string[] args, TextWriter output)
    {
        var (_, statePath, seedPath) = StateStore.ResolvePaths(args);
        switch (StateStore.LoadOrSeed(statePath, seedPath))
        {
            case Result<Ledger>.Success loaded:
                output.WriteLine($"Bookings in {statePath}");
                if (loaded.Value.Bookings.Count == 0)
                    output.WriteLine("  no records");
                foreach (var booking in loaded.Value.Bookings.Values)
                    output.WriteLine($"  {booking.Shipment.Id,-5} {booking.Shipment.Customer,-10} {Actions.DescribeState(booking)}");
                return 0;
            case Result<Ledger>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    // reset: rebuild the state from the seed file. The old state is replaced only if the seed loads.
    public static int Reset(string[] args, TextWriter output)
    {
        var (_, statePath, seedPath) = StateStore.ResolvePaths(args);
        var outcome = JsonBoundary.Load(seedPath)
            .Bind(ledger => StateStore.Save(statePath, ledger)
                .Map(_ => $"state {statePath} reset from {seedPath} ({ledger.Bookings.Count} open bookings)"));
        return Show(outcome, output);
    }
}
