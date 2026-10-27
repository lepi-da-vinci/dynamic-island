namespace WindowsDynamicIsland.Core;

public enum IslandVisualState
{
    Hidden,
    TuckedTopEdge,
    IdlePill,
    CompactMedia,
    ToastNotification,
    ExpandedDashboard
}

public enum IslandDashboardTab
{
    MediaLyrics = 0,
    TodoAndWater = 1,
    Pomodoro = 2,
    FileShelf = 3,
    SystemStatus = 4
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
    public bool IsMediaActive { get; private set; }
    public bool IsFullScreenHidden { get; private set; }
    public bool IsTuckedMode { get; private set; }
    public bool IsAutoTuckEnabled { get; private set; } = true;
    public bool IsIdleCycleTucked { get; private set; } = false;

    // Dynamic content sizing inputs so the notch breathes organically
    public double DynamicCompactWidth { get; private set; } = 320;
    public double DynamicIdleWidth { get; private set; } = 268;
    public int ShelfItemCount { get; private set; }

    public event Action<IslandVisualState, IslandDashboardTab>? StateChanged;

    public (double BodyWidth, double Height, double BottomCornerRadius, double TopEarRadius, double GlowOpacity) GetTargetDimensions()
    {
        if (CurrentState == IslandVisualState.Hidden)
        {
            return (90, 2, 2, 4, 0.0);
        }

        if (CurrentState == IslandVisualState.TuckedTopEdge)
        {
            // Ultra-slim 5px flush lip at Y = 0 with zero glow so Chrome tabs at Y >= 6px are completely clear
            return (118, 5, 4, 4, 0.0);
        }

        return CurrentState switch
        {
            IslandVisualState.IdlePill => (DynamicIdleWidth, 37, 17, 13, 0.18),
            IslandVisualState.CompactMedia => (DynamicCompactWidth, 40, 18, 14, 0.35),
            IslandVisualState.ToastNotification => (388, 52, 22, 15, 0.48),
            IslandVisualState.ExpandedDashboard => (540, 260, 26, 18, 0.48),
            _ => (268, 37, 17, 13, 0.16)
        };
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
        double target = hasExtraIndicators ? 296 : 268;
        if (Math.Abs(target - DynamicIdleWidth) > 1.0)
        {
            DynamicIdleWidth = target;
            if (CurrentState == IslandVisualState.IdlePill)
            {
                StateChanged?.Invoke(CurrentState, ActiveTab);
            }
        }
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
        }
        RecalculateState();
    }

    public void SetAutoTuckEnabled(bool enabled)
    {
        if (IsAutoTuckEnabled == enabled) return;
        IsAutoTuckEnabled = enabled;
        if (!enabled)
        {
            IsIdleCycleTucked = false;
        }
        RecalculateState();
    }

    public void ToggleIdleCycleTuck()
    {
        IsIdleCycleTucked = !IsIdleCycleTucked;
        IsTuckedMode = false;
        RecalculateState();
    }

    public void ResetIdleCycle()
    {
        IsIdleCycleTucked = false;
        IsTuckedMode = false;
        RecalculateState();
    }

    public void SetIdleCycleTucked(bool tucked)
    {
        IsIdleCycleTucked = tucked;
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
        else if (IsHovered || IsPinnedExpanded)
        {
            next = IslandVisualState.ExpandedDashboard;
        }
        else if (ActiveToast != null)
        {
            next = IslandVisualState.ToastNotification;
        }
        else if (IsMediaActive)
        {
            // When media is active (music playing), always show CompactMedia as usual
            next = IslandVisualState.CompactMedia;
        }
        else if (IsTuckedMode)
        {
            // Explicit manual "Simpan"
            next = IslandVisualState.TuckedTopEdge;
        }
        else if (IsAutoTuckEnabled && IsIdleCycleTucked)
        {
            // Periodic 10s auto-tuck phase during idle
            next = IslandVisualState.TuckedTopEdge;
        }
        else
        {
            // Default view (tampilan awal): Date, Clock, Battery, Wifi, TrueDepth sensor
            next = IslandVisualState.IdlePill;
        }

        CurrentState = next;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }
}
