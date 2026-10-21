using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WindowsDynamicIsland;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // Prevent app exit!
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogCrash("AppDomainUnhandledException", ex);
            }
        };

        bool hasHandle = false;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\WindowsDynamicIsland_SingleInstance_Mutex", out bool createdNew);
            hasHandle = createdNew || _singleInstanceMutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            hasHandle = true;
        }
        catch
        {
            hasHandle = true;
        }

        if (!hasHandle)
        {
            LogCrash("Lifecycle", new Exception($"[PID {Environment.ProcessId}] Mutex not acquired -> Shutdown()"));
            Shutdown();
            return;
        }

        LogCrash("Lifecycle", new Exception($"[PID {Environment.ProcessId}] Startup Succeeded"));
        base.OnStartup(e);
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "WindowsDynamicIsland_error.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.Message}\r\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogCrash("Lifecycle", new Exception($"[PID {Environment.ProcessId}] App.OnExit: {e.ApplicationExitCode}"));
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // Ignore mutex release on exit
        }

        base.OnExit(e);
    }
}
