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
/// Main overlay window hosting the unified one-piece NotchNook-style Dynamic Island and 60-120 FPS spring physics loop.
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

    private readonly DispatcherTimer _oneSecondTimer;
    private readonly DispatcherTimer _fastUpdateTimer;
    private readonly DispatcherTimer _hitTestTimer;
    private readonly DispatcherTimer _toastDismissTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;

    private readonly double[] _spectrumHeights = [5, 8, 12, 8, 5];
    private Rectangle[] _waveRectangles = [];

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

    public MainWindow()
    {
        InitializeComponent();

        _waveRectangles = [WaveBar0, WaveBar1, WaveBar2, WaveBar3, WaveBar4];
        ShelfItemsControl.ItemsSource = _fileShelfService.Items;
        _fileShelfService.Items.CollectionChanged += (_, _) => UpdateShelfUi();

        _oneSecondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _oneSecondTimer.Tick += OnOneSecondTick;

        _fastUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fastUpdateTimer.Tick += OnFastUpdateTick;

        _hitTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _hitTestTimer.Tick += (_, _) => UpdateWindowClickThroughState();

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

        _stateMachine.StateChanged += OnIslandStateChanged;
        _mediaService.TrackChanged += OnMediaTrackChanged;
        _mediaService.PlaybackStateChanged += OnMediaPlaybackStateChanged;
        _lyricsService.LyricsUpdated += () => Dispatcher.InvokeAsync(UpdateMediaAndLyricsUi);
        _hardwareService.HardwareToastTriggered += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _notificationService.NotificationReceived += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _pomodoroService.TimerTicked += () => Dispatcher.InvokeAsync(UpdatePomodoroAndClockUi);
        _pomodoroService.TimerCompleted += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));

        // Subscribe to external keyboard Volume / Mute / Mic Mute / Brightness key events
        _audioService.SpeakerVolumeOrMuteChanged += (vol, muted, muteToggled) =>
            Dispatcher.InvokeAsync(() => ShowVolumeToast(vol, muted, muteToggled));

        _audioService.MicrophoneMuteChanged += micMuted =>
            Dispatcher.InvokeAsync(() => ShowMicMuteToast(micMuted));

        _brightnessService.BrightnessChanged += brt =>
            Dispatcher.InvokeAsync(() => ShowBrightnessToast(brt));

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        int exStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
        exStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST | Win32Interop.WS_EX_NOACTIVATE;
        Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);

        Win32Interop.AddClipboardFormatListener(_hwnd);
        InitializeSystemTrayIcon();
        PositionTopCenter();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        PositionTopCenter();
        UpdateTabHighlight();
        _hardwareService.RefreshFullTelemetry(emitToasts: false);
        _audioService.PollExternalChanges();
        _brightnessService.PollExternalChanges();
        UpdateHardwareUi();
        UpdateSlidersAndMuteUi();
        UpdatePomodoroAndClockUi();

        _oneSecondTimer.Start();
        _fastUpdateTimer.Start();
        _hitTestTimer.Start();
        UpdateWindowClickThroughState();

        TriggerToast(new ToastPayload(
            Icon: "🏝️",
            Title: "Dynamic Island Aktif",
            Subtitle: "Scroll = Volume • Shift+Scroll = Kecerahan",
            AccentHex: "#38BDF8",
            Duration: TimeSpan.FromSeconds(2.6)));

        await _mediaService.InitializeAsync();
        EnsureRenderLoopRunning();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        StopRenderLoop();
        _oneSecondTimer.Stop();
        _fastUpdateTimer.Stop();
        _hitTestTimer.Stop();
        _mediaService.Dispose();
        _hardwareService.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.RemoveClipboardFormatListener(_hwnd);
            Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_DELETE, ref _trayIconData);
        }
    }

    private void PositionTopCenter()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2.0;
        Top = 0;
    }

    private void InitializeSystemTrayIcon()
    {
        _trayIconData = new Win32Interop.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32Interop.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP,
            uCallbackMessage = Win32Interop.WM_USER_TRAY,
            hIcon = Win32Interop.LoadIcon(IntPtr.Zero, Win32Interop.IDI_APPLICATION),
            szTip = "Windows Dynamic Island (Klik untuk Buka / Klik Kanan untuk Sistem)"
        };

        Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_ADD, ref _trayIconData);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_NCHITTEST)
        {
            bool isAltBypass = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_LMENU) & 0x8000) != 0;
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
        }

        double leftEdge = (Width - bodyWidth) / 2.0;
        double rightEdge = leftEdge + bodyWidth;

        return dipX >= leftEdge && dipX <= rightEdge && dipY >= 0 && dipY <= bodyHeight;
    }

    /// <summary>
    /// Dynamically toggles WS_EX_TRANSPARENT on the Win32 layered window so that
    /// clicks outside the exact Dynamic Island body pass 100% directly to Chrome and other apps underneath.
    /// </summary>
    private void UpdateWindowClickThroughState()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool isAltBypass = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_LMENU) & 0x8000) != 0;
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
                        Icon: "📋",
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
                        Icon: "🗂️",
                        Title: $"{files.Count} File Disalin",
                        Subtitle: "Tip: Drag ke Island untuk simpan di Tray File",
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
        ShelfTabGrid.Visibility = tab == IslandDashboardTab.FileShelf ? Visibility.Visible : Visibility.Collapsed;
        PomodoroTabGrid.Visibility = tab == IslandDashboardTab.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        SystemTabGrid.Visibility = tab == IslandDashboardTab.SystemStatus ? Visibility.Visible : Visibility.Collapsed;

        if (state == IslandVisualState.ExpandedDashboard && tab == IslandDashboardTab.SystemStatus)
        {
            UpdateSlidersAndMuteUi();
        }

        UpdateTabHighlight();
        UpdateWindowClickThroughState();
        EnsureRenderLoopRunning();
    }

    private void UpdateTabHighlight()
    {
        var activeBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
        var inactiveBrush = Brushes.Transparent;
        var activeFg = Brushes.White;
        var inactiveFg = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        SetTabVisual(TabMediaBtn, _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics);
        SetTabVisual(TabShelfBtn, _stateMachine.ActiveTab == IslandDashboardTab.FileShelf);
        SetTabVisual(TabPomodoroBtn, _stateMachine.ActiveTab == IslandDashboardTab.Pomodoro);
        SetTabVisual(TabSystemBtn, _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus);

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
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((double)(now - _lastRenderTick) / Stopwatch.Frequency, 0.001, 0.05);
        _lastRenderTick = now;

        bool morphActive = _morph.Update(dt);

        double bodyWidth = Math.Max(48, _morph.Width.Current);
        double height = Math.Max(4, _morph.Height.Current);
        double bottomRadius = Math.Clamp(_morph.BottomCornerRadius.Current, 2, height * 0.65);
        double topEarRadius = Math.Clamp(_morph.TopEarRadius.Current, 2, height * 0.55);

        IslandBorder.Width = bodyWidth;
        IslandBorder.Height = height;

        AmbientGlowBorder.Width = bodyWidth;
        AmbientGlowBorder.Height = height;
        AmbientGlowBorder.CornerRadius = new CornerRadius(0, 0, bottomRadius, bottomRadius);
        AmbientGlowBorder.Opacity = _morph.GlowOpacity.Current;

        UpdateUnifiedNotchGeometry(bodyWidth, height, bottomRadius, topEarRadius);
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
            float peak = _audioService.GetCurrentPeakLevel(_mediaService.CurrentTrack.IsPlaying);
            LyricsStageGlowOrb.Opacity = 0.14 + Math.Clamp(peak * 0.45, 0.0, 0.35);
            LyricsStageGlowOrb.Width = 180 + peak * 95;
        }

        if (!morphActive && !isCompactMedia && !isExpandedMedia)
        {
            StopRenderLoop();
        }
    }

    private void UpdateUnifiedNotchGeometry(double bodyWidth, double height, double bottomRadius, double topEarRadius)
    {
        if (topEarRadius + bottomRadius > height)
        {
            double scale = height / Math.Max(1.0, topEarRadius + bottomRadius);
            topEarRadius *= scale;
            bottomRadius *= scale;
        }

        double totalWidth = bodyWidth + (topEarRadius * 2.0);
        double xLeft = topEarRadius;
        double xRight = topEarRadius + bodyWidth;

        var fillGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeom.Open())
        {
            BuildNotchContour(ctx, totalWidth, xLeft, xRight, height, topEarRadius, bottomRadius, isFilled: true, isClosed: true);
        }
        fillGeom.Freeze();

        UnifiedNotchPath.Width = totalWidth;
        UnifiedNotchPath.Height = height;
        UnifiedNotchPath.Data = fillGeom;

        var rimGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = rimGeom.Open())
        {
            BuildNotchContour(ctx, totalWidth, xLeft, xRight, height, topEarRadius, bottomRadius, isFilled: false, isClosed: false);
        }
        rimGeom.Freeze();

        UnifiedNotchRimPath.Width = totalWidth;
        UnifiedNotchRimPath.Height = height;
        UnifiedNotchRimPath.Data = rimGeom;
    }

    private static void BuildNotchContour(
        StreamGeometryContext ctx,
        double totalWidth,
        double xLeft,
        double xRight,
        double height,
        double topEarRadius,
        double bottomRadius,
        bool isFilled,
        bool isClosed)
    {
        ctx.BeginFigure(new Point(0, 0), isFilled: isFilled, isClosed: isClosed);

        ctx.QuadraticBezierTo(
            new Point(xLeft, 0),
            new Point(xLeft, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xLeft, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xLeft, height),
            new Point(xLeft + bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRight - bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRight, height),
            new Point(xRight, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRight, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRight, 0),
            new Point(totalWidth, 0),
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
        _hardwareService.PollFastKeyboardIndicators();
        _audioService.PollExternalChanges();
        _brightnessService.PollExternalChanges();
        _notificationService.PollNewNotifications();

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

    private void OnOneSecondTick(object? sender, EventArgs e)
    {
        if (_autoHideFullScreen)
        {
            bool fullScreen = Win32Interop.IsFullScreenAppActive();
            _stateMachine.SetFullScreenHidden(fullScreen);
        }

        if (_hwnd != IntPtr.Zero && _stateMachine.CurrentState != IslandVisualState.Hidden)
        {
            Win32Interop.SetWindowPos(
                _hwnd,
                Win32Interop.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
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

    // --- VOLUME, BRIGHTNESS & MUTE OSD TOAST HELPERS ---

    private void ShowVolumeToast(int volumePercent, bool isMuted, bool muteToggled)
    {
        UpdateSlidersAndMuteUi();

        string icon = isMuted || volumePercent == 0 ? "🔇" : volumePercent < 45 ? "🔉" : "🔊";
        string title = isMuted ? "Sound: Muted" : $"Volume {volumePercent}%";
        string sub = isMuted
            ? "Audio output dibisukan"
            : muteToggled ? "Audio output aktif kembali" : "Geser slider atau scroll mouse";
        string accent = isMuted ? "#F87171" : "#38BDF8";

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

        string icon = brightnessPercent < 35 ? "🔅" : "☀️";
        TriggerToast(new ToastPayload(
            Icon: icon,
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
            Icon: isMicMuted ? "🚫" : "🎙️",
            Title: isMicMuted ? "Mikrofon: MUTED" : "Mikrofon: ON (Aktif)",
            Subtitle: isMicMuted ? "Suara mikrofon Anda dibisukan" : "Mikrofon siap menerima suara",
            AccentHex: isMicMuted ? "#F87171" : "#22C55E",
            Duration: TimeSpan.FromSeconds(2.2)));
    }

    private void UpdateSlidersAndMuteUi()
    {
        int vol = _audioService.GetMasterVolumePercent();
        bool soundMuted = _audioService.IsMuted();
        bool micMuted = _audioService.IsMicrophoneMuted();
        int brt = _brightnessService.GetBrightnessPercent();

        SysVolumePercentText.Text = soundMuted ? "MUTED" : $"{vol}%";
        SysVolumePercentText.Foreground = new SolidColorBrush(
            soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(56, 189, 248));
        SysVolumeLabelText.Text = soundMuted ? "🔇 AUDIO MUTED" : "🔊 VOLUME AUDIO";

        double volTrackW = SysVolumeTrackBorder.ActualWidth > 10 ? SysVolumeTrackBorder.ActualWidth : 185.0;
        double volFillPx = volTrackW * (soundMuted ? 0.0 : Math.Clamp(vol / 100.0, 0.0, 1.0));
        SysVolumeFillBorder.Width = volFillPx;

        SysBrightnessPercentText.Text = $"{brt}%";
        double brtTrackW = SysBrightnessTrackBorder.ActualWidth > 10 ? SysBrightnessTrackBorder.ActualWidth : 185.0;
        double brtFillPx = brtTrackW * Math.Clamp(brt / 100.0, 0.0, 1.0);
        SysBrightnessFillBorder.Width = brtFillPx;

        ToggleMuteBtn.Content = soundMuted ? "🔇 Sound: MUTED" : "🔊 Sound: ON";
        ToggleMuteBtn.Foreground = new SolidColorBrush(
            soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(248, 250, 252));

        ToggleMicMuteBtn.Content = micMuted ? "🚫 Mic: MUTED" : "🎙️ Mic: ON";
        ToggleMicMuteBtn.Foreground = new SolidColorBrush(
            micMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(34, 197, 94));
    }

    private void UpdatePomodoroAndClockUi()
    {
        string nowStr = DateTime.Now.ToString("HH:mm");
        ExpandedClockBadge.Text = nowStr;

        if (_pomodoroService.IsRunning)
        {
            ClockTextBlock.Text = $"⏱ {_pomodoroService.FormattedRemaining}";
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
        PomodoroStartPauseBtn.Content = _pomodoroService.IsRunning ? "⏸ Jeda" : "▶ Mulai";
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
        IdleBatteryIconText.Text = "⚡";
        IdleBatteryIconText.Foreground = new SolidColorBrush(
            hw.IsCharging ? Color.FromRgb(254, 240, 138) : Color.FromRgb(248, 250, 252));

        CameraPrivacyDot.Visibility = hw.IsCameraInUse ? Visibility.Visible : Visibility.Collapsed;
        MicPrivacyDot.Visibility = hw.IsMicrophoneInUse ? Visibility.Visible : Visibility.Collapsed;

        bool hasExtraIdle = hw.IsCameraInUse || hw.IsMicrophoneInUse || _pomodoroService.IsRunning;
        _stateMachine.UpdateDynamicIdleWidth(hasExtraIdle);

        SysBatteryDetailText.Text = hw.HasBattery
            ? $"{(hw.IsCharging ? "⚡ Charging" : "🔋 Baterai")} {hw.BatteryPercent}%"
            : "⚡ Daya Desktop AC (100%)";

        SysNetworkDetailText.Text = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName)
            ? $"🎧 {hw.ConnectedBluetoothName} • {hw.NetworkName}"
            : hw.NetworkName;

        if (hw.IsCameraInUse || hw.IsMicrophoneInUse)
        {
            string activeDevices = (hw.IsCameraInUse && hw.IsMicrophoneInUse)
                ? "🔴 Kamera & Mic Aktif"
                : hw.IsCameraInUse ? "🟢 Kamera Aktif" : $"🟠 Mic Aktif ({hw.ActiveMicApp})";
            SysPrivacyDetailText.Text = activeDevices;
            SysPrivacyDetailText.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
        }
        else
        {
            SysPrivacyDetailText.Text = "🛡️ Kamera & Mic Aman (Idle)";
            SysPrivacyDetailText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
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

        var barBrush = new SolidColorBrush(c);
        TimelineFillBorder.Background = barBrush;
        LyricLineProgressFill.Background = barBrush;
        TuckedAccentBar.Background = barBrush;

        foreach (var rect in _waveRectangles)
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
        PlayPauseButton.Content = track.IsPlaying ? "⏸" : "▶";

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
            TimelineThumb.Margin = new Thickness(fillPx - 6, 0, 0, 0);
        }
        else
        {
            TimelineFillBorder.Width = 0;
            TimelineThumb.Margin = new Thickness(-6, 0, 0, 0);
        }

        var lyricSnap = _lyricsService.GetActiveLyrics(pos);
        _currentLyricLineProgress = lyricSnap.LineProgressRatio;

        LyricLineProgressFill.Width = 120.0 * Math.Clamp(lyricSnap.LineProgressRatio, 0.0, 1.0);

        string compactTargetText;
        if (lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.CurrentLine) && lyricSnap.CurrentLine != "♪  •  •  •")
        {
            compactTargetText = $"♪ {lyricSnap.CurrentLine}";
        }
        else if (lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.NextLine))
        {
            compactTargetText = $"♪ {lyricSnap.NextLine}";
        }
        else
        {
            compactTargetText = track.IsPlaying
                ? $"♪ {track.Title} • {track.Artist}"
                : $"⏸ {track.Title}";
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
        ToastIconText.Text = toast.Icon;
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

        ToastIconBadge.Background = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B));
        AmbientGlowBrush.Color = accent;

        if (toast.SliderKind == ToastSliderKind.Battery)
        {
            // Render realistic Battery Graphic with Lightning Bolt ⚡ centered inside (NOT a slider)
            ToastProgressContainer.Visibility = Visibility.Collapsed;
            ToastBatteryContainer.Visibility = Visibility.Visible;

            double batRatio = Math.Clamp(toast.ProgressRatio ?? 1.0, 0.08, 1.0);
            ToastBatteryFill.Width = 46.5 * batRatio;
            ToastBatteryFill.Background = new SolidColorBrush(accent);
            ToastBatteryBoltIcon.Text = "⚡";
            ToastBatteryBoltIcon.Foreground = new SolidColorBrush(
                toast.IsBatteryCharging ? Color.FromRgb(254, 240, 138) : Color.FromRgb(255, 255, 255));
        }
        else if (toast.ProgressRatio.HasValue)
        {
            ToastBatteryContainer.Visibility = Visibility.Collapsed;
            ToastProgressContainer.Visibility = Visibility.Visible;
            double ratio = Math.Clamp(toast.ProgressRatio.Value, 0.0, 1.0);
            double fillPx = 112.0 * ratio;
            ToastProgressFill.Width = fillPx;
            ToastProgressFill.Background = new SolidColorBrush(accent);
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

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        _collapseDebounceTimer.Stop();
        _collapseDebounceTimer.Start();
    }

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
        EmptyShelfHintText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfHeaderCountText.Text = count == 0
            ? "Taruh file/gambar sementara di sini (Drag Masuk & Keluar)"
            : $"📦 {count} item tersimpan di rak sementara (Tarik keluar untuk kirim)";
    }

    private void OnClearShelfClick(object sender, RoutedEventArgs e)
    {
        _fileShelfService.Clear();
        UpdateShelfUi();
    }

    // --- TAB & BUTTON HANDLERS ---

    private void OnTabMediaClick(object sender, RoutedEventArgs e) => _stateMachine.SetActiveTab(IslandDashboardTab.MediaLyrics);
    private void OnTabShelfClick(object sender, RoutedEventArgs e) => _stateMachine.SetActiveTab(IslandDashboardTab.FileShelf);
    private void OnTabPomodoroClick(object sender, RoutedEventArgs e) => _stateMachine.SetActiveTab(IslandDashboardTab.Pomodoro);
    private void OnTabSystemClick(object sender, RoutedEventArgs e)
    {
        _stateMachine.SetActiveTab(IslandDashboardTab.SystemStatus);
        UpdateSlidersAndMuteUi();
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

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
