using System.Reflection;

namespace MoonlightWinUI;

/// <summary>
/// 当前程序版本。
/// 源头是项目根目录的 VERSION 文件 —— csproj 把它读进 &lt;Version&gt;，
/// 编译后体现在 AssemblyInformationalVersion 上；D:\Projects\moonlight-sync-build.ps1
/// 也用同一个文件决定同步方向，所以两边不会不一致。
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // "1.0.0+abcdef" 这种带源码信息的形式只留版本号
                var plus = info.IndexOf('+');
                return (plus > 0 ? info[..plus] : info).Trim();
            }

            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
