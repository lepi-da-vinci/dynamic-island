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
/// Win32 window subclassing, multi-monitor positioning, DPI scaling, tray icon, clipboard hook, and mouse/drag-drop interaction.
/// </summary>
public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        AppLogger.LogInfo("OnSourceInitialized", "Entering OnSourceInitialized.");
        try
        {
            base.OnSourceInitialized(e);

            _hwnd = new WindowInteropHelper(this).Handle;
            AppLogger.LogInfo("OnSourceInitialized", $"Got HWND=0x{_hwnd:X}. Adding WndProc hook...");
            HwndSource? source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProc);

            AppLogger.LogInfo("OnSourceInitialized", "Setting window styles...");
            int exStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
            exStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST | Win32Interop.WS_EX_NOACTIVATE;
            Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);

            AppLogger.LogInfo("OnSourceInitialized", "Adding clipboard format listener...");
            Win32Interop.AddClipboardFormatListener(_hwnd);

            if (_settingsService.SpotlightHotkeyEnabled)
            {
                AppLogger.LogInfo("OnSourceInitialized", "Registering Spotlight Hotkey (Alt + Spasi)...");
                Win32Interop.RegisterHotKey(_hwnd, Win32Interop.HOTKEY_ID_SPOTLIGHT, Win32Interop.MOD_ALT | Win32Interop.MOD_NOREPEAT, Win32Interop.VK_SPACE);
            }

            AppLogger.LogInfo("OnSourceInitialized", "Initializing system tray icon...");
            InitializeSystemTrayIcon();

            AppLogger.LogInfo("OnSourceInitialized", "Positioning top center...");
            PositionTopCenter();
            AppLogger.LogInfo("OnSourceInitialized", "OnSourceInitialized completed.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("OnSourceInitialized", ex);
            throw;
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", "Entering OnWindowLoaded.");
        try
        {
            PositionTopCenter();
            UpdateTabHighlight();
            _hardwareService.RefreshFullTelemetry(emitToasts: false);
            _audioService.PollExternalChanges();
            _brightnessService.PollExternalChanges();
            ApplyAppSettings();
            UpdateHardwareUi();
            UpdatePerformanceUi();
            UpdateShortcutsUi();
            UpdateSlidersAndMuteUi();
            UpdatePomodoroAndClockUi();
            UpdateTodoAndWaterUi();
            UpdatePaginationDots();
            UpdateSystemUpdateUi(_updateService.LastResult);

            _oneSecondTimer.Start();
            _fastUpdateTimer.Start();
            _hitTestTimer.Start();
            _heartbeatTimer.Start();
            if (_settingsService.AutoTuckEnabled)
            {
                _stateMachine.SetAutoTucked(false);
                _autoTuckTimer.Stop();
                _autoTuckTimer.Interval = TimeSpan.FromSeconds(_settingsService.IdleCycleSeconds);
                _autoTuckTimer.Start();
            }
            UpdateWindowClickThroughState();

            AppLogger.LogInfo("WindowLifecycle", $"MainWindow loaded successfully. HWND=0x{_hwnd:X}, Left={Left}, Top={Top}, Width={Width}, Height={Height}");

            TriggerToast(new ToastPayload(
                Icon: "BELL",
                Title: "Dynamic Island Aktif",
                Subtitle: "Scroll = Volume • Shift+Scroll = Kecerahan",
                AccentHex: "#38BDF8",
                Duration: TimeSpan.FromSeconds(2.6)));

            await _mediaService.InitializeAsync();
            EnsureRenderLoopRunning();
            AppLogger.LogInfo("WindowLifecycle", "OnWindowLoaded completed successfully.");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash("OnWindowLoaded", ex);
        }
    }

    private void PositionTopCenter()
    {
        try
        {
            var targetMonitor = Win32Interop.ResolveTargetMonitor(_settingsService.TargetMonitorMode);
            if (targetMonitor != null && _hwnd != IntPtr.Zero)
            {
                var mon = targetMonitor;
                _brightnessService.SetTargetMonitor(mon.HMonitor);

                double scaleX = mon.ScaleFactor;
                if (scaleX <= 0)
                {
                    uint nativeDpi = Win32Interop.GetDpiForWindow(_hwnd);
                    scaleX = nativeDpi > 0 ? nativeDpi / 96.0 : Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
                }
                double scaleY = scaleX;

                int monitorWidthPx = mon.BoundsPx.Right - mon.BoundsPx.Left;
                double screenWidthDip = monitorWidthPx / scaleX;
                double monitorLeftDip = mon.BoundsPx.Left / scaleX;
                double monitorTopDip = (mon.BoundsPx.Top / scaleY) + _settingsService.TopOffsetPx;

                Left = monitorLeftDip + (screenWidthDip - Width) / 2.0;
                Top = monitorTopDip;

                int windowWidthPx = (int)Math.Round(Width * scaleX);
                int windowHeightPx = (int)Math.Round(Height * scaleY);
                int pixelX = mon.BoundsPx.Left + (monitorWidthPx - windowWidthPx) / 2;
                int pixelY = mon.BoundsPx.Top + (int)Math.Round(_settingsService.TopOffsetPx * scaleY);

                Win32Interop.SetWindowPos(
                    _hwnd,
                    Win32Interop.HWND_TOPMOST,
                    pixelX,
                    pixelY,
                    windowWidthPx,
                    windowHeightPx,
                    Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

                AppLogger.LogInfo("PositionTopCenter",
                    $"Monitor-aligned ({_settingsService.TargetMonitorMode}): Left={Left:F1}, Top={Top:F1}, PixelX={pixelX}, PixelY={pixelY}, Scale={scaleX:F2}");
                return;
            }

            // Fallback if HWND not ready or monitor enumeration fails
            double fallbackWidth = SystemParameters.PrimaryScreenWidth;
            Left = (fallbackWidth - Width) / 2.0;
            Top = _settingsService.TopOffsetPx;
            AppLogger.LogInfo("PositionTopCenter", $"SystemParameters fallback: Left={Left:F1}, Top={Top:F1}, ScreenWidthDip={fallbackWidth:F1}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("PositionTopCenter", "Error positioning top center", ex);
        }
    }

    private void ScheduleDebouncedReposition()
    {
        try
        {
            _repositionCts?.Cancel();
            _repositionCts?.Dispose();
            var cts = new CancellationTokenSource();
            _repositionCts = cts;

            // 1. Reposition immediately
            PositionTopCenter();

            // 2. Delayed multi-phase reposition to catch OS video mode settling (e.g. after exiting exclusive fullscreen games)
            _ = Task.Run(async () =>
            {
                int[] delays = [150, 400, 1000, 2500];
                foreach (int delay in delays)
                {
                    if (cts.Token.IsCancellationRequested) return;
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    if (cts.Token.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!cts.Token.IsCancellationRequested)
                        {
                            PositionTopCenter();
                        }
                    });
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ScheduleDebouncedReposition", "Error scheduling reposition", ex);
        }
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        AppLogger.LogInfo("WindowLifecycle", $"DpiChanged event fired: {e.NewDpi.DpiScaleX}x{e.NewDpi.DpiScaleY} -> Scheduling reposition.");
        ScheduleDebouncedReposition();
    }

    private void InitializeSystemTrayIcon()
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                Win32Interop.ExtractIconEx(exePath, 0, out _, out hIcon, 1);
            }
        }
        catch { }

        if (hIcon == IntPtr.Zero)
        {
            try
            {
                string localIco = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                if (System.IO.File.Exists(localIco))
                {
                    Win32Interop.ExtractIconEx(localIco, 0, out _, out hIcon, 1);
                }
            }
            catch { }
        }

        if (hIcon == IntPtr.Zero)
        {
            try
            {
                string installedIco = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsDynamicIsland", "app_icon.ico");
                if (System.IO.File.Exists(installedIco))
                {
                    Win32Interop.ExtractIconEx(installedIco, 0, out _, out hIcon, 1);
                }
            }
            catch { }
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = Win32Interop.LoadIcon(IntPtr.Zero, Win32Interop.IDI_APPLICATION);
        }

        _trayIconData = new Win32Interop.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32Interop.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP,
            uCallbackMessage = Win32Interop.WM_USER_TRAY,
            hIcon = hIcon,
            szTip = "Windows Dynamic Island (Klik untuk Buka / Klik Kanan untuk Sistem)"
        };

        Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_ADD, ref _trayIconData);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_NCHITTEST)
        {
            bool isAltBypass = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool isHidden = _stateMachine.CurrentState == IslandVisualState.Hidden;
            bool isDraggingControl =
                _isDraggingTimeline ||
                _isDraggingToastSlider ||
                _isDraggingSysVolume ||
                _isDraggingSysBrightness ||
                _isDraggingAppMixer;

            if (isAltBypass || isHidden || (!IsCursorInsideIslandBounds() && !isDraggingControl))
            {
                UpdateWindowClickThroughState();
                handled = true;
                return new IntPtr(Win32Interop.HTTRANSPARENT);
            }
        }
        else if (msg == Win32Interop.WM_MOUSEACTIVATE)
        {
            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard ||
                _stateMachine.IsSpotlightActive)
            {
                handled = true;
                return new IntPtr(Win32Interop.MA_ACTIVATE);
            }
        }
        else if (msg == Win32Interop.WM_HOTKEY)
        {
            if (wParam.ToInt32() == Win32Interop.HOTKEY_ID_SPOTLIGHT)
            {
                ToggleSpotlightMode();
                handled = true;
                return IntPtr.Zero;
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
                OpenSettingsWindow(0);
            }
        }
        else if (msg == Win32Interop.WM_QUERYENDSESSION)
        {
            AppLogger.LogWarning("WndProc", $"WM_QUERYENDSESSION received (Windows logoff/shutdown in progress). wParam={wParam}, lParam={lParam}");
        }
        else if (msg == Win32Interop.WM_ENDSESSION)
        {
            AppLogger.LogWarning("WndProc", $"WM_ENDSESSION received. wParam={wParam}, lParam={lParam}");
        }
        else if (msg == Win32Interop.WM_CLOSE)
        {
            AppLogger.LogWarning("WndProc", $"WM_CLOSE received via WndProc. CallStack:\r\n{new StackTrace(true)}");
        }
        else if (msg == Win32Interop.WM_DESTROY)
        {
            AppLogger.LogInfo("WndProc", "WM_DESTROY received.");
        }
        else if (msg == Win32Interop.WM_POWERBROADCAST)
        {
            AppLogger.LogInfo("WndProc", $"WM_POWERBROADCAST received: wParam={wParam}");
        }
        else if (msg == Win32Interop.WM_DISPLAYCHANGE)
        {
            int dispW = lParam.ToInt32() & 0xFFFF;
            int dispH = (lParam.ToInt32() >> 16) & 0xFFFF;
            AppLogger.LogInfo("WndProc", $"WM_DISPLAYCHANGE received: {dispW}x{dispH} -> Scheduling reposition.");
            ScheduleDebouncedReposition();
        }
        else if (msg == Win32Interop.WM_DPICHANGED)
        {
            AppLogger.LogInfo("WndProc", "WM_DPICHANGED received -> Scheduling reposition.");
            ScheduleDebouncedReposition();
        }

        return IntPtr.Zero;
    }

    private void UpdateScreenshotModeState()
    {
        bool active = _settingsService.ScreenshotAutoTuckEnabled && Win32Interop.IsScreenshotSessionActive();
        if (active != _stateMachine.IsScreenshotMode)
        {
            if (active)
            {
                _collapseDebounceTimer.Stop();
                _autoTuckTimer.Stop();
                _toastDismissTimer.Stop();
                _topHandleHoverTicks = 0;
                _topBezelHoldTicks = 0;
            }
            _stateMachine.SetScreenshotMode(active);
            UpdateWindowClickThroughState();
        }
    }

    private (double GridDipX, double GridDipY) ScreenCursorToMasterGridDip(Win32Interop.POINT pt)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double winDipX = (pt.X / dpi.DpiScaleX) - Left;
        double winDipY = (pt.Y / dpi.DpiScaleY) - Top;
        double scale = Math.Max(0.5, _settingsService.EffectiveNotchScale);
        double winCenterX = Width / 2.0;
        double gridDipX = (MasterGridWidth / 2.0) + ((winDipX - winCenterX) / scale);
        double gridDipY = winDipY / scale;
        return (gridDipX, gridDipY);
    }

    /// <summary>
    /// Checks whether the mouse cursor is strictly inside the active Dynamic Island rectangle.
    /// Any pixel outside (including DropShadowEffect and BlurEffect areas) is treated as click-through.
    /// </summary>
    private bool IsCursorInsideIslandBounds()
    {
        // During Screenshot Mode ("Mode SS"), Dynamic Island is locked in Mode Simpan & 100% untouchable!
        if (_stateMachine.IsScreenshotMode)
            return false;

        if (_hwnd == IntPtr.Zero || !Win32Interop.GetCursorPos(out var pt))
            return false;

        var (dipX, dipY) = ScreenCursorToMasterGridDip(pt);

        double bodyWidth = Math.Max(48, _morph.Width.Current);
        double bodyHeight = Math.Max(4, _morph.Height.Current);
        double yTop = _currentPlacementTopDip;
        double centerX = MasterGridWidth / 2.0;

        // In TuckedTopEdge ("Mode Simpan"), restrict clickable zone to the micro handle
        // so browser tabs right below are 100% clickable!
        if (_stateMachine.CurrentState == IslandVisualState.TuckedTopEdge)
        {
            double maxTuckY = yTop + Math.Min(bodyHeight, 7.0);
            double tuckedLeft = (MasterGridWidth - bodyWidth) / 2.0;
            return dipX >= (tuckedLeft - 10) && dipX <= (tuckedLeft + bodyWidth + 10) && dipY >= 0 && dipY <= maxTuckY;
        }

        if (_stateMachine.CurrentState == IslandVisualState.SpotlightSearch)
        {
            double halfW = bodyWidth / 2.0;
            return dipX >= (centerX - halfW - 14) && dipX <= (centerX + halfW + 14) && dipY >= 0 && dipY <= (yTop + bodyHeight + 14);
        }

        // Generous margin tolerance around the notch so swift cursor moves never flicker
        double marginTolerance = 24.0;
        double leftEdge = centerX - (bodyWidth / 2.0) - _leftWingWidth - marginTolerance;
        double rightEdge = centerX + (bodyWidth / 2.0) + _rightWingWidth + marginTolerance;
        double bottomEdge = yTop + bodyHeight + marginTolerance;

        return dipX >= leftEdge && dipX <= rightEdge && dipY >= -5 && dipY <= bottomEdge;
    }

    /// <summary>
    /// Dynamically toggles WS_EX_TRANSPARENT on the Win32 layered window so that
    /// clicks outside the exact Dynamic Island body pass 100% directly to Chrome and other apps underneath.
    /// </summary>
    private void UpdateWindowClickThroughState()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool isAltBypass = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool isHidden = _stateMachine.CurrentState == IslandVisualState.Hidden;
        bool isScreenshotLocked = _stateMachine.IsScreenshotMode;
        bool isDraggingControl =
            _isDraggingTimeline ||
            _isDraggingToastSlider ||
            _isDraggingSysVolume ||
            _isDraggingSysBrightness ||
            _isDraggingAppMixer;

        bool cursorInside = IsCursorInsideIslandBounds();
        bool shouldBeClickThrough = isAltBypass || isHidden || isScreenshotLocked || (!cursorInside && !isDraggingControl);

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

            // Ensure WS_EX_NOACTIVATE is consistent with _isWindowActivatable
            if (_isWindowActivatable)
            {
                exStyle &= ~Win32Interop.WS_EX_NOACTIVATE;
            }
            else
            {
                exStyle |= Win32Interop.WS_EX_NOACTIVATE;
            }

            Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);
        }

        if (!cursorInside && !isDraggingControl && _stateMachine.IsHovered && !_collapseDebounceTimer.IsEnabled)
        {
            _collapseDebounceTimer.Start();
        }
    }

    private void SetWindowActivatable(bool activatable)
    {
        if (_hwnd == IntPtr.Zero) return;
        _isWindowActivatable = activatable;
        int exStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
        if (activatable)
        {
            exStyle &= ~Win32Interop.WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle |= Win32Interop.WS_EX_NOACTIVATE;
        }
        Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, exStyle);
        Win32Interop.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER |
            Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_NOACTIVATE);
    }

    private void ForceActivateAndFocus(TextBox? tb)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowActivatable(true);

        try
        {
            uint foregroundThreadId = Win32Interop.GetWindowThreadProcessId(Win32Interop.GetForegroundWindow(), out _);
            uint currentThreadId = Win32Interop.GetCurrentThreadId();

            if (foregroundThreadId != currentThreadId && foregroundThreadId != 0)
            {
                Win32Interop.AttachThreadInput(foregroundThreadId, currentThreadId, true);
                Win32Interop.SetForegroundWindow(_hwnd);
                Win32Interop.SetFocus(_hwnd);
                Win32Interop.AttachThreadInput(foregroundThreadId, currentThreadId, false);
            }
            else
            {
                Win32Interop.SetForegroundWindow(_hwnd);
                Win32Interop.SetFocus(_hwnd);
            }
        }
        catch { }

        Activate();

        if (tb != null)
        {
            tb.Focus();
            Keyboard.Focus(tb);
        }
    }

    private void OnClipboardUpdated()
    {
        if (_stateMachine.IsScreenshotMode) return;
        _clipboardHistoryService.RecordCurrentClipboard();
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
                        Icon: "FOLDER",
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
                        Icon: "FOLDER",
                        Title: $"{files.Count} File Disalin",
                        Subtitle: "Tip: Drag ke Island untuk simpan di Rak File",
                        AccentHex: "#38BDF8",
                        Duration: TimeSpan.FromSeconds(2.2)));
                }
            }
        }
        catch { }
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        if (_stateMachine.IsScreenshotMode)
        {
            return;
        }

        _collapseDebounceTimer.Stop();

        if (_stateMachine.CurrentState is IslandVisualState.ToastNotification)
        {
            return;
        }

        if (_stateMachine.CurrentState == IslandVisualState.TuckedTopEdge || _stateMachine.IsTuckedMode)
        {
            // When tucked, do not un-tuck on incidental MouseEnter.
            // Un-tucking from TuckedTopEdge is handled deliberately by UpdateTuckedProximityHover or clicking the top handle.
            return;
        }

        _autoTuckTimer.Stop();
        _stateMachine.SetTuckedMode(false);
        _stateMachine.SetAutoTucked(false);
        _stateMachine.SetHovered(true);
        UpdateMediaAndLyricsUi();
    }

    private void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingToastSlider)
        {
            OnToastSliderMouseMove(sender, e);
            return;
        }

        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
        {
            UpdateSideWingHover();
        }
    }

    private void OnRootCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
        {
            UpdateSideWingHover();
        }
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateSideWingHover();
        if (_stateMachine.CurrentState is IslandVisualState.ToastNotification)
        {
            _toastDismissTimer.Stop();
            _toastDismissTimer.Interval = TimeSpan.FromSeconds(2.8);
            _toastDismissTimer.Start();
            return;
        }
        _collapseDebounceTimer.Stop();
        _collapseDebounceTimer.Start();
    }

    private void UpdateSideWingHover()
    {
        if (_stateMachine.IsScreenshotMode || _stateMachine.CurrentState != IslandVisualState.ExpandedDashboard)
        {
            if (_targetLeftWingWidth != 0.0 || _targetRightWingWidth != 0.0 || _hoveredSideZone != 0 || _isBottomDockMorphed)
            {
                _targetLeftWingWidth = 0.0;
                _targetRightWingWidth = 0.0;
                _hoveredSideZone = 0;
                SetBottomDockMorphed(false);
            }
            return;
        }

        if (_hwnd == IntPtr.Zero || !Win32Interop.GetCursorPos(out var pt))
            return;

        var (dipX, dipY) = ScreenCursorToMasterGridDip(pt);
        double yTop = _currentPlacementTopDip;
        double curHeight = Math.Max(220.0, _morph.Height.Current);
        double bottomEdgeY = yTop + curHeight;
        double dxFromCenter = Math.Abs(dipX - (MasterGridWidth / 2.0));

        // Morph bottom pagination dots <-> tab logo dock smoothly based on cursor proximity
        bool shouldMorphBottomDock = _isBottomDockMorphed
            ? (dipY >= (bottomEdgeY - 48.0) && dipY <= (bottomEdgeY + 18.0) && dxFromCenter <= 225.0)
            : (dipY >= (bottomEdgeY - 38.0) && dipY <= (bottomEdgeY + 12.0) && dxFromCenter <= 165.0);
        SetBottomDockMorphed(shouldMorphBottomDock);

        if (GetEnabledTabs().Count <= 1)
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 0.0;
            _hoveredSideZone = 0;
            return;
        }

        // In 760px master grid, main notch is 540px centered: left edge = 110, right edge = 650.
        bool withinY = dipY >= yTop && dipY <= (bottomEdgeY + 16.0);

        if (!withinY)
        {
            _hoveredSideZone = 0;
        }
        else
        {
            // Avoid triggering side wings while cursor is over top status header or bottom morphing dock
            bool isProtectedHeaderOrDock = dipY <= (yTop + 46.0) || shouldMorphBottomDock;

            if (_hoveredSideZone == -1)
            {
                if (dipX > 195 || dipX < 5) _hoveredSideZone = 0;
            }
            else if (_hoveredSideZone == 1)
            {
                if (dipX < 565 || dipX > 755) _hoveredSideZone = 0;
            }
            else
            {
                if (dipX >= 5 && (dipX < 110 || (!isProtectedHeaderOrDock && dipX <= 165)))
                {
                    _hoveredSideZone = -1;
                }
                else if (dipX <= 755 && (dipX > 650 || (!isProtectedHeaderOrDock && dipX >= 595)))
                {
                    _hoveredSideZone = 1;
                }
                else
                {
                    _hoveredSideZone = 0;
                }
            }
        }

        if (_hoveredSideZone == -1)
        {
            _targetLeftWingWidth = 56.0;
            _targetRightWingWidth = 0.0;
        }
        else if (_hoveredSideZone == 1)
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 56.0;
        }
        else
        {
            _targetLeftWingWidth = 0.0;
            _targetRightWingWidth = 0.0;
        }

        UpdateSideWingVisuals();

        if (_targetLeftWingWidth > 0 || _targetRightWingWidth > 0 || _leftWingWidth > 0.01 || _rightWingWidth > 0.01)
        {
            EnsureRenderLoopRunning();
        }
    }

    private void OnBottomDockMouseEnter(object sender, MouseEventArgs e)
    {
        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard && !_stateMachine.IsScreenshotMode)
        {
            SetBottomDockMorphed(true);
        }
    }

    private void OnBottomDockMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateSideWingHover();
    }

    private void SetBottomDockMorphed(bool morphed)
    {
        if (_isBottomDockMorphed == morphed) return;
        _isBottomDockMorphed = morphed;

        BottomTabDockBorder.IsHitTestVisible = morphed;
        DotsPaginationPanel.IsHitTestVisible = !morphed;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        DotsPaginationPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(morphed ? 0.0 : 1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        DotsPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(morphed ? 0.82 : 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        DotsPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(morphed ? 0.82 : 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });

        BottomTabDockBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(morphed ? 1.0 : 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        BottomDockScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(morphed ? 1.0 : 0.86, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        BottomDockScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(morphed ? 1.0 : 0.86, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
    }

    private void UpdateTuckedProximityHover()
    {
        if (_stateMachine.IsScreenshotMode)
        {
            _topHandleHoverTicks = 0;
            return;
        }

        if (_tuckCooldownTicks > 0)
        {
            _tuckCooldownTicks--;
            return;
        }

        if (_stateMachine.CurrentState != IslandVisualState.TuckedTopEdge)
        {
            _topHandleHoverTicks = 0;
            return;
        }

        if (_hwnd == IntPtr.Zero || !Win32Interop.GetCursorPos(out var pt))
        {
            _topHandleHoverTicks = 0;
            return;
        }

        var (dipX, dipY) = ScreenCursorToMasterGridDip(pt);

        double bodyWidth = Math.Max(48, _morph.Width.Current);
        double centerX = MasterGridWidth / 2.0;
        double halfWidth = (bodyWidth / 2.0) + 16.0;
        double maxHoverY = _currentPlacementTopDip + 7.5;

        if (dipY >= 0 && dipY <= maxHoverY && Math.Abs(dipX - centerX) <= halfWidth)
        {
            _topHandleHoverTicks++;
            if (_topHandleHoverTicks >= 3) // ~60ms deliberate hover
            {
                _topHandleHoverTicks = 0;
                _collapseDebounceTimer.Stop();
                _autoTuckTimer.Stop();
                _stateMachine.SetTuckedMode(false);
                _stateMachine.SetAutoTucked(false);
                _stateMachine.SetHovered(true);
                UpdateMediaAndLyricsUi();
            }
        }
        else
        {
            _topHandleHoverTicks = 0;
        }
    }

    private void UpdateSideWingVisuals()
    {
        try
        {
            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard)
            {
                var prevTab = GetPrevTab(_stateMachine.ActiveTab);
                var (prevIcon, prevLabel) = GetTabIconAndLabel(prevTab);
                LeftWingIcon.Data = GetVectorGeometry(prevIcon);
                LeftWingLabel.Text = prevLabel;

                var nextTab = GetNextTab(_stateMachine.ActiveTab);
                var (nextIcon, nextLabel) = GetTabIconAndLabel(nextTab);
                RightWingIcon.Data = GetVectorGeometry(nextIcon);
                RightWingLabel.Text = nextLabel;
            }
        }
        catch { }
    }

    private List<IslandDashboardTab> GetEnabledTabs()
    {
        var list = new List<IslandDashboardTab>(6);
        if (_settingsService.ShowTabMedia) list.Add(IslandDashboardTab.MediaLyrics);
        if (_settingsService.ShowTabTodo) list.Add(IslandDashboardTab.TodoAndWater);
        if (_settingsService.ShowTabPomodoro) list.Add(IslandDashboardTab.Pomodoro);
        if (_settingsService.ShowTabShortcuts) list.Add(IslandDashboardTab.AppShortcuts);
        if (_settingsService.ShowTabPerformance) list.Add(IslandDashboardTab.PerformanceMonitor);
        if (_settingsService.ShowTabShelf) list.Add(IslandDashboardTab.FileShelf);
        if (list.Count == 0) list.Add(IslandDashboardTab.MediaLyrics);
        return list;
    }

    private IslandDashboardTab GetPrevTab(IslandDashboardTab tab)
    {
        var tabs = GetEnabledTabs();
        int idx = tabs.IndexOf(tab);
        if (idx < 0) return tabs[0];
        return tabs[(idx - 1 + tabs.Count) % tabs.Count];
    }

    private IslandDashboardTab GetNextTab(IslandDashboardTab tab)
    {
        var tabs = GetEnabledTabs();
        int idx = tabs.IndexOf(tab);
        if (idx < 0) return tabs[0];
        return tabs[(idx + 1) % tabs.Count];
    }

    private static (string GeometryKey, string Label) GetTabIconAndLabel(IslandDashboardTab tab)
    {
        var loc = LocalizationService.Instance;
        return tab switch
        {
            IslandDashboardTab.MediaLyrics => ("GeomMusic", loc.IsEnglish ? "Music" : "Musik"),
            IslandDashboardTab.TodoAndWater => ("GeomChecklist", loc.IsEnglish ? "To-Do" : "Tugas"),
            IslandDashboardTab.Pomodoro => ("GeomFocusTimer", loc.IsEnglish ? "Focus" : "Fokus"),
            IslandDashboardTab.AppShortcuts => ("GeomAppGrid", loc.IsEnglish ? "Shortcuts" : "Pintasan"),
            IslandDashboardTab.PerformanceMonitor => ("GeomCpuPulse", loc.IsEnglish ? "Perf" : "Performa"),
            IslandDashboardTab.FileShelf => ("GeomFolderTray", loc.IsEnglish ? "Shelf" : "Rak File"),
            IslandDashboardTab.CalendarSchedule => ("GeomCalendar", loc.IsEnglish ? "Calendar" : "Kalender"),
            IslandDashboardTab.Weather => ("GeomWeatherSunCloud", loc.IsEnglish ? "Weather" : "Cuaca"),
            IslandDashboardTab.NotificationHistory => ("GeomBellNotif", loc.IsEnglish ? "Notifs" : "Notifikasi"),
            IslandDashboardTab.SystemStatus => ("GeomSliders", loc.IsEnglish ? "System" : "Sistem"),
            _ => ("GeomMusic", loc.IsEnglish ? "Music" : "Musik")
        };
    }

    private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingToastSlider) return;

        if (_stateMachine.CurrentState == IslandVisualState.ToastNotification)
        {
            _toastDismissTimer.Stop();
            _stateMachine.ClearToast();

            if (_settingsService.AutoTuckEnabled && !_stateMachine.IsTuckedMode)
            {
                _stateMachine.SetAutoTucked(false);
                _autoTuckTimer.Stop();
                _autoTuckTimer.Interval = TimeSpan.FromSeconds(10);
                _autoTuckTimer.Start();
            }
            return;
        }

        if (_stateMachine.CurrentState != IslandVisualState.ExpandedDashboard)
        {
            _stateMachine.ClearToast();
            _autoTuckTimer.Stop();
            _stateMachine.SetTuckedMode(false);
            _stateMachine.SetAutoTucked(false);
            _stateMachine.SetHovered(true);
            UpdateMediaAndLyricsUi();
        }
    }

    private void OnIslandMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stateMachine.CurrentState is IslandVisualState.CompactMedia or IslandVisualState.IdlePill or IslandVisualState.PinnedPerformancePill)
        {
            _autoTuckTimer.Stop();
            _stateMachine.SetTuckedMode(true);
        }
        else
        {
            _explicitSlideDirection = 1;
            _stateMachine.SetActiveTab(GetNextTab(_stateMachine.ActiveTab));
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
        // Scroll wheel on island body does NOT adjust volume or brightness anymore.
        // Adjustments are preserved exclusively in the System tab sliders and hardware keys.
    }

    // --- DRAG AND DROP FILE SHELF HANDLERS ---

    private void OnIslandDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.AppShortcuts)
            {
                // Keep on AppShortcuts tab so user can drop .exe / .lnk shortcuts directly onto Pintasan!
                _collapseDebounceTimer.Stop();
                _autoTuckTimer.Stop();
            }
            else if (_settingsService.AutoOpenShelfOnDrag)
            {
                _collapseDebounceTimer.Stop();
                _autoTuckTimer.Stop();
                _stateMachine.SetTuckedMode(false);
                _stateMachine.SetAutoTucked(false);
                _stateMachine.SetActiveTab(IslandDashboardTab.FileShelf);
                _stateMachine.SetHovered(true);
            }
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
        if (_stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
            _stateMachine.ActiveTab == IslandDashboardTab.AppShortcuts &&
            e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            int addedShortcuts = 0;
            foreach (string p in droppedPaths)
            {
                if (_shortcutService.AddShortcutFromPath(p))
                {
                    addedShortcuts++;
                }
            }
            if (addedShortcuts > 0)
            {
                UpdateShortcutsUi();
                e.Handled = true;
                return;
            }
        }

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
}
