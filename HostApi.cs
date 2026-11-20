using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MoonlightWinUI;

/// <summary>主机上的一个应用（Sunshine 的 /applist 条目）。封面图异步补上。</summary>
public sealed class AppEntry : INotifyPropertyChanged
{
    private ImageSource? _boxArt;

    public AppEntry(int id, string title, bool hdr)
    {
        Id = id;
        Title = title;
        IsHdr = hdr;
    }

    public int Id { get; }

    public string Title { get; }

    public bool IsHdr { get; }

    private double _cardWidth = 170;
    private double _cardHeight = 240;

    /// <summary>卡片尺寸（随窗口大小变化，由主窗口算好写进来）。</summary>
    public double CardWidth
    {
        get => _cardWidth;
        set
        {
            if (Math.Abs(_cardWidth - value) < 0.5) { return; }
            _cardWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotWidth)));
        }
    }

    public double CardHeight
    {
        get => _cardHeight;
        set
        {
            if (Math.Abs(_cardHeight - value) < 0.5) { return; }
            _cardHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardHeight)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotHeight)));
        }
    }

    /// <summary>
    /// 卡片在布局里占的格子尺寸 = 卡片 × 悬停放大倍数。
    /// 格子必须把放大后的卡片装进去，否则最外侧/滚动到视口边缘的卡片一悬停就会被 ScrollViewer 裁掉。
    /// </summary>
    public double SlotWidth => _cardWidth * 1.05;

    public double SlotHeight => _cardHeight * 1.05;

    /// <summary>占位图上显示的首字（封面图还没到时可见）。</summary>
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1].ToUpperInvariant();

    /// <summary>主机提供的封面（boxart）；没取到就是 null，界面显示占位图。</summary>
    public ImageSource? BoxArt
    {
        get => _boxArt;
        set
        {
            if (ReferenceEquals(_boxArt, value))
            {
                return;
            }

            _boxArt = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoxArt)));
        }
    }

    private bool _isRunning;

    /// <summary>主机的 currentgame 就是本应用 → 正在运行：卡片压暗 + 左上角三角角标。</summary>
    public bool IsRunning => _isRunning;

    public void SetRunning(bool running)
    {
        if (_isRunning == running) { return; }
        _isRunning = running;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeVisibility)));
    }

    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(0xFF, 0x18, 0x18, 0x18));

    /// <summary>卡片底色：运行中的压暗（封面另有半透明黑色遮罩）。</summary>
    public SolidColorBrush CardBrush => _isRunning ? RunningBrush : IdleBrush;

    /// <summary>三角角标可见性。</summary>
    public Visibility BadgeVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;


    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 主机的 HTTP 接口（HTTPS 47984，带客户端证书）：目前只读应用列表。
/// 纯同步实现，调用方用 Task.Run 包一下，别卡 UI 线程。
/// </summary>
internal static class HostApi
{
    public sealed record AppListResult(bool Success, string Message, IReadOnlyList<AppEntry> Apps, int CurrentGame = 0);

    /// <summary>
    /// 明文 HTTP 的 /serverinfo 里取主机名（不需要客户端证书），
    /// 配对成功后用来当卡片显示名；拿不到就返回 null（界面用地址）。
    /// </summary>
    public static async Task<string?> GetHostnameAsync(string host, int httpPort = 47989, int timeoutMs = 2500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var uid = Guid.NewGuid().ToString("N");
            var xml = await http.GetStringAsync(
                $"http://{host}:{httpPort}/serverinfo?uniqueid={uid}&uuid={uid}").ConfigureAwait(false);

            const string open = "<hostname>";
            const string close = "</hostname>";
            var start = xml.IndexOf(open, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += open.Length;
            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end <= start)
            {
                return null;
            }

            var name = xml[start..end].Trim();
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取 /serverinfo 里的 MAC（唤醒用）。</summary>
    public static async Task<string?> GetMacAsync(string host, int httpPort = 47989, int timeoutMs = 2500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var uid = Guid.NewGuid().ToString("N");
            var xml = await http.GetStringAsync(
                $"http://{host}:{httpPort}/serverinfo?uniqueid={uid}&uuid={uid}").ConfigureAwait(false);

            const string open = "<mac>";
            const string close = "</mac>";
            var start = xml.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) { return null; }
            start += open.Length;
            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end <= start) { return null; }
            var mac = xml[start..end].Trim();
            return mac.Length > 0 ? mac : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>封面图：/appasset?asset=boxart。没有图时主机返回 404/HTML，这里按文件头判断。</summary>
    public static byte[]? GetBoxArt(string host, int appId, int httpsPort = 47984)
    {
        var baseDir = AppContext.BaseDirectory;
        var target = $"/appasset?appid={appId}&asset=boxart&index=0";

        if (!NativeTls.TryGetBytes(host, httpsPort, target,
                Path.Combine(baseDir, "creds", "client.crt"),
                Path.Combine(baseDir, "creds", "client.key"),
                out var bytes, out _))
        {
            return null;
        }

        // PNG / JPEG 才当图片用，其它（404 的 XML/HTML）一律忽略，由界面画占位图
        var isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        var isJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        return isPng || isJpeg ? bytes : null;
    }

