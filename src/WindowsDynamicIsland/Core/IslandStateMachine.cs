namespace WindowsDynamicIsland.Core;

public enum IslandVisualState
{
    Hidden,
    TuckedTopEdge,
    IdlePill,
    PinnedPerformancePill,
    CompactMedia,
    ToastNotification,
    ExpandedDashboard
}

public enum IslandDashboardTab
{
    MediaLyrics = 0,
    TodoAndWater = 1,
    Pomodoro = 2,
    AppShortcuts = 3,
    PerformanceMonitor = 4,
    FileShelf = 5,
    SystemStatus = 6
}

public enum ToastSliderKind
{
    None = 0,
    Volume = 1,
    Brightness = 2,
    Battery = 3
}

public sealed record ToastPayload(
    string Icon,
    string Title,
    string Subtitle,
    string AccentHex = "#38BDF8",
    double? ProgressRatio = null,
    TimeSpan? Duration = null,
    ToastSliderKind SliderKind = ToastSliderKind.None,
    bool IsBatteryCharging = false);

public sealed class IslandStateMachine
{
    public IslandVisualState CurrentState { get; private set; } = IslandVisualState.IdlePill;
    public IslandDashboardTab ActiveTab { get; private set; } = IslandDashboardTab.MediaLyrics;
    public ToastPayload? ActiveToast { get; private set; }

    public bool IsHovered { get; private set; }
    public bool IsPinnedExpanded { get; private set; }
    public bool IsPerformancePinned { get; private set; }
    public bool IsMediaActive { get; private set; }
    public bool IsFullScreenHidden { get; private set; }
    public bool IsScreenshotMode { get; private set; }
    public bool IsTuckedMode { get; private set; }
    public bool IsAutoTuckEnabled { get; private set; } = true;
    public bool IsAutoTucked { get; private set; } = false;
    public bool IsMinimalNotch { get; private set; } = false;

    // Dynamic content sizing inputs so the notch breathes organically
    public double DynamicCompactWidth { get; private set; } = 320;
    public double DynamicIdleWidth { get; private set; } = 268;
    public int ShelfItemCount { get; private set; }

    public string CornerStyle { get; private set; } = "Squircle";
    public string PlacementStyle { get; private set; } = "Notch";

    public event Action<IslandVisualState, IslandDashboardTab>? StateChanged;

    public void SetCornerStyle(string style)
    {
        if (string.IsNullOrWhiteSpace(style)) style = "Squircle";
        if (CornerStyle == style) return;
        CornerStyle = style;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }

    public void SetPlacementStyle(string style)
    {
        string normalized = style == "PunchHole" ? "PunchHole" : "Notch";
        if (PlacementStyle == normalized) return;
        PlacementStyle = normalized;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }

    public (double BodyWidth, double Height, double BottomCornerRadius, double TopEarRadius, double GlowOpacity) GetTargetDimensions()
    {
        bool isPunchHole = PlacementStyle == "PunchHole";

        if (CurrentState == IslandVisualState.Hidden)
        {
            return (90, 2, 2, isPunchHole ? 0 : 4, 0.0);
        }

        if (CurrentState == IslandVisualState.TuckedTopEdge)
        {
            if (isPunchHole)
            {
                // Sleek floating punch-hole micro-pill dot when tucked
                return (76, 6, 3, 0, 0.0);
            }
            // Ultra-slim 5px flush lip at Y = 0 with zero glow so Chrome tabs at Y >= 6px are completely clear
            return (118, 5, 4, 4, 0.0);
        }

        var (w, h, bRad, eRad, glow) = CurrentState switch
        {
            IslandVisualState.IdlePill => (DynamicIdleWidth, 37.0, 17.0, 13.0, 0.18),
            IslandVisualState.PinnedPerformancePill => (268.0, 37.0, 17.0, 13.0, 0.22),
            IslandVisualState.CompactMedia => (DynamicCompactWidth, 40.0, 18.0, 14.0, 0.35),
            IslandVisualState.ToastNotification => (388.0, 52.0, 22.0, 15.0, 0.48),
            IslandVisualState.ExpandedDashboard => (540.0, 268.0, 26.0, 18.0, 0.48),
            _ => (268.0, 37.0, 17.0, 13.0, 0.16)
        };

        if (isPunchHole)
        {
            eRad = 0.0;
            if (CurrentState == IslandVisualState.ExpandedDashboard)
            {
                bRad = CornerStyle == "Sharp" ? 14.0 : (CornerStyle == "UltraPill" ? 30.0 : 24.0);
            }
            else
            {
                bRad = CornerStyle == "Sharp" ? 11.0 : (h * 0.5);
            }
            return (w, h, bRad, eRad, glow);
        }

        if (CornerStyle == "UltraPill")
        {
            bRad = Math.Min(h * 0.62, bRad + 6.0);
            eRad = Math.Min(h * 0.50, eRad + 4.0);
        }
        else if (CornerStyle == "Sharp")
        {
            bRad = Math.Max(8.0, bRad * 0.52);
            eRad = Math.Max(5.0, eRad * 0.50);
        }

        return (w, h, bRad, eRad, glow);
    }

    public void UpdateDynamicCompactWidth(int lyricCharLength)
    {
        double target = Math.Clamp(254 + lyricCharLength * 4.2, 286, 408);
        if (Math.Abs(target - DynamicCompactWidth) >= 6.0)
        {
            DynamicCompactWidth = target;
            if (CurrentState == IslandVisualState.CompactMedia)
            {
                StateChanged?.Invoke(CurrentState, ActiveTab);
            }
        }
    }

