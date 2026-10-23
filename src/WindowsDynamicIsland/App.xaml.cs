using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using WindowsDynamicIsland.Services;
using WindowsDynamicIsland.UI;

namespace WindowsDynamicIsland;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.LogInfo("Lifecycle", $"=== Windows Dynamic Island Starting === PID={Environment.ProcessId}, Args='{string.Join(" ", e.Args)}', OS={Environment.OSVersion}, Is64Bit={Environment.Is64BitProcess}, CLR={Environment.Version}");

        DispatcherUnhandledException += (s, args) =>
        {
            AppLogger.LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLogger.LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.LogCrash("AppDomainUnhandledException", ex);
            }
        };

        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            AppLogger.LogInfo("Lifecycle", $"AppDomain.ProcessExit triggered. ExitCode={Environment.ExitCode}");
        };

        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, args) =>
            {
                AppLogger.LogInfo("SystemEvents", $"SessionSwitch Reason={args.Reason}");
            };
        }
        catch { }

        base.OnStartup(e);

        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        bool isArgUninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        bool isArgQuiet = e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        bool isArgSilentInstall = e.Args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent-install", StringComparison.OrdinalIgnoreCase) || a.Equals("/install", StringComparison.OrdinalIgnoreCase));
        bool isArgSetupGui = e.Args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase));

        bool isExeUninstaller = exeName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
        bool isExeSetup = exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase) || exeName.Contains("Installer", StringComparison.OrdinalIgnoreCase);

        // 1. Handle Uninstall Mode (CLI or Uninstall.exe)
        if (isArgUninstall || isExeUninstaller)
        {
            if (isArgQuiet)
            {
                try { AppInstallerService.UninstallApplication(); } catch { }
                Shutdown();
                return;
            }

            var uninstallWin = new SetupWindow(isUninstallMode: true);
            MainWindow = uninstallWin;
            uninstallWin.Show();
            return;
        }

        // 2. Handle Silent Install Mode (--install)
        if (isArgSilentInstall)
        {
            try
            {
                AppInstallerService.InstallApplication(
                    createDesktopShortcut: true,
                    createStartMenuShortcut: true,
                    enableAutostart: true,
                    launchAfterInstall: true);
            }
            catch (Exception ex)
            {
                AppLogger.LogCrash("SilentInstall", ex);
            }
            Shutdown();
            return;
        }

        // 3. Handle Interactive Setup Wizard Mode (WindowsDynamicIsland-Setup.exe or --setup)
        if (isArgSetupGui || isExeSetup)
        {
            var setupWin = new SetupWindow(isUninstallMode: false);
            MainWindow = setupWin;
            setupWin.Show();
            return;
        }

        // 4. Normal Dynamic Island Overlay Launch
        try
        {
            int currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("WindowsDynamicIsland"))
            {
                if (proc.Id != currentPid)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(1000);
                    }
                    catch { }
                }
            }
        }
        catch { }

        bool hasHandle = false;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\WindowsDynamicIsland_SingleInstance_Mutex", out bool createdNew);
            hasHandle = createdNew || _singleInstanceMutex.WaitOne(500, false);
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
            AppLogger.LogWarning("Lifecycle", $"[PID {Environment.ProcessId}] Mutex not acquired -> Shutdown()");
            Shutdown();
            return;
        }

        AppLogger.LogInfo("Lifecycle", $"[PID {Environment.ProcessId}] Mutex acquired successfully. Creating MainWindow.");
        try
        {
            var islandWindow = new MainWindow();
            MainWindow = islandWindow;
            AppLogger.LogInfo("Lifecycle", $"[PID {Environment.ProcessId}] MainWindow instance created. Calling Show().");
            islandWindow.Show();
            AppLogger.LogInfo("Lifecycle", $"[PID {Environment.ProcessId}] islandWindow.Show() succeeded.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("MainWindowCreation", ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.LogInfo("Lifecycle", $"App.OnExit called. ApplicationExitCode={e.ApplicationExitCode}. CallStack:\r\n{new StackTrace(1, true)}");
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }
}
