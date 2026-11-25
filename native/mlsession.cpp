// mlsession.cpp —— 真串流会话：/serverinfo → /launch → LiStartConnection
//
// 三步走：
//   1）MS_Init   ：加载 Qt 版已配对的客户端证书/私钥，建好 SSL_CTX（OpenSSL，不用 WinHTTP）
//   2）MS_Start  ：工作线程里 /serverinfo → 生成 rikey → /launch → 拿到 rtsp:// → LiStartConnection
//   3）解码单元  ：moonlight-common-c 的 submitDecodeUnit 把 LENTRY 链拼成一个完整访问单元，
//                  交给 mlrender 的 ML_PushPacket（它只从队列取包，不关心包从哪来）
//
// 导出（extern "C"，C# P/Invoke 用）：
//   int  MS_Init(const char* certPemPath, const char* keyPemPath, const char* uniqueId);
//       返回 0 成功；-2 证书读不到；-3 私钥读不到；-4 证书/私钥不匹配；-10 网络初始化失败
//   int  MS_Status(char* stage, int stageLen);
//       把中文阶段名写进 stage；返回值 0=空闲/已停止 1=正在建立 2=串流中 -1=失败
//   int  MS_Start(const char* host, int width, int height, int fps, int bitrateKbps, int appId);
//       立即返回（不阻塞 UI）：0 已开始；-1 已经在跑；-2 没 MS_Init；-3 host 为空；-4 参数不合法
//   void MS_Stop(void);                     // 停流并等工作线程收尾
//   int  MS_SendMouseMove(int dx, int dy);  // 0 成功；-1 还没连上
//   int  MS_SendKey(int vk, int down, int modifiers);      // 键盘（Win32 VK）
//   int  MS_SendMouseButton(int button, int down);         // 1 左 2 中 3 右 4 X1 5 X2
//   int  MS_SendMouseScroll(int clicks);                   // 一格 = 1，正数向上
//   int  MS_SendMouseHScroll(int clicks);                  // 横向滚轮（Sunshine 扩展）
//   void MS_Stats(long long* packets, long long* bytes, int* connected);   // 附加：UI 统计
//
// 音频走 mlaudio.cpp（Opus 多流解码 + WASAPI 共享模式播放），这里只转发回调。
//
// 线程模型：MS_Start 只起线程；HTTPS 和 LiStartConnection 都在工作线程。
// UI 线程拿 MS_Status 轮询阶段。MS_Stop 置停止位、打断进行中的 HTTPS/握手，再 join。

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <openssl/err.h>
#include <openssl/rand.h>
#include <openssl/ssl.h>

#include "Limelight.h"

#include <atomic>
#include <cctype>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <deque>
#include <string>
#include <thread>
#include <chrono>
#include <vector>

// mlrender.cpp 里的入口：把一整个访问单元推进解码队列
extern "C" void ML_PushPacket(const void* data, int len);

// mlrender.cpp：按协商结果切 H.264 / HEVC 解码器（VIDEO_FORMAT_* 值）
extern "C" int ML_SetVideoFormat(int mlcFormat);

// mlrender.cpp：解码统计（帧数 / 上一帧解码耗时 ms）
extern "C" void ML_Stats(int* decodedFrames, double* lastDecodeMs);
// mlaudio.cpp：Opus 多流解码 + WASAPI 播放
extern "C" int MA_Init(int audioConfiguration, const OPUS_MULTISTREAM_CONFIGURATION* cfg);
extern "C" void MA_Start(void);
extern "C" void MA_Stop(void);
extern "C" void MA_Shutdown(void);
extern "C" void MA_Push(const void* opusData, int length);

