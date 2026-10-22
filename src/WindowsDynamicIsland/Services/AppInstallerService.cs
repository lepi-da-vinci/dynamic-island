using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowsDynamicIsland.Services;

public static class AppInstallerService
{
    public const string AppId = "WindowsDynamicIsland";
    public const string DisplayName = "Windows Dynamic Island";
    public const string AppVersion = "1.1.0";
    public const string Publisher = "Lepi Da Vinci";
    public const string RepoUrl = "https://github.com/lepi-da-vinci/dynamic-island";

    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);

    public static string InstalledExePath =>
        Path.Combine(DefaultInstallDir, "WindowsDynamicIsland.exe");

    public static string UninstallerExePath =>
        Path.Combine(DefaultInstallDir, "Uninstall.exe");

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

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
            var val = key?.GetValue(AppId) as string;
            return !string.IsNullOrWhiteSpace(val);
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
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true);
            if (key == null) return;

            if (enable)
            {
                string target = exePathOverride ?? (File.Exists(InstalledExePath) ? InstalledExePath : Environment.ProcessPath ?? InstalledExePath);
                key.SetValue(AppId, $"\"{target}\"");
            }
            else
            {
                key.DeleteValue(AppId, false);
            }
        }
        catch { }
    }

    public static void InstallApplication(
        bool createDesktopShortcut = true,
        bool createStartMenuShortcut = true,
        bool enableAutostart = true,
        bool launchAfterInstall = true,
        Action<string>? progressCallback = null)
    {
        string currentExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Tidak dapat membaca lokasi file aplikasi.");

        string installDir = DefaultInstallDir;
        progressCallback?.Invoke("Menghentikan proses lama yang sedang berjalan...");
        StopOtherRunningInstances();

        progressCallback?.Invoke("Menyiapkan direktori instalasi...");
        Directory.CreateDirectory(installDir);

        string targetExe = InstalledExePath;
        string uninstallerExe = UninstallerExePath;

        // Copy main executable if we are not already running from targetExe
        if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            progressCallback?.Invoke("Menyalin file program utama...");
            File.Copy(currentExe, targetExe, overwrite: true);
        }

        // Also copy companion DLLs/deps if running from a non-single-file build folder
        string sourceDir = Path.GetDirectoryName(currentExe) ?? string.Empty;
        if (!string.IsNullOrEmpty(sourceDir) &&
            !string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
        {
            string sourceDeps = Path.Combine(sourceDir, "WindowsDynamicIsland.dll");
            if (File.Exists(sourceDeps))
            {
                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string destFile = Path.Combine(installDir, fileName);
                    if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Copy(file, destFile, overwrite: true); } catch { }
                    }
                }
            }
        }

        // Create dedicated Uninstall.exe copy so Windows Settings > Installed Apps has a dedicated uninstaller binary
        progressCallback?.Invoke("Membuat modul Uninstaller...");
        try
        {
            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(uninstallerExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(targetExe, uninstallerExe, overwrite: true);
            }
        }
        catch { }

        // Register in Windows "Installed Apps" / "Programs and Features"
        progressCallback?.Invoke("Mendaftarkan aplikasi ke Windows Installed Apps...");
        RegisterUninstallInRegistry(installDir, targetExe, uninstallerExe);

        // Configure shortcuts
        if (createStartMenuShortcut)
        {
            progressCallback?.Invoke("Membuat shortcut di Start Menu Windows...");
            CreateStartMenuShortcuts(targetExe, uninstallerExe, installDir);
        }

        if (createDesktopShortcut)
        {
            progressCallback?.Invoke("Membuat shortcut di Desktop...");
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{DisplayName}.lnk");
            CreateShortcut(desktopShortcut, targetExe, installDir, DisplayName, string.Empty);
        }

        // Configure Autostart
        progressCallback?.Invoke("Mengonfigurasi pengaturan Startup...");
        SetAutostart(enableAutostart, targetExe);

        progressCallback?.Invoke("Instalasi selesai!");

        if (launchAfterInstall)
        {
            LaunchInstalledApp();
        }
    }

    public static void UninstallApplication(Action<string>? progressCallback = null)
    {
        progressCallback?.Invoke("Menghentikan aplikasi Windows Dynamic Island...");
        StopOtherRunningInstances();

        progressCallback?.Invoke("Menghapus konfigurasi Startup...");
        SetAutostart(false);

        progressCallback?.Invoke("Menghapus shortcut Desktop & Start Menu...");
        RemoveShortcuts();

        progressCallback?.Invoke("Menghapus registrasi dari Windows Installed Apps...");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch { }

        progressCallback?.Invoke("Membersihkan file instalasi...");
        ScheduleInstallDirDeletion(DefaultInstallDir);
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

    private static void RegisterUninstallInRegistry(string installDir, string targetExe, string uninstallerExe)
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

        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"\"{targetExe}\",0", RegistryValueKind.String);
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

    private static void CreateStartMenuShortcuts(string targetExe, string uninstallerExe, string workingDir)
    {
        try
        {
            string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string appFolder = Path.Combine(programsDir, DisplayName);
            Directory.CreateDirectory(appFolder);

            // Root Start Menu shortcut for fast Windows Search indexing
            CreateShortcut(
                Path.Combine(programsDir, $"{DisplayName}.lnk"),
                targetExe,
                workingDir,
                DisplayName,
                string.Empty);

            // Folder shortcuts (Launch & Uninstall)
            CreateShortcut(
                Path.Combine(appFolder, $"{DisplayName}.lnk"),
                targetExe,
                workingDir,
                DisplayName,
                string.Empty);

            CreateShortcut(
                Path.Combine(appFolder, $"Uninstall {DisplayName}.lnk"),
                uninstallerExe,
                workingDir,
                $"Uninstall {DisplayName}",
                "--uninstall");
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

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description, string arguments)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            shortcut.IconLocation = $"{targetPath},0";
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                shortcut.Arguments = arguments;
            }
            shortcut.Save();
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

    private static void ScheduleInstallDirDeletion(string installDir)
    {
        if (!Directory.Exists(installDir)) return;

        try
        {
            // Background cmd waits 2 seconds for Uninstall.exe to exit, then deletes the folder cleanly
            string cmdArgs = $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{installDir}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }
}

