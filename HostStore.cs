using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoonlightWinUI;

/// <summary>一台已配对的电脑。</summary>
public sealed class HostEntry
{
    /// <summary>连接用的地址（主机名或 IP），发请求都用它。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>显示名（一般是主机自己报的 hostname），空就用地址。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Sunshine 的 HTTP 端口（默认 47989；HTTPS 取 -5）。</summary>
    public int Port { get; set; } = 47989;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    /// <summary>网卡 MAC（唤醒用），从 /serverinfo 里拿，可能为空。</summary>
    public string Mac { get; set; } = string.Empty;

    [JsonIgnore]
    public int HttpsPort => Math.Max(1, Port - 5);
}

/// <summary>
/// 电脑列表的持久化：exe 同目录的 hosts.json。
/// 配对成功后往里加一条，启动时读出来生成卡片 —— 不再写死。
/// </summary>
public static class HostStore
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "hosts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>首次运行（没有 hosts.json）时给两台已知电脑，之后完全以文件为准。</summary>
    private static List<HostEntry> Defaults() => new()
    {
        new HostEntry { Address = "DESKTOP-M9RGVD3", Name = "DESKTOP-M9RGVD3", Port = 47989 },
        new HostEntry { Address = "LAPTOP-8BQF1SDN", Name = "LAPTOP-8BQF1SDN", Port = 47989 },
    };

    public static List<HostEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<HostEntry>>(File.ReadAllText(FilePath), JsonOptions);
                if (list is { Count: > 0 })
                {
                    var cleaned = list
                        .Where(h => !string.IsNullOrWhiteSpace(h.Address))
                        .Select(h =>
                        {
                            h.Address = h.Address.Trim();
                            h.Name = string.IsNullOrWhiteSpace(h.Name) ? h.Address : h.Name.Trim();
                            h.Port = h.Port is > 0 and <= 65535 ? h.Port : 47989;
                            return h;
                        })
                        .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();

                    if (cleaned.Count > 0)
                    {
                        return cleaned;
                    }
                }
            }
        }
        catch
        {
            // 文件坏了就当没有，下面写一份新的
        }

        var defaults = Defaults();
        TrySave(defaults);
        return defaults;
    }

    public static void Save(IReadOnlyList<HostEntry> hosts) => TrySave(hosts);

    private static void TrySave(IReadOnlyList<HostEntry> hosts)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(hosts, JsonOptions));
        }
        catch
        {
            // 低完整性目录偶尔写不了：卡片照样能用，只是记不住
        }
    }

    /// <summary>加一台电脑（按地址去重）；返回 true 表示是新加的。</summary>
    public static bool AddOrUpdate(List<HostEntry> hosts, string address, string? name, int port = 47989)
    {
        address = address.Trim();
        if (address.Length == 0)
        {
            return false;
        }

        if (port is <= 0 or > 65535)
        {
            port = 47989;
        }

        var existing = hosts.FirstOrDefault(h => string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase));

        // 同一台主机换了 IP：主机名（hostname）相同就沿用原条目、只更新地址，
        // 否则列表里会出现两条同一台电脑（旧的离线、新的在线），看起来像"要重新配对"。
        if (existing is null && !string.IsNullOrWhiteSpace(name))
        {
            var trimmedName = name.Trim();
            existing = hosts.FirstOrDefault(h =>
                !string.IsNullOrWhiteSpace(h.Name) &&
                string.Equals(h.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Address = address;
                existing.Port = port;
                return false;
            }
        }

        if (existing is null)
        {
            hosts.Add(new HostEntry
            {
                Address = address,
                Name = string.IsNullOrWhiteSpace(name) ? address : name.Trim(),
                Port = port,
            });
            return true;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            existing.Name = name.Trim();
        }

        existing.Port = port;
        return false;
    }
}