namespace {

// ---------------------------------------------------------------- 常量

constexpr int kHttpsPort = 47984;        // Sunshine 的 HTTPS 端口（serverinfo / launch）
constexpr int kServerInfoTimeoutMs = 8000;
constexpr int kLaunchTimeoutMs = 120000; // 主机启动应用可能要很久，照抄 Qt 的 LAUNCH_TIMEOUT_MS

// ---------------------------------------------------------------- 全局状态

enum class Phase
{
    Idle = 0,       // 空闲 / 已停止
    Starting = 1,   // 正在建立
    Streaming = 2,  // 串流中
    Failed = 3,
    Stopped = 4,
};

std::mutex g_stateLock;          // 保护 g_phase / g_stage
Phase g_phase = Phase::Idle;
std::string g_stage = "idle";

std::string g_certPath;
std::string g_keyPath;
std::string g_uniqueId;
SSL_CTX* g_tls = nullptr;
bool g_wsaStarted = false;

std::thread g_worker;
std::atomic<bool> g_busy{false};       // 工作线程活着
std::atomic<bool> g_stop{false};       // 要求停流
std::atomic<bool> g_connected{false};  // LiStartConnection 成功过

// 正在用的 HTTPS socket：MS_Stop 靠 shutdown 它来打断阻塞的读
std::atomic<SOCKET> g_httpSocket{INVALID_SOCKET};

std::atomic<long long> g_packets{0};
std::atomic<long long> g_bytes{0};

// 0=自动（H264+HEVC+AV1 里挑主机支持的），1=H264，2=HEVC，3=AV1
int g_videoCodec = 0;

// 性能浮窗用的采样状态（只在 UI 线程的 MS_Perf 里读写）
std::atomic<int> g_hostLatencyTenthMs{0};
long long g_perfLastBytes = 0;
int g_perfLastFrames = 0;
double g_perfLastTime = 0;
float g_perfFps = 0;
float g_perfKbps = 0;

double PerfNowSeconds()
{
    using clock = std::chrono::steady_clock;
    static const auto start = clock::now();
    return std::chrono::duration<double>(clock::now() - start).count();
}

CONNECTION_LISTENER_CALLBACKS g_connCb;
DECODER_RENDERER_CALLBACKS g_videoCb;
AUDIO_RENDERER_CALLBACKS g_audioCb;
bool g_callbacksReady = false;

// ---------------------------------------------------------------- 日志

// 和 mlrender 一样写 exe 目录的 decoder.log，方便一次看到网络 + 解码两条线
void SLog(const char* fmt, ...)
{
    static char path[MAX_PATH] = {};
    if (path[0] == 0)
    {
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        char* slash = strrchr(path, '\\');
        if (slash) { *(slash + 1) = 0; }
        strcat_s(path, "decoder.log");
    }

    char line[4096];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof(line), fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);

    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") == 0 && f)
    {
        fprintf(f, "%02d:%02d:%02d.%03d [session] %s\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

const char* PhaseName(Phase phase)
{
    switch (phase)
    {
    case Phase::Idle:      return "idle";
    case Phase::Starting:  return "starting";
    case Phase::Streaming: return "streaming";
    case Phase::Failed:    return "failed";
    case Phase::Stopped:   return "stopped";
    }
    return "?";
}

// 唯一入口：改阶段 + 写日志
void SetStage(Phase phase, const char* fmt, ...)
{
    char text[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);

    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        g_phase = phase;
        g_stage = text;
    }
    SLog("[%s] %s", PhaseName(phase), text);
}

void Fail(const char* fmt, ...)
{
    char text[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);

    std::string stage = std::string("失败：") + text;
    SetStage(Phase::Failed, "%s", stage.c_str());
}

// ---------------------------------------------------------------- 小工具

std::string Trim(const std::string& s)
{
    size_t b = 0;
    size_t e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\r' || s[b] == '\n')) { b++; }
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r' || s[e - 1] == '\n')) { e--; }
    return s.substr(b, e - b);
}

// 从 XML 里抠 <tag>value</tag>；Sunshine 的响应没有 CDATA，够用
std::string XmlTag(const std::string& xml, const char* tag)
{
    const std::string open = std::string("<") + tag;
    size_t pos = 0;

    while ((pos = xml.find(open, pos)) != std::string::npos)
    {
        size_t after = pos + open.size();
        if (after >= xml.size())
        {
            return {};
        }

        // 标签名必须到此为止，避免 <state> 命中 <statexyz>
        const char c = xml[after];
        if (c != '>' && c != '/' && c != ' ' && c != '\t' && c != '\r' && c != '\n')
        {
            pos = after;
            continue;
        }

        size_t gt = xml.find('>', after);
        if (gt == std::string::npos)
        {
            return {};
        }
        if (xml[gt - 1] == '/')
        {
            return {}; // 自闭合标签
        }

        const std::string close = std::string("</") + tag;
        size_t end = xml.find(close, gt + 1);
        if (end == std::string::npos)
        {
            return {};
        }
        return Trim(xml.substr(gt + 1, end - gt - 1));
    }
    return {};
}

// Sunshine 把真正的状态码写在 XML 根节点上（HTTP 层可能仍是 200）：
//   <root status_code="401" status_message="The client is not authorized..."/>
std::string XmlAttribute(const std::string& xml, const char* name)
{
    const std::string needle = std::string(name) + "=\"";
    const size_t pos = xml.find(needle);
    if (pos == std::string::npos)
    {
        return {};
    }
    const size_t begin = pos + needle.size();
    const size_t end = xml.find('"', begin);
    if (end == std::string::npos)
    {
        return {};
    }
    return xml.substr(begin, end - begin);
}

std::string HexLower(const char* data, int len)
{
    static const char* kHex = "0123456789abcdef";
    std::string out;
    out.reserve((size_t)len * 2);
    for (int i = 0; i < len; ++i)
    {
        const unsigned char b = (unsigned char)data[i];
        out.push_back(kHex[b >> 4]);
        out.push_back(kHex[b & 0x0F]);
    }
    return out;
}

bool FileExists(const std::string& path)
{
    if (path.empty())
    {
        return false;
    }
    const DWORD attr = GetFileAttributesA(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::string SslErrorText()
{
    const unsigned long code = ERR_get_error();
    if (code == 0)
    {
        return "socket 错误 " + std::to_string(WSAGetLastError());
    }
    char buf[256] = {};
    ERR_error_string_n(code, buf, sizeof(buf));
    return buf;
}

// 大小写不敏感地在响应头里找某个字段，返回值起点（找不到返回 npos）
size_t FindHeaderValue(const std::string& head, const char* name)
{
    std::string lower;
    lower.reserve(head.size());
    for (char c : head)
    {
        lower.push_back((c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c);
    }

    std::string needle = "\r\n";
    needle += name;
    needle += ":";

    const size_t pos = lower.find(needle);
    if (pos == std::string::npos)
    {
        return std::string::npos;
    }
    return pos + needle.size();
}

// ---------------------------------------------------------------- HTTPS GET

struct HttpResponse
{
    int status = 0;
    std::string body;
    std::string error;
};

// 统一的"真实状态码 + 错误说明"：优先 XML 里的，其次 HTTP 状态行
int ResponseStatus(const HttpResponse& resp, std::string& message)
{
    message = XmlAttribute(resp.body, "status_message");
    const std::string code = XmlAttribute(resp.body, "status_code");
    if (!code.empty())
    {
        return atoi(code.c_str());
    }
    return resp.status;
}

// 建连：IPv4 优先（主机名常常同时解析出 IPv6 链路本地地址，直接连它要白等 20 秒超时），
// 非阻塞 connect + select，单个地址最多等 timeoutMs。
bool ConnectSocket(const std::string& host, int port, int timeoutMs, SOCKET& outSock)
{
    outSock = INVALID_SOCKET;

    addrinfo hints = {};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    char portText[16] = {};
    sprintf_s(portText, "%d", port);

    addrinfo* addrs = nullptr;
    if (getaddrinfo(host.c_str(), portText, &hints, &addrs) != 0 || !addrs)
    {
        return false;
    }

    std::vector<addrinfo*> ordered;
    for (addrinfo* ai = addrs; ai; ai = ai->ai_next)
    {
        if (ai->ai_family == AF_INET) { ordered.push_back(ai); }
    }
    for (addrinfo* ai = addrs; ai; ai = ai->ai_next)
    {
        if (ai->ai_family != AF_INET) { ordered.push_back(ai); }
    }

    for (addrinfo* ai : ordered)
    {
        SOCKET s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (s == INVALID_SOCKET)
        {
            continue;
        }

        u_long nonBlocking = 1;
        ioctlsocket(s, FIONBIO, &nonBlocking);

        bool connected = false;
        if (connect(s, ai->ai_addr, (int)ai->ai_addrlen) == 0)
        {
            connected = true;
        }
        else if (WSAGetLastError() == WSAEWOULDBLOCK)
        {
            fd_set writable;
            FD_ZERO(&writable);
            FD_SET(s, &writable);

            timeval tv = {};
            tv.tv_sec = timeoutMs / 1000;
            tv.tv_usec = (timeoutMs % 1000) * 1000;

            if (select(0, nullptr, &writable, nullptr, &tv) > 0)
            {
                int socketError = 0;
                int len = (int)sizeof(socketError);
                getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&socketError, &len);
                connected = (socketError == 0);
            }
        }

        if (connected)
        {
            nonBlocking = 0;
            ioctlsocket(s, FIONBIO, &nonBlocking);
            outSock = s;
            break;
        }

        closesocket(s);
    }

    freeaddrinfo(addrs);
    return outSock != INVALID_SOCKET;
}

// 带客户端证书的 HTTPS GET。Sunshine 是自签证书，verify 一律关掉；
// 身份靠客户端证书（Qt 版已经配对好了）来认定。
bool HttpsGet(const std::string& host, const std::string& target, int timeoutMs, HttpResponse& out)
{
    out = HttpResponse();

    if (!g_tls)
    {
        out.error = "TLS 未初始化（先调 MS_Init）";
        return false;
    }

    // --- 建连 ---
    SOCKET sock = INVALID_SOCKET;
    const int connectTimeoutMs = timeoutMs < 10000 ? timeoutMs : 10000;
    if (!ConnectSocket(host, kHttpsPort, connectTimeoutMs, sock))
    {
        out.error = "连不上 " + host + ":" + std::to_string(kHttpsPort);
        return false;
    }

    DWORD rcv = (DWORD)timeoutMs;
    DWORD snd = 10000;
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&rcv, sizeof(rcv));
    setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char*)&snd, sizeof(snd));
    g_httpSocket = sock;

    // --- TLS 握手 ---
    SSL* ssl = SSL_new(g_tls);
    if (!ssl)
    {
        out.error = "SSL_new 失败：" + SslErrorText();
        SOCKET expected = sock;
        g_httpSocket.compare_exchange_strong(expected, INVALID_SOCKET);
        closesocket(sock);
        return false;
    }

    SSL_set_fd(ssl, (int)sock);
    SSL_set_tlsext_host_name(ssl, host.c_str());   // SNI：Sunshine 不看，但留着无害
    SSL_set_connect_state(ssl);

    bool ok = (SSL_connect(ssl) == 1);
    if (!ok)
    {
        out.error = "TLS 握手失败：" + SslErrorText();
    }

    // --- 发请求 ---
    if (ok)
    {
        const std::string req =
            "GET " + target + " HTTP/1.1\r\n"
            "Host: " + host + ":" + std::to_string(kHttpsPort) + "\r\n"
            "Accept: */*\r\n"
            "User-Agent: MoonlightWinUI/0.1\r\n"
            "Connection: close\r\n"
            "\r\n";

        int sent = 0;
        const int total = (int)req.size();
        while (sent < total)
        {
            const int n = SSL_write(ssl, req.data() + sent, total - sent);
            if (n <= 0)
            {
                out.error = "发送请求失败：" + SslErrorText();
                ok = false;
                break;
            }
            sent += n;
        }
    }

    // --- 读到 EOF（Connection: close，Sunshine 会给 Content-Length）---
    if (ok)
    {
        std::string raw;
        std::vector<char> buf(16384);

        for (;;)
        {
            const int n = SSL_read(ssl, buf.data(), (int)buf.size());
            if (n > 0)
            {
                raw.append(buf.data(), (size_t)n);
                continue;
            }

            const int err = SSL_get_error(ssl, n);
            if (err == SSL_ERROR_ZERO_RETURN)
            {
                break; // 对端干净关闭
            }
            if (err == SSL_ERROR_SYSCALL && n == 0)
            {
                break; // 没发 close_notify 直接断，数据仍是完整的
            }
            // 超时/被 MS_Stop shutdown：已经收到一部分就尽量解析
            if (!raw.empty() && (err == SSL_ERROR_SYSCALL || err == SSL_ERROR_WANT_READ))
            {
                SLog("响应被截断（%zu 字节），继续尝试解析", raw.size());
                break;
            }

            out.error = "读响应失败：" + SslErrorText();
            ok = false;
            break;
        }

        if (ok)
        {
            // --- 拆 header / body ---
            const size_t hdrEnd = raw.find("\r\n\r\n");
            if (hdrEnd == std::string::npos)
            {
                out.error = "响应不完整（没有 header 结束标记）";
                ok = false;
            }
            else
            {
                const std::string head = raw.substr(0, hdrEnd);
                std::string body = raw.substr(hdrEnd + 4);

                if (head.rfind("HTTP/", 0) == 0)
                {
                    const size_t sp = head.find(' ');
                    if (sp != std::string::npos)
                    {
                        out.status = atoi(head.c_str() + sp + 1);
                    }
                }

                const size_t clPos = FindHeaderValue(head, "content-length");
                if (clPos != std::string::npos)
                {
                    const long long len = _strtoi64(head.c_str() + clPos, nullptr, 10);
                    if (len >= 0 && body.size() > (size_t)len)
                    {
                        body.resize((size_t)len);
                    }
                }

                out.body = std::move(body);
            }
        }
    }

    // --- 收尾：先从全局槽位摘掉，再关（避免 MS_Stop 撞上被复用的句柄）---
    SOCKET expected = sock;
    g_httpSocket.compare_exchange_strong(expected, INVALID_SOCKET);
    SSL_free(ssl);
    closesocket(sock);

    return ok;
}

// ---------------------------------------------------------------- 回调

void ClStageStarting(int stage)
{
    SetStage(Phase::Starting, "%s…", LiGetStageName(stage));
}

void ClStageComplete(int stage)
{
    SLog("阶段完成：%s", LiGetStageName(stage));
}

void ClStageFailed(int stage, int errorCode)
{
    Fail("%s 失败（错误码 %d）", LiGetStageName(stage), errorCode);
    g_stop = true;
}

void ClConnectionStarted(void)
{
    g_connected = true;
    g_packets = 0;
    g_bytes = 0;
    SetStage(Phase::Streaming, "已连接，等待首帧");
    SLog("主机能力位：0x%x（本地光标模式 %s）", LiGetHostFeatureFlags(), (LiGetHostFeatureFlags() & 0x40) ? "支持" : "不支持");
    SLog("触摸支持：%s（能力位 0x%x）", (LiGetHostFeatureFlags() & LI_FF_PEN_TOUCH_EVENTS) ? "是" : "否", LiGetHostFeatureFlags());
}

void ClConnectionTerminated(int errorCode)
{
    g_connected = false;
    if (errorCode == ML_ERROR_GRACEFUL_TERMINATION)
    {
        SetStage(Phase::Stopped, "主机关闭了会话（应用退出）");
    }
    else
    {
        SetStage(Phase::Stopped, "连接中断（错误码 %d）", errorCode);
    }
    g_stop = true; // 让工作线程去收尾
}

// MLC 的日志回调是变参的，先格式化再落盘
// ---- 本地光标：主机发来的形状/可见性（LI_CURSOR_UPDATE） ----
static std::mutex g_cursorMutex;
static bool g_cursorVisible = true;
static uint32_t g_cursorShapeId = 0;
static int g_cursorW = 0;
static int g_cursorH = 0;
static int g_cursorHotX = 0;
static int g_cursorHotY = 0;
static std::vector<uint8_t> g_cursorPixels;
static uint32_t g_cursorUpdates = 0;

// ---- 手柄震动回调（P2）：保存到全局，C# 侧轮询后用 XInput 输出 ----
static std::atomic<uint16_t> g_rumbleLow{ 0 };
static std::atomic<uint16_t> g_rumbleHigh{ 0 };
static std::atomic<uint16_t> g_triggerLeft{ 0 };
static std::atomic<uint16_t> g_triggerRight{ 0 };

static void ClRumble(unsigned short controllerNumber, unsigned short lowFreqMotor, unsigned short highFreqMotor)
{
    (void)controllerNumber;
    g_rumbleLow = lowFreqMotor;
    g_rumbleHigh = highFreqMotor;
}

static void ClRumbleTriggers(uint16_t controllerNumber, uint16_t leftTriggerMotor, uint16_t rightTriggerMotor)
{
    (void)controllerNumber;
    g_triggerLeft = leftTriggerMotor;
    g_triggerRight = rightTriggerMotor;
}

void ClCursorUpdate(const LI_CURSOR_UPDATE* update)
{
    if (!update)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(g_cursorMutex);
    g_cursorUpdates++;
    g_cursorVisible = (update->flags & LI_CURSOR_UPDATE_FLAG_VISIBLE) != 0;

    if ((update->flags & LI_CURSOR_UPDATE_FLAG_SHAPE) && update->pixels && update->pixelDataLength > 0)
    {
        g_cursorShapeId = update->shapeId;
        g_cursorW = update->width;
        g_cursorH = update->height;
        g_cursorHotX = update->hotspotX;
        g_cursorHotY = update->hotspotY;
        g_cursorPixels.assign(update->pixels, update->pixels + update->pixelDataLength);
        SLog("光标形状：%ux%u 热点(%d,%d) visible=%d id=%u 第 %u 次更新",
             (unsigned)update->width, (unsigned)update->height,
             update->hotspotX, update->hotspotY,
             g_cursorVisible ? 1 : 0, update->shapeId, g_cursorUpdates);
    }
}

// ---------------- 剪贴板同步（Sunshine 扩展 0x5508） ----------------
// 负载格式（v1，小端）：u8 version=1 | u8 kind | u32 token | u32 length | payload
//   kind: 1 = UTF-8 文本，2 = PNG，3 = 大对象引用（走 HTTPS 另传）
// 回调在控制流接收线程上跑，所以只入队，解析和写系统剪贴板交给 UI 线程轮询。
std::mutex g_clipMutex;
std::deque<std::vector<uint8_t>> g_clipQueue;
unsigned g_clipDropped = 0;

void ClClipboardData(const char* data, int length)
{
    if (!data || length <= 0)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(g_clipMutex);
    g_clipQueue.emplace_back((const uint8_t*)data, (const uint8_t*)data + length);
    while (g_clipQueue.size() > 8)
    {
        g_clipQueue.pop_front();
        g_clipDropped++;
    }

    SLog("剪贴板：主机推来 %d 字节（队列 %zu，丢弃 %u）", length, g_clipQueue.size(), g_clipDropped);
}

// C# 轮询：有数据时返回负载长度；buffer 为空或不够大时只返回需要的长度（不弹出）
extern "C" __declspec(dllexport) int MS_PollClipboard(unsigned char* buffer, int capacity, int* kind, int* outToken)
{
    std::lock_guard<std::mutex> lock(g_clipMutex);
    if (g_clipQueue.empty())
    {
        return 0;
    }

    const std::vector<uint8_t>& packet = g_clipQueue.front();
    int k = 0;
    int token = 0;
    size_t off = 0;

    if (packet.size() >= 10 && packet[0] == 1)
    {
        k = (int)packet[1];
        uint32_t tok = 0;
        memcpy(&tok, packet.data() + 2, 4);
        token = (int)tok;
        off = 10;
    }

    const int payloadLength = (int)(packet.size() - off);
    if (kind) { *kind = k; }
    if (outToken) { *outToken = token; }

    if (!buffer || capacity < payloadLength)
    {
        return payloadLength;
    }

    if (payloadLength > 0)
    {
        memcpy(buffer, packet.data() + off, (size_t)payloadLength);
    }

    g_clipQueue.pop_front();
    return payloadLength;
}

// C# 发文本到主机：这里补 v1 头（UTF-8 文本）
extern "C" __declspec(dllexport) int MS_SendClipboardText(const char* utf8, int length)
{
    if (!utf8 || length <= 0)
    {
        return -1;
    }

    if (length > 65000)
    {
        SLog("剪贴板：文本太大（%d 字节），跳过", length);
        return -2;
    }

    std::vector<uint8_t> payload((size_t)length + 10);
    payload[0] = 1;                          // version
    payload[1] = LI_CLIPBOARD_KIND_TEXT;
    const uint32_t token = (uint32_t)GetTickCount();
    const uint32_t len = (uint32_t)length;
    memcpy(payload.data() + 2, &token, 4);
    memcpy(payload.data() + 6, &len, 4);
    memcpy(payload.data() + 10, utf8, (size_t)length);

    const int rc = LiSendClipboardData(payload.data(), (int)payload.size());
    SLog("剪贴板：发送 %d 字节文本 → rc=%d", length, rc);
    return rc;
}

// 发 PNG 图片（kind=2）。控制帧上限 65511，头占 10 字节，所以 PNG 最多 65001。
extern "C" __declspec(dllexport) int MS_SendClipboardPng(const unsigned char* png, int length)
{
    if (!png || length <= 0)
    {
        return -1;
    }

    if (length > 65000)
    {
        SLog("剪贴板：PNG 太大（%d 字节），超过单帧上限，跳过", length);
        return -2;
    }

    std::vector<uint8_t> payload((size_t)length + 10);
    payload[0] = 1;                          // version
    payload[1] = LI_CLIPBOARD_KIND_PNG;
    const uint32_t token = (uint32_t)GetTickCount();
    const uint32_t len = (uint32_t)length;
    memcpy(payload.data() + 2, &token, 4);
    memcpy(payload.data() + 6, &len, 4);
    memcpy(payload.data() + 10, png, (size_t)length);

    const int rc = LiSendClipboardData(payload.data(), (int)payload.size());
    SLog("剪贴板：发送 %d 字节 PNG → rc=%d", length, rc);
    return rc;
}

// 发大对象引用（kind=3）：正文是 JSON {"id":..,"mime":..,"size":..}，真正的数据走 HTTPS blob。
extern "C" __declspec(dllexport) int MS_SendClipboardRef(const char* json, int length)
{
    if (!json || length <= 0)
    {
        return -1;
    }

    if (length > 65000)
    {
        SLog("剪贴板：引用 JSON 太大（%d 字节），跳过", length);
        return -2;
    }

    std::vector<uint8_t> payload((size_t)length + 10);
    payload[0] = 1;                          // version
    payload[1] = LI_CLIPBOARD_KIND_REF;
    const uint32_t token = (uint32_t)GetTickCount();
    const uint32_t len = (uint32_t)length;
    memcpy(payload.data() + 2, &token, 4);
    memcpy(payload.data() + 6, &len, 4);
    memcpy(payload.data() + 10, json, (size_t)length);

    const int rc = LiSendClipboardData(payload.data(), (int)payload.size());
    SLog("剪贴板：发送引用 %d 字节 → rc=%d", length, rc);
    return rc;
}

void ClLogMessage(const char* format, ...)
{
    char text[2048] = {};
    va_list ap;
    va_start(ap, format);
    vsnprintf(text, sizeof(text), format ? format : "", ap);
    va_end(ap);

    SLog("mlc: %s", text);
}

int DrSetup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags)
{
    SLog("视频协商：format=0x%X %dx%d @%d（context=%p flags=%d）",
         videoFormat, width, height, redrawRate, context, drFlags);

    if (videoFormat != VIDEO_FORMAT_H264 &&
        videoFormat != VIDEO_FORMAT_H265 &&
        videoFormat != VIDEO_FORMAT_H265_MAIN10 &&
        videoFormat != VIDEO_FORMAT_AV1_MAIN8)
    {
        SLog("mlrender 只支持 H.264 / HEVC / AV1(Main8)，主机选了 0x%X", videoFormat);
        return -1;
    }

    // 让 mlrender 换到对应解码器（H264 ↔ HEVC）
    const int rc = ML_SetVideoFormat(videoFormat);
    if (rc == -3)
    {
        // 控制台探针没建 D3D 设备：不切换，但仍然让连接继续（只为看协商结果）
        SLog("没有 D3D 设备（探针模式）：跳过解码器切换");
        return 0;
    }
    if (rc != 0)
    {
        SLog("ML_SetVideoFormat(0x%X) 失败：%d", videoFormat, rc);
        return -1;
    }

    SLog("解码器已切换：%s",
         videoFormat == VIDEO_FORMAT_H264 ? "H.264" :
         videoFormat == VIDEO_FORMAT_AV1_MAIN8 ? "AV1" : "HEVC");
    return 0;
}

