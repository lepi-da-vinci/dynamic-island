using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    public string BatteryEstimatedTimeText { get; init; } = string.Empty;
    public bool IsCapsLockOn { get; init; }
    public bool IsNumLockOn { get; init; }
    public bool IsCameraInUse { get; init; }
    public string ActiveCamApp { get; init; } = string.Empty;
    public bool IsMicrophoneInUse { get; init; }
    public string ActiveMicApp { get; init; } = string.Empty;
    public string NetworkName { get; init; } = "Connected";
    public bool IsNetworkConnected => !string.IsNullOrWhiteSpace(NetworkName) && NetworkName != "Tidak Ada Jaringan" && NetworkName != "Terputus" && NetworkName != "Disconnected";
    public string ConnectedBluetoothName { get; init; } = string.Empty;
    public bool IsBluetoothRadioOn { get; init; } = true;
    public double AppMemoryMb { get; init; } = 24.0;

    // Real-time Performance Monitor Telemetry (CPU, GPU, RAM, Disk, Network Speed, Uptime)
    public int CpuUsagePercent { get; init; } = 8;
    public string CpuModelShort { get; init; } = "Processor";
    public int CpuCoresCount { get; init; } = Environment.ProcessorCount;

    public int GpuUsagePercent { get; init; } = 4;
    public string GpuModelShort { get; init; } = "Integrated GPU";
    public double GpuVramUsedMb { get; init; }
    public string GpuVramText { get; init; } = "0 MB";

    public int RamUsagePercent { get; init; } = 45;
    public double RamUsedGb { get; init; } = 7.2;
    public double RamTotalGb { get; init; } = 16.0;

    public int DiskUsagePercent { get; init; } = 50;
    public double DiskUsedGb { get; init; } = 120.0;
    public double DiskTotalGb { get; init; } = 256.0;
    public double DiskFreeGb { get; init; } = 136.0;

    public string NetDownloadSpeedText { get; init; } = "0 KB/s";
    public string NetUploadSpeedText { get; init; } = "0 KB/s";
    public string SystemUptimeText { get; init; } = "0j 15m";
}

public sealed class HardwareTelemetryService : IDisposable
{
    private readonly Dictionary<string, string> _connectedBtDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missingBtPollCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastBtToastTimeUtc = new(StringComparer.OrdinalIgnoreCase);
    private bool _btBaselineCaptured;
    private bool _isScanningBt;
    private string _lastAudioRenderDeviceId = string.Empty;
    private string _lastAudioRenderDeviceName = string.Empty;

    private bool _initialized;
    private bool _lastCapsLock;
    private bool _lastNumLock;
    private bool _lastCharging;
    private int _lastBatteryPercent = -1;
    private bool _hasEmitted80PercentAlertForCurrentChargeCycle;
    private bool _netBaselineCaptured;
    private bool _lastNetAvailable;
    private string _lastNetName = string.Empty;
    private DateTime _lastSlowPollUtc = DateTime.MinValue;
    private DateTime _lastBtPollUtc = DateTime.MinValue;
    private bool _cachedCamActive;
    private string _cachedCamApp = string.Empty;
    private bool _cachedMicActive;
    private string _cachedMicApp = string.Empty;
    private string _cachedNetworkName = "Terhubung";
    private bool _isBtRadioOn = true;

    // Performance telemetry state
    private ulong _prevIdleTicks;
    private ulong _prevKernelTicks;
    private ulong _prevUserTicks;
    private double _smoothedCpuPct = 8.0;
    private double _smoothedGpuPct = 4.0;
    private double _lastGpuVramMb;
    private IntPtr _pdhQuery;
    private IntPtr _pdhGpuUtilCounter;
    private IntPtr _pdhGpuVramCounter;
    private bool _pdhInitAttempted;
    private bool _pdhAvailable;
    private bool _pdhPrimed;
    private byte[] _pdhBuffer = Array.Empty<byte>();
    private string _cpuModelShort = "CPU";
    private string _gpuModelShort = "GPU";
    private long _prevRxBytes = -1;
    private long _prevTxBytes = -1;
    private long _prevNetSampleTick;
    private string _cachedNetDownText = "0 KB/s";
    private string _cachedNetUpText = "0 KB/s";
    private int _cachedDiskPct = 50;
    private double _cachedDiskUsedGb = 120;
    private double _cachedDiskTotalGb = 256;
    private double _cachedDiskFreeGb = 136;
    private DateTime _lastNetSampleUtc = DateTime.MinValue;

