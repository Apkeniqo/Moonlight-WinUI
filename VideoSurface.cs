using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MoonlightWinUI;

/// <summary>
/// WinUI 3 视频面：C# 负责 D3D11 设备 + 合成交换链 + 挂到 SwapChainPanel，
/// 解码和上屏交给 native/mlrender.dll（FFmpeg d3d11va 硬解，复用同一个 device）。
///
/// 数据流：test-1080p60.mp4 → avcodec(d3d11va) → ID3D11Texture2D(NV12)
///        → 双平面 SRV → BT.709 PS → swapchain → SwapChainPanel
/// 这正是 Moonlight 真接流时要走的路径，只差把文件换成 session 里的 H.264 包。
/// </summary>
public sealed class VideoSurface : IDisposable
{
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        void SetSwapChain(IntPtr swapChain);
    }

    private const string NativeDll = "mlrender.dll";

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_Init(IntPtr device, IntPtr context, IntPtr swapChain, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_HostChild(IntPtr parent, int x, int y, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_MoveChild(int x, int y, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_DetachChild();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_ShowChild(int visible);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int SUI_Create(string title, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SUI_IsClosed();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_Destroy();

    // ---------------- 剪贴板（Sunshine 扩展 0x5508） ----------------
    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_PollClipboard(byte[]? buffer, int capacity, out int kind, out int token);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendClipboardText(byte[] utf8, int length);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendClipboardPng(byte[] png, int length);


    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int MS_SendClipboardRef(string json, int length);
    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetFullscreen(int fullscreen);


    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetBackdrop(int kind);

    // 串流窗口移动/缩放时的同步回调（控制面板实时跟随用）
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeMoveDelegate();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetMoveCallback(IntPtr callback);

    private static Action? _moveAction;
    private static NativeMoveDelegate? _moveDelegateKeepAlive;

    private static void OnNativeMove() => _moveAction?.Invoke();

    /// <summary>建纯 Win32 串流窗口（Mica 背景 + 视频子窗口）。</summary>
    public int CreateStreamWindow(string title, int width, int height) => SUI_Create(title, width, height);

    /// <summary>设置串流窗口材质（0=Mica 1=Mica Alt 2=Acrylic 3=无），与主窗口保持一致。</summary>
    public void SetBackdrop(int kind) => SUI_SetBackdrop(kind);

    /// <summary>注册"串流窗口移动/缩放"回调；传 null 取消。</summary>
    public void SetMoveCallback(Action? callback)
    {
        _moveAction = callback;
        _moveDelegateKeepAlive ??= OnNativeMove;
        SUI_SetMoveCallback(callback is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_moveDelegateKeepAlive));
    }

    public bool IsStreamWindowClosed() => SUI_IsClosed() != 0;

    public void DestroyStreamWindow() => SUI_Destroy();

    public void SetStreamWindowFullscreen(bool fullscreen) => SUI_SetFullscreen(fullscreen ? 1 : 0);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetVideoAspect(int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void SUI_SetStatus(string text);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void SUI_ShowError(string title, string text);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetVideoReady(int ready);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PNL_SetState(int width, int height, int kbps, int fullscreen);

    public delegate void PanelApplyDelegate(int width, int height, int kbps, int fullscreen);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PNL_SetApplyCallback(IntPtr callback);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PNL_Show(IntPtr parent, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PNL_Hide();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PNL_TakeOpenRequest();

    /// <summary>标题栏菜单按钮被点击（native 置位），由 C# 带当前设置打开面板。</summary>
    public bool TakePanelOpenRequest() => PNL_TakeOpenRequest() != 0;

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SUI_GetHwnd();

    private PanelApplyDelegate? _panelApply;

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SUI_SetFollowWindow(IntPtr panel);

    /// <summary>让原生串流窗口在移动/缩放时把 WinUI 面板实时居中（0 = 取消）。</summary>
    public void SetFollowWindow(IntPtr panelHwnd) => SUI_SetFollowWindow(panelHwnd);

    public void SetPanelApplyCallback(PanelApplyDelegate callback)
    {
        _panelApply = callback;
        PNL_SetApplyCallback(System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(callback));
    }

    public void ShowControlPanel(int width, int height, int kbps, bool fullscreen)
    {
        PNL_SetState(width, height, kbps, fullscreen ? 1 : 0);
        PNL_Show(SUI_GetHwnd(), 0, 0);   // 0,0 = 由原生按串流窗口大小自动缩放
    }

    public void HideControlPanel() => PNL_Hide();

    /// <summary>原生串流窗口句柄（用来给 WinUI 弹窗定位）。</summary>
    public IntPtr StreamWindowHandle => SUI_GetHwnd();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_SetGearEnabled(int enabled);

    /// <summary>原生窗口模式下彻底关掉 HUD 的圆形齿轮（改用标题栏按钮）。</summary>
    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_RequestResolution(int width, int height);

    /// <summary>走控制流请主机改分辨率（0 = 成功）。</summary>
    public int RequestResolution(int width, int height) => MS_RequestResolution(width, height);

    public void SetGearEnabled(bool enabled) => ML_SetGearEnabled(enabled ? 1 : 0);

    public void SetWindowStatus(string text) => SUI_SetStatus(text);

    public void ShowWindowError(string title, string text) => SUI_ShowError(title, text);

    public void SetWindowVideoReady(bool ready) => SUI_SetVideoReady(ready ? 1 : 0);

    /// <summary>告诉窗口流的宽高比，用于 letterbox 保持比例。</summary>
    public void SetVideoAspect(int width, int height) => SUI_SetVideoAspect(width, height);

    /// <summary>显示/隐藏原生视频弹窗（打开控制面板时先藏起来，否则面板会被视频盖住）。</summary>
    public void ShowChildVideo(bool visible) => ML_ShowChild(visible ? 1 : 0);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_StartFile(string path, double fps);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_PushPacket(IntPtr data, int length);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_Queue(out int depth, out long pushed, out long dropped);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_Frame();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_Resize(int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_Size(out int width, out int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_Stats(out int decodedFrames, out double lastDecodeMs);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_StreamSize(out int width, out int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_DecoderInfo(byte[] buffer, int capacity);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SetCursorMode(int mode);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint MS_HostFeatures();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_SetPointerLock(int locked);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_SetMouseSensitivity(double value);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_GetMouseButtons();

    /// <summary>
    /// 原生子窗口当前的鼠标按键位图（1 左 / 2 右 / 4 中）。
    /// Win32 串流窗口模式下 WinUI 面板收不到 PointerPressed，只能从 native 问。
    /// </summary>
    public int GetMouseButtons()
    {
        try { return ML_GetMouseButtons(); } catch { return 0; }
    }

    /// <summary>锁定模式的相对位移倍率（1.0 = 原始，调小抵消主机鼠标加速）。</summary>
    public void SetMouseSensitivity(double value)
    {
        try { ML_SetMouseSensitivity(value); } catch { }
    }

    /// <summary>指针锁定：native 侧把 WM_MOUSEMOVE 翻译成相对位移（Win32 串流窗口模式下用）。</summary>
    public void SetPointerLock(bool locked)
    {
        try { ML_SetPointerLock(locked ? 1 : 0); } catch { }
    }

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_CursorInfo(out int visible, out int shapeId, out int width, out int height, out int hotX, out int hotY);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_CursorShape(byte[]? buffer, int capacity, out int shapeId);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint MS_CursorUpdates();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void HudSetText(string text, float opacity, float x, float y);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HudSetGear(int x, int y, int visible);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HudTakeGearClick();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HudGetGear(out int x, out int y);

    /// <summary>当前齿轮位置（视频像素坐标，拖动后会变）。</summary>
    public (int X, int Y) GetHudGear()
    {
        HudGetGear(out var x, out var y);
        return (x, y);
    }

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HudSetVisible(int visible);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_ShowGearMenu(out int width, out int height, out int kbps, out int fullScreenToggle);

    /// <summary>在鼠标位置弹出齿轮菜单；返回 (kind, w, h, kbps, fs)：kind 1=分辨率 2=码率 3=全屏。</summary>
    public (int Kind, int Width, int Height, int Kbps, bool FullScreenToggle) ShowGearMenu()
    {
        var kind = ML_ShowGearMenu(out var w, out var h, out var kbps, out var fs);
        return (kind, w, h, kbps, fs != 0);
    }

    /// <summary>显示/隐藏 HUD 分层窗口（打开控制面板时用）。</summary>
    public void SetHudVisible(bool visible) => HudSetVisible(visible ? 1 : 0);

    public void SetHudText(string text, double opacity, double x, double y)
        => HudSetText(text ?? string.Empty, (float)opacity, (float)x, (float)y);

    public void SetHudGear(int x, int y, bool visible) => HudSetGear(x, y, visible ? 1 : 0);

    public bool TakeHudGearClick() => HudTakeGearClick() != 0;

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_Close();

    // ---- mlsession.cpp（真串流：/serverinfo + /launch + LiStartConnection）----

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ML_StopFile();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_Init(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string certPemPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string keyPemPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string uniqueId);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_Status(byte[] stage, int stageLen);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void MS_SetVideoCodec(int codec);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_Start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        int width, int height, int fps, int bitrateKbps, int appId);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void MS_Stop();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_RequestIdr();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendController(int index, int mask, int buttons,
        int lt, int rt, int lx, int ly, int rx, int ry);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_GetRumble(out int low, out int high);

    /// <summary>发送手柄状态（P1）。mask 为已连接手柄位图（bit0=手柄0）。</summary>
    public int SendController(int index, int mask, int buttons, int lt, int rt,
        int lx, int ly, int rx, int ry)
        => MS_SendController(index, mask, buttons, lt, rt, lx, ly, rx, ry);

    /// <summary>读取主机下发的震动强度（P2，0..65535）。</summary>
    public (int Low, int High) GetRumble()
    {
        MS_GetRumble(out var low, out var high);
        return (low, high);
    }

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendMouseMove(int dx, int dy);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendMousePosition(int x, int y, int width, int height);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendKey(int vk, int down, int modifiers);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendMouseButton(int button, int down);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendMouseScroll(int clicks);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int MS_SendMouseHScroll(int clicks);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void MS_Stats(out long packets, out long bytes, out int connected);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void MS_Perf(out PerfStats stats);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void MA_Stats(out int sampleRate, out int channels,
                                        out long playedFrames, out long underruns);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private int _panelWidth;
    private int _panelHeight;
    private bool _initialized;
    private bool _childMode;

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    /// <summary>流的分辨率（最近一帧解码出来的尺寸），没解到帧时是 0。</summary>
    public int StreamWidth { get; private set; }
    public int StreamHeight { get; private set; }

    /// <summary>主机能力位（LI_FF_CURSOR_SHAPE = 0x40 表示支持本地光标模式）。</summary>
    public uint HostFeatures
    {
        get
        {
            try { return MS_HostFeatures(); }
            catch { return 0; }
        }
    }

    /// <summary>0 = 主机把光标画进视频；1 = 主机不画光标，由客户端渲染（Parsec 那种）。</summary>
    /// <summary>主机光标状态（本地光标模式下由主机推送）。</summary>
    public struct CursorState
    {
        public bool Visible;
        public int ShapeId;
        public int Width;
        public int Height;
        public int HotX;
        public int HotY;
    }

    /// <summary>收到过多少次主机光标更新（0 表示主机根本没推）。</summary>
    public uint CursorUpdateCount
    {
        get
        {
            try { return MS_CursorUpdates(); }
            catch { return 0; }
        }
    }

    /// <summary>读当前光标状态（未连接或主机不支持时 Visible=true、ShapeId=0）。</summary>
    public CursorState GetCursorState()
    {
        try
        {
            MS_CursorInfo(out var visible, out var shapeId, out var w, out var h, out var hx, out var hy);
            return new CursorState
            {
                Visible = visible != 0,
                ShapeId = shapeId,
                Width = w,
                Height = h,
                HotX = hx,
                HotY = hy,
            };
        }
        catch
        {
            return new CursorState { Visible = true };
        }
    }

    /// <summary>取当前光标形状的 BGRA 像素（失败或还没收到形状时返回 null）。</summary>
    public byte[]? GetCursorShape()
    {
        try
        {
            var needed = MS_CursorShape(null, 0, out _);
            if (needed <= 0)
            {
                return null;
            }

            var buffer = new byte[needed];
            var copied = MS_CursorShape(buffer, buffer.Length, out _);
            return copied == needed ? buffer : null;
        }
        catch
        {
            return null;
        }
    }

    public int SetCursorMode(int mode)
    {
        try { return MS_SetCursorMode(mode); }
        catch
        {
            return -1;
        }
    }

    /// <summary>当前解码方式，例如 "HEVC·硬解" / "AV1·软解"。</summary>
    public string DecoderInfo
    {
        get
        {
            try
            {
                var buffer = new byte[64];
                ML_DecoderInfo(buffer, buffer.Length);
                var text = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd((char)0).Trim();
                return text.Length > 0 ? text : "—";
            }
            catch
            {
                return "—";
            }
        }
    }

    public void RefreshStreamSize()
    {
        ML_StreamSize(out int w, out int h);
        if (w > 0 && h > 0)
        {
            StreamWidth = w;
            StreamHeight = h;
        }
    }
    public int DecodedFrames { get; private set; }
    public double LastDecodeMs { get; private set; }
    public int QueueDepth { get; private set; }
    public long PacketsPushed { get; private set; }
    public long PacketsDropped { get; private set; }

    /// <summary>真实串流时由网络线程/回调调用（对应 moonlight-common-c 的解码回调）。</summary>
    public void PushPacket(byte[] accessUnit)
    {
        if (accessUnit is null || accessUnit.Length == 0)
        {
            return;
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(accessUnit, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            ML_PushPacket(handle.AddrOfPinnedObject(), accessUnit.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>原生子窗口模式：视频和光标都在一个 Win32 子 HWND 里（绕过 WinUI 的光标限制）。</summary>
    private VideoSurface()
    {
        _childMode = true;
        _initialized = true;
    }

    /// <summary>纯 Win32 窗口模式：视频由原生串流窗口承载。</summary>
    public static VideoSurface CreateWin32Stream() => new() { _panelWidth = 1280, _panelHeight = 720 };

    /// <summary>创建并初始化原生子窗口视频面（返回 null 表示失败）。</summary>
    public static VideoSurface? CreateChildWindow(IntPtr parentHwnd, int x, int y, int width, int height)
    {
        var rc = ML_HostChild(parentHwnd, x, y, Math.Max(2, width), Math.Max(2, height));
        if (rc != 0)
        {
            return null;
        }

        return new VideoSurface { _panelWidth = width, _panelHeight = height };
    }

    public void MoveChild(int x, int y, int width, int height)
    {
        if (!_childMode)
        {
            return;
        }

        ML_MoveChild(x, y, Math.Max(2, width), Math.Max(2, height));
        _panelWidth = width;
        _panelHeight = height;
    }
    public VideoSurface(SwapChainPanel panel, int panelWidth, int panelHeight, string videoPath)
    {
        _panelWidth = Math.Max(2, panelWidth);
        _panelHeight = Math.Max(2, panelHeight);

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device,
            out _context).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)_panelWidth,
            Height = (uint)_panelHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, desc);

        var scale = panel.XamlRoot?.RasterizationScale ?? 1.0;
        using (var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            swapChain2.MatrixTransform = new System.Numerics.Matrix3x2(
                (float)(1.0 / scale), 0, 0, (float)(1.0 / scale), 0, 0);
        }

        AttachToPanel(panel);

        int hr = ML_Init(_device.NativePointer, _context.NativePointer, _swapChain.NativePointer,
                         _panelWidth, _panelHeight);
        if (hr != 0)
        {
            throw new InvalidOperationException($"ML_Init 失败：{hr}");
        }

        // 演示用：把测试流按 60fps 当"网络包"推进来；真串流时改成 ML_PushPacket
        hr = ML_StartFile(videoPath, 60.0);
        if (hr != 0)
        {
            ML_Close();
            throw new InvalidOperationException($"ML_StartFile 失败：{hr}（{videoPath}）");
        }

        ML_Size(out int vw, out int vh);
        VideoWidth = vw;
        VideoHeight = vh;
        _initialized = true;
    }

    private void AttachToPanel(SwapChainPanel panel)
    {
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        IntPtr unknown = WinRT.MarshalInspectable<object>.FromManaged(panel);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out IntPtr native));
            try
            {
                var panelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(native);
                panelNative.SetSwapChain(_swapChain.NativePointer);
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>取一条主机推来的剪贴板数据（没有返回 null）。Kind：1=文本 2=PNG 3=引用。</summary>
    public (int Kind, byte[] Payload)? PollClipboard()
    {
        if (!_initialized)
        {
            return null;
        }

        var needed = MS_PollClipboard(null, 0, out var kind, out _);
        if (needed <= 0)
        {
            return null;
        }

        var buffer = new byte[needed];
        var copied = MS_PollClipboard(buffer, buffer.Length, out kind, out _);
        if (copied <= 0)
        {
            return null;
        }

        return (kind, buffer);
    }

    /// <summary>把一段文本（UTF-8）发到主机剪贴板。</summary>
    public int SendClipboardText(string text)
    {
        if (!_initialized || string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return MS_SendClipboardText(bytes, bytes.Length);
    }

    /// <summary>把一张 PNG（字节）发到主机剪贴板；超过单帧上限返回 -2。</summary>
    public int SendClipboardPng(byte[] png)
    {
        if (!_initialized || png is null || png.Length == 0)
        {
            return -1;
        }

        return MS_SendClipboardPng(png, png.Length);
    }

    /// <summary>发一条大对象引用（kind=3），正文是 JSON {"id","mime","size"}。</summary>
    public int SendClipboardRef(string json)
    {
        if (!_initialized || string.IsNullOrEmpty(json))
        {
            return -1;
        }

        return MS_SendClipboardRef(json, json.Length);
    }

    public (int Width, int Height) Size => (_panelWidth, _panelHeight);

    /// <summary>解一帧并上屏；返回是否画了新帧。</summary>
    public bool Render()
    {
        if (!_initialized)
        {
            return false;
        }

        bool drew = ML_Frame() != 0;
        ML_Stats(out int decoded, out double ms);
        ML_Queue(out int depth, out long pushed, out long dropped);
        QueueDepth = depth;
        PacketsPushed = pushed;
        PacketsDropped = dropped;
        ML_Size(out int vw, out int vh);
        if (vw > 0 && vh > 0)
        {
            VideoWidth = vw;
            VideoHeight = vh;
        }
        DecodedFrames = decoded;
        LastDecodeMs = ms;
        return drew;
    }

    public void Resize(int width, int height)
    {
        if (_childMode)
        {
            return;   // 子窗口模式由 MoveChild 处理
        }

        width = Math.Max(2, width);
        height = Math.Max(2, height);
        if (width == _panelWidth && height == _panelHeight)
        {
            return;
        }

        if (_initialized)
        {
            ML_Resize(width, height);
        }

        _panelWidth = width;
        _panelHeight = height;
    }

    // ---------------- 真串流 ----------------

    /// <summary>停掉演示用的"文件喂包"线程；切到真串流前先调。</summary>
    public void StopDemo()
    {
        if (_initialized)
        {
            ML_StopFile();
        }
    }

    /// <summary>
    /// 原生子窗口模式下的演示流：直接把本地 H.264 裸流按 fps 推进解码队列。
    /// （子窗口模式不走 SwapChainPanel 那套初始化，所以不能复用 StartDemo。）
    /// </summary>
    public bool StartDemoFile(string h264Path, double fps)
    {
        return ML_StartFile(h264Path, fps) == 0;
    }
    /// <summary>回到演示模式：按 fps 把本地 H.264 裸流当网络包推进来。</summary>
    public void StartDemo(string h264Path, double fps)
    {
        if (!_initialized)
        {
            return;
        }

        ML_StopFile();
        ML_StartFile(h264Path, fps);
    }

    /// <summary>
    /// 连上真实主机：MS_Init（客户端证书）+ MS_Start（serverinfo/launch/RTSP）。
    /// 立即返回，之后用 <see cref="StreamStatus"/> 轮询阶段。
    /// 返回 0 表示已开始；非 0 见 native/mlsession.cpp 顶部注释。
    /// </summary>
    public int StartStream(string host, int width = 1920, int height = 1080,
                           int fps = 60, int bitrateKbps = 20000, int appId = 1, int codec = 0)
    {
        if (!_initialized)
        {
            return -100;
        }

        // 演示流和真串流共用同一个解码队列，先停掉喂文件的线程
        ML_StopFile();

        var dir = AppContext.BaseDirectory;
        var cert = Path.Combine(dir, "creds", "client.crt");
        var key = Path.Combine(dir, "creds", "client.key");
        var uidFile = Path.Combine(dir, "creds", "uniqueid.txt");

        var uid = File.Exists(uidFile) ? File.ReadAllText(uidFile).Trim() : "5325b8a3fdd68731";

        int rc = MS_Init(cert, key, uid);
        if (rc != 0)
        {
            return rc;
        }

        // 0=自动 1=H264 2=HEVC 3=AV1，必须在 MS_Start 之前设置
        MS_SetVideoCodec(codec);

        return MS_Start(host, width, height, fps, bitrateKbps, appId);
    }

    public void StopStream()
    {
        MS_Stop();
    }

    /// <summary>阶段文字 + 状态码（0 空闲 1 建立中 2 串流中 -1 失败）。</summary>
    public (int State, string Stage) StreamStatus()
    {
        var buffer = new byte[512];
        int state = MS_Status(buffer, buffer.Length);

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return (state, System.Text.Encoding.UTF8.GetString(buffer, 0, length));
    }

    public (long Packets, long Bytes, bool Connected) StreamStats()
    {
        MS_Stats(out long packets, out long bytes, out int connected);
        return (packets, bytes, connected != 0);
    }

    /// <summary>性能浮窗的 6 个指标（fps / 编码 / 解码 / 网络 / 丢包 / 码率）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PerfStats
    {
        public float Fps;
        public float EncodeMs;
        public float DecodeMs;
        public float RttMs;
        public float LossPercent;
        public float BitrateKbps;
    }

    // ---- 性能信息卡片（原生分层窗口） ----
    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PERF_Init(IntPtr ownerRoot);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PERF_SetConfig(int pos, int items, int cornerSingle, double opacity);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PERF_Update(IntPtr lines, int count, int singleLine);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PERF_Destroy();

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ML_GetVideoInfo(out int codec, out int hardware);

    /// <summary>当前解码器：codec 1=H264 2=HEVC 3=AV1；hardware=1 表示 d3d11va 硬解。</summary>
    public (int Codec, bool Hardware) GetVideoInfo()
    {
        ML_GetVideoInfo(out var c, out var h);
        return (c, h == 1);
    }

    public void PerfInit(IntPtr ownerRoot) => PERF_Init(ownerRoot);

    public void PerfConfig(int pos, int items, bool cornerSingle, double opacity)
        => PERF_SetConfig(pos, items, cornerSingle ? 1 : 0, opacity);

    /// <summary>更新卡片内容；单行用空格分隔。</summary>
    public void PerfUpdate(string[] lines, bool singleLine)
    {
        var handles = new IntPtr[lines.Length];
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                handles[i] = Marshal.StringToHGlobalUni(lines[i]);
            }

            var buf = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            try
            {
                Marshal.Copy(handles, 0, buf, handles.Length);
                PERF_Update(buf, lines.Length, singleLine ? 1 : 0);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            foreach (var h in handles)
            {
                if (h != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(h);
                }
            }
        }
    }

    public void PerfDestroy() => PERF_Destroy();

    public PerfStats GetPerf()
    {
        MS_Perf(out var stats);
        return stats;
    }

    /// <summary>音频状态：设备采样率/声道、已播帧数、欠载次数（channels=0 表示没有输出设备）。</summary>
    public (int SampleRate, int Channels, long PlayedFrames, long Underruns) AudioStats()
    {
        MA_Stats(out int rate, out int channels, out long played, out long underruns);
        return (rate, channels, played, underruns);
    }

    public int SendMouseMove(int dx, int dy) => MS_SendMouseMove(dx, dy);

    /// <summary>
    /// 绝对鼠标：坐标是「流画面像素」（0..width-1 / 0..height-1）。
    /// 和发送相对增量相比，不会累积误差，也不受主机端指针加速影响。
    /// </summary>
    public int SendMousePosition(int x, int y, int width, int height) => MS_SendMousePosition(x, y, width, height);

    /// <summary>键盘：vk 用 Win32 Virtual Key（和 WinUI 的 VirtualKey 数值一致）。</summary>
    public int SendKey(int vk, bool down, int modifiers = 0) => MS_SendKey(vk, down ? 1 : 0, modifiers);

    /// <summary>鼠标键：1 左 2 中 3 右 4 X1 5 X2。</summary>
    public int SendMouseButton(int button, bool down) => MS_SendMouseButton(button, down ? 1 : 0);

    /// <summary>滚轮：一格 = 1，正数向上。</summary>
    public int SendMouseScroll(int clicks) => MS_SendMouseScroll(clicks);

    public int SendMouseHScroll(int clicks) => MS_SendMouseHScroll(clicks);

    /// <summary>请求主机立刻发关键帧（画面卡住时恢复用）。</summary>
    public void RequestIdrFrame() => MS_RequestIdr();

    public void Dispose()
    {
        if (_initialized)
        {
            MS_Stop();   // 关窗口时把主机的会话也收掉
            ML_Close();
            _initialized = false;
        }

        if (_childMode)
        {
            ML_DetachChild();
        }

        // Win32 串流窗口（和它上面的标题栏菜单按钮）是 native 自己 CreateWindow 的，
        // 不归 WinUI 管。不显式销毁的话，退出串流后画面没了但空窗口会一直留在屏幕上。
        // 顺序：先 ML_DetachChild（销毁视频子窗口、释放 swapchain），再销毁父窗口。
        SUI_Destroy();

        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
