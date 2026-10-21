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
    FileShelf = 1,
    Pomodoro = 2,
    SystemStatus = 3
}

public sealed record ToastPayload(
    string Icon,
    string Title,
    string Subtitle,
    string AccentHex = "#38BDF8",
    double? ProgressRatio = null,
    TimeSpan? Duration = null);

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

    // Dynamic content sizing inputs so the island breathes organically instead of staying static
    public double DynamicCompactWidth { get; private set; } = 320;
    public double DynamicIdleWidth { get; private set; } = 196;
    public int ShelfItemCount { get; private set; }

    public event Action<IslandVisualState, IslandDashboardTab>? StateChanged;

    public (double Width, double Height, double CornerRadius, double GlowOpacity, double NeckDropOffset) GetTargetDimensions()
    {
        if (CurrentState == IslandVisualState.Hidden)
        {
            return (90, 4, 2, 0.0, 0.0);
        }

        if (CurrentState == IslandVisualState.TuckedTopEdge)
        {
            // Small surface-tension water bead clinging to the very top bezel (Y = 0..10px)
            return (152, 10, 8, 0.35, 0.0);
        }

        return CurrentState switch
        {
            IslandVisualState.IdlePill => (DynamicIdleWidth, 38, 20, 0.14, 5.0),
            IslandVisualState.CompactMedia => (DynamicCompactWidth, 42, 22, 0.46, 6.0),
            IslandVisualState.ToastNotification => (346, 50, 25, 0.54, 7.0),
            IslandVisualState.ExpandedDashboard => ActiveTab switch
            {
                IslandDashboardTab.MediaLyrics => (496, 252, 32, 0.72, 10.0),
                IslandDashboardTab.FileShelf => ShelfItemCount > 3
                    ? (504, 248, 32, 0.52, 9.0)
                    : (486, 222, 30, 0.50, 9.0),
                IslandDashboardTab.Pomodoro => (462, 204, 30, 0.48, 8.0),
                IslandDashboardTab.SystemStatus => (484, 218, 30, 0.45, 8.0),
                _ => (486, 230, 30, 0.50, 9.0)
            },
            _ => (196, 38, 20, 0.10, 5.0)
        };
    }

    public void UpdateDynamicCompactWidth(int lyricCharLength)
    {
        // Dynamically scale compact pill width between 284px and 404px based on active lyric line length
        double target = Math.Clamp(250 + lyricCharLength * 4.2, 284, 404);
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
        double target = hasExtraIndicators ? 228 : 196;
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
        int next = ((int)ActiveTab + 1) % 4;
        SetActiveTab((IslandDashboardTab)next);
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
        else if (IsTuckedMode)
        {
            next = IslandVisualState.TuckedTopEdge;
        }
        else if (IsMediaActive)
        {
            next = IslandVisualState.CompactMedia;
        }
        else
        {
            next = IslandVisualState.IdlePill;
        }

        CurrentState = next;
        StateChanged?.Invoke(CurrentState, ActiveTab);
    }
}
