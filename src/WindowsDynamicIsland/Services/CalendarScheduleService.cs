using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class ScheduleItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Time { get; set; } = "09:00";
    public string Category { get; set; } = "Kuliah";
    public string AccentHex { get; set; } = "#38BDF8";
    public bool IsCompleted { get; set; }
}

public sealed class CalendarDayCell
{
    public int DayNumber { get; set; }
    public bool IsToday { get; set; }
    public bool IsCurrentMonth { get; set; }
}

/// <summary>
/// Manages the monthly calendar date grid and daily schedule/agenda items.
/// Persists schedule items to local JSON.
/// </summary>
public sealed class CalendarScheduleService
{
    private readonly string _storagePath;
    public ObservableCollection<ScheduleItem> ScheduleItems { get; } = [];

    public event Action? ScheduleChanged;

    public CalendarScheduleService()
    {
        string localFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicIsland");
        Directory.CreateDirectory(localFolder);
        _storagePath = Path.Combine(localFolder, "schedule.json");

        Load();
    }

    public List<CalendarDayCell> GenerateCurrentMonthGrid()
    {
        var now = DateTime.Now;
        var firstDay = new DateTime(now.Year, now.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

        // Convert Sunday-first (0..6) to Monday-first (0..6) or keep standard 0=Sunday
        // Let's use standard Sunday-first: Sun=0, Mon=1, Tue=2, Wed=3, Thu=4, Fri=5, Sat=6
        int offset = (int)firstDay.DayOfWeek;

        var prevMonth = firstDay.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);

        var grid = new List<CalendarDayCell>();

        // Days from previous month to fill first week
        for (int i = offset - 1; i >= 0; i--)
        {
            grid.Add(new CalendarDayCell
            {
                DayNumber = daysInPrevMonth - i,
                IsToday = false,
                IsCurrentMonth = false
            });
        }

        // Days of current month
        for (int d = 1; d <= daysInMonth; d++)
        {
            grid.Add(new CalendarDayCell
            {
                DayNumber = d,
                IsToday = (d == now.Day),
                IsCurrentMonth = true
            });
        }

        // Fill trailing days up to multiple of 7 (at least 35 or 42 cells total)
        int totalCells = grid.Count <= 35 ? 35 : 42;
        int nextDay = 1;
        while (grid.Count < totalCells)
        {
            grid.Add(new CalendarDayCell
            {
                DayNumber = nextDay++,
                IsToday = false,
                IsCurrentMonth = false
            });
        }

        return grid;
    }

    public void AddScheduleItem(string title, string time, string category)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        string accent = category switch
        {
            "Kuliah" => "#38BDF8",
            "Proyek" => "#A855F7",
            "Penting" => "#FB7185",
            "Pribadi" => "#22C55E",
            _ => "#38BDF8"
        };

        var item = new ScheduleItem
        {
            Title = title.Trim(),
            Time = string.IsNullOrWhiteSpace(time) ? "Hari ini" : time.Trim(),
            Category = category,
            AccentHex = accent,
            IsCompleted = false
        };

        ScheduleItems.Add(item);
        Save();
        ScheduleChanged?.Invoke();
    }

    public void ToggleScheduleItem(string id)
    {
        var item = ScheduleItems.FirstOrDefault(s => s.Id == id);
        if (item != null)
        {
            item.IsCompleted = !item.IsCompleted;
            Save();
            ScheduleChanged?.Invoke();
        }
    }

    public void DeleteScheduleItem(string id)
    {
        var item = ScheduleItems.FirstOrDefault(s => s.Id == id);
        if (item != null)
        {
            ScheduleItems.Remove(item);
            Save();
            ScheduleChanged?.Invoke();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                string json = File.ReadAllText(_storagePath);
                var items = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
                if (items != null && items.Count > 0)
                {
                    ScheduleItems.Clear();
                    foreach (var it in items) ScheduleItems.Add(it);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("CalendarScheduleService", $"Failed loading schedule: {ex.Message}");
        }

        // Populate helpful default sample agenda items
        ScheduleItems.Clear();
        ScheduleItems.Add(new ScheduleItem
        {
            Title = "Pemrograman Berorientasi Objek",
            Time = "08:00 - 10:30",
            Category = "Kuliah",
            AccentHex = "#38BDF8",
            IsCompleted = false
        });
        ScheduleItems.Add(new ScheduleItem
        {
            Title = "Struktur Data & Algoritma",
            Time = "11:00 - 12:30",
            Category = "Kuliah",
            AccentHex = "#818CF8",
            IsCompleted = false
        });
        ScheduleItems.Add(new ScheduleItem
        {
            Title = "Pengembangan Dynamic Island",
            Time = "14:00 - 16:00",
            Category = "Proyek",
            AccentHex = "#A855F7",
            IsCompleted = false
        });
        ScheduleItems.Add(new ScheduleItem
        {
            Title = "Evaluasi Materi & Istirahat",
            Time = "19:30 - 20:30",
            Category = "Pribadi",
            AccentHex = "#22C55E",
            IsCompleted = false
        });
        Save();
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(ScheduleItems.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("CalendarScheduleService", $"Failed saving schedule: {ex.Message}");
        }
    }
}

