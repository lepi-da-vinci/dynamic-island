using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Suppresses default Windows 10/11 volume and brightness on-screen display (OSD) flyouts
/// while strictly preserving Alt+Tab, Win+Tab (Task View), Start Menu, and Quick Settings.
/// Applies composited zero alpha and position deflection to eliminate micro-flashes completely.
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x00000002;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static readonly HashSet<uint> ShellPids = [];
    private static DateTime _lastShellPidRefreshUtc = DateTime.MinValue;
    private static readonly Lock PidLock = new();

    private readonly WinEventDelegate _winEventDelegate;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private IntPtr _winEventHook;
    private IntPtr _keyboardHook;
    private bool _isEnabled;

    private static CancellationTokenSource? _burstCts;
    private static readonly Lock BurstLock = new();

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
        _keyboardProc = LowLevelKeyboardCallback;
        if (initialEnabled)
        {
            Start();
        }
    }

    public void Start()
    {
        if (_winEventHook != IntPtr.Zero) return;

        try
        {
            RefreshShellPids();

            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            // Install low-level keyboard hook to catch volume media keys instantly before Explorer processes them
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = GetModuleHandle(curModule?.ModuleName);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning("WindowsFlyoutSuppressor", $"Could not install keyboard hook: {ex.Message}");
            }

            _isEnabled = true;
            AppLogger.LogInfo("WindowsFlyoutSuppressor", "Native flyout suppressor hooks registered.");
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("WindowsFlyoutSuppressor", $"Could not set win event hook: {ex.Message}");
        }

        TriggerSuppressionBurst(durationMs: 400, intervalMs: 25);
    }

    public void Stop()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        lock (BurstLock)
        {
            _burstCts?.Cancel();
            _burstCts = null;
        }

        _isEnabled = false;
        AppLogger.LogInfo("WindowsFlyoutSuppressor", "Native flyout suppressor hooks uninstalled.");
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && _isEnabled)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                // Volume key touched on physical keyboard: trigger suppression burst immediately
                TriggerSuppressionBurst(durationMs: 700, intervalMs: 20);
                SuppressOnce();
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Launches a short, high-frequency suppression loop in background to eliminate any micro-flash
    /// during the 50-500ms window where Windows Explorer attempts to construct and show the OSD.
    /// </summary>
    public static void TriggerSuppressionBurst(int durationMs = 600, int intervalMs = 20)
    {
        lock (BurstLock)
        {
            _burstCts?.Cancel();
            _burstCts = new CancellationTokenSource();
            var token = _burstCts.Token;

            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < durationMs && !token.IsCancellationRequested)
                {
                    SuppressOnce();
                    try
                    {
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
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
            string[] hostClasses = ["XamlExplorerHostIslandWindow", "NativeHWNDHost"];

            foreach (var cls in hostClasses)
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
                {
                    if (IsVolumeOrBrightnessFlyout(hwnd, cls))
                    {
                        MakeWindowCompletelyInvisible(hwnd);
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
    /// Forces window opacity to 0 via DWM composition, hides it, and moves it far off-screen.
    /// Setting Alpha = 0 ensures DWM composites zero pixels even if Windows attempts to draw.
    /// </summary>
    private static void MakeWindowCompletelyInvisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            }

            // Alpha = 0 guarantees 100% transparency at DWM compositor level
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
            ShowWindow(hwnd, SW_HIDE);
            SetWindowPos(hwnd, IntPtr.Zero, -10000, -10000, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }
        catch
        {
            // Suppress API errors
        }
    }

    /// <summary>
    /// Strict heuristic to determine whether a given window is specifically the volume or brightness OSD flyout,
    /// and definitely NOT Alt+Tab (Task Switching), Win+Tab (Task View), Start Menu, or Notification Center.
    /// </summary>
    private static bool IsVolumeOrBrightnessFlyout(IntPtr hwnd, string cls)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 1. Title verification: Native volume/brightness OSDs strictly have an empty title.
        // Alt+Tab ("Task Switching"), Task View ("Task View"), Start Menu ("Start"), Quick Settings, etc. all have non-empty titles.
        if (GetWindowTextLength(hwnd) > 0) return false;

        // 2. Strict dimension verification:
        // Native volume/brightness flyout is a small horizontal pill (~180-360px wide, ~40-80px high, up to ~550x140px on 175% DPI).
        // Alt+Tab, Task View, Start Menu, and Quick Settings are vastly larger (> 650x500px).
        if (!GetWindowRect(hwnd, out RECT rect)) return false;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w < 60 || w > 580 || h < 20 || h > 160) return false;

        // 3. Process verification: must belong to Windows Shell (explorer.exe or ShellExperienceHost.exe)
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!IsShellProcess(pid)) return false;

        // 4. Child hierarchy verification:
        if (cls == "NativeHWNDHost")
        {
            IntPtr directUI = FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null);
            return directUI != IntPtr.Zero;
        }
        else if (cls == "XamlExplorerHostIslandWindow")
        {
            IntPtr bridge = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            IntPtr xamlSource = FindWindowEx(hwnd, IntPtr.Zero, "DesktopWindowXamlSource", null);
            return bridge != IntPtr.Zero || xamlSource != IntPtr.Zero;
        }

        return false;
    }

    private static bool IsShellProcess(uint pid)
    {
        if (pid == 0) return false;

        if (DateTime.UtcNow - _lastShellPidRefreshUtc > TimeSpan.FromSeconds(30))
        {
            RefreshShellPids();
        }

        lock (PidLock)
        {
            if (ShellPids.Contains(pid)) return true;
        }

        // Fallback query for newly spawned shell host processes
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName.ToLowerInvariant();
            if (name is "explorer" or "shellexperiencehost" or "startmenuexperiencehost")
            {
                lock (PidLock)
                {
                    ShellPids.Add(pid);
                }
                return true;
            }
        }
        catch
        {
            // Process might have terminated
        }

        return false;
    }

    private static void RefreshShellPids()
    {
        try
        {
            var pids = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        return name is "explorer" or "shellexperiencehost" or "startmenuexperiencehost";
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(p => (uint)p.Id)
                .ToList();

            lock (PidLock)
            {
                ShellPids.Clear();
                foreach (var pid in pids)
                {
                    ShellPids.Add(pid);
                }
            }

            _lastShellPidRefreshUtc = DateTime.UtcNow;
        }
        catch
        {
            // Suppress process enumeration issues
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // idObject == 0 represents top-level OBJID_WINDOW. Ignore child elements for zero latency.
        if (!_isEnabled || hwnd == IntPtr.Zero || idObject != 0) return;

        try
        {
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, 64) > 0)
            {
                string cls = sb.ToString();
                if ((cls == "XamlExplorerHostIslandWindow" || cls == "NativeHWNDHost") &&
                    IsVolumeOrBrightnessFlyout(hwnd, cls))
                {
                    MakeWindowCompletelyInvisible(hwnd);
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
