namespace WindowsDynamicIsland.Core;

public enum IslandVisualState
{
    Hidden,
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
    public bool IsNotchDockedMode { get; set; }

    public event Action<IslandVisualState, IslandDashboardTab>? StateChanged;

    public (double Width, double Height, double CornerRadius, double GlowOpacity) GetTargetDimensions()
    {
        if (CurrentState == IslandVisualState.Hidden)
        {
            return (80, 4, 2, 0.0);
        }

        double cornerBase = IsNotchDockedMode ? 16 : 18;

        return CurrentState switch
        {
            IslandVisualState.IdlePill => (174, 34, cornerBase, 0.08),
            IslandVisualState.CompactMedia => (258, 38, cornerBase + 1, 0.35),
            IslandVisualState.ToastNotification => (296, 44, 22, 0.45),
            IslandVisualState.ExpandedDashboard => ActiveTab switch
            {
                IslandDashboardTab.MediaLyrics => (420, 198, 28, 0.65),
                IslandDashboardTab.FileShelf => (420, 186, 28, 0.45),
                IslandDashboardTab.Pomodoro => (390, 174, 28, 0.45),
                IslandDashboardTab.SystemStatus => (410, 184, 28, 0.40),
                _ => (410, 190, 28, 0.50)
            },
            _ => (174, 34, 17, 0.0)
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

    public void TogglePinnedExpanded()
    {
        IsPinnedExpanded = !IsPinnedExpanded;
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

