using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed record WifiNetworkItem(
    string Ssid,
    int SignalPercent,
    bool IsSecured,
    string SecurityText,
    bool IsConnected,
    bool IsSavedProfile,
    string BandText);

public sealed record WifiSnapshot(
    bool IsRadioOn,
    string? ConnectedSsid,
    int ConnectedSignalPercent,
    string ConnectedSecurity,
    string ConnectedBand,
    string IpAddress,
    List<WifiNetworkItem> Networks);

/// <summary>
/// Manages Windows Wi-Fi radio state, active connection info, nearby network scanning,
/// profile connection/disconnection, and temporary WPA2 XML provisioning without flashing console windows.
/// </summary>
public sealed class WifiService
{
    public async Task<WifiSnapshot> GetSnapshotAsync(bool triggerScan = false)
    {
        bool radioOn = await IsWifiRadioOnAsync();
        if (!radioOn)
        {
            return new WifiSnapshot(
                IsRadioOn: false,
                ConnectedSsid: null,
                ConnectedSignalPercent: 0,
                ConnectedSecurity: string.Empty,
                ConnectedBand: string.Empty,
                IpAddress: string.Empty,
                Networks: []);
        }

        if (triggerScan)
        {
            await TryWinRtScanAsync();
        }

        var ifaceTask = RunNetshAsync("wlan show interfaces");
        var networksTask = RunNetshAsync("wlan show networks mode=bssid");
        var profilesTask = RunNetshAsync("wlan show profiles");

        await Task.WhenAll(ifaceTask, networksTask, profilesTask);

        var (connectedSsid, connectedSignal, connectedSecurity, connectedBand) = ParseNetshInterfaceOutput(await ifaceTask);
        var savedProfiles = ParseNetshProfilesOutput(await profilesTask);
        var networks = ParseNetshNetworksOutput(await networksTask, connectedSsid, savedProfiles);
        string ipAddress = GetActiveWifiIpv4Address();

        return new WifiSnapshot(
            IsRadioOn: true,
            ConnectedSsid: connectedSsid,
            ConnectedSignalPercent: connectedSignal,
            ConnectedSecurity: connectedSecurity,
            ConnectedBand: connectedBand,
            IpAddress: ipAddress,
            Networks: networks);
    }

