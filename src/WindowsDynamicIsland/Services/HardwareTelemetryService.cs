using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Devices;
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
    public bool IsNetworkConnected => !string.IsNullOrWhiteSpace(NetworkName) && NetworkName != "Tidak Ada Jaringan" && NetworkName != "Terputus" && NetworkName != "Disconnected";
    public string ConnectedBluetoothName { get; init; } = string.Empty;
    public bool IsBluetoothRadioOn { get; init; } = true;
    public double AppMemoryMb { get; init; } = 24.0;
}

public sealed class HardwareTelemetryService : IDisposable
{
    private readonly Dictionary<string, string> _connectedBtDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _btBaselineCaptured;
    private bool _isScanningBt;
    private string _lastAudioRenderDeviceId = string.Empty;
    private string _lastAudioRenderDeviceName = string.Empty;

    private bool _initialized;
    private bool _lastCapsLock;
    private bool _lastNumLock;
    private bool _lastCharging;
    private bool _netBaselineCaptured;
    private bool _lastNetAvailable;
    private string _lastNetName = string.Empty;
    private DateTime _lastSlowPollUtc = DateTime.MinValue;
    private DateTime _lastBtPollUtc = DateTime.MinValue;
    private bool _cachedCamActive;
    private bool _cachedMicActive;
    private string _cachedMicApp = string.Empty;
    private string _cachedNetworkName = "Terhubung";
    private bool _isBtRadioOn = true;

    public HardwareStatusSnapshot Current { get; private set; } = new();
    public IReadOnlyList<string> ConnectedBluetoothDevices => _connectedBtDevices.Values.ToList();

    public event Action<ToastPayload>? HardwareToastTriggered;

