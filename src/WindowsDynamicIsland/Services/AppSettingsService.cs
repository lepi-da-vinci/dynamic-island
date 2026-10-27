using System;
using System.IO;
using System.Text.Json;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class AppSettingsData
{
    public bool AutoTuckEnabled { get; set; } = true;
    public bool MediaTrackChangeToastEnabled { get; set; } = true;
    public int TopEdgeHoverSensitivityMs { get; set; } = 80;
}

public sealed class AppSettingsService
{
    private readonly string _storagePath;
    private AppSettingsData _data = new();

    public event Action? SettingsChanged;

    public bool AutoTuckEnabled
    {
        get => _data.AutoTuckEnabled;
        set
        {
            if (_data.AutoTuckEnabled == value) return;
            _data.AutoTuckEnabled = value;
            SaveSettings();
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
            SaveSettings();
            SettingsChanged?.Invoke();
        }
    }

    public int TopEdgeHoverSensitivityMs
    {
        get => _data.TopEdgeHoverSensitivityMs;
        set
        {
            if (_data.TopEdgeHoverSensitivityMs == value) return;
            _data.TopEdgeHoverSensitivityMs = Math.Clamp(value, 20, 500);
            SaveSettings();
            SettingsChanged?.Invoke();
        }
    }

    public AppSettingsService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(localAppData, "WindowsDynamicIsland");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "settings.json");

        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                string json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (loaded != null)
                {
                    _data = loaded;
                }
            }
            else
            {
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppSettingsService", $"Failed to load settings. Using defaults: {ex.Message}");
            _data = new AppSettingsData();
        }
    }

    private void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppSettingsService", $"Failed to save settings: {ex.Message}");
        }
    }
}
