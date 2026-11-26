using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MoonlightWinUI;

/// <summary>
/// 串流参数，持久化到 exe 同目录的 settings.json（和 creds 放一起，随应用目录走）。
/// 只在建立连接时读取；连接中的串流不受影响。
/// </summary>
public sealed class StreamSettings
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    public int BitrateKbps { get; set; } = 20000;

    // ---- 以下与 Qt 版（moonlight-qt）的设置项同名/同义 ----

    /// <summary>Qt: windowmode —— false=窗口，true=全屏。</summary>
    public bool FullScreen { get; set; }

    /// <summary>Qt: swapmousebuttons —— 交换鼠标左右键。</summary>
    public bool SwapMouseButtons { get; set; }

    /// <summary>Qt: reversescroll —— 反转滚轮方向。</summary>
    public bool ReverseScroll { get; set; }

    /// <summary>隐藏远程光标：主机不再把光标画进视频，改由本地光标显示（Parsec 式）。</summary>
    public bool HideRemoteCursor { get; set; }

    /// <summary>同步剪贴板图片（PNG，≤64KB）；关闭后只同步文本。默认开。</summary>
    public bool ClipboardImageSync { get; set; } = true;

    /// <summary>窗口材质：0=Mica 1=Mica Alt 2=Acrylic 3=无（主窗口和串流窗口共用）。</summary>
    public int BackdropKind { get; set; }

    /// <summary>应用卡片行数：1 = 单行，2 = 双行（默认）。</summary>
    public int AppCardRows { get; set; } = 2;

    /// <summary>性能浮窗透明度（0.2–1.0）。</summary>
    public double OverlayOpacity { get; set; } = 1.0;

    /// <summary>性能浮窗位置：0 左上 / 1 上中 / 2 右上 / 3 左下 / 4 下中 / 5 右下。</summary>
    public int OverlayPosition { get; set; }

    /// <summary>四角位置的排版：true=单行，false=多行（上下中间恒为单行，左右中间恒为多行）。</summary>
    public bool OverlayCornerSingleLine { get; set; } = true;

    /// <summary>性能浮窗显示项目位掩码：1 FPS / 2 编码 / 4 解码耗时 / 8 网络延迟 / 16 丢包 / 32 码率。</summary>
    public int OverlayItems { get; set; } = 0x3F;

    /// <summary>Qt: showperfoverlay —— 串流窗口顶部显示性能浮窗（fps/编解码/延时/丢包/码率）。</summary>
    public bool ShowPerformanceOverlay { get; set; } = true;

    /// <summary>Qt: keepawake —— 串流时阻止系统休眠/熄屏（默认开，和 Qt 一致）。</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>Qt: quitAppAfter —— 串流结束后退出应用。</summary>
    public bool QuitAfterStream { get; set; }

    /// <summary>
    /// Qt: absoluteMouseMode —— 真：发绝对坐标（光标和主机严格同步、不受主机指针加速影响）；
    /// 关：发相对增量（少数游戏需要）。
    /// </summary>
    public bool AbsoluteMouse { get; set; } = true;

    /// <summary>指针锁定（游戏内）时的相对位移倍率：1.0 = 原始，调小可抵消主机的鼠标加速。</summary>
    public double MouseSensitivity { get; set; } = 1.0;

    /// <summary>编码偏好（native MS_SetVideoCodec）：0=自动，1=H264，2=HEVC，3=AV1。</summary>
    public int Codec { get; set; }

    /// <summary>用户自定义的分辨率（"宽x高"），下拉框里追加显示。</summary>
    public List<string> CustomResolutions { get; set; } = new();

    public static readonly (int Width, int Height)[] Resolutions =
    {
        (1280, 720),
        (1600, 900),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160),
    };

    /// <summary>用户自定义刷新率（Hz），下拉框里追加显示。</summary>
    public List<int> CustomRefreshRates { get; set; } = new();

    public static readonly int[] RefreshRates = { 30, 60, 90, 120, 144 };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static StreamSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded.Normalized();
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不至于起不来
        }

        return new StreamSettings();
    }

    /// <summary>设置被保存时触发；串流窗口用它实时跟随材质等改动。</summary>
    public static event Action<StreamSettings>? Saved;

    public void Save()
    {
        try
        {
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 只读目录/权限不足时静默失败，界面里仍按当前选择生效
        }

        try { Saved?.Invoke(this); } catch { }
    }

    public StreamSettings Clone() => (StreamSettings)MemberwiseClone();

    /// <summary>把手工改坏的 settings.json 拉回可用范围。</summary>
    public StreamSettings Normalized()
    {
        if (MouseSensitivity is < 0.2 or > 3.0)
        {
            MouseSensitivity = 1.0;
        }

        if (Width < 320 || Height < 200)
        {
            Width = 1920;
            Height = 1080;
        }

        if (Fps < 10 || Fps > 240)
        {
            Fps = 60;
        }

        BitrateKbps = Math.Clamp(BitrateKbps, 1000, 200000);
        Codec = Math.Clamp(Codec, 0, 3);
        return this;
    }

    public override string ToString() => $"{Width}×{Height}@{Fps} · {BitrateKbps / 1000.0:F0} Mbps";
}
