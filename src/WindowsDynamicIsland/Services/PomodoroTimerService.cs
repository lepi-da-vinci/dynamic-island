using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class PomodoroTimerService
{
    public TimeSpan TotalDuration { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public bool IsRunning { get; private set; }
    public bool IsBreakMode { get; private set; }

    public event Action? TimerTicked;
    public event Action<ToastPayload>? TimerCompleted;

    public string FormattedRemaining =>
        Remaining.TotalHours >= 1
            ? Remaining.ToString(@"h\:mm\:ss")
            : Remaining.ToString(@"mm\:ss");

    public double ProgressRatio =>
        TotalDuration.TotalSeconds > 0
            ? Math.Clamp(1.0 - (Remaining.TotalSeconds / TotalDuration.TotalSeconds), 0.0, 1.0)
            : 0.0;

    public void ToggleStartPause()
    {
        IsRunning = !IsRunning;
        TimerTicked?.Invoke();
    }

    public void Reset(int minutes = 25, bool isBreak = false)
    {
        IsRunning = false;
        IsBreakMode = isBreak;
        TotalDuration = TimeSpan.FromMinutes(minutes);
        Remaining = TotalDuration;
        TimerTicked?.Invoke();
    }

    public void AddMinutes(int deltaMinutes)
    {
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

        if (Remaining > TimeSpan.FromSeconds(1))
        {
            Remaining -= TimeSpan.FromSeconds(1);
            TimerTicked?.Invoke();
        }
        else
        {
            IsRunning = false;
            bool finishedBreak = IsBreakMode;
            Reset(finishedBreak ? 25 : 5, isBreak: !finishedBreak);

            TimerCompleted?.Invoke(new ToastPayload(
                Icon: finishedBreak ? "🔥" : "☕",
                Title: finishedBreak ? "Break Selesai! Siap Fokus" : "Sesi Fokus Selesai!",
                Subtitle: finishedBreak ? "Timer disetel ke 25m Fokus" : "Waktunya istirahat 5 menit",
                AccentHex: finishedBreak ? "#38BDF8" : "#22C55E",
                Duration: TimeSpan.FromSeconds(4.0)));
        }
    }
}

