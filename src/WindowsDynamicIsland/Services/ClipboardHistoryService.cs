using System.Collections.ObjectModel;
using System.IO;
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

    public string TimeAgoText
    {
        get
        {
            var diff = DateTime.UtcNow - TimestampUtc;
            if (diff.TotalSeconds < 45) return "Baru saja";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m lalu";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}j lalu";
            return $"{(int)diff.TotalDays}h lalu";
        }
    }

    public string BadgeLabel => Kind switch
    {
        ClipboardItemKind.Url => "URL",
        ClipboardItemKind.ColorHex => "HEX",
        ClipboardItemKind.Image => "GAMBAR",
        ClipboardItemKind.FilePaths => "FILE",
        _ => "TEKS"
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

    private const int MaxItemsCount = 25;
    private string _lastRecordedText = string.Empty;

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
            if (existing != null)
            {
                Items.Remove(existing);
            }

            Items.Insert(0, item);

            while (Items.Count > MaxItemsCount)
            {
                Items.RemoveAt(Items.Count - 1);
            }

            HistoryChanged?.Invoke();
        });
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
        Items.Remove(item);
        HistoryChanged?.Invoke();
    }

    public void ClearAll()
    {
        Items.Clear();
        _lastRecordedText = string.Empty;
        HistoryChanged?.Invoke();
    }
}

