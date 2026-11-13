using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Zero-admin local Wi-Fi file server backed by TcpListener on an ephemeral port.
/// Generates a token-protected LAN URL compatible with mobile cameras via QrCodeGenerator
/// to transfer File Shelf items wirelessly without Internet or UAC prompts.
/// </summary>
public sealed class FileShelfShareService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<ShelfItem> _sharedItems = [];
    private string _token = string.Empty;
    private DateTime _expiresUtc = DateTime.MinValue;

    public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
    public string ShareUrl { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public string StartSharing(IReadOnlyList<ShelfItem> items, TimeSpan? lifetime = null)
    {
        StopSharing();

        var valid = items.Where(i => File.Exists(i.FullPath) || Directory.Exists(i.FullPath)).ToList();
        if (valid.Count == 0) return string.Empty;

        _sharedItems.Clear();
        _sharedItems.AddRange(valid);
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _expiresUtc = DateTime.UtcNow + (lifetime ?? TimeSpan.FromMinutes(15));

        try
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();

            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            string localIp = GetBestLanIpv4();
            ShareUrl = $"http://{localIp}:{port}/s/{_token}";

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));

            StateChanged?.Invoke();
            return ShareUrl;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("FileShelfShareService", "Failed starting LAN share server", ex);
            StopSharing();
            return string.Empty;
        }
    }

    public void StopSharing()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts = null;
        _listener = null;
        _sharedItems.Clear();
        ShareUrl = string.Empty;
        StateChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= _expiresUtc)
                {
                    StopSharing();
                    break;
                }

                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 30000;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 2048, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            // Drain headers until blank line
            while (!ct.IsCancellationRequested)
            {
                string? headerLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(headerLine)) break;
            }

            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteTextResponseAsync(stream, "400 Bad Request", "text/plain", "Bad Request", ct);
                return;
            }

            string rawPath = parts[1].Split('?')[0];

            // Landing index for token: /s/<token>
            if (string.Equals(rawPath, $"/s/{_token}", StringComparison.OrdinalIgnoreCase))
            {
                if (_sharedItems.Count == 1 && File.Exists(_sharedItems[0].FullPath))
                {
                    // Single file: serve clean mobile download page with instant download button
                    string htmlSingle = BuildLandingHtml();
                    await WriteTextResponseAsync(stream, "200 OK", "text/html; charset=utf-8", htmlSingle, ct);
                    return;
                }

                string html = BuildLandingHtml();
                await WriteTextResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, ct);
                return;
            }

            // Direct item download: /s/<token>/d/<index>
            string prefix = $"/s/{_token}/d/";
            if (rawPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(rawPath[prefix.Length..], out int idx) &&
                idx >= 0 && idx < _sharedItems.Count)
            {
                await ServeItemAsync(stream, _sharedItems[idx], ct);
                return;
            }

            await WriteTextResponseAsync(stream, "404 Not Found", "text/plain", "Not Found or Expired", ct);
        }
    }

    private string BuildLandingHtml()
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Dynamic Island Shelf</title>
            <style>
            body{margin:0;background:#09090b;color:#f8fafc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;padding:24px;}
            .card{max-width:460px;margin:0 auto;background:#141418;border:1px solid #27272a;border-radius:20px;padding:20px;box-shadow:0 16px 40px rgba(0,0,0,.6);}
            h1{font-size:18px;margin:0 0 6px;font-weight:700;}
            p{font-size:13px;color:#94a3b8;margin:0 0 18px;}
            .item{display:flex;align-items:center;justify-content:space-between;background:#1c1c22;border:1px solid #27272a;border-radius:14px;padding:12px 14px;margin-bottom:10px;gap:12px;}
            .name{font-size:14px;font-weight:600;word-break:break-word;}
            .meta{font-size:11px;color:#94a3b8;margin-top:2px;}
            a.btn{background:#38bdf8;color:#09090b;text-decoration:none;padding:8px 14px;border-radius:10px;font-size:12px;font-weight:700;white-space:nowrap;}
            </style></head><body><div class="card"><h1>Windows Dynamic Island</h1><p>Local Wi-Fi Shelf Transfer</p>
            """);

        for (int i = 0; i < _sharedItems.Count; i++)
        {
            var item = _sharedItems[i];
            string safeName = WebUtility.HtmlEncode(item.FileName);
            string safeSize = WebUtility.HtmlEncode(item.SizeLabel);
            sb.Append($"""
                <div class="item">
                  <div><div class="name">{safeName}</div><div class="meta">{safeSize}</div></div>
                  <a class="btn" href="/s/{_token}/d/{i}">Download</a>
                </div>
                """);
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static async Task ServeItemAsync(NetworkStream stream, ShelfItem item, CancellationToken ct)
    {
        if (File.Exists(item.FullPath))
        {
            var fi = new FileInfo(item.FullPath);
            string fileName = Uri.EscapeDataString(item.FileName);
            string header = $"HTTP/1.1 200 OK\r\n" +
                            $"Content-Type: application/octet-stream\r\n" +
                            $"Content-Length: {fi.Length}\r\n" +
                            $"Content-Disposition: attachment; filename=\"{item.FileName.Replace("\"", "")}\"; filename*=UTF-8''{fileName}\r\n" +
                            $"Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, ct);

            await using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await fs.CopyToAsync(stream, 65536, ct);
            await stream.FlushAsync(ct);
            return;
        }

        if (Directory.Exists(item.FullPath))
        {
            using var ms = new MemoryStream();
            ZipFile.CreateFromDirectory(item.FullPath, ms, CompressionLevel.Fastest, includeBaseDirectory: true);
            byte[] zipBytes = ms.ToArray();
            string zipName = $"{item.FileName}.zip";
            string header = $"HTTP/1.1 200 OK\r\n" +
                            $"Content-Type: application/zip\r\n" +
                            $"Content-Length: {zipBytes.Length}\r\n" +
                            $"Content-Disposition: attachment; filename=\"{zipName.Replace("\"", "")}\"\r\n" +
                            $"Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(zipBytes, ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task WriteTextResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken ct)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
        await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    private static string GetBestLanIpv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Address.ToString();
            }
        }
        catch { }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        StopSharing();
    }
}
