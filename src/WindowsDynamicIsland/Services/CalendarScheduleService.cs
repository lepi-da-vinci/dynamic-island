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
    public string DateIso { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string Category { get; set; } = "Kuliah";
    public string AccentHex { get; set; } = "#38BDF8";
    public bool IsCompleted { get; set; }
}

public sealed class CalendarDayCell
{
    public DateOnly Date { get; set; }
    public int DayNumber { get; set; }
    public bool IsToday { get; set; }
    public bool IsSelected { get; set; }
    public bool IsCurrentMonth { get; set; }
    public bool HasEvents { get; set; }
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
        return GenerateMonthGrid(now.Year, now.Month, DateOnly.FromDateTime(now));
    }

    public List<CalendarDayCell> GenerateMonthGrid(int year, int month, DateOnly? selectedDate = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var firstDay = new DateOnly(year, month, 1);
        int daysInMonth = DateTime.DaysInMonth(year, month);
        int offset = (int)firstDay.DayOfWeek;

        var activeDatesWithEvents = new HashSet<string>(
            ScheduleItems
                .Where(s => !s.IsCompleted && !string.IsNullOrWhiteSpace(s.DateIso))
                .Select(s => s.DateIso),
            StringComparer.Ordinal);

        var prevMonth = firstDay.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
        var grid = new List<CalendarDayCell>(42);

        for (int i = offset - 1; i >= 0; i--)
        {
            var d = new DateOnly(prevMonth.Year, prevMonth.Month, daysInPrevMonth - i);
            grid.Add(new CalendarDayCell
            {
                Date = d,
                DayNumber = d.Day,
                IsToday = d == today,
                IsSelected = selectedDate.HasValue && d == selectedDate.Value,
                IsCurrentMonth = false,
                HasEvents = activeDatesWithEvents.Contains(d.ToString("yyyy-MM-dd"))
            });
        }

        for (int day = 1; day <= daysInMonth; day++)
        {
            var d = new DateOnly(year, month, day);
            grid.Add(new CalendarDayCell
            {
                Date = d,
                DayNumber = day,
                IsToday = d == today,
                IsSelected = selectedDate.HasValue && d == selectedDate.Value,
                IsCurrentMonth = true,
                HasEvents = activeDatesWithEvents.Contains(d.ToString("yyyy-MM-dd"))
            });
        }

        var nextMonth = firstDay.AddMonths(1);
        int totalCells = grid.Count <= 35 ? 35 : 42;
        int nextDay = 1;
        while (grid.Count < totalCells)
        {
            var d = new DateOnly(nextMonth.Year, nextMonth.Month, nextDay++);
            grid.Add(new CalendarDayCell
            {
                Date = d,
                DayNumber = d.Day,
                IsToday = d == today,
                IsSelected = selectedDate.HasValue && d == selectedDate.Value,
                IsCurrentMonth = false,
                HasEvents = activeDatesWithEvents.Contains(d.ToString("yyyy-MM-dd"))
            });
        }

        return grid;
    }

    public List<ScheduleItem> GetItemsForDate(DateOnly date)
    {
        string iso = date.ToString("yyyy-MM-dd");
        return ScheduleItems
            .Where(s => string.Equals(s.DateIso, iso, StringComparison.Ordinal))
            .OrderBy(s => s.Time, StringComparer.Ordinal)
            .ToList();
    }

    public List<(ScheduleItem Item, int MinutesUntilStart)> CheckUpcomingReminders(DateTime nowLocal, HashSet<string> firedReminderKeys)
    {
        var due = new List<(ScheduleItem, int)>();
        string todayIso = DateOnly.FromDateTime(nowLocal).ToString("yyyy-MM-dd");
        int nowMin = (nowLocal.Hour * 60) + nowLocal.Minute;

        foreach (var item in ScheduleItems)
        {
            if (item.IsCompleted || !string.Equals(item.DateIso, todayIso, StringComparison.Ordinal))
                continue;

            if (!TryParseStartTimeMinutes(item.Time, out int startMin))
                continue;

            int diff = startMin - nowMin;
            if (diff == 5 || diff == 0)
            {
                string key = $"{item.Id}|{todayIso}|{diff}";
                if (firedReminderKeys.Add(key))
                {
                    due.Add((item, diff));
                }
            }
        }

        return due;
    }

    private static bool TryParseStartTimeMinutes(string? timeText, out int totalMinutes)
    {
        totalMinutes = 0;
        if (string.IsNullOrWhiteSpace(timeText)) return false;

        string firstPart = timeText.Split('-', StringSplitOptions.TrimEntries)[0];
        var match = System.Text.RegularExpressions.Regex.Match(firstPart, @"\b(\d{1,2})[:.](\d{2})\b");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out int h) &&
            int.TryParse(match.Groups[2].Value, out int m) &&
            h is >= 0 and <= 23 &&
            m is >= 0 and <= 59)
        {
            totalMinutes = (h * 60) + m;
            return true;
        }
        return false;
    }

    public void AddScheduleItem(string title, string time, string category, DateOnly? date = null)
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
            Time = string.IsNullOrWhiteSpace(time) ? "09:00" : time.Trim(),
            DateIso = (date ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd"),
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
                    string todayIso = DateTime.Today.ToString("yyyy-MM-dd");
                    foreach (var it in items)
                    {
                        if (string.IsNullOrWhiteSpace(it.DateIso))
                        {
                            it.DateIso = todayIso;
                        }
                        ScheduleItems.Add(it);
                    }
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

