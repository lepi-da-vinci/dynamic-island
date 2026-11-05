using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Suppresses default Windows 10/11 volume and brightness on-screen display (OSD) flyouts
/// while strictly preserving Alt+Tab, Win+Tab (Task View), Start Menu, and Quick Settings.
/// </summary>
public sealed class WindowsFlyoutSuppressor : IDisposable
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_HIDEWINDOW = 0x0080;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook;
    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            if (_isEnabled) Start();
            else Stop();
        }
    }

    public WindowsFlyoutSuppressor(bool initialEnabled = true)
    {
        _winEventDelegate = OnWinEvent;
        if (initialEnabled)
        {
            Start();
        }
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        try
        {
            _hook = SetWinEventHook(
                EVENT_OBJECT_CREATE,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            _isEnabled = true;
            AppLogger.LogInfo("WindowsFlyoutSuppressor", "Native flyout suppressor hook registered.");
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("WindowsFlyoutSuppressor", $"Could not set win event hook: {ex.Message}");
        }

        SuppressOnce();
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _isEnabled = false;
            AppLogger.LogInfo("WindowsFlyoutSuppressor", "Native flyout suppressor hook uninstalled.");
        }
    }

    /// <summary>
    /// Explicitly suppresses any active native OSD flyout windows immediately.
    /// Can be called on volume/brightness adjust events.
    /// </summary>
    public static void SuppressOnce()
    {
        try
        {
            string[] hostClasses = ["NativeHWNDHost", "XamlExplorerHostIslandWindow"];

            foreach (var cls in hostClasses)
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
                {
                    if (IsVolumeOrBrightnessFlyout(hwnd, cls))
                    {
                        ShowWindow(hwnd, SW_HIDE);
                        SetWindowPos(hwnd, IntPtr.Zero, -10000, -10000, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
                    }
                }
            }
        }
        catch
        {
            // Suppress errors during window enumeration
        }
    }

    /// <summary>
    /// Strict heuristic to determine whether a given window is specifically the volume or brightness OSD flyout,
    /// and definitely NOT Alt+Tab (Task Switching), Win+Tab (Task View), Start Menu, or Notification Center.
    /// </summary>
    private static bool IsVolumeOrBrightnessFlyout(IntPtr hwnd, string cls)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 1. Title verification: Native volume/brightness OSDs have an empty title.
        // Alt+Tab, Task View, Start Menu, Quick Settings, etc. all have non-empty titles.
        if (GetWindowTextLength(hwnd) > 0) return false;

        // 2. Strict dimension verification:
        // Native volume/brightness flyout is a small horizontal pill (~180-340px wide, ~40-90px high).
        // Alt+Tab, Task View, Start Menu, and Quick Settings are vastly larger.
        if (!GetWindowRect(hwnd, out RECT rect)) return false;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w < 80 || w > 420 || h < 25 || h > 130) return false;

        // 3. Process verification: must belong to Windows Shell (explorer.exe or ShellExperienceHost.exe)
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string pname = proc.ProcessName.ToLowerInvariant();
            if (pname != "explorer" && pname != "shellexperiencehost") return false;
        }
        catch
        {
            return false;
        }

        // 4. Child hierarchy verification:
        if (cls == "NativeHWNDHost")
        {
            // Windows 10 OSD contains DirectUIHWND
            IntPtr directUI = FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null);
            return directUI != IntPtr.Zero;
        }
        else if (cls == "XamlExplorerHostIslandWindow")
        {
            // Windows 11 OSD contains composition bridge or xaml source
            IntPtr bridge = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            IntPtr xamlSource = FindWindowEx(hwnd, IntPtr.Zero, "DesktopWindowXamlSource", null);
            return bridge != IntPtr.Zero || xamlSource != IntPtr.Zero;
        }

        return false;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_isEnabled || hwnd == IntPtr.Zero) return;

        try
        {
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, 64) > 0)
            {
                string cls = sb.ToString();
                if ((cls == "NativeHWNDHost" || cls == "XamlExplorerHostIslandWindow") &&
                    IsVolumeOrBrightnessFlyout(hwnd, cls))
                {
                    ShowWindow(hwnd, SW_HIDE);
                    SetWindowPos(hwnd, IntPtr.Zero, -10000, -10000, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
                }
            }
        }
        catch
        {
            // Ignore hook callback errors
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
