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
/// Live toasts, volume/brightness sliders, privacy dot, hardware telemetry cards, and Bluetooth/Battery quick controls.
/// </summary>
public partial class MainWindow
{
    private void ShowVolumeToast(int volumePercent, bool isMuted, bool muteToggled)
    {
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
        if (iconCode == "BATTERY_HEALTH")
            return GetVectorGeometry("GeomBatteryHealth");

        if (iconCode == "CALCULATOR" || iconCode == "CALC")
            return GetVectorGeometry("GeomCalculator");

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

        if (iconCode == "TOUCHPAD_ON")
            return GetVectorGeometry("GeomTouchpadOn");

        if (iconCode == "TOUCHPAD_OFF")
            return GetVectorGeometry("GeomTouchpadOff");

        if (iconCode == "KBD_BACKLIGHT")
            return GetVectorGeometry("GeomKeyboardBacklight");

        if (iconCode == "FOLDER" || iconCode == "DROP_SHELF")
            return GetVectorGeometry("GeomFolderTray");

        if (iconCode == "POMO_FOCUS" || iconCode == "POMO_BREAK" || iconCode.StartsWith("TIMER") || iconCode.StartsWith("POMO"))
            return GetVectorGeometry("GeomFocusTimer");

        return GetVectorGeometry("GeomBellNotif");
    }

    private static Color GetBatteryStatusColor(int batteryPercent, bool isCharging, bool isMonochrome = false)
    {
        if (isMonochrome)
        {
            if (isCharging || batteryPercent > 65)
                return Color.FromRgb(244, 244, 245); // Zinc-100
            if (batteryPercent <= 20)
                return Color.FromRgb(113, 113, 122); // Zinc-500
            return Color.FromRgb(161, 161, 170); // Zinc-400
        }

        // Always green when charging, even if battery is 20% or below
        if (isCharging)
            return Color.FromRgb(34, 197, 94); // #22C55E
        if (batteryPercent <= 20)
            return Color.FromRgb(239, 68, 68); // #EF4444 (Red at <=20%)
        if (batteryPercent <= 65)
            return Color.FromRgb(249, 115, 22); // #F97316 (Orange around half)
        return Color.FromRgb(34, 197, 94); // #22C55E (Green when full)
    }

    private static void SetProportionalBatteryBar(
        ColumnDefinition fillCol,
        ColumnDefinition emptyCol,
        Border fillBar,
        int batteryPercent,
        Color fillColor)
    {
        double clampedPct = Math.Clamp(batteryPercent, 0, 100);
        double starFill = clampedPct <= 0 ? 0.0 : Math.Max(8.0, clampedPct);
        fillCol.Width = new GridLength(starFill, GridUnitType.Star);
        emptyCol.Width = new GridLength(Math.Max(0.0, 100.0 - starFill), GridUnitType.Star);
        fillBar.Background = BrushCache.Get(fillColor);
    }

