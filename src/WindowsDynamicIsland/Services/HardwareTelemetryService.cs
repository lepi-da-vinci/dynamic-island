using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class HardwareStatusSnapshot
{
    public int BatteryPercent { get; init; } = 100;
    public bool IsCharging { get; init; } = true;
    public bool HasBattery { get; init; }
    public bool IsCapsLockOn { get; init; }
    public bool IsNumLockOn { get; init; }
    public bool IsCameraInUse { get; init; }
    public bool IsMicrophoneInUse { get; init; }
    public string ActiveMicApp { get; init; } = string.Empty;
    public string NetworkName { get; init; } = "Connected";
    public double AppMemoryMb { get; init; } = 24.0;
}

public sealed class HardwareTelemetryService
{
    private static readonly Process CurrentProc = Process.GetCurrentProcess();
    private bool _initialized;
    private bool _lastCapsLock;
    private bool _lastNumLock;
    private bool _lastCharging;
    private DateTime _lastSlowPollUtc = DateTime.MinValue;
    private bool _cachedCamActive;
    private bool _cachedMicActive;
    private string _cachedMicApp = string.Empty;
    private string _cachedNetworkName = "Terhubung";

    public HardwareStatusSnapshot Current { get; private set; } = new();

    public event Action<ToastPayload>? HardwareToastTriggered;

    public void PollFastKeyboardIndicators()
    {
        bool caps = (Win32Interop.GetKeyState(Win32Interop.VK_CAPITAL) & 0x0001) != 0;
        bool num = (Win32Interop.GetKeyState(Win32Interop.VK_NUMLOCK) & 0x0001) != 0;

        if (!_initialized)
        {
            _lastCapsLock = caps;
            _lastNumLock = num;
            RefreshFullTelemetry(emitToasts: false);
            _initialized = true;
            return;
        }

        if (caps != _lastCapsLock)
        {
            _lastCapsLock = caps;
            HardwareToastTriggered?.Invoke(new ToastPayload(
                Icon: caps ? "🔠" : "🔡",
                Title: caps ? "CAPS LOCK: ON" : "CAPS LOCK: OFF",
                Subtitle: caps ? "Huruf kapital aktif" : "Huruf standar aktif",
                AccentHex: caps ? "#F59E0B" : "#94A3B8",
                Duration: TimeSpan.FromSeconds(1.8)));
        }

        if (num != _lastNumLock)
        {
            _lastNumLock = num;
            HardwareToastTriggered?.Invoke(new ToastPayload(
                Icon: "🔢",
                Title: num ? "NUM LOCK: ON" : "NUM LOCK: OFF",
                Subtitle: num ? "Numpad numerik aktif" : "Numpad navigasi",
                AccentHex: num ? "#38BDF8" : "#94A3B8",
                Duration: TimeSpan.FromSeconds(1.8)));
        }
    }

    public void RefreshFullTelemetry(bool emitToasts = true)
    {
        int batteryPct = 100;
        bool isCharging = true;
        bool hasBattery = false;

        if (Win32Interop.GetSystemPowerStatus(out var power))
        {
            hasBattery = (power.BatteryFlag & 128) == 0 && power.BatteryFlag != 255;
            isCharging = power.ACLineStatus == 1;
            batteryPct = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : 100;
        }

        if (emitToasts && _initialized && hasBattery && isCharging != _lastCharging)
        {
            _lastCharging = isCharging;
            HardwareToastTriggered?.Invoke(new ToastPayload(
                Icon: isCharging ? "⚡" : "🔋",
                Title: isCharging ? $"Charging ({batteryPct}%)" : $"On Battery ({batteryPct}%)",
                Subtitle: isCharging ? "Daya AC terhubung" : "Menggunakan daya baterai",
                AccentHex: isCharging ? "#22C55E" : "#F59E0B",
                ProgressRatio: batteryPct / 100.0,
                Duration: TimeSpan.FromSeconds(2.6)));
        }
        _lastCharging = isCharging;

        // Throttle Registry & Network interface checks to every 6 seconds for zero overhead
        if ((DateTime.UtcNow - _lastSlowPollUtc).TotalSeconds >= 6.0)
        {
            _lastSlowPollUtc = DateTime.UtcNow;
            (_cachedCamActive, _) = CheckDeviceCapabilityInUse("webcam");
            (_cachedMicActive, _cachedMicApp) = CheckDeviceCapabilityInUse("microphone");
            _cachedNetworkName = GetActiveNetworkNameLightweight();
        }

        double memoryMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0) + 18.0, 1);

        Current = new HardwareStatusSnapshot
        {
            BatteryPercent = batteryPct,
            IsCharging = isCharging,
            HasBattery = hasBattery,
            IsCapsLockOn = _lastCapsLock,
            IsNumLockOn = _lastNumLock,
            IsCameraInUse = _cachedCamActive,
            IsMicrophoneInUse = _cachedMicActive,
            ActiveMicApp = _cachedMicApp,
            NetworkName = _cachedNetworkName,
            AppMemoryMb = memoryMb
        };
    }

    private static string GetActiveNetworkNameLightweight()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return "Offline";

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                {
                    return ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                        ? $"Wi-Fi ({ni.Name})"
                        : $"Online ({ni.Name})";
                }
            }
            return "Online";
        }
        catch
        {
            return "Online";
        }
    }

    private static (bool IsActive, string AppName) CheckDeviceCapabilityInUse(string capabilityName)
    {
        try
        {
            string baseKeyPath = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capabilityName}";
            using var baseKey = Registry.CurrentUser.OpenSubKey(baseKeyPath);
            if (baseKey == null) return (false, string.Empty);

            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPackaged = baseKey.OpenSubKey("NonPackaged");
                    if (nonPackaged == null) continue;
                    foreach (string appKeyName in nonPackaged.GetSubKeyNames())
                    {
                        using var appKey = nonPackaged.OpenSubKey(appKeyName);
                        if (IsStopTimeZero(appKey))
                        {
                            string friendly = appKeyName.Split('#').LastOrDefault() ?? "Desktop App";
                            return (true, friendly);
                        }
                    }
                }
                else
                {
                    using var pkgKey = baseKey.OpenSubKey(subKeyName);
                    if (IsStopTimeZero(pkgKey))
                    {
                        string friendly = subKeyName.Split('_')[0];
                        return (true, friendly);
                    }
                }
            }
        }
        catch
        {
            // Ignore registry permission errors
        }

        return (false, string.Empty);
    }

    private static bool IsStopTimeZero(RegistryKey? key)
    {
        if (key == null) return false;
        object? startObj = key.GetValue("LastUsedTimeStart");
        object? stopObj = key.GetValue("LastUsedTimeStop");
        return startObj is long start && start > 0 && stopObj is long stop && stop == 0;
    }
}
