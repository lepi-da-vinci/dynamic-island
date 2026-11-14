using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Pure native Windows installer, updater, and uninstaller engine.
/// Uses native Win32/COM APIs (IShellLinkW, IPersistFile, Registry, DeleteFileW ADS unblocking,
/// and self-relocating WinExe uninstaller) with zero PowerShell or console processes.
/// </summary>
public static class AppInstallerService
{
    public const string AppId = "WindowsDynamicIsland";
    public const string DisplayName = "Windows Dynamic Island";

    public static string AppVersion
    {
        get
        {
            var v = typeof(AppInstallerService).Assembly.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.2.0";
        }
    }

    public const string Publisher = "Lepi Da Vinci";
    public const string RepoUrl = "https://github.com/lepi-da-vinci/dynamic-island";

    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
    private const string LegacyRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);

    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

    public static string InstalledExePath =>
        Path.Combine(DefaultInstallDir, "WindowsDynamicIsland.exe");

    public static string UninstallerExePath =>
        Path.Combine(DefaultInstallDir, "Uninstall.exe");

    private static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"{DisplayName}.lnk");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string lpFileName);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, false);
            return key != null && File.Exists(InstalledExePath);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, false);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            return File.Exists(StartupShortcutPath);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutostart(bool enable, string? exePathOverride = null)
    {
        try
        {
            // Clean up any legacy HKCU Run registry value so Startup is managed cleanly in one place
            using (var runKey = Registry.CurrentUser.OpenSubKey(LegacyRunRegistryPath, writable: true))
            {
                runKey?.DeleteValue(AppId, throwOnMissingValue: false);
            }
        }
        catch { }

        try
        {
            if (enable)
            {
                string target = exePathOverride ?? (File.Exists(InstalledExePath) ? InstalledExePath : Environment.ProcessPath ?? InstalledExePath);
                string workingDir = Path.GetDirectoryName(target) ?? DefaultInstallDir;
                string iconPath = Path.Combine(workingDir, "app_icon.ico");
                CreateShortcutNative(StartupShortcutPath, target, workingDir, DisplayName, string.Empty, iconPath);
            }
            else
            {
                if (File.Exists(StartupShortcutPath))
                {
                    File.Delete(StartupShortcutPath);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Removes NTFS Zone.Identifier Alternate Data Stream (Mark-of-the-Web) natively via Win32 DeleteFileW
    /// so downloaded/extracted binaries never trigger SmartScreen prompts.
    /// </summary>
    public static void UnblockDirectoryFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return;
        try
        {
            foreach (string file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try { DeleteFileW(file + ":Zone.Identifier"); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Cleans up any leftover temporary uninstaller directories in %TEMP% from previous uninstall/update runs.
    /// </summary>
    public static void CleanupTempInstallerDirectories()
    {
        try
        {
            string tempRoot = Path.GetTempPath();
            foreach (string dir in Directory.GetDirectories(tempRoot, "WDI_Setup_*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }

            string updatesRoot = Path.Combine(UserDataDir, "Updates");
            if (Directory.Exists(updatesRoot))
            {
                string staged = Path.Combine(updatesRoot, "staged");
                string zip = Path.Combine(updatesRoot, "update.zip");
                try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); } catch { }
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// If the uninstaller was started directly from inside DefaultInstallDir, copies the uninstaller
    /// runtime into %TEMP%\WDI_Setup_<guid> and relaunches it from there so DefaultInstallDir is not locked
    /// and can be 100% deleted without PowerShell or cmd.exe.
    /// </summary>
    public static bool TryRelocateUninstallerIfLocked(string[] currentArgs)
    {
        try
        {
            if (currentArgs.Any(a => a.Equals("--relocated", StringComparison.OrdinalIgnoreCase)))
                return false;

            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe)) return false;

            string currentDir = Path.GetFullPath(Path.GetDirectoryName(currentExe) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetInstallDir = Path.GetFullPath(DefaultInstallDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(currentDir, targetInstallDir, StringComparison.OrdinalIgnoreCase))
                return false;

            string tempDir = Path.Combine(Path.GetTempPath(), $"WDI_Setup_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            foreach (string file in Directory.GetFiles(currentDir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(tempDir, name), overwrite: true);
            }

            string tempExe = Path.Combine(tempDir, Path.GetFileName(currentExe));
            if (!File.Exists(tempExe))
            {
                tempExe = Path.Combine(tempDir, "WindowsDynamicIsland.exe");
            }

            var relocatedArgs = new List<string>(currentArgs)
            {
                "--uninstall",
                "--relocated",
                "--wait-pid",
                Environment.ProcessId.ToString()
            };

            Process.Start(new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = string.Join(" ", relocatedArgs.Distinct(StringComparer.OrdinalIgnoreCase)),
                WorkingDirectory = tempDir,
                UseShellExecute = false
            });

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Installer", "Failed relocating uninstaller to temp", ex);
            return false;
        }
    }

    public static void WaitForProcessExit(int pid, int maxWaitMs = 6000)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.WaitForExit(maxWaitMs);
        }
        catch { }
    }

    public static void InstallApplication(
        bool createDesktopShortcut = true,
        bool createStartMenuShortcut = true,
        bool enableAutostart = true,
        bool launchAfterInstall = true,
        Action<string>? progressCallback = null,
        Action<int, string>? detailedProgressCallback = null)
    {
        bool en = LocalizationService.Instance.IsEnglish;
        void Report(int pct, string idMsg, string enMsg)
        {
            string msg = en ? enMsg : idMsg;
            progressCallback?.Invoke(msg);
            detailedProgressCallback?.Invoke(pct, msg);
        }

        string currentExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException(en ? "Unable to locate installer executable." : "Tidak dapat membaca lokasi file aplikasi.");

        string installDir = DefaultInstallDir;
        Report(12, "Menghentikan proses lama yang sedang berjalan...", "Stopping active instances...");
        StopOtherRunningInstances();

        Report(28, "Menyiapkan direktori instalasi...", "Preparing installation directory...");
        Directory.CreateDirectory(installDir);

        string targetExe = InstalledExePath;
        string uninstallerExe = UninstallerExePath;

        // Copy main executable if we are not already running from targetExe
        if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            Report(45, "Menyalin file program utama...", "Copying core application binaries...");
            CopyFileWithRetry(currentExe, targetExe);
        }

        // Also copy companion DLLs/deps if running from an unpacked build folder
        string sourceDir = Path.GetDirectoryName(currentExe) ?? string.Empty;
        if (!string.IsNullOrEmpty(sourceDir) &&
            !string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
        {
            string sourceDeps = Path.Combine(sourceDir, "WindowsDynamicIsland.dll");
            if (File.Exists(sourceDeps))
            {
                string[] allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                for (int i = 0; i < allFiles.Length; i++)
                {
                    string file = allFiles[i];
                    if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relPath = Path.GetRelativePath(sourceDir, file);
                    string destFile = Path.Combine(installDir, relPath);
                    string? destSubDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destSubDir)) Directory.CreateDirectory(destSubDir);

                    if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                    {
                        CopyFileWithRetry(file, destFile);
                    }
                }
            }
        }

        Report(64, "Membuat modul Uninstaller & menghapus blokir keamanan...", "Creating Uninstaller module & unblocking files...");
        try
        {
            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(uninstallerExe), StringComparison.OrdinalIgnoreCase))
            {
                CopyFileWithRetry(targetExe, uninstallerExe);
            }
        }
        catch { }

        // Extract embedded app_icon.ico into installDir so shortcuts & Installed Apps always display the crisp icon
        string iconFilePath = Path.Combine(installDir, "app_icon.ico");
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app_icon.ico"));
            if (streamInfo != null)
            {
                using var fs = File.Create(iconFilePath);
                streamInfo.Stream.CopyTo(fs);
            }
        }
        catch { }

        UnblockDirectoryFiles(installDir);

        // Register in Windows "Installed Apps" / "Programs and Features"
        Report(78, "Mendaftarkan aplikasi ke Windows Installed Apps...", "Registering in Windows Installed Apps...");
        RegisterUninstallInRegistry(installDir, targetExe, uninstallerExe, iconFilePath);

        // Configure shortcuts via native IShellLinkW COM
        Report(88, "Membuat pintasan Start Menu, Desktop & Startup...", "Configuring Start Menu, Desktop & Startup shortcuts...");
        if (createStartMenuShortcut)
        {
            CreateStartMenuShortcuts(targetExe, uninstallerExe, installDir, iconFilePath);
        }

        if (createDesktopShortcut)
        {
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{DisplayName}.lnk");
            CreateShortcutNative(desktopShortcut, targetExe, installDir, DisplayName, string.Empty, iconFilePath);
        }

        SetAutostart(enableAutostart, targetExe);

        // Notify Windows Explorer shell of shortcut/icon updates natively (no external process needed)
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        Report(100, "Instalasi selesai dengan sukses!", "Installation completed successfully!");

        if (launchAfterInstall)
        {
            LaunchInstalledApp();
        }
    }

    /// <summary>
    /// Applies a staged update directory directly into targetInstallDir without PowerShell.
    /// Executed by the staged WindowsDynamicIsland.exe process launched with --apply-update.
    /// </summary>
    public static void ApplyStagedUpdate(
        string targetInstallDir,
        int waitPid,
        Action<int, string>? detailedProgressCallback = null)
    {
        bool en = LocalizationService.Instance.IsEnglish;
        void Report(int pct, string idMsg, string enMsg) =>
            detailedProgressCallback?.Invoke(pct, en ? enMsg : idMsg);

        Report(15, "Menunggu aplikasi versi sebelumnya berhenti...", "Waiting for previous instance to close...");
        WaitForProcessExit(waitPid, 6000);
        StopOtherRunningInstances();

        string sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(targetInstallDir))
        {
            targetInstallDir = DefaultInstallDir;
        }
        Directory.CreateDirectory(targetInstallDir);

        string[] files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string srcFile = files[i];
            if (srcFile.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

            string rel = Path.GetRelativePath(sourceDir, srcFile);
            string dstFile = Path.Combine(targetInstallDir, rel);
            string? dstSub = Path.GetDirectoryName(dstFile);
            if (!string.IsNullOrEmpty(dstSub)) Directory.CreateDirectory(dstSub);

            CopyFileWithRetry(srcFile, dstFile);

            int pct = 20 + (int)((i + 1) * 55.0 / Math.Max(1, files.Length));
            Report(pct, $"Memperbarui komponen ({i + 1}/{files.Length})...", $"Updating components ({i + 1}/{files.Length})...");
        }

        string targetExe = Path.Combine(targetInstallDir, "WindowsDynamicIsland.exe");
        string uninstallerExe = Path.Combine(targetInstallDir, "Uninstall.exe");
        string iconFilePath = Path.Combine(targetInstallDir, "app_icon.ico");

        if (File.Exists(targetExe))
        {
            try { CopyFileWithRetry(targetExe, uninstallerExe); } catch { }
        }

        Report(85, "Memperbarui registrasi versi Windows...", "Updating Windows registry metadata...");
        UnblockDirectoryFiles(targetInstallDir);
        RegisterUninstallInRegistry(targetInstallDir, targetExe, uninstallerExe, iconFilePath);

        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        Report(100, "Pembaruan selesai! Menjalankan versi baru...", "Update complete! Launching new version...");

        if (File.Exists(targetExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = targetInstallDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public static void UninstallApplication(
        Action<string>? progressCallback = null,
        bool removeUserData = false,
        Action<int, string>? detailedProgressCallback = null)
    {
        bool en = LocalizationService.Instance.IsEnglish;
        void Report(int pct, string idMsg, string enMsg)
        {
            string msg = en ? enMsg : idMsg;
            progressCallback?.Invoke(msg);
            detailedProgressCallback?.Invoke(pct, msg);
        }

        Report(15, "Menghentikan proses Windows Dynamic Island...", "Stopping Windows Dynamic Island processes...");
        StopOtherRunningInstances();

        Report(35, "Menghapus konfigurasi Startup otomatis...", "Removing Windows Startup configuration...");
        SetAutostart(false);

        Report(55, "Menghapus pintasan Desktop & Start Menu...", "Removing Desktop & Start Menu shortcuts...");
        RemoveShortcuts();

        Report(75, "Menghapus registrasi dari Windows Installed Apps...", "Removing Windows Installed Apps registration...");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch { }

        Report(90, "Membersihkan berkas instalasi aplikasi...", "Cleaning up application binaries...");
        DeleteDirectoryThoroughly(DefaultInstallDir);

        if (removeUserData && Directory.Exists(UserDataDir))
        {
            Report(96, "Menghapus data pengaturan & cache pengguna...", "Removing user settings & cache data...");
            DeleteDirectoryThoroughly(UserDataDir);
        }

        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        Report(100, "Uninstall selesai sepenuhnya.", "Uninstallation completed cleanly.");
    }

    public static void LaunchInstalledApp()
    {
        if (!File.Exists(InstalledExePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExePath,
                WorkingDirectory = DefaultInstallDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void CopyFileWithRetry(string sourceFile, string destFile, int maxAttempts = 8)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourceFile, destFile, overwrite: true);
                return;
            }
            catch
            {
                if (attempt == maxAttempts) throw;
                Thread.Sleep(200);
            }
        }
    }

    private static void RegisterUninstallInRegistry(string installDir, string targetExe, string uninstallerExe, string iconFilePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, true);
        if (key == null) return;

        long sizeKb = 70000;
        try
        {
            var dirInfo = new DirectoryInfo(installDir);
            long totalBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            sizeKb = Math.Max(1024, totalBytes / 1024);
        }
        catch { }

        string displayIcon = File.Exists(iconFilePath) ? iconFilePath : $"\"{targetExe}\",0";

        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayIcon", displayIcon, RegistryValueKind.String);
        key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{uninstallerExe}\" --uninstall --quiet", RegistryValueKind.String);
        key.SetValue("URLInfoAbout", RepoUrl, RegistryValueKind.String);
        key.SetValue("HelpLink", RepoUrl, RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
    }

    private static void CreateStartMenuShortcuts(string targetExe, string uninstallerExe, string workingDir, string iconFilePath)
    {
        try
        {
            string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string appFolder = Path.Combine(programsDir, DisplayName);
            Directory.CreateDirectory(appFolder);

            CreateShortcutNative(
                Path.Combine(programsDir, $"{DisplayName}.lnk"),
                targetExe,
                workingDir,
                DisplayName,
                string.Empty,
                iconFilePath);

            CreateShortcutNative(
                Path.Combine(appFolder, $"{DisplayName}.lnk"),
                targetExe,
                workingDir,
                DisplayName,
                string.Empty,
                iconFilePath);

            CreateShortcutNative(
                Path.Combine(appFolder, $"Uninstall {DisplayName}.lnk"),
                uninstallerExe,
                workingDir,
                $"Uninstall {DisplayName}",
                "--uninstall",
                iconFilePath);
        }
        catch { }
    }

    private static void RemoveShortcuts()
    {
        try
        {
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{DisplayName}.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
        }
        catch { }

        try
        {
            string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string rootShortcut = Path.Combine(programsDir, $"{DisplayName}.lnk");
            if (File.Exists(rootShortcut)) File.Delete(rootShortcut);

            string appFolder = Path.Combine(programsDir, DisplayName);
            if (Directory.Exists(appFolder)) Directory.Delete(appFolder, recursive: true);
        }
        catch { }
    }

    /// <summary>
    /// Creates a Windows .lnk shortcut using native Shell COM interfaces (IShellLinkW + IPersistFile)
    /// without invoking WScript.Shell or external scripting engines.
    /// </summary>
    private static void CreateShortcutNative(string shortcutPath, string targetPath, string workingDir, string description, string arguments, string iconFilePath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                link.SetArguments(arguments);
            }

            if (File.Exists(iconFilePath))
            {
                link.SetIconLocation(iconFilePath, 0);
            }
            else
            {
                link.SetIconLocation(targetPath, 0);
            }

            var persistFile = (IPersistFile)link;
            persistFile.Save(shortcutPath, false);
        }
        catch { }
    }

    private static void StopOtherRunningInstances()
    {
        int currentPid = Environment.ProcessId;
        foreach (string procName in new[] { "WindowsDynamicIsland", "WindowsDynamicIsland-Setup", "Uninstall" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    if (proc.Id != currentPid)
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(1500);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static void DeleteDirectoryThoroughly(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch { }
                }

                Directory.Delete(dirPath, recursive: true);
                if (!Directory.Exists(dirPath)) return;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
