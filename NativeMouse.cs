using System;
using System.Runtime.InteropServices;

namespace MoonlightWinUI;

/// <summary>
/// 指针锁定用的 Win32 薄封装：藏系统指针、把它夹在画面里、再钉回中心。
/// 只在真正进入相对捕获时才调用，演示流/没连上时一个都不会触发。
/// </summary>
internal static class NativeMouse
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern int ShowCursor(bool show);

    /// <summary>把指针限制在矩形内（屏幕物理像素）。</summary>
    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    public static extern bool ClipCursorTo(ref RECT rect);

    /// <summary>解除限制（传 nint.Zero）。</summary>
    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    public static extern bool ClipCursorFree(IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    /// <summary>全局按键状态（锁定时用来判断 Ctrl+Alt+Shift+Q）。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;

    // ---------------- 本地光标渲染（主机发形状，客户端画） ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCursor();

    private static IntPtr _streamCursor = IntPtr.Zero;
    private static readonly IntPtr ArrowCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)); // IDC_ARROW

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    /// <summary>窗口所属的 GUI 线程（光标归它管，直接在本线程 SetCursor 会被它重置）。</summary>
    public static IntPtr CursorOwnerWindow;

    private static IntPtr SetCursorOnWindowThread(IntPtr cursor)
    {
        if (CursorOwnerWindow == IntPtr.Zero)
        {
            return SetCursor(cursor);
        }

        var targetThread = GetWindowThreadProcessId(CursorOwnerWindow, IntPtr.Zero);
        var current = GetCurrentThreadId();
        if (targetThread == 0 || targetThread == current)
        {
            return SetCursor(cursor);
        }

        var attached = AttachThreadInput(current, targetThread, true);
        try
        {
            return SetCursor(cursor);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(current, targetThread, false);
            }
        }
    }
    /// <summary>把主机发来的 BGRA 像素做成 HCURSOR（会释放上一张）。</summary>
    public static bool SetStreamCursor(byte[] bgra, int width, int height, int hotX, int hotY)
    {
        if (bgra is null || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return false;
        }

        var color = CreateBitmap(width, height, 1, 32, bgra);
        // 1bpp 掩码：每行按 4 字节对齐，尺寸不对 CreateBitmap 会直接失败
        var maskStride = ((width + 31) / 32) * 4;
        var mask = CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
        if (color == IntPtr.Zero || mask == IntPtr.Zero)
        {
            if (color != IntPtr.Zero) { DeleteObject(color); }
            if (mask != IntPtr.Zero) { DeleteObject(mask); }
            return false;
        }

        var info = new ICONINFO
        {
            fIcon = false,
            xHotspot = Math.Clamp(hotX, 0, width - 1),
            yHotspot = Math.Clamp(hotY, 0, height - 1),
            hbmMask = mask,
            hbmColor = color,
        };

        var cursor = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        if (cursor == IntPtr.Zero)
        {
            return false;
        }

        // 先把新光标设上去，再销毁旧的 —— 反过来会把"当前正在用"的句柄销毁掉，
        // Windows 就会退回系统箭头（表现就是"形状同步没生效"）
        var previous = _streamCursor;
        _streamCursor = cursor;
        SetCursor(cursor);

        if (previous != IntPtr.Zero)
        {
            DestroyCursor(previous);
        }

        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    /// <summary>重复设一次，压掉 WinUI 在 WM_SETCURSOR 里重置成箭头。</summary>
    public static void ReapplyStreamCursor()
    {
        if (_streamCursor != IntPtr.Zero)
        {
            SetCursor(_streamCursor);
        }
    }

    /// <summary>主机隐藏光标时用：把光标设成空的（每次 tick 重设）。</summary>
    public static void HideStreamCursor()
    {
        SetCursorOnWindowThread(IntPtr.Zero);
    }

    /// <summary>收流后恢复系统箭头。</summary>
    public static void RestoreSystemCursor()
    {
        if (_streamCursor != IntPtr.Zero)
        {
            DestroyCursor(_streamCursor);
            _streamCursor = IntPtr.Zero;
        }

        SetCursorOnWindowThread(ArrowCursor);
    }
}
