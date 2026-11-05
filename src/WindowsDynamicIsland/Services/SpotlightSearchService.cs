using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public enum SpotlightResultKind
{
    Application,
    MathResult,
    SystemCommand,
    WebUrl,
    ClipboardItem
}

public sealed class SpotlightItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public SpotlightResultKind Kind { get; init; } = SpotlightResultKind.Application;
    public ImageSource? IconImage { get; set; }
    public string? VectorIconKey { get; init; }
    public string ActionData { get; init; } = string.Empty;
    public Action? ExecuteAction { get; set; }
}

public sealed class SpotlightSearchService
{
    private readonly AppShortcutService _shortcutService;
    private readonly AppSettingsService _settingsService;
    private readonly ClipboardHistoryService? _clipboardHistoryService;
    private readonly List<SpotlightItem> _cachedAppItems = new();
    private DateTime _lastAppScanUtc = DateTime.MinValue;

    public SpotlightSearchService(
        AppShortcutService shortcutService,
        AppSettingsService settingsService,
        ClipboardHistoryService? clipboardHistoryService = null)
    {
        _shortcutService = shortcutService;
        _settingsService = settingsService;
        _clipboardHistoryService = clipboardHistoryService;
        _shortcutService.ShortcutsChanged += InvalidateAppCache;
        RefreshAppIndexAsync();
    }

    public void InvalidateAppCache()
    {
        _lastAppScanUtc = DateTime.MinValue;
    }

    public async void RefreshAppIndexAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var list = new List<SpotlightItem>();

                // 1. Items from configured App Shortcuts (high priority)
                foreach (var sc in _shortcutService.Items)
                {
                    list.Add(new SpotlightItem
                    {
                        Title = sc.Name,
                        Subtitle = "Aplikasi Pintasan",
                        Kind = SpotlightResultKind.Application,
                        IconImage = sc.IconImage,
                        ActionData = sc.TargetPath,
                        ExecuteAction = () => _shortcutService.LaunchShortcut(sc)
                    });
                }

