using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Provides native Windows power state controls (Sleep / Modern Standby and Hibernate)
/// without external dependencies or UAC prompts.
/// </summary>
public static class SystemPowerService
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.I1)] bool hibernate,
        [MarshalAs(UnmanagedType.I1)] bool forceCritical,
        [MarshalAs(UnmanagedType.I1)] bool disableWakeEvent);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_MONITORPOWER = 0xF170;
    private static readonly IntPtr MONITOR_OFF = new(2);

    private static bool? _isModernStandby;

    /// <summary>
    /// Detects whether the current hardware/OS uses S0 Low Power Idle (Modern Standby)
    /// rather than legacy ACPI S3 sleep.
    /// </summary>
    public static bool IsModernStandby
    {
        get
        {
            if (_isModernStandby.HasValue) return _isModernStandby.Value;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/a",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    // Modern Standby laptops have S0 Low Power Idle enabled and S3 disabled
                    _isModernStandby = output.Contains("S0 Low Power Idle");
                    return _isModernStandby.Value;
                }
            }
            catch { }
            _isModernStandby = false;
            return false;
        }
    }

    /// <summary>
    /// Enters Sleep mode:
    /// On Modern Standby (S0) systems, turning off the display triggers native S0 Low Power Idle
    /// (avoiding fallback to S4 Hibernate that SetSuspendState would otherwise cause).
    /// On legacy ACPI S3 systems, invokes SetSuspendState(hibernate: false).
    /// </summary>
    public static bool Sleep()
    {
        try
        {
            if (IsModernStandby)
            {
                AppLogger.LogInfo("SystemPowerService", "Putting system to Sleep via Modern Standby (S0)...");
                SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, MONITOR_OFF);
                return true;
            }
            else
            {
                AppLogger.LogInfo("SystemPowerService", "Putting system to Sleep via ACPI S3 Suspend...");
                return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SystemPowerService", "Failed to trigger system sleep", ex);
            return false;
        }
    }

    /// <summary>
    /// Enters system Hibernation (S4) mode.
    /// </summary>
    public static bool Hibernate()
    {
        try
        {
            AppLogger.LogInfo("SystemPowerService", "Putting system to Hibernate (S4)...");
            return SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SystemPowerService", "Failed to trigger system hibernation", ex);
            return false;
        }
    }
}
