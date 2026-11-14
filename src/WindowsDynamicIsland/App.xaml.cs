using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using WindowsDynamicIsland.Services;
using WindowsDynamicIsland.UI;

namespace WindowsDynamicIsland
{
    public partial class App : Application
    {
        private static Mutex? _appMutex;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.Default;
                SetProcessDPIAware();
            }
            catch { }

            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            string exeName = Path.GetFileNameWithoutExtension(currentExePath);

            bool isArgUninstall = false;
            bool isArgInstall = false;
            bool isArgSetup = false;
            bool isArgUpdate = false;
            bool isArgApplyUpdate = false;
            bool isArgRelocated = false;
            bool isArgQuiet = false;
            int waitPid = 0;
            string? targetInstallDir = null;

            for (int i = 0; i < e.Args.Length; i++)
            {
                string arg = e.Args[i];
                if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    isArgUninstall = true;
                }
                else if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/s", StringComparison.OrdinalIgnoreCase))
                {
                    isArgQuiet = true;
                }
                else if (arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--silent-install", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/install", StringComparison.OrdinalIgnoreCase))
                {
                    isArgInstall = true;
                }
                else if (arg.Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/setup", StringComparison.OrdinalIgnoreCase))
                {
                    isArgSetup = true;
                }
                else if (arg.Equals("--update", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/update", StringComparison.OrdinalIgnoreCase))
                {
                    isArgUpdate = true;
                }
                else if (arg.Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
                {
                    isArgApplyUpdate = true;
                    if (i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith('-'))
                    {
                        targetInstallDir = e.Args[++i];
                    }
                }
                else if (arg.Equals("--relocated", StringComparison.OrdinalIgnoreCase))
                {
                    isArgRelocated = true;
                }
                else if (arg.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int parsedPid))
                    {
                        waitPid = parsedPid;
                        i++;
                    }
                }
            }

            if (!isArgRelocated && !isArgApplyUpdate)
            {
                AppInstallerService.CleanupTempInstallerDirectories();
            }

            // 0. Staged Update Applicator Mode (--apply-update <targetInstallDir> --wait-pid <pid>)
            if (isArgApplyUpdate)
            {
                string resolvedTarget = string.IsNullOrWhiteSpace(targetInstallDir)
                    ? AppInstallerService.DefaultInstallDir
                    : targetInstallDir;

                if (isArgQuiet)
                {
                    AppInstallerService.ApplyStagedUpdate(resolvedTarget, waitPid);
                    Shutdown();
                    return;
                }

                base.OnStartup(e);
                var applyWin = new SetupWindow(
                    SetupWindowMode.ApplyStagedUpdate,
                    updateService: null,
                    targetInstallDir: resolvedTarget,
                    waitPid: waitPid);
                MainWindow = applyWin;
                applyWin.Show();
                return;
            }

            // 1. Uninstall Mode (--uninstall or Uninstall.exe)
            if (isArgUninstall || exeName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                if (!isArgRelocated && AppInstallerService.TryRelocateUninstallerIfLocked(e.Args))
                {
                    Shutdown();
                    return;
                }

                if (isArgQuiet)
                {
                    if (waitPid > 0)
                    {
                        AppInstallerService.WaitForProcessExit(waitPid, 6000);
                    }
                    AppInstallerService.UninstallApplication();
                    Shutdown();
                    return;
                }

                base.OnStartup(e);
                var uninstallWin = new SetupWindow(
                    SetupWindowMode.Uninstall,
                    updateService: null,
                    targetInstallDir: AppInstallerService.DefaultInstallDir,
                    waitPid: waitPid);
                MainWindow = uninstallWin;
                uninstallWin.Show();
                return;
            }

            // 2. Silent Auto-Install Mode (--install)
            if (isArgInstall)
            {
                if (waitPid > 0)
                {
                    AppInstallerService.WaitForProcessExit(waitPid, 5000);
                }
                AppInstallerService.InstallApplication(true, true, true, true);
                Shutdown();
                return;
            }

            // 3. Dedicated Standalone Updater Mode (--update)
            if (isArgUpdate)
            {
                base.OnStartup(e);
                var updateWin = new SetupWindow(SetupWindowMode.Update, new UpdateCheckerService());
                MainWindow = updateWin;
                updateWin.Show();
                return;
            }

            // 4. Interactive Setup Wizard Mode (--setup or *Setup*.exe / *Installer*.exe)
            if (isArgSetup ||
                exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                exeName.Contains("Installer", StringComparison.OrdinalIgnoreCase))
            {
                base.OnStartup(e);
                var setupWin = new SetupWindow(SetupWindowMode.Install, waitPid: waitPid);
                MainWindow = setupWin;
                setupWin.Show();
                return;
            }

            // 5. Normal Application Startup (Single-Instance Guard)
            const string mutexName = "Global\\WindowsDynamicIsland_SingleInstance_Mutex_v2";
            _appMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                try
                {
                    int currentPid = Environment.ProcessId;
                    foreach (var proc in Process.GetProcessesByName("WindowsDynamicIsland"))
                    {
                        using (proc)
                        {
                            if (proc.Id != currentPid)
                            {
                                proc.Kill();
                                proc.WaitForExit(1500);
                            }
                        }
                    }
                }
                catch { }
            }

            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                AppLogger.LogError("App.DispatcherUnhandledException", args.Exception.Message, args.Exception);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    AppLogger.LogError("AppDomain.UnhandledException", ex.Message, ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                AppLogger.LogError("TaskScheduler.UnobservedTaskException", args.Exception.Message, args.Exception);
                args.SetObserved();
            };

            try
            {
                var mainWin = new MainWindow();
                MainWindow = mainWin;
                mainWin.Show();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("OnStartup.MainWindow", ex.Message, ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
