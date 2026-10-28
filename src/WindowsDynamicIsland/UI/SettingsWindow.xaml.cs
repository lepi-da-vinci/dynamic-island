using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settings;
    private readonly UpdateCheckerService _updateService;
    private readonly HardwareTelemetryService _hardwareService;
    private readonly TodoAndWaterService _todoAndWaterService;
    private readonly FileShelfService _fileShelfService;
    private bool _isLoadingUi;

    public SettingsWindow(
        AppSettingsService settings,
        UpdateCheckerService updateService,
        HardwareTelemetryService hardwareService,
        TodoAndWaterService todoAndWaterService,
        FileShelfService fileShelfService)
    {
        InitializeComponent();

        _settings = settings;
        _updateService = updateService;
        _hardwareService = hardwareService;
        _todoAndWaterService = todoAndWaterService;
        _fileShelfService = fileShelfService;

        LoadSettingsIntoControls();
        RefreshUpdateCard(_updateService.LastResult);
        RefreshBluetoothCard();
        RefreshWaterCard();
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
            switch (_settings.AccentTheme)
            {
                case "Cyan": ThemeCyanBtn.IsChecked = true; break;
                case "Green": ThemeGreenBtn.IsChecked = true; break;
                case "Purple": ThemePurpleBtn.IsChecked = true; break;
                case "Orange": ThemeOrangeBtn.IsChecked = true; break;
                case "Rose": ThemeRoseBtn.IsChecked = true; break;
                default: ThemeAutoBtn.IsChecked = true; break;
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
        _settings.AutoHideFullScreen = ChkAutoHideFullScreen.IsChecked == true;
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

    private void OnAccentThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || rb.Tag is not string theme) return;
        _settings.AccentTheme = theme;
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
