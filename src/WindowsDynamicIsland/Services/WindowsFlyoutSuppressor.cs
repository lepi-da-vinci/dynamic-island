using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Suppresses the default Windows 10/11 volume and brightness on-screen display (OSD) flyouts
/// so that only Dynamic Island's modern HUD appears on screen.
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

        // Suppress any currently visible flyouts immediately
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
            // Windows 10/11 common host classes
            string[] hostClasses = ["NativeHWNDHost", "XamlExplorerHostIslandWindow"];

            foreach (var cls in hostClasses)
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
                {
                    // Check if child DirectUIHWND or related OSD exists
                    IntPtr child = FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null);
                    if (child != IntPtr.Zero || cls == "XamlExplorerHostIslandWindow")
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

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_isEnabled || hwnd == IntPtr.Zero) return;

        try
        {
            var sb = new StringBuilder(128);
            if (GetClassName(hwnd, sb, 128) > 0)
            {
                string cls = sb.ToString();
                if (cls == "NativeHWNDHost" || cls == "XamlExplorerHostIslandWindow")
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