    private void UpdateHardwareUi()
    {
        var hw = _hardwareService.Current;

        int batPct = hw.HasBattery ? Math.Clamp(hw.BatteryPercent, 0, 100) : 100;
        bool isCharging = hw.HasBattery && hw.IsCharging;
        bool isMono = IsMonochromeTheme;
        Color batColor = GetBatteryStatusColor(batPct, isCharging, isMono);

        // 1. Idle Pill Battery & Wi-Fi
        IdleBatteryPercentText.Text = $"{batPct}%";
        SetProportionalBatteryBar(IdleBatteryFillCol, IdleBatteryEmptyCol, IdleBatteryFillBar, batPct, batColor);
        IdleBatteryBoltPath.Fill = BrushCache.Get(
            isMono
                ? Color.FromRgb(244, 244, 245)
                : (isCharging ? Color.FromRgb(254, 240, 138) : Color.FromRgb(248, 250, 252)));
        IdleBatteryBoltPath.Visibility = isCharging ? Visibility.Visible : Visibility.Collapsed;

        IdleWifiIconPath.Data = hw.IsNetworkConnected
            ? GetVectorGeometry("GeomWifi")
            : GetVectorGeometry("GeomWifiOff");
        IdleWifiIconPath.Fill = BrushCache.Get(
            isMono
                ? (hw.IsNetworkConnected ? Color.FromRgb(228, 228, 231) : Color.FromRgb(113, 113, 122))
                : (hw.IsNetworkConnected ? Color.FromRgb(148, 163, 184) : Color.FromRgb(239, 68, 68)));

        // 2. Expanded Dashboard Top Header: Wi-Fi, Bluetooth & DPI-Fit Battery
        string wifiLabel = !hw.IsNetworkConnected
            ? "Offline"
            : (string.IsNullOrWhiteSpace(hw.NetworkName) || hw.NetworkName == "Terhubung" ? "Online" : hw.NetworkName);
        if (wifiLabel.StartsWith("Wi-Fi: ", StringComparison.OrdinalIgnoreCase))
        {
            wifiLabel = wifiLabel[7..].Trim();
        }
        HeaderWifiIconPath.Data = hw.IsNetworkConnected
            ? GetVectorGeometry("GeomWifi")
            : GetVectorGeometry("GeomWifiOff");
        HeaderWifiIconPath.Fill = BrushCache.Get(
            isMono
                ? (hw.IsNetworkConnected ? Color.FromRgb(228, 228, 231) : Color.FromRgb(113, 113, 122))
                : (hw.IsNetworkConnected ? _currentThemeAccentColor : Color.FromRgb(239, 68, 68)));
        HeaderWifiText.Text = wifiLabel;
        HeaderWifiText.Foreground = BrushCache.Get(
            isMono
                ? (hw.IsNetworkConnected ? Color.FromRgb(228, 228, 231) : Color.FromRgb(113, 113, 122))
                : (hw.IsNetworkConnected ? Color.FromRgb(226, 232, 240) : Color.FromRgb(248, 113, 113)));
        var loc = LocalizationService.Instance;
        HeaderWifiBadge.ToolTip = hw.IsNetworkConnected
            ? (loc.IsEnglish
                ? $"Wi-Fi Connected: {hw.NetworkName} (Left-Click: Wi-Fi Menu | Right-Click: Windows Settings)"
                : $"Wi-Fi Terhubung: {hw.NetworkName} (Klik Kiri: Menu Wi-Fi | Klik Kanan: Pengaturan)")
            : (loc.IsEnglish
                ? "Not Connected (Left-Click: Wi-Fi Menu | Right-Click: Windows Settings)"
                : "Tidak Terhubung (Klik Kiri: Menu Wi-Fi | Klik Kanan: Pengaturan)");

        bool btOn = hw.IsBluetoothRadioOn;
        bool btConnected = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName);
        Color headerBtColor = isMono
            ? (!btOn ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
            : (!btOn
                ? Color.FromRgb(148, 163, 184)
                : btConnected
                    ? Color.FromRgb(34, 197, 94)
                    : _currentThemeAccentColor);
        HeaderBluetoothIconPath.Data = btOn ? GetVectorGeometry("GeomBluetooth") : GetVectorGeometry("GeomBluetoothOff");
        HeaderBluetoothIconPath.Fill = BrushCache.Get(headerBtColor);
        string btBatSuffix = hw.ConnectedBluetoothBatteryPercent.HasValue ? $" {hw.ConnectedBluetoothBatteryPercent.Value}%" : string.Empty;
        HeaderBluetoothText.Text = !btOn
            ? "Off"
            : (btConnected ? $"{hw.ConnectedBluetoothName}{btBatSuffix}" : "On");
        HeaderBluetoothText.Foreground = BrushCache.Get(
            isMono
                ? (!btOn ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
                : (!btOn ? Color.FromRgb(148, 163, 184) : Color.FromRgb(226, 232, 240)));
        string btTooltipBat = hw.ConnectedBluetoothBatteryPercent.HasValue ? $" • {hw.ConnectedBluetoothBatteryPercent.Value}%" : string.Empty;
        HeaderBluetoothBtn.ToolTip = loc.IsEnglish
            ? (btConnected ? $"Bluetooth: {hw.ConnectedBluetoothName}{btTooltipBat} (Left-Click: ON/OFF | Right-Click: Settings)" : $"Bluetooth {(btOn ? "On" : "Off")} (Left-Click: ON/OFF | Right-Click: Settings)")
            : (btConnected ? $"Bluetooth: {hw.ConnectedBluetoothName}{btTooltipBat} (Klik Kiri: ON/OFF | Klik Kanan: Pengaturan)" : $"Bluetooth {(btOn ? "Aktif" : "Mati")} (Klik Kiri: ON/OFF | Klik Kanan: Pengaturan)");

        HeaderBatteryPercentText.Text = $"{batPct}%";
        HeaderBatteryPercentText.Foreground = BrushCache.Get(
            isMono
                ? Color.FromRgb(228, 228, 231)
                : (isCharging
                    ? Color.FromRgb(134, 239, 172)
                    : (batPct <= 20 ? Color.FromRgb(252, 165, 165) : Color.FromRgb(226, 232, 240))));
        SetProportionalBatteryBar(HeaderBatteryFillCol, HeaderBatteryEmptyCol, HeaderBatteryFillBar, batPct, batColor);
        HeaderBatteryBoltPath.Visibility = isCharging ? Visibility.Visible : Visibility.Collapsed;
        HeaderBatteryBoltPath.Fill = BrushCache.Get(
            isMono ? Color.FromRgb(244, 244, 245) : Color.FromRgb(34, 197, 94));

        string batteryEstimate = !string.IsNullOrWhiteSpace(hw.BatteryEstimatedTimeText)
            ? $" ({hw.BatteryEstimatedTimeText})"
            : string.Empty;

        string limitModeInfo = _batteryLimitService.IsSupported
            ? (_batteryLimitService.CurrentMode == BatteryChargeLimitMode.SmartCharge80 ? " • Smart Charge 80%" : " • Baterai 100%")
            : string.Empty;

        HeaderBatteryBadge.ToolTip = hw.HasBattery
            ? $"{(isCharging ? (loc.IsEnglish ? "Charging" : "Sedang Mengisi Daya (Charging)") : (loc.IsEnglish ? "Battery" : "Baterai"))} • {batPct}%{batteryEstimate}{limitModeInfo}"
            : (loc.IsEnglish ? "Desktop AC Power (100%)" : "Daya Desktop AC (100%)");

        IdleBatteryBodyBorder.ToolTip = hw.HasBattery
            ? $"{(isCharging ? "Sedang Mengisi Daya (Charging)" : "Baterai")} • {batPct}%{batteryEstimate}{limitModeInfo}"
            : "Daya Desktop AC (100%)";

        bool minimal = _settingsService.MinimalNotchEnabled;
        IdleWifiIconPath.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        IdleBatteryPercentText.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;

        CameraPrivacyDot.Visibility = hw.IsCameraInUse ? Visibility.Visible : Visibility.Collapsed;
        CameraPrivacyDot.ToolTip = !string.IsNullOrEmpty(hw.ActiveCamApp)
            ? $"Kamera aktif: {hw.ActiveCamApp} (Klik untuk detail)"
            : "Kamera sedang aktif (Klik untuk detail)";

        MicPrivacyDot.Visibility = hw.IsMicrophoneInUse ? Visibility.Visible : Visibility.Collapsed;
        MicPrivacyDot.ToolTip = !string.IsNullOrEmpty(hw.ActiveMicApp)
            ? $"Mikrofon aktif: {hw.ActiveMicApp} (Klik untuk bisukan)"
            : "Mikrofon sedang aktif (Klik untuk bisukan)";

        // Live Privacy Badge in Expanded Dashboard Header
        if (hw.IsCameraInUse || hw.IsMicrophoneInUse)
        {
            ExpandedPrivacyBadge.Visibility = Visibility.Visible;
            ExpandedCamDot.Visibility = hw.IsCameraInUse ? Visibility.Visible : Visibility.Collapsed;
            ExpandedMicDot.Visibility = hw.IsMicrophoneInUse ? Visibility.Visible : Visibility.Collapsed;

            if (hw.IsCameraInUse && hw.IsMicrophoneInUse)
            {
                ExpandedPrivacyText.Text = $"Kamera & Mic ({hw.ActiveCamApp}/{hw.ActiveMicApp})";
            }
            else if (hw.IsCameraInUse)
            {
                ExpandedPrivacyText.Text = !string.IsNullOrEmpty(hw.ActiveCamApp) ? $"Kamera: {hw.ActiveCamApp}" : "Kamera Aktif";
            }
            else
            {
                ExpandedPrivacyText.Text = !string.IsNullOrEmpty(hw.ActiveMicApp) ? $"Mic: {hw.ActiveMicApp}" : "Mic Aktif";
            }
        }
        else
        {
            ExpandedPrivacyBadge.Visibility = Visibility.Collapsed;
        }

        bool hasExtraIdle = hw.IsCameraInUse ||
                            hw.IsMicrophoneInUse ||
                            (_pomodoroService.IsRunning && _settingsService.ShowPomodoroOnIdle) ||
                            (_settingsService.ShowTodoBadgeOnIdle && (_todoAndWaterService.TotalTodosCount - _todoAndWaterService.CompletedTodosCount > 0));
        _stateMachine.SetMinimalNotch(minimal, hasExtraIdle);
        _stateMachine.UpdateDynamicIdleWidth(hasExtraIdle);
    }

    private void OnPrivacyDotClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var hw = _hardwareService.Current;

        if (hw.IsMicrophoneInUse && hw.IsCameraInUse)
        {
            string camName = !string.IsNullOrEmpty(hw.ActiveCamApp) ? hw.ActiveCamApp : "Aplikasi";
            string micName = !string.IsNullOrEmpty(hw.ActiveMicApp) ? hw.ActiveMicApp : "Aplikasi";

            TriggerToast(new ToastPayload(
                Icon: "SHIELD",
                Title: "Kamera & Mikrofon Aktif",
                Subtitle: $"Kamera: {camName} - Mic: {micName}",
                AccentHex: "#F97316",
                Duration: TimeSpan.FromSeconds(3.2)));
        }
        else if (hw.IsMicrophoneInUse)
        {
            string appName = !string.IsNullOrEmpty(hw.ActiveMicApp) ? hw.ActiveMicApp : "Aplikasi";
            TriggerToast(new ToastPayload(
                Icon: "MIC",
                Title: "Mikrofon Sedang Digunakan",
                Subtitle: $"{appName} sedang merekam audio",
                AccentHex: "#F97316",
                Duration: TimeSpan.FromSeconds(3.0)));
        }
        else if (hw.IsCameraInUse)
        {
            string appName = !string.IsNullOrEmpty(hw.ActiveCamApp) ? hw.ActiveCamApp : "Aplikasi";
            TriggerToast(new ToastPayload(
                Icon: "SHIELD",
                Title: "Kamera Sedang Digunakan",
                Subtitle: $"{appName} sedang mengakses kamera",
                AccentHex: "#22C55E",
                Duration: TimeSpan.FromSeconds(3.0)));
        }
        else
        {
            TriggerToast(new ToastPayload(
                Icon: "SHIELD",
                Title: "Privasi Aman (Idle)",
                Subtitle: "Tidak ada aplikasi yang sedang mengakses webcam atau mikrofon",
                AccentHex: "#22C55E",
                Duration: TimeSpan.FromSeconds(2.0)));
        }
    }

