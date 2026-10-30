using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowsDynamicIsland.Services;

public sealed class AppSettingsData
{
    // General - Startup
    public bool StartWithWindows { get; set; } = true;

    // General - Notch
    public int NotchSizePercent { get; set; } = 0; // 0 = Auto (100%), 100, 115, 130, 150
    public bool MinimalNotchEnabled { get; set; } = false;
    public bool AutoTuckEnabled { get; set; } = true;
    public int IdleCycleSeconds { get; set; } = 10;

    // General - Notch Tabs Customization
    public bool ShowTabMedia { get; set; } = true;
    public bool ShowTabTodo { get; set; } = true;
    public bool ShowTabPomodoro { get; set; } = true;
    public bool ShowTabShortcuts { get; set; } = true;
    public bool ShowTabPerformance { get; set; } = true;
    public bool ShowTabShelf { get; set; } = true;

    // Display & Screenshot Mode
    public bool AutoHideFullScreen { get; set; } = true;
    public bool ScreenshotAutoTuckEnabled { get; set; } = true;
    public bool ExcludeFromScreenCapture { get; set; } = false;
    public int TopOffsetPx { get; set; } = 0;
    public int TopEdgeHoverSensitivityMs { get; set; } = 60;

    // Appearance & Design Themes
    public string IslandPlacementStyle { get; set; } = "Notch"; // Notch (Top-Attached), PunchHole (Floating Capsule)
    public string IslandSurfaceTheme { get; set; } = "OledBlack"; // OledBlack, MidnightCarbon, FrostedGraphite, CyberMatrix, RoyalAmethyst, CrimsonObsidian, Monochrome
    public string AccentTheme { get; set; } = "Auto"; // Auto, Cyan, Green, Purple, Orange, Rose, Gold, White, Monochrome
    public string NotchCornerStyle { get; set; } = "Squircle"; // Squircle, UltraPill, Sharp
    public string RimGlowStyle { get; set; } = "Subtle"; // Subtle, AccentNeon, Hidden
    public double NotchBackgroundOpacity { get; set; } = 1.0; // 1.0, 0.94, 0.86
    public double GlowIntensity { get; set; } = 1.0; // 0.0, 0.5, 1.0, 1.4
    public bool ShowContourRim { get; set; } = true;

    // Music and lyrics
    public bool LyricsEnabled { get; set; } = true;
    public bool SlowLyricPanEnabled { get; set; } = true;
    public bool MediaTrackChangeToastEnabled { get; set; } = true;
    public bool AudioVisualizerEnabled { get; set; } = true;

    // To-do
    public bool ShowTodoBadgeOnIdle { get; set; } = true;

    // Calendar & Clock
    public bool ShowDateOnIdle { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;
    public bool ShowSecondsOnClock { get; set; } = false;

    // Pomodoro
    public int DefaultFocusMinutes { get; set; } = 25;
    public int DefaultBreakMinutes { get; set; } = 5;
    public bool ShowPomodoroOnIdle { get; set; } = true;

    // Water
    public double WaterDailyTargetLiters { get; set; } = 1.5;
    public double WaterCupSizeLiters { get; set; } = 0.25;

    // Shelf
    public bool AutoOpenShelfOnDrag { get; set; } = true;

    // HUD & popups
    public bool VolumeBrightnessHudEnabled { get; set; } = true;
    public bool BatteryToastEnabled { get; set; } = true;
    public bool BluetoothToastEnabled { get; set; } = true;
    public bool SystemNotificationToastEnabled { get; set; } = true;
    public double ToastDurationSeconds { get; set; } = 2.8;
}

public sealed class AppSettingsService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "WindowsDynamicIsland";

    private readonly string _settingsPath;
    private AppSettingsData _data = new();

    public event Action? SettingsChanged;

    public AppSettingsData Data => _data;