void DrStart(void)   { SLog("视频流开始"); }
void DrStop(void)    { SLog("视频流停止"); }
void DrCleanup(void) { SLog("视频解码清理"); }

// 关键回调：du->bufferList 是 LENTRY 链（IDR 帧会先给 SPS/PPS），
// 拼成一整个 Annex-B 访问单元再交给 mlrender 的解码器
int DrSubmitDecodeUnit(PDECODE_UNIT du)
{
    if (!du || !du->bufferList)
    {
        return DR_OK;
    }

    thread_local std::vector<uint8_t> au; // 60fps 下别每帧分配
    au.clear();
    if (du->fullLength > 0)
    {
        au.reserve((size_t)du->fullLength);
    }

    for (PLENTRY e = du->bufferList; e; e = e->next)
    {
        if (e->data && e->length > 0)
        {
            const uint8_t* p = (const uint8_t*)e->data;
            au.insert(au.end(), p, p + e->length);
        }
    }

    if (au.empty())
    {
        return DR_OK;
    }

    ML_PushPacket(au.data(), (int)au.size());
    g_bytes += (long long)au.size();

    // 主机在 DECODE_UNIT 里带出来的编码耗时（1/10 ms），有些帧是 0
    if (du->frameHostProcessingLatency)
    {
        g_hostLatencyTenthMs = du->frameHostProcessingLatency;
    }

    // 第一个解码单元到了才把"等待首帧"换掉（每个包都改状态太浪费）
    if (g_packets.fetch_add(1) == 0)
    {
        SetStage(Phase::Streaming, "串流中：首帧已到达");
    }
    return DR_OK;
}

