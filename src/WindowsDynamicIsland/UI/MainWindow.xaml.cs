using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly DispatcherTimer _fastKeyboardTimer;
    private readonly DispatcherTimer _toastDismissTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;

    private readonly double[] _spectrumHeights = [5, 8, 12, 8, 5];
    private Rectangle[] _waveRectangles = [];

    private IntPtr _hwnd;
    private Win32Interop.NOTIFYICONDATA _trayIconData;
    private bool _isRenderLoopAttached;
    private long _lastRenderTick;
    private string _lastClipboardPreview = string.Empty;
    private bool _autoHideFullScreen = true;

    public MainWindow()
    {
        InitializeComponent();

        _waveRectangles = [WaveBar0, WaveBar1, WaveBar2, WaveBar3, WaveBar4];
        ShelfItemsControl.ItemsSource = _fileShelfService.Items;
        _fileShelfService.Items.CollectionChanged += (_, _) => UpdateShelfUi();

        _oneSecondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _oneSecondTimer.Tick += OnOneSecondTick;

        _fastKeyboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _fastKeyboardTimer.Tick += OnFastKeyboardTick;

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

        // Apply non-intrusive overlay styles: Topmost, ToolWindow (hidden from Alt+Tab), NoActivate (never steals focus)
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
        _fastKeyboardTimer.Start();

        await _mediaService.InitializeAsync();
        EnsureRenderLoopRunning();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        StopRenderLoop();
        _oneSecondTimer.Stop();
        _fastKeyboardTimer.Stop();
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
            szTip = "Windows Dynamic Island (Klik untuk Buka / Klik Kanan untuk Mode)"
        };

        Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_ADD, ref _trayIconData);
    }

    /// <summary>
    /// Custom Win32 WndProc for Dynamic Hit-Testing (HTTRANSPARENT outside pill bounds),
    /// Alt+Hover click-through bypass, Clipboard Peek, and System Tray interactions.
    /// </summary>
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

            long lp = lParam.ToInt64();
            int screenX = (short)(lp & 0xFFFF);
            int screenY = (short)((lp >> 16) & 0xFFFF);

            Point pt = PointFromScreen(new Point(screenX, screenY));
            double pillW = IslandBorder.Width;
            double pillH = IslandBorder.Height;
            double pillLeft = (ActualWidth - pillW) / 2.0;
            double pillTop = IslandBorder.Margin.Top;

            // Expand hit area slightly by 4px for smooth hover entry, while keeping the rest 100% click-through
            Rect activePillRect = new(pillLeft - 4, Math.Max(0, pillTop - 4), pillW + 8, pillH + 8);
            if (!activePillRect.Contains(pt))
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
                _stateMachine.TogglePinnedExpanded();
            }
            else if (mouseMsg == Win32Interop.WM_RBUTTONUP)
            {
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
            // Clipboard may be locked by another process
        }
    }

    private void OnIslandStateChanged(IslandVisualState state, IslandDashboardTab tab)
    {
        var (w, h, corner, glow) = _stateMachine.GetTargetDimensions();
        bool expanding = w > _morph.Width.Current || h > _morph.Height.Current;
        _morph.MorphTo(w, h, corner, glow, bouncy: expanding);

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
        double dt = (double)(now - _lastRenderTick) / Stopwatch.Frequency;
        _lastRenderTick = now;

        bool morphActive = _morph.Update(dt);

        IslandBorder.Width = Math.Max(36, _morph.Width.Current);
        IslandBorder.Height = Math.Max(4, _morph.Height.Current);
        IslandBorder.CornerRadius = new CornerRadius(Math.Max(2, _morph.CornerRadius.Current));

        AmbientGlowBorder.Width = IslandBorder.Width + 6;
        AmbientGlowBorder.Height = IslandBorder.Height + 6;
        AmbientGlowBorder.CornerRadius = IslandBorder.CornerRadius;
        AmbientGlowBorder.Opacity = _morph.GlowOpacity.Current;

        bool needAudioSpectrum =
            _stateMachine.CurrentState == IslandVisualState.CompactMedia &&
            _mediaService.CurrentTrack.HasActiveSession;

        if (needAudioSpectrum)
        {
            _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 17.5);
            for (int i = 0; i < _waveRectangles.Length; i++)
            {
                _waveRectangles[i].Height = _spectrumHeights[i];
            }
        }

        // Frame Rate Throttling: Drop to 0 FPS when spring animation settles and compact spectrum is not needed
        if (!morphActive && !needAudioSpectrum)
        {
            StopRenderLoop();
        }
    }

    private void OnFastKeyboardTick(object? sender, EventArgs e)
    {
        _hardwareService.PollFastKeyboardIndicators();
    }

    private void OnOneSecondTick(object? sender, EventArgs e)
    {
        // 1. Check Full-Screen Game / Presentation Mode
        if (_autoHideFullScreen)
        {
            bool fullScreen = Win32Interop.IsFullScreenAppActive();
            _stateMachine.SetFullScreenHidden(fullScreen);
        }

        // 2. Keep topmost z-order without stealing focus
        if (_hwnd != IntPtr.Zero && _stateMachine.CurrentState != IslandVisualState.Hidden)
        {
            Win32Interop.SetWindowPos(
                _hwnd,
                Win32Interop.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
        }

        // 3. Tick Pomodoro & Clock
        _pomodoroService.TickOneSecond();
        UpdatePomodoroAndClockUi();

        // 4. Refresh Hardware & Privacy Indicators
        _hardwareService.RefreshFullTelemetry();
        UpdateHardwareUi();

        // 5. Update Media Timeline & Synchronized Lyrics
        if (_mediaService.CurrentTrack.HasActiveSession)
        {
            UpdateMediaAndLyricsUi();
        }
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
        var barBrush = new SolidColorBrush(c);
        TimelineFillBorder.Background = barBrush;
        foreach (var rect in _waveRectangles)
        {
            rect.Fill = barBrush;
        }
    }

    private void UpdateMediaAndLyricsUi()
    {
        var track = _mediaService.CurrentTrack;
        CompactSongTitleText.Text = track.IsPlaying
            ? $"{track.Title} • {track.Artist}"
            : $"⏸ {track.Title}";

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

        var (currentLine, nextLine) = _lyricsService.GetActiveLines(pos);
        CurrentLyricLineText.Text = currentLine;
        NextLyricLineText.Text = nextLine;
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
            _stateMachine.SetHovered(true);
        }
    }

    private void OnIslandMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Right click cycles through dashboard tabs smoothly
        _stateMachine.CycleNextTab();
        _stateMachine.SetHovered(true);
        e.Handled = true;
    }

    private async void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle click: instant Play/Pause media toggle
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            await _mediaService.TogglePlayPauseAsync();
        }
    }

    private void OnIslandMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Scroll wheel over island: adjust system master volume with live toast OSD
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
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _collapseDebounceTimer.Stop();
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            int count = _fileShelfService.AddFiles(files);
            UpdateShelfUi();
            if (count > 0)
            {
                _stateMachine.SetActiveTab(IslandDashboardTab.FileShelf);
                _stateMachine.SetHovered(true);
            }
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
            ? "Taruh file sementara di sini (Drag Masuk & Keluar)"
            : $"📦 {count} file tersimpan di rak sementara (Tarik keluar untuk kirim)";
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

    private async void OnMediaPrevClick(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void OnMediaPlayPauseClick(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void OnMediaNextClick(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    private void OnPomodoroStartPauseClick(object sender, RoutedEventArgs e) => _pomodoroService.ToggleStartPause();
    private void OnPomodoroResetFocusClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(25, isBreak: false);
    private void OnPomodoroBreakClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(5, isBreak: true);
    private void OnPomodoroAdd5MinClick(object sender, RoutedEventArgs e) => _pomodoroService.AddMinutes(5);

    private void OnToggleMuteClick(object sender, RoutedEventArgs e)
    {
        bool muted = _audioService.ToggleMute();
        ToggleMuteBtn.Content = muted ? "🔇 Unmute Audio" : "🔊 Mute Audio";
    }

    private void OnToggleNotchModeClick(object sender, RoutedEventArgs e)
    {
        _stateMachine.IsNotchDockedMode = !_stateMachine.IsNotchDockedMode;
        double topMargin = _stateMachine.IsNotchDockedMode ? 0 : 8;
        IslandBorder.Margin = new Thickness(0, topMargin, 0, 0);
        AmbientGlowBorder.Margin = new Thickness(0, topMargin, 0, 0);
        ToggleNotchModeBtn.Content = _stateMachine.IsNotchDockedMode
            ? "💻 Mode: Top Notch"
            : "🏝️ Mode: Floating Pill";

        OnIslandStateChanged(_stateMachine.CurrentState, _stateMachine.ActiveTab);
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}

