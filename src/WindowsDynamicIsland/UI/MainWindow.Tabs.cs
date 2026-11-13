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
/// Island state machine transitions, tab navigation, Pomodoro/Stopwatch, File Shelf/Clipboard/QR/OCR/Picker, Shortcuts, Performance, Todo, Calendar, Weather, and Notification History.
/// </summary>
public partial class MainWindow
{
    private void OnIslandStateChanged(IslandVisualState state, IslandDashboardTab tab)
    {
        var (w, h, bottomRadius, topEarRadius, glow) = _stateMachine.GetTargetDimensions();
        if (_stateMachine.IsScreenshotMode)
        {
            _morph.SnapTo(w, h, bottomRadius, topEarRadius, glow);
            _targetLeftWingWidth = 0;
            _targetRightWingWidth = 0;
            _leftWingWidth = 0;
            _rightWingWidth = 0;
            LeftWingContainer.Visibility = Visibility.Collapsed;
            RightWingContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            bool expanding = w > _morph.Width.Current || h > _morph.Height.Current;
            _morph.MorphTo(w, h, bottomRadius, topEarRadius, glow, bouncy: expanding);
        }

        TuckedHandlePanel.Visibility = state == IslandVisualState.TuckedTopEdge ? Visibility.Visible : Visibility.Collapsed;
        if (state == IslandVisualState.TuckedTopEdge)
        {
            if (IsMonochromeTheme)
            {
                TuckedAccentBar.Background = BrushCache.Get(Color.FromRgb(228, 228, 231));
            }
            else if (_mediaService.CurrentTrack.HasActiveSession && _mediaService.CurrentTrack.Title != "Tidak Ada Media")
            {
                TuckedAccentBar.Background = BrushCache.Get(_mediaService.CurrentTrack.DominantColor);
            }
            else if (_pomodoroService.IsRunning)
            {
                TuckedAccentBar.Background = BrushCache.Get(Color.FromRgb(251, 113, 133));
            }
            else if (_hardwareService.Current.IsCharging)
            {
                TuckedAccentBar.Background = BrushCache.Get(Color.FromRgb(34, 197, 94));
            }
            else
            {
                TuckedAccentBar.Background = BrushCache.Get(_currentThemeAccentColor);
            }
        }

        IdlePillPanel.Visibility = state == IslandVisualState.IdlePill ? Visibility.Visible : Visibility.Collapsed;
        PinnedPerfPillPanel.Visibility = state == IslandVisualState.PinnedPerformancePill ? Visibility.Visible : Visibility.Collapsed;
        CompactMediaPanel.Visibility = state == IslandVisualState.CompactMedia ? Visibility.Visible : Visibility.Collapsed;
        ToastPanel.Visibility = state == IslandVisualState.ToastNotification ? Visibility.Visible : Visibility.Collapsed;
        ExpandedDashboardPanel.Visibility = state == IslandVisualState.ExpandedDashboard ? Visibility.Visible : Visibility.Collapsed;
        SpotlightPanel.Visibility = state == IslandVisualState.SpotlightSearch ? Visibility.Visible : Visibility.Collapsed;

        if (state == IslandVisualState.PinnedPerformancePill)
        {
            UpdatePerformanceUi();
        }

        double visibleOpacity = state == IslandVisualState.Hidden ? 0.0 : 1.0;
        IslandBorder.Opacity = visibleOpacity;
        UnifiedNotchPath.Opacity = visibleOpacity;
        UnifiedNotchRimPath.Opacity = visibleOpacity;

        // Completely remove drop-shadow and glow bleed when tucked or hidden so pixels below Y=5px have Alpha=0
        bool suppressShadowAndGlow = state is IslandVisualState.TuckedTopEdge or IslandVisualState.Hidden;
        UnifiedNotchShadow.Opacity = suppressShadowAndGlow ? 0.0 : 0.72;
        AmbientGlowBorder.Visibility = suppressShadowAndGlow ? Visibility.Collapsed : Visibility.Visible;

        _hardwareService.IsPerformanceViewActive =
            state == IslandVisualState.PinnedPerformancePill ||
            (state == IslandVisualState.ExpandedDashboard && tab == IslandDashboardTab.PerformanceMonitor);

        if (state is IslandVisualState.TuckedTopEdge or IslandVisualState.Hidden || _stateMachine.IsFullScreenHidden)
        {
            _fastUpdateTimer.Interval = TimeSpan.FromMilliseconds(400);
        }
        else
        {
            _fastUpdateTimer.Interval = TimeSpan.FromMilliseconds(100);
        }

        MediaTabGrid.Visibility = tab == IslandDashboardTab.MediaLyrics ? Visibility.Visible : Visibility.Collapsed;
        TodoAndWaterTabGrid.Visibility = tab == IslandDashboardTab.TodoAndWater ? Visibility.Visible : Visibility.Collapsed;
        PomodoroTabGrid.Visibility = tab == IslandDashboardTab.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsTabGrid.Visibility = tab == IslandDashboardTab.AppShortcuts ? Visibility.Visible : Visibility.Collapsed;
        PerformanceTabGrid.Visibility = tab == IslandDashboardTab.PerformanceMonitor ? Visibility.Visible : Visibility.Collapsed;
        ShelfTabGrid.Visibility = tab == IslandDashboardTab.FileShelf ? Visibility.Visible : Visibility.Collapsed;
                CalendarScheduleTabGrid.Visibility = tab == IslandDashboardTab.CalendarSchedule ? Visibility.Visible : Visibility.Collapsed;
        WeatherTabGrid.Visibility = tab == IslandDashboardTab.Weather ? Visibility.Visible : Visibility.Collapsed;
        NotificationHistoryTabGrid.Visibility = tab == IslandDashboardTab.NotificationHistory ? Visibility.Visible : Visibility.Collapsed;
        SystemTabGrid.Visibility = tab == IslandDashboardTab.SystemStatus ? Visibility.Visible : Visibility.Collapsed;

        if (state == IslandVisualState.ExpandedDashboard)
        {
            SetWindowActivatable(true);
            AnimateTabContentSlide(tab);

            if (tab == IslandDashboardTab.MediaLyrics)
            {
                UpdateAppMixerUi();
            }
            else if (tab == IslandDashboardTab.SystemStatus)
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
            else if (tab == IslandDashboardTab.AppShortcuts)
            {
                UpdateShortcutsUi();
            }
            else if (tab == IslandDashboardTab.PerformanceMonitor)
            {
                UpdatePerformanceUi();
            }
        }
        else if (state == IslandVisualState.SpotlightSearch)
        {
            SetWindowActivatable(true);
            ForceActivateAndFocus(SpotlightSearchBox);
        }
        else
        {
            SetBottomDockMorphed(false);
            SetWindowActivatable(false);
            Keyboard.ClearFocus();
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

            var enabled = GetEnabledTabs();
            int prevIdx = enabled.IndexOf(_previousActiveTab);
            int nextIdx = enabled.IndexOf(newTab);
            int direction;

            if (_explicitSlideDirection != 0)
            {
                direction = _explicitSlideDirection;
                _explicitSlideDirection = 0;
            }
            else if (prevIdx >= 0 && nextIdx >= 0 && enabled.Count > 1)
            {
                if (prevIdx == enabled.Count - 1 && nextIdx == 0) direction = 1;
                else if (prevIdx == 0 && nextIdx == enabled.Count - 1) direction = -1;
                else direction = nextIdx > prevIdx ? 1 : -1;
            }
            else
            {
                direction = (int)newTab > (int)_previousActiveTab ? 1 : -1;
            }

            _previousActiveTab = newTab;

            double startOffset = direction * 85.0;

            TabContentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TabContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TabContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TabContentContainer.BeginAnimation(UIElement.OpacityProperty, null);

            TabContentTranslate.X = startOffset;
            TabContentScale.ScaleX = 0.97;
            TabContentScale.ScaleY = 0.97;
            TabContentContainer.Opacity = 0.15;

            var slideAnimation = new DoubleAnimation
            {
                From = startOffset,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };

            var scaleAnimation = new DoubleAnimation
            {
                From = 0.97,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };

            var fadeAnimation = new DoubleAnimation
            {
                From = 0.15,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            TabContentTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
            TabContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            TabContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            TabContentContainer.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AnimateTabContentSlide", "Error running tab slide animation", ex);
        }
    }

    private void UpdateTabHighlight()
    {
        Color accent = _currentThemeAccentColor;
        var activeBrush = BrushCache.Get(Color.FromArgb(52, accent.R, accent.G, accent.B));
        var activeBorder = BrushCache.Get(Color.FromArgb(115, accent.R, accent.G, accent.B));
        var inactiveBrush = Brushes.Transparent;
        var activeFg = Brushes.White;
        var inactiveFg = BrushCache.Get(Color.FromRgb(148, 163, 184));

        SetTabVisual(TabMediaBtn, _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics);
        SetTabVisual(TabTodoBtn, _stateMachine.ActiveTab == IslandDashboardTab.TodoAndWater);
        SetTabVisual(TabPomodoroBtn, _stateMachine.ActiveTab == IslandDashboardTab.Pomodoro);
        SetTabVisual(TabShortcutsBtn, _stateMachine.ActiveTab == IslandDashboardTab.AppShortcuts);
        SetTabVisual(TabPerformanceBtn, _stateMachine.ActiveTab == IslandDashboardTab.PerformanceMonitor);
        SetTabVisual(TabShelfBtn, _stateMachine.ActiveTab == IslandDashboardTab.FileShelf);
                SetTabVisual(TabCalendarBtn, _stateMachine.ActiveTab == IslandDashboardTab.CalendarSchedule);
        SetTabVisual(TabWeatherBtn, _stateMachine.ActiveTab == IslandDashboardTab.Weather);
        SetTabVisual(TabNotifBtn, _stateMachine.ActiveTab == IslandDashboardTab.NotificationHistory);
        SetTabVisual(TabSystemBtn, _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus);

        UpdatePaginationDots();
        UpdateSideWingVisuals();

        void SetTabVisual(Button btn, bool isSelected)
        {
            btn.Background = isSelected ? activeBrush : inactiveBrush;
            btn.BorderBrush = isSelected ? activeBorder : inactiveBrush;
            btn.Foreground = isSelected ? activeFg : inactiveFg;
        }
    }

    private void UpdatePomodoroAndClockUi()
    {
        string fmt = _settingsService.Use24HourClock
            ? (_settingsService.ShowSecondsOnClock ? "HH:mm:ss" : "HH:mm")
            : (_settingsService.ShowSecondsOnClock ? "h:mm:ss tt" : "h:mm tt");

        string nowStr = DateTime.Now.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        ExpandedClockBadge.Text = DateTime.Now.ToString(_settingsService.Use24HourClock ? "HH:mm" : "h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

        var dateCulture = LocalizationService.Instance.IsEnglish
            ? new System.Globalization.CultureInfo("en-US")
            : new System.Globalization.CultureInfo("id-ID");
        string idDate = DateTime.Now.ToString("d MMM", dateCulture);
        ExpandedDateBadge.Text = DateTime.Now.ToString("ddd, d MMM", dateCulture);
        IdleDateText.Text = idDate;
        IdleDateText.Visibility = (_settingsService.ShowDateOnIdle && !_settingsService.MinimalNotchEnabled)
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool isSw = _pomodoroService.IsStopwatchMode;
        if (_pomodoroService.IsRunning && _settingsService.ShowPomodoroOnIdle)
        {
            IdlePomodoroCountdownBadge.Visibility = Visibility.Visible;
            IdlePomodoroCountdownText.Text = _pomodoroService.ActiveDisplayClock;
            var pomoColor = IsMonochromeTheme
                ? Color.FromRgb(228, 228, 231)
                : isSw
                    ? Color.FromRgb(56, 189, 248)
                    : (_pomodoroService.IsBreakMode ? Color.FromRgb(56, 189, 248) : Color.FromRgb(34, 197, 94));
            var pomoBrush = BrushCache.Get(pomoColor);
            IdlePomodoroCountdownBadge.Background = BrushCache.Get(Color.FromArgb(32, pomoColor.R, pomoColor.G, pomoColor.B));
            IdlePomodoroCountdownBadge.BorderBrush = BrushCache.Get(Color.FromArgb(64, pomoColor.R, pomoColor.G, pomoColor.B));
            IdlePomodoroRingPath.Stroke = pomoBrush;
            UpdateIdlePomodoroMiniRing(isSw ? (_pomodoroService.StopwatchElapsed.TotalSeconds % 60.0) / 60.0 : _pomodoroService.ProgressRatio);

            ClockTextBlock.Visibility = _settingsService.MinimalNotchEnabled ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            IdlePomodoroCountdownBadge.Visibility = Visibility.Collapsed;
            ClockTextBlock.Visibility = Visibility.Visible;
        }

        ClockTextBlock.Text = nowStr;

        var loc = LocalizationService.Instance;
        PomodoroTimeText.Text = _pomodoroService.ActiveDisplayClock;
        PomodoroModeLabel.Text = isSw
            ? loc.Get("Pomo_ModeStopwatch", "Stopwatch")
            : (_pomodoroService.IsBreakMode ? loc.Get("Pomo_BreakMode", "Break") : loc.Get("Pomo_FocusMode", "Focus Session"));

        var pomoModeColor = IsMonochromeTheme
            ? Color.FromRgb(228, 228, 231)
            : isSw
                ? Color.FromRgb(56, 189, 248)
                : (_pomodoroService.IsBreakMode ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));
        PomodoroModeLabel.Foreground = BrushCache.Get(
            IsMonochromeTheme
                ? Color.FromRgb(228, 228, 231)
                : isSw
                    ? Color.FromRgb(56, 189, 248)
                    : (_pomodoroService.IsBreakMode ? Color.FromRgb(34, 197, 94) : Color.FromRgb(248, 250, 252)));
        PomodoroModeBadge.Background = BrushCache.Get(Color.FromArgb(34, pomoModeColor.R, pomoModeColor.G, pomoModeColor.B));
        PomodoroModeBadge.BorderBrush = BrushCache.Get(Color.FromArgb(64, pomoModeColor.R, pomoModeColor.G, pomoModeColor.B));
        PomodoroModeDot.Fill = BrushCache.Get(pomoModeColor);

        if (isSw)
        {
            double ringRatio = (_pomodoroService.StopwatchElapsed.TotalSeconds % 60.0) / 60.0;
            PomodoroPercentText.Text = $"{(int)_pomodoroService.StopwatchElapsed.TotalSeconds % 60:00}s";
            UpdatePomodoroProgressRing(ringRatio);

            PomoModeTimerBtn.Background = Brushes.Transparent;
            PomoModeTimerText.Foreground = BrushCache.Get(Color.FromRgb(148, 163, 184));
            PomoModeStopwatchBtn.Background = BrushCache.Get(Color.FromArgb(44, 56, 189, 248));
            PomoModeStopwatchText.Foreground = BrushCache.Get(Color.FromRgb(248, 250, 252));

            StopwatchLapBtn.Visibility = Visibility.Visible;
            PomodoroBreakBtn.Visibility = Visibility.Collapsed;
            PomodoroResetFocusBtn.Visibility = Visibility.Collapsed;
            PomodoroAdd5Btn.Visibility = Visibility.Collapsed;

            if (_pomodoroService.StopwatchLaps.Count > 0)
            {
                StopwatchLapSummaryText.Visibility = Visibility.Visible;
                StopwatchLapSummaryText.Text = string.Join("  |  ", _pomodoroService.StopwatchLaps.Take(2));
            }
            else
            {
                StopwatchLapSummaryText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            int pomoPct = (int)Math.Round(_pomodoroService.ProgressRatio * 100);
            PomodoroPercentText.Text = $"{pomoPct}%";
            UpdatePomodoroProgressRing(_pomodoroService.ProgressRatio);

            PomoModeTimerBtn.Background = BrushCache.Get(Color.FromArgb(44, 239, 68, 68));
            PomoModeTimerText.Foreground = BrushCache.Get(Color.FromRgb(248, 250, 252));
            PomoModeStopwatchBtn.Background = Brushes.Transparent;
            PomoModeStopwatchText.Foreground = BrushCache.Get(Color.FromRgb(148, 163, 184));

            StopwatchLapBtn.Visibility = Visibility.Collapsed;
            StopwatchLapSummaryText.Visibility = Visibility.Collapsed;
            PomodoroBreakBtn.Visibility = Visibility.Visible;
            PomodoroResetFocusBtn.Visibility = Visibility.Visible;
            PomodoroAdd5Btn.Visibility = Visibility.Visible;
        }

        PomodoroPlayPauseIconPath.Data = _pomodoroService.IsRunning
            ? GetVectorGeometry("GeomPause")
            : GetVectorGeometry("GeomPlay");
        PomodoroStartPauseText.Text = _pomodoroService.IsRunning
            ? loc.Get("Pomo_Pause", "Pause")
            : (isSw ? loc.Get("Pomo_StopwatchStart", "Start Stopwatch") : loc.Get("Pomo_Start", "Start"));
    }

    private void UpdateIdlePomodoroMiniRing(double progressRatio)
    {
        try
        {
            double radius = 5.2;
            double cx = 6.5;
            double cy = 6.5;

            double ratio = Math.Clamp(1.0 - progressRatio, 0.05, 0.999);
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
            IdlePomodoroRingPath.Data = geom;
        }
        catch { }
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

    private void UpdateShelfUi()
    {
        var loc = LocalizationService.Instance;
        int count = _fileShelfService.Items.Count;
        _stateMachine.UpdateShelfCount(count);
        EmptyShelfHintPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfHeaderCountText.Text = count == 0
            ? (loc.IsEnglish ? "Drop files temporarily here (Drag In & Out)" : "Taruh file/gambar sementara di sini (Drag Masuk & Keluar)")
            : (loc.IsEnglish ? $"{count} item(s) held in tray (Drag out to send)" : $"{count} item tersimpan di rak sementara (Tarik keluar untuk kirim)");

        if (!_isShelfClipboardMode && ShelfActionButton != null)
        {
            ShelfActionButton.Content = loc.Get("Shelf_Clear", "Clear");
        }

        if (count > 0)
        {
            TabShelfCountBadge.Visibility = Visibility.Visible;
            TabShelfCountText.Text = $"{count}";
        }
        else
        {
            TabShelfCountBadge.Visibility = Visibility.Collapsed;
            if (_fileShelfShareService.IsRunning)
            {
                _fileShelfShareService.StopSharing();
                ShelfQrOverlayBorder.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnClearShelfClick(object sender, RoutedEventArgs e)
    {
        _fileShelfShareService.StopSharing();
        ShelfQrOverlayBorder.Visibility = Visibility.Collapsed;
        _fileShelfService.Clear();
        UpdateShelfUi();
    }

    private void OnShelfModeFilesClick(object sender, RoutedEventArgs e)
    {
        _isShelfClipboardMode = false;
        ShelfFilesContainer.Visibility = Visibility.Visible;
        ShelfClipboardContainer.Visibility = Visibility.Collapsed;

        var accentBrush = (Brush)FindResource("AppAccentBrush");
        var slateBrush = BrushCache.Get(Color.FromRgb(148, 163, 184));

        ShelfModeFilesIcon.Fill = accentBrush;
        ShelfModeFilesText.Foreground = accentBrush;
        ShelfModeClipboardIcon.Fill = slateBrush;
        ShelfModeClipboardText.Foreground = slateBrush;

        ShelfActionButton.Content = LocalizationService.Instance.Get("Shelf_Clear", "Clear");
        UpdateShelfUi();
    }

    private void OnShelfModeClipboardClick(object sender, RoutedEventArgs e)
    {
        _isShelfClipboardMode = true;
        ShelfFilesContainer.Visibility = Visibility.Collapsed;
        ShelfClipboardContainer.Visibility = Visibility.Visible;
        ShelfQrOverlayBorder.Visibility = Visibility.Collapsed;

        var accentBrush = (Brush)FindResource("AppAccentBrush");
        var slateBrush = BrushCache.Get(Color.FromRgb(148, 163, 184));

        ShelfModeFilesIcon.Fill = slateBrush;
        ShelfModeFilesText.Foreground = slateBrush;
        ShelfModeClipboardIcon.Fill = accentBrush;
        ShelfModeClipboardText.Foreground = accentBrush;

        ShelfActionButton.Content = LocalizationService.Instance.Get("Notif_Clear", "Clear All");
        RefreshClipboardHistoryView();
    }

    private void OnShelfActionClick(object sender, RoutedEventArgs e)
    {
        if (_isShelfClipboardMode)
        {
            _clipboardHistoryService.ClearAll();
            RefreshClipboardHistoryView();
            TriggerToast(new ToastPayload(
                Icon: "FOLDER",
                Title: "Riwayat Klip Dikosongkan",
                Subtitle: "Semua cuplikan papan klip berhasil dihapus",
                AccentHex: "#94A3B8",
                Duration: TimeSpan.FromSeconds(2.0)));
        }
        else
        {
            OnClearShelfClick(sender, e);
        }
    }

    private void OnCopyClipboardItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipboardItemViewModel vm)
        {
            _clipboardHistoryService.CopyToClipboard(vm.SourceItem);
            TriggerToast(new ToastPayload(
                Icon: "FOLDER",
                Title: "Tersalin ke Clipboard",
                Subtitle: vm.PreviewText,
                AccentHex: "#38BDF8",
                Duration: TimeSpan.FromSeconds(2.0)));
        }
    }

    private void OnDeleteClipboardItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipboardItemViewModel vm)
        {
            _clipboardHistoryService.Remove(vm.SourceItem);
            RefreshClipboardHistoryView();
        }
    }

    private void OnClipboardHistoryChanged()
    {
        if (_isShelfClipboardMode && ShelfTabGrid.Visibility == Visibility.Visible)
        {
            RefreshClipboardHistoryView();
        }
    }

    private void RefreshClipboardHistoryView()
    {
        var items = _clipboardHistoryService.Items;
        if (items.Count == 0)
        {
            EmptyClipboardHintPanel.Visibility = Visibility.Visible;
            ShelfHeaderCountText.Text = "Papan Klip Kosong";
            ClipboardItemsControl.ItemsSource = null;
            return;
        }

        EmptyClipboardHintPanel.Visibility = Visibility.Collapsed;
        ShelfHeaderCountText.Text = $"{items.Count} Klip Tersimpan";

        var viewModels = new List<ClipboardItemViewModel>();
        foreach (var item in items)
        {
            Color badgeColor;
            try
            {
                badgeColor = (Color)ColorConverter.ConvertFromString(item.BadgeColorHex);
            }
            catch
            {
                badgeColor = Color.FromRgb(148, 163, 184);
            }

            viewModels.Add(new ClipboardItemViewModel
            {
                BadgeBrush = BrushCache.Get(badgeColor),
                SourceItem = item
            });
        }

        ClipboardItemsControl.ItemsSource = viewModels;
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
    private void OnTabShortcutsClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.AppShortcuts);
        UpdateShortcutsUi();
    }
    private void OnTabPerformanceClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.PerformanceMonitor);
        UpdatePerformanceUi();
    }
    private void OnTabShelfClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.FileShelf);
    private void OnTabSystemClick(object sender, RoutedEventArgs e)
    {
        SwitchToTab(IslandDashboardTab.SystemStatus);
        UpdateSlidersAndMuteUi();
    }

    private void OnLeftWingClick(object sender, RoutedEventArgs e)
    {
        _explicitSlideDirection = -1;
        _stateMachine.SetActiveTab(GetPrevTab(_stateMachine.ActiveTab));
        UpdateSideWingVisuals();
    }

    private void OnRightWingClick(object sender, RoutedEventArgs e)
    {
        _explicitSlideDirection = 1;
        _stateMachine.SetActiveTab(GetNextTab(_stateMachine.ActiveTab));
        UpdateSideWingVisuals();
    }

    private void OnDot0Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.MediaLyrics);
    private void OnDot1Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.TodoAndWater);
    private void OnDot2Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.Pomodoro);
    private void OnDotShortcutsClick(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.AppShortcuts);
    private void OnDotPerfClick(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.PerformanceMonitor);
    private void OnDot3Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.FileShelf);
    private void OnDot4Click(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.SystemStatus);

    private void UpdatePaginationDots()
    {
        try
        {
            var activeDotBrush = BrushCache.Get(_currentThemeAccentColor);
            var inactiveDotBrush = BrushCache.Get(Color.FromArgb(48, 255, 255, 255)); // #30FFFFFF

            PageDot0.Visibility = _settingsService.ShowTabMedia ? Visibility.Visible : Visibility.Collapsed;
            PageDot1.Visibility = _settingsService.ShowTabTodo ? Visibility.Visible : Visibility.Collapsed;
            PageDot2.Visibility = _settingsService.ShowTabPomodoro ? Visibility.Visible : Visibility.Collapsed;
            PageDotShortcuts.Visibility = _settingsService.ShowTabShortcuts ? Visibility.Visible : Visibility.Collapsed;
            PageDotPerf.Visibility = _settingsService.ShowTabPerformance ? Visibility.Visible : Visibility.Collapsed;
            PageDot3.Visibility = _settingsService.ShowTabShelf ? Visibility.Visible : Visibility.Collapsed;
            PageDot4.Visibility = Visibility.Collapsed;

            SetDot(PageDot0, _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics);
            SetDot(PageDot1, _stateMachine.ActiveTab == IslandDashboardTab.TodoAndWater);
            SetDot(PageDot2, _stateMachine.ActiveTab == IslandDashboardTab.Pomodoro);
            SetDot(PageDotShortcuts, _stateMachine.ActiveTab == IslandDashboardTab.AppShortcuts);
            SetDot(PageDotPerf, _stateMachine.ActiveTab == IslandDashboardTab.PerformanceMonitor);
            SetDot(PageDot3, _stateMachine.ActiveTab == IslandDashboardTab.FileShelf);
                        SetDot(PageDotCal, _stateMachine.ActiveTab == IslandDashboardTab.CalendarSchedule);
            SetDot(PageDotWeather, _stateMachine.ActiveTab == IslandDashboardTab.Weather);
            SetDot(PageDotNotif, _stateMachine.ActiveTab == IslandDashboardTab.NotificationHistory);
            SetDot(PageDot4, _stateMachine.ActiveTab == IslandDashboardTab.SystemStatus);

            void SetDot(Border dot, bool isActive)
            {
                double targetWidth = isActive ? 18.0 : 4.5;
                if (Math.Abs(dot.Width - targetWidth) > 0.5)
                {
                    var anim = new DoubleAnimation
                    {
                        To = targetWidth,
                        Duration = TimeSpan.FromMilliseconds(240),
                        EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
                    };
                    dot.BeginAnimation(FrameworkElement.WidthProperty, anim);
                }
                dot.Background = isActive ? activeDotBrush : inactiveDotBrush;
            }
        }
        catch { }
    }

    // --- APP SHORTCUTS LAUNCHER TAB HANDLERS ---

    private void UpdateShortcutsUi()
    {
        try
        {
            var list = _shortcutService.Items.ToList();
            ShortcutsItemsControl.ItemsSource = list;
            ShortcutsCountBadgeText.Text = $"{list.Count} App";
        }
        catch (Exception ex)
        {
            AppLogger.LogError("UpdateShortcutsUi", "Error updating Shortcuts UI", ex);
        }
    }

    private void OnShortcutCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var item = fe.Tag as AppShortcutItem ?? fe.DataContext as AppShortcutItem;
            if (item != null)
            {
                bool ok = _shortcutService.LaunchShortcut(item);
                if (ok)
                {
                    // Gently collapse after launching app so user can immediately interact with the opened app
                    _stateMachine.SetHovered(false);
                }
                e.Handled = true;
            }
        }
    }

    private void OnRemoveShortcutClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            _shortcutService.RemoveShortcut(id);
            UpdateShortcutsUi();
        }
    }

    private void OnResetShortcutsClick(object sender, RoutedEventArgs e)
    {
        _shortcutService.ResetDefaults();
        UpdateShortcutsUi();
    }

    private void OnAddShortcutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _collapseDebounceTimer.Stop();
            _autoTuckTimer.Stop();

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pilih Aplikasi atau Pintasan untuk Ditambahkan ke Dynamic Island",
                Filter = "Aplikasi & Pintasan (*.exe;*.lnk;*.bat;*.cmd;*.url)|*.exe;*.lnk;*.bat;*.cmd;*.url|Semua File (*.*)|*.*",
                Multiselect = true,
                DereferenceLinks = false
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    _shortcutService.AddShortcutFromPath(file);
                }
                UpdateShortcutsUi();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("OnAddShortcutClick", "Error adding shortcut via dialog", ex);
        }
    }

    // --- PERFORMANCE MONITOR TAB HANDLERS ---

    private void UpdatePerformanceUi()
    {
        try
        {
            var hw = _hardwareService.Current;

            PerfCpuPercentText.Text = $"{hw.CpuUsagePercent}%";
            PerfCpuModelText.Text = hw.CpuModelShort;
            PerfCpuCoresText.Text = $"{hw.CpuCoresCount} Logical Cores";
            UpdateRingGaugePath(PerfCpuRingPath, hw.CpuUsagePercent / 100.0);

            var cpuColor = IsMonochromeTheme
                ? (hw.CpuUsagePercent >= 85 ? Color.FromRgb(244, 244, 245) : (hw.CpuUsagePercent >= 60 ? Color.FromRgb(212, 212, 216) : Color.FromRgb(161, 161, 170)))
                : (hw.CpuUsagePercent >= 85
                    ? Color.FromRgb(251, 113, 133)
                    : hw.CpuUsagePercent >= 60
                        ? Color.FromRgb(251, 191, 36)
                        : Color.FromRgb(56, 189, 248));
            var cpuBrush = BrushCache.Get(cpuColor);
            PerfCpuRingPath.Stroke = cpuBrush;
            PerfCpuPercentText.Foreground = cpuBrush;

            PerfGpuPercentText.Text = $"{hw.GpuUsagePercent}%";
            PerfGpuModelText.Text = hw.GpuModelShort;
            if (PerfGpuVramText != null) PerfGpuVramText.Text = hw.GpuVramText;
            UpdateRingGaugePath(PerfGpuRingPath, hw.GpuUsagePercent / 100.0);

            var gpuColor = IsMonochromeTheme
                ? (hw.GpuUsagePercent >= 85 ? Color.FromRgb(244, 244, 245) : (hw.GpuUsagePercent >= 65 ? Color.FromRgb(212, 212, 216) : Color.FromRgb(161, 161, 170)))
                : (hw.GpuUsagePercent >= 85
                    ? Color.FromRgb(251, 113, 133)
                    : hw.GpuUsagePercent >= 65
                        ? Color.FromRgb(251, 191, 36)
                        : Color.FromRgb(168, 85, 247));
            var gpuBrush = BrushCache.Get(gpuColor);
            PerfGpuRingPath.Stroke = gpuBrush;
            PerfGpuPercentText.Foreground = gpuBrush;

            PerfRamPercentText.Text = $"{hw.RamUsagePercent}%";
            PerfRamDetailText.Text = $"{hw.RamUsedGb:0.0} / {hw.RamTotalGb:0.0} GB";
            PerfAppRamText.Text = $"Island: {hw.AppMemoryMb:0.0} MB";
            UpdateRingGaugePath(PerfRamRingPath, hw.RamUsagePercent / 100.0);

            var ramColor = IsMonochromeTheme
                ? (hw.RamUsagePercent >= 88 ? Color.FromRgb(244, 244, 245) : (hw.RamUsagePercent >= 75 ? Color.FromRgb(212, 212, 216) : Color.FromRgb(161, 161, 170)))
                : (hw.RamUsagePercent >= 88
                    ? Color.FromRgb(251, 113, 133)
                    : hw.RamUsagePercent >= 75
                        ? Color.FromRgb(251, 191, 36)
                        : Color.FromRgb(34, 197, 94));
            var ramBrush = BrushCache.Get(ramColor);
            PerfRamRingPath.Stroke = ramBrush;
            PerfRamPercentText.Foreground = ramBrush;

            PerfNetNameText.Text = hw.IsNetworkConnected ? hw.NetworkName : "Offline";
            PerfNetDownText.Text = hw.NetDownloadSpeedText;
            PerfNetUpText.Text = hw.NetUploadSpeedText;

            PerfDiskLabelText.Text = "Disk C:";
            PerfUptimeText.Text = $"{hw.DiskUsagePercent}%";
            PerfDiskBarFill.Width = 105.0 * Math.Clamp(hw.DiskUsagePercent / 100.0, 0.04, 1.0);

            // Update Pinned Compact Performance Pill (same size as IdlePill)
            PinPerfCpuText.Text = $"{hw.CpuUsagePercent}%";
            PinPerfCpuText.Foreground = cpuBrush;
            PinPerfGpuText.Text = $"{hw.GpuUsagePercent}%";
            PinPerfGpuText.Foreground = gpuBrush;
            PinPerfRamText.Text = $"{hw.RamUsagePercent}%";
            PinPerfRamText.Foreground = ramBrush;
            PinPerfNetText.Text = hw.NetDownloadSpeedText;

            bool isPinned = _stateMachine.IsPerformancePinned;
            var pinAccent = IsMonochromeTheme
                ? (isPinned ? Color.FromRgb(244, 244, 245) : Color.FromRgb(113, 113, 122))
                : (isPinned ? Color.FromRgb(56, 189, 248) : Color.FromRgb(148, 163, 184));
            var pinBrush = BrushCache.Get(pinAccent);
            PinPerfToggleIcon.Fill = pinBrush;
            PinPerfToggleText.Text = isPinned ? "Pin Stay: ON" : "Pin Stay: OFF";
            PinPerfToggleText.Foreground = isPinned ? pinBrush : BrushCache.Get(Color.FromRgb(226, 232, 240));
            PinPerfToggleBtn.Background = BrushCache.Get(
                IsMonochromeTheme
                    ? (isPinned ? Color.FromArgb(40, 244, 244, 245) : Color.FromArgb(24, 255, 255, 255))
                    : (isPinned ? Color.FromArgb(40, 56, 189, 248) : Color.FromArgb(24, 255, 255, 255)));
            PinPerfToggleBtn.BorderBrush = BrushCache.Get(
                IsMonochromeTheme
                    ? (isPinned ? Color.FromArgb(90, 244, 244, 245) : Color.FromArgb(40, 255, 255, 255))
                    : (isPinned ? Color.FromArgb(90, 56, 189, 248) : Color.FromArgb(40, 255, 255, 255)));
        }
        catch (Exception ex)
        {
            AppLogger.LogError("UpdatePerformanceUi", "Error updating performance UI", ex);
        }
    }

    private void OnTogglePinPerformanceClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _stateMachine.TogglePerformancePinned();
        UpdatePerformanceUi();

        if (_stateMachine.IsPerformancePinned)
        {
            // Collapse expanded dashboard into the compact IdlePill-sized Performance Pill so it stays on top!
            _collapseDebounceTimer.Stop();
            _autoTuckTimer.Stop();
            _stateMachine.SetHovered(false);
        }
        else if (!_stateMachine.IsHovered && _settingsService.AutoTuckEnabled)
        {
            _stateMachine.SetAutoTucked(false);
            _autoTuckTimer.Stop();
            _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
            _autoTuckTimer.Start();
        }
    }

    private static void UpdateRingGaugePath(Path ringPath, double ratio)
    {
        try
        {
            double radius = 23.75;
            double cx = 27.0;
            double cy = 27.0;

            double clamped = Math.Clamp(ratio, 0.02, 0.999);
            double angleDeg = clamped * 360.0;
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
            ringPath.Data = geom;
        }
        catch { }
    }

    private void OnOpenTaskManagerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskmgr.exe",
                UseShellExecute = true
            });
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
                    IsDone = t.IsDone,
                    IsMonochrome = IsMonochromeTheme
                })
                .ToList();

            int total = _todoAndWaterService.TotalTodosCount;
            int done = _todoAndWaterService.CompletedTodosCount;
            int pending = total - done;

            TodoCounterText.Text = LocalizationService.Instance.IsEnglish ? $"{done}/{total} Done" : $"{done}/{total} Selesai";

            double ratio = total > 0 ? (double)done / total : 0.0;
            TodoProgressFill.Width = 38.0 * Math.Clamp(ratio, 0.0, 1.0);

            EmptyTodoHintPanel.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            TodoItemsControl.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (pending > 0)
            {
                IdleTodoMiniPill.Visibility = _settingsService.ShowTodoBadgeOnIdle ? Visibility.Visible : Visibility.Collapsed;
                IdleTodoMiniText.Text = $"{pending}";
                TabTodoCountBadge.Visibility = Visibility.Visible;
                TabTodoCountText.Text = $"{pending}";
            }
            else
            {
                IdleTodoMiniPill.Visibility = Visibility.Collapsed;
                TabTodoCountBadge.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("UpdateTodoAndWaterUi", "Error updating Todo and Water UI", ex);
        }
    }

    private void OnTodoInputBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != NewTodoInputBox)
        {
            ForceActivateAndFocus(NewTodoInputBox);
        }
    }

    private void OnTodoInputBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        ForceActivateAndFocus(NewTodoInputBox);
    }

    private void OnNewTodoInputGotFocus(object sender, RoutedEventArgs e)
    {
        _collapseDebounceTimer.Stop();
        if (NewTodoInputBorder != null)
        {
            NewTodoInputBorder.BorderBrush = BrushCache.Get(_currentThemeAccentColor);
        }
    }

    private void OnNewTodoInputLostFocus(object sender, RoutedEventArgs e)
    {
        if (NewTodoInputBorder != null)
        {
            NewTodoInputBorder.BorderBrush = BrushCache.Get(Color.FromArgb(42, 255, 255, 255));
        }
    }

    private void OnTodoItemTextBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb)
        {
            ForceActivateAndFocus(tb);
        }
    }

    private void OnTodoItemTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _collapseDebounceTimer.Stop();
    }

    private void OnNewTodoInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitNewTodo();
            e.Handled = true;
        }
    }

    private void OnNewTodoInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (TodoInputPlaceholder != null && NewTodoInputBox != null)
        {
            TodoInputPlaceholder.Visibility = string.IsNullOrEmpty(NewTodoInputBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnSubmitTodoClick(object sender, RoutedEventArgs e)
    {
        SubmitNewTodo();
    }

    private void SubmitNewTodo()
    {
        if (NewTodoInputBox == null) return;
        string text = NewTodoInputBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _todoAndWaterService.AddTodo(text);
            NewTodoInputBox.Text = string.Empty;
            if (TodoInputPlaceholder != null)
            {
                TodoInputPlaceholder.Visibility = Visibility.Visible;
            }
            UpdateTodoAndWaterUi();
            ForceActivateAndFocus(NewTodoInputBox);
        }
    }

    private void OnTodoItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            int id = fe.Tag is int tagId ? tagId : (fe.DataContext is TodoItemViewModel vm ? vm.Id : -1);
            if (id > 0)
            {
                _todoAndWaterService.ToggleTodo(id);
                e.Handled = true;
            }
        }
    }

    private void OnTodoTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is int id)
        {
            _todoAndWaterService.UpdateTodoTitle(id, tb.Text);
        }
    }

    private void OnTodoTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            if (tb.Tag is int id)
            {
                _todoAndWaterService.UpdateTodoTitle(id, tb.Text);
            }
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnTodoDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            int id = fe.Tag is int tagId ? tagId : (fe.DataContext is TodoItemViewModel vm ? vm.Id : -1);
            if (id > 0)
            {
                _todoAndWaterService.DeleteTodo(id);
                e.Handled = true;
            }
        }
    }

    private void OnClearCompletedTodoClick(object sender, RoutedEventArgs e)
    {
        var completed = _todoAndWaterService.Todos.Where(t => t.IsDone).ToList();
        foreach (var item in completed)
        {
            _todoAndWaterService.DeleteTodo(item.Id);
        }
    }

    private void OnManualTuckClick(object sender, RoutedEventArgs e)
    {
        _tuckCooldownTicks = 25; // ~500ms cooldown so it doesn't immediately re-open on the same cursor position
        _topHandleHoverTicks = 0;
        _collapseDebounceTimer.Stop();
        _autoTuckTimer.Stop();
        _stateMachine.SetHovered(false);
        _stateMachine.SetTuckedMode(true);
        Keyboard.ClearFocus();
    }

    private void OnToggleAutoTuckClick(object sender, RoutedEventArgs e)
    {
        _settingsService.AutoTuckEnabled = !_settingsService.AutoTuckEnabled;
        _stateMachine.SetAutoTuckEnabled(_settingsService.AutoTuckEnabled);
        if (_settingsService.AutoTuckEnabled)
        {
            _stateMachine.SetAutoTucked(false);
            _autoTuckTimer.Stop();
            _autoTuckTimer.Interval = TimeSpan.FromSeconds(10);
            _autoTuckTimer.Start();
        }
        else
        {
            _autoTuckTimer.Stop();
            _stateMachine.SetAutoTucked(false);
        }
        UpdateSlidersAndMuteUi();
    }

    private void OnPomodoroStartPauseClick(object sender, RoutedEventArgs e)
    {
        _pomodoroService.ToggleStartPause();
        if (_pomodoroService.IsRunning)
        {
            _autoTuckTimer.Stop();
        }
        else
        {
            if (_settingsService.AutoTuckEnabled && !_stateMachine.IsTuckedMode)
            {
                _stateMachine.SetAutoTucked(false);
                _autoTuckTimer.Stop();
                _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
                _autoTuckTimer.Start();
            }
        }
    }
    private void OnPomodoroResetFocusClick(object sender, RoutedEventArgs e)
    {
        if (_pomodoroService.IsStopwatchMode)
        {
            _pomodoroService.Reset();
        }
        else
        {
            _pomodoroService.Reset(_settingsService.DefaultFocusMinutes, isBreak: false);
        }
    }

    private void OnPomodoroBreakClick(object sender, RoutedEventArgs e) => _pomodoroService.Reset(_settingsService.DefaultBreakMinutes, isBreak: true);
    private void OnPomodoroAdd5MinClick(object sender, RoutedEventArgs e) => _pomodoroService.AddMinutes(5);
    private void OnPomoSwitchTimerClick(object sender, RoutedEventArgs e) => _pomodoroService.SetMode(false);
    private void OnPomoSwitchStopwatchClick(object sender, RoutedEventArgs e) => _pomodoroService.SetMode(true);
    private void OnPomodoroLapClick(object sender, RoutedEventArgs e) => _pomodoroService.RecordLap();

    private void SwitchToClipboardTab()
    {
        CloseSpotlight();
        _stateMachine.SetTuckedMode(false);
        if (!_stateMachine.IsPinnedExpanded)
        {
            _stateMachine.TogglePinnedExpanded();
        }
        SwitchToTab(IslandDashboardTab.FileShelf);
        OnShelfModeClipboardClick(this, new RoutedEventArgs());
    }


    private void OnTabCalendarClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.CalendarSchedule);
    private void OnTabWeatherClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.Weather);
    private void OnTabNotifClick(object sender, RoutedEventArgs e) => SwitchToTab(IslandDashboardTab.NotificationHistory);

    private void OnDotCalClick(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.CalendarSchedule);
    private void OnDotWeatherClick(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.Weather);
    private void OnDotNotifClick(object sender, MouseButtonEventArgs e) => SwitchToTab(IslandDashboardTab.NotificationHistory);

    private void OnSystemSleepClick(object sender, RoutedEventArgs e) => SystemPowerService.Sleep();
    private void OnSystemHibernateClick(object sender, RoutedEventArgs e) => SystemPowerService.Hibernate();

    private void OnWeatherRefreshClick(object sender, RoutedEventArgs e) => _ = _weatherService.GetWeatherAsync(forceRefresh: true);
    private void OnClearNotifHistoryClick(object sender, RoutedEventArgs e) => _notificationHistoryService.Clear();

    private void OnQuickAddScheduleClick(object sender, RoutedEventArgs e)
    {
        if (ScheduleAddInputPanel == null) return;
        ScheduleAddInputPanel.Visibility = ScheduleAddInputPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (ScheduleAddInputPanel.Visibility == Visibility.Visible)
        {
            ScheduleInputTitleText.Focus();
        }
    }

    private void OnSaveScheduleItemClick(object sender, RoutedEventArgs e)
    {
        SaveCurrentScheduleInput();
    }

    private void OnScheduleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveCurrentScheduleInput();
        }
        else if (e.Key == Key.Escape)
        {
            if (ScheduleAddInputPanel != null) ScheduleAddInputPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveCurrentScheduleInput()
    {
        if (ScheduleInputTitleText == null || ScheduleInputTimeText == null) return;
        string title = ScheduleInputTitleText.Text.Trim();
        string time = ScheduleInputTimeText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            _calendarService.AddScheduleItem(title, time, "Kuliah");
            ScheduleInputTitleText.Text = string.Empty;
            if (ScheduleAddInputPanel != null) ScheduleAddInputPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string MapToastIconToGeom(string icon) => (icon ?? "").ToUpperInvariant() switch
    {
        "BELL" or "NOTIF" => "GeomBellNotif",
        "WIFI" => "GeomWifi",
        "BLUETOOTH" => "GeomBluetooth",
        "BATTERY" or "BATTERY_HEALTH" => "GeomBatteryHealth",
        "BOLT" => "GeomBolt",
        "CLIPBOARD" => "GeomClipboard",
        "MIC" or "MIC_MUTED" => "GeomMicOn",
        "VOLUME" => "GeomSpeakerOn",
        _ => "GeomBellNotif"
    };

    private void RefreshCalendarMonthGrid()
    {
        if (CalendarDaysMatrix == null || CalendarMonthTitleText == null) return;

        var loc = LocalizationService.Instance;
        var calCulture = loc.IsEnglish ? new System.Globalization.CultureInfo("en-US") : new System.Globalization.CultureInfo("id-ID");
        var now = DateTime.Now;
        CalendarMonthTitleText.Text = now.ToString("MMMM yyyy", calCulture);

        if (CalendarDayHeadersGrid != null)
        {
            CalendarDayHeadersGrid.Children.Clear();
            string[] dayLetters = loc.IsEnglish
                ? new[] { "S", "M", "T", "W", "T", "F", "S" }
                : new[] { "M", "S", "S", "R", "K", "J", "S" };
            foreach (var d in dayLetters)
            {
                CalendarDayHeadersGrid.Children.Add(new TextBlock
                {
                    Text = d,
                    Foreground = BrushCache.Get(Color.FromRgb(0x64, 0x74, 0x8B)),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
        }

        CalendarDaysMatrix.Children.Clear();
        var cells = _calendarService.GenerateCurrentMonthGrid();

        foreach (var cell in cells)
        {
            var border = new Border
            {
                Width = 26,
                Height = 22,
                CornerRadius = new CornerRadius(cell.IsToday ? 7 : 4),
                Margin = new Thickness(1),
                Background = cell.IsToday
                    ? BrushCache.Get(Color.FromRgb(0x38, 0xBD, 0xF8))
                    : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var tb = new TextBlock
            {
                Text = cell.DayNumber.ToString(),
                FontSize = 10,
                FontWeight = cell.IsToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = cell.IsToday
                    ? Brushes.Black
                    : (cell.IsCurrentMonth
                        ? BrushCache.Get(Color.FromRgb(0xF8, 0xFA, 0xFC))
                        : BrushCache.Get(Color.FromRgb(0x47, 0x55, 0x69))),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = tb;
            CalendarDaysMatrix.Children.Add(border);
        }
    }

    private void RefreshScheduleUi()
    {
        if (ScheduleItemsStack == null) return;
        ScheduleItemsStack.Children.Clear();

        var loc = LocalizationService.Instance;

        if (_calendarService.ScheduleItems.Count == 0)
        {
            ScheduleItemsStack.Children.Add(new TextBlock
            {
                Text = loc.IsEnglish ? "No agenda for today yet. Click '+ Add' to create a schedule." : "Belum ada agenda hari ini. Klik '+ Tambah' untuk membuat jadwal.",
                Foreground = BrushCache.Get(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 10.5,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var item in _calendarService.ScheduleItems)
        {
            var card = new Border
            {
                Background = BrushCache.Get(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = BrushCache.Get(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var topRow = new StackPanel { Orientation = Orientation.Horizontal };

            var catBorder = new Border
            {
                Background = BrushCache.Get(Color.FromArgb(0x22, 0x38, 0xBD, 0xF8)),
                BorderBrush = BrushCache.Get(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 6, 0)
            };
            catBorder.Child = new TextBlock
            {
                Text = item.Category,
                Foreground = BrushCache.Get(Color.FromRgb(0x38, 0xBD, 0xF8)),
                FontSize = 9,
                FontWeight = FontWeights.Bold
            };
            topRow.Children.Add(catBorder);

            topRow.Children.Add(new TextBlock
            {
                Text = item.Time,
                Foreground = BrushCache.Get(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            leftStack.Children.Add(topRow);

            var titleTb = new TextBlock
            {
                Text = item.Title,
                Foreground = item.IsCompleted
                    ? BrushCache.Get(Color.FromRgb(0x64, 0x74, 0x8B))
                    : BrushCache.Get(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (item.IsCompleted) titleTb.TextDecorations = TextDecorations.Strikethrough;
            leftStack.Children.Add(titleTb);

            grid.Children.Add(leftStack);
            Grid.SetColumn(leftStack, 0);

            var actionsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var checkBtn = new Button
            {
                Style = TryFindResource("IslandIconButtonStyle") as Style,
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = item.IsCompleted ? "Tandai Belum Selesai" : "Tandai Selesai"
            };
            checkBtn.Click += (_, _) => _calendarService.ToggleScheduleItem(item.Id);
            checkBtn.Content = new Path
            {
                Data = TryFindResource(item.IsCompleted ? "GeomCheckCircle" : "GeomCircleOutline") as Geometry,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                Fill = item.IsCompleted
                    ? BrushCache.Get(Color.FromRgb(0x22, 0xC5, 0x5E))
                    : BrushCache.Get(Color.FromRgb(0x94, 0xA3, 0xB8))
            };
            actionsStack.Children.Add(checkBtn);

            var delBtn = new Button
            {
                Style = TryFindResource("IslandIconButtonStyle") as Style,
                Padding = new Thickness(5, 3, 5, 3),
                ToolTip = "Hapus Jadwal"
            };
            delBtn.Click += (_, _) => _calendarService.DeleteScheduleItem(item.Id);
            delBtn.Content = new Path
            {
                Data = TryFindResource("GeomTrash") as Geometry,
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                Fill = BrushCache.Get(Color.FromRgb(0xFB, 0x71, 0x85))
            };
            actionsStack.Children.Add(delBtn);

            grid.Children.Add(actionsStack);
            Grid.SetColumn(actionsStack, 1);

            card.Child = grid;
            ScheduleItemsStack.Children.Add(card);
        }
    }

    private async void RefreshWeatherUi()
    {
        var w = await _weatherService.GetWeatherAsync();
        if (w == null || WeatherTempLargeText == null) return;

        WeatherTempLargeText.Text = $"{Math.Round(w.TempC)}°";
        WeatherConditionText.Text = w.ConditionText;
        WeatherCityText.Text = $"{w.City}, Indonesia";
        var loc = LocalizationService.Instance;
        WeatherFeelsLikeText.Text = loc.IsEnglish
            ? $"Feels like: {Math.Round(w.ApparentTempC)}°C"
            : $"Terasa: {Math.Round(w.ApparentTempC)}°C";
        WeatherStatsDetailText.Text = loc.IsEnglish
            ? $"Humidity: {w.Humidity}% • Wind: {w.WindSpeedKmh} km/h"
            : $"Kelembapan: {w.Humidity}% • Angin: {w.WindSpeedKmh} km/j";
        WeatherMainIconPath.Data = TryFindResource(w.IconKey) as Geometry ?? (TryFindResource("GeomWeatherSunCloud") as Geometry);

        WeatherHourlyUniformGrid.Children.Clear();
        foreach (var h in w.HourlyForecast)
        {
            var hCard = new Border
            {
                Background = BrushCache.Get(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                BorderBrush = BrushCache.Get(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4, 5, 4, 5),
                Margin = new Thickness(3, 0, 3, 0)
            };

            var hStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            hStack.Children.Add(new TextBlock
            {
                Text = h.HourText,
                Foreground = BrushCache.Get(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 9.5,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var hIcon = new Path
            {
                Data = TryFindResource(h.IconKey) as Geometry ?? (TryFindResource("GeomWeatherSunCloud") as Geometry),
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                Fill = BrushCache.Get(Color.FromRgb(0x38, 0xBD, 0xF8)),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            hStack.Children.Add(hIcon);

            hStack.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(h.TempC)}°",
                Foreground = BrushCache.Get(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            hCard.Child = hStack;
            WeatherHourlyUniformGrid.Children.Add(hCard);
        }
    }

    private void RefreshNotificationHistoryUi()
    {
        if (NotificationHistoryStack == null || NotifCountBadgeText == null || EmptyNotifHistoryPanel == null) return;

        var loc = LocalizationService.Instance;
        int count = _notificationHistoryService.Items.Count;
        NotifCountBadgeText.Text = loc.IsEnglish ? $"{count} This Session" : $"{count} Sesi Ini";
        NotificationHistoryStack.Children.Clear();

        if (count == 0)
        {
            EmptyNotifHistoryPanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyNotifHistoryPanel.Visibility = Visibility.Collapsed;

        foreach (var it in _notificationHistoryService.Items)
        {
            var card = new Border
            {
                Background = BrushCache.Get(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = BrushCache.Get(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBorder = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                Background = BrushCache.Get(Color.FromArgb(0x20, 0x38, 0xBD, 0xF8)),
                BorderBrush = BrushCache.Get(Color.FromArgb(0x35, 0x38, 0xBD, 0xF8)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            iconBorder.Child = new Path
            {
                Data = TryFindResource(it.IconKey) as Geometry ?? (TryFindResource("GeomBellNotif") as Geometry),
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Fill = BrushCache.Get(Color.FromRgb(0x38, 0xBD, 0xF8)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(iconBorder);
            Grid.SetColumn(iconBorder, 0);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            headerRow.Children.Add(new TextBlock
            {
                Text = it.AppName,
                Foreground = BrushCache.Get(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                FontSize = 11,
                FontWeight = FontWeights.Bold
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = $" • {it.RelativeTime}",
                Foreground = BrushCache.Get(Color.FromRgb(0x64, 0x74, 0x8B)),
                FontSize = 9.5,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            textStack.Children.Add(headerRow);

            if (!string.IsNullOrWhiteSpace(it.Message))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = it.Message,
                    Foreground = BrushCache.Get(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                    LineHeight = 13.5
                });
            }

            grid.Children.Add(textStack);
            Grid.SetColumn(textStack, 1);

            var dismissBtn = new Button
            {
                Style = TryFindResource("IslandIconButtonStyle") as Style,
                Padding = new Thickness(5, 3, 5, 3),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Hapus Notifikasi"
            };
            dismissBtn.Click += (_, _) => _notificationHistoryService.Remove(it);
            dismissBtn.Content = new Path
            {
                Data = TryFindResource("GeomClosePower") as Geometry,
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                Fill = BrushCache.Get(Color.FromRgb(0x64, 0x74, 0x8B))
            };
            grid.Children.Add(dismissBtn);
            Grid.SetColumn(dismissBtn, 2);

            card.Child = grid;
            NotificationHistoryStack.Children.Add(card);
        }
    }

    // =========================================================================
    // FEATURE 3: 1-CLICK AUDIO OUTPUT SWITCHER
    // =========================================================================

    private void OnShelfQrShareClick(object sender, RoutedEventArgs e)
    {
        if (_fileShelfService.Items.Count == 0)
        {
            TriggerToast(new ToastPayload(
                Icon: "FOLDER",
                Title: LocalizationService.Instance.Get("Qr_Title", "Local Wi-Fi Share"),
                Subtitle: LocalizationService.Instance.Get("Shelf_DropHint", "Drop files first before sharing"),
                AccentHex: "#FBBF24",
                Duration: TimeSpan.FromSeconds(2.2)));
            return;
        }

        if (_isShelfClipboardMode)
        {
            OnShelfModeFilesClick(sender, e);
        }

        if (ShelfQrOverlayBorder.Visibility == Visibility.Visible && _fileShelfShareService.IsRunning)
        {
            OnStopShelfQrShareClick(sender, e);
            return;
        }

        string? url = _fileShelfShareService.StartSharing(_fileShelfService.Items);
        if (!string.IsNullOrWhiteSpace(url))
        {
            ShelfQrVectorPath.Data = QrCodeGenerator.BuildVectorGeometry(url, moduleSize: 4.0);
            ShelfQrUrlText.Text = url;
            ShelfQrOverlayBorder.Visibility = Visibility.Visible;
        }
    }

    private void OnStopShelfQrShareClick(object sender, RoutedEventArgs e)
    {
        _fileShelfShareService.StopSharing();
        ShelfQrOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private void OnLaunchOcrToolClick(object sender, RoutedEventArgs e) => LaunchOcrSnipTool();
    private void OnLaunchColorPickerToolClick(object sender, RoutedEventArgs e) => LaunchColorPickerTool();

    private void LaunchOcrSnipTool()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                CloseSpotlight();
                bool prevHidden = _stateMachine.IsFullScreenHidden;
                Opacity = 0.0;
                await Task.Delay(120);

                var frame = ScreenCaptureToolsService.CaptureVirtualScreen();
                Opacity = 1.0;

                if (frame != null)
                {
                    var overlay = new ScreenSnipOverlayWindow(frame, ScreenToolMode.OcrSnip);
                    overlay.ShowDialog();

                    if (overlay.Completed && !string.IsNullOrWhiteSpace(overlay.ExtractedOcrText))
                    {
                        Clipboard.SetText(overlay.ExtractedOcrText);
                        string preview = overlay.ExtractedOcrText.Length > 80
                            ? overlay.ExtractedOcrText[..77] + "..."
                            : overlay.ExtractedOcrText;
                        TriggerToast(new ToastPayload(
                            Icon: "SHIELD",
                            Title: LocalizationService.Instance.Get("Ocr_CopySuccess", "Screen Text Copied (OCR)"),
                            Subtitle: preview,
                            AccentHex: "#38BDF8",
                            Duration: TimeSpan.FromSeconds(2.8)));
                    }
                    else if (overlay.Completed)
                    {
                        TriggerToast(new ToastPayload(
                            Icon: "SHIELD",
                            Title: "OCR",
                            Subtitle: LocalizationService.Instance.Get("Ocr_NoTextFound", "No readable text found"),
                            AccentHex: "#FBBF24",
                            Duration: TimeSpan.FromSeconds(2.2)));
                    }
                }
            }
            catch (Exception ex)
            {
                Opacity = 1.0;
                AppLogger.LogError("OcrTool", "Failed launching OCR snip tool", ex);
            }
        });
    }

    private void LaunchColorPickerTool()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                CloseSpotlight();
                Opacity = 0.0;
                await Task.Delay(120);

                var frame = ScreenCaptureToolsService.CaptureVirtualScreen();
                Opacity = 1.0;

                if (frame != null)
                {
                    var overlay = new ScreenSnipOverlayWindow(frame, ScreenToolMode.ColorPicker);
                    overlay.ShowDialog();

                    if (overlay.Completed && !string.IsNullOrWhiteSpace(overlay.PickedColorHex))
                    {
                        Clipboard.SetText(overlay.PickedColorHex);
                        TriggerToast(new ToastPayload(
                            Icon: "SUN",
                            Title: LocalizationService.Instance.Format("Picker_CopySuccess", overlay.PickedColorHex),
                            Subtitle: overlay.PickedColorHex,
                            AccentHex: overlay.PickedColorHex,
                            Duration: TimeSpan.FromSeconds(2.6)));
                    }
                }
            }
            catch (Exception ex)
            {
                Opacity = 1.0;
                AppLogger.LogError("ColorPicker", "Failed launching eyedropper tool", ex);
            }
        });
    }
}