// 音频：Opus 多流解码 + WASAPI 共享模式播放（mlaudio.cpp）
int ArInit(int audioConfiguration, const POPUS_MULTISTREAM_CONFIGURATION opusConfig, void* context, int arFlags)
{
    SLog("音频初始化：config=0x%X 采样率=%d 声道=%d（context=%p flags=%d）",
         audioConfiguration,
         opusConfig ? opusConfig->sampleRate : 0,
         opusConfig ? opusConfig->channelCount : 0,
         context, arFlags);

    return MA_Init(audioConfiguration, opusConfig);
}

void ArStart(void)   { MA_Start(); }
void ArStop(void)    { MA_Stop(); }
void ArCleanup(void) { MA_Shutdown(); }

void ArDecodeAndPlaySample(char* sampleData, int sampleLength)
{
    MA_Push(sampleData, sampleLength);
}

void InitCallbacks()
{
    if (g_callbacksReady)
    {
        return;
    }

    LiInitializeConnectionCallbacks(&g_connCb);
    g_connCb.stageStarting = ClStageStarting;
    g_connCb.stageComplete = ClStageComplete;
    g_connCb.stageFailed = ClStageFailed;
    g_connCb.connectionStarted = ClConnectionStarted;
    g_connCb.connectionTerminated = ClConnectionTerminated;
    g_connCb.logMessage = ClLogMessage;
    g_connCb.cursorUpdate = ClCursorUpdate;
    g_connCb.clipboardData = ClClipboardData;
    g_connCb.rumble = ClRumble;
    g_connCb.rumbleTriggers = ClRumbleTriggers;

    LiInitializeVideoCallbacks(&g_videoCb);
    g_videoCb.setup = DrSetup;
    g_videoCb.start = DrStart;
    g_videoCb.stop = DrStop;
    g_videoCb.cleanup = DrCleanup;
    g_videoCb.submitDecodeUnit = DrSubmitDecodeUnit;
    g_videoCb.capabilities = 0; // 不开 DIRECT_SUBMIT：让 MLC 自己排队做帧节奏

    LiInitializeAudioCallbacks(&g_audioCb);
    g_audioCb.init = ArInit;
    g_audioCb.start = ArStart;
    g_audioCb.stop = ArStop;
    g_audioCb.cleanup = ArCleanup;
    g_audioCb.decodeAndPlaySample = ArDecodeAndPlaySample;
    // 我们的解码器能处理任意时长（5/10/20ms 的 Opus 帧都行），让 MLC 挑更好的预设
    g_audioCb.capabilities = CAPABILITY_SUPPORTS_ARBITRARY_AUDIO_DURATION;

    g_callbacksReady = true;
}

