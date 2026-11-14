using System.Windows.Media;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Services;
using WindowsDynamicIsland.UI;

namespace WindowsDynamicIsland.Tests;

public sealed class IslandCoreTests
{
    [Fact]
    public void StateMachine_PriorityPrecedence_WorksAsDesigned()
    {
        var sm = new IslandStateMachine();
        Assert.Equal(IslandVisualState.IdlePill, sm.CurrentState);

        sm.SetMediaActive(true);
        Assert.Equal(IslandVisualState.CompactMedia, sm.CurrentState);

        var toast = new ToastPayload("VOL", "Volume", "75%");
        sm.ShowToast(toast);
        Assert.Equal(IslandVisualState.ToastNotification, sm.CurrentState);

        sm.SetSpotlightActive(true);
        Assert.Equal(IslandVisualState.SpotlightSearch, sm.CurrentState);

        sm.SetFullScreenHidden(true);
        Assert.Equal(IslandVisualState.Hidden, sm.CurrentState);

        sm.SetFullScreenHidden(false);
        sm.SetSpotlightActive(false);
        sm.ClearToast();
        Assert.Equal(IslandVisualState.CompactMedia, sm.CurrentState);

        sm.SetTuckedMode(true);
        Assert.Equal(IslandVisualState.TuckedTopEdge, sm.CurrentState);
    }

    [Fact]
    public void StateMachine_PinnedPerformance_OverridesAutoTuck()
    {
        var sm = new IslandStateMachine();
        sm.SetAutoTucked(true);
        Assert.Equal(IslandVisualState.TuckedTopEdge, sm.CurrentState);

        sm.SetPerformancePinned(true);
        Assert.Equal(IslandVisualState.PinnedPerformancePill, sm.CurrentState);

        sm.SetPerformancePinned(false);
        Assert.Equal(IslandVisualState.IdlePill, sm.CurrentState);
    }

    [Fact]
    public void SpringValue_SettlesWithinBoundsAtTarget()
    {
        var spring = new SpringValue(100.0);
        spring.SetTarget(300.0);

        bool stillAnimating = true;
        for (int i = 0; i < 240 && stillAnimating; i++)
        {
            stillAnimating = spring.Step(1.0 / 60.0);
        }

        Assert.False(stillAnimating);
        Assert.True(spring.IsAtRest);
        Assert.Equal(300.0, spring.Current, precision: 1);
    }

    [Fact]
    public void PomodoroService_StopwatchMode_TracksElapsedAndLaps()
    {
        var pomo = new PomodoroTimerService();
        pomo.SetMode(stopwatchMode: true);
        Assert.True(pomo.IsStopwatchMode);

        pomo.ToggleStartPause();
        Assert.True(pomo.IsRunning);

        for (int i = 0; i < 65; i++)
        {
            pomo.TickOneSecond();
        }

        Assert.Equal("01:05", pomo.ActiveDisplayClock);

        pomo.RecordLap();
        Assert.Single(pomo.StopwatchLaps);
        Assert.Contains("01:05", pomo.StopwatchLaps[0]);

        pomo.Reset();
        Assert.False(pomo.IsRunning);
        Assert.Empty(pomo.StopwatchLaps);
        Assert.Equal("00:00", pomo.ActiveDisplayClock);
    }

    [Fact]
    public void BrushCache_ReturnsSameFrozenInstanceForEqualColor()
    {
        var first = BrushCache.Get(Color.FromArgb(255, 56, 189, 248));
        var second = BrushCache.Get(56, 189, 248);

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
    }

    [Fact]
    public void AppInstallerAndUpdateServices_ProvideValidPathsAndFallbackUrls()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInstallerService.DefaultInstallDir));
        Assert.Contains("WindowsDynamicIsland", AppInstallerService.DefaultInstallDir);
        Assert.EndsWith("WindowsDynamicIsland.exe", AppInstallerService.InstalledExePath);
        Assert.EndsWith("Uninstall.exe", AppInstallerService.UninstallerExePath);
        Assert.Contains("https://github.com/lepi-da-vinci/dynamic-island/releases/latest/download/", UpdateCheckerService.FallbackReleaseZipUrl);

        using var updater = new UpdateCheckerService();
        Assert.False(string.IsNullOrWhiteSpace(updater.LastResult.CurrentVersion));
    }
}
