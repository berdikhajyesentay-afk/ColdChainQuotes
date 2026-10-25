namespace ColdChainQuotes;

// Final outcomes are handled here, once per request, with explicit cases.
public static class ResultConsole
{
    public static int Run(string[] args, TextWriter output)
    {
        string path = args.Length > 0 ? args[0] : "samples/shipments.json";
        if (args.Length > 1)
        {
            string id = args[1];
            string policy = args.Length > 2 ? args[2] : "standard";
            var result = JsonBoundary.Load(path).Bind(ledger => ResultBookings.Book(ledger, id, policy));
            return Show(result, output);
        }
        switch (JsonBoundary.Load(path))
        {
            case Result<Ledger>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            case Result<Ledger>.Success loaded:
                var start = loaded.Value;
                output.WriteLine("Cold chain Result workflow");
                foreach (string id in new[] { "SH5", "UNKNOWN" })
                {
                    output.WriteLine(ResultBookings.Find(start, id) switch
                    {
                        Option<Booking>.Found found => $"{id}: present, distance {BookingRules.Money(found.Value.Shipment.DistanceKm)} km",
                        Option<Booking>.Missing => $"{id}: missing",
                        _ => throw new InvalidOperationException("Unknown Option case")
                    });
                }
                var success = ResultBookings.Book(start, "SH1", "standard");
                Show(success, output);
                Show(ResultBookings.Book(start, "UNKNOWN", "standard"), output);
                Show(ResultBookings.Book(start, "SH1", "unknown"), output);
                if (success is Result<BookingReceipt>.Success done)
                {
                    // Repricing a confirmed booking is a state rejection. Neither ledger changes.
                    Show(ResultBookings.Book(done.Value.Ledger, "SH1", "express"), output);
                    output.WriteLine($"Original state: {start.Bookings["SH1"].GetType().Name}; successful state: {done.Value.Ledger.Bookings["SH1"].GetType().Name}");
                }
                // This mode deliberately demonstrates errors. A successful demonstration exits 0.
                return 0;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    static int Show(Result<BookingReceipt> result, TextWriter output)
    {
        switch (result)
        {
            case Result<BookingReceipt>.Success success:
                output.WriteLine("SUCCESS " + success.Value.Display);
                return 0;
            case Result<BookingReceipt>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }
}
