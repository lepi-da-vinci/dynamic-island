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
    private readonly WifiService _wifiService = new();
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
    private int _topHandleHoverTicks;
    private int _tuckCooldownTicks;
    private double _cachedDpiScaleX = 1.0;
    private double _cachedDpiScaleY = 1.0;

    // Interactive timeline & slider scrubbing states
    private bool _isDraggingTimeline;
    private double _draggedTimelineRatio;
    private bool _isDraggingToastSlider;
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
    private DateOnly _selectedCalendarDate = DateOnly.FromDateTime(DateTime.Now);
    private int _displayedCalendarYear = DateTime.Now.Year;
    private int _displayedCalendarMonth = DateTime.Now.Month;
    private readonly HashSet<string> _firedCalendarReminders = new(StringComparer.Ordinal);
    private int _lastReminderMinuteChecked = -1;
    private ImageSource? _lastCrossfadeThumb;
    private string _lastAppliedWeatherManualKey = string.Empty;
    private int _ecoTickDivider;

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
                Subtitle: "Buka Pengaturan di Dynamic Island untuk pasang",
                AccentHex: "#22C55E",
                Duration: TimeSpan.FromSeconds(4.5)));
        });

        _batteryLimitService.LimitModeChanged += _ => Dispatcher.InvokeAsync(UpdateHardwareUi);
        _notificationHistoryService.HistoryChanged += () => Dispatcher.InvokeAsync(RefreshNotificationHistoryUi);
        _weatherService.WeatherUpdated += _ => Dispatcher.InvokeAsync(RefreshWeatherUi);
        _calendarService.ScheduleChanged += () => Dispatcher.InvokeAsync(() =>
        {
            RefreshCalendarMonthGrid();
            RefreshScheduleUi();
        });
        _fileShelfShareService.FileReceivedFromPhone += OnFileReceivedFromPhone;
        _flyoutSuppressor = new WindowsFlyoutSuppressor(_settingsService.SuppressWindowsNativeOsd);
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
            Dispatcher.InvokeAsync(() =>
            {
                ShowVolumeToast(vol, muted, muteToggled);
                _settingsWindow?.RefreshSystemControlCenterCard();
            });
        };

        _audioService.MicrophoneMuteChanged += micMuted =>
            Dispatcher.InvokeAsync(() =>
            {
                ShowMicMuteToast(micMuted);
                _settingsWindow?.RefreshSystemControlCenterCard();
            });

        _audioService.OutputDeviceChanged += (devName, isHeadphone) =>
            Dispatcher.InvokeAsync(() =>
            {
                UpdateMediaAndLyricsUi();
                _settingsWindow?.RefreshSystemControlCenterCard();
            });

        _brightnessService.BrightnessChanged += brt =>
        {
            if (_settingsService.SuppressWindowsNativeOsd)
            {
                WindowsFlyoutSuppressor.TriggerSuppressionBurst(durationMs: 700, intervalMs: 20);
                WindowsFlyoutSuppressor.SuppressOnce();
            }
            Dispatcher.InvokeAsync(() =>
            {
                ShowBrightnessToast(brt);
                _settingsWindow?.RefreshSystemControlCenterCard();
            });
        };

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        DpiChanged += OnDpiChanged;

        _stateMachine.SetAutoTuckEnabled(_settingsService.AutoTuckEnabled);
        _settingsService.SettingsChanged += OnAppSettingsChanged;

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
        AppLogger.LogInfo("WindowLifecycle", $"MainWindow.OnClosing called. Cancel={e.Cancel}.");
        base.OnClosing(e);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", "MainWindow.OnWindowClosed called.");
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
        _settingsService.SettingsChanged -= OnAppSettingsChanged;

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

    private void RefreshCachedDpi()
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0.1) _cachedDpiScaleX = dpi.DpiScaleX;
            if (dpi.DpiScaleY > 0.1) _cachedDpiScaleY = dpi.DpiScaleY;
        }
        catch { }
    }

    private void OnAppSettingsChanged() => Dispatcher.InvokeAsync(ApplyAppSettings);

    private void OnFastUpdateTick(object? sender, EventArgs e)
    {
        try
        {
            _hardwareService.PollFastKeyboardIndicators();
            _audioService.PollExternalChanges();
            _brightnessService.PollExternalChanges();
            _notificationService.PollNewNotifications();

            bool isTuckedOrHidden = _stateMachine.CurrentState is IslandVisualState.TuckedTopEdge or IslandVisualState.Hidden || _stateMachine.IsFullScreenHidden;

            if (!isTuckedOrHidden)
            {
            // Brightness polling already runs unconditionally above so hardware keys wake tucked island
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

            // Check today's upcoming Calendar agenda reminders once per minute change
            DateTime nowLocal = DateTime.Now;
            int currentMinuteKey = (nowLocal.Hour * 60) + nowLocal.Minute;
            if (currentMinuteKey != _lastReminderMinuteChecked)
            {
                _lastReminderMinuteChecked = currentMinuteKey;
                foreach (var (schedItem, minsUntil) in _calendarService.CheckUpcomingReminders(nowLocal, _firedCalendarReminders))
                {
                    TriggerToast(new ToastPayload(
                        Icon: "SHIELD",
                        Title: LocalizationService.Instance.Get("Cal_ReminderTitle", "Schedule Reminder"),
                        Subtitle: minsUntil == 0
                            ? LocalizationService.Instance.Format("Cal_StartsSoonFmt", schedItem.Title, schedItem.Time)
                            : $"{schedItem.Title} ({minsUntil}m • {schedItem.Time})",
                        AccentHex: schedItem.AccentHex,
                        Duration: TimeSpan.FromSeconds(4.2)));
                }
            }

            bool skipEcoIdleTelemetry = _settingsService.AutoBatteryEcoMode &&
                                        _hardwareService.Current.IsEcoPowerSaverActive &&
                                        !_hardwareService.IsPerformanceViewActive &&
                                        ((++_ecoTickDivider % 3) != 0);

            if (!_stateMachine.IsFullScreenHidden && !skipEcoIdleTelemetry)
            {
                _hardwareService.RefreshFullTelemetry();
                UpdateHardwareUi();
                if (_settingsWindow is { IsVisible: true })
                {
                    _settingsWindow.RefreshSystemControlCenterCard();
                }
                if (_stateMachine.CurrentState == IslandVisualState.PinnedPerformancePill ||
                    (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                     _stateMachine.ActiveTab == IslandDashboardTab.PerformanceMonitor))
                {
                    UpdatePerformanceUi();
                }
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
            (Color topSurface, Color bottomSurface) = _settingsService.IslandSurfaceTheme switch
            {
                "MidnightCarbon" => (
                    Color.FromArgb(surfaceAlpha, 5, 8, 15),
                    Color.FromArgb(surfaceAlpha, 14, 22, 38)),
                "FrostedGraphite" => (
                    Color.FromArgb(surfaceAlpha, 12, 13, 17),
                    Color.FromArgb(surfaceAlpha, 26, 28, 36)),
                "CyberMatrix" => (
                    Color.FromArgb(surfaceAlpha, 3, 11, 9),
                    Color.FromArgb(surfaceAlpha, 8, 28, 22)),
                "RoyalAmethyst" => (
                    Color.FromArgb(surfaceAlpha, 9, 5, 17),
                    Color.FromArgb(surfaceAlpha, 24, 13, 42)),
                "CrimsonObsidian" => (
                    Color.FromArgb(surfaceAlpha, 13, 4, 8),
                    Color.FromArgb(surfaceAlpha, 32, 10, 18)),
                "AuroraOcean" => (
                    Color.FromArgb(surfaceAlpha, 4, 12, 19),
                    Color.FromArgb(surfaceAlpha, 9, 30, 44)),
                "Monochrome" => (
                    Color.FromArgb(surfaceAlpha, 9, 9, 11),
                    Color.FromArgb(surfaceAlpha, 21, 21, 25)),
                _ => (
                    Color.FromArgb(surfaceAlpha, 0, 0, 0),
                    Color.FromArgb(surfaceAlpha, 7, 7, 10))
            };

            if (IsMonochromeTheme && _settingsService.IslandSurfaceTheme != "Monochrome")
            {
                topSurface = MonochromeThemeHelper.ToMonochromeColor(topSurface);
                bottomSurface = MonochromeThemeHelper.ToMonochromeColor(bottomSurface);
            }

            var surfaceGrad = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0.0),
                EndPoint = new Point(0.5, 1.0)
            };
            surfaceGrad.GradientStops.Add(new GradientStop(topSurface, 0.0));
            surfaceGrad.GradientStops.Add(new GradientStop(bottomSurface, 1.0));
            surfaceGrad.Freeze();
            UnifiedNotchPath.Fill = surfaceGrad;

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
                SetAmbientGlowColors(themeColor, themeColor, animate: false);
                TuckedAccentBar.Background = BrushCache.Get(themeColor);
                if (_mediaService.CurrentTrack.HasActiveSession && _mediaService.CurrentTrack.Title != "Tidak Ada Media")
                {
                    ApplyDominantColor(_mediaService.CurrentTrack.DominantColor, _mediaService.CurrentTrack.SecondaryColor);
                }
            }
            else if (_mediaService.CurrentTrack.HasActiveSession && _mediaService.CurrentTrack.Title != "Tidak Ada Media")
            {
                themeColor = _mediaService.CurrentTrack.DominantColor;
                ApplyDominantColor(themeColor, _mediaService.CurrentTrack.SecondaryColor);
            }

            _currentThemeAccentColor = themeColor;
            var accentBrush = BrushCache.Get(themeColor);
            Application.Current.Resources["AppAccentBrush"] = accentBrush;
            HeaderSettingsGearIcon.Fill = accentBrush;
            LeftWingChevron.Fill = accentBrush;
            RightWingChevron.Fill = accentBrush;
            IdleSensorInnerRing.BorderBrush = BrushCache.Get(Color.FromArgb(68, themeColor.R, themeColor.G, themeColor.B));
            IdleSensorGlossDot.Fill = BrushCache.Get(Color.FromArgb(145, themeColor.R, themeColor.G, themeColor.B));

            MonochromeThemeHelper.ApplyToSubtree(UnifiedMasterGrid, IsMonochromeTheme, MonochromeExcludedNames);

            bool showRim = _settingsService.ShowContourRim && _settingsService.RimGlowStyle != "Hidden";
            UnifiedNotchRimPath.Visibility = showRim ? Visibility.Visible : Visibility.Collapsed;
            if (_settingsService.RimGlowStyle == "AccentNeon")
            {
                Color brightAccent = Color.FromArgb(
                    210,
                    (byte)Math.Min(255, themeColor.R + 45),
                    (byte)Math.Min(255, themeColor.G + 45),
                    (byte)Math.Min(255, themeColor.B + 45));
                var neonGrad = new LinearGradientBrush(
                    Color.FromArgb(140, themeColor.R, themeColor.G, themeColor.B),
                    brightAccent,
                    new Point(0.0, 0.0),
                    new Point(1.0, 1.0));
                neonGrad.Freeze();
                UnifiedNotchRimPath.Stroke = neonGrad;
                UnifiedNotchRimPath.StrokeThickness = 1.45;
            }
            else if (_settingsService.RimGlowStyle == "PrismGlass")
            {
                var prismGrad = new LinearGradientBrush
                {
                    StartPoint = new Point(0.0, 0.0),
                    EndPoint = new Point(1.0, 1.0)
                };
                prismGrad.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 0.0));
                prismGrad.GradientStops.Add(new GradientStop(Color.FromArgb(140, themeColor.R, themeColor.G, themeColor.B), 0.48));
                prismGrad.GradientStops.Add(new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.78));
                prismGrad.GradientStops.Add(new GradientStop(Color.FromArgb(125, themeColor.R, themeColor.G, themeColor.B), 1.0));
                prismGrad.Freeze();
                UnifiedNotchRimPath.Stroke = prismGrad;
                UnifiedNotchRimPath.StrokeThickness = 1.25;
            }
            else
            {
                var subtleGrad = new LinearGradientBrush(
                    Color.FromArgb(22, 255, 255, 255),
                    Color.FromArgb(56, 255, 255, 255),
                    new Point(0.5, 0.0),
                    new Point(0.5, 1.0));
                subtleGrad.Freeze();
                UnifiedNotchRimPath.Stroke = subtleGrad;
                UnifiedNotchRimPath.StrokeThickness = 1.05;
            }

            // 2b. Customizable 9 Notch Tabs (Order + Visibility) & DWM Capture Shield
            ApplyTabVisibilityAndOrder();

            _hardwareService.AutoBatteryEcoModeEnabled = _settingsService.AutoBatteryEcoMode;
            string manualWeatherKey = $"{_settingsService.WeatherManualCity}|{_settingsService.WeatherManualLat}|{_settingsService.WeatherManualLon}";
            if (!string.Equals(manualWeatherKey, _lastAppliedWeatherManualKey, StringComparison.Ordinal))
            {
                _lastAppliedWeatherManualKey = manualWeatherKey;
                if (!string.IsNullOrWhiteSpace(_settingsService.WeatherManualCity) &&
                    _settingsService.WeatherManualLat.HasValue &&
                    _settingsService.WeatherManualLon.HasValue)
                {
                    _weatherService.SetManualLocation(_settingsService.WeatherManualCity, _settingsService.WeatherManualLat.Value, _settingsService.WeatherManualLon.Value);
                }
                else
                {
                    _weatherService.ClearManualLocation();
                }
                _ = _weatherService.GetWeatherAsync(forceRefresh: true);
            }

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
            UpdateMediaAndLyricsUi();
            UpdatePerformanceUi();
            RefreshWeatherUi();
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
            if (ScheduleSaveBtnText != null) ScheduleSaveBtnText.Text = loc.Get("Cal_Save");

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

    private void SetAmbientGlowColors(Color c1, Color c2, bool animate = true)
    {
        if (AmbientGlowStop1 == null || AmbientGlowStop2 == null) return;
        if (!animate)
        {
            AmbientGlowStop1.BeginAnimation(GradientStop.ColorProperty, null);
            AmbientGlowStop2.BeginAnimation(GradientStop.ColorProperty, null);
            AmbientGlowStop1.Color = c1;
            AmbientGlowStop2.Color = c2;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        AmbientGlowStop1.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        AmbientGlowStop2.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c2, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
    }

    private void ApplyTabVisibilityAndOrder()
    {
        var order = _settingsService.GetNormalizedTabOrder();
        var dockButtons = new Dictionary<string, Button>(StringComparer.Ordinal)
        {
            ["MediaLyrics"] = TabMediaBtn,
            ["TodoAndWater"] = TabTodoBtn,
            ["Pomodoro"] = TabPomodoroBtn,
            ["CalendarSchedule"] = TabCalendarBtn,
            ["Weather"] = TabWeatherBtn,
            ["NotificationHistory"] = TabNotifBtn,
            ["AppShortcuts"] = TabShortcutsBtn,
            ["PerformanceMonitor"] = TabPerformanceBtn,
            ["FileShelf"] = TabShelfBtn,
            ["Wifi"] = TabWifiBtn
        };
        var pageDots = new Dictionary<string, Border>(StringComparer.Ordinal)
        {
            ["MediaLyrics"] = PageDot0,
            ["TodoAndWater"] = PageDot1,
            ["Pomodoro"] = PageDot2,
            ["CalendarSchedule"] = PageDotCal,
            ["Weather"] = PageDotWeather,
            ["NotificationHistory"] = PageDotNotif,
            ["AppShortcuts"] = PageDotShortcuts,
            ["PerformanceMonitor"] = PageDotPerf,
            ["FileShelf"] = PageDot3,
            ["Wifi"] = PageDotWifi
        };

        TabMediaBtn.Visibility = _settingsService.ShowTabMedia ? Visibility.Visible : Visibility.Collapsed;
        TabTodoBtn.Visibility = _settingsService.ShowTabTodo ? Visibility.Visible : Visibility.Collapsed;
        TabPomodoroBtn.Visibility = _settingsService.ShowTabPomodoro ? Visibility.Visible : Visibility.Collapsed;
        TabCalendarBtn.Visibility = _settingsService.ShowTabCalendar ? Visibility.Visible : Visibility.Collapsed;
        TabWeatherBtn.Visibility = _settingsService.ShowTabWeather ? Visibility.Visible : Visibility.Collapsed;
        TabNotifBtn.Visibility = _settingsService.ShowTabNotifications ? Visibility.Visible : Visibility.Collapsed;
        TabShortcutsBtn.Visibility = _settingsService.ShowTabShortcuts ? Visibility.Visible : Visibility.Collapsed;
        TabPerformanceBtn.Visibility = _settingsService.ShowTabPerformance ? Visibility.Visible : Visibility.Collapsed;
        TabShelfBtn.Visibility = _settingsService.ShowTabShelf ? Visibility.Visible : Visibility.Collapsed;
        TabWifiBtn.Visibility = _settingsService.ShowTabWifi ? Visibility.Visible : Visibility.Collapsed;

        if (BottomDockButtonsPanel != null)
        {
            BottomDockButtonsPanel.Children.Clear();
            foreach (string key in order)
            {
                if (dockButtons.TryGetValue(key, out var btn))
                {
                    BottomDockButtonsPanel.Children.Add(btn);
                }
            }
        }

        if (DotsPaginationPanel != null)
        {
            DotsPaginationPanel.Children.Clear();
            foreach (string key in order)
            {
                if (pageDots.TryGetValue(key, out var dot))
                {
                    DotsPaginationPanel.Children.Add(dot);
                }
            }
        }
    }

    private void OnFileReceivedFromPhone(string filePath)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _fileShelfService.AddFiles([filePath]);
                TriggerToast(new ToastPayload(
                    Icon: "FOLDER",
                    Title: LocalizationService.Instance.Get("Qr_ReceivedTitle", "File Received from Phone"),
                    Subtitle: System.IO.Path.GetFileName(filePath),
                    AccentHex: "#22C55E",
                    Duration: TimeSpan.FromSeconds(3.2)));
            }
            catch { }
        });
    }

    private void UpdateSystemUpdateUi(UpdateCheckResult res)
    {
        bool isMono = IsMonochromeTheme;
        if (res.IsUpdateAvailable)
        {
            HeaderSettingsUpdateDot.Visibility = Visibility.Visible;
            HeaderSettingsUpdateDot.Fill = isMono ? BrushCache.Get(Color.FromRgb(228, 228, 231)) : BrushCache.Get(Color.FromRgb(34, 197, 94));
        }
        else
        {
            HeaderSettingsUpdateDot.Visibility = Visibility.Collapsed;
        }
        _settingsWindow?.RefreshUpdateCard(res);
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
