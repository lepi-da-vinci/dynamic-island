using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

/// <summary>
/// Main overlay window hosting the unified one-piece NotchNook-style Dynamic Island,
/// bespoke vector SVG iconography, and real-time WASAPI Loopback 5-band audio visualizer.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IslandStateMachine _stateMachine = new();
    private readonly IslandMorphController _morph = new();
    private readonly MediaSessionService _mediaService = new();
    private readonly LyricsService _lyricsService = new();
    private readonly AudioService _audioService = new();
    private readonly BrightnessService _brightnessService = new();
    private readonly HardwareTelemetryService _hardwareService = new();
    private readonly NotificationListenerService _notificationService = new();
    private readonly FileShelfService _fileShelfService = new();
    private readonly PomodoroTimerService _pomodoroService = new();
    private readonly TodoAndWaterService _todoAndWaterService = new();
    private readonly UpdateCheckerService _updateService = new();

    private readonly DispatcherTimer _oneSecondTimer;
    private readonly DispatcherTimer _fastUpdateTimer;
    private readonly DispatcherTimer _hitTestTimer;
    private readonly DispatcherTimer _toastDismissTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;
    private readonly DispatcherTimer _heartbeatTimer;

    private readonly double[] _spectrumHeights = [4, 7, 11, 7, 4];
    private Rectangle[] _waveRectangles = [];
    private Rectangle[] _expWaveRectangles = [];

    private IntPtr _hwnd;
    private Win32Interop.NOTIFYICONDATA _trayIconData;
    private bool _isRenderLoopAttached;
    private bool _isWindowClickThrough;
    private long _lastRenderTick;
    private string _lastClipboardPreview = string.Empty;
    private readonly bool _autoHideFullScreen = true;

    // Lyric animation & slow one-way pan tracking
    private int _lastAnimatedLyricIndex = -999;
    private string _lastDisplayedCompactText = string.Empty;
    private double _currentLyricLineProgress;
    private double _currentPanX;
    private int _topBezelHoldTicks;

    // Interactive timeline & slider scrubbing states
    private bool _isDraggingTimeline;
    private double _draggedTimelineRatio;
    private bool _isDraggingToastSlider;
    private bool _isDraggingSysVolume;
    private bool _isDraggingSysBrightness;
    private ToastSliderKind _activeToastSliderKind = ToastSliderKind.None;
    private CancellationTokenSource? _repositionCts;

    // Interactive satellite side-wing switcher state (Zero drift, anchor rock-solid at center)
    private double _leftWingWidth;
    private double _targetLeftWingWidth;
    private double _rightWingWidth;
    private double _targetRightWingWidth;
    private int _hoveredSideZone; // -1 = Left, 0 = None, 1 = Right
    private IslandDashboardTab _previousActiveTab = IslandDashboardTab.MediaLyrics;
    private int _explicitSlideDirection;

    public MainWindow()
    {
        AppLogger.LogInfo("MainWindow", "Entering MainWindow constructor.");
        try
        {
            InitializeComponent();
            AppLogger.LogInfo("MainWindow", "InitializeComponent() succeeded.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("InitializeComponent", ex);
            throw;
        }

        _waveRectangles = [WaveBar0, WaveBar1, WaveBar2, WaveBar3, WaveBar4];
        _expWaveRectangles = [ExpWaveBar0, ExpWaveBar1, ExpWaveBar2, ExpWaveBar3, ExpWaveBar4];

        ShelfItemsControl.ItemsSource = _fileShelfService.Items;
        _fileShelfService.Items.CollectionChanged += (_, _) => UpdateShelfUi();

        _oneSecondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _oneSecondTimer.Tick += OnOneSecondTick;

        _fastUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fastUpdateTimer.Tick += OnFastUpdateTick;

        _hitTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _hitTestTimer.Tick += (_, _) =>
        {
            UpdateSideWingHover();
            UpdateWindowClickThroughState();
        };

        _toastDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastDismissTimer.Tick += (_, _) =>
        {
            if (_isDraggingToastSlider) return;
            _toastDismissTimer.Stop();
            _stateMachine.ClearToast();
        };

        _collapseDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _collapseDebounceTimer.Tick += (_, _) =>
        {
            _collapseDebounceTimer.Stop();
            if (!IsCursorInsideIslandBounds() &&
                !_isDraggingTimeline &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness &&
                !_isDraggingToastSlider)
            {
                _stateMachine.SetHovered(false);
            }
        };

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _heartbeatTimer.Tick += OnHeartbeatTick;

        _stateMachine.StateChanged += OnIslandStateChanged;
        _mediaService.TrackChanged += OnMediaTrackChanged;
        _mediaService.PlaybackStateChanged += OnMediaPlaybackStateChanged;
        _lyricsService.LyricsUpdated += () => Dispatcher.InvokeAsync(UpdateMediaAndLyricsUi);
        _hardwareService.HardwareToastTriggered += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _notificationService.NotificationReceived += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _pomodoroService.TimerTicked += () => Dispatcher.InvokeAsync(UpdatePomodoroAndClockUi);
        _pomodoroService.TimerCompleted += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _todoAndWaterService.DataChanged += () => Dispatcher.InvokeAsync(UpdateTodoAndWaterUi);
        _updateService.UpdateStatusChanged += res => Dispatcher.InvokeAsync(() => UpdateSystemUpdateUi(res));
        _updateService.UpdateNotificationTriggered += res => Dispatcher.InvokeAsync(() =>
        {
            TriggerToast(new ToastPayload(
                Icon: "SHIELD",
                Title: $"Update Baru: {res.LatestVersion}",
                Subtitle: "Buka Tab Sistem di Dynamic Island untuk pasang",
                AccentHex: "#22C55E",
                Duration: TimeSpan.FromSeconds(4.5)));
        });

        // Subscribe to external keyboard Volume / Mute / Mic Mute / Brightness key events
        _audioService.SpeakerVolumeOrMuteChanged += (vol, muted, muteToggled) =>
            Dispatcher.InvokeAsync(() => ShowVolumeToast(vol, muted, muteToggled));

        _audioService.MicrophoneMuteChanged += micMuted =>
            Dispatcher.InvokeAsync(() => ShowMicMuteToast(micMuted));

        _audioService.OutputDeviceChanged += (devName, isHeadphone) =>
            Dispatcher.InvokeAsync(() =>
            {
                UpdateSlidersAndMuteUi();
                UpdateMediaAndLyricsUi();
            });

        _brightnessService.BrightnessChanged += brt =>
            Dispatcher.InvokeAsync(() => ShowBrightnessToast(brt));

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        DpiChanged += OnDpiChanged;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        AppLogger.LogInfo("MainWindow", "MainWindow constructor completed successfully.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        AppLogger.LogInfo("OnSourceInitialized", "Entering OnSourceInitialized.");
        try
        {
            base.OnSourceInitialized(e);

            _hwnd = new WindowInteropHelper(this).Handle;
            AppLogger.LogInfo("OnSourceInitialized", $"Got HWND=0x{_hwnd:X}. Adding WndProc hook...");
            HwndSource? source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProc);

            AppLogger.LogInfo("OnSourceInitialized", "Setting window styles...");
            int exStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
            exStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST | Win32Interop.WS_EX_NOACTIVATE;
            Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);

            AppLogger.LogInfo("OnSourceInitialized", "Adding clipboard format listener...");
            Win32Interop.AddClipboardFormatListener(_hwnd);

            AppLogger.LogInfo("OnSourceInitialized", "Initializing system tray icon...");
            InitializeSystemTrayIcon();

            AppLogger.LogInfo("OnSourceInitialized", "Positioning top center...");
            PositionTopCenter();
            AppLogger.LogInfo("OnSourceInitialized", "OnSourceInitialized completed.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("OnSourceInitialized", ex);
            throw;
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", "Entering OnWindowLoaded.");
        try
        {
            PositionTopCenter();
            UpdateTabHighlight();
            _hardwareService.RefreshFullTelemetry(emitToasts: false);
            _audioService.PollExternalChanges();
            _brightnessService.PollExternalChanges();
            UpdateHardwareUi();
            UpdateSlidersAndMuteUi();
            UpdatePomodoroAndClockUi();
            UpdateTodoAndWaterUi();
            UpdatePaginationDots();
            UpdateSystemUpdateUi(_updateService.LastResult);

            _oneSecondTimer.Start();
            _fastUpdateTimer.Start();
            _hitTestTimer.Start();
            _heartbeatTimer.Start();
            UpdateWindowClickThroughState();

            AppLogger.LogInfo("WindowLifecycle", $"MainWindow loaded successfully. HWND=0x{_hwnd:X}, Left={Left}, Top={Top}, Width={Width}, Height={Height}");

            TriggerToast(new ToastPayload(
                Icon: "BELL",
                Title: "Dynamic Island Aktif",
                Subtitle: "Scroll = Volume • Shift+Scroll = Kecerahan",
                AccentHex: "#38BDF8",
                Duration: TimeSpan.FromSeconds(2.6)));

            await _mediaService.InitializeAsync();
            EnsureRenderLoopRunning();
            AppLogger.LogInfo("WindowLifecycle", "OnWindowLoaded completed successfully.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("OnWindowLoaded", ex);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"MainWindow.OnClosing called. Cancel={e.Cancel}. CallStack:\r\n{new StackTrace(1, true)}");
        base.OnClosing(e);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"MainWindow.OnWindowClosed called. CallStack:\r\n{new StackTrace(1, true)}");
        StopRenderLoop();
        _heartbeatTimer.Stop();
        _oneSecondTimer.Stop();
        _fastUpdateTimer.Stop();
        _hitTestTimer.Stop();
        _mediaService.Dispose();
        _hardwareService.Dispose();
        _updateService.Dispose();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DpiChanged -= OnDpiChanged;

        try
        {
            _repositionCts?.Cancel();
            _repositionCts?.Dispose();
            _repositionCts = null;
        }
        catch { }

        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.RemoveClipboardFormatListener(_hwnd);
            Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_DELETE, ref _trayIconData);
            if (_trayIconData.hIcon != IntPtr.Zero && _trayIconData.hIcon != Win32Interop.IDI_APPLICATION)
            {
                Win32Interop.DestroyIcon(_trayIconData.hIcon);
            }
        }
    }

    private void OnHeartbeatTick(object? sender, EventArgs e)
    {
        try
        {
            bool isCursorInside = IsCursorInsideIslandBounds();
            string status = $"State={_stateMachine.CurrentState}, Tab={_stateMachine.ActiveTab}, CursorInIsland={isCursorInside}, FullScreenHidden={_stateMachine.IsFullScreenHidden}, RenderLoopAttached={_isRenderLoopAttached}, HWND=0x{_hwnd:X}";
            AppLogger.LogHeartbeat(status);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Heartbeat", "Error during heartbeat tick", ex);
        }
    }

    private void PositionTopCenter()
    {
        try
        {
            double targetLeft;
            double targetTop = 0;
            double screenWidthDip;

            if (_hwnd != IntPtr.Zero)
            {
                IntPtr hMonitor = Win32Interop.MonitorFromWindow(_hwnd, Win32Interop.MONITOR_DEFAULTTOPRIMARY);
                var mi = new Win32Interop.MONITORINFO();
                mi.cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>();
                if (Win32Interop.GetMonitorInfo(hMonitor, ref mi))
                {
                    double scaleX = 1.0;
                    double scaleY = 1.0;

                    uint nativeDpi = Win32Interop.GetDpiForWindow(_hwnd);
                    if (nativeDpi > 0)
                    {
                        scaleX = nativeDpi / 96.0;
                        scaleY = scaleX;
                    }
                    else
                    {
                        DpiScale dpi = VisualTreeHelper.GetDpi(this);
                        scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                        scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                    }

                    int monitorWidthPx = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    screenWidthDip = monitorWidthPx / scaleX;
                    double monitorLeftDip = mi.rcMonitor.Left / scaleX;
                    double monitorTopDip = mi.rcMonitor.Top / scaleY;

                    targetLeft = monitorLeftDip + (screenWidthDip - Width) / 2.0;
                    targetTop = monitorTopDip;

                    Left = targetLeft;
                    Top = targetTop;

                    // Enforce physical native HWND coordinates directly at the OS level
                    int windowWidthPx = (int)Math.Round(Width * scaleX);
                    int windowHeightPx = (int)Math.Round(Height * scaleY);
                    int pixelX = mi.rcMonitor.Left + (monitorWidthPx - windowWidthPx) / 2;
                    int pixelY = mi.rcMonitor.Top;

                    Win32Interop.SetWindowPos(
                        _hwnd,
                        Win32Interop.HWND_TOPMOST,
                        pixelX,
                        pixelY,
                        windowWidthPx,
                        windowHeightPx,
                        Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

                    AppLogger.LogInfo("PositionTopCenter",
                        $"Monitor-aligned: Left={Left:F1}, Top={Top:F1}, PixelX={pixelX}, PixelY={pixelY}, MonWidthPx={monitorWidthPx}, ScaleX={scaleX:F2}, ScreenWidthDip={screenWidthDip:F1}");
                    return;
                }
            }

            // Fallback if HWND not ready or monitor info fails
            screenWidthDip = SystemParameters.PrimaryScreenWidth;
            targetLeft = (screenWidthDip - Width) / 2.0;
            Left = targetLeft;
            Top = 0;
            AppLogger.LogInfo("PositionTopCenter", $"SystemParameters fallback: Left={Left:F1}, Top={Top:F1}, ScreenWidthDip={screenWidthDip:F1}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("PositionTopCenter", "Error positioning top center", ex);
        }
    }

    private void ScheduleDebouncedReposition()
    {
        try
        {
            _repositionCts?.Cancel();
            _repositionCts?.Dispose();
            var cts = new CancellationTokenSource();
            _repositionCts = cts;

            // 1. Reposition immediately
            PositionTopCenter();

            // 2. Delayed multi-phase reposition to catch OS video mode settling (e.g. after exiting exclusive fullscreen games)
            _ = Task.Run(async () =>
            {
                int[] delays = [150, 400, 1000, 2500];
                foreach (int delay in delays)
                {
                    if (cts.Token.IsCancellationRequested) return;
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    if (cts.Token.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!cts.Token.IsCancellationRequested)
                        {
                            PositionTopCenter();
                        }
                    });
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ScheduleDebouncedReposition", "Error scheduling reposition", ex);
        }
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"DpiChanged event fired: {e.NewDpi.DpiScaleX}x{e.NewDpi.DpiScaleY} -> Scheduling reposition.");
        ScheduleDebouncedReposition();
    }

    private void InitializeSystemTrayIcon()
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                Win32Interop.ExtractIconEx(exePath, 0, out _, out hIcon, 1);
            }
        }
        catch { }

        if (hIcon == IntPtr.Zero)
        {
            try
            {
                string localIco = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                if (System.IO.File.Exists(localIco))
                {
                    Win32Interop.ExtractIconEx(localIco, 0, out _, out hIcon, 1);
                }
            }
            catch { }
        }

        if (hIcon == IntPtr.Zero)
        {
            try
            {
                string installedIco = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsDynamicIsland", "app_icon.ico");
                if (System.IO.File.Exists(installedIco))
                {
                    Win32Interop.ExtractIconEx(installedIco, 0, out _, out hIcon, 1);
                }
            }
            catch { }
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = Win32Interop.LoadIcon(IntPtr.Zero, Win32Interop.IDI_APPLICATION);
        }

        _trayIconData = new Win32Interop.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32Interop.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP,
            uCallbackMessage = Win32Interop.WM_USER_TRAY,
            hIcon = hIcon,
            szTip = "Windows Dynamic Island (Klik untuk Buka / Klik Kanan untuk Sistem)"
        };

        Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_ADD, ref _trayIconData);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_NCHITTEST)
        {
            bool isAltBypass = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool isHidden = _stateMachine.CurrentState == IslandVisualState.Hidden;
            bool isDraggingControl =
                _isDraggingTimeline ||
                _isDraggingToastSlider ||
                _isDraggingSysVolume ||
                _isDraggingSysBrightness;

            if (isAltBypass || isHidden || (!IsCursorInsideIslandBounds() && !isDraggingControl))
            {
                UpdateWindowClickThroughState();
                handled = true;
                return new IntPtr(Win32Interop.HTTRANSPARENT);
            }
        }
        else if (msg == Win32Interop.WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdated();
        }
        else if (msg == Win32Interop.WM_USER_TRAY)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == Win32Interop.WM_LBUTTONUP || mouseMsg == Win32Interop.WM_LBUTTONDBLCLK)
            {
                _stateMachine.SetTuckedMode(false);
                _stateMachine.TogglePinnedExpanded();
            }
            else if (mouseMsg == Win32Interop.WM_RBUTTONUP)
            {
                _stateMachine.SetTuckedMode(false);
                _stateMachine.SetActiveTab(IslandDashboardTab.SystemStatus);
                _stateMachine.TogglePinnedExpanded();
            }
        }
        else if (msg == Win32Interop.WM_QUERYENDSESSION)
        {
            AppLogger.LogWarning("WndProc", $"WM_QUERYENDSESSION received (Windows logoff/shutdown in progress). wParam={wParam}, lParam={lParam}");
        }
        else if (msg == Win32Interop.WM_ENDSESSION)
        {
            AppLogger.LogWarning("WndProc", $"WM_ENDSESSION received. wParam={wParam}, lParam={lParam}");
        }
        else if (msg == Win32Interop.WM_CLOSE)
        {
            AppLogger.LogWarning("WndProc", $"WM_CLOSE received via WndProc. CallStack:\r\n{new StackTrace(true)}");
        }
        else if (msg == Win32Interop.WM_DESTROY)
        {
            AppLogger.LogInfo("WndProc", "WM_DESTROY received.");
        }
        else if (msg == Win32Interop.WM_POWERBROADCAST)
        {
            AppLogger.LogInfo("WndProc", $"WM_POWERBROADCAST received: wParam={wParam}");
        }
        else if (msg == Win32Interop.WM_DISPLAYCHANGE)
        {
            int dispW = lParam.ToInt32() & 0xFFFF;
            int dispH = (lParam.ToInt32() >> 16) & 0xFFFF;
            AppLogger.LogInfo("WndProc", $"WM_DISPLAYCHANGE received: {dispW}x{dispH} -> Scheduling reposition.");
            ScheduleDebouncedReposition();
        }
        else if (msg == Win32Interop.WM_DPICHANGED)
        {
            AppLogger.LogInfo("WndProc", "WM_DPICHANGED received -> Scheduling reposition.");
            ScheduleDebouncedReposition();
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Checks whether the mouse cursor is strictly inside the active Dynamic Island rectangle.
    /// Any pixel outside (including DropShadowEffect and BlurEffect areas) is treated as click-through.
    /// </summary>
    private bool IsCursorInsideIslandBounds()
    {
        if (_hwnd == IntPtr.Zero || !Win32Interop.GetCursorPos(out var pt))
            return false;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dipX = (pt.X / dpi.DpiScaleX) - Left;
        double dipY = (pt.Y / dpi.DpiScaleY) - Top;

        double bodyWidth = Math.Max(48, _morph.Width.Current);
        double bodyHeight = Math.Max(4, _morph.Height.Current);

        // In TuckedTopEdge ("Mode Simpan"), restrict clickable zone to top 0..5px
        // so Chrome tabs right below (Y >= 6px) are 100% clickable!
        if (_stateMachine.CurrentState == IslandVisualState.TuckedTopEdge)
        {
            bodyHeight = Math.Min(bodyHeight, 5.0);
            double tuckedLeft = (Width - bodyWidth) / 2.0;
            return dipX >= tuckedLeft && dipX <= (tuckedLeft + bodyWidth) && dipY >= 0 && dipY <= bodyHeight;
        }

        // Generous margin tolerance around the notch so swift cursor moves never flicker
        double marginTolerance = 24.0;
        double centerX = Width / 2.0;
        double leftEdge = centerX - (bodyWidth / 2.0) - _leftWingWidth - marginTolerance;
        double rightEdge = centerX + (bodyWidth / 2.0) + _rightWingWidth + marginTolerance;
        double bottomEdge = bodyHeight + marginTolerance;

        return dipX >= leftEdge && dipX <= rightEdge && dipY >= -5 && dipY <= bottomEdge;
    }

    /// <summary>
    /// Dynamically toggles WS_EX_TRANSPARENT on the Win32 layered window so that
    /// clicks outside the exact Dynamic Island body pass 100% directly to Chrome and other apps underneath.
    /// </summary>
    private void UpdateWindowClickThroughState()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool isAltBypass = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool isHidden = _stateMachine.CurrentState == IslandVisualState.Hidden;
        bool isDraggingControl =
            _isDraggingTimeline ||
            _isDraggingToastSlider ||
            _isDraggingSysVolume ||
            _isDraggingSysBrightness;

        bool cursorInside = IsCursorInsideIslandBounds();
        bool shouldBeClickThrough = isAltBypass || isHidden || (!cursorInside && !isDraggingControl);

        if (shouldBeClickThrough != _isWindowClickThrough)
        {
            _isWindowClickThrough = shouldBeClickThrough;
            int exStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
            if (shouldBeClickThrough)
            {
                exStyle |= Win32Interop.WS_EX_TRANSPARENT;
            }
            else
            {
                exStyle &= ~Win32Interop.WS_EX_TRANSPARENT;
            }
            Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);
        }

        if (!cursorInside && !isDraggingControl && _stateMachine.IsHovered && !_collapseDebounceTimer.IsEnabled)
        {
            _collapseDebounceTimer.Start();
        }
    }

    private void OnClipboardUpdated()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(text) && text != _lastClipboardPreview)
                {
                    _lastClipboardPreview = text;
                    string singleLine = text.Replace("\r", " ").Replace("\n", " ");
                    if (singleLine.Length > 36)
                    {
                        singleLine = singleLine[..36] + "…";
                    }

                    TriggerToast(new ToastPayload(
                        Icon: "FOLDER",
                        Title: "Tersalin ke Clipboard",
                        Subtitle: singleLine,
                        AccentHex: "#A855F7",
                        Duration: TimeSpan.FromSeconds(2.0)));
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    TriggerToast(new ToastPayload(
                        Icon: "FOLDER",
                        Title: $"{files.Count} File Disalin",
                        Subtitle: "Tip: Drag ke Island untuk simpan di Rak File",
                        AccentHex: "#38BDF8",
                        Duration: TimeSpan.FromSeconds(2.2)));
                }
            }
        }
        catch { }
    }

    private void OnIslandStateChanged(IslandVisualState state, IslandDashboardTab tab)
    {
        var (w, h, bottomRadius, topEarRadius, glow) = _stateMachine.GetTargetDimensions();
        bool expanding = w > _morph.Width.Current || h > _morph.Height.Current;
        _morph.MorphTo(w, h, bottomRadius, topEarRadius, glow, bouncy: expanding);

        TuckedHandlePanel.Visibility = state == IslandVisualState.TuckedTopEdge ? Visibility.Visible : Visibility.Collapsed;
        IdlePillPanel.Visibility = state == IslandVisualState.IdlePill ? Visibility.Visible : Visibility.Collapsed;
        CompactMediaPanel.Visibility = state == IslandVisualState.CompactMedia ? Visibility.Visible : Visibility.Collapsed;
        ToastPanel.Visibility = state == IslandVisualState.ToastNotification ? Visibility.Visible : Visibility.Collapsed;
        ExpandedDashboardPanel.Visibility = state == IslandVisualState.ExpandedDashboard ? Visibility.Visible : Visibility.Collapsed;

        double visibleOpacity = state == IslandVisualState.Hidden ? 0.0 : 1.0;
        IslandBorder.Opacity = visibleOpacity;
        UnifiedNotchPath.Opacity = visibleOpacity;
        UnifiedNotchRimPath.Opacity = visibleOpacity;

        // Completely remove drop-shadow and glow bleed when tucked or hidden so pixels below Y=5px have Alpha=0
        bool suppressShadowAndGlow = state is IslandVisualState.TuckedTopEdge or IslandVisualState.Hidden;
        UnifiedNotchShadow.Opacity = suppressShadowAndGlow ? 0.0 : 0.72;
        AmbientGlowBorder.Visibility = suppressShadowAndGlow ? Visibility.Collapsed : Visibility.Visible;

        MediaTabGrid.Visibility = tab == IslandDashboardTab.MediaLyrics ? Visibility.Visible : Visibility.Collapsed;
        TodoAndWaterTabGrid.Visibility = tab == IslandDashboardTab.TodoAndWater ? Visibility.Visible : Visibility.Collapsed;
        PomodoroTabGrid.Visibility = tab == IslandDashboardTab.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        ShelfTabGrid.Visibility = tab == IslandDashboardTab.FileShelf ? Visibility.Visible : Visibility.Collapsed;
        SystemTabGrid.Visibility = tab == IslandDashboardTab.SystemStatus ? Visibility.Visible : Visibility.Collapsed;

        if (state == IslandVisualState.ExpandedDashboard)
        {
            AnimateTabContentSlide(tab);

            if (tab == IslandDashboardTab.SystemStatus)
            {
                UpdateSlidersAndMuteUi();
            }
            else if (tab == IslandDashboardTab.TodoAndWater)
            {
                UpdateTodoAndWaterUi();
            }
            else if (tab == IslandDashboardTab.Pomodoro)
            {
                UpdatePomodoroAndClockUi();
            }
        }

        UpdateTabHighlight();
        UpdateWindowClickThroughState();
        EnsureRenderLoopRunning();
    }

    private void AnimateTabContentSlide(IslandDashboardTab newTab)
    {
        try
        {
            if (newTab == _previousActiveTab) return;

            int prevIdx = (int)_previousActiveTab;
            int nextIdx = (int)newTab;
            int direction;

            if (_explicitSlideDirection != 0)
            {
                direction = _explicitSlideDirection;
                _explicitSlideDirection = 0;
            }
            else
            {
                if (prevIdx == 4 && nextIdx == 0) direction = 1;
                else if (prevIdx == 0 && nextIdx == 4) direction = -1;
                else direction = nextIdx > prevIdx ? 1 : -1;
            }

            _previousActiveTab = newTab;

            double startOffset = direction * 65.0;

            TabContentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TabContentContainer.BeginAnimation(UIElement.OpacityProperty, null);

            TabContentTranslate.X = startOffset;
            TabContentContainer.Opacity = 0.25;

            var slideAnimation = new DoubleAnimation
            {
                From = startOffset,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };

            var fadeAnimation = new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            TabContentTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
            TabContentContainer.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AnimateTabContentSlide", "Error running tab slide animation", ex);
        }
    }

    private void UpdateTabHighlight()
    {
        var activeBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        var inactiveBrush = Brushes.Transparent;
        var activeFg = Brushes.White;
        var inactiveFg = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        SetTabVisual(TabMediaBtn, _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics);
        SetTabVisual(TabTodoBtn, _stateMachine.ActiveTab == IslandDashboardTab.TodoAndWater);
        SetTabVisual(TabPomodoroBtn, _stateMachine.ActiveTab == IslandDashboardTab.Pomodoro);
        SetTabVisual(TabShelfBtn, _stateMachine.ActiveTab == IslandDashboardTab.FileShelf);
        SetTabVisual(TabSystemBtn, _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus);

        UpdatePaginationDots();
        UpdateSideWingVisuals();

        void SetTabVisual(Button btn, bool isSelected)
        {
            btn.Background = isSelected ? activeBrush : inactiveBrush;
            btn.Foreground = isSelected ? activeFg : inactiveFg;
        }
    }

    private void EnsureRenderLoopRunning()
    {
        if (_isRenderLoopAttached) return;
        _lastRenderTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _isRenderLoopAttached = true;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderLoopAttached) return;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _isRenderLoopAttached = false;
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Clamp((double)(now - _lastRenderTick) / Stopwatch.Frequency, 0.001, 0.05);
            _lastRenderTick = now;

            bool morphActive = _morph.Update(dt);

            // Smooth satellite side-wing width animations (Zero notch drift, rock-solid center)
            double wingSmooth = Math.Clamp(dt * 16.0, 0.05, 0.35);
            _leftWingWidth += (_targetLeftWingWidth - _leftWingWidth) * wingSmooth;
            _rightWingWidth += (_targetRightWingWidth - _rightWingWidth) * wingSmooth;

            if (Math.Abs(_targetLeftWingWidth - _leftWingWidth) < 0.1 && _targetLeftWingWidth == 0) _leftWingWidth = 0;
            if (Math.Abs(_targetRightWingWidth - _rightWingWidth) < 0.1 && _targetRightWingWidth == 0) _rightWingWidth = 0;

            if (_leftWingWidth > 1.0)
            {
                LeftWingContainer.Visibility = Visibility.Visible;
                LeftWingContainer.Opacity = Math.Clamp((_leftWingWidth - 8.0) / 50.0, 0.0, 1.0);
                LeftWingTranslate.X = (76.0 - _leftWingWidth) * 0.35;
                LeftWingContainer.IsHitTestVisible = _leftWingWidth > 32.0;
            }
            else
            {
                LeftWingContainer.Visibility = Visibility.Collapsed;
                LeftWingContainer.Opacity = 0.0;
                LeftWingTranslate.X = 0;
                LeftWingContainer.IsHitTestVisible = false;
            }

            if (_rightWingWidth > 1.0)
            {
                RightWingContainer.Visibility = Visibility.Visible;
                RightWingContainer.Opacity = Math.Clamp((_rightWingWidth - 8.0) / 50.0, 0.0, 1.0);
                RightWingTranslate.X = (_rightWingWidth - 76.0) * 0.35;
                RightWingContainer.IsHitTestVisible = _rightWingWidth > 32.0;
            }
            else
            {
                RightWingContainer.Visibility = Visibility.Collapsed;
                RightWingContainer.Opacity = 0.0;
                RightWingTranslate.X = 0;
                RightWingContainer.IsHitTestVisible = false;
            }

            double bodyWidth = Math.Max(48, _morph.Width.Current);
            double height = Math.Max(4, _morph.Height.Current);
            double bottomRadius = Math.Clamp(_morph.BottomCornerRadius.Current, 2, height * 0.65);
            double topEarRadius = Math.Clamp(_morph.TopEarRadius.Current, 2, height * 0.55);

            double contentLeft = (Width - bodyWidth) / 2.0;
            IslandBorder.Margin = new Thickness(contentLeft, 0, 0, 0);
            IslandBorder.Width = bodyWidth;
            IslandBorder.Height = height;

            double xLeftBody = contentLeft - _leftWingWidth;
            double xRightBody = contentLeft + bodyWidth + _rightWingWidth;

            AmbientGlowBorder.Margin = new Thickness(xLeftBody, 0, 0, 0);
            AmbientGlowBorder.Width = Math.Max(10, xRightBody - xLeftBody);
            AmbientGlowBorder.Height = height;
            AmbientGlowBorder.CornerRadius = new CornerRadius(0, 0, bottomRadius, bottomRadius);
            AmbientGlowBorder.Opacity = _morph.GlowOpacity.Current;

            UpdateUnifiedNotchGeometry(xLeftBody, xRightBody, height, bottomRadius, topEarRadius);
            UpdateWindowClickThroughState();

            bool isCompactMedia =
                _stateMachine.CurrentState == IslandVisualState.CompactMedia &&
                _mediaService.CurrentTrack.HasActiveSession;

            bool isExpandedMedia =
                _stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics &&
                _mediaService.CurrentTrack.HasActiveSession;

            if (isCompactMedia)
            {
                _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 18.0);
                for (int i = 0; i < _waveRectangles.Length; i++)
                {
                    _waveRectangles[i].Height = _spectrumHeights[i];
                }

                UpdateCompactLyricSlowPan(dt);
            }
            else if (isExpandedMedia)
            {
                // Drive real-time WASAPI Loopback 5-band waveform in Expanded Music Player too!
                _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 15.0);
                for (int i = 0; i < _expWaveRectangles.Length; i++)
                {
                    _expWaveRectangles[i].Height = _spectrumHeights[i];
                }

                float peak = _audioService.GetCurrentPeakLevel(_mediaService.CurrentTrack.IsPlaying);
                LyricsStageGlowOrb.Opacity = 0.14 + Math.Clamp(peak * 0.45, 0.0, 0.35);
                LyricsStageGlowOrb.Width = 180 + peak * 95;
            }

            bool wingsActive = Math.Abs(_targetLeftWingWidth - _leftWingWidth) > 0.05 ||
                               Math.Abs(_targetRightWingWidth - _rightWingWidth) > 0.05 ||
                               _leftWingWidth > 0.05 ||
                               _rightWingWidth > 0.05;

            if (!morphActive && !wingsActive && !isCompactMedia && !isExpandedMedia)
            {
                StopRenderLoop();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("RenderLoop", "Error during OnCompositionTargetRendering", ex);
        }
    }

    private void UpdateUnifiedNotchGeometry(double xLeftBody, double xRightBody, double height, double bottomRadius, double topEarRadius)
    {
        if (topEarRadius + bottomRadius > height)
        {
            double scale = height / Math.Max(1.0, topEarRadius + bottomRadius);
            topEarRadius *= scale;
            bottomRadius *= scale;
        }

        double xLeftEar = Math.Max(0.0, xLeftBody - topEarRadius);
        double xRightEar = Math.Min(Width, xRightBody + topEarRadius);

        var fillGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeom.Open())
        {
            BuildNotchContourDirect(ctx, xLeftEar, xLeftBody, xRightBody, xRightEar, height, topEarRadius, bottomRadius, isFilled: true, isClosed: true);
        }
        fillGeom.Freeze();

        UnifiedNotchPath.Data = fillGeom;

        var rimGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = rimGeom.Open())
        {
            BuildNotchContourDirect(ctx, xLeftEar, xLeftBody, xRightBody, xRightEar, height, topEarRadius, bottomRadius, isFilled: false, isClosed: false);
        }
        rimGeom.Freeze();

        UnifiedNotchRimPath.Data = rimGeom;
    }

    private static void BuildNotchContourDirect(
        StreamGeometryContext ctx,
        double xLeftEar,
        double xLeftBody,
        double xRightBody,
        double xRightEar,
        double height,
        double topEarRadius,
        double bottomRadius,
        bool isFilled,
        bool isClosed)
    {
        ctx.BeginFigure(new Point(xLeftEar, 0), isFilled: isFilled, isClosed: isClosed);

        ctx.QuadraticBezierTo(
            new Point(xLeftBody, 0),
            new Point(xLeftBody, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xLeftBody, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xLeftBody, height),
            new Point(xLeftBody + bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRightBody - bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRightBody, height),
            new Point(xRightBody, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRightBody, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRightBody, 0),
            new Point(xRightEar, 0),
            isStroked: true,
            isSmoothJoin: true);
    }

    private void UpdateCompactLyricSlowPan(double dt)
    {
        double textWidth = CompactSongTitleText.ActualWidth;
        double viewportWidth = CompactLyricViewport.ActualWidth;

        if (viewportWidth <= 10 || textWidth <= viewportWidth + 2)
        {
            _currentPanX += (0 - _currentPanX) * Math.Min(1.0, dt * 8.0);
            CompactLyricTranslate.X = _currentPanX;
            return;
        }

        double maxOverflow = textWidth - viewportWidth + 10;
        double normalizedPan = Math.Clamp((_currentLyricLineProgress - 0.15) / 0.73, 0.0, 1.0);
        double targetX = -maxOverflow * normalizedPan;

        _currentPanX += (targetX - _currentPanX) * Math.Min(1.0, dt * 4.5);
        CompactLyricTranslate.X = _currentPanX;
    }

    private void OnFastUpdateTick(object? sender, EventArgs e)
    {
        try
        {
            _hardwareService.PollFastKeyboardIndicators();
            _audioService.PollExternalChanges();
            _brightnessService.PollExternalChanges();
            _notificationService.PollNewNotifications();

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness)
            {
                UpdateSlidersAndMuteUi();
            }

            if (_mediaService.CurrentTrack.HasActiveSession)
            {
                UpdateMediaAndLyricsUi();
            }

            if (_stateMachine.CurrentState == IslandVisualState.TuckedTopEdge &&
                Win32Interop.GetCursorPos(out var pt))
            {
                int centerPx = (int)(SystemParameters.PrimaryScreenWidth / 2);
                if (pt.Y <= 2 && Math.Abs(pt.X - centerPx) <= 85)
                {
                    _topBezelHoldTicks++;
                    if (_topBezelHoldTicks >= 3)
                    {
                        _topBezelHoldTicks = 0;
                        _stateMachine.SetHovered(true);
                    }
                }
                else
                {
                    _topBezelHoldTicks = 0;
                }
            }
            else
            {
                _topBezelHoldTicks = 0;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("FastUpdateTick", "Error during OnFastUpdateTick", ex);
        }
    }

    private void OnOneSecondTick(object? sender, EventArgs e)
    {
        try
        {
            if (_autoHideFullScreen)
            {
                bool fullScreen = Win32Interop.IsFullScreenAppActive();
                bool wasFullScreen = _stateMachine.IsFullScreenHidden;
                _stateMachine.SetFullScreenHidden(fullScreen);

                if (wasFullScreen && !fullScreen)
                {
                    AppLogger.LogInfo("FullScreenCheck", "Exited full screen app -> Scheduling reposition.");
                    ScheduleDebouncedReposition();
                }
            }

            if (_hwnd != IntPtr.Zero && _stateMachine.CurrentState != IslandVisualState.Hidden)
            {
                // Self-healing: verify physical window position matches monitor center
                IntPtr hMonitor = Win32Interop.MonitorFromWindow(_hwnd, Win32Interop.MONITOR_DEFAULTTOPRIMARY);
                var mi = new Win32Interop.MONITORINFO();
                mi.cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>();
                if (Win32Interop.GetMonitorInfo(hMonitor, ref mi) && Win32Interop.GetWindowRect(_hwnd, out var currentRect))
                {
                    int monitorCenterPx = mi.rcMonitor.Left + (mi.rcMonitor.Right - mi.rcMonitor.Left) / 2;
                    int windowCenterPx = (currentRect.Left + currentRect.Right) / 2;
                    if (Math.Abs(windowCenterPx - monitorCenterPx) > 4)
                    {
                        AppLogger.LogWarning("OneSecondTick",
                            $"Window misaligned detected! WindowCenter={windowCenterPx}, MonitorCenter={monitorCenterPx}, Diff={windowCenterPx - monitorCenterPx}. Auto self-healing PositionTopCenter.");
                        PositionTopCenter();
                    }
                    else
                    {
                        Win32Interop.SetWindowPos(
                            _hwnd,
                            Win32Interop.HWND_TOPMOST,
                            0, 0, 0, 0,
                            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
                    }
                }
                else
                {
                    Win32Interop.SetWindowPos(
                        _hwnd,
                        Win32Interop.HWND_TOPMOST,
                        0, 0, 0, 0,
                        Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
                }
            }

            _pomodoroService.TickOneSecond();
            UpdatePomodoroAndClockUi();

            _hardwareService.RefreshFullTelemetry();
            UpdateHardwareUi();

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness)
            {
                UpdateSlidersAndMuteUi();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("OneSecondTick", "Error during OnOneSecondTick", ex);
        }
    }

    // --- VOLUME, BRIGHTNESS & MUTE OSD TOAST HELPERS ---

    private void ShowVolumeToast(int volumePercent, bool isMuted, bool muteToggled)
    {
        UpdateSlidersAndMuteUi();

        bool isHeadphones = _audioService.IsHeadphoneOrTws;
        string devName = _audioService.ActiveOutputDeviceName;

        string icon = isMuted || volumePercent == 0
            ? "MUTE"
            : isHeadphones ? "HEADPHONES" : "SPEAKER";

        string title = isMuted
            ? $"{devName}: Muted"
            : $"{devName} • {volumePercent}%";

        string sub = isMuted
            ? "Audio output dibisukan"
            : isHeadphones
                ? "Output Earphone / TWS aktif"
                : muteToggled ? "Audio output aktif kembali" : "Geser slider atau scroll mouse";

        string accent = isMuted ? "#F87171" : isHeadphones ? "#A855F7" : "#38BDF8";

        TriggerToast(new ToastPayload(
            Icon: icon,
            Title: title,
            Subtitle: sub,
            AccentHex: accent,
            ProgressRatio: isMuted ? 0.0 : volumePercent / 100.0,
            Duration: TimeSpan.FromSeconds(1.8),
            SliderKind: ToastSliderKind.Volume));
    }

    private void ShowBrightnessToast(int brightnessPercent)
    {
        UpdateSlidersAndMuteUi();

        TriggerToast(new ToastPayload(
            Icon: "SUN",
            Title: $"Kecerahan {brightnessPercent}%",
            Subtitle: "Layar Monitor • Shift+Scroll atau geser",
            AccentHex: "#FBBF24",
            ProgressRatio: brightnessPercent / 100.0,
            Duration: TimeSpan.FromSeconds(1.8),
            SliderKind: ToastSliderKind.Brightness));
    }

    private void ShowMicMuteToast(bool isMicMuted)
    {
        UpdateSlidersAndMuteUi();

        TriggerToast(new ToastPayload(
            Icon: isMicMuted ? "MIC_MUTE" : "MIC_ON",
            Title: isMicMuted ? "Mikrofon: MUTED" : "Mikrofon: ON (Aktif)",
            Subtitle: isMicMuted ? "Suara mikrofon Anda dibisukan" : "Mikrofon siap menerima suara",
            AccentHex: isMicMuted ? "#F87171" : "#22C55E",
            Duration: TimeSpan.FromSeconds(2.2)));
    }

    private Geometry GetVectorGeometry(string resourceKey)
    {
        return TryFindResource(resourceKey) as Geometry ?? Geometry.Empty;
    }

    private Geometry ResolveToastVectorGeometry(string iconCode, ToastSliderKind sliderKind)
    {
        if (sliderKind == ToastSliderKind.Brightness || iconCode == "SUN" || iconCode.StartsWith("BRIGHTNESS"))
            return GetVectorGeometry("GeomBrightnessSun");

        if (sliderKind == ToastSliderKind.Battery || iconCode == "BATTERY" || iconCode.StartsWith("BATTERY") || iconCode == "BOLT")
            return GetVectorGeometry("GeomBolt");

        if (iconCode == "MUTE" || (iconCode.Contains("MUTE") && !iconCode.Contains("MIC")))
            return GetVectorGeometry("GeomSpeakerMuted");

        if (iconCode == "HEADPHONES" || iconCode == "BLUETOOTH" || iconCode.StartsWith("BT"))
            return GetVectorGeometry("GeomHeadphones");

        if (iconCode == "SPEAKER")
            return GetVectorGeometry("GeomSpeakerOn");

        if (iconCode == "MIC_MUTE")
            return GetVectorGeometry("GeomMicMuted");

        if (iconCode == "MIC_ON" || iconCode == "MIC")
            return GetVectorGeometry("GeomMicOn");

        if (iconCode == "WIFI")
            return GetVectorGeometry("GeomWifi");

        if (iconCode == "WIFI_OFF")
            return GetVectorGeometry("GeomWifiOff");

        if (iconCode == "FOLDER" || iconCode == "DROP_SHELF")
            return GetVectorGeometry("GeomFolderTray");

        if (iconCode == "POMO_FOCUS" || iconCode == "POMO_BREAK" || iconCode.StartsWith("TIMER") || iconCode.StartsWith("POMO"))
            return GetVectorGeometry("GeomFocusTimer");

        return GetVectorGeometry("GeomBellNotif");
    }

    private void UpdateSlidersAndMuteUi()
    {
        int vol = _audioService.GetMasterVolumePercent();
        bool soundMuted = _audioService.IsMuted();
        bool micMuted = _audioService.IsMicrophoneMuted();
        bool isHeadphones = _audioService.IsHeadphoneOrTws;
        int brt = _brightnessService.GetBrightnessPercent();

        SysVolumePercentText.Text = soundMuted ? "MUTED" : $"{vol}%";
        var volColor = soundMuted
            ? Color.FromRgb(248, 113, 113)
            : isHeadphones
                ? Color.FromRgb(192, 132, 252)
                : Color.FromRgb(56, 189, 248);

        var volBrush = new SolidColorBrush(volColor);
        SysVolumePercentText.Foreground = volBrush;
        SysVolumeHeaderIconPath.Fill = volBrush;
        SysVolumeHeaderIconPath.Data = soundMuted
            ? GetVectorGeometry("GeomSpeakerMuted")
            : isHeadphones
                ? GetVectorGeometry("GeomHeadphones")
                : GetVectorGeometry("GeomSpeakerOn");

        SysVolumeLabelText.Text = soundMuted
            ? "AUDIO MUTED"
            : _audioService.ActiveOutputDeviceName.ToUpperInvariant();

        double volTrackW = SysVolumeTrackBorder.ActualWidth > 10 ? SysVolumeTrackBorder.ActualWidth : 185.0;
        double volFillPx = volTrackW * (soundMuted ? 0.0 : Math.Clamp(vol / 100.0, 0.0, 1.0));
        SysVolumeFillBorder.Width = volFillPx;
        SysVolumeFillBorder.Background = volBrush;

        SysBrightnessPercentText.Text = $"{brt}%";
        double brtTrackW = SysBrightnessTrackBorder.ActualWidth > 10 ? SysBrightnessTrackBorder.ActualWidth : 185.0;
        double brtFillPx = brtTrackW * Math.Clamp(brt / 100.0, 0.0, 1.0);
        SysBrightnessFillBorder.Width = brtFillPx;

        ToggleMuteIconPath.Data = soundMuted
            ? GetVectorGeometry("GeomSpeakerMuted")
            : GetVectorGeometry("GeomSpeakerOn");
        var soundBtnBrush = new SolidColorBrush(
            soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(56, 189, 248));
        ToggleMuteIconPath.Fill = soundBtnBrush;
        ToggleMuteText.Text = soundMuted ? "Sound: MUTED" : "Sound: ON";
        ToggleMuteText.Foreground = new SolidColorBrush(
            soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(248, 250, 252));

        ToggleMicIconPath.Data = micMuted
            ? GetVectorGeometry("GeomMicMuted")
            : GetVectorGeometry("GeomMicOn");
        var micBtnBrush = new SolidColorBrush(
            micMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(34, 197, 94));
        ToggleMicIconPath.Fill = micBtnBrush;
        ToggleMicText.Text = micMuted ? "Mic: MUTED" : "Mic: ON";
        ToggleMicText.Foreground = micBtnBrush;
    }

    private void UpdatePomodoroAndClockUi()
    {
        string nowStr = DateTime.Now.ToString("HH:mm");
        ExpandedClockBadge.Text = nowStr;

        if (_pomodoroService.IsRunning)
        {
            ClockTextBlock.Text = _pomodoroService.FormattedRemaining;
            IdlePulseDot.Fill = new SolidColorBrush(Color.FromRgb(249, 115, 22));
        }
        else
        {
            ClockTextBlock.Text = nowStr;
            IdlePulseDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }

        PomodoroTimeText.Text = _pomodoroService.FormattedRemaining;
        PomodoroModeLabel.Text = _pomodoroService.IsBreakMode ? "WAKTU ISTIRAHAT" : "SESI FOKUS";
        PomodoroModeLabel.Foreground = new SolidColorBrush(
            _pomodoroService.IsBreakMode ? Color.FromRgb(34, 197, 94) : Color.FromRgb(56, 189, 248));

        PomodoroPlayPauseIconPath.Data = _pomodoroService.IsRunning
            ? GetVectorGeometry("GeomPause")
            : GetVectorGeometry("GeomPlay");
        PomodoroStartPauseText.Text = _pomodoroService.IsRunning ? "Jeda" : "Mulai";

        UpdatePomodoroProgressRing(_pomodoroService.ProgressRatio);
    }

    private void UpdatePomodoroProgressRing(double progressRatio)
    {
        try
        {
            double radius = 38.0;
            double cx = 43.0;
            double cy = 43.0;

            double ratio = Math.Clamp(progressRatio, 0.002, 0.999);
            double angleDeg = ratio * 360.0;
            double angleRad = (angleDeg - 90.0) * Math.PI / 180.0;

            double startX = cx;
            double startY = cy - radius;

            double endX = cx + radius * Math.Cos(angleRad);
            double endY = cy + radius * Math.Sin(angleRad);

            bool isLargeArc = angleDeg > 180.0;

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(startX, startY), isFilled: false, isClosed: false);
                ctx.ArcTo(new Point(endX, endY), new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();
            PomodoroProgressRingPath.Data = geom;
        }
        catch { }
    }

    private void UpdateHardwareUi()
    {
        var hw = _hardwareService.Current;

        IdleBatteryPercentText.Text = $"{hw.BatteryPercent}%";
        double idleBatRatio = Math.Clamp(hw.BatteryPercent / 100.0, 0.08, 1.0);
        IdleBatteryFillBar.Width = 20.5 * idleBatRatio;

        Color batColor = hw.IsCharging
            ? Color.FromRgb(34, 197, 94)
            : hw.BatteryPercent <= 20
                ? Color.FromRgb(239, 68, 68)
                : Color.FromRgb(245, 158, 11);

        IdleBatteryFillBar.Background = new SolidColorBrush(batColor);
        IdleBatteryBoltPath.Fill = new SolidColorBrush(
            hw.IsCharging ? Color.FromRgb(254, 240, 138) : Color.FromRgb(248, 250, 252));

        CameraPrivacyDot.Visibility = hw.IsCameraInUse ? Visibility.Visible : Visibility.Collapsed;
        MicPrivacyDot.Visibility = hw.IsMicrophoneInUse ? Visibility.Visible : Visibility.Collapsed;

        bool hasExtraIdle = hw.IsCameraInUse || hw.IsMicrophoneInUse || _pomodoroService.IsRunning;
        _stateMachine.UpdateDynamicIdleWidth(hasExtraIdle);

        SysBatteryDetailText.Text = hw.HasBattery
            ? $"{(hw.IsCharging ? "Charging" : "Baterai")} • {hw.BatteryPercent}%"
            : "Daya Desktop AC (100%)";

        SysNetworkDetailText.Text = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName)
            ? $"{hw.ConnectedBluetoothName} • {hw.NetworkName}"
            : hw.NetworkName;

        if (hw.IsCameraInUse || hw.IsMicrophoneInUse)
        {
            string activeDevices = (hw.IsCameraInUse && hw.IsMicrophoneInUse)
                ? "Kamera & Mic Aktif"
                : hw.IsCameraInUse ? "Kamera Aktif" : $"Mic Aktif ({hw.ActiveMicApp})";
            var warnBrush = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            SysPrivacyDetailText.Text = activeDevices;
            SysPrivacyDetailText.Foreground = warnBrush;
            SysPrivacyIconPath.Fill = warnBrush;
        }
        else
        {
            var safeBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SysPrivacyDetailText.Text = "Kamera & Mic Aman (Idle)";
            SysPrivacyDetailText.Foreground = safeBrush;
            SysPrivacyIconPath.Fill = safeBrush;
        }

        SysMemoryDetailText.Text = $"RAM: {hw.AppMemoryMb:0.0} MB • {(_isRenderLoopAttached ? "Active 60FPS" : "0% CPU Idle")}";
    }

    private void OnMediaTrackChanged(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _lastAnimatedLyricIndex = -999;
            bool hasSession = track.HasActiveSession && track.Title != "Tidak Ada Media";
            _stateMachine.SetMediaActive(hasSession);

            ApplyDominantColor(track.DominantColor);
            UpdateMediaAndLyricsUi();

            if (hasSession)
            {
                await _lyricsService.LoadLyricsForTrackAsync(
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.Duration.TotalSeconds);
            }
        });
    }

    private void OnMediaPlaybackStateChanged(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(() =>
        {
            bool hasSession = track.HasActiveSession && track.Title != "Tidak Ada Media";
            _stateMachine.SetMediaActive(hasSession);
            UpdateMediaAndLyricsUi();
            EnsureRenderLoopRunning();
        });
    }

    private void ApplyDominantColor(Color c)
    {
        AmbientGlowBrush.Color = c;
        LyricsStageOrbBrush.Color = c;
        ActiveLyricGlowEffect.Color = c;
        AlbumShadowEffect.Color = c;

        var barBrush = new SolidColorBrush(c);
        TimelineFillBorder.Background = barBrush;
        LyricLineProgressFill.Background = barBrush;
        TuckedAccentBar.Background = barBrush;

        foreach (var rect in _waveRectangles)
        {
            rect.Fill = barBrush;
        }

        foreach (var rect in _expWaveRectangles)
        {
            rect.Fill = barBrush;
        }
    }

    private void UpdateMediaAndLyricsUi()
    {
        var track = _mediaService.CurrentTrack;

        CompactAlbumImage.Source = track.Thumbnail;
        ExpandedAlbumImage.Source = track.Thumbnail;
        ExpandedSongTitleText.Text = track.Title;
        ExpandedArtistText.Text = track.Artist;

        PlayPauseIconPath.Data = track.IsPlaying
            ? GetVectorGeometry("GeomPause")
            : GetVectorGeometry("GeomPlay");

        // Active output destination badge with custom Vector Icon + Clean Device Name
        string devName = _audioService.ActiveOutputDeviceName;
        bool isHeadphones = _audioService.IsHeadphoneOrTws;
        AudioDeviceIconPath.Data = isHeadphones
            ? GetVectorGeometry("GeomHeadphones")
            : GetVectorGeometry("GeomSpeakerOn");

        var badgeAccent = isHeadphones
            ? Color.FromRgb(192, 132, 252)
            : Color.FromRgb(56, 189, 248);
        var badgeFgBrush = new SolidColorBrush(badgeAccent);

        AudioDeviceIconPath.Fill = badgeFgBrush;
        AudioDeviceBadgeText.Text = devName;
        AudioDeviceBadgeText.Foreground = badgeFgBrush;
        AudioDeviceBadge.Background = new SolidColorBrush(
            isHeadphones ? Color.FromArgb(38, 168, 85, 247) : Color.FromArgb(34, 56, 189, 248));
        AudioDeviceBadge.BorderBrush = new SolidColorBrush(
            isHeadphones ? Color.FromArgb(65, 168, 85, 247) : Color.FromArgb(60, 56, 189, 248));

        TimeSpan dur = track.Duration;
        TimeSpan pos = _isDraggingTimeline && dur.TotalSeconds > 0
            ? TimeSpan.FromSeconds(dur.TotalSeconds * _draggedTimelineRatio)
            : _mediaService.GetInterpolatedPosition();

        ElapsedTimeText.Text = FormatTimeSpan(pos);
        TotalDurationText.Text = FormatTimeSpan(dur);

        double trackWidth = Math.Max(0, TimelineTrackBorder.ActualWidth);
        if (dur.TotalSeconds > 0.5 && trackWidth > 0)
        {
            double ratio = _isDraggingTimeline
                ? _draggedTimelineRatio
                : Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
            double fillPx = trackWidth * ratio;
            TimelineFillBorder.Width = fillPx;
        }
        else
        {
            TimelineFillBorder.Width = 0;
        }

        var lyricSnap = _lyricsService.GetActiveLyrics(pos);
        _currentLyricLineProgress = lyricSnap.LineProgressRatio;

        LyricLineProgressFill.Width = 120.0 * Math.Clamp(lyricSnap.LineProgressRatio, 0.0, 1.0);

        string compactTargetText;
        if (lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.CurrentLine) && lyricSnap.CurrentLine != "• • •")
        {
            compactTargetText = lyricSnap.CurrentLine;
        }
        else if (lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.NextLine))
        {
            compactTargetText = lyricSnap.NextLine;
        }
        else
        {
            compactTargetText = track.IsPlaying
                ? $"{track.Title} • {track.Artist}"
                : track.Title;
        }

        if (compactTargetText != _lastDisplayedCompactText)
        {
            _lastDisplayedCompactText = compactTargetText;
            CompactSongTitleText.Text = compactTargetText;
            _currentPanX = 0;
            CompactLyricTranslate.X = 0;

            _stateMachine.UpdateDynamicCompactWidth(compactTargetText.Length);

            var slideUp = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(340))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0.20, 1.0, TimeSpan.FromMilliseconds(300));
            CompactLyricTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            CompactSongTitleText.BeginAnimation(OpacityProperty, fadeIn);
        }

        if (lyricSnap.LineIndex != _lastAnimatedLyricIndex || CurrentLyricLineText.Text != lyricSnap.CurrentLine)
        {
            _lastAnimatedLyricIndex = lyricSnap.LineIndex;
            PrevLyricLineText.Text = lyricSnap.PreviousLine;
            CurrentLyricLineText.Text = lyricSnap.CurrentLine;
            NextLyricLineText.Text = lyricSnap.NextLine;

            AnimateExpandedLyricsTransition();
        }
    }

    private void AnimateExpandedLyricsTransition()
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var springBack = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        var stageSlide = new DoubleAnimation(16.0, 0.0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = easeOut
        };
        LyricsStageTranslate.BeginAnimation(TranslateTransform.YProperty, stageSlide);

        var scalePop = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = springBack
        };
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleXProperty, scalePop);
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleYProperty, scalePop);

        var fade = new DoubleAnimation(0.20, 1.0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = easeOut
        };
        CurrentLyricLineText.BeginAnimation(OpacityProperty, fade);
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public void TriggerToast(ToastPayload toast)
    {
        _activeToastSliderKind = toast.SliderKind;
        ToastTitleText.Text = toast.Title;
        ToastSubtitleText.Text = toast.Subtitle;

        Color accent;
        try
        {
            accent = (Color)ColorConverter.ConvertFromString(toast.AccentHex);
        }
        catch
        {
            accent = Color.FromRgb(56, 189, 248);
        }

        var accentBrush = new SolidColorBrush(accent);
        ToastIconPath.Data = ResolveToastVectorGeometry(toast.Icon, toast.SliderKind);
        ToastIconPath.Fill = accentBrush;
        ToastIconBadge.Background = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B));
        ToastIconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(68, accent.R, accent.G, accent.B));
        AmbientGlowBrush.Color = accent;

        if (toast.SliderKind == ToastSliderKind.Battery)
        {
            // Render realistic Vector Battery Graphic with Lightning Bolt centered inside
            ToastProgressContainer.Visibility = Visibility.Collapsed;
            ToastBatteryContainer.Visibility = Visibility.Visible;

            double batRatio = Math.Clamp(toast.ProgressRatio ?? 1.0, 0.08, 1.0);
            ToastBatteryFill.Width = 46.5 * batRatio;
            ToastBatteryFill.Background = accentBrush;
            ToastBatteryBoltPath.Fill = new SolidColorBrush(
                toast.IsBatteryCharging ? Color.FromRgb(254, 240, 138) : Color.FromRgb(255, 255, 255));
        }
        else if (toast.ProgressRatio.HasValue)
        {
            ToastBatteryContainer.Visibility = Visibility.Collapsed;
            ToastProgressContainer.Visibility = Visibility.Visible;
            double ratio = Math.Clamp(toast.ProgressRatio.Value, 0.0, 1.0);
            double fillPx = 112.0 * ratio;
            ToastProgressFill.Width = fillPx;
            ToastProgressFill.Background = accentBrush;
        }
        else
        {
            ToastProgressContainer.Visibility = Visibility.Collapsed;
            ToastBatteryContainer.Visibility = Visibility.Collapsed;
        }

        _stateMachine.ShowToast(toast);

        _toastDismissTimer.Stop();
        _toastDismissTimer.Interval = toast.Duration ?? TimeSpan.FromSeconds(2.2);
        _toastDismissTimer.Start();
    }

    // --- INTERACTIVE TOAST SLIDER HANDLERS (VOLUME / BRIGHTNESS IN TOAST PILL) ---

    private void OnToastSliderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeToastSliderKind == ToastSliderKind.None) return;
        _isDraggingToastSlider = true;
        _toastDismissTimer.Stop();
        ToastProgressContainer.CaptureMouse();
        ApplyToastSliderFromMouse(e.GetPosition(ToastProgressTrack).X);
        e.Handled = true;
    }

    private void OnToastSliderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingToastSlider) return;
        ApplyToastSliderFromMouse(e.GetPosition(ToastProgressTrack).X);
        e.Handled = true;
    }

    private void OnToastSliderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingToastSlider) return;
        ApplyToastSliderFromMouse(e.GetPosition(ToastProgressTrack).X);
        _isDraggingToastSlider = false;
        ToastProgressContainer.ReleaseMouseCapture();
        _toastDismissTimer.Interval = TimeSpan.FromSeconds(1.6);
        _toastDismissTimer.Start();
        e.Handled = true;
    }

    private void ApplyToastSliderFromMouse(double mouseX)
    {
        double ratio = Math.Clamp(mouseX / 112.0, 0.0, 1.0);
        int pct = (int)Math.Round(ratio * 100);

        if (_activeToastSliderKind == ToastSliderKind.Volume)
        {
            int newVol = _audioService.SetMasterVolumePercent(pct);
            ShowVolumeToast(newVol, isMuted: false, muteToggled: false);
        }
        else if (_activeToastSliderKind == ToastSliderKind.Brightness)
        {
            int newBrt = _brightnessService.SetBrightnessPercent(pct);
            ShowBrightnessToast(newBrt);
        }
    }

    // --- INTERACTIVE SYSTEM TAB VOLUME & BRIGHTNESS SLIDER HANDLERS ---

    private void OnSysVolumeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSysVolume = true;
        SysVolumeSliderHitArea.CaptureMouse();
        ApplySysVolumeFromMouse(e.GetPosition(SysVolumeTrackBorder).X);
        e.Handled = true;
    }

    private void OnSysVolumeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSysVolume) return;
        ApplySysVolumeFromMouse(e.GetPosition(SysVolumeTrackBorder).X);
        e.Handled = true;
    }

    private void OnSysVolumeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSysVolume) return;
        ApplySysVolumeFromMouse(e.GetPosition(SysVolumeTrackBorder).X);
        _isDraggingSysVolume = false;
        SysVolumeSliderHitArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ApplySysVolumeFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, SysVolumeTrackBorder.ActualWidth);
        int pct = (int)Math.Round(Math.Clamp(mouseX / trackW, 0.0, 1.0) * 100);
        _audioService.SetMasterVolumePercent(pct);
        UpdateSlidersAndMuteUi();
    }

    private void OnSysBrightnessMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSysBrightness = true;
        SysBrightnessSliderHitArea.CaptureMouse();
        ApplySysBrightnessFromMouse(e.GetPosition(SysBrightnessTrackBorder).X);
        e.Handled = true;
    }

    private void OnSysBrightnessMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSysBrightness) return;
        ApplySysBrightnessFromMouse(e.GetPosition(SysBrightnessTrackBorder).X);
        e.Handled = true;
    }

    private void OnSysBrightnessMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSysBrightness) return;
        ApplySysBrightnessFromMouse(e.GetPosition(SysBrightnessTrackBorder).X);
        _isDraggingSysBrightness = false;
        SysBrightnessSliderHitArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ApplySysBrightnessFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, SysBrightnessTrackBorder.ActualWidth);
        int pct = (int)Math.Round(Math.Clamp(mouseX / trackW, 0.0, 1.0) * 100);
        _brightnessService.SetBrightnessPercent(pct);
        UpdateSlidersAndMuteUi();
    }

    // --- INTERACTIVE TIMELINE SCRUBBER HANDLERS ---

    private void OnTimelineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mediaService.CurrentTrack.HasActiveSession || _mediaService.CurrentTrack.Duration.TotalSeconds <= 0.5)
            return;

        _isDraggingTimeline = true;
        TimelineScrubberHitArea.CaptureMouse();
        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        e.Handled = true;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTimeline) return;
        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        e.Handled = true;
    }

    private async void OnTimelineMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTimeline) return;

        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        _isDraggingTimeline = false;
        TimelineScrubberHitArea.ReleaseMouseCapture();

        TimeSpan dur = _mediaService.CurrentTrack.Duration;
        if (dur.TotalSeconds > 0.5)
        {
            TimeSpan target = TimeSpan.FromSeconds(dur.TotalSeconds * _draggedTimelineRatio);
            await _mediaService.SeekToPositionAsync(target);
            UpdateMediaAndLyricsUi();
        }

        e.Handled = true;
    }

    private void UpdateTimelineRatioFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, TimelineTrackBorder.ActualWidth);
        _draggedTimelineRatio = Math.Clamp(mouseX / trackW, 0.0, 1.0);
        UpdateMediaAndLyricsUi();
    }

    // --- MOUSE & GESTURE HANDLERS ---

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDebounceTimer.Stop();

        if (_stateMachine.CurrentState is IslandVisualState.TuckedTopEdge or IslandVisualState.ToastNotification)
        {
            return;
        }

        _stateMachine.SetHovered(true);
        UpdateMediaAndLyricsUi();
    }

    private void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingToastSlider)
        {
            OnToastSliderMouseMove(sender, e);
            return;
        }

        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
        {
            UpdateSideWingHover();
        }
    }

    private void OnRootCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
        {
            UpdateSideWingHover();
        }
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateSideWingHover();
        _collapseDebounceTimer.Stop();
        _collapseDebounceTimer.Start();
    }

    private void UpdateSideWingHover()
    {
        if (_stateMachine.CurrentState != IslandVisualState.ExpandedDashboard)
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 0.0;
            _hoveredSideZone = 0;
            return;
        }

        if (_hwnd == IntPtr.Zero || !Win32Interop.GetCursorPos(out var pt))
            return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dipX = (pt.X / dpi.DpiScaleX) - Left;
        double dipY = (pt.Y / dpi.DpiScaleY) - Top;

        // In 760px window, main notch is 540px centered: left edge = 110, right edge = 650. Height = 260.
        bool withinY = dipY >= 0 && dipY <= 285;

        if (!withinY)
        {
            _hoveredSideZone = 0;
        }
        else
        {
            // If cursor is at the very top (dipY <= 46), it is hovering over the top tab navigation bar (Musik, Tugas, Fokus, Rak, Sistem, Simpan).
            // Avoid triggering wings here unless cursor is genuinely off to the sides (dipX < 110 or dipX > 650).
            bool isTopBarArea = dipY <= 46;

            if (_hoveredSideZone == -1)
            {
                // Left wing is active: keep active until cursor moves well inside (dipX > 195) or too far left (dipX < 5)
                if (dipX > 195 || dipX < 5) _hoveredSideZone = 0;
            }
            else if (_hoveredSideZone == 1)
            {
                // Right wing is active: keep active until cursor moves well inside (dipX < 565) or too far right (dipX > 755)
                if (dipX < 565 || dipX > 755) _hoveredSideZone = 0;
            }
            else
            {
                // Trigger Left Wing:
                // Either cursor is outside left edge (dipX < 110)
                // OR cursor is on left margin of dashboard (dipX <= 165) and NOT clicking the top tab bar buttons
                if (dipX >= 5 && (dipX < 110 || (!isTopBarArea && dipX <= 165)))
                {
                    _hoveredSideZone = -1;
                }
                // Trigger Right Wing:
                // Either cursor is outside right edge (dipX > 650)
                // OR cursor is on right margin of dashboard (dipX >= 595) and NOT clicking the top Simpan button
                else if (dipX <= 755 && (dipX > 650 || (!isTopBarArea && dipX >= 595)))
                {
                    _hoveredSideZone = 1;
                }
                else
                {
                    _hoveredSideZone = 0;
                }
            }
        }

        if (_hoveredSideZone == -1)
        {
            _targetLeftWingWidth = 76.0;
            _targetRightWingWidth = 0.0;
        }
        else if (_hoveredSideZone == 1)
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 76.0;
        }
        else
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 0.0;
        }

        UpdateSideWingVisuals();

        if (_targetLeftWingWidth > 0 || _targetRightWingWidth > 0 || _leftWingWidth > 0.01 || _rightWingWidth > 0.01)
        {
            EnsureRenderLoopRunning();
        }
    }

    private void UpdateSideWingVisuals()
    {
        try
        {
            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
            {
                var prevTab = GetPrevTab(_stateMachine.ActiveTab);
                var (prevIcon, prevLabel) = GetTabIconAndLabel(prevTab);
                LeftWingIcon.Data = GetVectorGeometry(prevIcon);
                LeftWingLabel.Text = prevLabel;

                var nextTab = GetNextTab(_stateMachine.ActiveTab);
                var (nextIcon, nextLabel) = GetTabIconAndLabel(nextTab);
                RightWingIcon.Data = GetVectorGeometry(nextIcon);
                RightWingLabel.Text = nextLabel;
            }
        }
        catch { }
    }

    private static IslandDashboardTab GetPrevTab(IslandDashboardTab tab)
    {
        int count = Enum.GetValues<IslandDashboardTab>().Length;
        return (IslandDashboardTab)(((int)tab - 1 + count) % count);
    }

    private static IslandDashboardTab GetNextTab(IslandDashboardTab tab)
    {
        int count = Enum.GetValues<IslandDashboardTab>().Length;
        return (IslandDashboardTab)(((int)tab + 1) % count);
    }

    private static (string GeometryKey, string Label) GetTabIconAndLabel(IslandDashboardTab tab) => tab switch
    {
        IslandDashboardTab.MediaLyrics => ("GeomMusic", "Musik"),
        IslandDashboardTab.TodoAndWater => ("GeomChecklist", "Tugas & Air"),
        IslandDashboardTab.Pomodoro => ("GeomFocusTimer", "Fokus"),
        IslandDashboardTab.FileShelf => ("GeomFolderTray", "Rak File"),
        IslandDashboardTab.SystemStatus => ("GeomSliders", "Sistem"),
        _ => ("GeomMusic", "Musik")
    };

    private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingToastSlider) return;

        if (_stateMachine.CurrentState != IslandVisualState.ExpandedDashboard)
        {
            _stateMachine.ClearToast();
            _stateMachine.SetTuckedMode(false);
            _stateMachine.SetHovered(true);
            UpdateMediaAndLyricsUi();
        }
    }

    private void OnIslandMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stateMachine.CurrentState is IslandVisualState.CompactMedia or IslandVisualState.IdlePill)
        {
            _stateMachine.SetTuckedMode(true);
        }
        else
        {
            _explicitSlideDirection = 1;
            _stateMachine.CycleNextTab();
            _stateMachine.SetHovered(true);
        }
        e.Handled = true;
    }

    private async void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            await _mediaService.TogglePlayPauseAsync();
        }
    }

    private void OnIslandMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int step = e.Delta > 0 ? 4 : -4;

        // Hold Shift + Scroll Wheel to adjust Screen Brightness!
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int newBrt = _brightnessService.AdjustBrightness(step);
            ShowBrightnessToast(newBrt);
        }
        else
        {
            int newVol = _audioService.AdjustMasterVolume(step);
            ShowVolumeToast(newVol, isMuted: false, muteToggled: false);
        }

        e.Handled = true;
    }

    // --- DRAG AND DROP FILE SHELF HANDLERS ---

    private void OnIslandDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            _collapseDebounceTimer.Stop();
            _stateMachine.SetTuckedMode(false);
            _stateMachine.SetActiveTab(IslandDashboardTab.FileShelf);
            _stateMachine.SetHovered(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        int addedCount = 0;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            addedCount = _fileShelfService.AddFiles(files);
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                 e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
        {
            addedCount = _fileShelfService.AddBitmapFromDrag(bmp);
        }

        UpdateShelfUi();
        if (addedCount > 0)
        {
            _stateMachine.SetActiveTab(IslandDashboardTab.FileShelf);
            _stateMachine.SetHovered(true);
        }
        e.Handled = true;
    }

    private void OnShelfItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is FrameworkElement fe &&
            fe.DataContext is ShelfItem item)
        {
            var data = new DataObject();
            var collection = new StringCollection { item.FullPath };
            data.SetFileDropList(collection);
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
    }

    private void UpdateShelfUi()
    {
        int count = _fileShelfService.Items.Count;
        _stateMachine.UpdateShelfCount(count);
        EmptyShelfHintPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfHeaderCountText.Text = count == 0
            ? "Taruh file/gambar sementara di sini (Drag Masuk & Keluar)"
            : $"{count} item tersimpan di rak sementara (Tarik keluar untuk kirim)";
    }

    private void OnClearShelfClick(object sender, RoutedEventArgs e)
    {
        _fileShelfService.Clear();
        UpdateShelfUi();
    }

    // --- TAB & BUTTON HANDLERS ---

    private void SwitchToTab(IslandDashboardTab targetTab)
    {
        if (_stateMachine.ActiveTab == targetTab) return;
        _explicitSlideDirection = (int)targetTab > (int)_stateMachine.ActiveTab ? 1 : -1;
        _stateMachine.SetActiveTab(targetTab);
    }

    private void OnTabMediaClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.MediaLyrics);
    private void OnTabTodoClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.TodoAndWater);
    private void OnTabPomodoroClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.Pomodoro);
    private void OnTabShelfClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.FileShelf);
    private void OnTabSystemClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.SystemStatus);
        UpdateSlidersAndMuteUi();
    }

    private void OnLeftWingClick(object sender, RoutedEventArgs e)
    {
        _explicitSlideDirection = -1;
        _stateMachine.CyclePrevTab();
        UpdateSideWingVisuals();
    }

    private void OnRightWingClick(object sender, RoutedEventArgs e)
    {
        _explicitSlideDirection = 1;
        _stateMachine.CycleNextTab();
        UpdateSideWingVisuals();
    }

    private void OnDot0Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.MediaLyrics);
    private void OnDot1Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.TodoAndWater);
    private void OnDot2Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.Pomodoro);
    private void OnDot3Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.FileShelf);
    private void OnDot4Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.SystemStatus);

    private void UpdatePaginationDots()
    {
        try
        {
            var activeDotBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // #38BDF8
            var inactiveDotBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)); // #30FFFFFF

            SetDot(PageDot0, _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics);
            SetDot(PageDot1, _stateMachine.ActiveTab == IslandDashboardTab.TodoAndWater);
            SetDot(PageDot2, _stateMachine.ActiveTab == IslandDashboardTab.Pomodoro);
            SetDot(PageDot3, _stateMachine.ActiveTab == IslandDashboardTab.FileShelf);
            SetDot(PageDot4, _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus);

            void SetDot(Border dot, bool isActive)
            {
                dot.Width = isActive ? 16 : 4;
                dot.Background = isActive ? activeDotBrush : inactiveDotBrush;
            }
        }
        catch { }
    }

    private void UpdateTodoAndWaterUi()
    {
        try
        {
            TodoItemsControl.ItemsSource = _todoAndWaterService.Todos
                .Select(t => new TodoItemViewModel
                {
                    Id = t.Id,
                    Title = t.Title,
                    IsDone = t.IsDone
                })
                .ToList();

            TodoCounterText.Text = $"{_todoAndWaterService.CompletedTodosCount}/{_todoAndWaterService.TotalTodosCount} Selesai";

            WaterAmountText.Text = _todoAndWaterService.WaterCurrentLiters.ToString("0.0");
            WaterTargetText.Text = $" / {_todoAndWaterService.WaterTargetLiters:0.0} L";
            WaterPercentBadgeText.Text = $"{Math.Round(_todoAndWaterService.WaterPercentage)}%";

            double ratio = Math.Clamp(_todoAndWaterService.WaterPercentage / 100.0, 0.05, 1.0);
            WaterProgressFill.Width = 110.0 * ratio;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("UpdateTodoAndWaterUi", "Error updating Todo and Water UI", ex);
        }
    }

    private void OnDrinkWaterClick(object sender, RoutedEventArgs e)
    {
        _todoAndWaterService.DrinkWater(0.25);
        TriggerToast(new ToastPayload(
            Icon: "BELL",
            Title: "Air Diminum (+250 ml)",
            Subtitle: $"Total hari ini: {_todoAndWaterService.WaterCurrentLiters:0.0} / {_todoAndWaterService.WaterTargetLiters:0.0} L",
            AccentHex: "#38BDF8",
            Duration: TimeSpan.FromSeconds(2.0)));
    }

    private void OnResetWaterClick(object sender, RoutedEventArgs e)
    {
        _todoAndWaterService.ResetWater();
    }

    private void OnTodoItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItemViewModel vm })
        {
            _todoAndWaterService.ToggleTodo(vm.Id);
        }
    }

    private void OnManualTuckClick(object sender, RoutedEventArgs e)
    {
        _stateMachine.SetTuckedMode(true);
    }

    private async void OnMediaPrevClick(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void OnMediaPlayPauseClick(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void OnMediaNextClick(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    private void OnPomodoroStartPauseClick(object sender, RoutedEventArgs e) => _pomodoroService.ToggleStartPause();
    private void OnPomodoroResetFocusClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(25, isBreak: false);
    private void OnPomodoroBreakClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(5, isBreak: true);
    private void OnPomodoroAdd5MinClick(object sender, RoutedEventArgs e) => _pomodoroService.AddMinutes(5);

    private void OnToggleMuteClick(object sender, RoutedEventArgs e)
    {
        _audioService.ToggleMute();
        UpdateSlidersAndMuteUi();
    }

    private void OnToggleMicMuteClick(object sender, RoutedEventArgs e)
    {
        _audioService.ToggleMicrophoneMute();
        UpdateSlidersAndMuteUi();
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = AppLogger.LogFilePath;
            if (!System.IO.File.Exists(path))
            {
                path = AppLogger.TempLogFilePath;
            }

            if (System.IO.File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else
            {
                string dir = System.IO.Path.GetDirectoryName(path) ?? System.IO.Path.GetTempPath();
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("OpenLog", "Error opening log file", ex);
        }
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        AppLogger.LogInfo("Lifecycle", "OnExitAppClick initiated by user.");
        Application.Current.Shutdown();
    }

    private void UpdateSystemUpdateUi(UpdateCheckResult res)
    {
        if (res.IsUpdateAvailable)
        {
            TabSystemUpdateBadge.Visibility = Visibility.Visible;
            SysUpdateCardBorder.Background = new SolidColorBrush(Color.FromArgb(32, 34, 197, 94));
            SysUpdateCardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 34, 197, 94));
            SysUpdateIconPath.Data = (Geometry)FindResource("GeomUpdate");
            SysUpdateIconPath.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SysUpdateStatusText.Text = $"Update Baru: {res.LatestVersion} Tersedia!";
            SysUpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SysUpdateSubText.Text = !string.IsNullOrWhiteSpace(res.ReleaseTitle) && res.ReleaseTitle != res.LatestVersion
                ? $"{res.ReleaseTitle} (Saat ini: {res.CurrentVersion})"
                : $"Versi saat ini: {res.CurrentVersion}. Klik untuk memasang sekarang.";
            SysInstallUpdateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            TabSystemUpdateBadge.Visibility = Visibility.Collapsed;
            SysUpdateCardBorder.Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
            SysUpdateCardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));
            SysUpdateIconPath.Data = (Geometry)FindResource("GeomCheckCircle");
            SysUpdateIconPath.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SysUpdateStatusText.Text = $"Windows Dynamic Island {res.CurrentVersion} (Terbaru)";
            SysUpdateStatusText.Foreground = Brushes.White;
            SysUpdateSubText.Text = res.HasError
                ? $"Gagal memuat info rilis ({res.ErrorMessage})"
                : "Sistem sudah menggunakan rilis versi paling mutakhir";
            SysInstallUpdateBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        SysCheckUpdateBtnText.Text = "Mengecek...";
        SysCheckUpdateBtn.IsEnabled = false;

        try
        {
            var res = await _updateService.CheckForUpdatesAsync(isBackgroundCheck: false);
            UpdateSystemUpdateUi(res);

            if (!res.IsUpdateAvailable && !res.HasError)
            {
                TriggerToast(new ToastPayload(
                    Icon: "SHIELD",
                    Title: "Sistem Terkini",
                    Subtitle: $"Windows Dynamic Island {res.CurrentVersion} sudah yang paling baru",
                    AccentHex: "#38BDF8",
                    Duration: TimeSpan.FromSeconds(2.6)));
            }
        }
        finally
        {
            SysCheckUpdateBtnText.Text = "Cek Update";
            SysCheckUpdateBtn.IsEnabled = true;
        }
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        TriggerToast(new ToastPayload(
            Icon: "BOLT",
            Title: "Memperbarui Aplikasi...",
            Subtitle: "Installer sedang mengunduh dan memperbarui versi terbaru",
            AccentHex: "#22C55E",
            Duration: TimeSpan.FromSeconds(4.0)));

        UpdateCheckerService.LaunchOneClickInstaller();
    }

    private void OnSystemPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        AppLogger.LogInfo("SystemEvents", $"OnSystemPowerModeChanged: Mode={e.Mode}");
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            Dispatcher.InvokeAsync(() =>
            {
                AppLogger.LogInfo("SystemEvents", "PowerModes.Resume -> Repositioning TopCenter and ensuring render loop running.");
                ScheduleDebouncedReposition();
                _stateMachine.SetFullScreenHidden(false);
                EnsureRenderLoopRunning();
                if (_hwnd != IntPtr.Zero)
                {
                    Win32Interop.SetWindowPos(
                        _hwnd,
                        Win32Interop.HWND_TOPMOST,
                        0, 0, 0, 0,
                        Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_SHOWWINDOW | Win32Interop.SWP_NOACTIVATE);
                }
            });
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            AppLogger.LogInfo("SystemEvents", "PowerModes.Suspend detected (System entering Sleep/Modern Standby).");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        AppLogger.LogInfo("SystemEvents", "OnDisplaySettingsChanged detected -> Scheduling reposition.");
        Dispatcher.InvokeAsync(() =>
        {
            ScheduleDebouncedReposition();
            EnsureRenderLoopRunning();
        });
    }
}

public sealed class TodoItemViewModel
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsDone { get; init; }

    public Brush CheckBgBrush => IsDone
        ? new SolidColorBrush(Color.FromArgb(50, 34, 197, 94))
        : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

    public Brush CheckBorderBrush => IsDone
        ? new SolidColorBrush(Color.FromArgb(160, 34, 197, 94))
        : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

    public Visibility DoneCheckmarkVisibility => IsDone ? Visibility.Visible : Visibility.Collapsed;

    public Brush TitleColorBrush => IsDone
        ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
        : new SolidColorBrush(Color.FromRgb(248, 250, 252));

    public TextDecorationCollection? TitleDecorations => IsDone ? TextDecorations.Strikethrough : null;
}
