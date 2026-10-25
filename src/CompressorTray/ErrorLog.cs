namespace SystemCompressor.Tray;

/// <summary>Minimal crash log in the shared data folder. It must never throw and never grow without bound.</summary>
public static class ErrorLog
{
    private const long MaxBytes = 100_000;
    private const int MaxEntriesPerRun = 50;
    private static int _entries;

    public static string FilePath => Path.Combine(CompressorConfiguration.DirectoryPath, "app-error.log");

    public static void Write(Exception? exception)
    {
        try
        {
            if (exception is null || ++_entries > MaxEntriesPerRun) return;
            Directory.CreateDirectory(CompressorConfiguration.DirectoryPath);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) return;
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // logging must not fail the app
        }
    }
}
