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

    // Dynamic content sizing inputs so the notch breathes organically
    public double DynamicCompactWidth { get; private set; } = 320;
    public double DynamicIdleWidth { get; private set; } = 196;
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
            // Flush thin NotchNook lip at Y = 0
            return (148, 9, 7, 8, 0.25);
        }

        return CurrentState switch
        {
            IslandVisualState.IdlePill => (DynamicIdleWidth, 36, 16, 13, 0.12),
            IslandVisualState.CompactMedia => (DynamicCompactWidth, 40, 18, 14, 0.35),
            IslandVisualState.ToastNotification => (348, 48, 20, 15, 0.45),
            IslandVisualState.ExpandedDashboard => ActiveTab switch
            {
                IslandDashboardTab.MediaLyrics => (504, 242, 26, 18, 0.55),
                IslandDashboardTab.FileShelf => ShelfItemCount > 3
                    ? (508, 242, 26, 18, 0.45)
                    : (492, 216, 26, 18, 0.42),
                IslandDashboardTab.Pomodoro => (468, 198, 26, 18, 0.40),
                IslandDashboardTab.SystemStatus => (488, 212, 26, 18, 0.38),
                _ => (492, 224, 26, 18, 0.45)
            },
            _ => (196, 36, 16, 13, 0.10)
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