    /// <summary>
    /// 让主机结束当前运行的应用（Sunshine /cancel）。
    /// 从应用卡片的"退出当前应用并结束串流"用；失败返回 false（可能本来就没在跑）。
    /// </summary>
    public static bool CancelRunningApp(string host, int httpsPort = 47984)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var certPath = Path.Combine(baseDir, "creds", "client.crt");
            var keyPath = Path.Combine(baseDir, "creds", "client.key");
            var uidPath = Path.Combine(baseDir, "creds", "uniqueid.txt");

            var uniqueId = File.Exists(uidPath) ? File.ReadAllText(uidPath).Trim() : "5325b8a3fdd68731";
            var query = $"uniqueid={Uri.EscapeDataString(uniqueId)}&uuid={Uri.EscapeDataString(uniqueId)}";

            return NativeTls.TryGet(host, httpsPort, "/cancel?" + query + "&clientname=MoonlightWinUI",
                certPath, keyPath, out _, out _);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>
    /// 主机当前运行的应用 ID（HTTPS /serverinfo 的 currentgame）；空闲返回 0。
    /// 应用卡片用它显示"运行中"。
    /// </summary>
    public static int GetCurrentGame(string host, int httpsPort = 47984)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var certPath = Path.Combine(baseDir, "creds", "client.crt");
            var keyPath = Path.Combine(baseDir, "creds", "client.key");
            var uidPath = Path.Combine(baseDir, "creds", "uniqueid.txt");

            var uniqueId = File.Exists(uidPath) ? File.ReadAllText(uidPath).Trim() : "5325b8a3fdd68731";
            var query = $"uniqueid={Uri.EscapeDataString(uniqueId)}&uuid={Uri.EscapeDataString(uniqueId)}";

            if (!NativeTls.TryGet(host, httpsPort, "/serverinfo?" + query, certPath, keyPath,
                    out var body, out _))
            {
                return 0;
            }

