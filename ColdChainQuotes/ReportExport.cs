namespace ColdChainQuotes;

// Owns the output file. Lines go to a temporary file that replaces the target only once it is complete.
public static class ReportExport
{
    public static Result<string> Write(string path, IEnumerable<string> lines)
    {
        var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false))
            {
                foreach (var line in lines)
                    writer.WriteLine(line);
            }
            File.Move(temp, path, overwrite: true);
            return Result<string>.Ok(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DeleteIfPresent(temp);
            return Result<string>.Fail(new("export_failed", $"Cannot write report '{path}': {ex.Message}"));
        }
    }

    static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
