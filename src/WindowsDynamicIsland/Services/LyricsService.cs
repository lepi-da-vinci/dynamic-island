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
        StatusMessage = "Mencari lirik lagu...";
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
                    if (_currentLines.Count > 0)
                    {
                        StatusMessage = "Lirik sinkron aktif";
                        LyricsUpdated?.Invoke();
                        return;
                    }
                }
            }
            catch
            {
                // Proceed to network fetch if cache read fails
            }
        }

        // 2. Check local .lrc files in user's Music library
        try
        {
            string? localLrc = await FetchFromLocalFilesAsync(cleanTitle, cleanArtist);
            if (!string.IsNullOrWhiteSpace(localLrc))
            {
                var lines = ParseLrc(localLrc);
                if (lines.Count > 0)
                {
                    _currentLines = lines;
                    StatusMessage = "Lirik file lokal (.lrc)";
                    await TrySaveCacheAsync(cacheFile, localLrc);
                    LyricsUpdated?.Invoke();
                    return;
                }
            }
        }
        catch { }

        // 3. Provider 1: LRCLIB (Direct Synced Lyrics)
        string? candidatePlain = null;
        try
        {
            var (syncedLrc, plainLrc) = await FetchFromLrcLibAsync(cleanTitle, cleanArtist, album, durationSeconds);
            if (!string.IsNullOrWhiteSpace(syncedLrc))
            {
                var lines = ParseLrc(syncedLrc);
                if (lines.Count > 0)
                {
                    _currentLines = lines;
                    StatusMessage = "Lirik sinkron (LRCLIB)";
                    await TrySaveCacheAsync(cacheFile, syncedLrc);
                    LyricsUpdated?.Invoke();
                    return;
                }
            }
            candidatePlain = plainLrc;
        }
        catch { }

        // 4. Provider 2: Lyrics.ovh API
        try
        {
            string? ovhLyrics = await FetchFromLyricsOvhAsync(cleanTitle, cleanArtist);
            if (!string.IsNullOrWhiteSpace(ovhLyrics))
            {
                var timedLines = ConvertPlainLyricsToTimedLines(ovhLyrics, durationSeconds);
                if (timedLines.Count > 0)
                {
                    _currentLines = timedLines;
                    StatusMessage = "Lirik lagu (Lyrics.ovh)";
                    await TrySaveCacheAsync(cacheFile, FormatAsLrc(timedLines));
                    LyricsUpdated?.Invoke();
                    return;
                }
            }
        }
        catch { }

        // 5. Fallback to LRCLIB plain lyrics if available
        if (!string.IsNullOrWhiteSpace(candidatePlain))
        {
            var timedLines = ConvertPlainLyricsToTimedLines(candidatePlain, durationSeconds);
            if (timedLines.Count > 0)
            {
                _currentLines = timedLines;
                StatusMessage = "Lirik lagu (LRCLIB)";
                await TrySaveCacheAsync(cacheFile, FormatAsLrc(timedLines));
                LyricsUpdated?.Invoke();
                return;
            }
        }

        StatusMessage = $"{cleanTitle} - {cleanArtist}";
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
                NextLine: "Sinkronisasi lirik multi-provider otomatis",
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

    private static async Task<(string? SyncedLrc, string? PlainLyrics)> FetchFromLrcLibAsync(string title, string artist, string album, double durationSeconds)
    {
        try
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
                    string? synced = doc.RootElement.TryGetProperty("syncedLyrics", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                    string? plain = doc.RootElement.TryGetProperty("plainLyrics", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(synced) || !string.IsNullOrWhiteSpace(plain))
                    {
                        return (synced, plain);
                    }
                }
            }

            // Fallback search: /api/search?q=...
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
            string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            using var searchResp = await Http.GetAsync(searchUrl);
            if (searchResp.IsSuccessStatusCode)
            {
                string searchJson = await searchResp.Content.ReadAsStringAsync();
                using var searchDoc = JsonDocument.Parse(searchJson);
                if (searchDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    string? bestPlain = null;
                    foreach (var item in searchDoc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("syncedLyrics", out var sp) && sp.ValueKind == JsonValueKind.String)
                        {
                            string? synced = sp.GetString();
                            if (!string.IsNullOrWhiteSpace(synced)) return (synced, null);
                        }
                        if (bestPlain == null && item.TryGetProperty("plainLyrics", out var pp) && pp.ValueKind == JsonValueKind.String)
                        {
                            bestPlain = pp.GetString();
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(bestPlain))
                    {
                        return (null, bestPlain);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("LyricsService", "LRCLIB fetch failed", ex);
        }

        return (null, null);
    }

    private static async Task<string?> FetchFromLyricsOvhAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            string url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}";
            using var response = await Http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("lyrics", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    string? lyrics = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(lyrics))
                        return lyrics;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("LyricsService", "Lyrics.ovh fetch failed", ex);
        }

        return null;
    }

    private static async Task<string?> FetchFromLocalFilesAsync(string title, string artist)
    {
        try
        {
            string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (Directory.Exists(myMusic))
            {
                string[] patterns = [
                    $"{artist} - {title}.lrc",
                    $"{title} - {artist}.lrc",
                    $"{title}.lrc"
                ];

                foreach (string pat in patterns)
                {
                    var files = Directory.GetFiles(myMusic, pat, SearchOption.AllDirectories);
                    if (files.Length > 0 && File.Exists(files[0]))
                    {
                        return await File.ReadAllTextAsync(files[0]);
                    }
                }
            }
        }
        catch
        {
            // Ignore filesystem access exceptions
        }

        return null;
    }

    private static List<LyricLine> ConvertPlainLyricsToTimedLines(string plainLyrics, double durationSeconds)
    {
        var rawLines = plainLyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('[') && !l.StartsWith("Paroles de") && !l.StartsWith("Lyrics by"))
            .ToList();

        if (rawLines.Count == 0) return [];

        double dur = durationSeconds > 15 ? durationSeconds : rawLines.Count * 4.5;
        double intro = Math.Min(10.0, dur * 0.08);
        double outro = Math.Max(dur - 8.0, dur * 0.92);
        double singingSpan = Math.Max(5.0, outro - intro);
        double lineInterval = singingSpan / rawLines.Count;

        var result = new List<LyricLine>();
        for (int i = 0; i < rawLines.Count; i++)
        {
            double sec = intro + (i * lineInterval);
            result.Add(new LyricLine(TimeSpan.FromSeconds(sec), rawLines[i]));
        }

        return result;
    }

    private static string FormatAsLrc(List<LyricLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine($"[{(int)line.Timestamp.TotalMinutes:D2}:{line.Timestamp.Seconds:D2}.{line.Timestamp.Milliseconds / 10:D2}] {line.Text}");
        }
        return sb.ToString();
    }

    private static async Task TrySaveCacheAsync(string cacheFile, string content)
    {
        try
        {
            await File.WriteAllTextAsync(cacheFile, content);
        }
        catch
        {
            // Ignore cache write failure
        }
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