    public void TriggerToast(ToastPayload toast)
    {
        if (_settingsService.SuppressWindowsNativeOsd &&
            toast.SliderKind is ToastSliderKind.Volume or ToastSliderKind.Brightness)
        {
            WindowsFlyoutSuppressor.TriggerSuppressionBurst(durationMs: 700, intervalMs: 20);
            WindowsFlyoutSuppressor.SuppressOnce();
        }

        if (toast.SliderKind == ToastSliderKind.None &&
            !toast.Icon.Equals("VOLUME", StringComparison.OrdinalIgnoreCase) &&
            !toast.Icon.Equals("BRIGHTNESS", StringComparison.OrdinalIgnoreCase))
        {
            _notificationHistoryService.Add(toast.Title, toast.Title, toast.Subtitle, MapToastIconToGeom(toast.Icon), toast.AccentHex);
        }
        if (_stateMachine.IsScreenshotMode)
        {
            return;
        }

        if (!_settingsService.VolumeBrightnessHudEnabled &&
            toast.SliderKind is ToastSliderKind.Volume or ToastSliderKind.Brightness)
        {
            return;
        }

        if (!_settingsService.BatteryToastEnabled && toast.SliderKind == ToastSliderKind.Battery)
        {
            return;
        }

        if (!_settingsService.Battery80PercentAlertEnabled && toast.Icon == "BATTERY_HEALTH")
        {
            return;
        }

        if (!_settingsService.BluetoothToastEnabled && toast.Icon.StartsWith("BLUETOOTH", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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

        if (IsMonochromeTheme)
        {
            accent = MonochromeThemeHelper.ToMonochromeColor(accent);
        }

        var accentBrush = BrushCache.Get(accent);
        ToastIconPath.Data = ResolveToastVectorGeometry(toast.Icon, toast.SliderKind);
        ToastIconPath.Fill = accentBrush;
        ToastIconBadge.Background = BrushCache.Get(Color.FromArgb(38, accent.R, accent.G, accent.B));
        ToastIconBadge.BorderBrush = BrushCache.Get(Color.FromArgb(68, accent.R, accent.G, accent.B));
        SetAmbientGlowColors(accent, accent, animate: false);

        if (toast.SliderKind == ToastSliderKind.Battery)
        {
            // Render realistic Vector Battery Graphic with proportional DPI-fit fill & 3-tier color rule
            ToastProgressContainer.Visibility = Visibility.Collapsed;
            ToastBatteryContainer.Visibility = Visibility.Visible;

            int toastBatPct = (int)Math.Round(Math.Clamp(toast.ProgressRatio ?? 1.0, 0.0, 1.0) * 100.0);
            Color toastBatColor = GetBatteryStatusColor(toastBatPct, toast.IsBatteryCharging, IsMonochromeTheme);
            SetProportionalBatteryBar(ToastBatteryFillCol, ToastBatteryEmptyCol, ToastBatteryFill, toastBatPct, toastBatColor);
            ToastBatteryBoltPath.Visibility = toast.IsBatteryCharging ? Visibility.Visible : Visibility.Collapsed;
            ToastBatteryBoltPath.Fill = BrushCache.Get(
                IsMonochromeTheme ? Color.FromRgb(244, 244, 245) : Color.FromRgb(254, 240, 138));
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

        _autoTuckTimer.Stop();
        _stateMachine.ShowToast(toast);

        _toastDismissTimer.Stop();
        TimeSpan duration = TimeSpan.FromSeconds(_settingsService.ToastDurationSeconds);
        _toastDismissTimer.Interval = duration;
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
            _settingsWindow?.RefreshSystemControlCenterCard();
        }
        else if (_activeToastSliderKind == ToastSliderKind.Brightness)
        {
            int newBrt = _brightnessService.SetBrightnessPercent(pct);
            ShowBrightnessToast(newBrt);
            _settingsWindow?.RefreshSystemControlCenterCard();
        }
    }

    private void OnHeaderClockDateClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.CalendarSchedule);
    }

