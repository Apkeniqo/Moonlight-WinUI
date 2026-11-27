using System;
using System.Formats.Asn1;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MoonlightWinUI;

/// <summary>
/// Moonlight / GameStream 配对协议（Sunshine 兼容）。
///
/// 五个阶段（Gen7+ 用 SHA-256；主机 appversion 7.1.x）：
///   1. getservercert      HTTP  —— 拿服务器证书；这一请求会被主机挂起，直到用户在网页里确认 PIN
///   2. clientchallenge    HTTP  —— 发 AES 加密的随机挑战
///   3. serverchallengeresp HTTP —— 回哈希，拿 <c>pairingsecret</c>（服务器签名）
///   4. clientpairingsecret HTTP —— 客户端私钥签名，确认身份
///   5. pairchallenge      HTTPS —— 带客户端证书，主机正式收下这把证书
///
/// 密钥：AES-128-ECB，key = SHA256(salt || pin)[0..16]，无填充。
/// </summary>
internal static class Pairing
{
    public sealed record Result(bool Success, string Message, string? ServerCertPem);

    // 配对时告诉主机的客户端名：用本机主机名，主机端列表里能直接认出是哪台电脑
    private static string DeviceName =>
        string.IsNullOrWhiteSpace(Environment.MachineName) ? "Moonlight" : Environment.MachineName;
    private const int ShortTimeoutMs = 10_000;

    /// <summary>完整配对流程。host 可以是主机名或 IP；httpPort 是主机的 HTTP 端口（默认 47989，HTTPS 取 -5）。</summary>
    public static async Task<Result> PairAsync(
        string host,
        string pin,
        string certPath,
        string keyPath,
        string uniqueId,
        int httpPort = 47989,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        void Log(string line) => log?.Invoke(line);

        if (string.IsNullOrWhiteSpace(host))
        {
            return new Result(false, "主机为空", null);
        }

        if (pin.Length != 4 || !int.TryParse(pin, out _))
        {
            return new Result(false, "PIN 必须是 4 位数字", null);
        }

        X509Certificate2 clientCert;
        try
        {
            clientCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }
        catch (Exception ex)
        {
            return new Result(false, "读取客户端证书失败：" + ex.Message, null);
        }

        using (clientCert)
        {
            var certPem = await System.IO.File.ReadAllTextAsync(certPath, cancellationToken).ConfigureAwait(false);
            var uid = Uri.EscapeDataString(uniqueId);
            var salt = RandomNumberGenerator.GetBytes(16);
            var aesKey = AesKey(salt, pin);
            var httpBase = $"http://{host}:{httpPort}";
            var httpsPort = Math.Max(1, httpPort - 5);   // Sunshine：47989 HTTP ↔ 47984 HTTPS

            using var shortHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // 阶段 1 要等用户在主机网页上点确认，单独给它长超时
            using var pinHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            try
            {
                // ---------------- 阶段 1：服务器证书 ----------------
                var query1 =
                    $"pair?uniqueid={uid}&uuid={uid}&devicename={DeviceName}&updateState=1" +
                    $"&phrase=getservercert&salt={Hex(salt)}&clientcert={Hex(Encoding.UTF8.GetBytes(certPem))}";
                Log("stage1: 请求服务器证书（等待主机确认 PIN）");
                var x1 = await GetXmlAsync(pinHttp, httpBase + "/" + query1, cancellationToken).ConfigureAwait(false);
                if (!CheckStatus(x1, out var status1))
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段1 " + status1, log).ConfigureAwait(false);
                }

                if (XmlValue(x1, "paired") != "1")
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段1 未配对（主机可能正在配对）", log).ConfigureAwait(false);
                }

                var serverCertPem = Encoding.UTF8.GetString(HexDecode(XmlValue(x1, "plaincert")));
                var serverCert = X509Certificate2.CreateFromPem(serverCertPem);
                Log($"stage1: 拿到服务器证书 {serverCert.Subject}");

                // ---------------- 阶段 2：客户端挑战 ----------------
                var clientChallenge = RandomNumberGenerator.GetBytes(16);
                var encryptedChallenge = AesEcb(clientChallenge, aesKey, encrypt: true);
                var query2 =
                    $"pair?uniqueid={uid}&uuid={uid}&devicename={DeviceName}&updateState=1" +
                    $"&clientchallenge={Hex(encryptedChallenge)}";
                Log("stage2: 发送客户端挑战");
                var x2 = await GetXmlAsync(shortHttp, httpBase + "/" + query2, cancellationToken).ConfigureAwait(false);
                if (!CheckStatus(x2, out var status2) || XmlValue(x2, "paired") != "1")
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段2 " + status2, log).ConfigureAwait(false);
                }

