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

        bool isMono = IsMonochromeTheme;
        SysVolumePercentText.Text = soundMuted ? "MUTED" : $"{vol}%";
        var volColor = isMono
            ? (soundMuted ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
            : (soundMuted
                ? Color.FromRgb(248, 113, 113)
                : isHeadphones
                    ? Color.FromRgb(192, 132, 252)
                    : Color.FromRgb(56, 189, 248));

        var volBrush = BrushCache.Get(volColor);
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
        SysBrightnessPercentText.Foreground = isMono ? BrushCache.Get(Color.FromRgb(228, 228, 231)) : (Brush)FindResource("AppAccentBrush");
        SysBrightnessFillBorder.Background = isMono ? BrushCache.Get(Color.FromRgb(228, 228, 231)) : (Brush)FindResource("AppAccentBrush");
        double brtTrackW = SysBrightnessTrackBorder.ActualWidth > 10 ? SysBrightnessTrackBorder.ActualWidth : 185.0;
        double brtFillPx = brtTrackW * Math.Clamp(brt / 100.0, 0.0, 1.0);
        SysBrightnessFillBorder.Width = brtFillPx;

        ToggleMuteIconPath.Data = soundMuted
            ? GetVectorGeometry("GeomSpeakerMuted")
            : GetVectorGeometry("GeomSpeakerOn");
        var soundBtnBrush = BrushCache.Get(
            isMono
                ? (soundMuted ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
                : (soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(56, 189, 248)));
        ToggleMuteIconPath.Fill = soundBtnBrush;
        ToggleMuteText.Text = soundMuted ? "Sound: MUTED" : "Sound: ON";
        ToggleMuteText.Foreground = isMono ? soundBtnBrush : BrushCache.Get(
            soundMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(248, 250, 252));

        ToggleMicIconPath.Data = micMuted
            ? GetVectorGeometry("GeomMicMuted")
            : GetVectorGeometry("GeomMicOn");
        var micBtnBrush = BrushCache.Get(
            isMono
                ? (micMuted ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
                : (micMuted ? Color.FromRgb(248, 113, 113) : Color.FromRgb(34, 197, 94)));
        ToggleMicIconPath.Fill = micBtnBrush;
        ToggleMicText.Text = micMuted ? "Mic: MUTED" : "Mic: ON";
        ToggleMicText.Foreground = micBtnBrush;

        bool autoTuck = _settingsService.AutoTuckEnabled;
        var autoTuckColor = isMono
            ? (autoTuck ? Color.FromRgb(228, 228, 231) : Color.FromRgb(113, 113, 122))
            : (autoTuck ? Color.FromRgb(34, 197, 94) : Color.FromRgb(148, 163, 184));
        var autoTuckBrush = BrushCache.Get(autoTuckColor);
        ToggleAutoTuckIconPath.Fill = autoTuckBrush;
        ToggleAutoTuckText.Text = autoTuck ? $"Auto-Simpan ({_settingsService.IdleCycleSeconds}s): ON" : "Auto-Simpan: OFF";
        ToggleAutoTuckText.Foreground = autoTuckBrush;
        ToggleAutoTuckBtn.Background = BrushCache.Get(Color.FromArgb((byte)(autoTuck ? 28 : 14), autoTuckColor.R, autoTuckColor.G, autoTuckColor.B));

        var hw = _hardwareService.Current;
        bool btOn = hw.IsBluetoothRadioOn;
        bool btConnected = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName);
        var btColor = isMono
            ? (!btOn ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
            : (!btOn
                ? Color.FromRgb(148, 163, 184)
                : btConnected
                    ? Color.FromRgb(34, 197, 94)
                    : Color.FromRgb(56, 189, 248));
        var btBrush = BrushCache.Get(btColor);
        SysBluetoothIconPath.Data = btOn ? GetVectorGeometry("GeomBluetooth") : GetVectorGeometry("GeomBluetoothOff");
        SysBluetoothIconPath.Fill = btBrush;
        SysBluetoothBtnText.Text = !btOn ? "BT: OFF" : (btConnected ? "BT: Aktif" : "Bluetooth");
        SysBluetoothBtnText.Foreground = btBrush;
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
            ? (loc.IsEnglish ? $"Wi-Fi Connected: {hw.NetworkName}" : $"Wi-Fi Terhubung: {hw.NetworkName}")
            : (loc.IsEnglish ? "Not Connected to Network" : "Tidak Terhubung ke Jaringan");

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
        HeaderBluetoothText.Text = !btOn
            ? "Off"
            : (btConnected ? hw.ConnectedBluetoothName : "On");
        HeaderBluetoothText.Foreground = BrushCache.Get(
            isMono
                ? (!btOn ? Color.FromRgb(113, 113, 122) : Color.FromRgb(228, 228, 231))
                : (!btOn ? Color.FromRgb(148, 163, 184) : Color.FromRgb(226, 232, 240)));
        HeaderBluetoothBtn.ToolTip = loc.IsEnglish
            ? (btConnected ? $"Bluetooth: {hw.ConnectedBluetoothName} (Left-Click: ON/OFF | Right-Click: Settings)" : $"Bluetooth {(btOn ? "On" : "Off")} (Left-Click: ON/OFF | Right-Click: Settings)")
            : (btConnected ? $"Bluetooth: {hw.ConnectedBluetoothName} (Klik Kiri: ON/OFF | Klik Kanan: Pengaturan)" : $"Bluetooth {(btOn ? "Aktif" : "Mati")} (Klik Kiri: ON/OFF | Klik Kanan: Pengaturan)");

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

        SysBatteryDetailText.Text = hw.HasBattery
            ? $"{(hw.IsCharging ? "Charging" : "Baterai")} • {hw.BatteryPercent}%{batteryEstimate}{limitModeInfo}"
            : "Daya Desktop AC (100%)";

        SysNetworkDetailText.Text = !string.IsNullOrWhiteSpace(hw.ConnectedBluetoothName)
            ? $"{hw.ConnectedBluetoothName} • {hw.NetworkName}"
            : hw.NetworkName;

        if (hw.IsCameraInUse || hw.IsMicrophoneInUse)
        {
            string activeDevices = (hw.IsCameraInUse && hw.IsMicrophoneInUse)
                ? "Kamera & Mic Aktif"
                : hw.IsCameraInUse ? "Kamera Aktif" : $"Mic Aktif ({hw.ActiveMicApp})";
            var warnColor = isMono ? Color.FromRgb(228, 228, 231) : Color.FromRgb(249, 115, 22);
            var warnBrush = BrushCache.Get(warnColor);
            SysPrivacyDetailText.Text = activeDevices;
            SysPrivacyDetailText.Foreground = warnBrush;
            SysPrivacyIconPath.Fill = warnBrush;
        }
        else
        {
            var safeColor = isMono ? Color.FromRgb(161, 161, 170) : Color.FromRgb(34, 197, 94);
            var safeBrush = BrushCache.Get(safeColor);
            SysPrivacyDetailText.Text = "Kamera & Mic Aman (Idle)";
            SysPrivacyDetailText.Foreground = safeBrush;
            SysPrivacyIconPath.Fill = safeBrush;
        }

        SysMemoryDetailText.Text = $"RAM: {hw.AppMemoryMb:0.0} MB • {(_isRenderLoopAttached ? "Active 60FPS" : "0% CPU Idle")}";
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
        AmbientGlowBrush.Color = accent;

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

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private async void OnBluetoothQuickClick(object sender, RoutedEventArgs e)
    {
        await _hardwareService.ToggleBluetoothRadioAsync();
        _hardwareService.RefreshFullTelemetry(emitToasts: false);
        UpdateSlidersAndMuteUi();
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
