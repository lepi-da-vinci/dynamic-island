using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsDynamicIsland.Services;

public sealed record LyricLine(TimeSpan Timestamp, string Text);

public sealed record ActiveLyricSnapshot(
    int LineIndex,
    string PreviousLine,
    string CurrentLine,
    string NextLine,
    double LineProgressRatio,
    bool IsSynced);

public sealed partial class LyricsService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string _cacheDirectory;
    private List<LyricLine> _currentLines = [];
    private string _loadedTrackKey = string.Empty;

    public bool HasSyncedLyrics => _currentLines.Count > 0;
    public string StatusMessage { get; private set; } = "Putar musik di Spotify / Browser";

    public event Action? LyricsUpdated;

    public LyricsService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicIsland",
            "LyricsCache");

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch
        {
            // Ignore directory creation errors if restricted
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDynamicIsland/1.1 (https://github.com/lepi-da-vinci/dynamic-island)");
        return client;
    }

    public async Task LoadLyricsForTrackAsync(string title, string artist, string album, double durationSeconds)
    {
        string cleanTitle = CleanTrackTitle(title);
        string cleanArtist = CleanArtistName(artist);
        string trackKey = $"{cleanArtist}|{cleanTitle}".ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            _currentLines = [];
            _loadedTrackKey = string.Empty;
            StatusMessage = "Tidak ada media aktif";
            LyricsUpdated?.Invoke();
            return;
        }

        if (trackKey == _loadedTrackKey)
        {
            return;
        }

        _loadedTrackKey = trackKey;
        _currentLines = [];
        StatusMessage = "Mencari lirik sinkron (LRCLIB)...";
        LyricsUpdated?.Invoke();

        // 1. Check local disk cache first (0ms offline response)
        string cacheFile = GetCacheFilePath(trackKey);
        if (File.Exists(cacheFile))
        {
            try
            {
                string cachedLrc = await File.ReadAllTextAsync(cacheFile);
                if (!string.IsNullOrWhiteSpace(cachedLrc))
                {
                    _currentLines = ParseLrc(cachedLrc);
                    StatusMessage = _currentLines.Count > 0 ? "Lirik sinkron aktif" : "Lirik instrumental / teks biasa";
                    LyricsUpdated?.Invoke();
                    return;
                }
            }
            catch
            {
                // Proceed to network fetch if cache read fails
            }
        }

        // 2. Fetch from LRCLIB API
        try
        {
            string? syncedLrc = await FetchFromLrcLibAsync(cleanTitle, cleanArtist, album, durationSeconds);

            if (!string.IsNullOrWhiteSpace(syncedLrc))
            {
                _currentLines = ParseLrc(syncedLrc);
                try
                {
                    await File.WriteAllTextAsync(cacheFile, syncedLrc);
                }
                catch
                {
                    // Ignore disk write failure
                }

                StatusMessage = _currentLines.Count > 0
                    ? "Lirik sinkron aktif"
                    : "Lirik tanpa timestamp";
            }
            else
            {
                StatusMessage = $"{cleanTitle} - {cleanArtist}";
            }
        }
        catch
        {
            StatusMessage = $"{cleanTitle} - {cleanArtist}";
        }

        LyricsUpdated?.Invoke();
    }

    public ActiveLyricSnapshot GetActiveLyrics(TimeSpan position)
    {
        if (_currentLines.Count == 0)
        {
            return new ActiveLyricSnapshot(
                LineIndex: -1,
                PreviousLine: string.Empty,
                CurrentLine: StatusMessage,
                NextLine: "Sinkronisasi lirik otomatis via LRCLIB",
                LineProgressRatio: 0.0,
                IsSynced: false);
        }

        // Lookahead by 200ms so visual animation lands right on the vocal beat
        TimeSpan adjusted = position + TimeSpan.FromMilliseconds(200);
        int activeIndex = -1;

        for (int i = 0; i < _currentLines.Count; i++)
        {
            if (_currentLines[i].Timestamp <= adjusted)
            {
                activeIndex = i;
            }
            else
            {
                break;
            }
        }

        if (activeIndex < 0)
        {
            TimeSpan firstTs = _currentLines[0].Timestamp;
            double introRatio = firstTs.TotalSeconds > 0
                ? Math.Clamp(adjusted.TotalSeconds / firstTs.TotalSeconds, 0.0, 1.0)
                : 0.0;

            return new ActiveLyricSnapshot(
                LineIndex: -1,
                PreviousLine: string.Empty,
                CurrentLine: "• • •",
                NextLine: _currentLines[0].Text,
                LineProgressRatio: introRatio,
                IsSynced: true);
        }

        string prev = activeIndex > 0 ? _currentLines[activeIndex - 1].Text : string.Empty;
        string current = _currentLines[activeIndex].Text;
        string next = (activeIndex + 1 < _currentLines.Count)
            ? _currentLines[activeIndex + 1].Text
            : "• • •";

        double progress = 1.0;
        if (activeIndex + 1 < _currentLines.Count)
        {
            TimeSpan start = _currentLines[activeIndex].Timestamp;
            TimeSpan end = _currentLines[activeIndex + 1].Timestamp;
            double spanSec = (end - start).TotalSeconds;
            if (spanSec > 0.15)
            {
                progress = Math.Clamp((adjusted - start).TotalSeconds / spanSec, 0.0, 1.0);
            }
        }

        return new ActiveLyricSnapshot(
            LineIndex: activeIndex,
            PreviousLine: prev,
            CurrentLine: current,
            NextLine: next,
            LineProgressRatio: progress,
            IsSynced: true);
    }

    private static async Task<string?> FetchFromLrcLibAsync(string title, string artist, string album, double durationSeconds)
    {
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var urlBuilder = new StringBuilder("https://lrclib.net/api/get?");
            urlBuilder.Append($"track_name={Uri.EscapeDataString(title)}");
            urlBuilder.Append($"&artist_name={Uri.EscapeDataString(artist)}");
            if (!string.IsNullOrWhiteSpace(album))
            {
                urlBuilder.Append($"&album_name={Uri.EscapeDataString(album)}");
            }
            if (durationSeconds > 5)
            {
                urlBuilder.Append($"&duration={Math.Round(durationSeconds)}");
            }

            using var response = await Http.GetAsync(urlBuilder.ToString());
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var prop) &&
                    prop.ValueKind == JsonValueKind.String)
                {
                    string? lrc = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(lrc))
                        return lrc;
                }
            }
        }

        // Fallback search: /api/search?q=...
        string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
        using var searchResp = await Http.GetAsync(searchUrl);
        if (!searchResp.IsSuccessStatusCode) return null;

        string searchJson = await searchResp.Content.ReadAsStringAsync();
        using var searchDoc = JsonDocument.Parse(searchJson);
        if (searchDoc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in searchDoc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("syncedLyrics", out var prop) &&
                    prop.ValueKind == JsonValueKind.String)
                {
                    string? lrc = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(lrc))
                        return lrc;
                }
            }
        }

        return null;
    }

    private static List<LyricLine> ParseLrc(string lrcContent)
    {
        var lines = new List<LyricLine>();
        var regex = LrcLineRegex();

        foreach (string rawLine in lrcContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = regex.Match(rawLine);
            if (!match.Success) continue;

            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);
            string fractionStr = match.Groups[3].Value;
            int milliseconds = fractionStr.Length == 2
                ? int.Parse(fractionStr) * 10
                : int.Parse(fractionStr.PadRight(3, '0')[..3]);

            string text = match.Groups[4].Value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "• • •";
            }

            lines.Add(new LyricLine(new TimeSpan(0, 0, minutes, seconds, milliseconds), text));
        }

        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return lines;
    }

    private static string CleanTrackTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;
        string cleaned = TitleNoiseRegex().Replace(rawTitle, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? rawTitle.Trim() : cleaned;
    }

    private static string CleanArtistName(string? rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist)) return string.Empty;
        return rawArtist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private string GetCacheFilePath(string trackKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(trackKey));
        string hex = Convert.ToHexString(hash)[..24].ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{hex}.lrc");
    }

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{2})\.(\d{2,3})\](.*)$")]
    private static partial Regex LrcLineRegex();

    [GeneratedRegex(@"\s*(\((Official|Lyric|Music|Audio|HD|4K|Visualizer|Video)[^)]*\)|\[(Official|Lyric|Music|Audio|HD|4K|Visualizer|Video)[^\]]*\]|-\s*YouTube.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoiseRegex();
}
