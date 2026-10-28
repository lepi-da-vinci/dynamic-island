using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settings;
    private readonly UpdateCheckerService _updateService;
    private readonly HardwareTelemetryService _hardwareService;
    private readonly AudioService _audioService;
    private readonly BrightnessService _brightnessService;
    private readonly TodoAndWaterService _todoAndWaterService;
    private readonly FileShelfService _fileShelfService;
    private readonly Action _onTuckRequested;
    private readonly DispatcherTimer _liveTelemetryTimer;
    private bool _isLoadingUi;
    private bool _isDraggingVol;
    private bool _isDraggingBrt;

    public SettingsWindow(
        AppSettingsService settings,
        UpdateCheckerService updateService,
        HardwareTelemetryService hardwareService,
        AudioService audioService,
        BrightnessService brightnessService,
        TodoAndWaterService todoAndWaterService,
        FileShelfService fileShelfService,
        Action onTuckRequested)
    {
        InitializeComponent();

        _settings = settings;
        _updateService = updateService;
        _hardwareService = hardwareService;
        _audioService = audioService;
        _brightnessService = brightnessService;
        _todoAndWaterService = todoAndWaterService;
        _fileShelfService = fileShelfService;
        _onTuckRequested = onTuckRequested;

        _liveTelemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _liveTelemetryTimer.Tick += (_, _) =>
        {
            if (!_isDraggingVol && !_isDraggingBrt)
            {
                RefreshSystemControlCenterCard();
            }
        };

        Loaded += (_, _) =>
        {
            RefreshSystemControlCenterCard();
            _liveTelemetryTimer.Start();
        };
        Closed += (_, _) => _liveTelemetryTimer.Stop();

        LoadSettingsIntoControls();
        RefreshUpdateCard(_updateService.LastResult);
        RefreshBluetoothCard();
        RefreshWaterCard();
        RefreshSystemControlCenterCard();
    }

    public void SelectCategory(int index)
    {
        RadioButton? target = index switch
        {
            0 => NavGeneralBtn,
            1 => NavDisplayBtn,
            2 => NavAppearanceBtn,
            3 => NavMusicBtn,
            4 => NavTodoBtn,
            5 => NavCalendarBtn,
            6 => NavPomodoroBtn,
            7 => NavWaterBtn,
            8 => NavShelfBtn,
            9 => NavHudBtn,
            _ => NavGeneralBtn
        };
        if (target != null)
        {
            target.IsChecked = true;
        }
    }

    private void LoadSettingsIntoControls()
    {
        _isLoadingUi = true;
        try
        {
            // Page 0: General
            ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
            ChkMinimalNotch.IsChecked = _settings.MinimalNotchEnabled;
            ChkMascot.IsChecked = _settings.MascotEnabled;
            ChkAutoTuck.IsChecked = _settings.AutoTuckEnabled;
            ChkScreenshotMode.IsChecked = _settings.ScreenshotAutoTuckEnabled;

            ChkTabMedia.IsChecked = _settings.ShowTabMedia;
            ChkTabTodo.IsChecked = _settings.ShowTabTodo;
            ChkTabPomodoro.IsChecked = _settings.ShowTabPomodoro;
            ChkTabShortcuts.IsChecked = _settings.ShowTabShortcuts;
            ChkTabPerformance.IsChecked = _settings.ShowTabPerformance;
            ChkTabShelf.IsChecked = _settings.ShowTabShelf;

            switch (_settings.NotchSizePercent)
            {
                case 100: Size100Btn.IsChecked = true; break;
                case 115: Size115Btn.IsChecked = true; break;
                case 130: Size130Btn.IsChecked = true; break;
                case 150: Size150Btn.IsChecked = true; break;
                default: SizeAutoBtn.IsChecked = true; break;
            }

            switch (_settings.IdleCycleSeconds)
            {
                case 5: Tuck5sBtn.IsChecked = true; break;
                case 15: Tuck15sBtn.IsChecked = true; break;
                case 30: Tuck30sBtn.IsChecked = true; break;
                default: Tuck10sBtn.IsChecked = true; break;
            }

            // Page 1: Display
            ChkAutoHideFullScreen.IsChecked = _settings.AutoHideFullScreen;
            ChkExcludeFromCapture.IsChecked = _settings.ExcludeFromScreenCapture;
            switch (_settings.TopOffsetPx)
            {
                case 2: Offset2Btn.IsChecked = true; break;
                case 4: Offset4Btn.IsChecked = true; break;
                case 8: Offset8Btn.IsChecked = true; break;
                case 12: Offset12Btn.IsChecked = true; break;
                default: Offset0Btn.IsChecked = true; break;
            }

            // Page 2: Appearance
            ChkShowContourRim.IsChecked = _settings.ShowContourRim;

            switch (_settings.IslandSurfaceTheme)
            {
                case "MidnightCarbon": SurfCarbonBtn.IsChecked = true; break;
                case "FrostedGraphite": SurfGraphiteBtn.IsChecked = true; break;
                case "CyberMatrix": SurfMatrixBtn.IsChecked = true; break;
                case "RoyalAmethyst": SurfAmethystBtn.IsChecked = true; break;
                case "CrimsonObsidian": SurfCrimsonBtn.IsChecked = true; break;
                default: SurfOledBtn.IsChecked = true; break;
            }

            switch (_settings.NotchCornerStyle)
            {
                case "UltraPill": CornerUltraPillBtn.IsChecked = true; break;
                case "Sharp": CornerSharpBtn.IsChecked = true; break;
                default: CornerSquircleBtn.IsChecked = true; break;
            }

            if (_settings.NotchBackgroundOpacity <= 0.89) Opacity86Btn.IsChecked = true;
            else if (_settings.NotchBackgroundOpacity <= 0.96) Opacity94Btn.IsChecked = true;
            else Opacity100Btn.IsChecked = true;

            switch (_settings.AccentTheme)
            {
                case "Cyan": ThemeCyanBtn.IsChecked = true; break;
                case "Green": ThemeGreenBtn.IsChecked = true; break;
                case "Purple": ThemePurpleBtn.IsChecked = true; break;
                case "Orange": ThemeOrangeBtn.IsChecked = true; break;
                case "Rose": ThemeRoseBtn.IsChecked = true; break;
                case "Gold": ThemeGoldBtn.IsChecked = true; break;
                case "White": ThemeWhiteBtn.IsChecked = true; break;
                default: ThemeAutoBtn.IsChecked = true; break;
            }

            switch (_settings.RimGlowStyle)
            {
                case "AccentNeon": RimNeonBtn.IsChecked = true; break;
                case "Hidden": RimHiddenBtn.IsChecked = true; break;
                default: RimSubtleBtn.IsChecked = true; break;
            }

            if (_settings.GlowIntensity <= 0.05) GlowOffBtn.IsChecked = true;
            else if (_settings.GlowIntensity <= 0.65) GlowLowBtn.IsChecked = true;
            else if (_settings.GlowIntensity >= 1.3) GlowHighBtn.IsChecked = true;
            else GlowNormBtn.IsChecked = true;

            // Page 3: Music and lyrics
            ChkLyricsEnabled.IsChecked = _settings.LyricsEnabled;
            ChkSlowLyricPan.IsChecked = _settings.SlowLyricPanEnabled;
            ChkTrackChangeToast.IsChecked = _settings.MediaTrackChangeToastEnabled;
            ChkAudioVisualizer.IsChecked = _settings.AudioVisualizerEnabled;

            // Page 4: To-do
            ChkShowTodoBadge.IsChecked = _settings.ShowTodoBadgeOnIdle;

            // Page 5: Calendar
            ChkShowDateOnIdle.IsChecked = _settings.ShowDateOnIdle;
            ChkUse24HourClock.IsChecked = _settings.Use24HourClock;
            ChkShowSeconds.IsChecked = _settings.ShowSecondsOnClock;

            // Page 6: Pomodoro
            ChkShowPomodoroOnIdle.IsChecked = _settings.ShowPomodoroOnIdle;
            switch (_settings.DefaultFocusMinutes)
            {
                case 15: Focus15Btn.IsChecked = true; break;
                case 45: Focus45Btn.IsChecked = true; break;
                case 60: Focus60Btn.IsChecked = true; break;
                default: Focus25Btn.IsChecked = true; break;
            }

            switch (_settings.DefaultBreakMinutes)
            {
                case 10: Break10Btn.IsChecked = true; break;
                case 15: Break15Btn.IsChecked = true; break;
                default: Break5Btn.IsChecked = true; break;
            }

            // Page 7: Water
            double waterTarget = _settings.WaterDailyTargetLiters;
            if (Math.Abs(waterTarget - 1.0) < 0.1) Water10Btn.IsChecked = true;
            else if (Math.Abs(waterTarget - 2.0) < 0.1) Water20Btn.IsChecked = true;
            else if (Math.Abs(waterTarget - 2.5) < 0.1) Water25Btn.IsChecked = true;
            else if (Math.Abs(waterTarget - 3.0) < 0.1) Water30Btn.IsChecked = true;
            else Water15Btn.IsChecked = true;

            // Page 8: Shelf
            ChkAutoOpenShelf.IsChecked = _settings.AutoOpenShelfOnDrag;

            // Page 9: HUD & popups
            ChkVolumeBrightnessHud.IsChecked = _settings.VolumeBrightnessHudEnabled;
            ChkBatteryToast.IsChecked = _settings.BatteryToastEnabled;
            ChkBluetoothToast.IsChecked = _settings.BluetoothToastEnabled;
            ChkSystemNotifToast.IsChecked = _settings.SystemNotificationToastEnabled;

            double toastDur = _settings.ToastDurationSeconds;
            if (Math.Abs(toastDur - 2.0) < 0.2) Toast20Btn.IsChecked = true;
            else if (Math.Abs(toastDur - 4.0) < 0.2) Toast40Btn.IsChecked = true;
            else if (Math.Abs(toastDur - 5.0) < 0.2) Toast50Btn.IsChecked = true;
            else Toast28Btn.IsChecked = true;
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void OnNavCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null || sender is not RadioButton rb || rb.Tag is not string tagStr)
            return;

        if (!int.TryParse(tagStr, out int idx))
            return;

        PageGeneral.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageDisplay.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageMusic.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageTodo.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageCalendar.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;
        PagePomodoro.Visibility = idx == 6 ? Visibility.Visible : Visibility.Collapsed;
        PageWater.Visibility = idx == 7 ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = idx == 8 ? Visibility.Visible : Visibility.Collapsed;
        PageHud.Visibility = idx == 9 ? Visibility.Visible : Visibility.Collapsed;

        PageHeaderTitleText.Text = idx switch
        {
            0 => "General",
            1 => "Display",
            2 => "Appearance",
            3 => "Music and lyrics",
            4 => "To-do",
            5 => "Calendar",
            6 => "Pomodoro",
            7 => "Water",
            8 => "Shelf",
            9 => "HUD & popups",
            _ => "General"
        };

        if (idx == 7) RefreshWaterCard();
        if (idx == 9) RefreshBluetoothCard();
    }

    private void OnSettingToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;

        _settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        _settings.MinimalNotchEnabled = ChkMinimalNotch.IsChecked == true;
        _settings.MascotEnabled = ChkMascot.IsChecked == true;
        _settings.AutoTuckEnabled = ChkAutoTuck.IsChecked == true;
        _settings.ScreenshotAutoTuckEnabled = ChkScreenshotMode.IsChecked == true;
        _settings.AutoHideFullScreen = ChkAutoHideFullScreen.IsChecked == true;
        _settings.ExcludeFromScreenCapture = ChkExcludeFromCapture.IsChecked == true;
        _settings.ShowContourRim = ChkShowContourRim.IsChecked == true;
        _settings.LyricsEnabled = ChkLyricsEnabled.IsChecked == true;
        _settings.SlowLyricPanEnabled = ChkSlowLyricPan.IsChecked == true;
        _settings.MediaTrackChangeToastEnabled = ChkTrackChangeToast.IsChecked == true;
        _settings.AudioVisualizerEnabled = ChkAudioVisualizer.IsChecked == true;
        _settings.ShowTodoBadgeOnIdle = ChkShowTodoBadge.IsChecked == true;
        _settings.ShowDateOnIdle = ChkShowDateOnIdle.IsChecked == true;
        _settings.Use24HourClock = ChkUse24HourClock.IsChecked == true;
        _settings.ShowSecondsOnClock = ChkShowSeconds.IsChecked == true;
        _settings.ShowPomodoroOnIdle = ChkShowPomodoroOnIdle.IsChecked == true;
        _settings.AutoOpenShelfOnDrag = ChkAutoOpenShelf.IsChecked == true;
        _settings.VolumeBrightnessHudEnabled = ChkVolumeBrightnessHud.IsChecked == true;
        _settings.BatteryToastEnabled = ChkBatteryToast.IsChecked == true;
        _settings.BluetoothToastEnabled = ChkBluetoothToast.IsChecked == true;
        _settings.SystemNotificationToastEnabled = ChkSystemNotifToast.IsChecked == true;
    }

    private void OnTabVisibilityToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;

        bool media = ChkTabMedia.IsChecked == true;
        bool todo = ChkTabTodo.IsChecked == true;
        bool pomo = ChkTabPomodoro.IsChecked == true;
        bool shortcuts = ChkTabShortcuts.IsChecked == true;
        bool perf = ChkTabPerformance.IsChecked == true;
        bool shelf = ChkTabShelf.IsChecked == true;

        // Ensure at least 1 tab stays enabled
        if (!media && !todo && !pomo && !shortcuts && !perf && !shelf)
        {
            if (sender is CheckBox cb)
            {
                cb.IsChecked = true;
            }
            return;
        }

        _settings.ShowTabMedia = media;
        _settings.ShowTabTodo = todo;
        _settings.ShowTabPomodoro = pomo;
        _settings.ShowTabShortcuts = shortcuts;
        _settings.ShowTabPerformance = perf;
        _settings.ShowTabShelf = shelf;
    }

    public void RefreshSystemControlCenterCard()
    {
        try
        {
            int vol = _audioService.GetMasterVolumePercent();
            bool soundMuted = _audioService.IsMuted();
            bool micMuted = _audioService.IsMicrophoneMuted();
            bool isHeadphones = _audioService.IsHeadphoneOrTws;
            int brt = _brightnessService.GetBrightnessPercent();

            SettingsVolPercentText.Text = soundMuted ? "MUTED" : $"{vol}%";
            var volColor = soundMuted
                ? Color.FromRgb(248, 113, 113)
                : isHeadphones
                    ? Color.FromRgb(192, 132, 252)
                    : Color.FromRgb(56, 189, 248);
            var volBrush = new SolidColorBrush(volColor);
            SettingsVolPercentText.Foreground = volBrush;
            SettingsVolIconPath.Fill = volBrush;
            SettingsVolIconPath.Data = soundMuted
                ? (TryFindResource("GeomSpeakerMuted") as Geometry ?? Geometry.Empty)
                : isHeadphones
                    ? (TryFindResource("GeomHeadphones") as Geometry ?? Geometry.Empty)
                    : (TryFindResource("GeomSpeakerOn") as Geometry ?? Geometry.Empty);

            SettingsVolLabelText.Text = soundMuted
                ? "AUDIO MUTED"
                : _audioService.ActiveOutputDeviceName.ToUpperInvariant();

            double volTrackW = SettingsVolTrackBorder.ActualWidth > 10 ? SettingsVolTrackBorder.ActualWidth : 195.0;
            SettingsVolFillBorder.Width = volTrackW * (soundMuted ? 0.0 : Math.Clamp(vol / 100.0, 0.0, 1.0));
            SettingsVolFillBorder.Background = volBrush;

            SettingsBrtPercentText.Text = $"{brt}%";
            double brtTrackW = SettingsBrtTrackBorder.ActualWidth > 10 ? SettingsBrtTrackBorder.ActualWidth : 195.0;
            SettingsBrtFillBorder.Width = brtTrackW * Math.Clamp(brt / 100.0, 0.0, 1.0);

            var hw = _hardwareService.Current;
            SettingsSysBatteryText.Text = hw.IsCharging
                ? $"Charging • {hw.BatteryPercent}%"
                : $"Battery • {hw.BatteryPercent}%";
            SettingsSysNetworkText.Text = hw.IsNetworkConnected
                ? $"Wi-Fi: {hw.NetworkName}"
                : "Offline (Tidak ada internet)";

            if (hw.IsCameraInUse || hw.IsMicrophoneInUse)
            {
                string active = hw.IsCameraInUse && hw.IsMicrophoneInUse
                    ? "Kamera & Mic Sedang Aktif!"
                    : hw.IsCameraInUse
                        ? "Kamera Sedang Digunakan"
                        : "Mikrofon Sedang Digunakan";
                SettingsSysPrivacyText.Text = active;
                var warnBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                SettingsSysPrivacyText.Foreground = warnBrush;
                SettingsSysPrivacyIcon.Fill = warnBrush;
            }
            else
            {
                SettingsSysPrivacyText.Text = "Kamera & Mic Aman (Idle)";
                var safeBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                SettingsSysPrivacyText.Foreground = safeBrush;
                SettingsSysPrivacyIcon.Fill = safeBrush;
            }

            SettingsSysMemoryText.Text = $"RAM: {hw.AppMemoryMb:F1} MB • CPU {hw.CpuUsagePercent}%";

            // Quick Action Buttons
            SettingsMuteBtnIcon.Data = soundMuted
                ? (TryFindResource("GeomSpeakerMuted") as Geometry ?? Geometry.Empty)
                : (TryFindResource("GeomSpeakerOn") as Geometry ?? Geometry.Empty);
            var soundBtnBrush = new SolidColorBrush(soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(56, 189, 248));
            SettingsMuteBtnIcon.Fill = soundBtnBrush;
            SettingsMuteBtnText.Text = soundMuted ? "Sound: MUTED" : "Sound: ON";
            SettingsMuteBtnText.Foreground = new SolidColorBrush(soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(248, 250, 252));

            SettingsMicBtnIcon.Data = micMuted
                ? (TryFindResource("GeomMicMuted") as Geometry ?? Geometry.Empty)
                : (TryFindResource("GeomMicOn") as Geometry ?? Geometry.Empty);
            var micBtnBrush = new SolidColorBrush(micMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(34, 197, 94));
            SettingsMicBtnIcon.Fill = micBtnBrush;
            SettingsMicBtnText.Text = micMuted ? "Mic: MUTED" : "Mic: ON";
            SettingsMicBtnText.Foreground = micBtnBrush;

            bool btOn = hw.IsBluetoothRadioOn;
            bool btConnected = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName);
            var btColor = !btOn
                ? Color.FromRgb(148, 163, 184)
                : btConnected
                    ? Color.FromRgb(34, 197, 94)
                    : Color.FromRgb(56, 189, 248);
            var btBrush = new SolidColorBrush(btColor);
            SettingsBtBtnIcon.Data = btOn
                ? (TryFindResource("GeomBluetooth") as Geometry ?? Geometry.Empty)
                : (TryFindResource("GeomBluetoothOff") as Geometry ?? Geometry.Empty);
            SettingsBtBtnIcon.Fill = btBrush;
            SettingsBtBtnText.Text = !btOn ? "BT: OFF" : (btConnected ? "BT: Aktif" : "Bluetooth");
            SettingsBtBtnText.Foreground = btBrush;
        }
        catch { }
    }

    private void OnSettingsVolumeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingVol = true;
        SettingsVolSliderHitArea.CaptureMouse();
        ApplySettingsVolumeFromMouse(e.GetPosition(SettingsVolTrackBorder).X);
        e.Handled = true;
    }

    private void OnSettingsVolumeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVol) return;
        ApplySettingsVolumeFromMouse(e.GetPosition(SettingsVolTrackBorder).X);
        e.Handled = true;
    }

    private void OnSettingsVolumeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVol) return;
        _isDraggingVol = false;
        SettingsVolSliderHitArea.ReleaseMouseCapture();
        ApplySettingsVolumeFromMouse(e.GetPosition(SettingsVolTrackBorder).X);
        e.Handled = true;
    }

    private void ApplySettingsVolumeFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, SettingsVolTrackBorder.ActualWidth);
        int pct = (int)Math.Round(Math.Clamp(mouseX / trackW, 0.0, 1.0) * 100.0);
        _audioService.SetMasterVolumePercent(pct);
        RefreshSystemControlCenterCard();
    }

    private void OnSettingsBrightnessMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingBrt = true;
        SettingsBrtSliderHitArea.CaptureMouse();
        ApplySettingsBrightnessFromMouse(e.GetPosition(SettingsBrtTrackBorder).X);
        e.Handled = true;
    }

    private void OnSettingsBrightnessMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingBrt) return;
        ApplySettingsBrightnessFromMouse(e.GetPosition(SettingsBrtTrackBorder).X);
        e.Handled = true;
    }

    private void OnSettingsBrightnessMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingBrt) return;
        _isDraggingBrt = false;
        SettingsBrtSliderHitArea.ReleaseMouseCapture();
        ApplySettingsBrightnessFromMouse(e.GetPosition(SettingsBrtTrackBorder).X);
        e.Handled = true;
    }

    private void ApplySettingsBrightnessFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, SettingsBrtTrackBorder.ActualWidth);
        int pct = (int)Math.Round(Math.Clamp(mouseX / trackW, 0.0, 1.0) * 100.0);
        _brightnessService.SetBrightnessPercent(pct);
        RefreshSystemControlCenterCard();
    }

    private void OnSettingsToggleMuteClick(object sender, RoutedEventArgs e)
    {
        _audioService.ToggleMute();
        RefreshSystemControlCenterCard();
    }

    private void OnSettingsToggleMicClick(object sender, RoutedEventArgs e)
    {
        _audioService.ToggleMicrophoneMute();
        RefreshSystemControlCenterCard();
    }

    private async void OnSettingsBluetoothRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await _hardwareService.ToggleBluetoothRadioAsync();
        _hardwareService.RefreshFullTelemetry(emitToasts: false);
        RefreshSystemControlCenterCard();
        RefreshBluetoothCard();
    }

    private void OnSettingsTuckIslandClick(object sender, RoutedEventArgs e)
    {
        _onTuckRequested?.Invoke();
    }

    private void OnSettingsExitAppClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void OnNotchSizeClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int sizePct))
        {
            _settings.NotchSizePercent = sizePct;
        }
    }

    private void OnTuckDelayClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int sec))
        {
            _settings.IdleCycleSeconds = sec;
        }
    }

    private void OnTopOffsetClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int px))
        {
            _settings.TopOffsetPx = px;
        }
    }

    private void OnSurfaceThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string surf) return;
        _settings.IslandSurfaceTheme = surf;
    }

    private void OnCornerStyleClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string corner) return;
        _settings.NotchCornerStyle = corner;
    }

    private void OnNotchOpacityClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double op))
        {
            _settings.NotchBackgroundOpacity = op;
        }
    }

    private void OnAccentThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string theme) return;
        _settings.AccentTheme = theme;
    }

    private void OnRimStyleClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string rim) return;
        _settings.RimGlowStyle = rim;
        if (rim == "Hidden")
        {
            ChkShowContourRim.IsChecked = false;
            _settings.ShowContourRim = false;
        }
        else if (!_settings.ShowContourRim)
        {
            ChkShowContourRim.IsChecked = true;
            _settings.ShowContourRim = true;
        }
    }

    private void OnGlowIntensityClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            _settings.GlowIntensity = val;
        }
    }

    private void OnFocusDurationClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int mins))
        {
            _settings.DefaultFocusMinutes = mins;
        }
    }

    private void OnBreakDurationClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int mins))
        {
            _settings.DefaultBreakMinutes = mins;
        }
    }

    private void OnWaterTargetClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double liters))
        {
            _settings.WaterDailyTargetLiters = liters;
            _todoAndWaterService.SetWaterTargetLiters(liters);
            RefreshWaterCard();
        }
    }

    private void OnToastDurationClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
        {
            _settings.ToastDurationSeconds = secs;
        }
    }

    private void OnDrinkWaterSettingsClick(object sender, RoutedEventArgs e)
    {
        _todoAndWaterService.DrinkWater(_settings.WaterCupSizeLiters);
        RefreshWaterCard();
    }

    private void OnResetWaterSettingsClick(object sender, RoutedEventArgs e)
    {
        _todoAndWaterService.ResetWater();
        RefreshWaterCard();
    }

    private void RefreshWaterCard()
    {
        SettingsWaterProgressText.Text = $"Progres hari ini: {_todoAndWaterService.WaterCurrentLiters:0.0} / {_todoAndWaterService.WaterTargetLiters:0.0} L ({_todoAndWaterService.WaterPercentage:0}%)";
    }

    private void OnClearCompletedTodosSettingsClick(object sender, RoutedEventArgs e)
    {
        var completed = _todoAndWaterService.Todos.Where(t => t.IsDone).ToList();
        foreach (var item in completed)
        {
            _todoAndWaterService.DeleteTodo(item.Id);
        }
    }

    private void OnClearShelfSettingsClick(object sender, RoutedEventArgs e)
    {
        _fileShelfService.Clear();
    }

    public void RefreshBluetoothCard()
    {
        var hw = _hardwareService.Current;
        BtStatusTitleText.Text = hw.IsBluetoothRadioOn ? "Bluetooth Radio: ON (Aktif)" : "Bluetooth Radio: OFF (Mati)";
        BtToggleBtnText.Text = hw.IsBluetoothRadioOn ? "Matikan BT" : "Nyalakan BT";

        if (!hw.IsBluetoothRadioOn)
        {
            BtConnectedDevicesText.Text = "Radio Bluetooth sedang dinonaktifkan";
            BtConnectedDevicesText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        }
        else if (!string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName))
        {
            BtConnectedDevicesText.Text = $"Terhubung: {hw.ConnectedBluetoothName}";
            BtConnectedDevicesText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        else
        {
            BtConnectedDevicesText.Text = "Tidak ada perangkat aktif • Klik Pair Perangkat untuk menyambungkan";
            BtConnectedDevicesText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }
    }

    private async void OnToggleBluetoothRadioClick(object sender, RoutedEventArgs e)
    {
        await _hardwareService.ToggleBluetoothRadioAsync();
        _hardwareService.RefreshFullTelemetry(emitToasts: false);
        RefreshBluetoothCard();
    }

    private void OnOpenBluetoothSettingsClick(object sender, RoutedEventArgs e)
    {
        HardwareTelemetryService.OpenWindowsBluetoothSettings();
    }

    public void RefreshUpdateCard(UpdateCheckResult res)
    {
        SidebarVersionText.Text = $"{res.CurrentVersion} • macOS Edition";

        if (res.IsUpdateAvailable)
        {
            SettingsUpdateTitleText.Text = $"Update Baru Tersedia: {res.LatestVersion}";
            SettingsUpdateTitleText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SettingsUpdateSubText.Text = $"Versi saat ini: {res.CurrentVersion} • Klik Update Sekarang untuk memasang";
            SettingsInstallUpdateBtn.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsUpdateTitleText.Text = $"Windows Dynamic Island {res.CurrentVersion} (Terbaru)";
            SettingsUpdateTitleText.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            SettingsUpdateSubText.Text = "Sistem sudah menggunakan rilis versi paling mutakhir";
            SettingsInstallUpdateBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        SettingsCheckUpdateBtnText.Text = "Mengecek...";
        SettingsCheckUpdateBtn.IsEnabled = false;
        try
        {
            var res = await _updateService.CheckForUpdatesAsync(isBackgroundCheck: false);
            RefreshUpdateCard(res);
        }
        finally
        {
            SettingsCheckUpdateBtnText.Text = "Cek Update";
            SettingsCheckUpdateBtn.IsEnabled = true;
        }
    }

    private void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        UpdateCheckerService.LaunchOneClickInstaller();
    }

    private void OnTitleBarDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