    public HardwareTelemetryService()
    {
        InitializeAudioAndBluetoothListeners();

        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => CheckNetworkChange(emitToasts: true);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => CheckNetworkChange(emitToasts: true);
        }
        catch { }
    }

    private void InitializeAudioAndBluetoothListeners()
    {
        try
        {
            // Listen for instant audio output switching (e.g. when TWS / Bluetooth headphones connect)
            MediaDevice.DefaultAudioRenderDeviceChanged += OnDefaultAudioRenderDeviceChanged;
            _ = CaptureInitialAudioDeviceAsync();
            _ = PollConnectedBluetoothDevicesAsync(emitToasts: false);
        }
        catch
        {
            // Ignore if MediaDevice WinRT API is unavailable
        }
    }

    private async Task CaptureInitialAudioDeviceAsync()
    {
        try
        {
            string? id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (!string.IsNullOrWhiteSpace(id))
            {
                _lastAudioRenderDeviceId = id;
                var info = await DeviceInformation.CreateFromIdAsync(id);
                _lastAudioRenderDeviceName = info?.Name ?? string.Empty;
            }
        }
        catch { }
    }

    private async void OnDefaultAudioRenderDeviceChanged(object? sender, DefaultAudioRenderDeviceChangedEventArgs args)
    {
        try
        {
            if (args.Role != AudioDeviceRole.Default || string.IsNullOrWhiteSpace(args.Id))
                return;

            if (string.Equals(args.Id, _lastAudioRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                return;

            _lastAudioRenderDeviceId = args.Id;
            var info = await DeviceInformation.CreateFromIdAsync(args.Id);
            string newName = info?.Name?.Trim() ?? "Perangkat Audio";

            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, _lastAudioRenderDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                _lastAudioRenderDeviceName = newName;
                HardwareToastTriggered?.Invoke(new ToastPayload(
                    Icon: "HEADPHONES",
                    Title: "Output Audio Terhubung",
                    Subtitle: newName,
                    AccentHex: "#38BDF8",
                    Duration: TimeSpan.FromSeconds(3.2)));
            }
        }
        catch { }
    }

    /// <summary>
    /// Fast, non-blocking poll of currently connected Classic Bluetooth & BLE devices.
    /// Queries only connected endpoints from OS cache (< 5ms) so connecting/disconnecting any BT device is caught reliably.
    /// </summary>
    private async Task PollConnectedBluetoothDevicesAsync(bool emitToasts)
    {
        if (_isScanningBt) return;
        _isScanningBt = true;

        try
        {
            var currentConnected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Classic Bluetooth connected devices (Headphones, Speakers, Controllers, Mice)
            string classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var classicDevices = await DeviceInformation.FindAllAsync(classicSelector);
            foreach (var dev in classicDevices)
            {
                string name = string.IsNullOrWhiteSpace(dev.Name) ? "Perangkat Bluetooth" : dev.Name.Trim();
                currentConnected[dev.Id] = name;
            }

            // 2. Bluetooth LE connected devices (Modern TWS, BLE Mice/Keyboards)
            string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var bleDevices = await DeviceInformation.FindAllAsync(bleSelector);
            foreach (var dev in bleDevices)
            {
                string name = string.IsNullOrWhiteSpace(dev.Name) ? "Perangkat BLE" : dev.Name.Trim();
                // Avoid duplicate toast if the same device is already listed by name
                if (!currentConnected.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                {
                    currentConnected[dev.Id] = name;
                }
            }

            if (_btBaselineCaptured && emitToasts)
            {
                // Check newly connected devices
                foreach (var kvp in currentConnected)
                {
                    if (!_connectedBtDevices.ContainsKey(kvp.Key) &&
                        !_connectedBtDevices.Values.Any(v => string.Equals(v, kvp.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "BLUETOOTH",
                            Title: "Bluetooth Terhubung",
                            Subtitle: kvp.Value,
                            AccentHex: "#38BDF8",
                            Duration: TimeSpan.FromSeconds(3.2)));
                    }
                }

                // Check disconnected devices
                foreach (var oldKvp in _connectedBtDevices)
                {
                    if (!currentConnected.ContainsKey(oldKvp.Key) &&
                        !currentConnected.Values.Any(v => string.Equals(v, oldKvp.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "BLUETOOTH_OFF",
                            Title: "Bluetooth Terputus",
                            Subtitle: oldKvp.Value,
                            AccentHex: "#94A3B8",
                            Duration: TimeSpan.FromSeconds(2.6)));
                    }
                }
            }

            _connectedBtDevices.Clear();
            foreach (var kvp in currentConnected)
            {
                _connectedBtDevices[kvp.Key] = kvp.Value;
            }
            _btBaselineCaptured = true;

            try
            {
                var radios = await Radio.GetRadiosAsync();
                var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (btRadio != null)
                {
                    _isBtRadioOn = btRadio.State == RadioState.On;
                }
            }
            catch { }
        }
        catch
        {
            // Ignore if Bluetooth radio is turned off
        }
        finally
        {
            _isScanningBt = false;
        }
    }

    public async Task<bool> SetBluetoothRadioStateAsync(bool turnOn)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (btRadio != null)
                {
                    var result = await btRadio.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
                    if (result == RadioAccessStatus.Allowed)
                    {
                        _isBtRadioOn = turnOn;
                        if (!turnOn)
                        {
                            _connectedBtDevices.Clear();
                        }
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: turnOn ? "BLUETOOTH" : "BLUETOOTH_OFF",
                            Title: turnOn ? "Bluetooth: ON (Aktif)" : "Bluetooth: OFF (Nonaktif)",
                            Subtitle: turnOn ? "Siap menghubungkan perangkat" : "Radio Bluetooth dimatikan",
                            AccentHex: turnOn ? "#38BDF8" : "#94A3B8",
                            Duration: TimeSpan.FromSeconds(2.5)));
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BluetoothControl", "Failed to toggle Bluetooth radio directly", ex);
        }

        OpenWindowsBluetoothSettings();
        return false;
    }

    public Task<bool> ToggleBluetoothRadioAsync() => SetBluetoothRadioStateAsync(!_isBtRadioOn);

    public static void OpenWindowsBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void PollFastKeyboardIndicators()
    {
        bool caps = Console.CapsLock;
        bool num = Console.NumberLock;

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
                Icon: caps ? "CAPS_ON" : "CAPS_OFF",
                Title: caps ? "CAPS LOCK: ON" : "CAPS LOCK: OFF",
                Subtitle: caps ? "Huruf kapital aktif" : "Huruf standar aktif",
                AccentHex: caps ? "#F59E0B" : "#94A3B8",
                Duration: TimeSpan.FromSeconds(1.8)));
        }

        if (num != _lastNumLock)
        {
            _lastNumLock = num;
            HardwareToastTriggered?.Invoke(new ToastPayload(
                Icon: num ? "NUM_ON" : "NUM_OFF",
                Title: num ? "NUM LOCK: ON" : "NUM LOCK: OFF",
                Subtitle: num ? "Numpad numerik aktif" : "Numpad navigasi",
                AccentHex: num ? "#38BDF8" : "#94A3B8",
                Duration: TimeSpan.FromSeconds(1.8)));
        }

        // Fast 100ms check for charger plug/unplug so Battery Toast responds immediately
        if (Win32Interop.GetSystemPowerStatus(out var fastPower))
        {
            bool hasBat = (fastPower.BatteryFlag & 128) == 0 && fastPower.BatteryFlag != 255;
            bool chargingNow = fastPower.ACLineStatus == 1;
            int batPct = fastPower.BatteryLifePercent <= 100 ? fastPower.BatteryLifePercent : 100;

            if (hasBat && chargingNow != _lastCharging)
            {
                _lastCharging = chargingNow;
                EmitBatteryChargingToast(chargingNow, batPct);
            }
        }
    }

    private void EmitBatteryChargingToast(bool isCharging, int batteryPct)
    {
        HardwareToastTriggered?.Invoke(new ToastPayload(
            Icon: "BATTERY",
            Title: isCharging ? $"Charging • {batteryPct}%" : $"Baterai • {batteryPct}%",
            Subtitle: isCharging ? "Adaptor Daya AC Terhubung" : "Adaptor Dilepas (Mode Baterai)",
            AccentHex: isCharging ? "#22C55E" : "#F59E0B",
            ProgressRatio: batteryPct / 100.0,
            Duration: TimeSpan.FromSeconds(2.8),
            SliderKind: ToastSliderKind.Battery,
            IsBatteryCharging: isCharging));
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
            EmitBatteryChargingToast(isCharging, batteryPct);
        }
        _lastCharging = isCharging;

        // Poll network state changes & connected Bluetooth devices every 2 seconds
        if ((DateTime.UtcNow - _lastBtPollUtc).TotalSeconds >= 2.0)
        {
            _lastBtPollUtc = DateTime.UtcNow;
            CheckNetworkChange(emitToasts);
            _ = PollConnectedBluetoothDevicesAsync(emitToasts);
        }

        if ((DateTime.UtcNow - _lastSlowPollUtc).TotalSeconds >= 6.0)
        {
            _lastSlowPollUtc = DateTime.UtcNow;
            (_cachedCamActive, _) = CheckDeviceCapabilityInUse("webcam");
            (_cachedMicActive, _cachedMicApp) = CheckDeviceCapabilityInUse("microphone");
            _cachedNetworkName = GetActiveNetworkNameLightweight();
        }

        double memoryMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0) + 18.0, 1);
        string btName = _connectedBtDevices.Values.FirstOrDefault() ?? string.Empty;

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
            ConnectedBluetoothName = btName,
            IsBluetoothRadioOn = _isBtRadioOn,
            AppMemoryMb = memoryMb
        };
    }

    private void CheckNetworkChange(bool emitToasts)
    {
        try
        {
            bool isAvailable = NetworkInterface.GetIsNetworkAvailable();
            string netName = GetActiveNetworkNameLightweight();

            if (!_netBaselineCaptured)
            {
                _lastNetAvailable = isAvailable;
                _lastNetName = netName;
                _netBaselineCaptured = true;
                return;
            }

            if (isAvailable != _lastNetAvailable || !string.Equals(netName, _lastNetName, StringComparison.OrdinalIgnoreCase))
            {
                if (emitToasts)
                {
                    bool isWifi = netName.StartsWith("Wi-Fi", StringComparison.OrdinalIgnoreCase);
                    bool wasWifi = _lastNetName.StartsWith("Wi-Fi", StringComparison.OrdinalIgnoreCase);

                    if (isAvailable && isWifi)
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "WIFI",
                            Title: "Wi-Fi Terhubung",
                            Subtitle: netName,
                            AccentHex: "#38BDF8",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                    else if (!isAvailable || netName == "Offline")
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "WIFI_OFF",
                            Title: "Jaringan Terputus",
                            Subtitle: wasWifi ? "Koneksi Wi-Fi terputus" : "Tidak ada akses internet",
                            AccentHex: "#F87171",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                    else if (isAvailable)
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "WIFI",
                            Title: "Jaringan Aktif",
                            Subtitle: netName,
                            AccentHex: "#38BDF8",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                }

                _lastNetAvailable = isAvailable;
                _lastNetName = netName;
                _cachedNetworkName = netName;
            }
        }
        catch { }
    }

    private static string GetActiveNetworkNameLightweight()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile != null)
            {
                if (profile.IsWlanConnectionProfile)
                {
                    string ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(ssid) ? $"Wi-Fi: {ssid}" : $"Wi-Fi ({profile.ProfileName})";
                }
                return profile.ProfileName ?? "Online";
            }

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
            MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultAudioRenderDeviceChanged;
        }
        catch { }
    }
}
