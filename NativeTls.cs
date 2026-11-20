using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MoonlightWinUI;

/// <summary>
/// pairtls.dll（OpenSSL）薄封装：带客户端证书的 HTTPS GET。
/// 不经过 Windows 证书库，所以应用以低完整性运行时也能用（见 PROGRESS §6）。
/// </summary>
internal static class NativeTls
{
    private const string Dll = "pairtls.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PT_Get(
        string host, int port, string target, string certPath, string keyPath,
        byte[] bodyOut, int bodyCap, byte[] errOut, int errCap);

    /// <summary>图片等二进制响应：缓冲区给到 4 MB（实测封面最大约 2 MB）。</summary>
    public static bool TryGetBytes(
        string host, int port, string target, string certPath, string keyPath,
        out byte[] body, out string error)
    {
        body = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            var buffer = new byte[4 * 1024 * 1024];
            var errorBuffer = new byte[1024];
            var length = PT_Get(host, port, target, certPath, keyPath,
                buffer, buffer.Length, errorBuffer, errorBuffer.Length);

            if (length < 0)
            {
                error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
                return false;
            }

            body = new byte[length];
            Buffer.BlockCopy(buffer, 0, body, 0, length);
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "pairtls.dll 缺失";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "pairtls.dll 版本过旧（没有 PT_Get）";
            return false;
        }
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PT_Request(
        string method, string host, int port, string target, string certPath, string keyPath,
        string? contentType, string? extraHeaders, byte[]? body, int bodyLength,
        byte[] bodyOut, int bodyCap, byte[] errOut, int errCap);

    /// <summary>
    /// 上传剪贴板大块：POST /api/v1/clipboard/blob，头 X-Clipboard-Mime，正文是原始字节。
    /// 主机返回 {"expires_in":60,"id":"&lt;uuid&gt;","size":N}，取 id 给控制流的 kind=3 引用用。
    /// </summary>
    public static bool TryUploadBlob(
        string host, int port, string mime, byte[] data,
        string certPath, string keyPath, out string id, out string error)
    {
        id = string.Empty;
        error = string.Empty;

        try
        {
            var buffer = new byte[16 * 1024];
            var errorBuffer = new byte[1024];
            var length = PT_Request("POST", host, port, "/api/v1/clipboard/blob", certPath, keyPath,
                mime, $"X-Clipboard-Mime: {mime}\r\n", data, data.Length,
                buffer, buffer.Length, errorBuffer, errorBuffer.Length);

            if (length < 0)
            {
                error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
                return false;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, length);
            id = ExtractJsonString(json, "id");
            if (string.IsNullOrEmpty(id))
            {
                error = "响应里没有 id：" + json;
                return false;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            error = "pairtls.dll 缺失";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "pairtls.dll 版本过旧（没有 PT_Request）";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>下载剪贴板大块：GET /api/v1/clipboard/blob/&lt;id&gt;（缓冲区给到 32 MB）。</summary>
    public static bool TryDownloadBlob(
        string host, int port, string id,
        string certPath, string keyPath, out byte[] body, out string error)
    {
        body = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            var buffer = new byte[32 * 1024 * 1024];
            var errorBuffer = new byte[1024];
            var length = PT_Get(host, port, "/api/v1/clipboard/blob/" + id, certPath, keyPath,
                buffer, buffer.Length, errorBuffer, errorBuffer.Length);

            if (length < 0)
            {
                error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
                return false;
            }

            body = new byte[length];
            Buffer.BlockCopy(buffer, 0, body, 0, length);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>从简单 JSON 里取一个字符串字段（够用，不引额外依赖）。</summary>
    public static string ExtractJsonString(string json, string key)
    {
        var marker = "\"" + key + "\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = json.IndexOf('"', start);
        return end > start ? json.Substring(start, end - start) : string.Empty;
    }
    public static bool TryGet(
        string host, int port, string target, string certPath, string keyPath,
        out string body, out string error)
    {
        body = string.Empty;
        error = string.Empty;

        try
        {
            var bodyBuffer = new byte[64 * 1024];
            var errorBuffer = new byte[1024];
            var length = PT_Get(host, port, target, certPath, keyPath,
                bodyBuffer, bodyBuffer.Length, errorBuffer, errorBuffer.Length);

            if (length < 0)
            {
                error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
                return false;
            }

            body = Encoding.UTF8.GetString(bodyBuffer, 0, length);
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "pairtls.dll 缺失";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "pairtls.dll 版本过旧（没有 PT_Get）";
            return false;
        }
    }
}
