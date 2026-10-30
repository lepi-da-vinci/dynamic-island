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
    private double _loadedDurationSeconds;
    private int _requestVersion;

    public bool HasSyncedLyrics => _currentLines.Count > 0;
    public string StatusMessage { get; private set; } = "Putar musik di Spotify / Browser";

    public event Action? LyricsUpdated;

    public LyricsService()
    {
        // Use V2 cache folder so any previously cached mismatched YouTube lyrics are cleanly bypassed
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicIsland",
            "LyricsCacheV2");

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsDynamicIsland/1.7 (https://github.com/lepi-da-vinci/dynamic-island)");
        return client;
    }

    public async Task LoadLyricsForTrackAsync(string title, string artist, string album, double durationSeconds)
    {
        string cleanTitle = CleanTrackTitle(title);
        string cleanArtist = CleanArtistName(artist);

        if (string.IsNullOrWhiteSpace(cleanTitle) || cleanTitle == "Tidak Ada Media" || cleanTitle == "Media Aktif")
        {
            Interlocked.Increment(ref _requestVersion);
            _currentLines = [];
            _loadedTrackKey = string.Empty;
            _loadedDurationSeconds = 0;
            StatusMessage = "Tidak ada media aktif";
            LyricsUpdated?.Invoke();
            return;
        }

        string trackKey = $"{cleanArtist}|{cleanTitle}".ToLowerInvariant();

        // Skip redundant reload unless duration just arrived (>5s) and either we had no lyrics or duration mismatches
        if (trackKey == _loadedTrackKey)
        {
            bool durationJustArrived = _loadedDurationSeconds <= 5.0 && durationSeconds > 5.0;
            bool loadedLyricsMismatchDuration = durationSeconds > 25.0 &&
                                                _currentLines.Count > 0 &&
                                                _currentLines[^1].Timestamp.TotalSeconds > durationSeconds + 30.0;
            if (!durationJustArrived && !loadedLyricsMismatchDuration)
            {
                return;
            }
        }

        int reqId = Interlocked.Increment(ref _requestVersion);
        _loadedTrackKey = trackKey;
        _loadedDurationSeconds = durationSeconds;
        _currentLines = [];
        StatusMessage = "Mencari lirik lagu...";
        LyricsUpdated?.Invoke();

        // Ignore very long non-music videos (podcasts, livestreams, 25+ min videos) so we don't match random song lyrics
        if (durationSeconds > 1200)
        {
            if (reqId != Volatile.Read(ref _requestVersion)) return;
            StatusMessage = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanTitle} • {cleanArtist}";
            LyricsUpdated?.Invoke();
            return;
        }

        // Build smart candidate pairs (handles YouTube's "Artist - Song Title" inside Title when Artist is uploader channel)
        var queryPairs = BuildCandidateQueries(cleanTitle, cleanArtist);
        var (primaryTitle, primaryArtist) = queryPairs[0];

        string durBucket = durationSeconds > 15 ? $"|{Math.Round(durationSeconds / 5.0) * 5}" : string.Empty;
        string cacheFile = GetCacheFilePath($"{trackKey}{durBucket}");

        // 1. Check local disk cache first
        if (File.Exists(cacheFile))
        {
            try
            {
                string cachedLrc = await File.ReadAllTextAsync(cacheFile);
                if (reqId != Volatile.Read(ref _requestVersion)) return;

                if (!string.IsNullOrWhiteSpace(cachedLrc))
                {
                    var parsed = ParseLrc(cachedLrc);
                    if (parsed.Count > 0 && IsTimelineCompatibleWithDuration(parsed, durationSeconds))
                    {
                        _currentLines = parsed;
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
            foreach (var (qTitle, qArtist) in queryPairs)
            {
                string? localLrc = await FetchFromLocalFilesAsync(qTitle, qArtist);
                if (reqId != Volatile.Read(ref _requestVersion)) return;

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
        }
        catch { }

        // 3. Provider 1: LRCLIB with strict title, artist, and duration verification
        string? verifiedPlain = null;
        try
        {
            var (syncedLrc, plainLrc) = await FetchVerifiedFromLrcLibAsync(queryPairs, cleanTitle, cleanArtist, album, durationSeconds);
            if (reqId != Volatile.Read(ref _requestVersion)) return;

            if (!string.IsNullOrWhiteSpace(syncedLrc))
            {
                var lines = ParseLrc(syncedLrc);
                if (lines.Count > 0 && IsTimelineCompatibleWithDuration(lines, durationSeconds))
                {
                    _currentLines = lines;
                    StatusMessage = "Lirik sinkron (LRCLIB)";
                    await TrySaveCacheAsync(cacheFile, syncedLrc);
                    LyricsUpdated?.Invoke();
                    return;
                }
            }
            verifiedPlain = plainLrc;
        }
        catch { }

        // 4. Provider 2: Lyrics.ovh API (strict exact artist/title path match)
        try
        {
            foreach (var (qTitle, qArtist) in queryPairs.Take(2))
            {
                if (string.IsNullOrWhiteSpace(qArtist) || qTitle.Length <= 1) continue;

                string? ovhLyrics = await FetchFromLyricsOvhAsync(qTitle, qArtist);
                if (reqId != Volatile.Read(ref _requestVersion)) return;

                if (!string.IsNullOrWhiteSpace(ovhLyrics))
                {
                    var timedLines = ConvertPlainLyricsToTimedLines(ovhLyrics, durationSeconds);
                    if (timedLines.Count > 0)
                    {
                        _currentLines = timedLines;
                        StatusMessage = "Lirik lagu (Lyrics.ovh)";
                        if (durationSeconds > 15)
                        {
                            await TrySaveCacheAsync(cacheFile, FormatAsLrc(timedLines));
                        }
                        LyricsUpdated?.Invoke();
                        return;
                    }
                }
            }
        }
        catch { }

        // 5. Fallback to verified LRCLIB plain lyrics if available
        if (!string.IsNullOrWhiteSpace(verifiedPlain))
        {
            if (reqId != Volatile.Read(ref _requestVersion)) return;

            var timedLines = ConvertPlainLyricsToTimedLines(verifiedPlain, durationSeconds);
            if (timedLines.Count > 0)
            {
                _currentLines = timedLines;
                StatusMessage = "Lirik lagu (LRCLIB)";
                if (durationSeconds > 15)
                {
                    await TrySaveCacheAsync(cacheFile, FormatAsLrc(timedLines));
                }
                LyricsUpdated?.Invoke();
                return;
            }
        }

        if (reqId != Volatile.Read(ref _requestVersion)) return;
        StatusMessage = string.IsNullOrWhiteSpace(primaryArtist)
            ? primaryTitle
            : $"{primaryTitle} • {primaryArtist}";
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
                NextLine: "Lirik sinkron belum tersedia untuk media ini",
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

    private static List<(string Title, string Artist)> BuildCandidateQueries(string cleanTitle, string cleanArtist)
    {
        var list = new List<(string Title, string Artist)>();

        void AddUnique(string t, string a)
        {
            t = t.Trim();
            a = a.Trim();
            if (string.IsNullOrWhiteSpace(t)) return;
            if (a.Equals("Aplikasi Desktop / Browser", StringComparison.OrdinalIgnoreCase)) a = string.Empty;

            foreach (var existing in list)
            {
                if (string.Equals(existing.Title, t, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Artist, a, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            list.Add((t, a));
        }

        // Check if YouTube video title embeds "Artist - Song Title" (or "Song Title - Artist")
        var dashMatch = ArtistTitleSplitRegex().Match(cleanTitle);
        if (dashMatch.Success)
        {
            string left = CleanArtistName(dashMatch.Groups[1].Value);
            string right = CleanTrackTitle(dashMatch.Groups[2].Value);

            if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
            {
                // If YouTube channel name matches right side, it's "Song - Artist"; otherwise standard "Artist - Song"
                if (!string.IsNullOrWhiteSpace(cleanArtist) &&
                    NormalizeAlphaNum(cleanArtist).Contains(NormalizeAlphaNum(right), StringComparison.Ordinal) &&
                    NormalizeAlphaNum(right).Length >= 3)
                {
                    AddUnique(left, right);
                    AddUnique(right, left);
                }
                else
                {
                    AddUnique(right, left);
                    if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.Equals(cleanArtist, left, StringComparison.OrdinalIgnoreCase))
                    {
                        AddUnique(right, cleanArtist);
                    }
                    AddUnique(left, right);
                }
            }
        }

        AddUnique(cleanTitle, cleanArtist);

        if (list.Count == 0)
        {
            list.Add((cleanTitle, cleanArtist));
        }

        return list;
    }

    private static async Task<(string? SyncedLrc, string? PlainLyrics)> FetchVerifiedFromLrcLibAsync(
        List<(string Title, string Artist)> queryPairs,
        string rawCleanTitle,
        string rawCleanArtist,
        string album,
        double durationSeconds)
    {
        try
        {
            // Step A: Try exact /api/get for top candidate pairs
            foreach (var (qTitle, qArtist) in queryPairs.Take(2))
            {
                if (string.IsNullOrWhiteSpace(qArtist)) continue;

                var urlBuilder = new StringBuilder("https://lrclib.net/api/get?");
                urlBuilder.Append($"track_name={Uri.EscapeDataString(qTitle)}");
                urlBuilder.Append($"&artist_name={Uri.EscapeDataString(qArtist)}");
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
                    var root = doc.RootElement;

                    double itemDur = root.TryGetProperty("duration", out var dp) && dp.TryGetDouble(out double dVal) ? dVal : 0;
                    if (durationSeconds > 25 && itemDur > 0 && Math.Abs(itemDur - durationSeconds) > 28)
                    {
                        // Duration differs too much; don't accept blindly
                        continue;
                    }

                    string? synced = root.TryGetProperty("syncedLyrics", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                    string? plain = root.TryGetProperty("plainLyrics", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(synced) || !string.IsNullOrWhiteSpace(plain))
                    {
                        return (synced, plain);
                    }
                }
            }

            // Step B: Search /api/search and score every candidate strictly (NEVER blindly return index 0!)
            string? bestSynced = null;
            int bestSyncedScore = -1000;
            string? bestPlain = null;
            int bestPlainScore = -1000;

            foreach (var (qTitle, qArtist) in queryPairs.Take(2))
            {
                string query = string.IsNullOrWhiteSpace(qArtist) ? qTitle : $"{qArtist} {qTitle}";
                string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
                using var searchResp = await Http.GetAsync(searchUrl);
                if (!searchResp.IsSuccessStatusCode) continue;

                string searchJson = await searchResp.Content.ReadAsStringAsync();
                using var searchDoc = JsonDocument.Parse(searchJson);
                if (searchDoc.RootElement.ValueKind != JsonValueKind.Array) continue;

                int inspected = 0;
                foreach (var item in searchDoc.RootElement.EnumerateArray())
                {
                    if (inspected++ >= 20) break;

                    string itemTrack = item.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() ?? "" : "";
                    string itemArtist = item.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() ?? "" : "";
                    double itemDur = item.TryGetProperty("duration", out var dp) && dp.TryGetDouble(out double dVal) ? dVal : 0;
                    bool isInstrumental = item.TryGetProperty("instrumental", out var ip) && ip.ValueKind == JsonValueKind.True;
                    if (isInstrumental) continue;

                    string? synced = item.TryGetProperty("syncedLyrics", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                    string? plain = item.TryGetProperty("plainLyrics", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;

                    int baseScore = ScoreSearchCandidate(
                        itemTrack,
                        itemArtist,
                        itemDur,
                        qTitle,
                        qArtist,
                        rawCleanTitle,
                        rawCleanArtist,
                        durationSeconds);

                    if (baseScore < 55) continue;

                    if (!string.IsNullOrWhiteSpace(synced) && baseScore + 15 > bestSyncedScore)
                    {
                        bestSyncedScore = baseScore + 15;
                        bestSynced = synced;
                    }

                    if (!string.IsNullOrWhiteSpace(plain) && baseScore > bestPlainScore)
                    {
                        bestPlainScore = baseScore;
                        bestPlain = plain;
                    }
                }

                if (bestSynced != null && bestSyncedScore >= 85)
                {
                    break;
                }
            }

            if (bestSynced != null)
            {
                return (bestSynced, bestPlain);
            }

            if (bestPlain != null)
            {
                return (null, bestPlain);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("LyricsService", "LRCLIB verified fetch failed", ex);
        }

        return (null, null);
    }

    private static int ScoreSearchCandidate(
        string itemTrack,
        string itemArtist,
        double itemDuration,
        string queryTitle,
        string queryArtist,
        string rawTitle,
        string rawArtist,
        double targetDuration)
    {
        string normItemTrack = NormalizeAlphaNum(CleanTrackTitle(itemTrack));
        string normQueryTitle = NormalizeAlphaNum(queryTitle);
        string normRawTitle = NormalizeAlphaNum(rawTitle);

        if (string.IsNullOrWhiteSpace(normItemTrack) || string.IsNullOrWhiteSpace(normQueryTitle))
            return -1000;

        // 1. Duration Gate: reject songs whose duration differs wildly from the playing media
        int durationBonus = 0;
        if (targetDuration > 25 && itemDuration > 0)
        {
            double diff = Math.Abs(itemDuration - targetDuration);
            if (diff > 35) return -1000;
            if (diff <= 4) durationBonus = 38;
            else if (diff <= 12) durationBonus = 24;
            else if (diff <= 22) durationBonus = 8;
            else durationBonus = -12;
        }

        // 2. Title Similarity Gate (Mandatory!)
        int titleScore = 0;
        bool isVeryShortTitle = normQueryTitle.Length <= 3;

        if (normItemTrack == normQueryTitle)
        {
            titleScore = 65;
        }
        else if (isVeryShortTitle)
        {
            // Short titles like "L" MUST match exact normalized title, never partial substring!
            return -1000;
        }
        else
        {
            double overlap = ComputeTokenOverlap(normItemTrack, normQueryTitle);
            if (overlap >= 0.85)
            {
                titleScore = 55;
            }
            else if (overlap >= 0.65 && (normItemTrack.Contains(normQueryTitle, StringComparison.Ordinal) || normQueryTitle.Contains(normItemTrack, StringComparison.Ordinal)))
            {
                titleScore = 42;
            }
            else
            {
                return -1000;
            }
        }

        // 3. Artist Verification
        string normItemArtist = NormalizeAlphaNum(CleanArtistName(itemArtist));
        string normQueryArtist = NormalizeAlphaNum(queryArtist);
        string normRawArtist = NormalizeAlphaNum(rawArtist);

        bool artistMatches = false;
        if (!string.IsNullOrWhiteSpace(normItemArtist))
        {
            if (!string.IsNullOrWhiteSpace(normQueryArtist) &&
                (normItemArtist.Contains(normQueryArtist, StringComparison.Ordinal) ||
                 normQueryArtist.Contains(normItemArtist, StringComparison.Ordinal) ||
                 ComputeTokenOverlap(normItemArtist, normQueryArtist) >= 0.5))
            {
                artistMatches = true;
            }
            else if (!string.IsNullOrWhiteSpace(normRawArtist) &&
                     (normItemArtist.Contains(normRawArtist, StringComparison.Ordinal) ||
                      normRawArtist.Contains(normItemArtist, StringComparison.Ordinal) ||
                      ComputeTokenOverlap(normItemArtist, normRawArtist) >= 0.5))
            {
                artistMatches = true;
            }
            else if (normItemArtist.Length >= 3 && normRawTitle.Contains(normItemArtist, StringComparison.Ordinal))
            {
                // Artist name appears directly inside the YouTube video title
                artistMatches = true;
            }
        }

        if (isVeryShortTitle && !artistMatches)
        {
            // For ultra-short titles like "L", artist MUST also match so we never pick another artist's song "L"
            return -1000;
        }

        int artistScore = artistMatches
            ? 45
            : (string.IsNullOrWhiteSpace(normQueryArtist) ? 0 : -28);

        return titleScore + artistScore + durationBonus;
    }

    private static double ComputeTokenOverlap(string a, string b)
    {
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return 0.0;

        var setA = new HashSet<string>(tokensA, StringComparer.Ordinal);
        var setB = new HashSet<string>(tokensB, StringComparer.Ordinal);

        int common = 0;
        foreach (string t in setA)
        {
            if (setB.Contains(t)) common++;
        }

        int maxCount = Math.Max(setA.Count, setB.Count);
        return (double)common / maxCount;
    }

    private static string NormalizeAlphaNum(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        bool prevSpace = true;

        foreach (char c in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsTimelineCompatibleWithDuration(List<LyricLine> lines, double durationSeconds)
    {
        if (durationSeconds <= 25 || lines.Count == 0) return true;
        // Last lyric line shouldn't appear way past the actual video/song duration
        double lastLineSec = lines[^1].Timestamp.TotalSeconds;
        return lastLineSec <= durationSeconds + 25.0;
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
                string safeTitle = SanitizeFilePattern(title);
                string safeArtist = SanitizeFilePattern(artist);
                if (string.IsNullOrWhiteSpace(safeTitle)) return null;

                List<string> patterns = [];
                if (!string.IsNullOrWhiteSpace(safeArtist))
                {
                    patterns.Add($"{safeArtist} - {safeTitle}.lrc");
                    patterns.Add($"{safeTitle} - {safeArtist}.lrc");
                }
                patterns.Add($"{safeTitle}.lrc");

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

    private static string SanitizeFilePattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '*' or '?' || Array.IndexOf(invalid, c) >= 0) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
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

        string s = LeadingNotifCountRegex().Replace(rawTitle.Trim(), string.Empty);
        s = PipeSuffixRegex().Replace(s, string.Empty);
        s = TitleNoiseRegex().Replace(s, string.Empty);
        s = HashtagNoiseRegex().Replace(s, string.Empty).Trim();
        s = s.Trim(' ', '-', '–', '—', '|', '"', '“', '”');

        return string.IsNullOrWhiteSpace(s) ? rawTitle.Trim() : s;
    }

    private static string CleanArtistName(string? rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist)) return string.Empty;

        string s = rawArtist
            .Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase)
            .Replace("VEVO", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Official Channel", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return string.IsNullOrWhiteSpace(s) ? rawArtist.Trim() : s;
    }

    private string GetCacheFilePath(string trackKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(trackKey));
        string hex = Convert.ToHexString(hash)[..24].ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{hex}.lrc");
    }

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{2})\.(\d{2,3})\](.*)$")]
    private static partial Regex LrcLineRegex();

    [GeneratedRegex(@"^\(\d+\)\s*")]
    private static partial Regex LeadingNotifCountRegex();

    [GeneratedRegex(@"\s*\|.*$")]
    private static partial Regex PipeSuffixRegex();

    [GeneratedRegex(@"\s*#\S+")]
    private static partial Regex HashtagNoiseRegex();

    [GeneratedRegex(@"^(.+?)\s+[-–—]\s+(.+)$")]
    private static partial Regex ArtistTitleSplitRegex();

    [GeneratedRegex(@"\s*(\((Official|Lyric|Lyrics|Lirik|Music|Audio|HD|4K|MV|M/V|Visualizer|Video|Live|Acoustic|Karaoke|Speed\s*Up|Slowed)[^)]*\)|\[(Official|Lyric|Lyrics|Lirik|Music|Audio|HD|4K|MV|M/V|Visualizer|Video|Live|Acoustic|Karaoke|Speed\s*Up|Slowed)[^\]]*\]|-\s*YouTube(\s*Music)?.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoiseRegex();
}
