using System;
using System.Net;
using System.Net.Sockets;

namespace MoonlightWinUI;

/// <summary>WOL 魔术包（UDP 9 / 7，广播）。</summary>
internal static class WakeOnLan
{
    public static bool Send(string mac)
    {
        var clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
        if (clean.Length != 12)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        }

        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (var i = 1; i <= 16; i++)
        {
            Buffer.BlockCopy(bytes, 0, packet, i * 6, 6);
        }

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
            return true;
        }
        catch
        {
            return false;
        }
    }
}