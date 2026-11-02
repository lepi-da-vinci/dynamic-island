using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public enum BatteryChargeLimitMode
{
    SmartCharge80 = 80,
    FullCapacity100 = 100,
    MaximumLifespan60 = 60,
    Unknown = 0
}

/// <summary>
/// Service to inspect and control laptop hardware battery charging thresholds (e.g. 80% vs 100%)
/// across various OEM manufacturers (ASUS ACPI WMI, Lenovo Conservation Mode, Dell, etc.)
/// with zero emoji output and smooth background execution.
/// </summary>
public sealed class BatteryChargeLimitService
{
    private const string AsusCustomizationIniPath =
        @"C:\ProgramData\ASUS\ASUS System Control Interface\ASUSOptimization\Customization.ini";

    public bool IsSupported { get; private set; }
    public string SupportedBrand { get; private set; } = string.Empty;
    public BatteryChargeLimitMode CurrentMode { get; private set; } = BatteryChargeLimitMode.SmartCharge80;

    public event Action<BatteryChargeLimitMode>? LimitModeChanged;

    public BatteryChargeLimitService()
    {
        DetectSupportAndCurrentLimit();
    }

    public void DetectSupportAndCurrentLimit()
    {
        try
        {
            // 1. Check for ASUS (Vivobook, Zenbook, ROG, TUF)
            if (File.Exists(AsusCustomizationIniPath) || IsAsusDevice())
            {
                IsSupported = true;
                SupportedBrand = "ASUS";
                CurrentMode = ReadAsusLimitFromIni();
                AppLogger.LogInfo("BatteryChargeLimitService", $"Detected ASUS hardware. Current battery limit: {(int)CurrentMode}%");
                return;
            }

            // 2. Check for Lenovo
            if (IsLenovoDevice())
            {
                IsSupported = true;
                SupportedBrand = "Lenovo";
                CurrentMode = BatteryChargeLimitMode.SmartCharge80;
                AppLogger.LogInfo("BatteryChargeLimitService", "Detected Lenovo hardware.");
                return;
            }

            IsSupported = false;
            SupportedBrand = "Generic";
            CurrentMode = BatteryChargeLimitMode.FullCapacity100;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BatteryChargeLimitService", "Error detecting battery hardware controller", ex);
            IsSupported = false;
        }
    }

