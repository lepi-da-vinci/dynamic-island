using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
    string DownloadZipUrl = "",
    string Sha256AssetUrl = "",
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
            _ =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CheckForUpdatesAsync(isBackgroundCheck: true);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogError("UpdateChecker", "Background update check timer error", ex);
                    }
                });
            },
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

            string zipUrl = string.Empty;
            string shaUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    string dlUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(zipUrl))
                    {
                        zipUrl = dlUrl;
                    }
                    else if (assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(shaUrl))
                    {
                        shaUrl = dlUrl;
                    }
                }
            }

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
                DownloadZipUrl: zipUrl,
                Sha256AssetUrl: shaUrl,
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

    public bool IsUpdating { get; private set; }

    public async Task<bool> DownloadAndInstallUpdateAsync(Action<int, string>? onProgress = null)
    {
        if (IsUpdating) return false;
        IsUpdating = true;

        try
        {
            if (string.IsNullOrWhiteSpace(LastResult.DownloadZipUrl))
            {
                await CheckForUpdatesAsync(isBackgroundCheck: false);
            }

            if (string.IsNullOrWhiteSpace(LastResult.DownloadZipUrl))
            {
                LaunchOneClickInstaller();
                return true;
            }

            string updatesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsDynamicIsland",
                "Updates");
            Directory.CreateDirectory(updatesRoot);

            string zipPath = Path.Combine(updatesRoot, "update.zip");
            string stageDir = Path.Combine(updatesRoot, "staged");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);

            onProgress?.Invoke(0, LocalizationService.Instance.Format("Updater_Downloading", 0));

            using (var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                dlClient.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowsDynamicIsland/{AppInstallerService.AppVersion}");
                using var response = await dlClient.GetAsync(LastResult.DownloadZipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var netStream = await response.Content.ReadAsStreamAsync();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long readTotal = 0;
                int lastPct = -1;
                int bytesRead;
                while ((bytesRead = await netStream.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
                    readTotal += bytesRead;
                    if (totalBytes > 0)
                    {
                        int pct = (int)Math.Clamp((readTotal * 100L) / totalBytes, 0, 100);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            onProgress?.Invoke(pct, LocalizationService.Instance.Format("Updater_Downloading", pct));
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(LastResult.Sha256AssetUrl))
            {
                using var shaClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                shaClient.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowsDynamicIsland/{AppInstallerService.AppVersion}");
                string expectedContent = await shaClient.GetStringAsync(LastResult.Sha256AssetUrl);
                string expectedHex = expectedContent.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (expectedHex.Length == 64)
                {
                    await using var verifyStream = File.OpenRead(zipPath);
                    byte[] hash = await SHA256.HashDataAsync(verifyStream);
                    string actualHex = Convert.ToHexString(hash);
                    if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("SHA-256 verification failed for downloaded update package.");
                    }
                }
            }

            onProgress?.Invoke(100, LocalizationService.Instance.Get("Updater_Installing"));

            // Validate archive & locate directory containing WindowsDynamicIsland.exe
            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);
            string? exeFile = Directory.GetFiles(stageDir, "WindowsDynamicIsland.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(exeFile))
            {
                throw new InvalidDataException("Downloaded package missing WindowsDynamicIsland.exe.");
            }

            string sourceDir = Path.GetDirectoryName(exeFile)!;
            string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetExe = Path.Combine(installDir, "WindowsDynamicIsland.exe");
            string scriptPath = Path.Combine(updatesRoot, "apply_update.ps1");

            string psScript = $$"""
                $ErrorActionPreference = 'Stop'
                $targetPid = {{Environment.ProcessId}}
                for ($i = 0; $i -lt 40; $i++) {
                    if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 200
                }
                Copy-Item -Path "{{sourceDir}}\*" -Destination "{{installDir}}" -Recurse -Force
                Start-Process -FilePath "{{targetExe}}"
                Remove-Item -Path "{{stageDir}}" -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path "{{zipPath}}" -Force -ErrorAction SilentlyContinue
                """;
            await File.WriteAllTextAsync(scriptPath, psScript, Encoding.UTF8);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("UpdateChecker", "In-app auto-update failed; opening releases page or script", ex);
            return false;
        }
        finally
        {
            IsUpdating = false;
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

