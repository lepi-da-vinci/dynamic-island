using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsDynamicIsland.Services;

public sealed record ShelfItem(
    string FullPath,
    string FileName,
    string SizeLabel,
    string IconGlyph,
    BitmapSource? PreviewImage)
{
    public Visibility PreviewImageVisibility => PreviewImage != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconGlyphVisibility => PreviewImage == null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Temporary "AirDrop-style" File Shelf allowing users to drag files/images/folders into the Dynamic Island
/// with real image thumbnail previews and drag them out later into any target application.
/// </summary>
public sealed class FileShelfService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".tiff", ".tif", ".jfif"
    };

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
            string icon = "DIR";
            BitmapSource? preview = null;

            if (File.Exists(path))
            {
                try
                {
                    var fi = new FileInfo(path);
                    sizeLabel = FormatBytes(fi.Length);
                    icon = GetExtensionIcon(fi.Extension);

                    if (ImageExtensions.Contains(fi.Extension))
                    {
                        preview = LoadImageThumbnailSafe(path);
                    }
                }
                catch
                {
                    sizeLabel = "File";
                    icon = "FILE";
                }
            }

            Items.Insert(0, new ShelfItem(path, name, sizeLabel, icon, preview));
            added++;

            while (Items.Count > 8)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }

        return added;
    }

    public int AddBitmapFromDrag(BitmapSource bitmap)
    {
        try
        {
            string tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsDynamicIsland",
                "ShelfDrops");
            Directory.CreateDirectory(tempDir);

            string fileName = $"Image_{DateTime.Now:HHmmss}.png";
            string fullPath = Path.Combine(tempDir, fileName);

            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(fs);
            }

            return AddFiles([fullPath]);
        }
        catch
        {
            return 0;
        }
    }

    private static BitmapSource? LoadImageThumbnailSafe(string filePath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 110;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
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
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" or ".bmp" => "IMG",
        ".mp4" or ".mkv" or ".mov" or ".webm" => "VID",
        ".mp3" or ".wav" or ".flac" or ".ogg" => "AUD",
        ".zip" or ".rar" or ".7z" or ".tar" => "ZIP",
        ".pdf" or ".docx" or ".txt" or ".md" => "DOC",
        ".cs" or ".py" or ".js" or ".ts" or ".json" or ".html" => "CODE",
        _ => "FILE"
    };
}