    private static string GetSystemManufacturer()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key != null)
            {
                return key.GetValue("SystemManufacturer")?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static bool IsAsusDevice()
    {
        string mfg = GetSystemManufacturer();
        return mfg.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ||
               mfg.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLenovoDevice()
    {
        string mfg = GetSystemManufacturer();
        return mfg.Contains("LENOVO", StringComparison.OrdinalIgnoreCase);
    }

    private static BatteryChargeLimitMode ReadAsusLimitFromIni()
    {
        try
        {
            if (File.Exists(AsusCustomizationIniPath))
            {
                string text = File.ReadAllText(AsusCustomizationIniPath);
                var match = Regex.Match(text, @"\[BatteryHealthCharging\][^\[]*?value=(\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int val))
                {
                    return val switch
                    {
                        60 => BatteryChargeLimitMode.MaximumLifespan60,
                        80 => BatteryChargeLimitMode.SmartCharge80,
                        100 => BatteryChargeLimitMode.FullCapacity100,
                        _ => (BatteryChargeLimitMode)val
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BatteryChargeLimitService", "Error reading ASUS Customization.ini", ex);
        }

        return BatteryChargeLimitMode.SmartCharge80;
    }

    private static bool UpdateAsusCustomizationIni(int limit)
    {
        try
        {
            if (!File.Exists(AsusCustomizationIniPath)) return false;

            string content = File.ReadAllText(AsusCustomizationIniPath);
            string timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss:fff");

            string pattern = @"(\[BatteryHealthCharging\][^\[]*?value=)\d+([^\[]*?time="")([^""]*)";
            if (Regex.IsMatch(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string updated = Regex.Replace(
                    content,
                    pattern,
                    $"${{1}}{limit}${{2}}{timestamp}",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                File.WriteAllText(AsusCustomizationIniPath, updated, Encoding.UTF8);
                AppLogger.LogInfo("BatteryChargeLimitService", $"Updated Customization.ini to {limit}% with timestamp {timestamp}");
                return true;
            }
            else
            {
                content = Regex.Replace(
                    content,
                    @"(\[BatteryHealthCharging\][^\[]*?value=)\d+",
                    $"${{1}}{limit}",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                File.WriteAllText(AsusCustomizationIniPath, content, Encoding.UTF8);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BatteryChargeLimitService", "Error writing to ASUS Customization.ini", ex);
            return false;
        }
    }

    public bool IsHardwareTaskRegistered =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", "WindowsDynamicIsland-Battery80")) ||
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", "WindowsDynamicIsland-Battery100"));

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        return false;
    }

    public static void RegisterHardwareTasksOneTime()
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string batPath = Path.Combine(appDir, "Aktifkan-Smart-Charge.bat");
            if (!File.Exists(batPath))
            {
                batPath = @"d:\DynamicIsland\Aktifkan-Smart-Charge.bat";
            }

            if (File.Exists(batPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{batPath}\"\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
                AppLogger.LogInfo("BatteryChargeLimitService", "Completed one-time hardware tasks registration via batch.");
                return;
            }

            string scriptPath = Path.Combine(appDir, "setup_battery_tasks.ps1");
            if (!File.Exists(scriptPath))
            {
                scriptPath = @"d:\DynamicIsland\setup_battery_tasks.ps1";
            }

            if (File.Exists(scriptPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
                AppLogger.LogInfo("BatteryChargeLimitService", "Completed one-time hardware tasks registration.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("BatteryChargeLimitService", $"One-time task registration dismissed or failed: {ex.Message}");
        }
    }

    private static void NotifyAsusOptimizationService(int limitPercent)
    {
        try
        {
            var proc = Process.GetProcessesByName("AsusOptimizationStartupTask").FirstOrDefault();
            string? exePath = null;
            if (proc != null)
            {
                try { exePath = proc.MainModule?.FileName; } catch { }
            }

            if (string.IsNullOrEmpty(exePath))
            {
                string driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
                if (Directory.Exists(driverStore))
                {
                    var files = Directory.GetFiles(driverStore, "AsusOptimizationStartupTask.exe", SearchOption.AllDirectories);
                    exePath = files.FirstOrDefault();
                }
            }

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"-BICustomization {limitPercent}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1500);
                AppLogger.LogInfo("BatteryChargeLimitService", $"Notified AsusOptimizationStartupTask -BICustomization {limitPercent}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("BatteryChargeLimitService", $"AsusOptimizationStartupTask notification skipped: {ex.Message}");
        }
    }

    private void ApplyAsusWmiLimit(int limitPercent)
    {
        try
        {
            string taskName = $"WindowsDynamicIsland-Battery{limitPercent}";
            string taskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", taskName);

            // Tier 1: Zero-UAC Scheduled Task (SYSTEM execution, instant & silent)
            if (File.Exists(taskPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/run /tn \"{taskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
                AppLogger.LogInfo("BatteryChargeLimitService", $"Dispatched zero-UAC scheduled task: {taskName}");
                return;
            }

            // Tier 2: Already elevated process
            if (IsCurrentProcessElevated())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"try {{ Invoke-CimMethod -Namespace root/wmi -ClassName AsusAtkWmi_WMNB -MethodName DEVS -Arguments @{{Device_ID=[uint32]0x00120057; Control_status=[uint32]{limitPercent}}} }} catch {{ (Get-WmiObject -Namespace root/wmi -Class AsusAtkWmi_WMNB).DEVS(0x00120057, {limitPercent}) }}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
                AppLogger.LogInfo("BatteryChargeLimitService", $"Dispatched elevated ASUS WMI DEVS command: {limitPercent}%");
                return;
            }

            // Tier 3: Trigger one-time task registration so all future switches are 100% silent
            AppLogger.LogInfo("BatteryChargeLimitService", "Tasks not yet registered. Triggering one-time registration helper.");
            RegisterHardwareTasksOneTime();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("BatteryChargeLimitService", $"ASUS WMI elevation skipped: {ex.Message}");
        }
    }

    public async Task<bool> SetLimitModeAsync(BatteryChargeLimitMode mode)
    {
        if (mode == BatteryChargeLimitMode.Unknown) return false;

        try
        {
            int limitPercent = (int)mode;

            if (SupportedBrand == "ASUS")
            {
                // 1. Update ASUS Customization.ini (immediate non-admin sync for MyASUS & ASUSOptimization)
                UpdateAsusCustomizationIni(limitPercent);

                // 2. Silently notify ASUS background components without any UAC elevation prompts
                await Task.Run(() =>
                {
                    NotifyAsusOptimizationService(limitPercent);
                    ApplyAsusWmiLimit(limitPercent);
                });

                CurrentMode = mode;
                LimitModeChanged?.Invoke(mode);
                return true;
            }

            if (SupportedBrand == "Lenovo")
            {
                int conservationMode = mode == BatteryChargeLimitMode.SmartCharge80 ? 1 : 0;
                await Task.Run(() =>
                {
                    try
                    {
                        if (IsCurrentProcessElevated())
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"try {{ (Get-CimInstance -Namespace root/wmi -ClassName Lenovo_SetConservationMode).SetConservationMode({conservationMode}) }} catch {{ }}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            using var p = Process.Start(psi);
                            p?.WaitForExit(2000);
                        }
                    }
                    catch { }
                });

                CurrentMode = mode;
                LimitModeChanged?.Invoke(mode);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("BatteryChargeLimitService", $"Failed setting charge limit to {mode}", ex);
        }

        return false;
    }

    public static void OpenOemBatteryCareSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "asus-support:",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:batterysaver",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
