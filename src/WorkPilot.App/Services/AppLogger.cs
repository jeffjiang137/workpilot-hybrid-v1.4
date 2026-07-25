namespace WorkPilot.Services;

public static class AppLogger
{
    private const long MaxLogBytes = 10 * 1024 * 1024;
    private const int MaxLogFiles = 5;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot", "Logs");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var line = $"{DateTimeOffset.Now:O} [{level}] {Sanitize(message)}{Environment.NewLine}";
            if (exception is not null) line += $"{exception.GetType().Name}: {Sanitize(exception.Message)}{Environment.NewLine}";
            lock (Gate)
            {
                RotateIfNeeded(); File.AppendAllText(Path.Combine(DirectoryPath, "workpilot.log"), line);
            }
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"WorkPilot logging failed: {error.GetType().Name}"); }
    }

    private static void RotateIfNeeded()
    {
        var active = Path.Combine(DirectoryPath, "workpilot.log");
        if (!File.Exists(active) || new FileInfo(active).Length < MaxLogBytes) return;
        var oldest = Path.Combine(DirectoryPath, $"workpilot.{MaxLogFiles - 1}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = MaxLogFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(DirectoryPath, $"workpilot.{index}.log");
            if (File.Exists(source)) File.Move(source, Path.Combine(DirectoryPath, $"workpilot.{index + 1}.log"));
        }
        File.Move(active, Path.Combine(DirectoryPath, "workpilot.1.log"));
    }

    private static string Sanitize(string value) => value.ReplaceLineEndings(" ").Replace("Bearer ", "Bearer [redacted]", StringComparison.OrdinalIgnoreCase);
}
