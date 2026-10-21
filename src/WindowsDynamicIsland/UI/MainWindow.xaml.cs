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

public partial class MainWindow : Window
{
    private readonly IslandStateMachine _stateMachine = new();
    private readonly IslandMorphController _morph = new();
    private readonly MediaSessionService _mediaService = new();
    private readonly LyricsService _lyricsService = new();
    private readonly AudioService _audioService = new();
    private readonly HardwareTelemetryService _hardwareService = new();
    private readonly FileShelfService _fileShelfService = new();
    private readonly PomodoroTimerService _pomodoroService = new();

    private readonly DispatcherTimer _oneSecondTimer;
    private readonly DispatcherTimer _fastUpdateTimer;
    private readonly DispatcherTimer _toastDismissTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;

    private readonly double[] _spectrumHeights = [5, 8, 12, 8, 5];
    private Rectangle[] _waveRectangles = [];

    private IntPtr _hwnd;
    private Win32Interop.NOTIFYICONDATA _trayIconData;
    private bool _isRenderLoopAttached;
    private long _lastRenderTick;
    private string _lastClipboardPreview = string.Empty;
    private readonly bool _autoHideFullScreen = true;

    // Lyric animation tracking
    private int _lastAnimatedLyricIndex = -999;
    private string _lastDisplayedCompactText = string.Empty;
    private double _marqueeOffset;
    private double _marqueeDirection = 1.0;
    private double _marqueePauseTimer;

