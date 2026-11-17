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
    public event Action<string>? FileReceivedFromPhone;

    public string StartSharing(IReadOnlyList<ShelfItem> items, TimeSpan? lifetime = null)
    {
        StopSharing();

        var valid = items.Where(i => File.Exists(i.FullPath) || Directory.Exists(i.FullPath)).ToList();

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
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 30000;
            using var stream = client.GetStream();

            var (requestLine, headers, leftoverBytes, leftoverCount) = await ReadHttpHeaderBlockAsync(stream, ct);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteTextResponseAsync(stream, "400 Bad Request", "text/plain", "Bad Request", ct);
                return;
            }

            string method = parts[0].ToUpperInvariant();
            string rawPath = parts[1].Split('?')[0];

            // Upload endpoint: POST /s/<token>/upload (Two-way AirDrop from Phone -> PC)
            if (method == "POST" && string.Equals(rawPath, $"/s/{_token}/upload", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUploadRequestAsync(stream, headers, leftoverBytes, leftoverCount, ct);
                return;
            }

            // Landing index for token: /s/<token>
            if (method == "GET" && string.Equals(rawPath, $"/s/{_token}", StringComparison.OrdinalIgnoreCase))
            {
                string html = BuildLandingHtml();
                await WriteTextResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, ct);
                return;
            }

            // Direct item download: /s/<token>/d/<index>
            string prefix = $"/s/{_token}/d/";
            if (method == "GET" &&
                rawPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(rawPath[prefix.Length..], out int idx) &&
                idx >= 0 && idx < _sharedItems.Count)
            {
                await ServeItemAsync(stream, _sharedItems[idx], ct);
                return;
            }

            await WriteTextResponseAsync(stream, "404 Not Found", "text/plain", "Not Found or Expired", ct);
        }
    }

    private static async Task<(string RequestLine, Dictionary<string, string> Headers, byte[] Leftover, int LeftoverCount)>
        ReadHttpHeaderBlockAsync(NetworkStream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var headerMs = new MemoryStream();
        byte[] buf = new byte[4096];
        int endIdx = -1;

        while (headerMs.Length < 65536)
        {
            int read = await stream.ReadAsync(buf, 0, buf.Length, ct);
            if (read <= 0) break;
            headerMs.Write(buf, 0, read);

            byte[] all = headerMs.GetBuffer();
            int len = (int)headerMs.Length;
            for (int i = 0; i <= len - 4; i++)
            {
                if (all[i] == '\r' && all[i + 1] == '\n' && all[i + 2] == '\r' && all[i + 3] == '\n')
                {
                    endIdx = i;
                    break;
                }
            }
            if (endIdx >= 0) break;
        }

        if (endIdx < 0) return (string.Empty, headers, Array.Empty<byte>(), 0);

        byte[] raw = headerMs.GetBuffer();
        int totalLen = (int)headerMs.Length;
        string headerText = Encoding.ASCII.GetString(raw, 0, endIdx);
        string[] lines = headerText.Split(["\r\n"], StringSplitOptions.None);
        string requestLine = lines.Length > 0 ? lines[0] : string.Empty;

        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                string key = lines[i][..colon].Trim();
                string val = lines[i][(colon + 1)..].Trim();
                headers[key] = val;
            }
        }

        int bodyStart = endIdx + 4;
        int leftoverCount = Math.Max(0, totalLen - bodyStart);
        byte[] leftover = leftoverCount > 0 ? raw.AsSpan(bodyStart, leftoverCount).ToArray() : Array.Empty<byte>();
        return (requestLine, headers, leftover, leftoverCount);
    }

    private async Task HandleUploadRequestAsync(
        NetworkStream stream,
        Dictionary<string, string> headers,
        byte[] leftover,
        int leftoverCount,
        CancellationToken ct)
    {
        const long maxUploadBytes = 250L * 1024 * 1024;
        if (!headers.TryGetValue("Content-Length", out string? clStr) ||
            !long.TryParse(clStr, out long contentLength) ||
            contentLength <= 0 ||
            contentLength > maxUploadBytes)
        {
            await WriteTextResponseAsync(stream, "400 Bad Request", "application/json", "{\"ok\":false,\"error\":\"Invalid file size\"}", ct);
            return;
        }

        string rawFileName = headers.TryGetValue("X-File-Name", out string? encodedName)
            ? Uri.UnescapeDataString(encodedName)
            : $"PhoneDrop_{DateTime.Now:yyyyMMdd_HHmmss}.bin";

        string safeName = SanitizeFileName(rawFileName);
        string dropDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "DynamicIsland Drops");
        Directory.CreateDirectory(dropDir);

        string targetPath = GetUniqueTargetPath(dropDir, safeName);

        await using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            long remaining = contentLength;
            if (leftoverCount > 0)
            {
                int toWrite = (int)Math.Min(leftoverCount, remaining);
                await fs.WriteAsync(leftover.AsMemory(0, toWrite), ct);
                remaining -= toWrite;
            }

            byte[] buffer = new byte[65536];
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read <= 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }

        FileReceivedFromPhone?.Invoke(targetPath);
        string json = $"{{\"ok\":true,\"fileName\":\"{Path.GetFileName(targetPath).Replace("\"", "")}\"}}";
        await WriteTextResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, ct);
    }

    private static string SanitizeFileName(string rawName)
    {
        string baseName = Path.GetFileName(rawName.Trim());
        if (string.IsNullOrWhiteSpace(baseName)) return $"Drop_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(c, '_');
        }
        return baseName.Length > 120 ? baseName[^120..] : baseName;
    }

    private static string GetUniqueTargetPath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 1; n <= 999; n++)
        {
            string next = Path.Combine(directory, $"{stem} ({n}){ext}");
            if (!File.Exists(next)) return next;
        }
        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
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
            p{font-size:13px;color:#94a3b8;margin:0 0 16px;}
            .section{font-size:11px;font-weight:700;color:#64748b;letter-spacing:.08em;text-transform:uppercase;margin:16px 0 8px;}
            .item{display:flex;align-items:center;justify-content:space-between;background:#1c1c22;border:1px solid #27272a;border-radius:14px;padding:12px 14px;margin-bottom:10px;gap:12px;}
            .name{font-size:14px;font-weight:600;word-break:break-word;}
            .meta{font-size:11px;color:#94a3b8;margin-top:2px;}
            a.btn,button.btn{background:#38bdf8;color:#09090b;border:none;text-decoration:none;padding:8px 14px;border-radius:10px;font-size:12px;font-weight:700;white-space:nowrap;cursor:pointer;}
            .uploadBox{border:1.5px dashed #38bdf866;background:#0f172a66;border-radius:14px;padding:16px;text-align:center;}
            .uploadBox input{display:none;}
            .status{margin-top:10px;font-size:12px;color:#34d399;min-height:16px;}
            </style></head><body><div class="card"><h1>Windows Dynamic Island</h1><p>Two-Way Local Wi-Fi AirDrop (PC &lt;-&gt; Phone)</p>
            """);

        if (_sharedItems.Count > 0)
        {
            sb.Append("<div class=\"section\">Files from PC</div>");
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
        }

        sb.Append($$"""
            <div class="section">Send File to PC Shelf</div>
            <div class="uploadBox">
              <p style="margin:0 0 12px;color:#cbd5e1;font-size:12.5px;">Select photos, videos, or documents from your phone to send directly to Windows Dynamic Island.</p>
              <label class="btn" style="display:inline-block;">Choose File(s)<input id="fileInput" type="file" multiple></label>
              <div id="status" class="status"></div>
            </div>
            <script>
            const inp=document.getElementById('fileInput');const st=document.getElementById('status');
            inp.addEventListener('change',async()=>{
              const files=Array.from(inp.files||[]);if(!files.length)return;
              for(let i=0;i<files.length;i++){
                const f=files[i];st.style.color='#38bdf8';st.textContent=`Sending ${i+1}/${files.length}: ${f.name}...`;
                try{
                  const r=await fetch('/s/{{_token}}/upload',{method:'POST',headers:{'X-File-Name':encodeURIComponent(f.name)},body:f});
                  if(!r.ok)throw new Error('Upload failed');
                }catch(e){st.style.color='#fb7185';st.textContent='Failed sending '+f.name;return;}
              }
              st.style.color='#34d399';st.textContent=`Sent ${files.length} file(s) to PC Shelf!`;inp.value='';
            });
            </script>
            </div></body></html>
            """);
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