// 从 /applist 里挑应用 id：优先标题含 Desktop 的，其次第一个 <ID>。
// Sunshine 的 Desktop 不保证是 appid=1，所以 404 时要来这里查一次。
int ResolveAppId(const std::string& host)
{
    HttpResponse resp;
    if (!HttpsGet(host, "/applist?uniqueid=" + g_uniqueId + "&uuid=" + g_uniqueId,
                  kServerInfoTimeoutMs, resp))
    {
        SLog("applist 请求失败：%s", resp.error.c_str());
        return 0;
    }

    std::string message;
    const int status = ResponseStatus(resp, message);
    if (status != 200)
    {
        SLog("applist 返回 %d：%s", status, message.c_str());
        return 0;
    }

    SLog("applist 正文：%.800s", resp.body.c_str());

    int firstId = 0;
    size_t pos = 0;

    while ((pos = resp.body.find("<App", pos)) != std::string::npos)
    {
        const size_t end = resp.body.find("</App>", pos);
        if (end == std::string::npos)
        {
            break;
        }

        const std::string chunk = resp.body.substr(pos, end - pos);
        const std::string title = XmlTag(chunk, "AppTitle");
        const std::string idText = XmlTag(chunk, "ID");
        const int id = idText.empty() ? 0 : atoi(idText.c_str());

        if (id > 0)
        {
            if (firstId == 0)
            {
                firstId = id;
            }

            std::string lower;
            for (char c : title)
            {
                lower.push_back((char)tolower((unsigned char)c));
            }
            if (lower.find("desktop") != std::string::npos)
            {
                SLog("applist：Desktop → appid=%d", id);
                return id;
            }
        }

        pos = end + 6;
    }

    SLog("applist：没有标题为 Desktop 的应用，退回第一个 appid=%d", firstId);
    return firstId;
}

// ---------------------------------------------------------------- 工作线程