                // 2. Scan Common Start Menu Programs (.lnk)
                var startDirs = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
                };

                var seenNames = new HashSet<string>(list.Select(i => i.Title), StringComparer.OrdinalIgnoreCase);

                foreach (var dir in startDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    try
                    {
                        var files = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories);
                        foreach (var f in files)
                        {
                            string fileName = Path.GetFileNameWithoutExtension(f);
                            if (seenNames.Contains(fileName) ||
                                fileName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                                fileName.Contains("Help", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            seenNames.Add(fileName);
                            ImageSource? icon = ExtractLnkIcon(f);

                            list.Add(new SpotlightItem
                            {
                                Title = fileName,
                                Subtitle = "Aplikasi Windows",
                                Kind = SpotlightResultKind.Application,
                                IconImage = icon,
                                ActionData = f,
                                ExecuteAction = () =>
                                {
                                    try
                                    {
                                        Process.Start(new ProcessStartInfo
                                        {
                                            FileName = f,
                                            UseShellExecute = true
                                        });
                                    }
                                    catch { }
                                }
                            });
                        }
                    }
                    catch { }
                }

                lock (_cachedAppItems)
                {
                    _cachedAppItems.Clear();
                    _cachedAppItems.AddRange(list);
                    _lastAppScanUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SpotlightSearchService.RefreshAppIndexAsync", "Failed indexing applications", ex);
            }
        });
    }

    private static ImageSource? ExtractLnkIcon(string path)
    {
        try
        {
            var shfi = new Win32Interop.SHFILEINFO();
            IntPtr res = Win32Interop.SHGetFileInfo(
                path,
                0,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_SMALLICON);

            if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
                finally
                {
                    Win32Interop.DestroyIcon(shfi.hIcon);
                }
            }
        }
        catch { }
        return null;
    }

    public List<SpotlightItem> Search(
        string rawQuery,
        Action toggleMuteAudio,
        Action toggleMuteMic,
        Action toggleBluetooth,
        Action openSettings,
        Action tuckIsland,
        Action startPomodoroFocus,
        Action? sleepSystem = null,
        Action? hibernateSystem = null,
        Action? openClipboardHistory = null)
    {
        string q = (rawQuery ?? string.Empty).Trim();
        var results = new List<SpotlightItem>();

        if (string.IsNullOrWhiteSpace(q))
        {
            // Empty query: Show smart quick commands & top apps
            results.AddRange(GetDefaultQuickCommands(toggleMuteAudio, toggleMuteMic, toggleBluetooth, openSettings, tuckIsland, startPomodoroFocus, sleepSystem, hibernateSystem, openClipboardHistory));
            lock (_cachedAppItems)
            {
                results.AddRange(_cachedAppItems.Take(4));
            }
            return results.Take(5).ToList();
        }

        // 1. Math calculation check (e.g. "25*4", "100/5", "12+48", "15% of 200", "sqrt(144)")
        if (TryEvaluateMath(q, out double mathResult, out string mathExpr))
        {
            string formattedRes = mathResult % 1 == 0
                ? mathResult.ToString("N0", CultureInfo.InvariantCulture)
                : mathResult.ToString("G6", CultureInfo.InvariantCulture);

            results.Add(new SpotlightItem
            {
                Title = $"= {formattedRes}",
                Subtitle = $"Hasil perhitungan: {mathExpr} (Tekan Enter untuk salin)",
                Kind = SpotlightResultKind.MathResult,
                VectorIconKey = "GeomCalculator",
                ActionData = formattedRes,
                ExecuteAction = () =>
                {
                    try
                    {
                        Clipboard.SetText(formattedRes);
                    }
                    catch { }
                }
            });
        }

        // 2. System Commands Match
        var commands = GetDefaultQuickCommands(toggleMuteAudio, toggleMuteMic, toggleBluetooth, openSettings, tuckIsland, startPomodoroFocus, sleepSystem, hibernateSystem, openClipboardHistory);
        foreach (var cmd in commands)
        {
            if (cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.ActionData.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(cmd);
            }
        }

        // 3. Application Search
        List<SpotlightItem> snapshot;
        lock (_cachedAppItems)
        {
            snapshot = _cachedAppItems.ToList();
        }

        var appMatches = new List<(SpotlightItem Item, int Score)>();
        foreach (var app in snapshot)
        {
            int score = CalculateMatchScore(app.Title, q);
            if (score > 0)
            {
                appMatches.Add((app, score));
            }
        }

        foreach (var m in appMatches.OrderByDescending(x => x.Score).Take(5))
        {
            results.Add(m.Item);
        }

        // 4. Clipboard History Match (Item 4 in ide.md)
        if (_clipboardHistoryService != null && _clipboardHistoryService.Items.Count > 0)
        {
            bool isExplicitClip = q.Equals("clip", StringComparison.OrdinalIgnoreCase) ||
                                  q.StartsWith("clip ", StringComparison.OrdinalIgnoreCase) ||
                                  q.Equals("klip", StringComparison.OrdinalIgnoreCase) ||
                                  q.StartsWith("klip ", StringComparison.OrdinalIgnoreCase) ||
                                  q.Equals("salin", StringComparison.OrdinalIgnoreCase) ||
                                  q.Equals("cp", StringComparison.OrdinalIgnoreCase);

            string filterQuery = isExplicitClip && q.Contains(' ') ? q[(q.IndexOf(' ') + 1)..].Trim() : (isExplicitClip ? string.Empty : q);

            var clipMatches = new List<ClipboardItem>();
            foreach (var clip in _clipboardHistoryService.Items)
            {
                if (isExplicitClip && string.IsNullOrEmpty(filterQuery))
                {
                    clipMatches.Add(clip);
                }
                else if (!string.IsNullOrEmpty(filterQuery) &&
                         (clip.Content.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
                          clip.PreviewText.Contains(filterQuery, StringComparison.OrdinalIgnoreCase)))
                {
                    clipMatches.Add(clip);
                }
            }

            int takeCount = isExplicitClip ? 6 : 2;
            foreach (var clip in clipMatches.Take(takeCount))
            {
                results.Add(new SpotlightItem
                {
                    Title = clip.PreviewText,
                    Subtitle = $"Papan Klip • {clip.BadgeLabel} • {clip.TimeAgoText}",
                    Kind = SpotlightResultKind.ClipboardItem,
                    VectorIconKey = "GeomClipboard",
                    ActionData = clip.Content,
                    IconImage = clip.ThumbnailImage,
                    ExecuteAction = () =>
                    {
                        _clipboardHistoryService.CopyToClipboard(clip);
                    }
                });
            }
        }

        // 4. Web URL / Google Search Fallback if no exact app matches or explicit query
        if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            q.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ||
            q.EndsWith(".id", StringComparison.OrdinalIgnoreCase) ||
            q.EndsWith(".org", StringComparison.OrdinalIgnoreCase) ||
            q.EndsWith(".net", StringComparison.OrdinalIgnoreCase))
        {
            string url = q.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? q : $"https://{q}";
            results.Add(new SpotlightItem
            {
                Title = $"Buka {q}",
                Subtitle = "Kunjungi website di browser default",
                Kind = SpotlightResultKind.WebUrl,
                VectorIconKey = "GeomGlobe",
                ActionData = url,
                ExecuteAction = () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            });
        }
        else if (results.Count < 5)
        {
            string searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(q)}";
            results.Add(new SpotlightItem
            {
                Title = $"Cari di Google: \"{q}\"",
                Subtitle = "Buka pencarian web di browser default",
                Kind = SpotlightResultKind.WebUrl,
                VectorIconKey = "GeomGlobe",
                ActionData = searchUrl,
                ExecuteAction = () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = searchUrl,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            });
        }

        return results.Take(5).ToList();
    }

    private static int CalculateMatchScore(string target, string query)
    {
        if (target.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (target.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;

        // Acronym match: e.g. "VS Code" -> "vsc"
        var words = target.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            string initials = new string(words.Select(w => w[0]).ToArray());
            if (initials.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 70;
        }

        int idx = target.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 50 - Math.Min(idx, 30);

        return 0;
    }

    private static List<SpotlightItem> GetDefaultQuickCommands(
        Action toggleMuteAudio,
        Action toggleMuteMic,
        Action toggleBluetooth,
        Action openSettings,
        Action tuckIsland,
        Action startPomodoroFocus,
        Action? sleepSystem = null,
        Action? hibernateSystem = null,
        Action? openClipboardHistory = null)
    {
        var list = new List<SpotlightItem>
        {
            new()
            {
                Title = "Pengaturan Dynamic Island",
                Subtitle = "Buka preferensi tampilan, tema & sistem",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomSettingsGear",
                ActionData = "setting settings opsi preferensi",
                ExecuteAction = openSettings
            },
            new()
            {
                Title = "Mute / Unmute Audio",
                Subtitle = "Beralih bisukan speaker / headphone",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomSpeakerOn",
                ActionData = "mute sound suara speaker audio hening",
                ExecuteAction = toggleMuteAudio
            },
            new()
            {
                Title = "Mute / Unmute Mikrofon",
                Subtitle = "Beralih bisukan input mikrofon",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomMicOn",
                ActionData = "mic mikrofon mute",
                ExecuteAction = toggleMuteMic
            },
            new()
            {
                Title = "Bluetooth Radio ON/OFF",
                Subtitle = "Beralih sakelar radio Bluetooth",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomBluetooth",
                ActionData = "bluetooth bt radio pair",
                ExecuteAction = toggleBluetooth
            },
            new()
            {
                Title = "Mulai Fokus Pomodoro",
                Subtitle = "Mulai sesi fokus 25 menit",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomFocusTimer",
                ActionData = "pomo pomodoro fokus waktu kerja timer",
                ExecuteAction = startPomodoroFocus
            },
            new()
            {
                Title = "Simpan Dynamic Island",
                Subtitle = "Lipat island ke bibir atas layar",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomChevronUp",
                ActionData = "simpan tuck hide lipat sembunyikan",
                ExecuteAction = tuckIsland
            },
            new()
            {
                Title = "Kunci Komputer (Lock Workstation)",
                Subtitle = "Kunci sesi Windows seketika (Win + L)",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomShieldCheck",
                ActionData = "lock kunci logout",
                ExecuteAction = () =>
                {
                    try { Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); } catch { }
                }
            },
            new()
            {
                Title = "Task Manager",
                Subtitle = "Buka pengelola proses Windows",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomCpuPulse",
                ActionData = "taskmgr task manager performa proses",
                ExecuteAction = () =>
                {
                    try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
                }
            }
        };

        if (sleepSystem != null)
        {
            list.Add(new SpotlightItem
            {
                Title = "Mode Tidur (Sleep)",
                Subtitle = "Masuk ke mode tidur / stand-by untuk menghemat daya",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomMoon",
                ActionData = "sleep tidur standby daya suspend",
                ExecuteAction = sleepSystem
            });
        }

        if (hibernateSystem != null)
        {
            list.Add(new SpotlightItem
            {
                Title = "Hibernasi (Hibernate)",
                Subtitle = "Simpan sesi ke penyimpanan dan matikan komputer",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomPower",
                ActionData = "hibernate hibernasi daya hemat",
                ExecuteAction = hibernateSystem
            });
        }

        if (openClipboardHistory != null)
        {
            list.Add(new SpotlightItem
            {
                Title = "Riwayat Papan Klip (Clipboard)",
                Subtitle = "Lihat riwayat cuplikan teks, tautan & gambar yang baru disalin",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomClipboard",
                ActionData = "clipboard clip klip salin copy riwayat papan",
                ExecuteAction = openClipboardHistory
            });
        }

        return list;
    }

    private static bool TryEvaluateMath(string input, out double result, out string expr)
    {
        result = 0;
        expr = string.Empty;

        // Clean input
        string sanitized = input.Replace("x", "*", StringComparison.OrdinalIgnoreCase)
                                .Replace("X", "*")
                                .Replace(":", "/")
                                .Replace(",", ".")
                                .Trim();

        // Must contain at least one math operator or function
        if (!Regex.IsMatch(sanitized, @"[\+\-\*/\^%]|sqrt|pi"))
        {
            return false;
        }

        // Avoid pure text
        if (Regex.IsMatch(sanitized, @"[a-zA-Z]{3,}") && !sanitized.Contains("sqrt") && !sanitized.Contains("pi"))
        {
            return false;
        }

        try
        {
            // Handle "15% of 200" or "15% * 200"
            sanitized = Regex.Replace(sanitized, @"(\d+(\.\d+)?)%\s*(of|\*)?\s*(\d+(\.\d+)?)", m =>
            {
                double pct = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0;
                double val = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                return (pct * val).ToString(CultureInfo.InvariantCulture);
            });

            // Handle sqrt(144)
            sanitized = Regex.Replace(sanitized, @"sqrt\(([^)]+)\)", m =>
            {
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) && val >= 0)
                {
                    return Math.Sqrt(val).ToString(CultureInfo.InvariantCulture);
                }
                return m.Value;
            });

            sanitized = sanitized.Replace("pi", Math.PI.ToString(CultureInfo.InvariantCulture));

            // Use DataTable compute for standard arithmetic
            var table = new System.Data.DataTable();
            var valObj = table.Compute(sanitized, null);
            if (valObj != null && double.TryParse(valObj.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double res) && !double.IsInfinity(res) && !double.IsNaN(res))
            {
                result = Math.Round(res, 6);
                expr = input;
                return true;
            }
        }
        catch { }

        return false;
    }
}