    private void OnHeaderWifiClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.Wifi);
        _ = RefreshWifiTabUiAsync(triggerScan: true);
    }

    private void OnHeaderWifiRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        WifiService.OpenWindowsWifiSettings();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private async void OnBluetoothQuickClick(object sender, RoutedEventArgs e)
    {
        await _hardwareService.ToggleBluetoothRadioAsync();
        _hardwareService.RefreshFullTelemetry(emitToasts: false);
        UpdateHardwareUi();
        _settingsWindow?.RefreshBluetoothCard();
    }

    private void OnBluetoothQuickRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HardwareTelemetryService.OpenWindowsBluetoothSettings();
    }

    private async void SetBatteryLimit(BatteryChargeLimitMode mode)
    {
        try
        {
            bool success = await _batteryLimitService.SetLimitModeAsync(mode);
            if (success)
            {
                if (mode == BatteryChargeLimitMode.SmartCharge80)
                {
                    TriggerToast(new ToastPayload(
                        Icon: "BATTERY_HEALTH",
                        Title: "Smart Charge 80% Aktif",
                        Subtitle: "Pengisian daya fisik dibatasi ke 80% (Mode Sehat Baterai)",
                        AccentHex: "#22C55E",
                        Duration: TimeSpan.FromSeconds(3.0)));
                }
                else
                {
                    TriggerToast(new ToastPayload(
                        Icon: "BOLT",
                        Title: "Kapasitas Penuh 100% Aktif",
                        Subtitle: "Batas pengisian dilepas hingga kapasitas penuh 100%",
                        AccentHex: "#38BDF8",
                        Duration: TimeSpan.FromSeconds(3.0)));
                }
                UpdateHardwareUi();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SetBatteryLimit", "Gagal mengubah batas pengisian baterai", ex);
        }
    }

    private void OnBatteryCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_batteryLimitService.IsSupported)
        {
            var nextMode = _batteryLimitService.CurrentMode == BatteryChargeLimitMode.SmartCharge80
                ? BatteryChargeLimitMode.FullCapacity100
                : BatteryChargeLimitMode.SmartCharge80;
            SetBatteryLimit(nextMode);
        }
        else
        {
            OpenSettingsWindow(0);
        }
    }

    private void OnBatteryCardRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettingsWindow(0);
    }
}
