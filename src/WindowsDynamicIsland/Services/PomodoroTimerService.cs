using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class PomodoroTimerService
{
    public int DefaultFocusMinutes { get; set; } = 25;
    public int DefaultBreakMinutes { get; set; } = 5;
    public TimeSpan TotalDuration { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan StopwatchElapsed { get; private set; } = TimeSpan.Zero;
    public bool IsRunning { get; private set; }
    public bool IsBreakMode { get; private set; }
    public bool IsStopwatchMode { get; private set; }
    public List<string> StopwatchLaps { get; } = [];

    public event Action? TimerTicked;
    public event Action<ToastPayload>? TimerCompleted;

    public string FormattedRemaining =>
        Remaining.TotalHours >= 1
            ? Remaining.ToString(@"h\:mm\:ss")
            : Remaining.ToString(@"mm\:ss");

    public string FormattedStopwatch =>
        StopwatchElapsed.TotalHours >= 1
            ? StopwatchElapsed.ToString(@"h\:mm\:ss")
            : StopwatchElapsed.ToString(@"mm\:ss");

    public string ActiveDisplayClock => IsStopwatchMode ? FormattedStopwatch : FormattedRemaining;

    public double ProgressRatio =>
        IsStopwatchMode
            ? (StopwatchElapsed.TotalSeconds % 60.0) / 60.0
            : (TotalDuration.TotalSeconds > 0
                ? Math.Clamp(1.0 - (Remaining.TotalSeconds / TotalDuration.TotalSeconds), 0.0, 1.0)
                : 0.0);

    public void SetMode(bool stopwatchMode)
    {
        if (IsStopwatchMode == stopwatchMode) return;
        IsRunning = false;
        IsStopwatchMode = stopwatchMode;
        TimerTicked?.Invoke();
    }

    public void ToggleStartPause()
    {
        IsRunning = !IsRunning;
        TimerTicked?.Invoke();
    }

    public void StartFocus(int minutes = 25)
    {
        IsStopwatchMode = false;
        IsBreakMode = false;
        TotalDuration = TimeSpan.FromMinutes(minutes);
        Remaining = TotalDuration;
        IsRunning = true;
        TimerTicked?.Invoke();
    }

    public void Reset(int minutes = 25, bool isBreak = false)
    {
        IsRunning = false;
        if (IsStopwatchMode)
        {
            StopwatchElapsed = TimeSpan.Zero;
            StopwatchLaps.Clear();
        }
        else
        {
            IsBreakMode = isBreak;
            TotalDuration = TimeSpan.FromMinutes(minutes);
            Remaining = TotalDuration;
        }
        TimerTicked?.Invoke();
    }

    public void RecordLap()
    {
        if (!IsStopwatchMode) return;
        StopwatchLaps.Insert(0, $"#{StopwatchLaps.Count + 1:D2}  {FormattedStopwatch}");
        if (StopwatchLaps.Count > 5)
        {
            StopwatchLaps.RemoveAt(StopwatchLaps.Count - 1);
        }
        TimerTicked?.Invoke();
    }

    public void AddMinutes(int deltaMinutes)
    {
        if (IsStopwatchMode) return;
        TimeSpan next = Remaining + TimeSpan.FromMinutes(deltaMinutes);
        if (next < TimeSpan.FromMinutes(1))
        {
            next = TimeSpan.FromMinutes(1);
        }
        Remaining = next;
        if (Remaining > TotalDuration)
        {
            TotalDuration = Remaining;
        }
        TimerTicked?.Invoke();
    }

    public void TickOneSecond()
    {
        if (!IsRunning) return;

        if (IsStopwatchMode)
        {
            StopwatchElapsed += TimeSpan.FromSeconds(1);
            TimerTicked?.Invoke();
            return;
        }

        if (Remaining > TimeSpan.FromSeconds(1))
        {
            Remaining -= TimeSpan.FromSeconds(1);
            TimerTicked?.Invoke();
        }
        else
        {
            IsRunning = false;
            bool finishedBreak = IsBreakMode;
            Reset(finishedBreak ? DefaultFocusMinutes : DefaultBreakMinutes, isBreak: !finishedBreak);

            var loc = LocalizationService.Instance;
            TimerCompleted?.Invoke(new ToastPayload(
                Icon: finishedBreak ? "POMO_FOCUS" : "POMO_BREAK",
                Title: loc.Get(finishedBreak ? "Pomo_BreakDoneTitle" : "Pomo_FocusDoneTitle"),
                Subtitle: finishedBreak
                    ? loc.Get("Pomo_BreakDoneSub")
                    : loc.Format("Pomo_FocusDoneSub", DefaultBreakMinutes),
                AccentHex: finishedBreak ? "#38BDF8" : "#22C55E",
                Duration: TimeSpan.FromSeconds(4.0)));
        }
    }
}

