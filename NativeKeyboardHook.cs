using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MoonlightWinUI;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）：用来拦 Win 键 —— XAML 收不到 Win 键，
/// 不拦的话本地会弹开始菜单，而且主机永远收不到 keyup（表现为"主机一直按着 Win"）。
/// </summary>
internal static class NativeKeyboardHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    public const int VkLeftWin = 0x5B;
    public const int VkRightWin = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>当前前台窗口句柄（钩子里判断"我们的窗口是不是在前台"）。</summary>
    public static IntPtr ForegroundWindow => GetForegroundWindow();

    /// <summary>返回 true 表示已经处理并吞掉该按键（不再交给系统）。</summary>
    public static Func<int, bool, bool>? ShouldSwallowKey;

    private static IntPtr _hook;
    private static LowLevelKeyboardProc? _proc;   // 保持引用，防止被 GC

    public static bool Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var isDown = message is WmKeyDown or WmSysKeyDown;
            var isUp = message is WmKeyUp or WmSysKeyUp;

            if (isDown || isUp)
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var handler = ShouldSwallowKey;
                if (handler is not null)
                {
                    try
                    {
                        if (handler(info.vkCode, isDown))
                        {
                            return (IntPtr)1;   // 吞掉：系统不再处理（不会弹开始菜单）
                        }
                    }
                    catch
                    {
                        // 钩子里绝不能抛异常
                    }
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
