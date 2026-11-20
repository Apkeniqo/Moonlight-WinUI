using System;
using System.Runtime.InteropServices;

namespace MoonlightWinUI;

/// <summary>
/// 用 SetWindowSubclass 截 WM_SETCURSOR —— 这是 WinUI3 里唯一能真正改系统指针的办法
/// （直接 SetCursor 会被 WinUI 的输入线程覆盖；子类化后在 DefSubclassProc 之前返回 1 就稳了）。
/// 参考：Microsoft Q&A "In WinUI3, how can I change the mouse cursor from Arrow to HourGlass"。
/// </summary>
internal static class WindowSubclass
{
    public const int WmSetCursor = 0x0020;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static SubclassProc? _proc;      // 保持引用，防止被 GC
    private static Func<IntPtr, uint, IntPtr, IntPtr, bool>? _handler;
    private static IntPtr _hwnd;

    /// <summary>安装子类化；handler 返回 true 表示已处理（不会再走 WinUI 的默认光标逻辑）。</summary>
    public static bool Install(IntPtr hwnd, Func<IntPtr, uint, IntPtr, IntPtr, bool> handler)
    {
        if (_hwnd != IntPtr.Zero)
        {
            return true;
        }

        _hwnd = hwnd;
        _handler = handler;
        _proc = Callback;
        return SetWindowSubclass(hwnd, _proc, 0, 0);
    }

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);

    private static readonly System.Collections.Generic.List<(IntPtr Hwnd, SubclassProc Proc)> Installed = new();

    /// <summary>把主窗口的所有子 HWND 都子类化（WinUI 的光标其实由内容岛子窗口处理）。</summary>
    public static int InstallForAllChildren(IntPtr parent, Func<IntPtr, uint, IntPtr, IntPtr, bool> handler)
    {
        var ok = Install(parent, handler) ? 1 : 0;
        EnumChildWindows(parent, (child, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassNameW(child, sb, 256);
            var proc = new SubclassProc(Callback);
            if (SetWindowSubclass(child, proc, (IntPtr)1, IntPtr.Zero))
            {
                Installed.Add((child, proc));
                ok++;
            }

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"),
                    $"{DateTime.Now:HH:mm:ss} subclass child {sb} hwnd={child}\n");
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);
        return ok;
    }

    public static void Uninstall()
    {
        if (_hwnd != IntPtr.Zero && _proc is not null)
        {
            RemoveWindowSubclass(_hwnd, _proc, 0);
        }

        foreach (var (hwnd, proc) in Installed)
        {
            RemoveWindowSubclass(hwnd, proc, (IntPtr)1);
        }
        Installed.Clear();

        _hwnd = IntPtr.Zero;
        _proc = null;
        _handler = null;
    }

    private static IntPtr Callback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        var handler = _handler;
        if (handler is not null)
        {
            try
            {
                if (handler(hWnd, msg, wParam, lParam))
                {
                    return (IntPtr)1;
                }
            }
            catch
            {
                // 子类回调里绝不能抛异常
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