void SessionThread(std::string host, int width, int height, int fps, int bitrateKbps, int appId)
{
    // 线程退出时统一复位标志（含所有提前 return 的分支）
    struct BusyGuard
    {
        ~BusyGuard()
        {
            g_connected = false;
            g_busy = false;
        }
    } guard;

    // 1) 流配置：rikey/rikeyid 必须在 /launch 之前填好，MLC 之后用同一组 key 加密控制流
    STREAM_CONFIGURATION sc;
    LiInitializeStreamConfiguration(&sc);

    sc.width = width;
    sc.height = height;
    sc.fps = fps;
    sc.bitrate = bitrateKbps;
    sc.packetSize = 1024;
    sc.streamingRemotely = STREAM_CFG_AUTO;
    sc.audioConfiguration = AUDIO_CONFIGURATION_STEREO;
    sc.supportedVideoFormats = VIDEO_FORMAT_H264; // 先只开 H264，HEVC 以后再说
    sc.clientRefreshRateX100 = fps * 100;
    sc.colorSpace = COLORSPACE_REC_709;
    sc.colorRange = COLOR_RANGE_LIMITED;
    sc.encryptionFlags = ENCFLG_ALL;

    if (RAND_bytes((unsigned char*)sc.remoteInputAesKey, sizeof(sc.remoteInputAesKey)) != 1 ||
        RAND_bytes((unsigned char*)sc.remoteInputAesIv, sizeof(sc.remoteInputAesIv)) != 1)
    {
        Fail("生成 rikey 随机数失败：%s", SslErrorText().c_str());
        return;
    }

    // Qt 的写法：IV 前 4 字节按大端解释成 rikeyid，再按有符号 int 打印
    const uint32_t riKeyIdBits =
        ((uint32_t)(unsigned char)sc.remoteInputAesIv[0] << 24) |
        ((uint32_t)(unsigned char)sc.remoteInputAesIv[1] << 16) |
        ((uint32_t)(unsigned char)sc.remoteInputAesIv[2] << 8) |
        ((uint32_t)(unsigned char)sc.remoteInputAesIv[3]);
    const int riKeyId = (int)(int32_t)riKeyIdBits;

    // 2) /serverinfo
    SetStage(Phase::Starting, "serverinfo：查询 %s…", host.c_str());

    const std::string infoQuery =
        "/serverinfo?uniqueid=" + g_uniqueId + "&uuid=" + g_uniqueId + "&clientname=MoonlightWinUI";

    HttpResponse resp;
    if (!HttpsGet(host, infoQuery, kServerInfoTimeoutMs, resp))
    {
        if (g_stop) { return; }
        Fail("%s", resp.error.c_str());
        return;
    }

    SLog("serverinfo HTTP %d，正文 %zu 字节：%.600s",
         resp.status, resp.body.size(), resp.body.c_str());

    std::string infoMessage;
    const int infoStatus = ResponseStatus(resp, infoMessage);
    if (infoStatus != 200)
    {
        const std::string detail = infoMessage.empty() ? resp.body.substr(0, 200) : infoMessage;
        if (infoStatus == 401 || infoStatus == 403)
        {
            Fail("主机不认可这个客户端证书（%d：%s）——需要用这个身份重新配对", infoStatus, detail.c_str());
        }
        else
        {
            Fail("serverinfo 返回 %d：%s", infoStatus, detail.c_str());
        }
        return;
    }

    const std::string pairStatus = XmlTag(resp.body, "PairStatus");
    const std::string appVersion = XmlTag(resp.body, "appversion");
    const std::string gfeVersion = XmlTag(resp.body, "GfeVersion");
    const std::string codecText = XmlTag(resp.body, "ServerCodecModeSupport");
    const std::string state = XmlTag(resp.body, "state");
    const std::string currentGameText = XmlTag(resp.body, "currentgame");
    const int currentGame = currentGameText.empty() ? 0 : atoi(currentGameText.c_str());

    if (pairStatus == "0")
    {
        Fail("主机报告未配对（PairStatus=0），先用同一个客户端身份完成配对");
        return;
    }

    int codecModeSupport = codecText.empty() ? 0 : (int)strtol(codecText.c_str(), nullptr, 0);
    if (codecModeSupport == 0)
    {
        codecModeSupport = SCM_H264; // 老主机不给这个字段时按 H264 处理
    }

    SLog("主机：state=%s appversion=%s gfe=%s codecModeSupport=0x%X pairStatus=%s currentgame=%d",
         state.c_str(), appVersion.c_str(), gfeVersion.c_str(), codecModeSupport, pairStatus.c_str(),
         currentGame);

    if ((codecModeSupport & SCM_MASK_H264) == 0 &&
        (codecModeSupport & SCM_MASK_HEVC) == 0 &&
        (codecModeSupport & SCM_MASK_AV1) == 0)
    {
        Fail("主机不支持任何已知编码（codecModeSupport=0x%X）", codecModeSupport);
        return;
    }

    // 客户端能解什么：H.264 / HEVC(Main8) / AV1(Main8)。Main10 不报——只做 NV12 上屏。
    int offered = 0;
    const char* wanted = "自动";
    switch (g_videoCodec)
    {
    case 1:
        wanted = "H.264";
        offered = (codecModeSupport & SCM_MASK_H264) ? VIDEO_FORMAT_H264 : 0;
        break;
    case 2:
        wanted = "HEVC";
        offered = (codecModeSupport & SCM_MASK_HEVC) ? VIDEO_FORMAT_H265 : 0;
        break;
    case 3:
        wanted = "AV1";
        offered = (codecModeSupport & SCM_MASK_AV1) ? VIDEO_FORMAT_AV1_MAIN8 : 0;
        break;
    default:
        offered = VIDEO_FORMAT_H264;
        if (codecModeSupport & SCM_MASK_HEVC) { offered |= VIDEO_FORMAT_H265; }
        // AV1 的 OBU 解析还没对（dav1d 报 Error parsing OBU data），自动模式先不报，
        // 想试 AV1 需要在设置里手动选。
        break;
    }

    if (offered == 0)
    {
        Fail("主机不支持所选编码（%s，codecModeSupport=0x%X）", wanted, codecModeSupport);
        return;
    }

    sc.supportedVideoFormats = offered;
    SLog("客户端上报编码：0x%X（偏好：%s）", offered, wanted);

    if (g_stop) { return; }

    // 3) /launch（主机上已经有应用在跑时用 /resume 接管）
    const std::string riKeyHex = HexLower(sc.remoteInputAesKey, (int)sizeof(sc.remoteInputAesKey));

    auto requestLaunch = [&](const char* verb, int launchAppId, HttpResponse& result) -> bool
    {
        char query[2048] = {};
        sprintf_s(query,
                  "/%s?appid=%d&mode=%dx%dx%d&additionalStates=1&sops=0&clientname=MoonlightWinUI"
                  "&rikey=%s&rikeyid=%d&localAudioPlayMode=0"
                  "&surroundAudioInfo=%d&remoteControllersBitmap=0&gcmap=0&gcpersist=0%s",
                  verb, launchAppId, width, height, fps,
                  riKeyHex.c_str(), riKeyId,
                  SURROUNDAUDIOINFO_FROM_AUDIO_CONFIGURATION(sc.audioConfiguration),
                  LiGetLaunchUrlQueryParameters());

        SLog("%s 请求：%.240s…", verb, query);
        return HttpsGet(host, query, kLaunchTimeoutMs, result);
    };

    const char* verb = (currentGame != 0) ? "resume" : "launch";
    int appIdUsed = appId;

    SetStage(Phase::Starting, "%s：请主机启动应用 %d…", verb, appId);

    if (!requestLaunch(verb, appId, resp))
    {
        if (g_stop) { return; }
        Fail("%s", resp.error.c_str());
        return;
    }

    SLog("%s HTTP %d，正文 %zu 字节：%.600s", verb, resp.status, resp.body.size(), resp.body.c_str());

    std::string launchMessage;
    int launchStatus = ResponseStatus(resp, launchMessage);

    // appid 猜错了（Sunshine 的 Desktop 不一定是 1）：查一次 /applist 再试
    if (launchStatus == 404)
    {
        SLog("%s 404（%s），改查 /applist", verb, launchMessage.c_str());
        SetStage(Phase::Starting, "%s：查应用列表…", verb);

        const int resolved = ResolveAppId(host);
        if (resolved > 0 && resolved != appId && !g_stop)
        {
            appIdUsed = resolved;
            SetStage(Phase::Starting, "%s：改用 appid=%d…", verb, resolved);
            if (!requestLaunch(verb, resolved, resp))
            {
                if (g_stop) { return; }
                Fail("%s", resp.error.c_str());
                return;
            }
            launchStatus = ResponseStatus(resp, launchMessage);
            SLog("%s（appid=%d）HTTP %d，正文 %.600s", verb, resolved, resp.status, resp.body.c_str());
        }
    }

    // 上一次会话没 /cancel，主机说"已有应用在跑"：改用 /resume 接管
    if (launchStatus == 400 && strcmp(verb, "launch") == 0 && !g_stop)
    {
        SLog("launch 400（%s），改用 /resume", launchMessage.c_str());
        SetStage(Phase::Starting, "resume：接管已运行的应用 %d…", appIdUsed);

        if (requestLaunch("resume", appIdUsed, resp))
        {
            launchStatus = ResponseStatus(resp, launchMessage);
            SLog("resume HTTP %d，正文 %.600s", resp.status, resp.body.c_str());
        }
    }
    // 反过来：resume 但主机上没应用了，退回 /launch
    else if (launchStatus == 400 && strcmp(verb, "resume") == 0 && !g_stop)
    {
        SLog("resume 400（%s），改回 /launch", launchMessage.c_str());

        if (requestLaunch("launch", appIdUsed, resp))
        {
            launchStatus = ResponseStatus(resp, launchMessage);
            SLog("launch HTTP %d，正文 %.600s", resp.status, resp.body.c_str());
        }
    }

    if (launchStatus != 200)
    {
        const std::string detail = launchMessage.empty() ? resp.body.substr(0, 200) : launchMessage;
        Fail("启动失败（%d）：%s", launchStatus, detail.c_str());
        return;
    }

    const std::string rtspUrl = XmlTag(resp.body, "sessionUrl0");
    if (rtspUrl.empty())
    {
        Fail("launch 响应里没有 sessionUrl0：%.200s", resp.body.c_str());
        return;
    }

    SLog("会话地址：%s", rtspUrl.c_str());

    if (g_stop) { return; }

    // 4) 交给 moonlight-common-c：RTSP 握手 + 控制/视频/输入流
    SERVER_INFORMATION si;
    LiInitializeServerInformation(&si);

    si.address = host.c_str();
    si.serverInfoAppVersion = appVersion.c_str();
    si.serverInfoGfeVersion = gfeVersion.empty() ? nullptr : gfeVersion.c_str();
    si.rtspSessionUrl = rtspUrl.c_str();
    si.serverCodecModeSupport = codecModeSupport;

    SetStage(Phase::Starting, "connecting：RTSP 握手…");

    const int err = LiStartConnection(&si, &sc, &g_connCb, &g_videoCb, &g_audioCb,
                                      nullptr, 0, nullptr, 0);
    if (err != 0)
    {
        // LiStartConnection 的返回值就是失败的阶段号
        Fail("LiStartConnection 失败：%s（%d）", LiGetStageName(err), err);
        return;
    }

    g_connected = true;
    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        if (g_phase != Phase::Streaming)
        {
            g_phase = Phase::Streaming;
            g_stage = "已连接，等待视频包";
        }
    }

    // 等停止指令（主机主动断开时由回调置位）
    while (!g_stop)
    {
        Sleep(20);
    }

    SLog("开始收尾：LiStopConnection()");
    LiStopConnection();
    g_connected = false;

    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        if (g_phase == Phase::Streaming)
        {
            g_phase = Phase::Stopped;
            g_stage = "已停止";
        }
    }
}

} // namespace

// ---------------------------------------------------------------- 导出

