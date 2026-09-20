using System.Text;

namespace NoteVault;

public static class Log
{
    private static readonly object Gate = new();
    private static string _dir = Path.Combine(Path.GetTempPath(), "note-vault-logs");

    public static void Init(string logDir)
    {
        lock (Gate)
        {
            _dir = logDir;
            Directory.CreateDirectory(_dir);
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_dir);
                var file = Path.Combine(_dir, $"note-vault-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
