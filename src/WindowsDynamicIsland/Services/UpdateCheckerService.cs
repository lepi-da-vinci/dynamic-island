using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsDynamicIsland.Services;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleaseUrl,
    string PublishedAt,
    bool HasError = false,
    string ErrorMessage = "");

/// <summary>
/// Background service that checks GitHub Releases for new application updates,
/// notifies the user via Dynamic Island OSD Toasts, and updates the System Status Tab.
/// </summary>
public sealed class UpdateCheckerService : IDisposable
{
    private const string RepoOwner = "lepi-da-vinci";
    private const string RepoName = "dynamic-island";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string GitHubReleasesPageUrl = $"https://github.com/lepi-da-vinci/{RepoName}/releases/latest";
    private const string OneClickInstallScriptUrl = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/main/install.ps1";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private System.Threading.Timer? _periodicTimer;
    private bool _hasEmittedInitialToast;

    public UpdateCheckResult LastResult { get; private set; } = new(
        IsUpdateAvailable: false,
        CurrentVersion: AppInstallerService.AppVersion,
        LatestVersion: $"v{AppInstallerService.AppVersion}",
        ReleaseTitle: "Versi Terbaru",
        ReleaseNotes: string.Empty,
        ReleaseUrl: GitHubReleasesPageUrl,
        PublishedAt: string.Empty);

    public bool IsChecking { get; private set; }

    public event Action<UpdateCheckResult>? UpdateStatusChanged;
    public event Action<UpdateCheckResult>? UpdateNotificationTriggered;

    public UpdateCheckerService()
    {
        // First check after 4 seconds (so app startup is instant), then repeat every 4 hours
        _periodicTimer = new System.Threading.Timer(
            async _ => await CheckForUpdatesAsync(isBackgroundCheck: true),
            null,
            TimeSpan.FromSeconds(4),
            TimeSpan.FromHours(4));
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool isBackgroundCheck = false)
    {
        if (IsChecking) return LastResult;
        IsChecking = true;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            request.Headers.Add("User-Agent", "WindowsDynamicIsland-DesktopApp");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LastResult = LastResult with
                {
                    HasError = true,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}"
                };
                UpdateStatusChanged?.Invoke(LastResult);
                return LastResult;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string releaseTitle = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? tagName : tagName;
            string releaseNotes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? GitHubReleasesPageUrl : GitHubReleasesPageUrl;
            string publishedAt = root.TryGetProperty("published_at", out var pubEl) ? pubEl.GetString() ?? "" : "";

            string cleanLatest = CleanVersionString(tagName);
            string cleanCurrent = CleanVersionString(AppInstallerService.AppVersion);

            bool isNewer = CompareVersions(cleanLatest, cleanCurrent) > 0;

            LastResult = new UpdateCheckResult(
                IsUpdateAvailable: isNewer,
                CurrentVersion: $"v{AppInstallerService.AppVersion.TrimStart('v', 'V')}",
                LatestVersion: tagName.StartsWith('v') || tagName.StartsWith('V') ? tagName : $"v{tagName}",
                ReleaseTitle: releaseTitle,
                ReleaseNotes: TruncateNotes(releaseNotes),
                ReleaseUrl: htmlUrl,
                PublishedAt: FormatPublishDate(publishedAt),
                HasError: false,
                ErrorMessage: string.Empty);

            UpdateStatusChanged?.Invoke(LastResult);

            if (isNewer && (!isBackgroundCheck || !_hasEmittedInitialToast))
            {
                _hasEmittedInitialToast = true;
                UpdateNotificationTriggered?.Invoke(LastResult);
            }

            return LastResult;
        }
        catch (Exception ex)
        {
            LastResult = LastResult with
            {
                HasError = true,
                ErrorMessage = ex.Message
            };
            UpdateStatusChanged?.Invoke(LastResult);
            return LastResult;
        }
        finally
        {
            IsChecking = false;
        }
    }

    public static void LaunchOneClickInstaller()
    {
        try
        {
            string psCommand = $"irm {OneClickInstallScriptUrl} | iex";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            });
        }
        catch
        {
            OpenReleasesInBrowser();
        }
    }

    public static void OpenReleasesInBrowser(string? url = null)
    {
        try
        {
            string targetUrl = !string.IsNullOrWhiteSpace(url) ? url : GitHubReleasesPageUrl;
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static string CleanVersionString(string ver)
    {
        string trimmed = ver.Trim().TrimStart('v', 'V');
        var match = Regex.Match(trimmed, @"^(\d+(\.\d+)*)");
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    private static int CompareVersions(string vLatest, string vCurrent)
    {
        if (Version.TryParse(vLatest, out var parsedLatest) &&
            Version.TryParse(vCurrent, out var parsedCurrent))
        {
            return parsedLatest.CompareTo(parsedCurrent);
        }

        string[] pL = vLatest.Split('.');
        string[] pC = vCurrent.Split('.');
        int maxLen = Math.Max(pL.Length, pC.Length);

        for (int i = 0; i < maxLen; i++)
        {
            int nL = i < pL.Length && int.TryParse(pL[i], out int valL) ? valL : 0;
            int nC = i < pC.Length && int.TryParse(pC[i], out int valC) ? valC : 0;
            if (nL != nC)
                return nL.CompareTo(nC);
        }

        return 0;
    }

    private static string FormatPublishDate(string isoDate)
    {
        if (DateTime.TryParse(isoDate, out var dt))
        {
            return dt.ToLocalTime().ToString("dd MMM yyyy");
        }
        return string.Empty;
    }

    private static string TruncateNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        string cleaned = notes.Replace("\r", " ").Replace("\n", " ").Trim();
        return cleaned.Length > 90 ? cleaned[..90] + "..." : cleaned;
    }

    public void Dispose()
    {
        _periodicTimer?.Dispose();
    }
}

