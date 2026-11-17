using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private volatile IReadOnlyList<SpotlightItem> _cachedAppItems = Array.Empty<SpotlightItem>();
    private static readonly HttpClient CurrencyHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static Dictionary<string, double> _usdRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.0,
        ["IDR"] = 15850.0,
        ["EUR"] = 0.92,
        ["GBP"] = 0.79,
        ["JPY"] = 154.0,
        ["SGD"] = 1.34,
        ["MYR"] = 4.45,
        ["AUD"] = 1.52,
        ["KRW"] = 1390.0,
        ["CNY"] = 7.22
    };
    private static DateTime _lastRatesFetchUtc = DateTime.MinValue;
    private static int _fetchingRatesFlag;

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
        RefreshAppIndexAsync();
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
                        Subtitle = LocalizationService.Instance.Get("Spotlight_ShortcutApp"),
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
                                Subtitle = LocalizationService.Instance.Get("Spotlight_WindowsApp"),
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

                // Atomic reference swap so concurrent Search() reads are always lock-free and thread-safe
                _cachedAppItems = list.AsReadOnly();
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
        Action? openClipboardHistory = null,
        Action? launchOcrSnip = null,
        Action? launchColorPicker = null,
        Action? launchRegionSnip = null,
        Action<string>? addTodo = null,
        Action<int>? startCustomTimer = null,
        Action<string, string>? addSchedule = null)
    {
        string q = (rawQuery ?? string.Empty).Trim();
        var results = new List<SpotlightItem>();

        if (string.IsNullOrWhiteSpace(q))
        {
            // Empty query: Show smart quick commands & top apps
            results.AddRange(GetDefaultQuickCommands(toggleMuteAudio, toggleMuteMic, toggleBluetooth, openSettings, tuckIsland, startPomodoroFocus, sleepSystem, hibernateSystem, openClipboardHistory, launchOcrSnip, launchColorPicker, launchRegionSnip));
            results.AddRange(_cachedAppItems.Take(4));
            return results.Take(5).ToList();
        }

        // 0. Quick Action Commands (todo, timer, jadwal/schedule)
        if (TryBuildQuickActionResult(q, addTodo, startCustomTimer, addSchedule, out var quickActionItem))
        {
            results.Add(quickActionItem);
        }

        // 1. Unit & Currency Converter (e.g. "100 km to mi", "32 c to f", "100 usd to idr")
        if (TryEvaluateConversion(q, out string convResult, out string convExplain, out string copyVal))
        {
            results.Add(new SpotlightItem
            {
                Title = $"= {convResult}",
                Subtitle = convExplain,
                Kind = SpotlightResultKind.MathResult,
                VectorIconKey = "GeomCalculator",
                ActionData = copyVal,
                ExecuteAction = () =>
                {
                    try { Clipboard.SetText(copyVal); } catch { }
                }
            });
        }

        // 2. Math calculation check (e.g. "25*4", "100/5", "12+48", "15% of 200", "sqrt(144)")
        if (TryEvaluateMath(q, out double mathResult, out string mathExpr))
        {
            string formattedRes = mathResult % 1 == 0
                ? mathResult.ToString("N0", CultureInfo.InvariantCulture)
                : mathResult.ToString("G6", CultureInfo.InvariantCulture);

            results.Add(new SpotlightItem
            {
                Title = $"= {formattedRes}",
                Subtitle = LocalizationService.Instance.IsEnglish
                    ? $"Calculated: {mathExpr} (Press Enter to copy)"
                    : $"Hasil perhitungan: {mathExpr} (Tekan Enter untuk salin)",
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

        // 3. System Commands Match
        var commands = GetDefaultQuickCommands(toggleMuteAudio, toggleMuteMic, toggleBluetooth, openSettings, tuckIsland, startPomodoroFocus, sleepSystem, hibernateSystem, openClipboardHistory, launchOcrSnip, launchColorPicker, launchRegionSnip);
        foreach (var cmd in commands)
        {
            if (cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.ActionData.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(cmd);
            }
        }

        // 4. Application Search (thread-safe snapshot read)
        var snapshot = _cachedAppItems;

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
                    Subtitle = $"{(LocalizationService.Instance.IsEnglish ? "Clipboard" : "Papan Klip")} • {clip.BadgeLabel} • {clip.TimeAgoText}",
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
                Title = LocalizationService.Instance.IsEnglish ? $"Open {q}" : $"Buka {q}",
                Subtitle = LocalizationService.Instance.IsEnglish ? "Open URL in default browser" : "Kunjungi website di browser default",
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
                Title = LocalizationService.Instance.IsEnglish ? $"Search Google: \"{q}\"" : $"Cari di Google: \"{q}\"",
                Subtitle = LocalizationService.Instance.IsEnglish ? "Open web search in default browser" : "Buka pencarian web di browser default",
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

    internal static int CalculateMatchScore(string target, string query)
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
        Action? openClipboardHistory = null,
        Action? launchOcrSnip = null,
        Action? launchColorPicker = null,
        Action? launchRegionSnip = null)
    {
        bool en = LocalizationService.Instance.IsEnglish;
        var list = new List<SpotlightItem>
        {
            new()
            {
                Title = en ? "Dynamic Island Settings" : "Pengaturan Dynamic Island",
                Subtitle = en ? "Open appearance, theme & system preferences" : "Buka preferensi tampilan, tema & sistem",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomSettingsGear",
                ActionData = "setting settings opsi preferensi",
                ExecuteAction = openSettings
            },
            new()
            {
                Title = en ? "Mute / Unmute Audio" : "Mute / Unmute Audio",
                Subtitle = en ? "Toggle speaker or headphone mute" : "Beralih bisukan speaker / headphone",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomSpeakerOn",
                ActionData = "mute sound suara speaker audio hening",
                ExecuteAction = toggleMuteAudio
            },
            new()
            {
                Title = en ? "Mute / Unmute Microphone" : "Mute / Unmute Mikrofon",
                Subtitle = en ? "Toggle microphone input mute" : "Beralih bisukan input mikrofon",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomMicOn",
                ActionData = "mic mikrofon mute",
                ExecuteAction = toggleMuteMic
            },
            new()
            {
                Title = en ? "Bluetooth Radio ON/OFF" : "Bluetooth Radio ON/OFF",
                Subtitle = en ? "Toggle Bluetooth radio power" : "Beralih sakelar radio Bluetooth",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomBluetooth",
                ActionData = "bluetooth bt radio pair",
                ExecuteAction = toggleBluetooth
            },
            new()
            {
                Title = en ? "Start Pomodoro Focus" : "Mulai Fokus Pomodoro",
                Subtitle = en ? "Start 25-minute focus session" : "Mulai sesi fokus 25 menit",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomFocusTimer",
                ActionData = "pomo pomodoro fokus focus waktu kerja timer",
                ExecuteAction = startPomodoroFocus
            },
            new()
            {
                Title = en ? "Tuck Dynamic Island" : "Simpan Dynamic Island",
                Subtitle = en ? "Collapse island to top edge" : "Lipat island ke bibir atas layar",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomChevronUp",
                ActionData = "simpan tuck hide lipat sembunyikan",
                ExecuteAction = tuckIsland
            },
            new()
            {
                Title = en ? "Lock Workstation" : "Kunci Komputer (Lock Workstation)",
                Subtitle = en ? "Lock Windows session immediately (Win + L)" : "Kunci sesi Windows seketika (Win + L)",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomShield",
                ActionData = "lock kunci logout",
                ExecuteAction = () =>
                {
                    try { Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); } catch { }
                }
            },
            new()
            {
                Title = "Task Manager",
                Subtitle = en ? "Open Windows process manager" : "Buka pengelola proses Windows",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomCpuPulse",
                ActionData = "taskmgr task manager performa proses",
                ExecuteAction = () =>
                {
                    try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
                }
            }
        };

        if (launchOcrSnip != null)
        {
            list.Add(new SpotlightItem
            {
                Title = en ? "OCR Text Snip (Copy Screen Text)" : "OCR Teks Layar (Salin Teks)",
                Subtitle = en ? "Select any screen region to extract & copy text" : "Pilih area layar untuk menyalin teks ke papan klip",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomClipboard",
                ActionData = "ocr snip teks text screenshot copy extract",
                ExecuteAction = launchOcrSnip
            });
        }

        if (launchColorPicker != null)
        {
            list.Add(new SpotlightItem
            {
                Title = en ? "Screen Color Eyedropper (HEX)" : "Ambil Warna Layar (Color Picker HEX)",
                Subtitle = en ? "Inspect pixel under cursor & copy HEX color" : "Ambil kode warna HEX piksel di bawah kursor",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomAppearanceSparkle",
                ActionData = "color colour warna picker eyedropper pipet hex",
                ExecuteAction = launchColorPicker
            });
        }

        if (launchRegionSnip != null)
        {
            list.Add(new SpotlightItem
            {
                Title = en ? "Region Screenshot to Shelf" : "Tangkap Area Layar ke Rak File",
                Subtitle = en ? "Capture rectangular region as PNG into File Shelf & Clipboard" : "Tangkap area layar jadi PNG langsung ke Rak File & Clipboard",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomFolderTray",
                ActionData = "snip screenshot capture tangkap area gambar",
                ExecuteAction = launchRegionSnip
            });
        }

        if (sleepSystem != null)
        {
            list.Add(new SpotlightItem
            {
                Title = en ? "Sleep Mode" : "Mode Tidur (Sleep)",
                Subtitle = en ? "Enter low-power standby immediately" : "Masuk ke mode tidur / stand-by untuk menghemat daya",
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
                Title = en ? "Hibernate" : "Hibernasi (Hibernate)",
                Subtitle = en ? "Save session to disk and power off" : "Simpan sesi ke penyimpanan dan matikan komputer",
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
                Title = en ? "Clipboard History" : "Riwayat Papan Klip (Clipboard)",
                Subtitle = en ? "Browse recently copied text, links & images" : "Lihat riwayat cuplikan teks, tautan & gambar yang baru disalin",
                Kind = SpotlightResultKind.SystemCommand,
                VectorIconKey = "GeomClipboard",
                ActionData = "clipboard clip klip salin copy riwayat papan",
                ExecuteAction = openClipboardHistory
            });
        }

        return list;
    }

    internal static bool TryEvaluateMath(string input, out double result, out string expr)
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

    private static bool TryBuildQuickActionResult(
        string query,
        Action<string>? addTodo,
        Action<int>? startCustomTimer,
        Action<string, string>? addSchedule,
        out SpotlightItem item)
    {
        item = null!;
        bool en = LocalizationService.Instance.IsEnglish;

        if (addTodo != null &&
            (query.StartsWith("todo ", StringComparison.OrdinalIgnoreCase) ||
             query.StartsWith("tugas ", StringComparison.OrdinalIgnoreCase)))
        {
            string taskTitle = query[(query.IndexOf(' ') + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(taskTitle))
            {
                item = new SpotlightItem
                {
                    Title = en ? $"Add To-Do: \"{taskTitle}\"" : $"Tambah Tugas: \"{taskTitle}\"",
                    Subtitle = en ? "Press Enter to add task to Daily To-Do List" : "Tekan Enter untuk menambahkan ke Daftar Tugas",
                    Kind = SpotlightResultKind.SystemCommand,
                    VectorIconKey = "GeomChecklist",
                    ActionData = taskTitle,
                    ExecuteAction = () => addTodo(taskTitle)
                };
                return true;
            }
        }

        if (startCustomTimer != null)
        {
            var timerMatch = Regex.Match(query, @"^(?:timer|pomo|fokus|focus)\s+(\d{1,3})\s*(?:m|min|menit)?$", RegexOptions.IgnoreCase);
            if (timerMatch.Success && int.TryParse(timerMatch.Groups[1].Value, out int mins) && mins is >= 1 and <= 180)
            {
                item = new SpotlightItem
                {
                    Title = en ? $"Start Focus Timer ({mins} min)" : $"Mulai Timer Fokus ({mins} menit)",
                    Subtitle = en ? "Press Enter to start countdown in Dynamic Island" : "Tekan Enter untuk memulai hitungan mundur",
                    Kind = SpotlightResultKind.SystemCommand,
                    VectorIconKey = "GeomFocusTimer",
                    ActionData = $"{mins}",
                    ExecuteAction = () => startCustomTimer(mins)
                };
                return true;
            }
        }

        if (addSchedule != null)
        {
            var schedMatch = Regex.Match(query, @"^(?:jadwal|agenda|schedule)\s+(?:(\d{1,2}[:.]\d{2})\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (schedMatch.Success)
            {
                string timeText = schedMatch.Groups[1].Success ? schedMatch.Groups[1].Value.Replace('.', ':') : DateTime.Now.AddHours(1).ToString("HH:00");
                string titleText = schedMatch.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(titleText))
                {
                    item = new SpotlightItem
                    {
                        Title = en ? $"Add Agenda ({timeText}): {titleText}" : $"Tambah Jadwal ({timeText}): {titleText}",
                        Subtitle = en ? "Press Enter to add to today's Calendar agenda" : "Tekan Enter untuk menambah agenda hari ini",
                        Kind = SpotlightResultKind.SystemCommand,
                        VectorIconKey = "GeomCalendar",
                        ActionData = titleText,
                        ExecuteAction = () => addSchedule(timeText, titleText)
                    };
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool TryEvaluateConversion(string input, out string displayResult, out string explanation, out string copyValue)
    {
        displayResult = string.Empty;
        explanation = string.Empty;
        copyValue = string.Empty;

        var m = Regex.Match(input.Trim(), @"^(-?\d+(?:[.,]\d+)?)\s*([a-zA-Z°]+)\s*(?:to|ke|in|=|->)?\s*([a-zA-Z°]+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            return false;

        string from = m.Groups[2].Value.Trim('°').ToLowerInvariant();
        string to = m.Groups[3].Value.Trim('°').ToLowerInvariant();
        if (from == to) return false;

        bool en = LocalizationService.Instance.IsEnglish;

        // Temperature
        if (from is "c" or "celsius" && to is "f" or "fahrenheit")
        {
            double f = (val * 9.0 / 5.0) + 32.0;
            copyValue = Math.Round(f, 2).ToString(CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} °F";
            explanation = en ? $"{val} °C converted to Fahrenheit" : $"{val} °C dikonversi ke Fahrenheit";
            return true;
        }
        if (from is "f" or "fahrenheit" && to is "c" or "celsius")
        {
            double c = (val - 32.0) * 5.0 / 9.0;
            copyValue = Math.Round(c, 2).ToString(CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} °C";
            explanation = en ? $"{val} °F converted to Celsius" : $"{val} °F dikonversi ke Celsius";
            return true;
        }

        // Length (base: meter)
        double? lengthToMeter = from switch
        {
            "mm" => 0.001,
            "cm" => 0.01,
            "m" => 1.0,
            "km" => 1000.0,
            "in" or "inch" => 0.0254,
            "ft" or "feet" => 0.3048,
            "mi" or "mile" => 1609.344,
            _ => null
        };
        double? meterToTarget = to switch
        {
            "mm" => 1000.0,
            "cm" => 100.0,
            "m" => 1.0,
            "km" => 0.001,
            "in" or "inch" => 1.0 / 0.0254,
            "ft" or "feet" => 1.0 / 0.3048,
            "mi" or "mile" => 1.0 / 1609.344,
            _ => null
        };
        if (lengthToMeter.HasValue && meterToTarget.HasValue)
        {
            double converted = Math.Round(val * lengthToMeter.Value * meterToTarget.Value, 4);
            copyValue = converted.ToString(CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} {to}";
            explanation = $"{val} {from} -> {displayResult}";
            return true;
        }

        // Mass (base: kg)
        double? massToKg = from switch
        {
            "g" or "gram" => 0.001,
            "kg" => 1.0,
            "lb" or "lbs" => 0.45359237,
            "oz" => 0.028349523,
            _ => null
        };
        double? kgToTarget = to switch
        {
            "g" or "gram" => 1000.0,
            "kg" => 1.0,
            "lb" or "lbs" => 1.0 / 0.45359237,
            "oz" => 1.0 / 0.028349523,
            _ => null
        };
        if (massToKg.HasValue && kgToTarget.HasValue)
        {
            double converted = Math.Round(val * massToKg.Value * kgToTarget.Value, 4);
            copyValue = converted.ToString(CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} {to}";
            explanation = $"{val} {from} -> {displayResult}";
            return true;
        }

        // Data size (base: MB, binary-decimal friendly 1024)
        double? dataToMb = from switch
        {
            "kb" => 1.0 / 1024.0,
            "mb" => 1.0,
            "gb" => 1024.0,
            "tb" => 1024.0 * 1024.0,
            _ => null
        };
        double? mbToTarget = to switch
        {
            "kb" => 1024.0,
            "mb" => 1.0,
            "gb" => 1.0 / 1024.0,
            "tb" => 1.0 / (1024.0 * 1024.0),
            _ => null
        };
        if (dataToMb.HasValue && mbToTarget.HasValue)
        {
            double converted = Math.Round(val * dataToMb.Value * mbToTarget.Value, 4);
            copyValue = converted.ToString(CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} {to.ToUpperInvariant()}";
            explanation = $"{val} {from.ToUpperInvariant()} -> {displayResult}";
            return true;
        }

        // Currency conversion (non-blocking cached rates, lazily refreshed in background)
        if (_usdRates.TryGetValue(from, out double fromPerUsd) &&
            _usdRates.TryGetValue(to, out double toPerUsd))
        {
            EnsureCurrencyRatesFreshAsync();
            double inUsd = val / fromPerUsd;
            double converted = inUsd * toPerUsd;
            string code = to.ToUpperInvariant();
            copyValue = converted >= 100
                ? Math.Round(converted, 0).ToString("N0", CultureInfo.InvariantCulture)
                : Math.Round(converted, 2).ToString("N2", CultureInfo.InvariantCulture);
            displayResult = $"{copyValue} {code}";
            explanation = en
                ? $"{val} {from.ToUpperInvariant()} -> {code} (Press Enter to copy)"
                : $"{val} {from.ToUpperInvariant()} -> {code} (Tekan Enter untuk salin)";
            return true;
        }

        return false;
    }

    private static void EnsureCurrencyRatesFreshAsync()
    {
        if ((DateTime.UtcNow - _lastRatesFetchUtc).TotalHours < 6) return;
        if (Interlocked.CompareExchange(ref _fetchingRatesFlag, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var res = await CurrencyHttp.GetAsync("https://open.er-api.com/v6/latest/USD");
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("rates", out var ratesElem))
                    {
                        var updated = new Dictionary<string, double>(_usdRates, StringComparer.OrdinalIgnoreCase);
                        foreach (var key in updated.Keys.ToList())
                        {
                            if (ratesElem.TryGetProperty(key, out var prop) && prop.TryGetDouble(out double rate) && rate > 0)
                            {
                                updated[key] = rate;
                            }
                        }
                        _usdRates = updated;
                        _lastRatesFetchUtc = DateTime.UtcNow;
                    }
                }
            }
            catch { }
            finally
            {
                Volatile.Write(ref _fetchingRatesFlag, 0);
            }
        });
    }
}

