using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// High-fidelity diagnostic logger for Windows Dynamic Island.
/// Captures full lifecycle transitions, thread exceptions, power/session events,
/// memory telemetry, and continuous heartbeat monitoring.
/// </summary>
public static class AppLogger
{
    private static readonly object _fileLock = new();
    private static readonly string _logDir;
    private static readonly string _logFilePath;
    private static readonly string _tempLogFilePath;
    private static readonly Stopwatch _uptime = Stopwatch.StartNew();

    static AppLogger()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDir = Path.Combine(localAppData, "WindowsDynamicIsland", "Logs");
        _logFilePath = Path.Combine(_logDir, "diagnostics.log");
        _tempLogFilePath = Path.Combine(Path.GetTempPath(), "WindowsDynamicIsland_detailed.log");

        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch { }
    }

    public static string LogFilePath => _logFilePath;
    public static string TempLogFilePath => _tempLogFilePath;

    public static void LogInfo(string category, string message)
    {
        WriteEntry("INFO", category, message, null);
    }

    public static void LogWarning(string category, string message, Exception? ex = null)
    {
        WriteEntry("WARN", category, message, ex);
    }

    public static void LogError(string category, string message, Exception? ex = null)
    {
        WriteEntry("ERROR", category, message, ex);
    }

    public static void LogCrash(string category, Exception ex)
    {
        WriteEntry("FATAL", category, "Uncaught or critical exception encountered", ex);
    }

    public static void LogHeartbeat(string statusInfo)
    {
        long workingSetMb = Environment.WorkingSet / (1024 * 1024);
        long gcMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
        string msg = $"[Uptime: {_uptime.Elapsed:hh\\:mm\\:ss}] [RAM: {workingSetMb} MB (GC: {gcMemoryMb} MB)] {statusInfo}";
        WriteEntry("HEARTBEAT", "HealthMonitor", msg, null);
    }

    private static void WriteEntry(string level, string category, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int threadId = Environment.CurrentManagedThreadId;
            int pid = Environment.ProcessId;

            sb.AppendLine($"[{timestamp}] [{level}] [PID {pid}:T{threadId}] [{category}] {message}");

            if (ex != null)
            {
                sb.AppendLine($"  ExceptionType: {ex.GetType().FullName}");
                sb.AppendLine($"  Message: {ex.Message}");
                try
                {
                    if (!string.IsNullOrWhiteSpace(ex.Source))
                    {
                        sb.AppendLine($"  Source: {ex.Source}");
                    }
                }
                catch { }

                try
                {
                    if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    {
                        sb.AppendLine($"  StackTrace:\r\n{Indent(ex.StackTrace, "    ")}");
                    }
                }
                catch { }

                Exception? inner = ex.InnerException;
                int depth = 1;
                while (inner != null && depth <= 5)
                {
                    sb.AppendLine($"  [InnerException #{depth}: {inner.GetType().FullName}]");
                    sb.AppendLine($"    Message: {inner.Message}");
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(inner.StackTrace))
                        {
                            sb.AppendLine($"    StackTrace:\r\n{Indent(inner.StackTrace, "      ")}");
                        }
                    }
                    catch { }
                    inner = inner.InnerException;
                    depth++;
                }
            }

            string entry = sb.ToString();

            lock (_fileLock)
            {
                try { File.AppendAllText(_logFilePath, entry); } catch { }
                try { File.AppendAllText(_tempLogFilePath, entry); } catch { }

                // Also maintain legacy error log if error or fatal
                if (level == "ERROR" || level == "FATAL")
                {
                    try
                    {
                        string legacyPath = Path.Combine(Path.GetTempPath(), "WindowsDynamicIsland_error.log");
                        File.AppendAllText(legacyPath, $"[{timestamp}] [{category}] {message}{(ex != null ? $" -> {ex.Message}" : string.Empty)}\r\n");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static string Indent(string text, string prefix)
    {
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        return string.Join("\r\n", lines.Select(l => prefix + l));
    }
}