extern "C" {

__declspec(dllexport) int MS_Init(const char* certPemPath, const char* keyPemPath, const char* uniqueId)
{
    if (g_busy)
    {
        return -1; // 先 MS_Stop
    }

    g_certPath = certPemPath ? certPemPath : "";
    g_keyPath = keyPemPath ? keyPemPath : "";
    g_uniqueId = uniqueId ? uniqueId : "";

    if (!FileExists(g_certPath))
    {
        SLog("客户端证书读不到：%s", g_certPath.c_str());
        return -2;
    }
    if (!FileExists(g_keyPath))
    {
        SLog("客户端私钥读不到：%s", g_keyPath.c_str());
        return -3;
    }

    if (!g_wsaStarted)
    {
        WSADATA wsa = {};
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
        {
            SLog("WSAStartup 失败");
            return -10;
        }
        g_wsaStarted = true;
    }

    InitCallbacks();

    if (g_tls)
    {
        SSL_CTX_free(g_tls);
        g_tls = nullptr;
    }
    ERR_clear_error();

    g_tls = SSL_CTX_new(TLS_client_method());
    if (!g_tls)
    {
        SLog("SSL_CTX_new 失败：%s", SslErrorText().c_str());
        return -11;
    }

    // Sunshine 用的是自签证书，链和主机名都不校验；身份由客户端证书证明
    SSL_CTX_set_verify(g_tls, SSL_VERIFY_NONE, nullptr);
    SSL_CTX_set_security_level(g_tls, 0); // 兼容老自签证书的弱参数

    if (SSL_CTX_use_certificate_chain_file(g_tls, g_certPath.c_str()) != 1)
    {
        SLog("加载客户端证书失败（%s）：%s", g_certPath.c_str(), SslErrorText().c_str());
        return -2;
    }
    if (SSL_CTX_use_PrivateKey_file(g_tls, g_keyPath.c_str(), SSL_FILETYPE_PEM) != 1)
    {
        SLog("加载客户端私钥失败（%s）：%s", g_keyPath.c_str(), SslErrorText().c_str());
        return -3;
    }
    if (SSL_CTX_check_private_key(g_tls) != 1)
    {
        SLog("证书和私钥不匹配：%s", SslErrorText().c_str());
        return -4;
    }

    SLog("初始化完成：uniqueid=%s cert=%s", g_uniqueId.c_str(), g_certPath.c_str());
    SetStage(Phase::Idle, "就绪");
    return 0;
}

__declspec(dllexport) int MS_Status(char* stage, int stageLen)
{
    std::lock_guard<std::mutex> lock(g_stateLock);

    if (stage && stageLen > 0)
    {
        strncpy_s(stage, (size_t)stageLen, g_stage.c_str(), _TRUNCATE);
    }

    switch (g_phase)
    {
    case Phase::Starting:  return 1;
    case Phase::Streaming: return 2;
    case Phase::Failed:    return -1;
    default:               return 0;
    }
}

// 设置编码偏好（要在 MS_Start 之前调用）：0=自动，1=H264，2=HEVC，3=AV1
__declspec(dllexport) void MS_SetVideoCodec(int codec)
{
    if (codec < 0 || codec > 3)
    {
        codec = 0;
    }

    g_videoCodec = codec;
    SLog("编码偏好：%d（0=自动 1=H264 2=HEVC 3=AV1）", codec);
}

__declspec(dllexport) int MS_Start(const char* host, int width, int height, int fps, int bitrateKbps, int appId)
{
    if (g_busy)
    {
        return -1;
    }
    if (!g_tls)
    {
        return -2;
    }
    if (!host || !*host)
    {
        return -3;
    }
    if (width <= 0 || height <= 0 || fps <= 0 || bitrateKbps <= 0)
    {
        return -4;
    }

    // 上一轮可能刚好结束，把线程收尸
    if (g_worker.joinable())
    {
        g_worker.join();
    }

    g_stop = false;
    g_connected = false;
    g_packets = 0;
    g_bytes = 0;
    g_hostLatencyTenthMs = 0;
    g_perfLastTime = 0;
    g_perfLastBytes = 0;
    g_perfLastFrames = 0;
    g_perfFps = 0;
    g_perfKbps = 0;
    g_busy = true;

    g_worker = std::thread(SessionThread, std::string(host), width, height, fps, bitrateKbps,
                           appId > 0 ? appId : 1);
    return 0;
}

__declspec(dllexport) void MS_Stop(void)
{
    g_stop = true;

    // 打断可能还卡在 HTTPS 上的工作线程
    SOCKET s = g_httpSocket.exchange(INVALID_SOCKET);
    if (s != INVALID_SOCKET)
    {
        shutdown(s, SD_BOTH);
    }

    // 只有"还没连上"时才用 LiInterruptConnection 打断 LiStartConnection；
    // 已经连上的走正常 LiStopConnection（由工作线程自己调）
    if (g_busy && !g_connected)
    {
        LiInterruptConnection();
    }

    if (g_worker.joinable())
    {
        g_worker.join();
    }

    g_connected = false;
    g_busy = false;

    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        if (g_phase != Phase::Failed)
        {
            g_phase = Phase::Idle;
            g_stage = "空闲";
        }
    }
    SLog("已停止");
}

__declspec(dllexport) int MS_SendMouseMove(int dx, int dy)
{
    if (!g_connected)
    {
        return -1;
    }
    if (dx < -32768) { dx = -32768; }
    if (dx > 32767) { dx = 32767; }
    if (dy < -32768) { dy = -32768; }
    if (dy > 32767) { dy = 32767; }
    return LiSendMouseMoveEvent((short)dx, (short)dy);
}

// 绝对鼠标：把光标直接放到「流画面坐标系」的 (x, y)。
// 好处是没有相对增量累积误差、也不受主机端指针加速影响（远程主机最容易踩这个）。
// 本地光标模式：0 = 主机把光标画进视频（默认），1 = 主机不发光标像素，改发光标更新由客户端自己画
extern "C" __declspec(dllexport) int MS_SetCursorMode(int mode)
{
    const int rc = LiSetCursorMode(mode);
    SLog("光标模式：%s → rc=%d（host features=0x%x）",
         mode == 1 ? "本地渲染" : "视频内合成", rc, LiGetHostFeatureFlags());
    return rc;
}

// 当前光标状态：visible / shapeId / 宽高 / 热点
extern "C" __declspec(dllexport) int MS_CursorInfo(int* visible, int* shapeId, int* width, int* height, int* hotX, int* hotY)
{
    std::lock_guard<std::mutex> lock(g_cursorMutex);
    if (visible) { *visible = g_cursorVisible ? 1 : 0; }
    if (shapeId) { *shapeId = (int)g_cursorShapeId; }
    if (width) { *width = g_cursorW; }
    if (height) { *height = g_cursorH; }
    if (hotX) { *hotX = g_cursorHotX; }
    if (hotY) { *hotY = g_cursorHotY; }
    return 0;
}

// 收到过多少次光标更新（诊断用）
extern "C" __declspec(dllexport) unsigned MS_CursorUpdates(void)
{
    std::lock_guard<std::mutex> lock(g_cursorMutex);
    return g_cursorUpdates;
}

