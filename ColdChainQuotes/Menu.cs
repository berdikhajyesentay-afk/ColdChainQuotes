namespace ColdChainQuotes;

// A small numbered menu. Each choice asks for the command's arguments and calls the same
// command the CLI runs, so the menu adds no logic of its own.
public static class Menu
{
    static readonly (string Label, string Command, string Hint)[] Items =
    [
        ("Quote pipeline", "quote", "[file] [standard|express|both]"),
        ("Price and confirm a booking", "confirm", "<id> [standard|express]"),
        ("Dispatch a booking", "dispatch", "<id> <vehicle> | auto <id> [firstfit|bestfit] | fleet"),
        ("Close a delivery", "deliver", "<id> [strict|tolerant] [readings] | report"),
        ("Invoice a customer", "invoice", "<customer> [loyalty|bulk] [--export file]"),
        ("Reports", "report", "[csv] [--export file]"),
        ("Booking status", "status", ""),
        ("Reset saved state", "reset", "")
    ];

    public static int Run(TextReader input, TextWriter output, Func<string[], int> run)
    {
        while (true)
        {
            output.WriteLine();
            output.WriteLine("ColdChainQuotes menu");
            for (int i = 0; i < Items.Length; i++)
                output.WriteLine($"  {i + 1}. {Items[i].Label}");
            output.WriteLine("  0. Exit");
            output.Write("Choice: ");

            var choice = input.ReadLine();
            if (choice is null || choice.Trim() == "0")
                return 0;
            if (!int.TryParse(choice.Trim(), out var n) || n < 1 || n > Items.Length)
            {
                output.WriteLine($"'{choice.Trim()}' is not a menu number.");
                continue;
            }

            var item = Items[n - 1];
            var words = Array.Empty<string>();
            if (item.Hint.Length > 0)
            {
                output.Write($"{item.Command} {item.Hint}: ");
                words = (input.ReadLine() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
            run([item.Command, .. words]);
        }
    }
}