    public async Task<bool> SetRadioAsync(bool turnOn)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifiRadio == null)
            {
                var access = await Radio.RequestAccessAsync();
                if (access == RadioAccessStatus.Allowed)
                {
                    radios = await Radio.GetRadiosAsync();
                    wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                }
            }

            if (wifiRadio != null)
            {
                var result = await wifiRadio.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
                if (result == RadioAccessStatus.Allowed)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("WifiService", $"WinRT SetRadioAsync failed: {ex.Message}");
        }

        return false;
    }

    public async Task<(bool Success, string Message)> ConnectAsync(WifiNetworkItem network, string? password = null)
    {
        var loc = LocalizationService.Instance;
        if (string.IsNullOrWhiteSpace(network.Ssid))
        {
            return (false, loc.Get("Wifi_ErrEmptySsid", "SSID tidak valid."));
        }

        // 1. Saved profile without manual password override -> direct netsh connect
        if (network.IsSavedProfile && string.IsNullOrEmpty(password))
        {
            string safeSsid = EscapeNetshArg(network.Ssid);
            string output = await RunNetshAsync($"wlan connect name=\"{safeSsid}\" ssid=\"{safeSsid}\"");
            if (IsNetshSuccess(output))
            {
                return (true, string.Empty);
            }
        }

        // 2. Open network without security -> temporary open XML profile or WinRT
        if (!network.IsSecured)
        {
            if (await TryWinRtConnectAsync(network.Ssid, null))
            {
                return (true, string.Empty);
            }

            return await ConnectViaXmlProfileAsync(network.Ssid, isOpen: true, password: null);
        }

        // 3. Secured network with explicit passphrase
        if (!string.IsNullOrEmpty(password))
        {
            if (await TryWinRtConnectAsync(network.Ssid, password))
            {
                return (true, string.Empty);
            }

            return await ConnectViaXmlProfileAsync(network.Ssid, isOpen: false, password: password);
        }

        return (false, loc.Get("Wifi_ErrNeedPassword", "Kata sandi Wi-Fi diperlukan."));
    }

    public async Task<bool> DisconnectAsync()
    {
        string output = await RunNetshAsync("wlan disconnect");
        return IsNetshSuccess(output);
    }

    public async Task<bool> ForgetProfileAsync(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return false;
        string safeSsid = EscapeNetshArg(ssid);
        string output = await RunNetshAsync($"wlan delete profile name=\"{safeSsid}\"");
        return IsNetshSuccess(output);
    }

    public static void OpenWindowsWifiSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:network-wifi",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static async Task<bool> IsWifiRadioOnAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifiRadio != null)
            {
                return wifiRadio.State == RadioState.On;
            }
        }
        catch { }

        return true;
    }

    private static async Task TryWinRtScanAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed) return;
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                await adapters[0].ScanAsync().AsTask(cts.Token);
            }
        }
        catch { }
    }

    private static async Task<bool> TryWinRtConnectAsync(string ssid, string? password)
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed) return false;
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0) return false;

            var adapter = adapters[0];
            var target = adapter.NetworkReport.AvailableNetworks
                .FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal));
            if (target == null) return false;

            WiFiConnectionResult res;
            if (!string.IsNullOrEmpty(password))
            {
                var cred = new PasswordCredential { Password = password };
                res = await adapter.ConnectAsync(target, WiFiReconnectionKind.Automatic, cred);
            }
            else
            {
                res = await adapter.ConnectAsync(target, WiFiReconnectionKind.Automatic);
            }

            return res.ConnectionStatus == WiFiConnectionStatus.Success;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("WifiService", $"WinRT ConnectAsync fallback for '{ssid}': {ex.Message}");
            return false;
        }
    }

    private static async Task<(bool Success, string Message)> ConnectViaXmlProfileAsync(string ssid, bool isOpen, string? password)
    {
        string tempXmlPath = Path.Combine(Path.GetTempPath(), $"wdi_wifi_{Guid.NewGuid():N}.xml");
        try
        {
            string xml = BuildWlanProfileXml(ssid, isOpen, password);
            await File.WriteAllTextAsync(tempXmlPath, xml, Encoding.UTF8);

            string addOut = await RunNetshAsync($"wlan add profile filename=\"{tempXmlPath}\" user=current");
            if (!IsNetshSuccess(addOut) && !addOut.Contains("added", StringComparison.OrdinalIgnoreCase))
            {
                return (false, addOut.Trim());
            }

            string safeSsid = EscapeNetshArg(ssid);
            string connOut = await RunNetshAsync($"wlan connect name=\"{safeSsid}\" ssid=\"{safeSsid}\"");
            if (IsNetshSuccess(connOut))
            {
                return (true, string.Empty);
            }

            return (false, string.IsNullOrWhiteSpace(connOut)
                ? LocalizationService.Instance.Get("Wifi_ErrConnectFailed", "Gagal terhubung ke jaringan Wi-Fi.")
                : connOut.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath); } catch { }
        }
    }

    internal static string BuildWlanProfileXml(string ssid, bool isOpen, string? password)
    {
        string escapedSsid = SecurityElement.Escape(ssid) ?? ssid;
        string authSection = isOpen
            ? """
              <authentication>open</authentication>
              <encryption>none</encryption>
              <useOneX>false</useOneX>
              """
            : """
              <authentication>WPA2PSK</authentication>
              <encryption>AES</encryption>
              <useOneX>false</useOneX>
              """;

        string keySection = isOpen || string.IsNullOrEmpty(password)
            ? string.Empty
            : $"""
                  <sharedKey>
                      <keyType>passPhrase</keyType>
                      <protected>false</protected>
                      <keyMaterial>{SecurityElement.Escape(password)}</keyMaterial>
                  </sharedKey>
              """;

        return $"""
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                    <name>{escapedSsid}</name>
                    <SSIDConfig>
                        <SSID>
                            <name>{escapedSsid}</name>
                        </SSID>
                    </SSIDConfig>
                    <connectionType>ESS</connectionType>
                    <connectionMode>auto</connectionMode>
                    <MSM>
                        <security>
                            <authEncryption>
                {authSection}
                            </authEncryption>
                {keySection}
                        </security>
                    </MSM>
                </WLANProfile>
                """;
    }

    internal static (string? Ssid, int SignalPercent, string Security, string Band) ParseNetshInterfaceOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (null, 0, string.Empty, string.Empty);
        }

        string? state = null;
        string? ssid = null;
        string security = string.Empty;
        string radioType = string.Empty;
        int signal = 0;

        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;

            string key = rawLine[..colon].Trim();
            string val = rawLine[(colon + 1)..].Trim();

            if (key.Equals("State", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                state = val;
            }
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
            {
                ssid = val;
            }
            else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Sinyal", StringComparison.OrdinalIgnoreCase))
            {
                signal = ParsePercent(val);
            }
            else if (key.Equals("Authentication", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Otentikasi", StringComparison.OrdinalIgnoreCase))
            {
                security = val;
            }
            else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Jenis radio", StringComparison.OrdinalIgnoreCase))
            {
                radioType = val;
            }
        }

        bool connected = !string.IsNullOrWhiteSpace(state) &&
                         (state.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                          state.Contains("terhubung", StringComparison.OrdinalIgnoreCase)) &&
                         !state.Contains("disconnected", StringComparison.OrdinalIgnoreCase);

        if (!connected || string.IsNullOrWhiteSpace(ssid))
        {
            return (null, 0, string.Empty, string.Empty);
        }

        return (ssid, signal, security, FormatBandLabel(radioType));
    }

    internal static HashSet<string> ParseNetshProfilesOutput(string output)
    {
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return profiles;

        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;
            string key = rawLine[..colon].Trim();
            string val = rawLine[(colon + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(val) &&
                (key.Contains("Profile", StringComparison.OrdinalIgnoreCase) ||
                 key.Contains("Profil", StringComparison.OrdinalIgnoreCase)))
            {
                profiles.Add(val);
            }
        }
        return profiles;
    }

    internal static List<WifiNetworkItem> ParseNetshNetworksOutput(
        string output,
        string? connectedSsid,
        IReadOnlySet<string> savedProfiles)
    {
        var bySsid = new Dictionary<string, WifiNetworkItem>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return [];

        string currentSsid = string.Empty;
        string currentAuth = string.Empty;
        string currentBand = string.Empty;
        int bestSignalForSsid = 0;

        void CommitCurrent()
        {
            if (string.IsNullOrWhiteSpace(currentSsid)) return;

            bool isSecured = !string.IsNullOrWhiteSpace(currentAuth) &&
                             !currentAuth.Equals("Open", StringComparison.OrdinalIgnoreCase) &&
                             !currentAuth.Equals("Terbuka", StringComparison.OrdinalIgnoreCase);
            bool isConnected = !string.IsNullOrWhiteSpace(connectedSsid) &&
                               string.Equals(currentSsid, connectedSsid, StringComparison.Ordinal);
            bool isSaved = savedProfiles.Contains(currentSsid);

            if (bySsid.TryGetValue(currentSsid, out var existing))
            {
                int mergedSignal = Math.Max(existing.SignalPercent, bestSignalForSsid);
                string mergedBand = string.IsNullOrWhiteSpace(existing.BandText) ? currentBand : existing.BandText;
                bySsid[currentSsid] = existing with { SignalPercent = mergedSignal, BandText = mergedBand };
            }
            else
            {
                bySsid[currentSsid] = new WifiNetworkItem(
                    Ssid: currentSsid,
                    SignalPercent: bestSignalForSsid,
                    IsSecured: isSecured,
                    SecurityText: string.IsNullOrWhiteSpace(currentAuth) ? "Open" : currentAuth,
                    IsConnected: isConnected,
                    IsSavedProfile: isSaved,
                    BandText: currentBand);
            }
        }

        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();

            if (Regex.IsMatch(key, @"^SSID\s+\d+$", RegexOptions.IgnoreCase))
            {
                CommitCurrent();
                currentSsid = val;
                currentAuth = string.Empty;
                currentBand = string.Empty;
                bestSignalForSsid = 0;
            }
            else if (key.Equals("Authentication", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Otentikasi", StringComparison.OrdinalIgnoreCase))
            {
                currentAuth = val;
            }
            else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Sinyal", StringComparison.OrdinalIgnoreCase))
            {
                int sig = ParsePercent(val);
                if (sig > bestSignalForSsid) bestSignalForSsid = sig;
            }
            else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Jenis radio", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(currentBand))
                {
                    currentBand = FormatBandLabel(val);
                }
            }
        }

        CommitCurrent();

        return bySsid.Values
            .OrderByDescending(n => n.IsConnected)
            .ThenByDescending(n => n.IsSavedProfile)
            .ThenByDescending(n => n.SignalPercent)
            .ToList();
    }

    private static int ParsePercent(string text)
    {
        var m = Regex.Match(text ?? string.Empty, @"\d+");
        return m.Success && int.TryParse(m.Value, out int v) ? Math.Clamp(v, 0, 100) : 0;
    }

    private static string FormatBandLabel(string radioType)
    {
        if (string.IsNullOrWhiteSpace(radioType)) return string.Empty;
        if (radioType.Contains("802.11ax", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi 6";
        if (radioType.Contains("802.11be", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi 7";
        if (radioType.Contains("802.11ac", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi 5";
        if (radioType.Contains("802.11n", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi 4";
        return radioType;
    }

    private static string GetActiveWifiIpv4Address()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null)
                {
                    return v4.Address.ToString();
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string EscapeNetshArg(string value) => value.Replace("\"", "\\\"");

    private static bool IsNetshSuccess(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("berhasil", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return stdout;
        }
        catch
        {
            return string.Empty;
        }
    }
}