// 拷贝当前光标形状的 BGRA 像素（buffer 为 nullptr 时只返回需要的字节数）
extern "C" __declspec(dllexport) int MS_CursorShape(unsigned char* buffer, int capacity, int* shapeId)
{
    std::lock_guard<std::mutex> lock(g_cursorMutex);
    if (shapeId) { *shapeId = (int)g_cursorShapeId; }

    const int needed = (int)g_cursorPixels.size();
    if (!buffer)
    {
        return needed;
    }

    if (capacity < needed)
    {
        return -needed;
    }

    if (needed > 0)
    {
        memcpy(buffer, g_cursorPixels.data(), (size_t)needed);
    }

    return needed;
}
// 主机能力位（LI_FF_CURSOR_SHAPE = 0x40 表示支持本地光标模式）
extern "C" __declspec(dllexport) unsigned MS_HostFeatures(void)
{
    return LiGetHostFeatureFlags();
}
__declspec(dllexport) int MS_SendMousePosition(int x, int y, int width, int height)
{
    if (!g_connected)
    {
        return -1;
    }
    if (width <= 0 || height <= 0)
    {
        return -2;
    }
    if (x < 0) { x = 0; }
    if (y < 0) { y = 0; }
    if (x >= width) { x = width - 1; }
    if (y >= height) { y = height - 1; }
    if (width > 32767) { width = 32767; }
    if (height > 32767) { height = 32767; }

    return LiSendMousePositionEvent((short)x, (short)y, (short)width, (short)height);
}

// vk：Win32 Virtual Key（0x41='A'，0x0D=Enter…）；down：1 按下 / 0 抬起；
// modifiers：MODIFIER_* 位（SHIFT=1 CTRL=2 ALT=4 META=8 EXTENDED=0x10），一般传 0，
// MLC 会按 VK 自己修正左右修饰键。
__declspec(dllexport) int MS_SendKey(int vk, int down, int modifiers)
{
    if (!g_connected)
    {
        return -1;
    }
    if (vk <= 0 || vk > 0xFF)
    {
        return -2;
    }

    // Moonlight/Sunshine 的约定：keyCode 高位置 1 表示"这是 VK 码，不要按扫描码处理"
    const short code = (short)(0x8000 | (vk & 0xFF));
    return LiSendKeyboardEvent(code,
                               down ? KEY_ACTION_DOWN : KEY_ACTION_UP,
                               (char)modifiers);
}

// button：1 左 2 中 3 右 4 X1 5 X2（对应 Limelight 的 BUTTON_*）
__declspec(dllexport) int MS_SendMouseButton(int button, int down)
{
    if (!g_connected)
    {
        return -1;
    }
    if (button < 1 || button > 5)
    {
        return -2;
    }
    return LiSendMouseButtonEvent(down ? BUTTON_ACTION_PRESS : BUTTON_ACTION_RELEASE, button);
}

// clicks：正数向上滚，一格 = 1（MLC 内部乘 120）
__declspec(dllexport) int MS_SendMouseScroll(int clicks)
{
    if (!g_connected)
    {
        return -1;
    }
    if (clicks < -127) { clicks = -127; }
    if (clicks > 127) { clicks = 127; }
    return LiSendScrollEvent((signed char)clicks);
}

__declspec(dllexport) int MS_SendMouseHScroll(int clicks)
{
    if (!g_connected)
    {
        return -1;
    }
    if (clicks < -127) { clicks = -127; }
    if (clicks > 127) { clicks = 127; }
    return LiSendHScrollEvent((signed char)clicks);
}

__declspec(dllexport) void MS_Stats(long long* packets, long long* bytes, int* connected)
{
    if (packets) { *packets = g_packets.load(); }
    if (bytes) { *bytes = g_bytes.load(); }
    if (connected) { *connected = g_connected ? 1 : 0; }
}

// 性能浮窗：一次取回 6 个值（UI 线程每 0.5s 调一次）
// out = { fps, 编码ms(主机), 解码ms, 网络ms(RTT/2), 丢包%, 码率kbps }
__declspec(dllexport) void MS_Perf(float* out)
{
    if (!out)
    {
        return;
    }

    for (int i = 0; i < 6; i++)
    {
        out[i] = 0;
    }

    int decodedFrames = 0;
    double lastDecodeMs = 0;
    ML_Stats(&decodedFrames, &lastDecodeMs);

    const double now = PerfNowSeconds();
    const uint64_t bytes = (uint64_t)LiGetRTPVideoBytesReceived();

    if (g_perfLastTime <= 0)
    {
        g_perfLastTime = now;
        g_perfLastBytes = (long long)bytes;
        g_perfLastFrames = decodedFrames;
    }
    else
    {
        const double dt = now - g_perfLastTime;
        if (dt >= 0.25)
        {
            const int frames = decodedFrames - g_perfLastFrames;
            const long long deltaBytes = (long long)bytes - g_perfLastBytes;
            g_perfFps = (float)(frames / dt);
            g_perfKbps = (float)((double)deltaBytes * 8.0 / 1000.0 / dt);
            g_perfLastTime = now;
            g_perfLastBytes = (long long)bytes;
            g_perfLastFrames = decodedFrames;
        }
    }

    uint32_t rtt = 0;
    uint32_t rttVariance = 0;
    LiGetEstimatedRttInfo(&rtt, &rttVariance);

    out[0] = g_perfFps;
    out[1] = g_hostLatencyTenthMs.load() / 10.0f;
    out[2] = (float)lastDecodeMs;
    out[3] = rtt / 2.0f;    // MLC 给的是 RTT，单向按一半估
    out[4] = (float)LiGetEstimatedVideoFrameLossPercentage();
    out[5] = g_perfKbps;
}

} // extern "C"
extern "C" int LiSendDynamicResolution(int width, int height);

// 通过控制流请求主机改分辨率（Sunshine 统一动态参数包 0x5506：int[type=0, width, height]）
extern "C" __declspec(dllexport) int MS_RequestResolution(int width, int height)
{
    if (width <= 0 || height <= 0)
    {
        return -1;
    }

    return LiSendDynamicResolution(width, height);
}


// ---------------- 多点触控（Sunshine 扩展） ----------------
// 主机是否支持触摸：对应 MLC 的 LI_FF_PEN_TOUCH_EVENTS
extern "C" __declspec(dllexport) int MS_TouchSupported(void)
{
    return (LiGetHostFeatureFlags() & LI_FF_PEN_TOUCH_EVENTS) != 0 ? 1 : 0;
}

// type: 1=DOWN 2=UP 3=MOVE 4=CANCEL；x/y 为相对视频区域的 0..1 归一化坐标
extern "C" __declspec(dllexport) int MS_SendTouch(int type, unsigned int pointerId, float x, float y)
{
    if (x < 0.0f) { x = 0.0f; }
    if (x > 1.0f) { x = 1.0f; }
    if (y < 0.0f) { y = 0.0f; }
    if (y > 1.0f) { y = 1.0f; }

    return LiSendTouchEvent((uint8_t)type, pointerId, x, y, 1.0f, 0.0f, 0.0f, LI_ROT_UNKNOWN);
}

// 请求主机立刻发一个关键帧（画面卡住时用来恢复）
extern "C" __declspec(dllexport) int MS_RequestIdr(void)
{
    // 后台线程发，避免控制流忙时把 UI 线程卡住；带 pending 保护，避免线程堆积
    static std::atomic<bool> pending{ false };
    if (!pending.exchange(true))
    {
        std::thread([]()
        {
            Sleep(50);
            LiRequestIdrFrame();
            pending.store(false);
        }).detach();
    }
    return 0;
}
// ---- 手柄（P1/P2）----
extern "C" int LiSendMultiControllerEvent(short controllerNumber, short activeGamepadMask,
    int buttonFlags, unsigned char leftTrigger, unsigned char rightTrigger,
    short leftStickX, short leftStickY, short rightStickX, short rightStickY);

extern "C" __declspec(dllexport) int MS_SendController(int index, int mask, int buttons,
    int lt, int rt, int lx, int ly, int rx, int ry)
{
    return LiSendMultiControllerEvent((short)index, (short)mask, buttons,
        (unsigned char)(lt & 0xFF), (unsigned char)(rt & 0xFF),
        (short)lx, (short)ly, (short)rx, (short)ry);
}

extern "C" __declspec(dllexport) int MS_GetRumble(int* low, int* high)
{
    if (low) { *low = (int)g_rumbleLow.load(); }
    if (high) { *high = (int)g_rumbleHigh.load(); }
    return 0;
}