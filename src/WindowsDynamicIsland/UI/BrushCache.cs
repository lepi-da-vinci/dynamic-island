using System.Collections.Concurrent;
using System.Windows.Media;

namespace WindowsDynamicIsland.UI;

/// <summary>
/// Thread-safe cache of frozen SolidColorBrush instances to avoid repeated WPF brush allocations
/// and garbage collection pressure inside high-frequency UI updates and render loops.
/// </summary>
public static class BrushCache
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> Cache = new();
    private static readonly ConcurrentDictionary<string, SolidColorBrush> HexCache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Get(Color color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return Cache.GetOrAdd(key, _ =>
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        });
    }

    public static SolidColorBrush Get(byte r, byte g, byte b) =>
        Get(Color.FromRgb(r, g, b));

    public static SolidColorBrush Get(byte a, byte r, byte g, byte b) =>
        Get(Color.FromArgb(a, r, g, b));

    public static SolidColorBrush GetHex(string? hex, Color? fallback = null)
    {
        string normalized = string.IsNullOrWhiteSpace(hex) ? string.Empty : hex.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return Get(fallback ?? Color.FromRgb(56, 189, 248));
        }

        return HexCache.GetOrAdd(normalized, h =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(h) is Color parsed)
                {
                    return Get(parsed);
                }
            }
            catch
            {
                // Fallback below
            }
            return Get(fallback ?? Color.FromRgb(56, 189, 248));
        });
    }
}
