namespace ColdChainQuotes;

// Console boundary for W5 Invoice (Yesyentai).
// invoice <customer> [loyalty|bulk|none] [--export file] [--state file] [--seed file]
public static class InvoiceConsole
{
    public static int Run(string[] args, TextWriter output)
    {
        var (rest, statePath, seedPath) = StateStore.ResolvePaths(args);
        var exportAt = Array.IndexOf(rest, "--export");
        string? exportPath = exportAt >= 0 && exportAt + 1 < rest.Length ? rest[exportAt + 1] : null;
        var words = exportAt >= 0 ? rest.Take(exportAt).Concat(rest.Skip(exportAt + 2)).ToArray() : rest;

        if (words.Length is < 1 or > 2 || (exportAt >= 0 && exportPath is null))
            return StateConsole.Show(Result<string>.Fail(new("usage", "invoice <customer> [loyalty|bulk|none] [--export file]")), output);

        var discountName = words.Length > 1 ? words[1] : "none";

        // Each step runs only if the one before succeeded. The state is saved last, after the
        // export (if any) is written, so a failure anywhere leaves the saved state unchanged.
        var outcome = Discounts.Find(discountName)
            .ToResult(new AppError("invalid_discount", $"Unknown discount '{discountName}'; use loyalty, bulk or none."))
            .Bind(discount => StateStore.LoadOrSeed(statePath, seedPath)
                .Bind(ledger => Invoicing.Build(ledger, words[0], discount)))
            .Bind(done => Export(exportPath, Invoicing.Render(done.Invoice)).Map(_ => done))
            .Bind(done => StateStore.Save(statePath, done.Ledger).Map(_ => done.Invoice));

        switch (outcome)
        {
            case Result<Invoice>.Success success:
                foreach (var line in Invoicing.Render(success.Value))
                    output.WriteLine(line);
                output.WriteLine($"SUCCESS {success.Value.Number} saved" + (exportPath is null ? "" : $", exported to {exportPath}"));
                return 0;
            case Result<Invoice>.Failure failure:
                output.WriteLine($"ERROR [{failure.Error.Code}] {failure.Error.Message}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown Result case");
        }
    }

    static Result<string> Export(string? path, IReadOnlyList<string> lines) =>
        path is null ? Result<string>.Ok("not exported") : ReportExport.Write(path, lines);
}
