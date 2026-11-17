using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WindowsDynamicIsland.Services;

public enum ClipboardItemKind
{
    Text,
    Url,
    ColorHex,
    Image,
    FilePaths
}

public sealed class ClipboardItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Content { get; init; } = string.Empty;
    public string PreviewText { get; init; } = string.Empty;
    public ClipboardItemKind Kind { get; init; } = ClipboardItemKind.Text;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public BitmapSource? ThumbnailImage { get; init; }
    public string MetadataLabel { get; init; } = string.Empty;
    public bool IsPinned { get; set; }

    public string TimeAgoText
    {
        get
        {
            var diff = DateTime.UtcNow - TimestampUtc;
            var loc = LocalizationService.Instance;
            if (diff.TotalSeconds < 45) return loc.Get("Time_JustNow");
            if (diff.TotalMinutes < 60) return loc.Format("Time_MinutesAgo", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24) return loc.Format("Time_HoursAgo", (int)diff.TotalHours);
            return $"{(int)diff.TotalDays}d";
        }
    }

    public string BadgeLabel => Kind switch
    {
        ClipboardItemKind.Url => "URL",
        ClipboardItemKind.ColorHex => "HEX",
        ClipboardItemKind.Image => LocalizationService.Instance.Get("Clip_BadgeImage", "IMAGE"),
        ClipboardItemKind.FilePaths => "FILE",
        _ => LocalizationService.Instance.Get("Clip_BadgeText", "TEXT")
    };

    public string BadgeColorHex => Kind switch
    {
        ClipboardItemKind.Url => "#38BDF8",       // Cyan
        ClipboardItemKind.ColorHex => "#F59E0B",  // Amber
        ClipboardItemKind.Image => "#A855F7",     // Purple
        ClipboardItemKind.FilePaths => "#22C55E", // Emerald
        _ => "#94A3B8"                            // Slate
    };

    public Visibility TextVisibility => Kind != ClipboardItemKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => Kind == ClipboardItemKind.Image && ThumbnailImage != null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class ClipboardHistoryService
{
    private static readonly Regex HexColorRegex = new(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"^(https?://|www\.)[^\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ObservableCollection<ClipboardItem> Items { get; } = [];
    public event Action? HistoryChanged;

    private sealed class PinnedClipDto
    {
        public string Id { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string PreviewText { get; set; } = string.Empty;
        public ClipboardItemKind Kind { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string MetadataLabel { get; set; } = string.Empty;
    }

    private const int MaxItemsCount = 25;
    private readonly string _pinnedPath;
    private string _lastRecordedText = string.Empty;

    public ClipboardHistoryService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicIsland");
        Directory.CreateDirectory(folder);
        _pinnedPath = Path.Combine(folder, "pinned_clips.json");
        LoadPinnedItems();
    }

    public void RecordCurrentClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (string.IsNullOrWhiteSpace(text) || text == _lastRecordedText)
                    return;

                _lastRecordedText = text;

                ClipboardItemKind kind = ClipboardItemKind.Text;
                if (HexColorRegex.IsMatch(text))
                {
                    kind = ClipboardItemKind.ColorHex;
                }
                else if (UrlRegex.IsMatch(text))
                {
                    kind = ClipboardItemKind.Url;
                }

                string singleLine = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
                string preview = singleLine.Length > 90 ? singleLine[..90] + "…" : singleLine;
                string meta = $"{text.Length} karakter";

                var item = new ClipboardItem
                {
                    Content = text,
                    PreviewText = preview,
                    Kind = kind,
                    MetadataLabel = meta,
                    TimestampUtc = DateTime.UtcNow
                };

                AddItem(item);
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    var item = new ClipboardItem
                    {
                        Content = $"[Gambar {image.PixelWidth}x{image.PixelHeight}]",
                        PreviewText = $"Screenshot / Gambar ({image.PixelWidth}x{image.PixelHeight})",
                        Kind = ClipboardItemKind.Image,
                        ThumbnailImage = image,
                        MetadataLabel = $"{image.PixelWidth}x{image.PixelHeight} px",
                        TimestampUtc = DateTime.UtcNow
                    };

                    AddItem(item);
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    string first = Path.GetFileName(files[0] ?? string.Empty);
                    string summary = files.Count == 1 ? first : $"{first} (+{files.Count - 1} lainnya)";

                    var item = new ClipboardItem
                    {
                        Content = string.Join(Environment.NewLine, files.Cast<string>()),
                        PreviewText = summary,
                        Kind = ClipboardItemKind.FilePaths,
                        MetadataLabel = $"{files.Count} file",
                        TimestampUtc = DateTime.UtcNow
                    };

                    AddItem(item);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("ClipboardHistoryService", $"Failed recording clipboard: {ex.Message}");
        }
    }

    private void AddItem(ClipboardItem item)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = Items.FirstOrDefault(i => i.Content == item.Content);
            bool preservePin = false;
            if (existing != null)
            {
                preservePin = existing.IsPinned;
                Items.Remove(existing);
            }

            if (preservePin)
            {
                item.IsPinned = true;
                Items.Insert(0, item);
                SavePinnedItems();
            }
            else
            {
                int firstUnpinnedIdx = 0;
                while (firstUnpinnedIdx < Items.Count && Items[firstUnpinnedIdx].IsPinned)
                {
                    firstUnpinnedIdx++;
                }
                Items.Insert(firstUnpinnedIdx, item);
            }

            while (Items.Count > MaxItemsCount)
            {
                int removeIdx = -1;
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (!Items[i].IsPinned)
                    {
                        removeIdx = i;
                        break;
                    }
                }
                if (removeIdx < 0) break;
                Items.RemoveAt(removeIdx);
            }

            HistoryChanged?.Invoke();
        });
    }

    public void TogglePin(ClipboardItem item)
    {
        if (item.Kind == ClipboardItemKind.Image && !item.IsPinned)
        {
            // Memory-only images still support pinning within current session
            item.IsPinned = true;
        }
        else
        {
            item.IsPinned = !item.IsPinned;
        }

        Items.Remove(item);
        if (item.IsPinned)
        {
            Items.Insert(0, item);
        }
        else
        {
            int insertIdx = 0;
            while (insertIdx < Items.Count && Items[insertIdx].IsPinned) insertIdx++;
            Items.Insert(insertIdx, item);
        }

        SavePinnedItems();
        HistoryChanged?.Invoke();
    }

    public void CopyToClipboard(ClipboardItem item)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Image && item.ThumbnailImage != null)
            {
                Clipboard.SetImage(item.ThumbnailImage);
            }
            else
            {
                _lastRecordedText = item.Content;
                Clipboard.SetText(item.Content);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ClipboardHistoryService", $"Failed to copy item to clipboard: {ex.Message}", ex);
        }
    }

    public void Remove(ClipboardItem item)
    {
        bool wasPinned = item.IsPinned;
        Items.Remove(item);
        if (wasPinned) SavePinnedItems();
        HistoryChanged?.Invoke();
    }

    public void ClearAll()
    {
        var pinnedOnly = Items.Where(i => i.IsPinned).ToList();
        Items.Clear();
        foreach (var p in pinnedOnly) Items.Add(p);
        _lastRecordedText = string.Empty;
        HistoryChanged?.Invoke();
    }

    private void LoadPinnedItems()
    {
        try
        {
            if (!File.Exists(_pinnedPath)) return;
            string json = File.ReadAllText(_pinnedPath);
            var list = JsonSerializer.Deserialize<List<PinnedClipDto>>(json);
            if (list == null) return;

            foreach (var dto in list)
            {
                if (string.IsNullOrWhiteSpace(dto.Content)) continue;
                Items.Add(new ClipboardItem
                {
                    Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N")[..8] : dto.Id,
                    Content = dto.Content,
                    PreviewText = dto.PreviewText,
                    Kind = dto.Kind,
                    TimestampUtc = dto.TimestampUtc == default ? DateTime.UtcNow : dto.TimestampUtc,
                    MetadataLabel = dto.MetadataLabel,
                    IsPinned = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("ClipboardHistoryService", $"Failed loading pinned clips: {ex.Message}");
        }
    }

    private void SavePinnedItems()
    {
        try
        {
            var pinned = Items
                .Where(i => i.IsPinned && i.Kind != ClipboardItemKind.Image)
                .Select(i => new PinnedClipDto
                {
                    Id = i.Id,
                    Content = i.Content,
                    PreviewText = i.PreviewText,
                    Kind = i.Kind,
                    TimestampUtc = i.TimestampUtc,
                    MetadataLabel = i.MetadataLabel
                })
                .ToList();

            string json = JsonSerializer.Serialize(pinned, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_pinnedPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("ClipboardHistoryService", $"Failed saving pinned clips: {ex.Message}");
        }
    }
}

