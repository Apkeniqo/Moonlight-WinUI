using System;
using System.Runtime.InteropServices;

namespace MoonlightWinUI;

/// <summary>
/// 主显示器的物理像素尺寸。
///
/// 不能用 <c>GetSystemMetrics(SM_CXSCREEN) * RasterizationScale</c>：本进程是 per-monitor v2
/// DPI-aware，GetSystemMetrics 已经返回物理像素，再乘缩放就会在 150% 下把 2560×1600 算成
/// 3840×2400。EnumDisplaySettings 返回显示模式本身的像素，与 DPI 缩放无关。
/// </summary>
internal static class ScreenInfo
{
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DmDeviceName;

        public ushort DmSpecVersion;
        public ushort DmDriverVersion;
        public ushort DmSize;
        public ushort DmDriverExtra;
        public uint DmFields;
        public int DmPositionX;
        public int DmPositionY;
        public uint DmDisplayOrientation;
        public uint DmDisplayFixedOutput;
        public short DmColor;
        public short DmDuplex;
        public short DmYResolution;
        public short DmTTOption;
        public short DmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DmFormName;

        public ushort DmLogPixels;
        public uint DmBitsPerPel;
        public uint DmPelsWidth;
        public uint DmPelsHeight;
        public uint DmDisplayFlags;
        public uint DmDisplayFrequency;
        public uint DmIcmMethod;
        public uint DmIcmIntent;
        public uint DmMediaType;
        public uint DmDitherType;
        public uint DmReserved1;
        public uint DmReserved2;
        public uint DmPanningWidth;
        public uint DmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevModeW devMode);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>主屏物理分辨率（像素）。取不到时退回 GetSystemMetrics。</summary>
    public static (int Width, int Height) PrimaryPixels()
    {
        try
        {
            var mode = new DevModeW { DmSize = (ushort)Marshal.SizeOf<DevModeW>() };
            if (EnumDisplaySettings(null, EnumCurrentSettings, ref mode) &&
                mode.DmPelsWidth > 0 && mode.DmPelsHeight > 0)
            {
                return ((int)mode.DmPelsWidth, (int)mode.DmPelsHeight);
            }
        }
        catch
        {
            // 退回 GetSystemMetrics
        }

        return (GetSystemMetrics(0), GetSystemMetrics(1));
    }
}