    public bool StartWithWindows
    {
        get => _data.StartWithWindows;
        set
        {
            if (_data.StartWithWindows == value) return;
            _data.StartWithWindows = value;
            ApplyStartWithWindowsRegistry(value);
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int NotchSizePercent
    {
        get => _data.NotchSizePercent;
        set
        {
            if (_data.NotchSizePercent == value) return;
            _data.NotchSizePercent = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double EffectiveNotchScale => _data.NotchSizePercent switch
    {
        100 => 1.0,
        115 => 1.15,
        130 => 1.30,
        150 => 1.50,
        _ => 1.0 // Auto (0)
    };

    public bool MinimalNotchEnabled
    {
        get => _data.MinimalNotchEnabled;
        set
        {
            if (_data.MinimalNotchEnabled == value) return;
            _data.MinimalNotchEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool AutoTuckEnabled
    {
        get => _data.AutoTuckEnabled;
        set
        {
            if (_data.AutoTuckEnabled == value) return;
            _data.AutoTuckEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int IdleCycleSeconds
    {
        get => Math.Clamp(_data.IdleCycleSeconds, 3, 60);
        set
        {
            int clamped = Math.Clamp(value, 3, 60);
            if (_data.IdleCycleSeconds == clamped) return;
            _data.IdleCycleSeconds = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool AutoHideFullScreen
    {
        get => _data.AutoHideFullScreen;
        set
        {
            if (_data.AutoHideFullScreen == value) return;
            _data.AutoHideFullScreen = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ScreenshotAutoTuckEnabled
    {
        get => _data.ScreenshotAutoTuckEnabled;
        set
        {
            if (_data.ScreenshotAutoTuckEnabled == value) return;
            _data.ScreenshotAutoTuckEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ExcludeFromScreenCapture
    {
        get => _data.ExcludeFromScreenCapture;
        set
        {
            if (_data.ExcludeFromScreenCapture == value) return;
            _data.ExcludeFromScreenCapture = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    private int EnabledTabCount()
    {
        int count = 0;
        if (_data.ShowTabMedia) count++;
        if (_data.ShowTabTodo) count++;
        if (_data.ShowTabPomodoro) count++;
        if (_data.ShowTabShortcuts) count++;
        if (_data.ShowTabPerformance) count++;
        if (_data.ShowTabShelf) count++;
        return count;
    }

    public bool ShowTabMedia
    {
        get => _data.ShowTabMedia;
        set
        {
            if (_data.ShowTabMedia == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabMedia = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTabTodo
    {
        get => _data.ShowTabTodo;
        set
        {
            if (_data.ShowTabTodo == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabTodo = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTabPomodoro
    {
        get => _data.ShowTabPomodoro;
        set
        {
            if (_data.ShowTabPomodoro == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabPomodoro = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTabShortcuts
    {
        get => _data.ShowTabShortcuts;
        set
        {
            if (_data.ShowTabShortcuts == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabShortcuts = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTabPerformance
    {
        get => _data.ShowTabPerformance;
        set
        {
            if (_data.ShowTabPerformance == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabPerformance = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTabShelf
    {
        get => _data.ShowTabShelf;
        set
        {
            if (_data.ShowTabShelf == value) return;
            if (!value && EnabledTabCount() <= 1) return;
            _data.ShowTabShelf = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int TopOffsetPx
    {
        get => Math.Clamp(_data.TopOffsetPx, 0, 24);
        set
        {
            int clamped = Math.Clamp(value, 0, 24);
            if (_data.TopOffsetPx == clamped) return;
            _data.TopOffsetPx = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int TopEdgeHoverSensitivityMs
    {
        get => _data.TopEdgeHoverSensitivityMs;
        set
        {
            if (_data.TopEdgeHoverSensitivityMs == value) return;
            _data.TopEdgeHoverSensitivityMs = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public string IslandPlacementStyle
    {
        get => string.IsNullOrWhiteSpace(_data.IslandPlacementStyle) ? "Notch" : _data.IslandPlacementStyle;
        set
        {
            string normalized = value == "PunchHole" ? "PunchHole" : "Notch";
            if (_data.IslandPlacementStyle == normalized) return;
            _data.IslandPlacementStyle = normalized;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public string IslandSurfaceTheme
    {
        get => string.IsNullOrWhiteSpace(_data.IslandSurfaceTheme) ? "OledBlack" : _data.IslandSurfaceTheme;
        set
        {
            if (_data.IslandSurfaceTheme == value) return;
            _data.IslandSurfaceTheme = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public string AccentTheme
    {
        get => _data.AccentTheme;
        set
        {
            if (_data.AccentTheme == value) return;
            _data.AccentTheme = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public string NotchCornerStyle
    {
        get => string.IsNullOrWhiteSpace(_data.NotchCornerStyle) ? "Squircle" : _data.NotchCornerStyle;
        set
        {
            if (_data.NotchCornerStyle == value) return;
            _data.NotchCornerStyle = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public string RimGlowStyle
    {
        get => string.IsNullOrWhiteSpace(_data.RimGlowStyle) ? "Subtle" : _data.RimGlowStyle;
        set
        {
            if (_data.RimGlowStyle == value) return;
            _data.RimGlowStyle = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double NotchBackgroundOpacity
    {
        get => Math.Clamp(_data.NotchBackgroundOpacity, 0.75, 1.0);
        set
        {
            double clamped = Math.Clamp(value, 0.75, 1.0);
            if (Math.Abs(_data.NotchBackgroundOpacity - clamped) < 0.005) return;
            _data.NotchBackgroundOpacity = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double GlowIntensity
    {
        get => Math.Clamp(_data.GlowIntensity, 0.0, 2.0);
        set
        {
            double clamped = Math.Clamp(value, 0.0, 2.0);
            if (Math.Abs(_data.GlowIntensity - clamped) < 0.01) return;
            _data.GlowIntensity = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowContourRim
    {
        get => _data.ShowContourRim;
        set
        {
            if (_data.ShowContourRim == value) return;
            _data.ShowContourRim = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool LyricsEnabled
    {
        get => _data.LyricsEnabled;
        set
        {
            if (_data.LyricsEnabled == value) return;
            _data.LyricsEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool SlowLyricPanEnabled
    {
        get => _data.SlowLyricPanEnabled;
        set
        {
            if (_data.SlowLyricPanEnabled == value) return;
            _data.SlowLyricPanEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool MediaTrackChangeToastEnabled
    {
        get => _data.MediaTrackChangeToastEnabled;
        set
        {
            if (_data.MediaTrackChangeToastEnabled == value) return;
            _data.MediaTrackChangeToastEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool AudioVisualizerEnabled
    {
        get => _data.AudioVisualizerEnabled;
        set
        {
            if (_data.AudioVisualizerEnabled == value) return;
            _data.AudioVisualizerEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowTodoBadgeOnIdle
    {
        get => _data.ShowTodoBadgeOnIdle;
        set
        {
            if (_data.ShowTodoBadgeOnIdle == value) return;
            _data.ShowTodoBadgeOnIdle = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowDateOnIdle
    {
        get => _data.ShowDateOnIdle;
        set
        {
            if (_data.ShowDateOnIdle == value) return;
            _data.ShowDateOnIdle = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool Use24HourClock
    {
        get => _data.Use24HourClock;
        set
        {
            if (_data.Use24HourClock == value) return;
            _data.Use24HourClock = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowSecondsOnClock
    {
        get => _data.ShowSecondsOnClock;
        set
        {
            if (_data.ShowSecondsOnClock == value) return;
            _data.ShowSecondsOnClock = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int DefaultFocusMinutes
    {
        get => Math.Clamp(_data.DefaultFocusMinutes, 5, 120);
        set
        {
            int clamped = Math.Clamp(value, 5, 120);
            if (_data.DefaultFocusMinutes == clamped) return;
            _data.DefaultFocusMinutes = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public int DefaultBreakMinutes
    {
        get => Math.Clamp(_data.DefaultBreakMinutes, 1, 60);
        set
        {
            int clamped = Math.Clamp(value, 1, 60);
            if (_data.DefaultBreakMinutes == clamped) return;
            _data.DefaultBreakMinutes = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool ShowPomodoroOnIdle
    {
        get => _data.ShowPomodoroOnIdle;
        set
        {
            if (_data.ShowPomodoroOnIdle == value) return;
            _data.ShowPomodoroOnIdle = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double WaterDailyTargetLiters
    {
        get => Math.Clamp(_data.WaterDailyTargetLiters, 0.5, 5.0);
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0.5, 5.0), 2);
            if (Math.Abs(_data.WaterDailyTargetLiters - clamped) < 0.01) return;
            _data.WaterDailyTargetLiters = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double WaterCupSizeLiters
    {
        get => Math.Clamp(_data.WaterCupSizeLiters, 0.05, 1.0);
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0.05, 1.0), 2);
            if (Math.Abs(_data.WaterCupSizeLiters - clamped) < 0.01) return;
            _data.WaterCupSizeLiters = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool AutoOpenShelfOnDrag
    {
        get => _data.AutoOpenShelfOnDrag;
        set
        {
            if (_data.AutoOpenShelfOnDrag == value) return;
            _data.AutoOpenShelfOnDrag = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool VolumeBrightnessHudEnabled
    {
        get => _data.VolumeBrightnessHudEnabled;
        set
        {
            if (_data.VolumeBrightnessHudEnabled == value) return;
            _data.VolumeBrightnessHudEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool BatteryToastEnabled
    {
        get => _data.BatteryToastEnabled;
        set
        {
            if (_data.BatteryToastEnabled == value) return;
            _data.BatteryToastEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool BluetoothToastEnabled
    {
        get => _data.BluetoothToastEnabled;
        set
        {
            if (_data.BluetoothToastEnabled == value) return;
            _data.BluetoothToastEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public bool SystemNotificationToastEnabled
    {
        get => _data.SystemNotificationToastEnabled;
        set
        {
            if (_data.SystemNotificationToastEnabled == value) return;
            _data.SystemNotificationToastEnabled = value;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public double ToastDurationSeconds
    {
        get => Math.Clamp(_data.ToastDurationSeconds, 1.5, 8.0);
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 1.5, 8.0), 1);
            if (Math.Abs(_data.ToastDurationSeconds - clamped) < 0.05) return;
            _data.ToastDurationSeconds = clamped;
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public AppSettingsService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(localAppData, "WindowsDynamicIsland");
        Directory.CreateDirectory(folder);
        _settingsPath = Path.Combine(folder, "settings.json");

        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (loaded != null)
                {
                    _data = loaded;
                }
            }
            else
            {
                Save();
            }

            // Synchronize startup state with Windows Registry
            ApplyStartWithWindowsRegistry(_data.StartWithWindows);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppSettingsService", "Failed to load settings.json", ex);
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppSettingsService", "Failed to save settings.json", ex);
        }
    }

    private static void ApplyStartWithWindowsRegistry(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string installedExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "WindowsDynamicIsland",
                    "WindowsDynamicIsland.exe");

                string exePath = File.Exists(installedExe)
                    ? installedExe
                    : (Environment.ProcessPath ?? installedExe);

                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppRegistryValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(AppRegistryValueName) != null)
                {
                    key.DeleteValue(AppRegistryValueName, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppSettingsService", "Failed to update Windows startup registry", ex);
        }
    }
}
