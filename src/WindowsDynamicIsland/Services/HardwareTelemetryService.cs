using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
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
    public string ConnectedBluetoothName { get; init; } = string.Empty;
    public double AppMemoryMb { get; init; } = 24.0;
}

public sealed class HardwareTelemetryService : IDisposable
{
    private const string BluetoothConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string BluetoothAqsFilter =
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

    private readonly ConcurrentDictionary<string, bool> _btConnectedStates = new();
    private readonly ConcurrentDictionary<string, string> _btDeviceNames = new();
    private DeviceWatcher? _bluetoothWatcher;
    private volatile bool _btInitialEnumerationDone;

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

    public HardwareTelemetryService()
    {
        StartBluetoothWatcher();
    }

    private void StartBluetoothWatcher()
    {
        try
        {
            string[] requestedProps = [BluetoothConnectedProperty];
            _bluetoothWatcher = DeviceInformation.CreateWatcher(
                BluetoothAqsFilter,
                requestedProps,
                DeviceInformationKind.AssociationEndpoint);

            _bluetoothWatcher.Added += OnBluetoothDeviceAdded;
            _bluetoothWatcher.Updated += OnBluetoothDeviceUpdated;
            _bluetoothWatcher.Removed += OnBluetoothDeviceRemoved;
            _bluetoothWatcher.EnumerationCompleted += (_, _) => _btInitialEnumerationDone = true;
            _bluetoothWatcher.Start();
        }
        catch
        {
            // Ignore if Bluetooth adapter is disabled or unavailable
        }
    }

    private void OnBluetoothDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInfo)
    {
        try
        {
            string name = string.IsNullOrWhiteSpace(deviceInfo.Name) ? "Perangkat Bluetooth" : deviceInfo.Name.Trim();
            _btDeviceNames[deviceInfo.Id] = name;

            bool isConnected = false;
            if (deviceInfo.Properties.TryGetValue(BluetoothConnectedProperty, out object? val) && val is bool b)
            {
                isConnected = b;
            }

            bool hadPrevious = _btConnectedStates.TryGetValue(deviceInfo.Id, out bool prevConnected);
            _btConnectedStates[deviceInfo.Id] = isConnected;

            if (_btInitialEnumerationDone && isConnected && (!hadPrevious || !prevConnected))
            {
                EmitBluetoothToast(name, connected: true);
            }
        }
        catch { }
    }

    private void OnBluetoothDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            if (!update.Properties.TryGetValue(BluetoothConnectedProperty, out object? val) || val is not bool isConnected)
                return;

            bool hadPrevious = _btConnectedStates.TryGetValue(update.Id, out bool prevConnected);
            _btConnectedStates[update.Id] = isConnected;

            if (!_btInitialEnumerationDone) return;

            if (!hadPrevious || prevConnected != isConnected)
            {
                string name = _btDeviceNames.TryGetValue(update.Id, out string? n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : "Perangkat Bluetooth";

                EmitBluetoothToast(name, isConnected);
            }
        }
        catch { }
    }

    private void OnBluetoothDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        _btConnectedStates.TryRemove(update.Id, out _);
    }

    private void EmitBluetoothToast(string deviceName, bool connected)
    {
        HardwareToastTriggered?.Invoke(new ToastPayload(
            Icon: connected ? "🎧" : "🔌",
            Title: connected ? "Bluetooth Terhubung" : "Bluetooth Terputus",
            Subtitle: deviceName,
            AccentHex: connected ? "#38BDF8" : "#94A3B8",
            Duration: TimeSpan.FromSeconds(3.0)));
    }

    private string GetPrimaryConnectedBluetoothName()
    {
        foreach (var kvp in _btConnectedStates)
        {
            if (kvp.Value && _btDeviceNames.TryGetValue(kvp.Key, out string? name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        return string.Empty;
    }

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
            ConnectedBluetoothName = GetPrimaryConnectedBluetoothName(),
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

    public void Dispose()
    {
        try
        {
            if (_bluetoothWatcher != null &&
                _bluetoothWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _bluetoothWatcher.Stop();
            }
        }
        catch { }
    }
}
