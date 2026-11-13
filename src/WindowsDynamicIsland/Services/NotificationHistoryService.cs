using System.Collections.ObjectModel;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class NotificationHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string IconKey { get; set; } = "GeomBellNotif";
    public string AccentHex { get; set; } = "#38BDF8";
    public string TimeString => Timestamp.ToString("HH:mm");
    public string RelativeTime => FormatRelativeTime(Timestamp);

    private static string FormatRelativeTime(DateTime dt)
    {
        var diff = DateTime.Now - dt;
        var loc = LocalizationService.Instance;
        if (diff.TotalSeconds < 60) return loc.Get("Time_JustNow");
        if (diff.TotalMinutes < 60) return loc.Format("Time_MinutesAgo", (int)diff.TotalMinutes);
        if (diff.TotalHours < 24) return loc.Format("Time_HoursAgo", (int)diff.TotalHours);
        return dt.ToString("dd MMM HH:mm");
    }
}

/// <summary>
/// In-memory notification history session manager.
/// Never persists items to disk or database: resets automatically on app exit or restart.
/// </summary>
public sealed class NotificationHistoryService
{
    private const int MaxItems = 50;

    public ObservableCollection<NotificationHistoryItem> Items { get; } = [];

    public event Action? HistoryChanged;

    public void Add(string appName, string title, string message, string iconKey = "GeomBellNotif", string accentHex = "#38BDF8")
    {
        if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
            return;

        // Skip pure internal volume/brightness telemetry from history to keep it focused on messaging/app alerts
        if (appName.Equals("Audio", StringComparison.OrdinalIgnoreCase) ||
            appName.Equals("Kecerahan", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Volume", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Kecerahan", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var item = new NotificationHistoryItem
        {
            AppName = string.IsNullOrWhiteSpace(appName) ? LocalizationService.Instance.Get("Notif_SystemFallback") : appName,
            Title = string.IsNullOrWhiteSpace(title) ? appName : title,
            Message = message,
            IconKey = iconKey,
            AccentHex = accentHex,
            Timestamp = DateTime.Now
        };

        // Insert at the beginning (newest first)
        Items.Insert(0, item);

        // Cap at MaxItems
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        HistoryChanged?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        HistoryChanged?.Invoke();
    }

    public void Remove(NotificationHistoryItem item)
    {
        if (Items.Remove(item))
        {
            HistoryChanged?.Invoke();
        }
    }
}