            return int.TryParse(XDocument.Parse(body).Root?.Element("currentgame")?.Value, out var current)
                ? current
                : 0;
        }
        catch
        {
            return 0;
        }
    }
    public static AppListResult GetAppList(string host, int httpsPort = 47984)
    {
        var baseDir = AppContext.BaseDirectory;
        var certPath = Path.Combine(baseDir, "creds", "client.crt");
        var keyPath = Path.Combine(baseDir, "creds", "client.key");
        var uidPath = Path.Combine(baseDir, "creds", "uniqueid.txt");

        var uniqueId = File.Exists(uidPath) ? File.ReadAllText(uidPath).Trim() : "5325b8a3fdd68731";
        var query = $"uniqueid={Uri.EscapeDataString(uniqueId)}&uuid={Uri.EscapeDataString(uniqueId)}";

        if (!NativeTls.TryGet(host, httpsPort, "/applist?" + query, certPath, keyPath,
                out var body, out var error))
        {
            return new AppListResult(false, $"连不上 {host}：{error}", Array.Empty<AppEntry>());
        }

        try
        {
            var root = XDocument.Parse(body).Root;
            var status = root?.Attribute("status_code")?.Value ?? "?";

            if (status == "401")
            {
                return new AppListResult(
                    false,
                    "这台主机还没配对这个客户端：先在「配对」里走一次，再回来刷新。",
                    Array.Empty<AppEntry>());
            }

            if (status != "200")
            {
                var message = root?.Attribute("status_message")?.Value ?? string.Empty;
                return new AppListResult(false, $"主机返回 {status} {message}".Trim(), Array.Empty<AppEntry>());
            }

            var apps = root!
                .Elements("App")
                .Select(app => new AppEntry(
                    int.TryParse(app.Element("ID")?.Value, out var id) ? id : 0,
                    app.Element("AppTitle")?.Value ?? "(未命名)",
                    int.TryParse(app.Element("IsHdrSupported")?.Value, out var hdr) && hdr != 0))
                .Where(app => app.Id > 0)
                .OrderBy(app => app.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 主机当前在跑哪个应用：/serverinfo 里的 currentgame（空闲时是 0）。
            // 用它给对应卡片打"运行中"标记（压暗 + 三角角标）。
            var currentGame = GetCurrentGame(host, httpsPort);

            foreach (var app in apps)
            {
                app.SetRunning(currentGame != 0 && app.Id == currentGame);
            }

            var runningTitle = apps.FirstOrDefault(a => a.IsRunning)?.Title;
            var summary = apps.Count == 0
                ? "主机没有返回应用"
                : currentGame != 0
                    ? $"{apps.Count} 个应用 · 运行中：{runningTitle ?? currentGame.ToString()}"
                    : $"{apps.Count} 个应用";

            return new AppListResult(true, summary, apps, currentGame);
        }
        catch (Exception ex)
        {
            return new AppListResult(false, "解析应用列表失败：" + ex.Message, Array.Empty<AppEntry>());
        }
    }

    // ---------------- 主机显示缩放（Sunshine 扩展：/display-scale*，47984 + 客户端证书） ----------------

    private static (string Cert, string Key) ScaleCredentials() => (
        Path.Combine(AppContext.BaseDirectory, "creds", "client.crt"),
        Path.Combine(AppContext.BaseDirectory, "creds", "client.key"));

    /// <summary>
    /// 读取主机当前显示器的缩放信息：GET /display-scale-options。
    /// 主机不支持、未配对或连不上时返回 null。
    /// </summary>
    public static DisplayScaleInfo? GetDisplayScale(
        string host, string? displayName = null, string? deviceId = null, int httpsPort = 47984)
    {
        var (cert, key) = ScaleCredentials();

        var target = "/display-scale-options";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            query.Add("display_name=" + Uri.EscapeDataString(displayName));
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query.Add("device_id=" + Uri.EscapeDataString(deviceId));
        }

        if (query.Count > 0)
        {
            target += "?" + string.Join("&", query);
        }

        if (!NativeTls.TryGet(host, httpsPort, target, cert, key, out var json, out _))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status_code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.GetInt32() != 200)
            {
                return null;
            }

            return new DisplayScaleInfo(
                ReadString(root, "display_name"),
                ReadString(root, "device_id"),
                ReadString(root, "friendly_name", ReadString(root, "display_name")),
                ReadBool(root, "is_primary"),
                ReadInt(root, "current_scale_percent"),
                ReadInt(root, "recommended_scale_percent"),
                ReadInts(root, "supported_scale_percents"),
                ReadBool(root, "scale_set_supported"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 设置主机显示缩放：POST /display-scale（JSON：scale_percent，可选 display_name / device_id）。
    /// 成功后通常立即生效（不需要重连），主机要求重启显示时 RestartRequired 为 true。
    /// </summary>
    public static DisplayScaleResult SetDisplayScale(
        string host, int percent, string? displayName = null, string? deviceId = null, int httpsPort = 47984)
    {
        var (cert, key) = ScaleCredentials();

        var payload = new System.Text.Json.Nodes.JsonObject { ["scale_percent"] = percent };
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            payload["display_name"] = displayName;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            payload["device_id"] = deviceId;
        }

        if (!NativeTls.TryPostJson(host, httpsPort, "/display-scale", payload.ToJsonString(),
                cert, key, out var json, out var requestError))
        {
            return new DisplayScaleResult(
                false, null, null, Array.Empty<int>(), false, false,
                string.IsNullOrWhiteSpace(requestError) ? "请求主机失败" : requestError, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new DisplayScaleResult(
                ReadBool(root, "success"),
                ReadInt(root, "previous_scale_percent"),
                ReadInt(root, "current_scale_percent"),
                ReadInts(root, "supported_scale_percents"),
                ReadBool(root, "restart_required"),
                ReadBool(root, "effective_immediately"),
                ReadString(root, "status_message", "OK"),
                ReadString(root, "error_code"));
        }
        catch
        {
            return new DisplayScaleResult(
                false, null, null, Array.Empty<int>(), false, false, "解析主机响应失败", null);
        }
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) &&
           (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
           value.GetBoolean();

    private static int[] ReadInts(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var list = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                list.Add(item.GetInt32());
            }
        }

        return list.ToArray();
    }
}

/// <summary>主机显示器缩放信息（GET /display-scale-options）。</summary>
internal sealed record DisplayScaleInfo(
    string DisplayName,
    string DeviceId,
    string FriendlyName,
    bool IsPrimary,
    int? CurrentScalePercent,
    int? RecommendedScalePercent,
    int[] SupportedScalePercents,
    bool ScaleSetSupported)
{
    /// <summary>主机是否允许通过接口设置缩放（Sunshine 扩展支持且返回了档位）。</summary>
    public bool IsAvailable => ScaleSetSupported && SupportedScalePercents.Length > 0;
}

/// <summary>设置显示缩放的结果（POST /display-scale）。</summary>
internal sealed record DisplayScaleResult(
    bool Success,
    int? PreviousScalePercent,
    int? CurrentScalePercent,
    int[] SupportedScalePercents,
    bool RestartRequired,
    bool EffectiveImmediately,
    string StatusMessage,
    string? ErrorCode);
