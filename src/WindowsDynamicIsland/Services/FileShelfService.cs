using System.Collections.ObjectModel;
using System.IO;

namespace WindowsDynamicIsland.Services;

public sealed record ShelfItem(string FullPath, string FileName, string SizeLabel, string IconGlyph);

/// <summary>
/// Temporary "AirDrop-style" File Shelf allowing users to drag files/folders into the Dynamic Island
/// and drag them out later into another application (Discord, WhatsApp, Browser, Explorer).
/// </summary>
public sealed class FileShelfService
{
    public ObservableCollection<ShelfItem> Items { get; } = [];

    public int AddFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) name = path;

            string sizeLabel = "Folder";
            string icon = "📁";

            if (File.Exists(path))
            {
                try
                {
                    var fi = new FileInfo(path);
                    sizeLabel = FormatBytes(fi.Length);
                    icon = GetExtensionIcon(fi.Extension);
                }
                catch
                {
                    sizeLabel = "File";
                    icon = "📄";
                }
            }

            Items.Insert(0, new ShelfItem(path, name, sizeLabel, icon));
            added++;

            // Keep shelf tidy (max 8 recent items)
            while (Items.Count > 8)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }

        return added;
    }

    public void RemoveItem(ShelfItem item)
    {
        Items.Remove(item);
    }

    public void Clear()
    {
        Items.Clear();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.1} MB";
        return $"{mb / 1024.0:0.2} GB";
    }

    private static string GetExtensionIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" => "🖼️",
        ".mp4" or ".mkv" or ".mov" or ".webm" => "🎬",
        ".mp3" or ".wav" or ".flac" or ".ogg" => "🎵",
        ".zip" or ".rar" or ".7z" or ".tar" => "📦",
        ".pdf" or ".docx" or ".txt" or ".md" => "📑",
        ".cs" or ".py" or ".js" or ".ts" or ".json" or ".html" => "💻",
        _ => "📄"
    };
}