                var challengeResponseData = AesEcb(HexDecode(XmlValue(x2, "challengeresponse")), aesKey, encrypt: false);
                if (challengeResponseData.Length < 32 + 16)
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段2 挑战响应长度不对", log).ConfigureAwait(false);
                }

                var serverResponse = challengeResponseData[..32];               // SHA256(...)
                var challengeTail = challengeResponseData[32..48];              // 服务器 challenge 后半段

                // ---------------- 阶段 3：回应服务器挑战 ----------------
                var clientSecret = RandomNumberGenerator.GetBytes(16);
                var toHash = Concat(challengeTail, CertificateSignature(clientCert), clientSecret);
                var encryptedHash = AesEcb(SHA256.HashData(toHash), aesKey, encrypt: true);
                var query3 =
                    $"pair?uniqueid={uid}&uuid={uid}&devicename={DeviceName}&updateState=1" +
                    $"&serverchallengeresp={Hex(encryptedHash)}";
                Log("stage3: 回应服务器挑战");
                var x3 = await GetXmlAsync(shortHttp, httpBase + "/" + query3, cancellationToken).ConfigureAwait(false);
                if (!CheckStatus(x3, out var status3) || XmlValue(x3, "paired") != "1")
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段3 " + status3, log).ConfigureAwait(false);
                }

                var pairingSecret = HexDecode(XmlValue(x3, "pairingsecret"));
                if (pairingSecret.Length <= 16)
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段3 pairingsecret 太短", log).ConfigureAwait(false);
                }

                var serverSecret = pairingSecret[..16];
                var serverSignature = pairingSecret[16..];

                // 服务器签名必须能用 serverCert 验过，否则是中间人
                using (var serverRsa = serverCert.GetRSAPublicKey())
                {
                    if (serverRsa is null ||
                        !serverRsa.VerifyData(serverSecret, serverSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    {
                        return await FailAsync(shortHttp, httpBase, uid, "服务器签名校验失败（可能的中间人）", log).ConfigureAwait(false);
                    }
                }

                // PIN 校验：SHA256(clientChallenge || serverCertSig || serverSecret) 应等于 serverResponse
                var expected = Concat(clientChallenge, CertificateSignature(serverCert), serverSecret);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), serverResponse))
                {
                    return await FailAsync(shortHttp, httpBase, uid, "PIN 不正确", log).ConfigureAwait(false);
                }

                // ---------------- 阶段 4：客户端配对密钥 ----------------
                byte[] clientSignature;
                using (var clientRsa = clientCert.GetRSAPrivateKey())
                {
                    if (clientRsa is null)
                    {
                        return new Result(false, "客户端证书没有私钥", null);
                    }

                    clientSignature = clientRsa.SignData(clientSecret, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }

                var clientPairingSecret = Concat(clientSecret, clientSignature);
                var query4 =
                    $"pair?uniqueid={uid}&uuid={uid}&devicename={DeviceName}&updateState=1" +
                    $"&clientpairingsecret={Hex(clientPairingSecret)}";
                Log("stage4: 提交客户端配对密钥");
                var x4 = await GetXmlAsync(shortHttp, httpBase + "/" + query4, cancellationToken).ConfigureAwait(false);
                if (!CheckStatus(x4, out var status4) || XmlValue(x4, "paired") != "1")
                {
                    return await FailAsync(shortHttp, httpBase, uid, "阶段4 " + status4, log).ConfigureAwait(false);
                }

                // ---------------- 阶段 5：HTTPS 收尾 ----------------
                Log("stage5: HTTPS pairchallenge");
                var x5 = await PairChallengeAsync(host, httpsPort, BuildChallengeQuery(uid), certPath, keyPath, cancellationToken).ConfigureAwait(false);
                if (!CheckStatus(x5, out var status5) || XmlValue(x5, "paired") != "1")
                {
                    // 别调 unpair：阶段 4 主机已经收下证书，清会话会把刚建立的配对撤销
                    return new Result(false, "收尾阶段失败 " + status5 + "（主机可能已接受证书）", null);
                }

                Log("pairing: 成功");
                return new Result(true, "配对成功", serverCertPem);
            }
            catch (TaskCanceledException)
            {
                return new Result(false, "配对超时（PIN 没在主机网页确认？）", null);
            }
            catch (Exception ex)
            {
                Log("pairing: 异常 " + ex);
                return new Result(false, "配对异常：" + ex.Message, null);
            }
        }
    }

    /// <summary>自检：只走「客户端证书 + HTTPS」这一段（ML_TLS_TEST 用），返回状态摘要。</summary>
    internal static async Task<string> ProbeTlsAsync(string host, string certPath, string keyPath, string uniqueId)
    {
        var uid = Uri.EscapeDataString(uniqueId);
        var target = $"/serverinfo?uniqueid={uid}&uuid={uid}";

        if (NativeTls.TryGet(host, 47984, target, certPath, keyPath, out var nativeBody, out var nativeError))
        {
            return "native OK " + Summarize(nativeBody);
        }

        try
        {
            using var tlsCert = LoadTlsClientCert(certPath, keyPath);
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            handler.ClientCertificates.Add(tlsCert);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync($"https://{host}:47984{target}").ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return $"native FAIL({nativeError}) | sChannel {(int)response.StatusCode} {Summarize(body)}";
        }
        catch (Exception ex)
        {
            return $"native FAIL({nativeError}) | sChannel EX {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string Summarize(string xml)
    {
        var status = System.Text.RegularExpressions.Regex.Match(xml, "PairStatus>(\\d+)<");
        var hostname = System.Text.RegularExpressions.Regex.Match(xml, "<hostname>([^<]*)</hostname>");
        return $"host={hostname.Groups[1].Value} pairStatus={(status.Success ? status.Groups[1].Value : "?")}";
    }

    /// <summary>pairtls.dll（OpenSSL）：带客户端证书的 HTTPS GET，不依赖证书库。</summary>

    // ---------------- 底层 ----------------

    private static string BuildChallengeQuery(string uid) =>
        $"pair?uniqueid={uid}&uuid={uid}&devicename={DeviceName}&updateState=1&phrase=pairchallenge";

    private static async Task<XDocument> PairChallengeAsync(
        string host, int httpsPort, string query5, string certPath, string keyPath, CancellationToken ct)
    {
        // 首选原生 OpenSSL：不碰证书库，低完整性进程也能用（见 PROGRESS.md §8）
        if (NativeTls.TryGet(host, httpsPort, "/" + query5, certPath, keyPath, out var body, out _))
        {
            return XDocument.Parse(body);
        }

        // 回退：SChannel（需要把私钥持久化进用户证书库；应用在 Low IL 下会失败）
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        handler.ClientCertificates.Add(LoadTlsClientCert(certPath, keyPath));
        using var https = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await https.GetAsync($"https://{host}:{httpsPort}/{query5}", ct).ConfigureAwait(false);
        var fallbackBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return XDocument.Parse(fallbackBody);
    }

    /// <summary>
    /// SChannel 不认「临时密钥」的客户端证书（会报“没有识别提供给安全包的凭证”或“拒绝访问”），
    /// 必须把 PEM 转成 PFX 再以用户密钥集导入并把密钥持久化；容器随用户配置保存（DPAPI 保护）。
    /// </summary>
    private static X509Certificate2 LoadTlsClientCert(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        var pfx = pem.Export(X509ContentType.Pfx, "moonlight");
#pragma warning disable SYSLIB0057 // net8 还没有 X509CertificateLoader
        return new X509Certificate2(pfx, "moonlight", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
#pragma warning restore SYSLIB0057
    }

    private static async Task<XDocument> GetXmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return XDocument.Parse(body);
    }

    private static bool CheckStatus(XDocument doc, out string message)
    {
        var root = doc.Root;
        var code = root?.Attribute("status_code")?.Value ?? "?";
        message = $"(status={code} {root?.Attribute("status_message")?.Value})";
        return code == "200";
    }

    private static string XmlValue(XDocument doc, string name) =>
        doc.Root?.Element(name)?.Value ?? string.Empty;

    private static async Task<Result> FailAsync(HttpClient http, string httpBase, string uid, string message, Action<string>? log)
    {
        log?.Invoke("pairing: 失败 " + message + "，清理主机侧会话");
        try
        {
            using var _ = await http.GetAsync($"{httpBase}/unpair?uniqueid={uid}").ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不影响结论
        }

        return new Result(false, message, null);
    }

    private static byte[] AesKey(byte[] salt, string pin)
    {
        var salted = Concat(salt, Encoding.UTF8.GetBytes(pin));
        var hash = SHA256.HashData(salted);
        return hash[..16];
    }

    private static byte[] AesEcb(byte[] data, byte[] key, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>证书自身的签名字节（DER 里 signatureValue BIT STRING 的内容，RSA 2048 是 256 字节）。</summary>
    private static byte[] CertificateSignature(X509Certificate2 cert)
    {
        var reader = new AsnReader(cert.RawData, AsnEncodingRules.DER);
        var certificate = reader.ReadSequence();
        certificate.ReadEncodedValue();   // tbsCertificate
        certificate.ReadEncodedValue();   // signatureAlgorithm
        return certificate.ReadBitString(out _).ToArray();
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private static byte[] HexDecode(string hex) => Convert.FromHexString(hex.Trim());

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }

        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}
