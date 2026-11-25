using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MoonlightWinUI;

/// <summary>
/// 手柄输入（P1/P2）：XInput 轮询 → LiSendMultiControllerEvent；主机震动 → XInputSetState。
/// 先支持手柄 0（XInput 四槽中的第一个活动手柄）。
/// </summary>
public sealed class GamepadInput : IDisposable
{
    // Moonlight 的按键位与 XInput 的 wButtons 完全一致，可直接透传。
    private const int ButtonGuide = 0x0400;

    private readonly VideoSurface _video;
    private readonly DispatcherTimer _timer;
    private bool _connected;
    private int _lastButtons = -1;
    private int _lastLx, _lastLy, _lastRx, _lastRy, _lastLt, _lastRt;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int index, out XINPUT_STATE state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern int XInputSetState(int index, ref XINPUT_VIBRATION vibration);

    public GamepadInput(VideoSurface video)
    {
        _video = video;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    private void Tick()
    {
        if (_disposed || _video is null)
        {
            return;
        }

        var rc = XInputGetState(0, out var state);

        if (rc != 0)
        {
            if (_connected)
            {
                _connected = false;
                // 断开：清空 mask
                _video.SendController(0, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            return;
        }

        var g = state.Gamepad;

        if (!_connected)
        {
            _connected = true;
            // 到达事件：mask bit0 置位
            _video.SendController(0, 1, g.wButtons, g.bLeftTrigger, g.bRightTrigger,
                g.sThumbLX, (short)-g.sThumbLY, g.sThumbRX, (short)-g.sThumbRY);
            _lastButtons = -1;
        }

        // 变化才发（减少带宽）
        if (g.wButtons != _lastButtons ||
            g.sThumbLX != _lastLx || g.sThumbLY != _lastLy ||
            g.sThumbRX != _lastRx || g.sThumbRY != _lastRy ||
            g.bLeftTrigger != _lastLt || g.bRightTrigger != _lastRt)
        {
            _lastButtons = g.wButtons;
            _lastLx = g.sThumbLX;
            _lastLy = g.sThumbLY;
            _lastRx = g.sThumbRX;
            _lastRy = g.sThumbRY;
            _lastLt = g.bLeftTrigger;
            _lastRt = g.bRightTrigger;

            // XInput 的 Y 轴正方向朝上，Moonlight 朝下 → 取反
            _video.SendController(0, 1, g.wButtons, g.bLeftTrigger, g.bRightTrigger,
                g.sThumbLX, (short)-g.sThumbLY, g.sThumbRX, (short)-g.sThumbRY);
        }

        // 震动（P2）：主机下发的强度直接给 XInput
        var (low, high) = _video.GetRumble();
        if (low > 0 || high > 0)
        {
            var vib = new XINPUT_VIBRATION
            {
                wLeftMotorSpeed = (ushort)Math.Clamp(low, 0, 65535),
                wRightMotorSpeed = (ushort)Math.Clamp(high, 0, 65535),
            };
            XInputSetState(0, ref vib);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        try
        {
            var vib = new XINPUT_VIBRATION();
            XInputSetState(0, ref vib);   // 停震
        }
        catch
        {
        }
    }
}