    public MainWindow()
    {
        InitializeComponent();

        _waveRectangles = [WaveBar0, WaveBar1, WaveBar2, WaveBar3, WaveBar4];
        ShelfItemsControl.ItemsSource = _fileShelfService.Items;
        _fileShelfService.Items.CollectionChanged += (_, _) => UpdateShelfUi();

        _oneSecondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _oneSecondTimer.Tick += OnOneSecondTick;

        // 110ms fast timer for responsive lyric sync, keyboard indicators, and smart top-bar auto-tuck
        _fastUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _fastUpdateTimer.Tick += OnFastUpdateTick;

        _toastDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastDismissTimer.Tick += (_, _) =>
        {
            _toastDismissTimer.Stop();
            _stateMachine.ClearToast();
        };

        _collapseDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _collapseDebounceTimer.Tick += (_, _) =>
        {
            _collapseDebounceTimer.Stop();
            if (!IslandBorder.IsMouseOver)
            {
                _stateMachine.SetHovered(false);
            }
        };

        _stateMachine.StateChanged += OnIslandStateChanged;
        _mediaService.TrackChanged += OnMediaTrackChanged;
        _mediaService.PlaybackStateChanged += OnMediaPlaybackStateChanged;
        _lyricsService.LyricsUpdated += () => Dispatcher.InvokeAsync(UpdateMediaAndLyricsUi);
        _hardwareService.HardwareToastTriggered += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));
        _pomodoroService.TimerTicked += () => Dispatcher.InvokeAsync(UpdatePomodoroAndClockUi);
        _pomodoroService.TimerCompleted += toast => Dispatcher.InvokeAsync(() => TriggerToast(toast));

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
        UpdateHardwareUi();
        UpdatePomodoroAndClockUi();

        _oneSecondTimer.Start();
        _fastUpdateTimer.Start();

        TriggerToast(new ToastPayload(
            Icon: "🏝️",
            Title: "Dynamic Island Aktif",
            Subtitle: "Sentuh bibir atas layar untuk buka • Otomatis menghindar di tab",
            AccentHex: "#38BDF8",
            Duration: TimeSpan.FromSeconds(2.8)));

        await _mediaService.InitializeAsync();
        EnsureRenderLoopRunning();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        StopRenderLoop();
        _oneSecondTimer.Stop();
        _fastUpdateTimer.Stop();
        _mediaService.Dispose();

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
            // Hold Left Alt to click straight through the Dynamic Island to browser tabs behind it
            if ((Win32Interop.GetAsyncKeyState(Win32Interop.VK_LMENU) & 0x8000) != 0 ||
                _stateMachine.CurrentState == IslandVisualState.Hidden)
            {
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
                _stateMachine.SetManuallyTucked(false);
                _stateMachine.TogglePinnedExpanded();
            }
            else if (mouseMsg == Win32Interop.WM_RBUTTONUP)
            {
                _stateMachine.SetManuallyTucked(false);
                _stateMachine.SetActiveTab(IslandDashboardTab.SystemStatus);
                _stateMachine.TogglePinnedExpanded();
            }
        }

        return IntPtr.Zero;
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
                    if (singleLine.Length > 34)
                    {
                        singleLine = singleLine[..34] + "…";
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
                        Subtitle: "Tip: Drag ke Island untuk simpan di File Shelf",
                        AccentHex: "#38BDF8",
                        Duration: TimeSpan.FromSeconds(2.2)));
                }
            }
        }
        catch
        {
            // Ignore clipboard lock
        }
    }

    private void OnIslandStateChanged(IslandVisualState state, IslandDashboardTab tab)
    {
        var (w, h, corner, glow, topOffset) = _stateMachine.GetTargetDimensions();
        bool expanding = w > _morph.Width.Current || h > _morph.Height.Current;
        _morph.MorphTo(w, h, corner, glow, topOffset, bouncy: expanding);

        TuckedHandlePanel.Visibility = state == IslandVisualState.TuckedTopEdge ? Visibility.Visible : Visibility.Collapsed;
        IdlePillPanel.Visibility = state == IslandVisualState.IdlePill ? Visibility.Visible : Visibility.Collapsed;
        CompactMediaPanel.Visibility = state == IslandVisualState.CompactMedia ? Visibility.Visible : Visibility.Collapsed;
        ToastPanel.Visibility = state == IslandVisualState.ToastNotification ? Visibility.Visible : Visibility.Collapsed;
        ExpandedDashboardPanel.Visibility = state == IslandVisualState.ExpandedDashboard ? Visibility.Visible : Visibility.Collapsed;

        IslandBorder.Opacity = state == IslandVisualState.Hidden ? 0.0 : 1.0;

        MediaTabGrid.Visibility = tab == IslandDashboardTab.MediaLyrics ? Visibility.Visible : Visibility.Collapsed;
        ShelfTabGrid.Visibility = tab == IslandDashboardTab.FileShelf ? Visibility.Visible : Visibility.Collapsed;
        PomodoroTabGrid.Visibility = tab == IslandDashboardTab.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        SystemTabGrid.Visibility = tab == IslandDashboardTab.SystemStatus ? Visibility.Visible : Visibility.Collapsed;

        UpdateTabHighlight();
        EnsureRenderLoopRunning();
    }

    private void UpdateTabHighlight()
    {
        var activeBrush = new SolidColorBrush(Color.FromArgb(48, 56, 189, 248));
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

        double topMargin = Math.Max(0, _morph.TopOffset.Current);
        IslandBorder.Margin = new Thickness(0, topMargin, 0, 0);
        IslandBorder.Width = Math.Max(36, _morph.Width.Current);
        IslandBorder.Height = Math.Max(4, _morph.Height.Current);
        IslandBorder.CornerRadius = new CornerRadius(Math.Max(2, _morph.CornerRadius.Current));

        AmbientGlowBorder.Margin = IslandBorder.Margin;
        AmbientGlowBorder.Width = IslandBorder.Width + 6;
        AmbientGlowBorder.Height = IslandBorder.Height + 6;
        AmbientGlowBorder.CornerRadius = IslandBorder.CornerRadius;
        AmbientGlowBorder.Opacity = _morph.GlowOpacity.Current;

        bool isCompactMedia =
            _stateMachine.CurrentState == IslandVisualState.CompactMedia &&
            _mediaService.CurrentTrack.HasActiveSession;

        bool isExpandedMedia =
            _stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
            _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics &&
            _mediaService.CurrentTrack.HasActiveSession;

        if (isCompactMedia)
        {
            _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 17.5);
            for (int i = 0; i < _waveRectangles.Length; i++)
            {
                _waveRectangles[i].Height = _spectrumHeights[i];
            }

            UpdateCompactMarqueeScroll(dt);
        }
        else if (isExpandedMedia)
        {
            float peak = _audioService.GetCurrentPeakLevel(_mediaService.CurrentTrack.IsPlaying);
            LyricsStageGlowOrb.Opacity = 0.14 + Math.Clamp(peak * 0.45, 0.0, 0.35);
            LyricsStageGlowOrb.Width = 170 + peak * 90;
        }

        if (!morphActive && !isCompactMedia && !isExpandedMedia)
        {
            StopRenderLoop();
        }
    }

    private void UpdateCompactMarqueeScroll(double dt)
    {
        double textWidth = CompactSongTitleText.ActualWidth;
        double viewportWidth = CompactLyricViewport.ActualWidth;

        if (viewportWidth <= 10 || textWidth <= viewportWidth + 4)
        {
            CompactLyricTranslate.X = 0;
            _marqueeOffset = 0;
            return;
        }

        if (_marqueePauseTimer > 0)
        {
            _marqueePauseTimer -= dt;
            return;
        }

        double maxOverflow = textWidth - viewportWidth + 12;
        _marqueeOffset += _marqueeDirection * 28.0 * dt;

        if (_marqueeOffset >= maxOverflow)
        {
            _marqueeOffset = maxOverflow;
            _marqueeDirection = -1.0;
            _marqueePauseTimer = 1.1;
        }
        else if (_marqueeOffset <= 0)
        {
            _marqueeOffset = 0;
            _marqueeDirection = 1.0;
            _marqueePauseTimer = 1.4;
        }

        CompactLyricTranslate.X = -_marqueeOffset;
    }

    private void OnFastUpdateTick(object? sender, EventArgs e)
    {
        _hardwareService.PollFastKeyboardIndicators();

        // Update live lyrics at ~9Hz so lyric transitions & karaoke progress bar feel instantaneous
        if (_mediaService.CurrentTrack.HasActiveSession)
        {
            UpdateMediaAndLyricsUi();
        }

        UpdateSmartTopBarAutoTuck();
    }

    /// <summary>
    /// Automatically tucks the Dynamic Island into a thin 6.5px strip at the top bezel ("Tersimpan di Atas")
    /// when an application has clickable tabs/buttons at the top of the screen and the user moves their mouse
    /// into the tab bar zone (Y = 7..52px). Touching the very top bezel (Y <= 5px) opens the Island!
    /// </summary>
    private void UpdateSmartTopBarAutoTuck()
    {
        if (!Win32Interop.GetCursorPos(out var pt)) return;

        int screenW = (int)SystemParameters.PrimaryScreenWidth;
        int centerPx = screenW / 2;
        bool inHorizontalIslandZone = Math.Abs(pt.X - centerPx) <= 230;

        // If user pushes cursor to the very top bezel (Y <= 4px) in the center, reveal & expand the island!
        if (pt.Y <= 4 && Math.Abs(pt.X - centerPx) <= 95)
        {
            _collapseDebounceTimer.Stop();
            _stateMachine.SetManuallyTucked(false);
            _stateMachine.SetAutoDodgingTopBar(false);
            _stateMachine.SetHovered(true);
            return;
        }

        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
        {
            return;
        }

        if (!_stateMachine.AutoDodgeEnabled)
        {
            _stateMachine.SetAutoDodgingTopBar(false);
            return;
        }

        bool appCoversTop = Win32Interop.IsForegroundAppOccupyingTopBar(_hwnd, screenW);
        if (!appCoversTop)
        {
            _stateMachine.SetAutoDodgingTopBar(false);
            return;
        }

        // When an app (like Chrome/Edge/VS Code) covers the top bar:
        // If cursor is in the tab bar band (Y = 6..54px), tuck the island into the top bezel so tabs are 100% clickable!
        if (pt.Y >= 6 && pt.Y <= 54 && inHorizontalIslandZone)
        {
            _stateMachine.SetAutoDodgingTopBar(true);
        }
        else if (pt.Y > 62 || !inHorizontalIslandZone)
        {
            _stateMachine.SetAutoDodgingTopBar(false);
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
        IdleBatteryIconText.Text = hw.IsCharging ? "⚡" : "🔋";
        IdleBatteryIconText.Foreground = new SolidColorBrush(
            hw.IsCharging ? Color.FromRgb(34, 197, 94) :
            hw.BatteryPercent <= 20 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(148, 163, 184));

        CameraPrivacyDot.Visibility = hw.IsCameraInUse ? Visibility.Visible : Visibility.Collapsed;
        MicPrivacyDot.Visibility = hw.IsMicrophoneInUse ? Visibility.Visible : Visibility.Collapsed;

        SysBatteryDetailText.Text = hw.HasBattery
            ? $"{(hw.IsCharging ? "⚡ Charging" : "🔋 Baterai")} {hw.BatteryPercent}%"
            : "⚡ Daya Desktop AC (100%)";
        SysNetworkDetailText.Text = hw.NetworkName;

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

        TimeSpan pos = _mediaService.GetInterpolatedPosition();
        TimeSpan dur = track.Duration;

        ElapsedTimeText.Text = FormatTimeSpan(pos);
        TotalDurationText.Text = FormatTimeSpan(dur);

        double trackWidth = Math.Max(0, TimelineTrackBorder.ActualWidth);
        if (dur.TotalSeconds > 0.5 && trackWidth > 0)
        {
            double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
            TimelineFillBorder.Width = trackWidth * ratio;
        }
        else
        {
            TimelineFillBorder.Width = 0;
        }

        // Retrieve 3-line synchronized lyrics + karaoke progress
        var lyricSnap = _lyricsService.GetActiveLyrics(pos);

        // Update Karaoke Line Progress Bar smoothly
        LyricLineProgressFill.Width = 110.0 * Math.Clamp(lyricSnap.LineProgressRatio, 0.0, 1.0);

        // Determine what text to show in Compact Media Pill (Gambar 4: Live Moving Lyrics!)
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
            _marqueeOffset = 0;
            _marqueeDirection = 1.0;
            _marqueePauseTimer = 0.9;
            CompactLyricTranslate.X = 0;

            // Animate vertical slide-up + fade-in inside the Compact Pill
            var slideUp = new DoubleAnimation(13.0, 0.0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(280));
            CompactLyricTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            CompactSongTitleText.BeginAnimation(OpacityProperty, fadeIn);
        }

        // Animate 3-Line Expanded Lyrics Stage (Gambar 3) when moving to a new lyric line
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

        // 1. Cascade Slide-Up on the entire 3-line stack
        var stageSlide = new DoubleAnimation(16.0, 0.0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = easeOut
        };
        LyricsStageTranslate.BeginAnimation(TranslateTransform.YProperty, stageSlide);

        // 2. Elastic Pop Scale on the Active Lyric Line
        var scalePop = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = springBack
        };
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleXProperty, scalePop);
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleYProperty, scalePop);

        // 3. Smooth Opacity Fade-In on the Active Lyric Line
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

        if (toast.ProgressRatio.HasValue)
        {
            ToastProgressContainer.Visibility = Visibility.Visible;
            double ratio = Math.Clamp(toast.ProgressRatio.Value, 0.0, 1.0);
            ToastProgressFill.Width = 58.0 * ratio;
            ToastProgressFill.Background = new SolidColorBrush(accent);
        }
        else
        {
            ToastProgressContainer.Visibility = Visibility.Collapsed;
        }

        _stateMachine.ShowToast(toast);

        _toastDismissTimer.Stop();
        _toastDismissTimer.Interval = toast.Duration ?? TimeSpan.FromSeconds(2.2);
        _toastDismissTimer.Start();
    }

    // --- MOUSE & GESTURE HANDLERS ---

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDebounceTimer.Stop();

        // Check if an app has tabs behind the island and cursor entered below the top bezel (Y > 6px):
        // If so, auto-tuck into the top bezel instead of blocking the user's browser tab!
        if (_stateMachine.AutoDodgeEnabled &&
            _stateMachine.CurrentState is IslandVisualState.CompactMedia or IslandVisualState.IdlePill &&
            Win32Interop.GetCursorPos(out var pt) &&
            pt.Y > 6 &&
            Win32Interop.IsForegroundAppOccupyingTopBar(_hwnd, (int)SystemParameters.PrimaryScreenWidth))
        {
            _stateMachine.SetAutoDodgingTopBar(true);
            return;
        }

        _stateMachine.SetManuallyTucked(false);
        _stateMachine.SetAutoDodgingTopBar(false);
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
        if (_stateMachine.CurrentState != IslandVisualState.ExpandedDashboard)
        {
            _stateMachine.SetManuallyTucked(false);
            _stateMachine.SetAutoDodgingTopBar(false);
            _stateMachine.SetHovered(true);
        }
    }

    private void OnIslandMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _stateMachine.SetManuallyTucked(false);
        _stateMachine.CycleNextTab();
        _stateMachine.SetHovered(true);
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
        int newVol = _audioService.AdjustMasterVolume(step);
        string icon = newVol == 0 ? "🔇" : newVol < 45 ? "🔉" : "🔊";

        TriggerToast(new ToastPayload(
            Icon: icon,
            Title: $"Volume {newVol}%",
            Subtitle: "Output Audio Sistem",
            AccentHex: "#38BDF8",
            ProgressRatio: newVol / 100.0,
            Duration: TimeSpan.FromSeconds(1.5)));

        e.Handled = true;
    }

    // --- DRAG AND DROP FILE SHELF HANDLERS ---

    private void OnIslandDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            _collapseDebounceTimer.Stop();
            _stateMachine.SetManuallyTucked(false);
            _stateMachine.SetAutoDodgingTopBar(false);
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
    private void OnTabSystemClick(object sender, RoutedEventArgs e) => _stateMachine.SetActiveTab(IslandDashboardTab.SystemStatus);

    private void OnManualTuckClick(object sender, RoutedEventArgs e)
    {
        _stateMachine.SetManuallyTucked(true);
    }

    private async void OnMediaPrevClick(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void OnMediaPlayPauseClick(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void OnMediaNextClick(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    private void OnPomodoroStartPauseClick(object sender, RoutedEventArgs e) => _pomodoroService.ToggleStartPause();
    private void OnPomodoroResetFocusClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(25, isBreak: false);
    private void OnPomodoroBreakClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(5, isBreak: true);
    private void OnPomodoroAdd5MinClick(object sender, RoutedEventArgs e) => _pomodoroService.AddMinutes(5);

    private void OnToggleAutoDodgeClick(object sender, RoutedEventArgs e)
    {
        _stateMachine.AutoDodgeEnabled = !_stateMachine.AutoDodgeEnabled;
        ToggleAutoDodgeBtn.Content = _stateMachine.AutoDodgeEnabled
            ? "🔼 Auto-Simpan Tab: ON"
            : "📌 Auto-Simpan Tab: OFF";
    }

    private void OnToggleMuteClick(object sender, RoutedEventArgs e)
    {
        bool muted = _audioService.ToggleMute();
        ToggleMuteBtn.Content = muted ? "🔇 Unmute Audio" : "🔊 Mute Audio";
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
