using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Listens for real-time Windows 10/11 system notifications (WhatsApp Desktop, WhatsApp via Phone Link,
/// Instagram, Telegram, Discord, Chrome, Edge, Mail, etc.) by reading the Windows Push Notification
/// SQLite database (wpndatabase.db) via built-in OS winsqlite3.dll in non-blocking read-only mode.
/// Catches both full Toast XML banners and taskbar Badge count increments (even when Focus Assist / DND is active).
/// </summary>
public sealed class NotificationListenerService
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x00000001;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zvfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr db);

    private readonly string _wpnDbPath;
    private readonly byte[] _wpnDbPathBytes;
    private readonly Dictionary<string, int> _lastBadgeCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastToastTimeByApp = new(StringComparer.OrdinalIgnoreCase);

    private long _lastSeenOrder = -1;
    private bool _baselineCaptured;
    private DateTime _lastPollUtc = DateTime.MinValue;

    public event Action<ToastPayload>? NotificationReceived;

    public NotificationListenerService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _wpnDbPath = Path.Combine(localAppData, "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        _wpnDbPathBytes = Encoding.UTF8.GetBytes(_wpnDbPath + "\0");

        CaptureBaseline();
    }

    private void CaptureBaseline()
    {
        if (!File.Exists(_wpnDbPath))
            return;

        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(_wpnDbPathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                return;

            const string maxSql = "SELECT COALESCE(MAX([Order]), 0) FROM Notification;\0";
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(maxSql), -1, out stmt, IntPtr.Zero) == SQLITE_OK)
            {
                if (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    _lastSeenOrder = sqlite3_column_int64(stmt, 0);
                    _baselineCaptured = true;
                }
            }
        }
        catch
        {
            // Ignore if WPN database is temporarily unavailable
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    /// <summary>
    /// Polls wpndatabase.db every ~500ms for newly arrived toast or badge notifications.
    /// </summary>
    public void PollNewNotifications()
    {
        if ((DateTime.UtcNow - _lastPollUtc).TotalMilliseconds < 450)
            return;

        _lastPollUtc = DateTime.UtcNow;

        if (!File.Exists(_wpnDbPath))
            return;

        if (!_baselineCaptured)
        {
            CaptureBaseline();
            return;
        }

        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(_wpnDbPathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                return;

            string query =
                $"SELECT n.[Order], n.Type, h.PrimaryId, CAST(n.Payload AS TEXT) " +
                $"FROM Notification n LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                $"WHERE n.[Order] > {_lastSeenOrder} AND n.Type IN ('toast', 'badge') " +
                $"ORDER BY n.[Order] ASC LIMIT 10;\0";

            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(query), -1, out stmt, IntPtr.Zero) != SQLITE_OK)
                return;

            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                long order = sqlite3_column_int64(stmt, 0);
                string type = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 1)) ?? string.Empty;
                string primaryId = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 2)) ?? string.Empty;
                string payload = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 3)) ?? string.Empty;

                if (order > _lastSeenOrder)
                {
                    _lastSeenOrder = order;
                }

                if (string.IsNullOrWhiteSpace(payload) ||
                    primaryId.Contains("WindowsDynamicIsland", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (appName, icon, accentHex, isMessagingApp) = ResolveAppIdentity(primaryId);

                if (string.Equals(type, "toast", StringComparison.OrdinalIgnoreCase))
                {
                    var (title, body) = ParseToastXml(payload, appName);
                    if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body))
                    {
                        _lastToastTimeByApp[appName] = DateTime.UtcNow;

                        string displayTitle = string.IsNullOrWhiteSpace(title)
                            ? appName
                            : title.Contains(appName, StringComparison.OrdinalIgnoreCase)
                                ? title
                                : $"{appName} • {title}";

                        string displaySubtitle = string.IsNullOrWhiteSpace(body)
                            ? "Notifikasi baru"
                            : body;

                        NotificationReceived?.Invoke(new ToastPayload(
                            Icon: icon,
                            Title: displayTitle,
                            Subtitle: displaySubtitle,
                            AccentHex: accentHex,
                            Duration: TimeSpan.FromSeconds(4.0)));
                    }
                }
                else if (string.Equals(type, "badge", StringComparison.OrdinalIgnoreCase) && isMessagingApp)
                {
                    int badgeVal = ParseBadgeValue(payload);
                    int prevBadge = _lastBadgeCounts.GetValueOrDefault(appName, 0);
                    _lastBadgeCounts[appName] = badgeVal;

                    bool hadRecentToast = _lastToastTimeByApp.TryGetValue(appName, out var lastToast) &&
                                          (DateTime.UtcNow - lastToast).TotalSeconds < 3.0;

                    if (badgeVal > 0 && badgeVal >= prevBadge && !hadRecentToast)
                    {
                        NotificationReceived?.Invoke(new ToastPayload(
                            Icon: icon,
                            Title: $"{appName} • Pesan Masuk",
                            Subtitle: $"{badgeVal} pesan belum dibaca",
                            AccentHex: accentHex,
                            Duration: TimeSpan.FromSeconds(3.8)));
                    }
                }
            }
        }
        catch
        {
            // Ignore transient SQLite lock
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static (string Title, string Body) ParseToastXml(string xml, string fallbackAppName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants("text")
                .Select(t => (t.Value ?? string.Empty).Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (texts.Count == 0)
                return (string.Empty, string.Empty);

            if (texts.Count == 1)
                return (fallbackAppName, SingleLine(texts[0]));

            string title = SingleLine(texts[0]);
            string body = SingleLine(string.Join(" — ", texts.Skip(1)));
            return (title, body);
        }
        catch
        {
            var matches = Regex.Matches(xml, @"<text[^>]*>(.*?)</text>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (matches.Count == 1)
            {
                return (fallbackAppName, SingleLine( StripXmlEntities(matches[0].Groups[1].Value)));
            }
            if (matches.Count >= 2)
            {
                return (
                    SingleLine(StripXmlEntities(matches[0].Groups[1].Value)),
                    SingleLine(StripXmlEntities(matches[1].Groups[1].Value)));
            }
            return (string.Empty, string.Empty);
        }
    }

    private static int ParseBadgeValue(string xml)
    {
        var match = Regex.Match(xml, @"value\s*=\s*[""'](\d+)[""']", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int val))
        {
            return val;
        }
        return 0;
    }

    private static string SingleLine(string input)
    {
        string clean = input.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length > 56 ? clean[..56] + "…" : clean;
    }

    private static string StripXmlEntities(string input)
    {
        return input
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    private static (string AppName, string Icon, string AccentHex, bool IsMessagingApp) ResolveAppIdentity(string primaryId)
    {
        if (primaryId.Contains("whatsapp", StringComparison.OrdinalIgnoreCase))
            return ("WhatsApp", "CHAT", "#25D366", true);

        if (primaryId.Contains("instagram", StringComparison.OrdinalIgnoreCase))
            return ("Instagram", "CHAT", "#E1306C", true);

        if (primaryId.Contains("telegram", StringComparison.OrdinalIgnoreCase))
            return ("Telegram", "CHAT", "#2AABEE", true);

        if (primaryId.Contains("discord", StringComparison.OrdinalIgnoreCase))
            return ("Discord", "CHAT", "#5865F2", true);

        if (primaryId.Contains("yourphone", StringComparison.OrdinalIgnoreCase))
            return ("Phone Link", "PHONE", "#38BDF8", true);

        if (primaryId.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
            primaryId.Contains("outlook", StringComparison.OrdinalIgnoreCase))
            return ("Email", "MAIL", "#3B82F6", true);

        if (primaryId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            return ("Chrome", "BROWSER", "#38BDF8", false);

        if (primaryId.Contains("edge", StringComparison.OrdinalIgnoreCase))
            return ("Edge", "BROWSER", "#38BDF8", false);

        if (primaryId.Contains("visualstudiocode", StringComparison.OrdinalIgnoreCase) ||
            primaryId.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
            return ("VS Code", "CODE", "#38BDF8", false);

        if (primaryId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            return ("Spotify", "MUSIC", "#1DB954", false);

        // Extract readable name from UWP / AUMID if possible
        string cleaned = primaryId;
        int bangIdx = cleaned.IndexOf('!');
        if (bangIdx > 0) cleaned = cleaned[..bangIdx];
        int underscoreIdx = cleaned.IndexOf('_');
        if (underscoreIdx > 0) cleaned = cleaned[..underscoreIdx];
        int dotIdx = cleaned.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx < cleaned.Length - 1) cleaned = cleaned[(dotIdx + 1)..];

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Sistem";

        return (cleaned, "BELL", "#38BDF8", false);
    }
}

