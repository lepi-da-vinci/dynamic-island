using System.Runtime.InteropServices;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Provides native Windows power state controls (Sleep / Suspend and Hibernate)
/// using PowrProf.dll without external dependencies or UAC prompts.
/// </summary>
public static class SystemPowerService
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.I1)] bool hibernate,
        [MarshalAs(UnmanagedType.I1)] bool forceCritical,
        [MarshalAs(UnmanagedType.I1)] bool disableWakeEvent);

    /// <summary>
    /// Enters modern Standby / Sleep (Suspend) mode.
    /// </summary>
    public static bool Sleep()
    {
        try
        {
            AppLogger.LogInfo("SystemPowerService", "Putting system to Sleep...");
            return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SystemPowerService", "Failed to trigger system sleep", ex);
            return false;
        }
    }

    /// <summary>
    /// Enters system Hibernation mode.
    /// </summary>
    public static bool Hibernate()
    {
        try
        {
            AppLogger.LogInfo("SystemPowerService", "Putting system to Hibernate...");
            return SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SystemPowerService", "Failed to trigger system hibernation", ex);
            return false;
        }
    }
}