    public bool IsPerformanceViewActive { get; set; }
    public HardwareStatusSnapshot Current { get; private set; } = new();
    public IReadOnlyList<string> ConnectedBluetoothDevices => _connectedBtDevices.Values.ToList();

    public event Action<ToastPayload>? HardwareToastTriggered;

    public HardwareTelemetryService()
    {
        InitializeStaticHardwareNames();
        InitializeAudioAndBluetoothListeners();

        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => CheckNetworkChange(emitToasts: true);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => CheckNetworkChange(emitToasts: true);
        }
        catch { }
    }

    private void InitializeStaticHardwareNames()
    {
        try
        {
            using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\Description\System\CentralProcessor\0");
            string rawCpu = cpuKey?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(rawCpu))
            {
                rawCpu = rawCpu
                    .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("CPU", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("12th Gen ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("13th Gen ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("14th Gen ", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                while (rawCpu.Contains("  ")) rawCpu = rawCpu.Replace("  ", " ");
                _cpuModelShort = rawCpu.Length > 20 ? rawCpu[..20].Trim() : rawCpu;
            }
            else
            {
                _cpuModelShort = $"{Environment.ProcessorCount}-Core CPU";
            }
        }
        catch
        {
            _cpuModelShort = $"{Environment.ProcessorCount}-Core CPU";
        }

        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (displayClass != null)
            {
                foreach (string subName in displayClass.GetSubKeyNames())
                {
                    if (!subName.StartsWith('0')) continue;
                    using var sub = displayClass.OpenSubKey(subName);
                    string desc = sub?.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(desc) && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    {
                        desc = desc
                            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Intel(R) ", "Intel ", StringComparison.OrdinalIgnoreCase)
                            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Graphics", "", StringComparison.OrdinalIgnoreCase)
                            .Trim();
                        while (desc.Contains("  ")) desc = desc.Replace("  ", " ");
                        _gpuModelShort = desc.Length > 20 ? desc[..20].Trim() : desc;
                        break;
                    }
                }
            }
        }
        catch
        {
            _gpuModelShort = "Discrete / iGPU";
        }

        RefreshDiskStats();
    }

    private void RefreshDiskStats()
    {
        try
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(sysRoot);
            if (drive.IsReady && drive.TotalSize > 0)
            {
                _cachedDiskTotalGb = Math.Round(drive.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                _cachedDiskFreeGb = Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                _cachedDiskUsedGb = Math.Round(_cachedDiskTotalGb - _cachedDiskFreeGb, 1);
                _cachedDiskPct = (int)Math.Clamp(Math.Round((_cachedDiskUsedGb / _cachedDiskTotalGb) * 100.0), 1, 100);
            }
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
                    Title: LocalizationService.Instance.Get("Audio_SwitchedToast"),
                    Subtitle: newName,
                    AccentHex: "#38BDF8",
                    Duration: TimeSpan.FromSeconds(3.2)));
            }
        }
        catch { }
    }

    /// <summary>
    /// Fast, non-blocking poll of currently connected AND explicitly paired Bluetooth Audio & Peripheral devices.
    /// Strictly filters out unpaired devices, raw BLE GATT background pings ("Perangkat BLE"), and phone RFCOMM probes ("S24 F").
    /// </summary>
    private async Task PollConnectedBluetoothDevicesAsync(bool emitToasts)
    {
        if (_isScanningBt) return;
        _isScanningBt = true;

        try
        {
            var currentConnected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var classicDevices = await DeviceInformation.FindAllAsync(classicSelector);
            foreach (var dev in classicDevices)
            {
                string name = dev.Name?.Trim() ?? string.Empty;
                if (!IsValidPairedBluetoothName(name)) continue;

                try
                {
                    using var btDev = await BluetoothDevice.FromIdAsync(dev.Id);
                    if (btDev == null) continue;

                    // 1. Must be explicitly paired by the user on this PC
                    bool isPaired = (dev.Pairing?.IsPaired == true) ||
                                    (btDev.DeviceInformation?.Pairing?.IsPaired == true);
                    if (!isPaired) continue;

                    // 2. Must still be genuinely connected
                    if (btDev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                    // 3. Only track true user peripherals (Audio/Video headphones/TWS/speakers & Peripheral mice/keyboards/controllers).
                    // Ignore Phone (e.g. "S24 F" background Phone Link / Quick Share sockets) and Computer/Network probes.
                    var majorClass = btDev.ClassOfDevice?.MajorClass ?? BluetoothMajorClass.Miscellaneous;
                    if (majorClass is not (BluetoothMajorClass.AudioVideo or BluetoothMajorClass.Peripheral or BluetoothMajorClass.Wearable))
                    {
                        continue;
                    }

                    currentConnected[name] = name;
                }
                catch
                {
                    // Skip inaccessible or transient endpoint
                }
            }

            DateTime nowUtc = DateTime.UtcNow;

            if (_btBaselineCaptured && emitToasts)
            {
                // Check newly connected paired audio/peripheral devices
                foreach (var kvp in currentConnected)
                {
                    _missingBtPollCounts.Remove(kvp.Key);

                    if (!_connectedBtDevices.ContainsKey(kvp.Key))
                    {
                        if (!_lastBtToastTimeUtc.TryGetValue($"CONN:{kvp.Key}", out DateTime lastToast) ||
                            (nowUtc - lastToast).TotalSeconds >= 15.0)
                        {
                            _lastBtToastTimeUtc[$"CONN:{kvp.Key}"] = nowUtc;
                            HardwareToastTriggered?.Invoke(new ToastPayload(
                                Icon: "BLUETOOTH",
                                Title: LocalizationService.Instance.Get("Toast_BtConnectedTitle"),
                                Subtitle: kvp.Value,
                                AccentHex: "#38BDF8",
                                Duration: TimeSpan.FromSeconds(3.0)));
                        }
                    }
                }

                // Check disconnected paired devices (require 3 consecutive polls ~6s to ignore brief micro-drops)
                var disconnectedKeys = new List<string>();
                foreach (var oldKvp in _connectedBtDevices)
                {
                    if (!currentConnected.ContainsKey(oldKvp.Key))
                    {
                        int misses = _missingBtPollCounts.TryGetValue(oldKvp.Key, out int prevMisses) ? prevMisses + 1 : 1;
                        _missingBtPollCounts[oldKvp.Key] = misses;

                        if (misses >= 3)
                        {
                            disconnectedKeys.Add(oldKvp.Key);
                            _missingBtPollCounts.Remove(oldKvp.Key);

                            if (!_lastBtToastTimeUtc.TryGetValue($"DISC:{oldKvp.Key}", out DateTime lastToast) ||
                                (nowUtc - lastToast).TotalSeconds >= 15.0)
                            {
                                _lastBtToastTimeUtc[$"DISC:{oldKvp.Key}"] = nowUtc;
                                HardwareToastTriggered?.Invoke(new ToastPayload(
                                    Icon: "BLUETOOTH_OFF",
                                    Title: LocalizationService.Instance.Get("Toast_BtDisconnectedTitle"),
                                    Subtitle: oldKvp.Value,
                                    AccentHex: "#94A3B8",
                                    Duration: TimeSpan.FromSeconds(2.6)));
                            }
                        }
                    }
                }

                foreach (var key in currentConnected.Keys)
                {
                    _connectedBtDevices[key] = currentConnected[key];
                }
                foreach (var discKey in disconnectedKeys)
                {
                    _connectedBtDevices.Remove(discKey);
                }
            }
            else
            {
                _connectedBtDevices.Clear();
                foreach (var kvp in currentConnected)
                {
                    _connectedBtDevices[kvp.Key] = kvp.Value;
                }
                _btBaselineCaptured = true;
            }

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

    private static bool IsValidPairedBluetoothName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return false;
        string name = rawName.Trim();
        if (name.Length < 2) return false;

        if (name.Equals("Perangkat BLE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Perangkat Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Bluetooth ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public async Task<bool> SetBluetoothRadioStateAsync(bool turnOn)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (btRadio == null)
            {
                var access = await Radio.RequestAccessAsync();
                if (access == RadioAccessStatus.Allowed)
                {
                    radios = await Radio.GetRadiosAsync();
                    btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                }
            }

            if (btRadio != null)
            {
                var result = await btRadio.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
                if (result == RadioAccessStatus.Allowed)
                {
                    _isBtRadioOn = turnOn;
                    if (!turnOn)
                    {
                        _connectedBtDevices.Clear();
                        _missingBtPollCounts.Clear();
                    }
                    HardwareToastTriggered?.Invoke(new ToastPayload(
                        Icon: turnOn ? "BLUETOOTH" : "BLUETOOTH_OFF",
                        Title: turnOn ? "Bluetooth: ON (Aktif)" : "Bluetooth: OFF (Nonaktif)",
                        Subtitle: turnOn ? "Radio Bluetooth diaktifkan" : "Radio Bluetooth dimatikan",
                        AccentHex: turnOn ? "#38BDF8" : "#94A3B8",
                        Duration: TimeSpan.FromSeconds(2.5)));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BluetoothControl", "Failed to toggle Bluetooth radio directly", ex);
        }

        return false;
    }

    public async Task<bool> ToggleBluetoothRadioAsync()
    {
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

        return await SetBluetoothRadioStateAsync(!_isBtRadioOn);
    }

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
                Subtitle: LocalizationService.Instance.Get(caps ? "Toast_CapsLockSubOn" : "Toast_CapsLockSubOff"),
                AccentHex: caps ? "#F59E0B" : "#94A3B8",
                Duration: TimeSpan.FromSeconds(1.8)));
        }

        if (num != _lastNumLock)
        {
            _lastNumLock = num;
            HardwareToastTriggered?.Invoke(new ToastPayload(
                Icon: num ? "NUM_ON" : "NUM_OFF",
                Title: num ? "NUM LOCK: ON" : "NUM LOCK: OFF",
                Subtitle: LocalizationService.Instance.IsEnglish
                    ? (num ? "Numeric keypad enabled" : "Navigation keypad active")
                    : (num ? "Numpad numerik aktif" : "Numpad navigasi"),
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
        string accentHex = isCharging
            ? "#22C55E"
            : batteryPct <= 20
                ? "#EF4444"
                : batteryPct <= 65
                    ? "#F97316"
                    : "#22C55E";

        HardwareToastTriggered?.Invoke(new ToastPayload(
            Icon: "BATTERY",
            Title: isCharging
                ? LocalizationService.Instance.Format("Toast_ChargerPluggedTitle", batteryPct)
                : LocalizationService.Instance.Format("Toast_ChargerUnpluggedTitle", batteryPct),
            Subtitle: isCharging
                ? LocalizationService.Instance.Get("Toast_ChargerPluggedSub")
                : LocalizationService.Instance.Get("Toast_ChargerUnpluggedSub"),
            AccentHex: accentHex,
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

        string batteryEstimateText = hasBattery
            ? (isCharging ? LocalizationService.Instance.Get("Status_Charging") : LocalizationService.Instance.Get("Status_Battery"))
            : LocalizationService.Instance.Get("Status_DesktopAc");
        if (hasBattery)
        {
            if (isCharging)
            {
                if (batteryPct < 75)
                {
                    _hasEmitted80PercentAlertForCurrentChargeCycle = false;
                }
                else if (batteryPct >= 80 && _lastBatteryPercent > 0 && _lastBatteryPercent < 80 && !_hasEmitted80PercentAlertForCurrentChargeCycle)
                {
                    _hasEmitted80PercentAlertForCurrentChargeCycle = true;
                    if (emitToasts && _initialized)
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "BATTERY_HEALTH",
                            Title: LocalizationService.Instance.Get("Toast_Battery80Title"),
                            Subtitle: LocalizationService.Instance.Get("Toast_Battery80Sub"),
                            AccentHex: "#22C55E",
                            Duration: TimeSpan.FromSeconds(4.5)));
                    }
                }
            }
            else
            {
                _hasEmitted80PercentAlertForCurrentChargeCycle = false;
                if (power.BatteryLifeTime > 0 && power.BatteryLifeTime != -1)
                {
                    int hours = power.BatteryLifeTime / 3600;
                    int mins = (power.BatteryLifeTime % 3600) / 60;
                    batteryEstimateText = LocalizationService.Instance.IsEnglish
                        ? (hours > 0 ? $"{hours}h {mins}m left" : $"{mins}m left")
                        : (hours > 0 ? $"{hours}j {mins}m tersisa" : $"{mins}m tersisa");
                }
                else
                {
                    batteryEstimateText = LocalizationService.Instance.Get("Status_Battery");
                }
            }
        }
        _lastBatteryPercent = batteryPct;

        if (emitToasts && _initialized && hasBattery && isCharging != _lastCharging)
        {
            _lastCharging = isCharging;
            EmitBatteryChargingToast(isCharging, batteryPct);
        }
        _lastCharging = isCharging;

        // Poll network state changes & connected Bluetooth devices (audio switching is already instant via WinRT event)
        double btPollInterval = IsPerformanceViewActive ? 4.0 : 8.0;
        if ((DateTime.UtcNow - _lastBtPollUtc).TotalSeconds >= btPollInterval)
        {
            _lastBtPollUtc = DateTime.UtcNow;
            CheckNetworkChange(emitToasts);
            _ = PollConnectedBluetoothDevicesAsync(emitToasts);
        }

        if ((DateTime.UtcNow - _lastSlowPollUtc).TotalSeconds >= 6.0)
        {
            _lastSlowPollUtc = DateTime.UtcNow;
            (_cachedCamActive, _cachedCamApp) = CheckDeviceCapabilityInUse("webcam");
            (_cachedMicActive, _cachedMicApp) = CheckDeviceCapabilityInUse("microphone");
            _cachedNetworkName = GetActiveNetworkNameLightweight();
            RefreshDiskStats();
        }

        // 1. Real-time System CPU Usage via Win32 GetSystemTimes (0ms overhead)
        if (Win32Interop.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idleNow = idleFt.ToUInt64();
            ulong kernelNow = kernelFt.ToUInt64();
            ulong userNow = userFt.ToUInt64();

            if (_prevKernelTicks > 0 || _prevUserTicks > 0)
            {
                ulong idleDelta = idleNow - _prevIdleTicks;
                ulong kernelDelta = kernelNow - _prevKernelTicks;
                ulong userDelta = userNow - _prevUserTicks;
                ulong totalSys = kernelDelta + userDelta;
                if (totalSys > 0 && totalSys >= idleDelta)
                {
                    double rawCpu = ((double)(totalSys - idleDelta) / totalSys) * 100.0;
                    _smoothedCpuPct = (_smoothedCpuPct * 0.45) + (Math.Clamp(rawCpu, 1.0, 100.0) * 0.55);
                }
            }

            _prevIdleTicks = idleNow;
            _prevKernelTicks = kernelNow;
            _prevUserTicks = userNow;
        }

        // 2. Real-time System RAM Usage via Win32 GlobalMemoryStatusEx (0ms overhead)
        int ramPct = 45;
        double ramUsedGb = 7.2;
        double ramTotalGb = 16.0;
        var memStatus = new Win32Interop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>()
        };
        if (Win32Interop.GlobalMemoryStatusEx(ref memStatus) && memStatus.ullTotalPhys > 0)
        {
            ramPct = (int)Math.Clamp(memStatus.dwMemoryLoad, 1, 100);
            ramTotalGb = Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
            double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            ramUsedGb = Math.Round(Math.Max(0.1, ramTotalGb - availGb), 1);
        }

        // 3. Real GPU Engine Utilization & Dedicated VRAM via native Win32 PDH
        if (TrySampleGpuPdh(out double gpuPct, out double vramMb))
        {
            _smoothedGpuPct = (_smoothedGpuPct * 0.45) + (gpuPct * 0.55);
            _lastGpuVramMb = vramMb;
        }
        else
        {
            double targetGpu = Math.Clamp((_smoothedCpuPct * 0.25) + 1.0, 1.0, 99.0);
            _smoothedGpuPct = (_smoothedGpuPct * 0.6) + (targetGpu * 0.4);
        }

        // 4. Real-time Network Download & Upload Speed
        if (IsPerformanceViewActive || (DateTime.UtcNow - _lastNetSampleUtc).TotalSeconds >= 10.0)
        {
            _lastNetSampleUtc = DateTime.UtcNow;
            SampleNetworkSpeed();
        }

        // 5. System Uptime
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string uptimeStr = uptime.TotalDays >= 1
            ? (LocalizationService.Instance.IsEnglish
                ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalDays}h {uptime.Hours}j {uptime.Minutes}m")
            : LocalizationService.Instance.Format("Perf_UptimeFmt", uptime.Hours, uptime.Minutes);

        string vramText = _lastGpuVramMb >= 1024
            ? $"{_lastGpuVramMb / 1024.0:F1} GB VRAM"
            : _lastGpuVramMb > 0 ? $"{_lastGpuVramMb:F0} MB VRAM" : "0 MB";

        double memoryMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0) + 18.0, 1);
        string btName = _connectedBtDevices.Values.FirstOrDefault() ?? string.Empty;

        Current = new HardwareStatusSnapshot
        {
            BatteryPercent = batteryPct,
            IsCharging = isCharging,
            HasBattery = hasBattery,
            BatteryEstimatedTimeText = batteryEstimateText,
            IsCapsLockOn = _lastCapsLock,
            IsNumLockOn = _lastNumLock,
            IsCameraInUse = _cachedCamActive,
            ActiveCamApp = _cachedCamApp,
            IsMicrophoneInUse = _cachedMicActive,
            ActiveMicApp = _cachedMicApp,
            NetworkName = _cachedNetworkName,
            ConnectedBluetoothName = btName,
            IsBluetoothRadioOn = _isBtRadioOn,
            AppMemoryMb = memoryMb,
            CpuUsagePercent = (int)Math.Round(_smoothedCpuPct),
            CpuModelShort = _cpuModelShort,
            CpuCoresCount = Environment.ProcessorCount,
            GpuUsagePercent = (int)Math.Round(_smoothedGpuPct),
            GpuModelShort = _gpuModelShort,
            GpuVramUsedMb = Math.Round(_lastGpuVramMb, 0),
            GpuVramText = vramText,
            RamUsagePercent = ramPct,
            RamUsedGb = ramUsedGb,
            RamTotalGb = ramTotalGb,
            DiskUsagePercent = _cachedDiskPct,
            DiskUsedGb = _cachedDiskUsedGb,
            DiskTotalGb = _cachedDiskTotalGb,
            DiskFreeGb = _cachedDiskFreeGb,
            NetDownloadSpeedText = _cachedNetDownText,
            NetUploadSpeedText = _cachedNetUpText,
            SystemUptimeText = uptimeStr
        };
    }

    private void SampleNetworkSpeed()
    {
        try
        {
            long totalRx = 0;
            long totalTx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var stats = ni.GetIPv4Statistics();
                totalRx += stats.BytesReceived;
                totalTx += stats.BytesSent;
            }

            long nowTick = Environment.TickCount64;
            if (_prevRxBytes >= 0 && _prevNetSampleTick > 0 && nowTick > _prevNetSampleTick)
            {
                double elapsedSec = (nowTick - _prevNetSampleTick) / 1000.0;
                if (elapsedSec >= 0.2)
                {
                    double rxPerSec = Math.Max(0, (totalRx - _prevRxBytes) / elapsedSec);
                    double txPerSec = Math.Max(0, (totalTx - _prevTxBytes) / elapsedSec);
                    _cachedNetDownText = FormatByteRate(rxPerSec);
                    _cachedNetUpText = FormatByteRate(txPerSec);
                }
            }

            _prevRxBytes = totalRx;
            _prevTxBytes = totalTx;
            _prevNetSampleTick = nowTick;
        }
        catch { }
    }

    private static string FormatByteRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024.0 * 1024.0)
            return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
        if (bytesPerSec >= 1024.0)
            return $"{bytesPerSec / 1024.0:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
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
                            Title: LocalizationService.Instance.Get("Toast_WifiConnectedTitle"),
                            Subtitle: netName,
                            AccentHex: "#38BDF8",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                    else if (!isAvailable || netName == "Offline")
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "WIFI_OFF",
                            Title: LocalizationService.Instance.Get("Toast_WifiDisconnectedTitle"),
                            Subtitle: LocalizationService.Instance.Get("Toast_WifiDisconnectedSub"),
                            AccentHex: "#F87171",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                    else if (isAvailable)
                    {
                        HardwareToastTriggered?.Invoke(new ToastPayload(
                            Icon: "WIFI",
                            Title: LocalizationService.Instance.Get("Status_Connected"),
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

    private bool TrySampleGpuPdh(out double gpuPercent, out double dedicatedVramMb)
    {
        gpuPercent = 0;
        dedicatedVramMb = _lastGpuVramMb;

        if (!_pdhInitAttempted)
        {
            _pdhInitAttempted = true;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) == 0 &&
                    PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _pdhGpuUtilCounter) == 0)
                {
                    PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _pdhGpuVramCounter);
                    PdhCollectQueryData(_pdhQuery);
                    _pdhAvailable = true;
                    _pdhPrimed = false;
                }
            }
            catch
            {
                _pdhAvailable = false;
            }
        }

        if (!_pdhAvailable || _pdhQuery == IntPtr.Zero) return false;

        try
        {
            if (PdhCollectQueryData(_pdhQuery) != 0) return false;
            if (!_pdhPrimed)
            {
                _pdhPrimed = true;
                return false;
            }

            // Sum per (luid + engtype) so overall GPU utilization matches Windows Task Manager
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, val) in ReadPdhCounterArray(_pdhGpuUtilCounter))
            {
                if (val <= 0 || double.IsNaN(val) || double.IsInfinity(val)) continue;
                int engIdx = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                int luidIdx = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                string groupKey = engIdx >= 0
                    ? (luidIdx >= 0 ? $"{name[luidIdx..Math.Min(name.Length, luidIdx + 24)]}|{name[engIdx..]}" : name[engIdx..])
                    : "default";

                engineTotals.TryGetValue(groupKey, out double acc);
                engineTotals[groupKey] = acc + val;
            }

            double peakEngine = 0;
            foreach (double engineSum in engineTotals.Values)
            {
                if (engineSum > peakEngine) peakEngine = engineSum;
            }
            gpuPercent = Math.Clamp(peakEngine, 0.0, 100.0);

            if (_pdhGpuVramCounter != IntPtr.Zero)
            {
                double maxVramBytes = 0;
                foreach (var (_, val) in ReadPdhCounterArray(_pdhGpuVramCounter))
                {
                    if (val > maxVramBytes && !double.IsNaN(val) && !double.IsInfinity(val))
                    {
                        maxVramBytes = val;
                    }
                }
                if (maxVramBytes > 0)
                {
                    dedicatedVramMb = maxVramBytes / (1024.0 * 1024.0);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<(string Name, double Value)> ReadPdhCounterArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return Array.Empty<(string, double)>();

        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bufferSize == 0 || itemCount == 0)
        {
            return Array.Empty<(string, double)>();
        }

        if (_pdhBuffer.Length < bufferSize)
        {
            _pdhBuffer = new byte[bufferSize + 4096];
        }

        var results = new List<(string, double)>((int)itemCount);
        GCHandle handle = GCHandle.Alloc(_pdhBuffer, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = handle.AddrOfPinnedObject();
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, ptr);
            if (status != 0) return results;

            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            for (int i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(IntPtr.Add(ptr, i * stride));
                if (item.FmtValue.CStatus == 0 && item.szName != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                    results.Add((name, item.FmtValue.doubleValue));
                }
            }
        }
        finally
        {
            handle.Free();
        }

        return results;
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_DOUBLE
    {
        public uint CStatus;
        public int Padding;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE_DOUBLE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr ItemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);

    public void Dispose()
    {
        try
        {
            MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultAudioRenderDeviceChanged;
        }
        catch { }

        if (_pdhQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_pdhQuery); } catch { }
            _pdhQuery = IntPtr.Zero;
        }
    }
}
