using System.IO;
using System.Text.Json;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
}

public sealed class TodoAndWaterData
{
    public List<TodoItem> Todos { get; set; } = new();
    public double WaterCurrentLiters { get; set; } = 1.0;
    public double WaterTargetLiters { get; set; } = 1.5;
    public DateTime LastUpdatedDate { get; set; } = DateTime.Today;
}

public sealed class TodoAndWaterService
{
    private readonly string _storagePath;
    private TodoAndWaterData _data = new();

    public event Action? DataChanged;

    public IReadOnlyList<TodoItem> Todos => _data.Todos;
    public double WaterCurrentLiters => _data.WaterCurrentLiters;
    public double WaterTargetLiters => _data.WaterTargetLiters;

    public int CompletedTodosCount => _data.Todos.Count(t => t.IsDone);
    public int TotalTodosCount => _data.Todos.Count;

    public double WaterPercentage =>
        _data.WaterTargetLiters > 0
            ? Math.Clamp((_data.WaterCurrentLiters / _data.WaterTargetLiters) * 100.0, 0.0, 150.0)
            : 0.0;

    public TodoAndWaterService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(localAppData, "WindowsDynamicIsland");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "todo_water.json");

        LoadData();
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                string json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<TodoAndWaterData>(json);
                if (loaded != null)
                {
                    _data = loaded;
                    // Reset water counter on new day
                    if (_data.LastUpdatedDate.Date != DateTime.Today)
                    {
                        _data.WaterCurrentLiters = 0.0;
                        _data.LastUpdatedDate = DateTime.Today;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TodoAndWaterService", "Error loading data, using defaults", ex);
        }

        if (_data.Todos.Count == 0)
        {
            _data.Todos = new List<TodoItem>
            {
                new() { Id = 1, Title = "Selesaikan laporan kerja", IsDone = true },
                new() { Id = 2, Title = "Balas email & pesan penting", IsDone = true },
                new() { Id = 3, Title = "Siram tanaman & rapikan meja", IsDone = false },
                new() { Id = 4, Title = "Review target harian", IsDone = false }
            };
            SaveData();
        }
    }

    private void SaveData()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TodoAndWaterService", "Error saving data", ex);
        }
    }

    public void ToggleTodo(int id)
    {
        var item = _data.Todos.FirstOrDefault(t => t.Id == id);
        if (item != null)
        {
            item.IsDone = !item.IsDone;
            SaveData();
            DataChanged?.Invoke();
        }
    }

    public void AddTodo(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        int nextId = _data.Todos.Count > 0 ? _data.Todos.Max(t => t.Id) + 1 : 1;
        _data.Todos.Add(new TodoItem { Id = nextId, Title = title.Trim(), IsDone = false });
        SaveData();
        DataChanged?.Invoke();
    }

    public void UpdateTodoTitle(int id, string newTitle)
    {
        var item = _data.Todos.FirstOrDefault(t => t.Id == id);
        if (item != null)
        {
            string clean = newTitle.Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                DeleteTodo(id);
                return;
            }

            if (item.Title != clean)
            {
                item.Title = clean;
                SaveData();
                DataChanged?.Invoke();
            }
        }
    }

    public void DeleteTodo(int id)
    {
        int count = _data.Todos.RemoveAll(t => t.Id == id);
        if (count > 0)
        {
            SaveData();
            DataChanged?.Invoke();
        }
    }

    public void DrinkWater(double deltaLiters = 0.25)
    {
        _data.WaterCurrentLiters = Math.Round(_data.WaterCurrentLiters + deltaLiters, 2);
        _data.LastUpdatedDate = DateTime.Today;
        SaveData();
        DataChanged?.Invoke();
    }

    public void ResetWater()
    {
        _data.WaterCurrentLiters = 0.0;
        SaveData();
        DataChanged?.Invoke();
    }
}