    public void UpdateDynamicIdleWidth(bool hasExtraIndicators)
    {
        double target = IsMinimalNotch
            ? (hasExtraIndicators ? 218 : 186)
            : (hasExtraIndicators ? 296 : 268);
        if (Math.Abs(target - DynamicIdleWidth) > 1.0)
        {
            DynamicIdleWidth = target;
            if (CurrentState == IslandVisualState.IdlePill)
            {
                StateChanged?.Invoke(CurrentState, ActiveTab);
            }
        }
    }

    public void SetMinimalNotch(bool minimal, bool hasExtraIndicators)
    {
        if (IsMinimalNotch == minimal) return;
        IsMinimalNotch = minimal;
        UpdateDynamicIdleWidth(hasExtraIndicators);
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }

    public void UpdateShelfCount(int count)
    {
        ShelfItemCount = count;
        if (CurrentState == IslandVisualState.ExpandedDashboard && ActiveTab == IslandDashboardTab.FileShelf)
        {
            StateChanged?.Invoke(CurrentState, ActiveTab);
        }
    }

    public void SetFullScreenHidden(bool hidden)
    {
        if (IsFullScreenHidden == hidden) return;
        IsFullScreenHidden = hidden;
        RecalculateState();
    }

    public void SetScreenshotMode(bool active)
    {
        if (IsScreenshotMode == active) return;
        IsScreenshotMode = active;
        if (active)
        {
            IsHovered = false;
            IsPinnedExpanded = false;
            ActiveToast = null;
        }
        RecalculateState();
    }

    public void SetMediaActive(bool active)
    {
        if (IsMediaActive == active) return;
        IsMediaActive = active;
        RecalculateState();
    }

    public void SetHovered(bool hovered)
    {
        if (IsHovered == hovered) return;
        IsHovered = hovered;
        if (hovered)
        {
            IsTuckedMode = false;
            IsAutoTucked = false;
        }
        if (!hovered && IsPinnedExpanded)
        {
            IsPinnedExpanded = false;
        }
        RecalculateState();
    }

    public void SetTuckedMode(bool tucked)
    {
        IsTuckedMode = tucked;
        if (tucked)
        {
            IsHovered = false;
            IsPinnedExpanded = false;
            IsAutoTucked = true;
        }
        RecalculateState();
    }

    public void SetAutoTuckEnabled(bool enabled)
    {
        if (IsAutoTuckEnabled == enabled) return;
        IsAutoTuckEnabled = enabled;
        if (!enabled)
        {
            IsAutoTucked = false;
        }
        RecalculateState();
    }

    public void SetAutoTucked(bool autoTucked)
    {
        if (IsAutoTucked == autoTucked) return;
        IsAutoTucked = autoTucked;
        RecalculateState();
    }

    public void TogglePinnedExpanded()
    {
        IsPinnedExpanded = !IsPinnedExpanded;
        if (IsPinnedExpanded)
        {
            IsTuckedMode = false;
        }
        RecalculateState();
    }

    public void SetPerformancePinned(bool pinned)
    {
        if (IsPerformancePinned == pinned) return;
        IsPerformancePinned = pinned;
        if (pinned)
        {
            IsTuckedMode = false;
            IsAutoTucked = false;
        }
        RecalculateState();
    }

    public void TogglePerformancePinned() => SetPerformancePinned(!IsPerformancePinned);

    public void SetActiveTab(IslandDashboardTab tab)
    {
        ActiveTab = tab;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }

    public void CycleNextTab()
    {
        int count = Enum.GetValues<IslandDashboardTab>().Length;
        int next = ((int)ActiveTab + 1) % count;
        SetActiveTab((IslandDashboardTab)next);
    }

    public void CyclePrevTab()
    {
        int count = Enum.GetValues<IslandDashboardTab>().Length;
        int prev = ((int)ActiveTab - 1 + count) % count;
        SetActiveTab((IslandDashboardTab)prev);
    }

    public void ShowToast(ToastPayload toast)
    {
        ActiveToast = toast;
        RecalculateState();
    }

    public void ClearToast()
    {
        if (ActiveToast == null) return;
        ActiveToast = null;
        RecalculateState();
    }

    private void RecalculateState()
    {
        IslandVisualState next;

        if (IsFullScreenHidden)
        {
            next = IslandVisualState.Hidden;
        }
        else if (IsScreenshotMode)
        {
            // Screenshot mode ("Mode SS"): lock Dynamic Island in tucked top-edge strip without touching or expanding
            next = IslandVisualState.TuckedTopEdge;
        }
        else if (ActiveToast != null)
        {
            // Notifications always take precedence so incoming toasts (volume, battery, system, alerts) are visible!
            next = IslandVisualState.ToastNotification;
        }
        else if (IsTuckedMode)
        {
            // Explicit manual "Simpan": user clicked "Simpan" or right-clicked notch, MUST tuck immediately even if media is active!
            next = IslandVisualState.TuckedTopEdge;
        }
        else if (IsHovered || IsPinnedExpanded)
        {
            next = IslandVisualState.ExpandedDashboard;
        }
        else if (IsPerformancePinned)
        {
            // When performance monitor is pinned ("fitur pin jadi performanya bisa stay"),
            // stay in the compact IdlePill-sized performance view without auto-tucking!
            next = IslandVisualState.PinnedPerformancePill;
        }
        else if (IsMediaActive)
        {
            // When media is active (music/video playing) and NOT manually tucked, show CompactMedia as usual
            next = IslandVisualState.CompactMedia;
        }
        else if (IsAutoTuckEnabled && IsAutoTucked)
        {
            // Automatically tucked after 10 seconds of inactivity in default view!
            next = IslandVisualState.TuckedTopEdge;
        }
        else
        {
            // Default normal view (tampilan awal): Date, Clock, Battery, Wifi, TrueDepth sensor
            next = IslandVisualState.IdlePill;
        }

        CurrentState = next;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }
}
