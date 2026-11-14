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
    private readonly AppShortcutService _shortcutService = new();
    private readonly PomodoroTimerService _pomodoroService = new();
    private readonly TodoAndWaterService _todoAndWaterService = new();
    private readonly UpdateCheckerService _updateService = new();
    private readonly AppSettingsService _settingsService = new();
        private readonly BatteryChargeLimitService _batteryLimitService = new();
    private readonly NotificationHistoryService _notificationHistoryService = new();
    private readonly WeatherService _weatherService = new();
    private readonly CalendarScheduleService _calendarService = new();
    private readonly FileShelfShareService _fileShelfShareService = new();
    private WindowsFlyoutSuppressor? _flyoutSuppressor;

    private readonly DispatcherTimer _oneSecondTimer;
    private readonly DispatcherTimer _fastUpdateTimer;
    private readonly DispatcherTimer _hitTestTimer;
    private readonly DispatcherTimer _toastDismissTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _autoTuckTimer;

    private readonly double[] _spectrumHeights = [4, 7, 11, 7, 4];
    private Rectangle[] _waveRectangles = [];
    private Rectangle[] _expWaveRectangles = [];

    private const double MasterGridWidth = 760.0;

    private IntPtr _hwnd;
    private Win32Interop.NOTIFYICONDATA _trayIconData;
    private SettingsWindow? _settingsWindow;
    private bool _isRenderLoopAttached;
    private bool _isWindowClickThrough;
    private long _lastRenderTick;
    private string _lastClipboardPreview = string.Empty;
    private double _lastAppliedScale = 1.0;

    // Lyric animation & slow one-way pan tracking
    private int _lastAnimatedLyricIndex = -999;
    private string _lastDisplayedCompactText = string.Empty;
    private string? _lastDisplayedCompactRomanizedText;
    private double _currentLyricLineProgress;
    private double _currentPanX;
    private int _topBezelHoldTicks;
    private int _topHandleHoverTicks;
    private int _tuckCooldownTicks;

    // Interactive timeline & slider scrubbing states
    private bool _isDraggingTimeline;
    private double _draggedTimelineRatio;
    private bool _isDraggingToastSlider;
    private bool _isDraggingSysVolume;
    private bool _isDraggingSysBrightness;
    private bool _isDraggingAppMixer;
    private int _draggingAppMixerPid;
    private ToastSliderKind _activeToastSliderKind = ToastSliderKind.None;
    private CancellationTokenSource? _repositionCts;
    private bool _isWindowActivatable;

    // Spotlight Quick Launcher, App Mixer & Clipboard History state
    private readonly SpotlightSearchService _spotlightService;
    private readonly ClipboardHistoryService _clipboardHistoryService;
    private bool _isShowingAppMixer;
    private bool _isShelfClipboardMode;
    private int _selectedSpotlightIndex = -1;
    private List<SpotlightItem> _currentSpotlightItems = [];

    // Interactive satellite side-wing switcher & bottom morphing dock state
    private double _leftWingWidth;
    private double _targetLeftWingWidth;
    private double _rightWingWidth;
    private double _targetRightWingWidth;
    private int _hoveredSideZone; // -1 = Left, 0 = None, 1 = Right
    private bool _isBottomDockMorphed;
    private double _currentPlacementTopDip;
    private Color _currentThemeAccentColor = Color.FromRgb(56, 189, 248);
    private IslandDashboardTab _previousActiveTab = IslandDashboardTab.MediaLyrics;
    private int _explicitSlideDirection;

    private static readonly HashSet<string> MonochromeExcludedNames =
    [
        "UnifiedNotchPath",
        "UnifiedNotchRimPath",
        "AmbientGlowBorder"
    ];

    private bool IsMonochromeTheme =>
        _settingsService.IslandSurfaceTheme == "Monochrome" ||
        _settingsService.AccentTheme == "Monochrome";

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

        _hitTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _hitTestTimer.Tick += (_, _) =>
        {
            UpdateScreenshotModeState();
            UpdateSideWingHover();
            UpdateWindowClickThroughState();
            UpdateTuckedProximityHover();
        };

        _autoTuckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoTuckTimer.Tick += (_, _) =>
        {
            _autoTuckTimer.Stop();
            if (_settingsService.AutoTuckEnabled &&
                !_stateMachine.IsHovered &&
                !_stateMachine.IsPinnedExpanded &&
                _stateMachine.ActiveToast == null &&
                !_isDraggingTimeline &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness &&
                !_isDraggingToastSlider)
            {
                _stateMachine.SetAutoTucked(true);
            }
        };

        _toastDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastDismissTimer.Tick += (_, _) =>
        {
            if (_isDraggingToastSlider) return;
            _toastDismissTimer.Stop();
            _stateMachine.ClearToast();

            // After notification finishes (2-3s), display the default normal view (IdlePill) for IdleCycleSeconds (default 10s) before auto-tucking!
            if (_settingsService.AutoTuckEnabled && !_stateMachine.IsTuckedMode)
            {
                _stateMachine.SetAutoTucked(false);
                _autoTuckTimer.Stop();
                _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
                _autoTuckTimer.Start();
            }
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
                Keyboard.ClearFocus();
                _stateMachine.SetHovered(false);

                // When user un-hovers from expanded dashboard, display default normal view for IdleCycleSeconds (default 10s) before auto-tucking!
                if (_settingsService.AutoTuckEnabled && !_stateMachine.IsTuckedMode)
                {
                    _stateMachine.SetAutoTucked(false);
                    _autoTuckTimer.Stop();
                    _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
                    _autoTuckTimer.Start();
                }
            }
        };

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _heartbeatTimer.Tick += OnHeartbeatTick;

        _stateMachine.StateChanged += OnIslandStateChanged;
        _mediaService.TrackChanged += OnMediaTrackChanged;
        _mediaService.TrackDurationRefined += OnMediaTrackDurationRefined;
        _mediaService.PlaybackStateChanged += OnMediaPlaybackStateChanged;
        _lyricsService.LyricsUpdated += () => Dispatcher.InvokeAsync(UpdateMediaAndLyricsUi);
        _hardwareService.HardwareToastTriggered += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _notificationService.NotificationReceived += toast => Dispatcher.InvokeAsync(() =>
        {
            if (_settingsService.SystemNotificationToastEnabled)
            {
                TriggerToast(toast);
            }
        });
        _pomodoroService.TimerTicked += () => Dispatcher.InvokeAsync(UpdatePomodoroAndClockUi);
        _pomodoroService.TimerCompleted += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _todoAndWaterService.DataChanged += () => Dispatcher.InvokeAsync(UpdateTodoAndWaterUi);
        _shortcutService.ShortcutsChanged += () => Dispatcher.InvokeAsync(UpdateShortcutsUi);
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

                _batteryLimitService.LimitModeChanged += _ => Dispatcher.InvokeAsync(UpdateHardwareUi);
        _notificationHistoryService.HistoryChanged += () => Dispatcher.InvokeAsync(RefreshNotificationHistoryUi);
        _weatherService.WeatherUpdated += _ => Dispatcher.InvokeAsync(RefreshWeatherUi);
        _calendarService.ScheduleChanged += () => Dispatcher.InvokeAsync(RefreshScheduleUi);
        _flyoutSuppressor = new WindowsFlyoutSuppressor(_settingsService.SuppressWindowsNativeOsd);
        _settingsService.SettingsChanged += () =>
        {
            if (_flyoutSuppressor != null)
            {
                _flyoutSuppressor.IsEnabled = _settingsService.SuppressWindowsNativeOsd;
            }
        };
        LocalizationService.Instance.LanguageChanged += () => Dispatcher.InvokeAsync(ApplyLocalization);

        RefreshCalendarMonthGrid();
        RefreshScheduleUi();
        RefreshNotificationHistoryUi();
        _ = _weatherService.GetWeatherAsync();

        // Subscribe to external keyboard Volume / Mute / Mic Mute / Brightness key events
        _audioService.SpeakerVolumeOrMuteChanged += (vol, muted, muteToggled) =>
        {
            if (_settingsService.SuppressWindowsNativeOsd)
            {
                WindowsFlyoutSuppressor.TriggerSuppressionBurst(durationMs: 700, intervalMs: 20);
                WindowsFlyoutSuppressor.SuppressOnce();
            }
            Dispatcher.InvokeAsync(() => ShowVolumeToast(vol, muted, muteToggled));
        };

        _audioService.MicrophoneMuteChanged += micMuted =>
            Dispatcher.InvokeAsync(() => ShowMicMuteToast(micMuted));

        _audioService.OutputDeviceChanged += (devName, isHeadphone) =>
            Dispatcher.InvokeAsync(() =>
            {
                UpdateSlidersAndMuteUi();
                UpdateMediaAndLyricsUi();
            });

        _brightnessService.BrightnessChanged += brt =>
        {
            if (_settingsService.SuppressWindowsNativeOsd)
            {
                WindowsFlyoutSuppressor.TriggerSuppressionBurst(durationMs: 700, intervalMs: 20);
                WindowsFlyoutSuppressor.SuppressOnce();
            }
            Dispatcher.InvokeAsync(() => ShowBrightnessToast(brt));
        };

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        DpiChanged += OnDpiChanged;

        _stateMachine.SetAutoTuckEnabled(_settingsService.AutoTuckEnabled);
        _settingsService.SettingsChanged += () => Dispatcher.InvokeAsync(ApplyAppSettings);

        _clipboardHistoryService = new ClipboardHistoryService();
        _clipboardHistoryService.HistoryChanged += () => Dispatcher.InvokeAsync(OnClipboardHistoryChanged);
        _spotlightService = new(_shortcutService, _settingsService, _clipboardHistoryService);
        Deactivated += OnWindowDeactivated;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        AppLogger.LogInfo("MainWindow", "MainWindow constructor completed successfully.");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"MainWindow.OnClosing called. Cancel={e.Cancel}. CallStack:\r\n{new StackTrace(1, true)}");
        base.OnClosing(e);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"MainWindow.OnWindowClosed called. CallStack:\r\n{new StackTrace(1, true)}");
        try { _settingsWindow?.Close(); } catch { }
        StopRenderLoop();
        _heartbeatTimer.Stop();
        _oneSecondTimer.Stop();
        _fastUpdateTimer.Stop();
        _hitTestTimer.Stop();
        _autoTuckTimer.Stop();
        _mediaService.Dispose();
        _hardwareService.Dispose();
        _updateService.Dispose();
        _fileShelfShareService.Dispose();
        _flyoutSuppressor?.Dispose();
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
            Win32Interop.UnregisterHotKey(_hwnd, Win32Interop.HOTKEY_ID_SPOTLIGHT);
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

    private void OnIdleCycleTick(object? sender, EventArgs e)
    {
        // No repeating alternating toggles. The Dynamic Island stays cleanly tucked in AutoTuck mode
        // and only wakes up when a notification arrives or user explicitly interacts with the island.
    }

    private void OnFastUpdateTick(object? sender, EventArgs e)
    {
        try
        {
            _hardwareService.PollFastKeyboardIndicators();
            _audioService.PollExternalChanges();
            _notificationService.PollNewNotifications();

            bool isTuckedOrHidden = _stateMachine.CurrentState is IslandVisualState.TuckedTopEdge or IslandVisualState.Hidden || _stateMachine.IsFullScreenHidden;

            if (!isTuckedOrHidden)
            {
                _brightnessService.PollExternalChanges();
            }

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness)
            {
                UpdateSlidersAndMuteUi();
            }

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics &&
                _isShowingAppMixer &&
                !_isDraggingAppMixer)
            {
                UpdateAppMixerUi();
            }

            if (!isTuckedOrHidden && _mediaService.CurrentTrack.HasActiveSession)
            {
                UpdateMediaAndLyricsUi();
            }

            if (!_stateMachine.IsScreenshotMode &&
                _stateMachine.CurrentState == IslandVisualState.TuckedTopEdge &&
                Win32Interop.GetCursorPos(out var pt))
            {
                var (dipX, dipY) = ScreenCursorToMasterGridDip(pt);
                if (dipY >= 0 && dipY <= (_currentPlacementTopDip + 4.0) && Math.Abs(dipX - (MasterGridWidth / 2.0)) <= 85)
                {
                    _topBezelHoldTicks++;
                    if (_topBezelHoldTicks >= 3)
                    {
                        _topBezelHoldTicks = 0;
                        _stateMachine.SetTuckedMode(false);
                        _stateMachine.SetAutoTucked(false);
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
            var targetMonitor = Win32Interop.ResolveTargetMonitor(_settingsService.TargetMonitorMode);
            IntPtr targetHMonitor = targetMonitor?.HMonitor ?? IntPtr.Zero;

            if (_settingsService.AutoHideFullScreen)
            {
                bool fullScreen = Win32Interop.IsFullScreenAppActive(_hwnd, targetHMonitor);
                bool wasFullScreen = _stateMachine.IsFullScreenHidden;
                _stateMachine.SetFullScreenHidden(fullScreen);

                if (wasFullScreen && !fullScreen)
                {
                    AppLogger.LogInfo("FullScreenCheck", "Exited full screen app -> Scheduling reposition.");
                    ScheduleDebouncedReposition();
                }
            }
            else if (_stateMachine.IsFullScreenHidden)
            {
                _stateMachine.SetFullScreenHidden(false);
            }

            if (_hwnd != IntPtr.Zero && _stateMachine.CurrentState != IslandVisualState.Hidden && !_stateMachine.IsFullScreenHidden)
            {
                // Self-healing: verify physical window position matches resolved target monitor center
                if (targetMonitor != null && Win32Interop.GetWindowRect(_hwnd, out var currentRect))
                {
                    var rcMon = targetMonitor.BoundsPx;
                    int monitorCenterPx = rcMon.Left + (rcMon.Right - rcMon.Left) / 2;
                    int windowCenterPx = (currentRect.Left + currentRect.Right) / 2;
                    if (Math.Abs(windowCenterPx - monitorCenterPx) > 4 ||
                        Math.Abs(currentRect.Top - (rcMon.Top + (int)Math.Round(_settingsService.TopOffsetPx * targetMonitor.ScaleFactor))) > 6)
                    {
                        AppLogger.LogInfo("OneSecondTick",
                            $"Re-aligning island to target monitor ({_settingsService.TargetMonitorMode}): WinCenter={windowCenterPx}, TargetCenter={monitorCenterPx}.");
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

            if (!_stateMachine.IsFullScreenHidden)
            {
                _hardwareService.RefreshFullTelemetry();
                UpdateHardwareUi();
                if (_stateMachine.CurrentState == IslandVisualState.PinnedPerformancePill ||
                    (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                     _stateMachine.ActiveTab == IslandDashboardTab.PerformanceMonitor))
                {
                    UpdatePerformanceUi();
                }
            }

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus &&
                !_isDraggingSysVolume &&
                !_isDraggingSysBrightness)
            {
                UpdateSlidersAndMuteUi();
            }

            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics &&
                _isShowingAppMixer &&
                !_isDraggingAppMixer)
            {
                UpdateAppMixerUi();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("OneSecondTick", "Error during OnOneSecondTick", ex);
        }
    }

    // --- VOLUME, BRIGHTNESS & MUTE OSD TOAST HELPERS ---

    private void OpenSettingsWindow(int categoryIndex = 0)
    {
        try
        {
            // Smoothly tuck/collapse Dynamic Island into the notch as Settings blooms out
            _stateMachine.SetHovered(false);
            _stateMachine.SetTuckedMode(true);

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(
                    _settingsService,
                    _updateService,
                    _hardwareService,
                    _audioService,
                    _brightnessService,
                    _todoAndWaterService,
                    _fileShelfService,
                    () => OnManualTuckClick(this, new RoutedEventArgs()),
                    _batteryLimitService);

                _settingsWindow.ClosedCallback = () =>
                {
                    Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(160);
                        _stateMachine.SetTuckedMode(false);
                    });
                };
            }

            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.SelectCategory(categoryIndex);
            _settingsWindow.Show();
            _settingsWindow.Activate();
            _settingsWindow.Focus();
            _settingsWindow.PlayDropletEntranceAnimation();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("OpenSettingsWindow", "Failed to open SettingsWindow", ex);
        }
    }

    private void ApplyAppSettings()
    {
        try
        {
            // 1. Notch Scale (Auto / 100% / 115% / 130% / 150%)
            double targetScale = _settingsService.EffectiveNotchScale;
            if (Math.Abs(targetScale - _lastAppliedScale) > 0.005)
            {
                _lastAppliedScale = targetScale;
                var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(240))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                NotchMasterScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                NotchMasterScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }

            // 2. Placement Style (Notch vs PunchHole), Surface Theme, Opacity, Corner Style, Contour Rim & Accent Theme
            _stateMachine.SetPlacementStyle(_settingsService.IslandPlacementStyle);
            _stateMachine.SetCornerStyle(_settingsService.NotchCornerStyle);

            byte surfaceAlpha = (byte)Math.Clamp((int)Math.Round(_settingsService.NotchBackgroundOpacity * 255.0), 200, 255);
            Color baseSurface = _settingsService.IslandSurfaceTheme switch
            {
                "MidnightCarbon" => Color.FromArgb(surfaceAlpha, 9, 13, 22),
                "FrostedGraphite" => Color.FromArgb(surfaceAlpha, 18, 19, 24),
                "CyberMatrix" => Color.FromArgb(surfaceAlpha, 4, 18, 14),
                "RoyalAmethyst" => Color.FromArgb(surfaceAlpha, 15, 8, 26),
                "CrimsonObsidian" => Color.FromArgb(surfaceAlpha, 22, 7, 12),
                "Monochrome" => Color.FromArgb(surfaceAlpha, 14, 14, 16),
                _ => Color.FromArgb(surfaceAlpha, 0, 0, 0)
            };

            if (IsMonochromeTheme && _settingsService.IslandSurfaceTheme != "Monochrome")
            {
                baseSurface = MonochromeThemeHelper.ToMonochromeColor(baseSurface);
            }
            UnifiedNotchPath.Fill = BrushCache.Get(baseSurface);

            Color themeColor = _settingsService.AccentTheme switch
            {
                "Green" => Color.FromRgb(34, 197, 94),
                "Purple" => Color.FromRgb(168, 85, 247),
                "Orange" => Color.FromRgb(249, 115, 22),
                "Rose" => Color.FromRgb(244, 63, 94),
                "Gold" => Color.FromRgb(251, 191, 36),
                "White" => Color.FromRgb(248, 250, 252),
                "Monochrome" => Color.FromRgb(226, 232, 240),
                _ => _settingsService.IslandSurfaceTheme == "Monochrome"
                    ? Color.FromRgb(226, 232, 240)
                    : Color.FromRgb(56, 189, 248)
            };

            if (_settingsService.AccentTheme != "Auto" || _settingsService.IslandSurfaceTheme == "Monochrome")
            {
                AmbientGlowBrush.Color = themeColor;
                TuckedAccentBar.Background = BrushCache.Get(themeColor);
                if (_mediaService.CurrentTrack.HasActiveSession && _mediaService.CurrentTrack.Title != "Tidak Ada Media")
                {
                    ApplyDominantColor(_mediaService.CurrentTrack.DominantColor);
                }
            }
            else if (_mediaService.CurrentTrack.HasActiveSession && _mediaService.CurrentTrack.Title != "Tidak Ada Media")
            {
                themeColor = _mediaService.CurrentTrack.DominantColor;
                ApplyDominantColor(themeColor);
            }

            _currentThemeAccentColor = themeColor;
            Application.Current.Resources["AppAccentBrush"] = BrushCache.Get(themeColor);
            HeaderSettingsGearIcon.Fill = BrushCache.Get(themeColor);

            MonochromeThemeHelper.ApplyToSubtree(UnifiedMasterGrid, IsMonochromeTheme, MonochromeExcludedNames);

            bool showRim = _settingsService.ShowContourRim && _settingsService.RimGlowStyle != "Hidden";
            UnifiedNotchRimPath.Visibility = showRim ? Visibility.Visible : Visibility.Collapsed;
            if (_settingsService.RimGlowStyle == "AccentNeon")
            {
                UnifiedNotchRimPath.Stroke = BrushCache.Get(Color.FromArgb(145, themeColor.R, themeColor.G, themeColor.B));
                UnifiedNotchRimPath.StrokeThickness = 1.35;
            }
            else
            {
                UnifiedNotchRimPath.Stroke = BrushCache.Get(Color.FromArgb(34, 255, 255, 255));
                UnifiedNotchRimPath.StrokeThickness = 1.0;
            }

            // 2b. Customizable Notch Tabs & DWM Capture Shield
            TabMediaBtn.Visibility = _settingsService.ShowTabMedia ? Visibility.Visible : Visibility.Collapsed;
            TabTodoBtn.Visibility = _settingsService.ShowTabTodo ? Visibility.Visible : Visibility.Collapsed;
            TabPomodoroBtn.Visibility = _settingsService.ShowTabPomodoro ? Visibility.Visible : Visibility.Collapsed;
            TabShortcutsBtn.Visibility = _settingsService.ShowTabShortcuts ? Visibility.Visible : Visibility.Collapsed;
            TabPerformanceBtn.Visibility = _settingsService.ShowTabPerformance ? Visibility.Visible : Visibility.Collapsed;
            TabShelfBtn.Visibility = _settingsService.ShowTabShelf ? Visibility.Visible : Visibility.Collapsed;
            TabSystemBtn.Visibility = Visibility.Collapsed;

            var enabledTabs = GetEnabledTabs();
            if (!enabledTabs.Contains(_stateMachine.ActiveTab))
            {
                _stateMachine.SetActiveTab(enabledTabs[0]);
            }
            UpdateTabHighlight();

            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.SetWindowDisplayAffinity(
                    _hwnd,
                    _settingsService.ExcludeFromScreenCapture ? Win32Interop.WDA_EXCLUDEFROMCAPTURE : Win32Interop.WDA_NONE);

                if (_settingsService.SpotlightHotkeyEnabled)
                {
                    Win32Interop.RegisterHotKey(_hwnd, Win32Interop.HOTKEY_ID_SPOTLIGHT, Win32Interop.MOD_ALT | Win32Interop.MOD_NOREPEAT, Win32Interop.VK_SPACE);
                }
                else
                {
                    Win32Interop.UnregisterHotKey(_hwnd, Win32Interop.HOTKEY_ID_SPOTLIGHT);
                }
            }

            MediaViewToggleBtn.Visibility = _settingsService.AppVolumeMixerEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (!_settingsService.AppVolumeMixerEnabled && _isShowingAppMixer)
            {
                _isShowingAppMixer = false;
                LyricsStageBorder.Visibility = Visibility.Visible;
                AppMixerBorder.Visibility = Visibility.Collapsed;
                MediaViewToggleText.Text = "Mixer";
                MediaViewToggleIcon.Data = GetVectorGeometry("GeomAppMixer");
            }

            // 3. Pomodoro & Water defaults
            _pomodoroService.DefaultFocusMinutes = _settingsService.DefaultFocusMinutes;
            _pomodoroService.DefaultBreakMinutes = _settingsService.DefaultBreakMinutes;
            if (!_pomodoroService.IsRunning)
            {
                _pomodoroService.Reset(
                    _pomodoroService.IsBreakMode ? _settingsService.DefaultBreakMinutes : _settingsService.DefaultFocusMinutes,
                    isBreak: _pomodoroService.IsBreakMode);
            }
            _todoAndWaterService.SetWaterTargetLiters(_settingsService.WaterDailyTargetLiters);

            // 4. Auto-Simpan state
            _stateMachine.SetAutoTuckEnabled(_settingsService.AutoTuckEnabled);
            if (_settingsService.AutoTuckEnabled && !_stateMachine.IsHovered && !_stateMachine.IsPinnedExpanded && !_stateMachine.IsMediaActive)
            {
                _autoTuckTimer.Stop();
                _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
                _autoTuckTimer.Start();
            }
            else if (!_settingsService.AutoTuckEnabled)
            {
                _autoTuckTimer.Stop();
                _stateMachine.SetAutoTucked(false);
            }

            // 5. Reposition & Refresh UI
            PositionTopCenter();
            ApplyLocalization();
            UpdateHardwareUi();
            UpdatePomodoroAndClockUi();
            UpdateTodoAndWaterUi();
            UpdateSlidersAndMuteUi();
            UpdateMediaAndLyricsUi();
            UpdatePerformanceUi();
            UpdateSystemUpdateUi(_updateService.LastResult);
            _settingsWindow?.ApplyThemeToSettingsWindow();
            if (_flyoutSuppressor != null) _flyoutSuppressor.IsEnabled = _settingsService.SuppressWindowsNativeOsd;
            EnsureRenderLoopRunning();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ApplyAppSettings", "Error applying app settings", ex);
        }
    }

    private void ApplyLocalization()
    {
        try
        {
            var loc = LocalizationService.Instance;

            if (TopHeaderTuckText != null) TopHeaderTuckText.Text = loc.Get("Status_Tuck");
            if (SysTuckBtnText != null) SysTuckBtnText.Text = loc.Get("Status_Tuck");
            if (SysExitBtnText != null) SysExitBtnText.Text = loc.Get("Status_Exit");
            if (ScheduleSaveBtnText != null) ScheduleSaveBtnText.Text = loc.Get("Cal_Save");

            if (SysVolumeLabelText != null) SysVolumeLabelText.Text = loc.Get("Sys_Volume");
            if (SysBrightnessLabelText != null) SysBrightnessLabelText.Text = loc.Get("Sys_Brightness");
            if (SysSettingsBtnText != null) SysSettingsBtnText.Text = loc.Get("Sys_Settings");
            if (SysCheckUpdateBtnText != null) SysCheckUpdateBtnText.Text = loc.Get("Settings_CheckUpdateBtn", "Check Update");
            UpdateSystemUpdateUi(_updateService.LastResult);

            if (TodoInputPlaceholder != null) TodoInputPlaceholder.Text = loc.Get("Todo_Placeholder");
            if (TodoAddBtnText != null) TodoAddBtnText.Text = loc.Get("Todo_AddBtn");
            if (EmptyTodoTitleText != null) EmptyTodoTitleText.Text = loc.Get("Todo_EmptyTitle");
            if (EmptyTodoSubtitleText != null) EmptyTodoSubtitleText.Text = loc.Get("Todo_EmptySub");

            if (PomodoroBreakBtnText != null) PomodoroBreakBtnText.Text = loc.IsEnglish ? "Break 5m" : "Istirahat 5m";
            if (PomodoroResetFocusBtnText != null) PomodoroResetFocusBtnText.Text = loc.IsEnglish ? "Focus 25m" : "Fokus 25m";

            if (ShortcutsHeaderTitleText != null) ShortcutsHeaderTitleText.Text = loc.Get("Shortcuts_Title");
            if (AddShortcutBtnText != null) AddShortcutBtnText.Text = loc.Get("Shortcuts_Add");

            if (PerfHeaderTitleText != null) PerfHeaderTitleText.Text = loc.Get("Perf_Title");

            if (ShelfModeFilesText != null) ShelfModeFilesText.Text = loc.Get("Shelf_Files");
            if (ShelfModeClipboardText != null) ShelfModeClipboardText.Text = loc.Get("Shelf_Clipboard");

            if (AgendaHeaderTitleText != null) AgendaHeaderTitleText.Text = loc.Get("Cal_AgendaTitle");
            if (QuickAddScheduleBtnText != null) QuickAddScheduleBtnText.Text = loc.Get("Cal_Add");

            if (EmptyNotifTitleText != null) EmptyNotifTitleText.Text = loc.Get("Notif_EmptyTitle");
            if (EmptyNotifSubtitleText != null) EmptyNotifSubtitleText.Text = loc.Get("Notif_EmptySub");

            UpdateSideWingVisuals();
            UpdateHardwareUi();
            UpdateMediaAndLyricsUi();
            UpdateTodoAndWaterUi();
            UpdatePomodoroAndClockUi();
            RefreshNotificationHistoryUi();
            RefreshWeatherUi();
            RefreshScheduleUi();
            RefreshCalendarMonthGrid();
        }
        catch { }
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
        bool isMono = IsMonochromeTheme;
        var loc = LocalizationService.Instance;
        if (res.IsUpdateAvailable)
        {
            TabSystemUpdateBadge.Visibility = Visibility.Visible;
            HeaderSettingsUpdateDot.Visibility = Visibility.Visible;
            HeaderSettingsUpdateDot.Fill = isMono ? BrushCache.Get(Color.FromRgb(228, 228, 231)) : BrushCache.Get(Color.FromRgb(34, 197, 94));
            SysUpdateCardBorder.Background = BrushCache.Get(
                isMono ? Color.FromArgb(32, 228, 228, 231) : Color.FromArgb(32, 34, 197, 94));
            SysUpdateCardBorder.BorderBrush = BrushCache.Get(
                isMono ? Color.FromArgb(80, 228, 228, 231) : Color.FromArgb(80, 34, 197, 94));
            SysUpdateIconPath.Data = (Geometry)FindResource("GeomUpdate");
            SysUpdateIconPath.Fill = BrushCache.Get(
                isMono ? Color.FromRgb(228, 228, 231) : Color.FromRgb(34, 197, 94));
            SysUpdateStatusText.Text = loc.Format("Updater_Available", res.LatestVersion);
            SysUpdateStatusText.Foreground = BrushCache.Get(
                isMono ? Color.FromRgb(228, 228, 231) : Color.FromRgb(34, 197, 94));
            SysUpdateSubText.Text = !string.IsNullOrWhiteSpace(res.ReleaseTitle) && res.ReleaseTitle != res.LatestVersion
                ? $"{res.ReleaseTitle} ({res.CurrentVersion})"
                : loc.Format("Updater_UpToDate", res.CurrentVersion);
            SysInstallUpdateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            TabSystemUpdateBadge.Visibility = Visibility.Collapsed;
            HeaderSettingsUpdateDot.Visibility = Visibility.Collapsed;
            SysUpdateCardBorder.Background = BrushCache.Get(Color.FromArgb(18, 255, 255, 255));
            SysUpdateCardBorder.BorderBrush = BrushCache.Get(Color.FromArgb(32, 255, 255, 255));
            SysUpdateIconPath.Data = (Geometry)FindResource("GeomCheckCircle");
            SysUpdateIconPath.Fill = BrushCache.Get(
                isMono ? Color.FromRgb(228, 228, 231) : Color.FromRgb(34, 197, 94));
            SysUpdateStatusText.Text = $"Windows Dynamic Island {res.CurrentVersion}";
            SysUpdateStatusText.Foreground = Brushes.White;
            SysUpdateSubText.Text = res.HasError
                ? res.ErrorMessage
                : loc.Format("Updater_UpToDate", res.CurrentVersion);
            SysInstallUpdateBtn.Visibility = Visibility.Collapsed;
        }
        _settingsWindow?.RefreshUpdateCard(res);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        SysCheckUpdateBtnText.Text = loc.Get("Updater_Checking", "Checking...");
        SysCheckUpdateBtn.IsEnabled = false;

        try
        {
            var res = await _updateService.CheckForUpdatesAsync(isBackgroundCheck: false);
            UpdateSystemUpdateUi(res);

            if (!res.IsUpdateAvailable && !res.HasError)
            {
                TriggerToast(new ToastPayload(
                    Icon: "SHIELD",
                    Title: $"Windows Dynamic Island {res.CurrentVersion}",
                    Subtitle: loc.Format("Updater_UpToDate", res.CurrentVersion),
                    AccentHex: "#38BDF8",
                    Duration: TimeSpan.FromSeconds(2.6)));
            }
        }
        finally
        {
            SysCheckUpdateBtnText.Text = loc.Get("Settings_CheckUpdateBtn", "Check Update");
            SysCheckUpdateBtn.IsEnabled = true;
        }
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var updaterWin = new SetupWindow(SetupWindowMode.Update, _updateService);
            updaterWin.Show();
            updaterWin.Activate();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("MainWindow.OnInstallUpdateClick", ex.Message, ex);
        }
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

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_stateMachine.IsSpotlightActive)
        {
            CloseSpotlight();
        }
    }

    // =========================================================================
    // FEATURE 1: SPOTLIGHT QUICK COMMAND & APP LAUNCHER (Alt + Spasi)
    // =========================================================================
}
