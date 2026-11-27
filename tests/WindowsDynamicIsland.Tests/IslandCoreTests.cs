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

    [Fact]
    public void WeatherService_ConvertTemperature_ConvertsCelsiusToFahrenheit()
    {
        Assert.Equal(32.0, WeatherService.ConvertTemperature(0.0, useFahrenheit: true), precision: 1);
        Assert.Equal(212.0, WeatherService.ConvertTemperature(100.0, useFahrenheit: true), precision: 1);
        Assert.Equal(28.5, WeatherService.ConvertTemperature(28.5, useFahrenheit: false), precision: 1);
    }

    [Fact]
    public void CalendarScheduleService_GeneratesEventDotsAndUpcomingReminders()
    {
        var cal = new CalendarScheduleService();
        var targetDate = DateOnly.FromDateTime(DateTime.Today);
        cal.AddScheduleItem("Test Agenda Unit", "14:30", "Proyek", targetDate);

        var grid = cal.GenerateMonthGrid(targetDate.Year, targetDate.Month, targetDate);
        Assert.True(grid.Count >= 28);
        Assert.Contains(grid, c => c.Date == targetDate && c.IsSelected && c.HasEvents);

        var fired = new HashSet<string>(StringComparer.Ordinal);
        var check5m = cal.CheckUpcomingReminders(DateTime.Today.AddHours(14).AddMinutes(25), fired);
        Assert.Contains(check5m, r => r.Item.Title == "Test Agenda Unit" && r.MinutesUntilStart == 5);

        // Must not fire duplicate reminder for the same key
        Assert.Empty(cal.CheckUpcomingReminders(DateTime.Today.AddHours(14).AddMinutes(25), fired));

        var added = cal.ScheduleItems.First(s => s.Title == "Test Agenda Unit");
        cal.DeleteScheduleItem(added.Id);
    }

    [Fact]
    public void AppSettingsService_NormalizedTabOrder_AlwaysContainsAllTenTabs()
    {
        var settings = new AppSettingsService();
        var originalOrder = settings.GetNormalizedTabOrder();
        Assert.Equal(10, originalOrder.Count);
        Assert.Contains("Wifi", originalOrder);

        settings.SetDashboardTabOrder(["Weather", "MediaLyrics", "Weather"]);
        var updated = settings.GetNormalizedTabOrder();
        Assert.Equal(10, updated.Count);
        Assert.Equal("Weather", updated[0]);
        Assert.Equal("MediaLyrics", updated[1]);
        Assert.Equal(10, updated.Distinct(StringComparer.Ordinal).Count());

        // Restore original order
        settings.SetDashboardTabOrder(originalOrder);
    }

    [Fact]
    public void WifiService_ParsesInterfacesProfilesAndNetworksCorrectly()
    {
        const string ifaceSample = """
            Name                   : Wi-Fi
            State                  : connected
            SSID                   : hotspot@unama
            Network type           : Infrastructure
            Radio type             : 802.11ax
            Authentication         : WPA2-Personal
            Signal                 : 88%
            """;

        var iface = WifiService.ParseNetshInterfaceOutput(ifaceSample);
        Assert.Equal("hotspot@unama", iface.Ssid);
        Assert.Equal(88, iface.SignalPercent);
        Assert.Equal("WPA2-Personal", iface.Security);
        Assert.Equal("Wi-Fi 6", iface.Band);

        const string profilesSample = """
            Profiles on interface Wi-Fi:
                All User Profile     : hotspot@unama
                All User Profile     : HomeFiber_5G
            """;
        var profiles = WifiService.ParseNetshProfilesOutput(profilesSample);
        Assert.Contains("hotspot@unama", profiles);
        Assert.Contains("HomeFiber_5G", profiles);

        const string netsSample = """
            SSID 1 : GuestOpen
                Network type            : Infrastructure
                Authentication          : Open
                Encryption              : None
                BSSID 1                 : aa:bb:cc:dd:ee:01
                     Signal             : 45%
                     Radio type         : 802.11ac

            SSID 2 : HomeFiber_5G
                Network type            : Infrastructure
                Authentication          : WPA2-Personal
                Encryption              : CCMP
                BSSID 1                 : aa:bb:cc:dd:ee:02
                     Signal             : 92%
                     Radio type         : 802.11ax

            SSID 3 : hotspot@unama
                Authentication          : WPA2-Personal
                BSSID 1                 : aa:bb:cc:dd:ee:03
                     Signal             : 86%
            """;

        var list = WifiService.ParseNetshNetworksOutput(netsSample, "hotspot@unama", profiles);
        Assert.Equal(3, list.Count);
        // Connected network comes first, then saved profile, then unsaved
        Assert.Equal("hotspot@unama", list[0].Ssid);
        Assert.True(list[0].IsConnected);
        Assert.True(list[0].IsSavedProfile);
        Assert.Equal("HomeFiber_5G", list[1].Ssid);
        Assert.True(list[1].IsSavedProfile);
        Assert.True(list[1].IsSecured);
        Assert.Equal("GuestOpen", list[2].Ssid);
        Assert.False(list[2].IsSecured);
        Assert.False(list[2].IsSavedProfile);
    }

    [Fact]
    public void BrightnessService_ResolveBrightnessFromPolicy_SelectsAcOrDcCorrectly()
    {
        Assert.Equal(85, BrightnessService.ResolveBrightnessFromPolicy(1, acBrightness: 85, dcBrightness: 20, isOnAcPower: false));
        Assert.Equal(20, BrightnessService.ResolveBrightnessFromPolicy(2, acBrightness: 85, dcBrightness: 20, isOnAcPower: true));
        Assert.Equal(85, BrightnessService.ResolveBrightnessFromPolicy(3, acBrightness: 85, dcBrightness: 20, isOnAcPower: true));
        Assert.Equal(20, BrightnessService.ResolveBrightnessFromPolicy(3, acBrightness: 85, dcBrightness: 20, isOnAcPower: false));
    }

    [Fact]
    public void HardwareTelemetryService_ParseAsusBacklightLevel_DecodesPresentBitAndLevels()
    {
        Assert.Null(HardwareTelemetryService.ParseAsusBacklightLevel(0x00000000u));
        Assert.Equal(0, HardwareTelemetryService.ParseAsusBacklightLevel(0x00158000u));
        Assert.Equal(1, HardwareTelemetryService.ParseAsusBacklightLevel(0x00158001u));
        Assert.Equal(2, HardwareTelemetryService.ParseAsusBacklightLevel(0x00158002u));
        Assert.Equal(3, HardwareTelemetryService.ParseAsusBacklightLevel(0x00158003u));
    }
}
