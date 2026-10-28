using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class AppShortcutItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string IconExtractPath { get; set; } = string.Empty;

    [JsonIgnore]
    public ImageSource? IconImage { get; set; }
}

public sealed class AppShortcutService
{
    private readonly string _storagePath;
    private readonly List<AppShortcutItem> _items = new();

    public IReadOnlyList<AppShortcutItem> Items => _items;

    public event Action? ShortcutsChanged;

    public AppShortcutService()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicIsland");
        Directory.CreateDirectory(appData);
        _storagePath = Path.Combine(appData, "shortcuts.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                string json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<AppShortcutItem>>(json);
                if (loaded is { Count: > 0 })
                {
                    _items.Clear();
                    foreach (var item in loaded)
                    {
                        HydrateIcon(item);
                        _items.Add(item);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppShortcutService.Load", "Failed to load shortcuts.json", ex);
        }

        PopulateSmartDefaults();
        Save();
    }

    public void PopulateSmartDefaults()
    {
        _items.Clear();

        string sys32 = Environment.SystemDirectory;
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string localApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 1. File Explorer
        AddDefaultItem("File Explorer", Path.Combine(winDir, "explorer.exe"));

        // 2. Web Browser (Chrome or Edge)
        string chromePath = FindFirstExisting(
            Path.Combine(progFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(progFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localApps, "Google", "Chrome", "Application", "chrome.exe"));
        string edgePath = FindFirstExisting(
            Path.Combine(progFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(progFiles, "Microsoft", "Edge", "Application", "msedge.exe"));

        if (!string.IsNullOrEmpty(chromePath))
        {
            AddDefaultItem("Google Chrome", chromePath);
        }
        else if (!string.IsNullOrEmpty(edgePath))
        {
            AddDefaultItem("Microsoft Edge", edgePath);
        }

        // 3. VS Code (if installed)
        string vsCodePath = FindFirstExisting(
            Path.Combine(localApps, "Microsoft VS Code", "Code.exe"),
            Path.Combine(progFiles, "Microsoft VS Code", "Code.exe"));
        if (!string.IsNullOrEmpty(vsCodePath))
        {
            AddDefaultItem("VS Code", vsCodePath);
        }

        // 4. Terminal / PowerShell
        string wtPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        string psPath = Path.Combine(sys32, "WindowsPowerShell", "v1.0", "powershell.exe");
        AddDefaultItem("Terminal", File.Exists(wtPath) ? wtPath : psPath, iconPath: psPath);

        // 5. Task Manager
        AddDefaultItem("Task Manager", Path.Combine(sys32, "taskmgr.exe"));

        // 6. Calculator
        AddDefaultItem("Kalkulator", Path.Combine(sys32, "calc.exe"));

        // 7. Notepad
        AddDefaultItem("Notepad", Path.Combine(sys32, "notepad.exe"));

        // 8. Snipping Tool
        string snipPath = Path.Combine(sys32, "SnippingTool.exe");
        if (File.Exists(snipPath))
        {
            AddDefaultItem("Snipping Tool", snipPath);
        }
    }

    private void AddDefaultItem(string name, string targetPath, string? iconPath = null)
    {
        var item = new AppShortcutItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            TargetPath = targetPath,
            IconExtractPath = iconPath ?? targetPath
        };
        HydrateIcon(item);
        _items.Add(item);
    }

    private static string FindFirstExisting(params string[] paths)
    {
        foreach (string p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }
        return string.Empty;
    }

    public bool AddShortcutFromPath(string path, string? customName = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim().Trim('"');

        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        if (_items.Any(i => string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        string displayName = customName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Aplikasi";
        }

        var item = new AppShortcutItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = displayName,
            TargetPath = path,
            IconExtractPath = path
        };

        HydrateIcon(item);
        _items.Add(item);
        Save();
        ShortcutsChanged?.Invoke();
        return true;
    }

    public void RemoveShortcut(string id)
    {
        int removed = _items.RemoveAll(i => i.Id == id);
        if (removed > 0)
        {
            Save();
            ShortcutsChanged?.Invoke();
        }
    }

    public void ResetDefaults()
    {
        PopulateSmartDefaults();
        Save();
        ShortcutsChanged?.Invoke();
    }

    public bool LaunchShortcut(AppShortcutItem item)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.TargetPath,
                Arguments = item.Arguments,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppShortcutService.Launch", $"Failed to launch {item.TargetPath}", ex);
            return false;
        }
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppShortcutService.Save", "Failed to save shortcuts.json", ex);
        }
    }

    private static void HydrateIcon(AppShortcutItem item)
    {
        try
        {
            string iconSource = !string.IsNullOrWhiteSpace(item.IconExtractPath) ? item.IconExtractPath : item.TargetPath;
            if (string.IsNullOrWhiteSpace(iconSource)) return;

            IntPtr hIcon = IntPtr.Zero;

            if (File.Exists(iconSource) || Directory.Exists(iconSource))
            {
                var shfi = new Win32Interop.SHFILEINFO();
                IntPtr res = Win32Interop.SHGetFileInfo(
                    iconSource,
                    0,
                    ref shfi,
                    (uint)Marshal.SizeOf(shfi),
                    Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON);

                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    hIcon = shfi.hIcon;
                }
            }

            if (hIcon == IntPtr.Zero && File.Exists(iconSource))
            {
                Win32Interop.ExtractIconEx(iconSource, 0, out hIcon, out _, 1);
            }

            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    item.IconImage = bmp;
                }
                finally
                {
                    Win32Interop.DestroyIcon(hIcon);
                }
            }
        }
        catch
        {
            // Leave IconImage null; UI will display clean vector fallback
        }
    }
}

