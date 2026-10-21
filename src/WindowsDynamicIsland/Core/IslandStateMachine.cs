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
    public bool IsManuallyTucked { get; private set; }
    public bool IsAutoDodgingTopBar { get; private set; }
    public bool AutoDodgeEnabled { get; set; } = true;
    public bool IsNotchDockedMode { get; set; }

    public event Action<IslandVisualState, IslandDashboardTab>? StateChanged;

    public (double Width, double Height, double CornerRadius, double GlowOpacity, double TopOffset) GetTargetDimensions()
    {
        if (CurrentState == IslandVisualState.Hidden)
        {
            return (80, 4, 2, 0.0, 0.0);
        }

        if (CurrentState == IslandVisualState.TuckedTopEdge)
        {
            // Tucked neatly into the very top bezel (6.5px thin drawer handle at Top = 0)
            // Leaves all browser tabs and window buttons at Y = 8..45px 100% uncovered!
            return (144, 6.5, 3.25, 0.30, 0.0);
        }

        double cornerBase = IsNotchDockedMode ? 16 : 19;
        double topOffset = IsNotchDockedMode ? 0.0 : 8.0;

        return CurrentState switch
        {
            IslandVisualState.IdlePill => (176, 34, cornerBase, 0.08, topOffset),
            IslandVisualState.CompactMedia => (304, 38, cornerBase, 0.40, topOffset),
            IslandVisualState.ToastNotification => (306, 44, 22, 0.48, topOffset),
            IslandVisualState.ExpandedDashboard => ActiveTab switch
            {
                IslandDashboardTab.MediaLyrics => (434, 224, 28, 0.68, topOffset),
                IslandDashboardTab.FileShelf => (434, 204, 28, 0.48, topOffset),
                IslandDashboardTab.Pomodoro => (400, 182, 28, 0.45, topOffset),
                IslandDashboardTab.SystemStatus => (420, 192, 28, 0.42, topOffset),
                _ => (420, 200, 28, 0.50, topOffset)
            },
            _ => (176, 34, 17, 0.0, topOffset)
        };
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

    public void SetManuallyTucked(bool tucked)
    {
        IsManuallyTucked = tucked;
        if (tucked)
        {
            IsHovered = false;
            IsPinnedExpanded = false;
        }
        RecalculateState();
    }

    public void SetAutoDodgingTopBar(bool dodging)
    {
        if (IsAutoDodgingTopBar == dodging) return;
        IsAutoDodgingTopBar = dodging;
        RecalculateState();
    }

    public void TogglePinnedExpanded()
    {
        IsPinnedExpanded = !IsPinnedExpanded;
        if (IsPinnedExpanded)
        {
            IsManuallyTucked = false;
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
        else if (IsManuallyTucked || (AutoDodgeEnabled && IsAutoDodgingTopBar))
        {
            next = IslandVisualState.TuckedTopEdge;
        }
        else if (ActiveToast != null)
        {
            next = IslandVisualState.ToastNotification;
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
