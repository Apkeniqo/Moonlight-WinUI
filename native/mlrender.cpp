// mlrender.cpp —— WinUI 3 视频面 + 网络包接口
//
// 第三版：不再"从文件读一帧画一帧"，而是模拟 moonlight-common-c 的解码回调：
//   · ML_PushPacket(data, len)  —— 外部（将来的 session 回调）把访问单元推进来
//   · ML_StartFile(path, fps)   —— 演示用：起一个线程按 60fps 把测试流当"网络包"推进来
//   · ML_Frame()                —— UI 线程每 tick 调一次：取包 → 解码 → 上屏
//
// 这样"喂数据"和"渲染"是解耦的，和真实串流的结构一致（网络线程 push，UI 线程画）。

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3d11_3.h>
#include <d3dcompiler.h>
#include <dbghelp.h>

#pragma comment(lib, "dbghelp.lib")

#include <cstdio>
#include <cstdint>
#include <cstdarg>
#include <cstring>
#include <vector>
#include <deque>
#include <mutex>
#include <thread>
#include <atomic>
#include <string>
#include <algorithm>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/imgutils.h>
#include <libavutil/error.h>
#include <libswscale/swscale.h>
}

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "dwmapi.lib")

extern "C" {
int  HudInit(void* d3dDevice);
void HudSetOwner(void* videoHwnd);
void HudSetVisible(int visible);
void HudSetText(const wchar_t* text, float opacity, float x, float y);
void HudSetGear(int x, int y, int visible);
int  HudTakeGearClick(void);
int  HudHitGear(int x, int y);
void HudClickGear(void);
void HudGetGear(int* x, int* y);
void HudSetGearVisible(int visible);
void HudSetGear(int x, int y, int visible);
void HudDraw(void* swapChain, int width, int height);
}

namespace {

const char* kShader = R"(
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.uv = p;
    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

Texture2D<float>  texY  : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState      samp  : register(s0);

float4 PS(VSOut i) : SV_Target
{
    float  y  = texY.Sample(samp, i.uv).r;
    float2 uv = texUV.Sample(samp, i.uv).rg;

    y = (y - 16.0 / 255.0) * 1.164383;
    float u = uv.r - 128.0 / 255.0;
    float v = uv.g - 128.0 / 255.0;

    float3 rgb;
    rgb.r = y + 1.792741 * v;
    rgb.g = y - 0.213249 * u - 0.532909 * v;
    rgb.b = y + 2.112402 * u;
    return float4(saturate(rgb), 1.0);
}
)";

double NowSeconds()
{
    static LARGE_INTEGER freq = {};
    if (freq.QuadPart == 0) { QueryPerformanceFrequency(&freq); }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return (double)now.QuadPart / (double)freq.QuadPart;
}

struct Renderer
{
    // --- D3D11 ---
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    IDXGISwapChain1* swap = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11SamplerState* samp = nullptr;
    ID3D11Texture2D* copyTex = nullptr;
    ID3D11ShaderResourceView* srvY = nullptr;
    ID3D11ShaderResourceView* srvUV = nullptr;
    int w = 0;
    int h = 0;

    // 最近一帧的解码尺寸（= 流的分辨率，和 D3D 交换链尺寸无关）
    std::atomic<int> streamW{0};
    std::atomic<int> streamH{0};

    // --- 解码 ---
    AVCodecContext* dec = nullptr;
    AVCodecID codecId = AV_CODEC_ID_NONE;   // 当前解码器对应的编码器（H264 / HEVC / AV1）
    bool software = false;                  // true = 软件解码（AV1 走这条）
    AVBufferRef* hw = nullptr;
    AVPacket* pkt = nullptr;
    AVFrame* frame = nullptr;
    AVFrame* hold = nullptr;   // 一次 ML_Frame 里只上屏最后一帧

    // --- 软件解码上屏（AV1 用）---
    SwsContext* sws = nullptr;
    std::vector<uint8_t> nv12;
    int copyW = 0;
    int copyH = 0;
    int swW = 0;
    int swH = 0;
    int swFormat = -1;

    // --- 包队列（模拟网络回调）---
    std::mutex qmutex;
    std::mutex decodeLock;   // ML_Frame 与 ML_SetVideoFormat/ML_Close 互斥（不在解码中途换解码器）
    std::deque<std::vector<uint8_t>> queue;
    std::thread feeder;
    std::atomic<bool> running{false};
    std::atomic<long long> pushed{0};
    std::atomic<long long> dropped{0};

    // --- 统计 ---
    std::atomic<bool> lastPresentOk{true};    // 上一帧是否真的上屏（false = 被遮挡/忙跳过）
    std::atomic<long long> decoded{0};
    std::atomic<double> lastDecodeMs{0};
    std::atomic<int> queueDepth{0};
};

Renderer g;

extern "C" void LiRequestIdrFrame(void);

// 请求主机补发关键帧（换解码器 / 分辨率变化后用），后台线程延迟 200ms 发，避免打断当前流程
void RequestIdrSoon()
{
    static std::atomic<int> pending{ 0 };

    if (pending.exchange(1) != 0)
    {
        return;
    }

    std::thread([]()
    {
        Sleep(200);
        LiRequestIdrFrame();
        pending.store(0);
    }).detach();
}

// ---------------------------------------------------------------- 工具

bool PipeProbe()
{
    static const bool enabled = []()
    {
        wchar_t buf[8] = {};
        return GetEnvironmentVariableW(L"ML_DEBUG_PIPE", buf, 8) > 0;
    }();

    return enabled;
}

void Log(const char* fmt, ...)
{
    static char path[MAX_PATH] = {};
    if (path[0] == 0)
    {
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        char* slash = strrchr(path, '\\');
        if (slash) { *(slash + 1) = 0; }
        strcat_s(path, "decoder.log");
    }
    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") == 0 && f)
    {
        va_list ap;
        va_start(ap, fmt);
        vfprintf(f, fmt, ap);
        va_end(ap);
        fputc('\n', f);
        fclose(f);
    }
}

// ---------------------------------------------------------------- 崩溃现场

// 未处理异常兜底：写 decoder.log（异常码/地址/模块）+ 生成 minidump。
// C# 的 UnhandledException 拦不到原生崩溃，而"弹窗开着开着闪退"正是这种，
// 没有 dump 只能靠猜。装了这个之后，下次崩溃能在 exe 目录拿到 crash-*.dmp。
LONG WINAPI CrashHandler(EXCEPTION_POINTERS* info)
{
    const DWORD code = (info && info->ExceptionRecord) ? info->ExceptionRecord->ExceptionCode : 0;
    const void* addr = (info && info->ExceptionRecord) ? info->ExceptionRecord->ExceptionAddress : nullptr;

    char module[MAX_PATH] = "?";
    HMODULE hmod = nullptr;
    if (addr &&
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCWSTR)addr, &hmod) && hmod)
    {
        char path[MAX_PATH] = {};
        GetModuleFileNameA(hmod, path, MAX_PATH);
        const char* slash = strrchr(path, '\\');
        strcpy_s(module, slash ? slash + 1 : path);
    }

    Log("!!! 崩溃 code=0x%08X addr=%p module=%s", (unsigned)code, addr, module);

    char dump[MAX_PATH] = {};
    GetModuleFileNameA(nullptr, dump, MAX_PATH);
    char* slash = strrchr(dump, '\\');
    if (slash) { *(slash + 1) = 0; }

    SYSTEMTIME st;
    GetLocalTime(&st);
    char name[80] = {};
    sprintf_s(name, "crash-%04d%02d%02d-%02d%02d%02d.dmp",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    strcat_s(dump, name);

    HANDLE file = CreateFileA(dump, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        MINIDUMP_EXCEPTION_INFORMATION mei = {};
        mei.ThreadId = GetCurrentThreadId();
        mei.ExceptionPointers = info;
        mei.ClientPointers = FALSE;
        MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), file,
                          MiniDumpNormal, &mei, nullptr, nullptr);
        CloseHandle(file);
        Log("!!! 已写 dump：%s", dump);
    }

    return EXCEPTION_CONTINUE_SEARCH;   // 交回系统，保持默认的崩溃终止流程
}

bool CreateRenderTarget()
{
    ID3D11Texture2D* back = nullptr;
    if (FAILED(g.swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back)))
    {
        return false;
    }
    HRESULT hr = g.dev->CreateRenderTargetView(back, nullptr, &g.rtv);
    back->Release();
    return SUCCEEDED(hr);
}bool CreateShaders()
{
    ID3DBlob* vsBlob = nullptr;
    ID3DBlob* psBlob = nullptr;
    ID3DBlob* err = nullptr;

    if (FAILED(D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vsBlob, &err)))
    {
        if (err) err->Release();
        return false;
    }
    if (err) { err->Release(); err = nullptr; }

    if (FAILED(D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &psBlob, &err)))
    {
        if (err) err->Release();
        vsBlob->Release();
        return false;
    }
    if (err) { err->Release(); }

    HRESULT hr = g.dev->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &g.vs);
    if (SUCCEEDED(hr))
    {
        hr = g.dev->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &g.ps);
    }
    vsBlob->Release();
    psBlob->Release();
    return SUCCEEDED(hr);
}

bool SetupHardwareDecoder()
{
    g.hw = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
    if (!g.hw)
    {
        return false;
    }

    auto* hwctx = (AVHWDeviceContext*)g.hw->data;
    auto* d3d = (AVD3D11VADeviceContext*)hwctx->hwctx;
    d3d->device = g.dev;
    d3d->device->AddRef();
    d3d->device_context = g.ctx;
    d3d->device_context->AddRef();

    if (av_hwdevice_ctx_init(g.hw) < 0)
    {
        return false;
    }

    g.dec->hw_device_ctx = av_buffer_ref(g.hw);
    return g.dec->hw_device_ctx != nullptr;
}

// 换编码器（H.264 / HEVC / AV1）。
// hardware=true 用 d3d11va 硬解（H.264/HEVC）；AV1 走软件解码（dav1d/FFmpeg 自带），
// 解出来的 YUV420P 再经 sws_scale 转 NV12 上传到纹理。
bool OpenDecoder(AVCodecID id, bool hardware = true)
{
    {
        std::lock_guard<std::mutex> lock(g.qmutex);
        g.queue.clear();
        g.queueDepth = 0;
    }

    if (g.dec)
    {
        avcodec_free_context(&g.dec);
    }
    if (g.hw)
    {
        av_buffer_unref(&g.hw);
        g.hw = nullptr;
    }
    if (g.sws)
    {
        sws_freeContext(g.sws);
        g.sws = nullptr;
    }
    g.swW = g.swH = 0;
    g.swFormat = -1;
    g.codecId = AV_CODEC_ID_NONE;

    const AVCodec* codec = avcodec_find_decoder(id);
    if (!codec)
    {
        Log("avcodec_find_decoder(%d) 失败", (int)id);
        return false;
    }

    g.dec = avcodec_alloc_context3(codec);
    if (!g.dec)
    {
        Log("avcodec_alloc_context3 失败");
        return false;
    }

    if (hardware)
    {
        g.dec->pix_fmt = AV_PIX_FMT_D3D11;
        if (!SetupHardwareDecoder())
        {
            Log("%s：d3d11va 初始化失败", codec->name);
            avcodec_free_context(&g.dec);
            return false;
        }
    }

    if (avcodec_open2(g.dec, codec, nullptr) < 0)
    {
        Log("avcodec_open2(%s) 失败", codec->name);
        avcodec_free_context(&g.dec);
        return false;
    }

    g.codecId = id;
    g.software = !hardware;
    Log("解码器就绪：%s（%s，%s）", codec->name, codec->long_name ? codec->long_name : "",
        hardware ? "d3d11va" : "软件");
    return true;
}

// 把 Annex-B 裸流切成访问单元：遇到 VCL NAL（1/5）就开始一个新 AU。
// 测试流是单 slice，所以"一个 VCL NAL = 一帧"。
std::vector<std::vector<uint8_t>> SplitAccessUnits(const std::vector<uint8_t>& buf)
{
    std::vector<std::vector<uint8_t>> aus;
    std::vector<size_t> starts;

    for (size_t i = 0; i + 3 < buf.size(); ++i)
    {
        if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1)
        {
            starts.push_back(i);
        }
    }
    if (starts.empty())
    {
        return aus;
    }

    std::vector<uint8_t> current;
    for (size_t k = 0; k < starts.size(); ++k)
    {
        size_t begin = starts[k];
        size_t end = (k + 1 < starts.size()) ? starts[k + 1] : buf.size();

        size_t nalStart = begin + 3;
        if (nalStart >= end) { continue; }
        int nalType = buf[nalStart] & 0x1F;
        bool isVcl = (nalType == 1 || nalType == 5);

        if (isVcl && !current.empty())
        {
            aus.push_back(std::move(current));
            current.clear();
        }
        current.insert(current.end(), buf.begin() + begin, buf.begin() + end);
    }
    if (!current.empty())
    {
        aus.push_back(std::move(current));
    }
    return aus;
}

bool ReadWholeFile(const char* path, std::vector<uint8_t>& out)
{
    FILE* f = nullptr;
    if (fopen_s(&f, path, "rb") != 0 || !f)
    {
        return false;
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    out.resize((size_t)size);
    size_t got = fread(out.data(), 1, (size_t)size, f);
    fclose(f);
    out.resize(got);
    return got > 0;
}

// 懒创建 NV12 拷贝纹理 + 两个平面 SRV（硬解/软解共用）
bool EnsureCopyTexture(int width, int height)
{
    if (g.copyTex && g.copyW == width && g.copyH == height)
    {
        return true;
    }

    if (g.srvY) { g.srvY->Release(); g.srvY = nullptr; }
    if (g.srvUV) { g.srvUV->Release(); g.srvUV = nullptr; }
    if (g.copyTex) { g.copyTex->Release(); g.copyTex = nullptr; }

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = (UINT)width;
    td.Height = (UINT)height;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(g.dev->CreateTexture2D(&td, nullptr, &g.copyTex)))
    {
        Log("CreateTexture2D(copy) failed");
        return false;
    }

    ID3D11Device3* dev3 = nullptr;
    if (FAILED(g.dev->QueryInterface(__uuidof(ID3D11Device3), (void**)&dev3)) || !dev3)
    {
        Log("QueryInterface(ID3D11Device3) failed");
        return false;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC1 sd = {};
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
    sd.Texture2DArray.MostDetailedMip = 0;
    sd.Texture2DArray.MipLevels = 1;
    sd.Texture2DArray.FirstArraySlice = 0;
    sd.Texture2DArray.ArraySize = 1;

    ID3D11ShaderResourceView1* srv1 = nullptr;

    sd.Format = DXGI_FORMAT_R8_UNORM;
    sd.Texture2DArray.PlaneSlice = 0;
    dev3->CreateShaderResourceView1(g.copyTex, &sd, &srv1);
    g.srvY = srv1;
    srv1 = nullptr;

    sd.Format = DXGI_FORMAT_R8G8_UNORM;
    sd.Texture2DArray.PlaneSlice = 1;
    dev3->CreateShaderResourceView1(g.copyTex, &sd, &srv1);
    g.srvUV = srv1;

    dev3->Release();

    if (!g.srvY || !g.srvUV)
    {
        Log("CreateShaderResourceView1(copy) failed");
        return false;
    }

    g.copyW = width;
    g.copyH = height;
    return true;
}

// 软件解码帧（AV1）→ YUV420P/NV12 → NV12 纹理
bool UploadSoftwareFrame(const AVFrame* frame)
{
    const int width = frame->width;
    const int height = frame->height;
    if (width <= 0 || height <= 0 || !EnsureCopyTexture(width, height))
    {
        return false;
    }

    if (!g.sws || g.swW != width || g.swH != height || g.swFormat != frame->format)
    {
        if (g.sws) { sws_freeContext(g.sws); g.sws = nullptr; }
        g.sws = sws_getContext(width, height, (AVPixelFormat)frame->format,
                               width, height, AV_PIX_FMT_NV12,
                               SWS_BILINEAR, nullptr, nullptr, nullptr);
        g.swW = width;
        g.swH = height;
        g.swFormat = frame->format;
        if (!g.sws)
        {
            Log("sws_getContext 失败");
            return false;
        }
    }

    g.nv12.resize((size_t)width * height * 3 / 2);
    uint8_t* dst[4] = { g.nv12.data(), g.nv12.data() + (size_t)width * height, nullptr, nullptr };
    int dstStride[4] = { width, width, 0, 0 };
    sws_scale(g.sws, frame->data, frame->linesize, 0, height, dst, dstStride);

    g.ctx->UpdateSubresource(g.copyTex, 0, nullptr, g.nv12.data(), width, 0);
    g.ctx->UpdateSubresource(g.copyTex, 1, nullptr, g.nv12.data() + (size_t)width * height, width, 0);
    return true;
}

void PresentFrame()
{
    D3D11_VIEWPORT vp = { 0, 0, (float)g.w, (float)g.h, 0, 1 };
    g.ctx->OMSetRenderTargets(1, &g.rtv, nullptr);
    g.ctx->RSSetViewports(1, &vp);
    g.ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g.ctx->VSSetShader(g.vs, nullptr, 0);
    g.ctx->PSSetShader(g.ps, nullptr, 0);
    g.ctx->PSSetShaderResources(0, 1, &g.srvY);
    g.ctx->PSSetShaderResources(1, 1, &g.srvUV);
    g.ctx->PSSetSamplers(0, 1, &g.samp);
    g.ctx->Draw(3, 0);
    g.ctx->OMSetRenderTargets(0, nullptr, nullptr);
    HudDraw(g.swap, g.w, g.h);

    // 关键：绝对不能用 Present(1, 0)。
    // 这个调用跑在 UI 线程（CompositionTarget.Rendering → ML_Frame → 这里），
    // 而窗口被遮挡 / 最小化 / 被置顶弹窗盖住 / 拖到屏幕外时，同步间隔 >0 的 Present
    // 会一直等垂直同步（DWM 不消费帧就不返回）—— 整条消息循环直接卡死，
    // 表现就是"串流画面卡住"，连弹窗都点不动。
    // 改成「一秒都不等」：忙或被遮挡就丢掉这一帧（反正渲染端本来只保留最后一帧）。
    const HRESULT phr = g.swap->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
    if (phr == DXGI_ERROR_WAS_STILL_DRAWING || phr == DXGI_STATUS_OCCLUDED)
    {
        static std::atomic<long long> skipped{ 0 };
        const long long n = skipped.fetch_add(1) + 1;
        g.lastPresentOk = false;
        if (n % 600 == 0 && PipeProbe())
        {
            Log("present 跳过累计 %lld（忙=上一帧未消费 / 被遮挡）", n);
        }
        return;
    }

    if (FAILED(phr))
    {
        static std::atomic<int> logged{ 0 };
        if (logged.fetch_add(1) < 3)
        {
            Log("Present 失败 hr=0x%08X（w=%d h=%d）", (unsigned)phr, g.w, g.h);
        }
        return;
    }

    g.lastPresentOk = true;
    g.decoded++;
}

void DrawFrame(AVFrame* frame)
{
    if (frame->width > 0 && frame->height > 0)
    {
        g.streamW = frame->width;
        g.streamH = frame->height;
    }

    if (frame->format == AV_PIX_FMT_D3D11)
    {
        auto* tex = (ID3D11Texture2D*)frame->data[0];
        const int slice = (int)(intptr_t)frame->data[1];
        if (!tex || !EnsureCopyTexture(frame->width, frame->height))
        {
            return;
        }

        g.ctx->CopySubresourceRegion(g.copyTex, 0, 0, 0, 0, tex, (UINT)slice, nullptr);
        PresentFrame();
        return;
    }

    // 软件解码出来的 YUV420P（AV1）/ NV12
    if (frame->format == AV_PIX_FMT_YUV420P || frame->format == AV_PIX_FMT_NV12 ||
        frame->format == AV_PIX_FMT_YUVJ420P)
    {
        if (UploadSoftwareFrame(frame))
        {
            PresentFrame();
        }
    }
}

// 演示用"网络源"：按 fps 把访问单元推进队列
void FeedLoop(std::string path, double fps)
{
    std::vector<uint8_t> raw;
    if (!ReadWholeFile(path.c_str(), raw))
    {
        Log("feeder: 读文件失败 %s", path.c_str());
        return;
    }

    auto aus = SplitAccessUnits(raw);
    Log("feeder: %zu 个访问单元, fps=%.0f", aus.size(), fps);
    if (aus.empty())
    {
        return;
    }

    const double interval = 1.0 / fps;
    double next = NowSeconds();
    size_t i = 0;

    while (g.running)
    {
        double now = NowSeconds();
        if (now < next)
        {
            Sleep(1);
            continue;
        }
        next += interval;
        if (now - next > 0.5)
        {
            // 落后太多就重新对齐，避免追帧
            next = now + interval;
        }

        {
            std::lock_guard<std::mutex> lock(g.qmutex);
            g.queue.emplace_back(aus[i % aus.size()]);
            g.pushed++;
            // 队列太深就丢最旧的，模拟实时流的背压
            while (g.queue.size() > 8)
            {
                g.queue.pop_front();
                g.dropped++;
            }
            g.queueDepth = (int)g.queue.size();
        }
        i++;
    }
}

} // namespace

extern "C" {

__declspec(dllexport) int ML_Init(void* device, void* context, void* swapChain, int w, int h)
{
    // 装一次崩溃兜底（写 dump + decoder.log），必须早于任何可能崩的路径
    static bool crashHandlerInstalled = false;
    if (!crashHandlerInstalled)
    {
        SetUnhandledExceptionFilter(CrashHandler);
        crashHandlerInstalled = true;
    }

    g.dev = (ID3D11Device*)device;
    g.ctx = (ID3D11DeviceContext*)context;
    g.swap = (IDXGISwapChain1*)swapChain;
    g.w = w;
    g.h = h;

    g.dev->AddRef();
    g.ctx->AddRef();
    g.swap->AddRef();

    if (!CreateRenderTarget() || !CreateShaders())
    {
        return -1;
    }

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    if (FAILED(g.dev->CreateSamplerState(&sd, &g.samp)))
    {
        return -1;
    }

    const AVCodec* codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!codec)
    {
        return -10;
    }

    if (!OpenDecoder(AV_CODEC_ID_H264))
    {
        return -12;
    }

    g.pkt = av_packet_alloc();
    g.frame = av_frame_alloc();
    g.hold = av_frame_alloc();
    if (!g.pkt || !g.frame)
    {
        return -16;
    }

    return 0;
}

// mlsession 在视频协商（setup 回调）里调它切编码器。
// 参数用 MLC 的 VIDEO_FORMAT_* 值：H264=0x0001，H265=0x0100，H265_MAIN10=0x0200，AV1_MAIN8=0x1000。
// Main10 暂按 8-bit 解（上屏还是 NV12）；AV1 走软件解码（dav1d）。
__declspec(dllexport) int ML_SetVideoFormat(int mlcFormat)
{
    // 还没 ML_Init（比如控制台探针不建 D3D 设备）时，只报告不切换
    if (!g.dev || !g.ctx)
    {
        Log("ML_SetVideoFormat：还没有 D3D 设备，跳过解码器切换");
        return -3;
    }

    AVCodecID want;
    bool hardware = true;

    switch (mlcFormat)
    {
    case 0x0001:
        want = AV_CODEC_ID_H264;
        break;
    case 0x0100:
    case 0x0200:
        want = AV_CODEC_ID_HEVC;
        break;
    case 0x1000:
        want = AV_CODEC_ID_AV1;
        hardware = false;   // AV1 先软解：d3d11va 只有 RDNA2+/RTX30+ 才支持
        break;
    default:
        Log("ML_SetVideoFormat：不认识的格式 0x%X", mlcFormat);
        return -2;
    }

    if (g.dec && g.codecId == want)
    {
        return 0; // 已经是这个编码器
    }

    std::lock_guard<std::mutex> decodeLock(g.decodeLock);
    const bool ok = OpenDecoder(want, hardware);
    if (ok)
    {
        // 换解码器后必须等关键帧，否则画面会一直停住直到主机周期 IDR
        RequestIdrSoon();
    }

    return ok ? 0 : -1;
}

// 真实串流时，这个函数就是 moonlight-common-c 的解码回调
__declspec(dllexport) void ML_PushPacket(const void* data, int len)
{
    if (!data || len <= 0)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(g.qmutex);
    g.queue.emplace_back((const uint8_t*)data, (const uint8_t*)data + len);
    g.pushed++;
    while (g.queue.size() > 8)
    {
        g.queue.pop_front();
        g.dropped++;
    }
    g.queueDepth = (int)g.queue.size();

    if (g.pushed % 120 == 0)
    {
        if (PipeProbe()) Log("push=%llu dropped=%llu queue=%d", (unsigned long long)g.pushed, (unsigned long long)g.dropped, (int)g.queueDepth);
    }
}

__declspec(dllexport) int ML_StartFile(const char* path, double fps)
{
    if (g.running)
    {
        return 0;
    }
    g.running = true;
    g.feeder = std::thread(FeedLoop, std::string(path), fps);
    return 0;
}

// 切到真串流前先停掉演示用的"文件喂包"线程；解码器不动，后面照样 ML_Frame
__declspec(dllexport) void ML_StopFile(void)
{
    g.running = false;
    if (g.feeder.joinable())
    {
        g.feeder.join();
    }

    std::lock_guard<std::mutex> lock(g.qmutex);
    g.queue.clear();
    g.queueDepth = 0;
}

__declspec(dllexport) int ML_Frame(void)
{
    {
        static std::atomic<uint64_t> frameTick{ 0 };
        const uint64_t n = ++frameTick;
        if (n % 120 == 0)
        {
            if (PipeProbe()) Log("frame=%llu queue=%d", (unsigned long long)n, (int)g.queueDepth);
        }
    }
    {
        if (!g.dec || !g.pkt || !g.frame)
    {
        return 0;
    }

    // 解码期间不允许换解码器（DrSetup 在会话线程里调 ML_SetVideoFormat）
    std::lock_guard<std::mutex> decodeLock(g.decodeLock);

    const double t0 = NowSeconds();
    bool decoded = false;

    for (;;)
    {
        // 一次渲染最多花 8ms 解码，剩下的留给下一个 tick，别把 UI 线程堵住
        if (NowSeconds() - t0 > 0.008)
        {
            break;
        }

        std::vector<uint8_t> au;
        {
            std::lock_guard<std::mutex> lock(g.qmutex);
            if (g.queue.empty())
            {
                break;
            }
            au = std::move(g.queue.front());
            g.queue.pop_front();
            g.queueDepth = (int)g.queue.size();
        }

        av_packet_unref(g.pkt);
        if (av_new_packet(g.pkt, (int)au.size()) < 0)
        {
            break;
        }
        memcpy(g.pkt->data, au.data(), au.size());
        const int sendRet = avcodec_send_packet(g.dec, g.pkt);

        // 解码器缓冲满时 send 会返回 EAGAIN，而这一包并没有被消费。
        // 以前直接忽略就等于丢了整帧（丢到 IDR 的话画面会一直停在上一帧，
        // 直到主机下一帧关键帧才恢复）。这里先取干净再重送一次。
        if (sendRet == AVERROR(EAGAIN))
        {
            while (avcodec_receive_frame(g.dec, g.frame) == 0)
            {
                if (g.hold)
                {
                    av_frame_unref(g.hold);
                    av_frame_ref(g.hold, g.frame);
                }
                av_frame_unref(g.frame);
                decoded = true;
            }
            avcodec_send_packet(g.dec, g.pkt);
        }

        {
            static std::atomic<uint64_t> sendTick{ 0 };
            if (++sendTick % 120 == 0)
            {
                if (PipeProbe()) Log("send=%d packets=%llu queue=%d", sendRet, (unsigned long long)sendTick, (int)g.queueDepth);
            }
        }

        int ret = 0;
        while ((ret = avcodec_receive_frame(g.dec, g.frame)) == 0)
        {
            // 只保留最后一帧：队列里还有帧时，中间帧没必要上屏（一轮只上屏一次就够）
            if (g.hold)
            {
                av_frame_unref(g.hold);
                av_frame_ref(g.hold, g.frame);
            }

            av_frame_unref(g.frame);
            decoded = true;
        }
    }

    // 卡顿取证：还有包排队、却一直解不出帧 → 问题在解码/上屏这一侧（不是网络）
    {
        static std::atomic<double> lastFrameSec{ 0.0 };
        static std::atomic<double> lastWarnSec{ 0.0 };
        const double now = NowSeconds();
        if (decoded)
        {
            lastFrameSec.store(now);
        }
        else if (g.queueDepth > 0)
        {
            const double last = lastFrameSec.load();
            if (last > 0.0 && now - last > 3.0 && now - lastWarnSec.load() > 5.0)
            {
                lastWarnSec.store(now);
                Log("!! 解码停滞：%.1f 秒没解出帧（队列 %d / 已推 %lld / 已丢 %lld）",
                    now - last, (int)g.queueDepth,
                    (long long)g.pushed.load(), (long long)g.dropped.load());
            }
        }
    }

    // 上屏取证：解码还在跑、但 Present 连续被跳过（遮挡/忙）超过 2 秒
    {
        static std::atomic<double> lastOkSec{ 0.0 };
        static std::atomic<double> lastWarnSec{ 0.0 };
        const double nowOk = NowSeconds();
        if (g.lastPresentOk.load())
        {
            lastOkSec.store(nowOk);
        }
        else if (decoded)
        {
            const double lastOk = lastOkSec.load();
            if (lastOk > 0.0 && nowOk - lastOk > 2.0 && nowOk - lastWarnSec.load() > 5.0)
            {
                lastWarnSec.store(nowOk);
                Log("!! 上屏停滞：%.1f 秒没能真正 Present（遮挡/忙跳过），解码仍在进行", nowOk - lastOk);
            }
        }
    }

    if (decoded && g.hold && g.hold->data[0])
    {
        static std::atomic<uint64_t> drawTick{ 0 };
        const uint64_t dn = ++drawTick;
        DrawFrame(g.hold);
        if (dn % 120 == 0)
        {
            if (PipeProbe()) Log("draw=%llu queue=%d", (unsigned long long)dn, (int)g.queueDepth);
        }
        av_frame_unref(g.hold);
    }

    if (decoded)
    {
        g.lastDecodeMs = (NowSeconds() - t0) * 1000.0;
    }
    return decoded ? 1 : 0;
    }
}

__declspec(dllexport) void ML_Resize(int w, int h)
{
    if (!g.swap || w < 2 || h < 2 || (w == g.w && h == g.h))
    {
        return;
    }

    g.ctx->OMSetRenderTargets(0, nullptr, nullptr);
    if (g.rtv) { g.rtv->Release(); g.rtv = nullptr; }

    if (SUCCEEDED(g.swap->ResizeBuffers(2, (UINT)w, (UINT)h, DXGI_FORMAT_B8G8R8A8_UNORM, 0)))
    {
        g.w = w;
        g.h = h;
        CreateRenderTarget();
    }
}

__declspec(dllexport) void ML_DecoderInfo(char* buf, int cap)
{
    if (!buf || cap <= 0)
    {
        return;
    }

    const char* codec = "未知";
    switch (g.codecId)
    {
    case AV_CODEC_ID_H264: codec = "H.264"; break;
    case AV_CODEC_ID_HEVC: codec = "HEVC"; break;
    case AV_CODEC_ID_AV1: codec = "AV1"; break;
    default: break;
    }

    snprintf(buf, (size_t)cap, "%s·%s", codec, g.software ? "软解" : "硬解");
}

__declspec(dllexport) void ML_Size(int* width, int* height)
{
    if (width) { *width = g.dec ? g.dec->width : 0; }
    if (height) { *height = g.dec ? g.dec->height : 0; }
}

// 流的分辨率（最近一帧解码出来的尺寸）；还没解到帧时返回 0。
// 注意别和 ML_Size 混：那个是解码器/交换链的尺寸，不是流的分辨率。
__declspec(dllexport) void ML_StreamSize(int* width, int* height)
{
    if (width) { *width = g.streamW.load(); }
    if (height) { *height = g.streamH.load(); }
}

__declspec(dllexport) void ML_Stats(int* decodedFrames, double* lastDecodeMs)
{
    if (decodedFrames) { *decodedFrames = (int)g.decoded.load(); }
    if (lastDecodeMs) { *lastDecodeMs = g.lastDecodeMs.load(); }
}

__declspec(dllexport) void ML_Queue(int* depth, long long* pushed, long long* dropped)
{
    if (depth) { *depth = g.queueDepth.load(); }
    if (pushed) { *pushed = g.pushed.load(); }
    if (dropped) { *dropped = g.dropped.load(); }
}

__declspec(dllexport) void ML_Close(void)
{
    std::lock_guard<std::mutex> decodeLock(g.decodeLock);

    g.running = false;
    if (g.feeder.joinable())
    {
        g.feeder.join();
    }

    {
        std::lock_guard<std::mutex> lock(g.qmutex);
        g.queue.clear();
    }

    if (g.hold) { av_frame_free(&g.hold); g.hold = nullptr; }
    if (g.frame) { av_frame_free(&g.frame); g.frame = nullptr; }
    if (g.pkt) { av_packet_free(&g.pkt); g.pkt = nullptr; }
    if (g.dec) { avcodec_free_context(&g.dec); g.dec = nullptr; }
    if (g.hw) { av_buffer_unref(&g.hw); g.hw = nullptr; }
    if (g.sws) { sws_freeContext(g.sws); g.sws = nullptr; }
    g.nv12.clear();
    g.copyW = g.copyH = 0;

    if (g.srvUV) { g.srvUV->Release(); g.srvUV = nullptr; }
    if (g.srvY) { g.srvY->Release(); g.srvY = nullptr; }
    if (g.copyTex) { g.copyTex->Release(); g.copyTex = nullptr; }
    if (g.samp) { g.samp->Release(); g.samp = nullptr; }
    if (g.ps) { g.ps->Release(); g.ps = nullptr; }
    if (g.vs) { g.vs->Release(); g.vs = nullptr; }
    if (g.rtv) { g.rtv->Release(); g.rtv = nullptr; }
    if (g.swap) { g.swap->Release(); g.swap = nullptr; }
    if (g.ctx) { g.ctx->Release(); g.ctx = nullptr; }
    if (g.dev) { g.dev->Release(); g.dev = nullptr; }
}

} // extern "C"

// ===================================================================
// 原生子窗口宿主：视频 + 光标都由这个 HWND 负责。
// 为什么：WinUI 3 不允许应用改系统指针（SetCursor / ProtectedCursor 都被拦），
// 而普通 Win32 窗口在 WM_SETCURSOR 里 SetCursor 是有效的，所以把视频面搬到一个
// 嵌进 WinUI 窗口的子 HWND 上，就能做"隐藏远程光标 + 本地渲染"。
// ===================================================================
#include <windowsx.h>

extern "C" {
int  HudInit(void* d3dDevice);
void HudSetOwner(void* videoHwnd);
void HudSetVisible(int visible);
void HudSetText(const wchar_t* text, float opacity, float x, float y);
void HudSetGear(int x, int y, int visible);
int  HudTakeGearClick(void);
int  HudHitGear(int x, int y);
void HudClickGear(void);
void HudGetGear(int* x, int* y);
void HudSetGearVisible(int visible);
void HudSetGear(int x, int y, int visible);
void HudDraw(void* swapChain, int width, int height);
}
#include <dwmapi.h>

extern "C" {
int MS_SendMousePosition(int x, int y, int width, int height);
int MS_SendMouseMove(int dx, int dy);
int MS_SendMouseButton(int button, int down);
int MS_SendMouseScroll(int clicks);
int MS_SendMouseHScroll(int clicks);
int MS_CursorInfo(int* visible, int* shapeId, int* width, int* height, int* hotX, int* hotY);
int MS_CursorShape(unsigned char* buffer, int capacity, int* shapeId);
int MS_SendKey(int vk, int down, int modifiers);
}

namespace {

// 键盘状态与修饰键
static bool g_keyDown[256] = {};

static int KeyModifiers();

static ULONGLONG g_lastBtnUpTick = 0;

static HHOOK g_kbHook = nullptr;
static HWND g_kbRoot = nullptr;
static bool g_kbSwallow = false;

static LRESULT CALLBACK KeyboardHookProc(int code, WPARAM wp, LPARAM lp)
{
    if (code != HC_ACTION || !g_kbSwallow)
    {
        return CallNextHookEx(g_kbHook, code, wp, lp);
    }

    // 只在串流窗口是前台时接管键盘，否则放行（不然整个系统都被吞键）
    {
        if (!g_kbRoot || GetForegroundWindow() != g_kbRoot)
        {
            return CallNextHookEx(g_kbHook, code, wp, lp);
        }
    }

    const KBDLLHOOKSTRUCT* kb = (const KBDLLHOOKSTRUCT*)lp;
    const int vk = (int)kb->vkCode;

    const bool down = (wp == WM_KEYDOWN || wp == WM_SYSKEYDOWN);
    MS_SendKey(vk, down ? 1 : 0, KeyModifiers());
    return 1;   // 吞掉，不让本地系统处理（否则 Win 键会弹开始菜单且收不到抬键）
}

static void EnsureKeyboardHook()
{
    if (g_kbHook)
    {
        return;
    }

    g_kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardHookProc, GetModuleHandleW(nullptr), 0);
}

static void RemoveKeyboardHook()
{
    if (g_kbHook)
    {
        UnhookWindowsHookEx(g_kbHook);
        g_kbHook = nullptr;
    }
}
static int KeyModifiers()
{
    int mods = 0;
    if (GetKeyState(VK_SHIFT) & 0x8000) { mods |= 0x01; }
    if (GetKeyState(VK_CONTROL) & 0x8000) { mods |= 0x02; }
    if (GetKeyState(VK_MENU) & 0x8000) { mods |= 0x04; }
    if ((GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000)) { mods |= 0x08; }
    return mods;
}

struct HostWindowState
{
    HWND parent = nullptr;
    HWND child = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    IDXGISwapChain1* swap = nullptr;
    int x = 0, y = 0, w = 0, h = 0;
    HCURSOR cursor = nullptr;
    HCURSOR lastCursor = nullptr;
    int cursorId = -1;
};

HostWindowState g_host;

// 指针锁定（游戏内）：WM_MOUSEMOVE 改发相对位移，不再发绝对坐标。
// C# 侧负责 ClipCursor + 把指针钉在中心 + 藏光标；这里只把"位置变化"翻译成相对移动。
bool  g_pointerLocked = false;
POINT g_lastMouse = {};
bool  g_hasLastMouse = false;

bool g_gearEnabled = true;
int  g_gearDockSide = 0;      // 0=右 1=左 2=上 3=下
bool g_gearShown = false;
ULONGLONG g_gearLastHover = 0;

void GearShow(bool show)
{
    if (!g_gearEnabled) { return; }
    if (g_gearShown == show)
    {
        return;
    }

    g_gearShown = show;
    HudSetGearVisible(show ? 1 : 0);
}

void GearDockToEdge(int w, int h, int mx, int my)
{
    const int size = 44;
    const int margin = 12;

    int best = 0;
    int bestDist = w - mx;
    if (mx < bestDist) { bestDist = mx; best = 1; }
    if (my < bestDist) { bestDist = my; best = 2; }
    if (h - my < bestDist) { best = 3; }

    g_gearDockSide = best;

    int x = mx;
    int y = my;
    switch (best)
    {
    case 0: x = w - size - margin; break;
    case 1: x = margin; break;
    case 2: y = margin; break;
    default: y = h - size - margin; break;
    }

    x = (int)max((int)margin, min(x, w - size - margin));
    y = (int)max((int)margin, min(y, h - size - margin));
    HudSetGear(x, y, 1);
    g_gearLastHover = GetTickCount64();
}

bool GearNear(int mx, int my, int w, int h)
{
    if (!g_gearEnabled) { return false; }
    const int zone = 70;
    switch (g_gearDockSide)
    {
    case 1: if (mx < zone) return true; break;
    case 2: if (my < zone) return true; break;
    case 3: if (my > h - zone) return true; break;
    default: if (mx > w - zone) return true; break;
    }

    int gx = 0, gy = 0;
    HudGetGear(&gx, &gy);
    return mx >= gx - 12 && mx <= gx + 56 && my >= gy - 12 && my <= gy + 56;
}

bool g_leftDown = false;
bool g_rightDown = false;
bool g_midDown = false;

void ReleaseAllButtons()
{
    if (g_leftDown) { MS_SendMouseButton(1, 0); g_leftDown = false; }
    if (g_rightDown) { MS_SendMouseButton(3, 0); g_rightDown = false; }
    if (g_midDown) { MS_SendMouseButton(2, 0); g_midDown = false; }
}

bool g_gearDragging = false;
bool g_gearMoved = false;
POINT g_gearDragStart = {};
POINT g_gearPos = {};

HCURSOR BuildCursorFromHost(int* outId)
{
    int visible = 1, id = 0, w = 0, h = 0, hx = 0, hy = 0;
    MS_CursorInfo(&visible, &id, &w, &h, &hx, &hy);
    if (outId) { *outId = id; }
    if (!visible || w <= 0 || h <= 0) { return nullptr; }

    int shapeId = 0;
    int need = MS_CursorShape(nullptr, 0, &shapeId);
    if (need <= 0) { return nullptr; }

    // 关键：w/h 来自 MS_CursorInfo，need 来自 MS_CursorShape，是两次独立加锁取到的快照。
    // 主机在这中间换了光标形状时两者会对不上，下面的 memcpy 就会按旧的 w*h*4 去读更小的 buffer
    // —— 越界读，正好发生在"打开菜单/切换焦点"这种光标形状集中更新的时刻。
    // 对不上就这一帧不换光标（下次 WM_SETCURSOR 再来），别拿越界的数据去建图标。
    if (need != w * h * 4)
    {
        Log("光标快照不一致：info %dx%d（%d 字节）vs shape %d 字节，跳过本次",
            w, h, w * h * 4, need);
        return nullptr;
    }

    std::vector<unsigned char> px((size_t)need);
    if (MS_CursorShape(px.data(), need, &shapeId) != need)
    {
        return nullptr;
    }

    BITMAPV5HEADER bi = {};
    bi.bV5Size = sizeof(bi);
    bi.bV5Width = w;
    bi.bV5Height = -h;   // 自上而下
    bi.bV5Planes = 1;
    bi.bV5BitCount = 32;
    bi.bV5Compression = BI_BITFIELDS;
    bi.bV5RedMask = 0x00FF0000;
    bi.bV5GreenMask = 0x0000FF00;
    bi.bV5BlueMask = 0x000000FF;
    bi.bV5AlphaMask = 0xFF000000;

    void* bits = nullptr;
    HDC screen = GetDC(nullptr);
    HBITMAP color = CreateDIBSection(screen, (BITMAPINFO*)&bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    ReleaseDC(nullptr, screen);
    if (!color || !bits) { if (color) { DeleteObject(color); } return nullptr; }
    memcpy(bits, px.data(), (size_t)(w * h * 4));

    const int maskStride = ((w + 31) / 32) * 4;
    std::vector<unsigned char> maskBits((size_t)(maskStride * h), 0);
    HBITMAP mask = CreateBitmap(w, h, 1, 1, maskBits.data());

    ICONINFO ii = {};
    ii.fIcon = FALSE;
    ii.xHotspot = (DWORD)hx;
    ii.yHotspot = (DWORD)hy;
    ii.hbmMask = mask;
    ii.hbmColor = color;
    HCURSOR cur = CreateIconIndirect(&ii);

    DeleteObject(color);
    DeleteObject(mask);
    return cur;
}

// mlsession 里的触摸导出（多点触控）
// 触摸诊断（写 decoder.log，和 streamwin 的 Diag 同一个文件）
static void SLogTouch(const char* fmt, ...)
{
    static char path[MAX_PATH] = {};
    if (path[0] == 0)
    {
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        char* slash = strrchr(path, '\\');
        if (slash) { *(slash + 1) = 0; }
        strcat_s(path, "decoder.log");
    }

    char line[512] = {};
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof(line), fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") == 0 && f)
    {
        fprintf(f, "%02d:%02d:%02d.%03d [touch] %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

// 判断某个鼠标消息是不是"触摸模拟"出来的（Windows 会给这类消息打 0xFF5157xx 的签名）。
// 我们已经在 WM_TOUCH 里把触摸发给主机了，再发一次鼠标就会变成单击两次。
static bool IsTouchSimulatedMouse()
{
    const ULONG_PTR info = GetMessageExtraInfo();
    return (info & 0xFFFFFF00u) == 0xFF515700u;
}

extern "C" int MS_TouchSupported(void);
extern "C" int MS_SendTouch(int type, unsigned int pointerId, float x, float y);

LRESULT CALLBACK HostWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    // 触摸屏会同时产生 WM_TOUCH 和"模拟鼠标消息"。主机支持触摸时，
    // 触摸已经单独发过，这里把鼠标复制品丢掉，否则单击会触发两次。
    // 只过滤"触摸模拟出来的鼠标消息"：WM_TOUCH 绝不能被丢，否则多点触摸只剩单点。
    const bool isMouseMsg = msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
                            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP ||
                            msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP ||
                            msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP;
    if (isMouseMsg && IsTouchSimulatedMouse() && MS_TouchSupported())
    {
        return 0;
    }
    switch (msg)
    {
    case WM_SETCURSOR:
    {
        // 先看形状有没有变，再决定要不要重建。
        // BuildCursorFromHost 每次都会 CreateIconIndirect 出一个新 HCURSOR；
        // 旧代码在 id 没变时把新建的那个直接丢掉（既不复用也不 DestroyCursor）——
        // 鼠标一动就漏一个 USER 句柄，漏到进程上限后 SetCursor / CreateIconIndirect
        // 全部失败，整个窗口就跟着卡死了。
        int visible = 1, id = -1, w = 0, h = 0, hx = 0, hy = 0;
        MS_CursorInfo(&visible, &id, &w, &h, &hx, &hy);

        if (visible && id != g_host.cursorId)
        {
            int built = -1;
            HCURSOR cur = BuildCursorFromHost(&built);
            if (cur)
            {
                if (g_host.cursor) { DestroyCursor(g_host.cursor); }
                g_host.cursor = cur;
                g_host.cursorId = built;
            }
        }

        if (!g_host.cursor)
        {
            return FALSE;   // 主机没有可用形状：交给系统，别 SetCursor(nullptr) 造成闪烁
        }

        if (g_host.cursor != g_host.lastCursor)
        {
            SetCursor(g_host.cursor);
            g_host.lastCursor = g_host.cursor;
        }
        return TRUE;
    }
    case WM_TOUCH:
    {
        // 主机不支持触摸就别吞消息：让 Windows 把它当鼠标，触屏至少还能点。
        // （仍然解析一遍并记日志，方便排查坐标问题；只是不发送）
        const bool hostTouch = MS_TouchSupported() != 0;
        if (!hostTouch)
        {
            static std::atomic<int> noTouchTick{ 0 };
            if ((++noTouchTick % 20) == 0) { SLogTouch("主机不支持触摸，本次忽略（已收到 WM_TOUCH）"); }
        }

        const UINT rawCount = LOWORD(lp);
        TOUCHINPUT inputs[10] = {};
        const UINT count = rawCount < 10 ? rawCount : 10;
        if (GetTouchInputInfo((HTOUCHINPUT)lp, count, inputs, sizeof(TOUCHINPUT)))
        {
            SLogTouch("WM_TOUCH count=%u rawCount=%u hwnd=%p", count, rawCount, (void*)hwnd);
            RECT rc = {};
            GetClientRect(hwnd, &rc);
            const float cw = (float)(rc.right > 0 ? rc.right : 1);
            const float ch = (float)(rc.bottom > 0 ? rc.bottom : 1);

            POINT touchOrigin = { 0, 0 };
            ClientToScreen(hwnd, &touchOrigin);

            // 每个触点缓存最后位置：部分驱动/合成器在 UP 事件里给的坐标是 0,0，
            // 直接用会让主机把触点"甩"到左上角（表现为拖动末端位置漂移）。
            static float lastX[64] = {};
            static float lastY[64] = {};
            static bool lastValid[64] = {};
            for (UINT i = 0; i < count; i++)
            {
                const TOUCHINPUT& t = inputs[i];
                const int rawPx = (int)(t.x / 100);   // TOUCHINPUT 是 1/100 像素
                const int rawPy = (int)(t.y / 100);

                // 一般是"相对窗口客户区"的坐标；但部分合成器/注入路径给的是屏幕坐标。
                // 判别：减去窗口原点后仍落在客户区内 → 按屏幕坐标处理（否则会越往右下偏得越多）。
                int px = rawPx;
                int py = rawPy;
                int screenCoords = 0;
                if (rawPx >= (int)touchOrigin.x && rawPy >= (int)touchOrigin.y &&
                    rawPx - (int)touchOrigin.x <= (int)cw && rawPy - (int)touchOrigin.y <= (int)ch)
                {
                    px = rawPx - (int)touchOrigin.x;
                    py = rawPy - (int)touchOrigin.y;
                    screenCoords = 1;
                }

                float x = (float)px / cw;
                float y = (float)py / ch;

                SLogTouch("  pt id=%u flags=0x%x raw=%d,%d primary=%d palm=%d",
                         (unsigned)t.dwID, (unsigned)t.dwFlags, rawPx, rawPy,
                         (t.dwFlags & TOUCHEVENTF_PRIMARY) ? 1 : 0, (t.dwFlags & TOUCHEVENTF_PALM) ? 1 : 0);
                const unsigned slot = (unsigned)t.dwID & 63u;

                // 1=DOWN 2=UP 3=MOVE
                int type = 3;
                if (t.dwFlags & TOUCHEVENTF_DOWN) { type = 1; }
                else if (t.dwFlags & TOUCHEVENTF_UP) { type = 2; }

                if (type == 2 && lastValid[slot] && t.x == 0 && t.y == 0)
                {
                    x = lastX[slot];   // UP 没带有效坐标：沿用最后一次位置，避免跳变
                    y = lastY[slot];
                }
                else if (type != 2)
                {
                    lastX[slot] = x;
                    lastY[slot] = y;
                    lastValid[slot] = true;
                }

                if (type == 2)
                {
                    lastValid[slot] = false;
                }

                if (hostTouch) { MS_SendTouch(type, (unsigned)t.dwID, x, y); }

                static std::atomic<int> touchLogTick{ 0 };
                if (type == 1 || (++touchLogTick % 10) == 0)
                {
                    SLogTouch("touch type=%d id=%u raw=%d,%d client=%dx%d origin=%d,%d screen=%d norm=%.4f,%.4f",
                             type, (unsigned)t.dwID, rawPx, rawPy, (int)cw, (int)ch,
                             (int)touchOrigin.x, (int)touchOrigin.y, screenCoords, x, y);
                }
            }

            CloseTouchInputHandle((HTOUCHINPUT)lp);
            return 0;
        }
        break;
    }

    case WM_MOUSEMOVE:
        if (g_pointerLocked)
        {
            // 游戏内锁定：发相对位移（视频像素），绝对坐标一律不发。
            // 光标被 C# 侧夹在画面里并周期性钉回中心 —— 那种瞬移要丢掉，只更新锚点。
            const int cx = GET_X_LPARAM(lp);
            const int cy = GET_Y_LPARAM(lp);

            if (g_hasLastMouse)
            {
                const int dx = cx - g_lastMouse.x;
                const int dy = cy - g_lastMouse.y;

                if (abs(dx) <= 300 && abs(dy) <= 300 && (dx != 0 || dy != 0))
                {
                    RECT rc = {};
                    GetClientRect(hwnd, &rc);
                    const int cw = rc.right > 0 ? rc.right : 1;
                    const int ch = rc.bottom > 0 ? rc.bottom : 1;
                    const int vw = g.w > 0 ? g.w : g_host.w;
                    const int vh = g.h > 0 ? g.h : g_host.h;

                    const int hx = dx * vw / cw;
                    const int hy = dy * vh / ch;
                    if (hx != 0 || hy != 0)
                    {
                        MS_SendMouseMove(hx, hy);
                    }
                }
            }

            g_lastMouse.x = cx;
            g_lastMouse.y = cy;
            g_hasLastMouse = true;
            return 0;
        }

        if (g_gearDragging)
        {
            const int dx = GET_X_LPARAM(lp) - g_gearDragStart.x;
            const int dy = GET_Y_LPARAM(lp) - g_gearDragStart.y;
            if (abs(dx) + abs(dy) > 4) { g_gearMoved = true; }

            int nx = g_gearPos.x + dx;
            int ny = g_gearPos.y + dy;
            nx = max(0, min(nx, g_host.w - 44));
            ny = max(0, min(ny, g_host.h - 44));
            HudSetGear(nx, ny, 1);
            return 0;
        }
        // 鼠标离开窗口时把按下的键全部补发抬起
        {
            TRACKMOUSEEVENT tme = {};
            tme.cbSize = sizeof(TRACKMOUSEEVENT);
            tme.dwFlags = TME_LEAVE;
            tme.hwndTrack = hwnd;
            TrackMouseEvent(&tme);
        }

        if (GearNear(GET_X_LPARAM(lp), GET_Y_LPARAM(lp), g_host.w, g_host.h))
        {
            g_gearLastHover = GetTickCount64();
            GearShow(true);
        }

        MS_SendMousePosition(GET_X_LPARAM(lp), GET_Y_LPARAM(lp), g.w > 0 ? g.w : g_host.w, g.h > 0 ? g.h : g_host.h);
        return 0;
    case WM_LBUTTONDOWN:
        if (HudHitGear(GET_X_LPARAM(lp), GET_Y_LPARAM(lp)))
        {
            g_gearDragging = true;
            g_gearMoved = false;
            g_gearDragStart.x = GET_X_LPARAM(lp);
            g_gearDragStart.y = GET_Y_LPARAM(lp);
            g_gearPos = g_gearDragStart;
            SetCapture(hwnd);
            return 0;
        }
        MS_SendMouseButton(1, 1);
        g_leftDown = true;
        return 0;

    case WM_LBUTTONUP:
        MS_SendMouseButton(1, 0);
        g_leftDown = false;
        g_lastBtnUpTick = GetTickCount64();
        return 0;

    case WM_RBUTTONDOWN:
        MS_SendMouseButton(3, 1);
        g_rightDown = true;
        return 0;

    case WM_RBUTTONUP:
        MS_SendMouseButton(3, 0);
        g_rightDown = false;
        g_lastBtnUpTick = GetTickCount64();
        return 0;

    case WM_MBUTTONDOWN:
        MS_SendMouseButton(2, 1);
        g_midDown = true;
        return 0;

    case WM_MBUTTONUP:
        MS_SendMouseButton(2, 0);
        g_midDown = false;
        g_lastBtnUpTick = GetTickCount64();
        return 0;

    case WM_MOUSEWHEEL:  MS_SendMouseScroll(GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA); return 0;
    case WM_MOUSEHWHEEL: MS_SendMouseHScroll(-GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA); return 0;
    case WM_MOUSELEAVE:
        ReleaseAllButtons();
        g_host.lastCursor = nullptr;   // 离开画面后别的窗口会改光标，回来必须重新设置
        return 0;

    case WM_CAPTURECHANGED:
    case WM_KILLFOCUS:
        ReleaseAllButtons();
        break;

    case WM_TIMER:
        if (wp == 1 && !g_gearDragging && g_gearShown &&
            GetTickCount64() - g_gearLastHover > 1500)
        {
            GearShow(false);
        }

        // 兜底：物理键已松开但主机仍认为按着（消息丢失/焦点切换）→ 补发抬起
        if (wp == 1)
        {
            const bool l = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            const bool r = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
            const bool m = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

            if (g_leftDown && !l) { MS_SendMouseButton(1, 0); g_leftDown = false; }
            if (g_rightDown && !r) { MS_SendMouseButton(3, 0); g_rightDown = false; }
            if (g_midDown && !m) { MS_SendMouseButton(2, 0); g_midDown = false; }
        }
        return 0;

    case WM_ERASEBKGND:  return 1;
    default: break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

bool CreateHostSwapChain()
{
    IDXGIDevice* dxgiDev = nullptr;
    if (FAILED(g_host.dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDev)))
    {
        return false;
    }

    IDXGIAdapter* adapter = nullptr;
    dxgiDev->GetAdapter(&adapter);
    IDXGIFactory2* factory = nullptr;
    bool ok = false;
    if (adapter && SUCCEEDED(adapter->GetParent(__uuidof(IDXGIFactory2), (void**)&factory)))
    {
        DXGI_SWAP_CHAIN_DESC1 desc = {};
        desc.Width = (UINT)g_host.w;
        desc.Height = (UINT)g_host.h;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

        ok = SUCCEEDED(factory->CreateSwapChainForHwnd(
            g_host.dev, g_host.child, &desc, nullptr, nullptr, &g_host.swap));
        factory->Release();
    }

    if (adapter) { adapter->Release(); }
    dxgiDev->Release();
    return ok;
}

}  // namespace

extern "C" __declspec(dllexport) int ML_HostChild(void* parent, int x, int y, int w, int h)
{
    if (!parent || w < 2 || h < 2)
    {
        return -1;
    }

    if (g_host.child)
    {
        return 0;
    }

    static bool classRegistered = false;
    if (!classRegistered)
    {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = HostWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = nullptr;           // 光标完全由 WM_SETCURSOR 决定
        wc.hbrBackground = nullptr;
        wc.lpszClassName = L"MoonlightChildVideo";
        RegisterClassExW(&wc);
        classRegistered = true;
    }

    g_host.parent = (HWND)parent;
    g_host.x = x; g_host.y = y; g_host.w = w; g_host.h = h;

    // 顶层弹窗 + owner 关系：一定在主窗口之上，又不会抢焦点/进任务栏
    g_host.child = CreateWindowExW(
        WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
        L"MoonlightChildVideo", L"",
        WS_POPUP,
        x, y, w, h, g_host.parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (g_host.child)
    {
        ShowWindow(g_host.child, SW_SHOWNOACTIVATE);
        UpdateWindow(g_host.child);
        SetTimer(g_host.child, 1, 200, nullptr);
    }
    if (!g_host.child)
    {
        return -2;
    }

    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                 levels, 2, D3D11_SDK_VERSION,
                                 &g_host.dev, nullptr, &g_host.ctx)))
    {
        return -3;
    }

    if (!CreateHostSwapChain())
    {
        return -4;
    }

    HudInit(g_host.dev);
    HudSetOwner(g_host.child);

    if (ML_Init(g_host.dev, g_host.ctx, g_host.swap, w, h) != 0)
    {
        return -5;
    }

    return 0;
}

extern "C" __declspec(dllexport) void ML_MoveChild(int x, int y, int w, int h)
{
    if (!g_host.child || w < 2 || h < 2)
    {
        return;
    }

    const int oldW = g_host.w;
    const int oldH = g_host.h;

    g_host.x = x; g_host.y = y; g_host.w = w; g_host.h = h;
    SetWindowPos(g_host.child, nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

    // 尺寸变了要重建 render target（否则 rtv 指向旧的 back buffer，画面全黑）
    if (w != oldW || h != oldH)
    {
        ML_Resize(w, h);
    }
}

extern "C" __declspec(dllexport) void ML_ShowChild(int visible)
{
    if (g_host.child)
    {
        ShowWindow(g_host.child, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }
}

extern "C" __declspec(dllexport) void ML_DetachChild(void)
{
    if (g_host.child)
    {
        DestroyWindow(g_host.child);
        g_host.child = nullptr;
    }

    if (g_host.cursor) { DestroyCursor(g_host.cursor); g_host.cursor = nullptr; }
    if (g_host.swap) { g_host.swap->Release(); g_host.swap = nullptr; }
    if (g_host.ctx) { g_host.ctx->Release(); g_host.ctx = nullptr; }
    if (g_host.dev) { g_host.dev->Release(); g_host.dev = nullptr; }
    g_host.cursorId = -1;
}

// ---------------- 齿轮弹出菜单（Win32 TrackPopupMenu，天然在视频弹窗之上） ----------------
extern "C" __declspec(dllexport) int ML_ShowGearMenu(int* outWidth, int* outHeight, int* outKbps, int* outFullScreenToggle)
{
    static const struct { const wchar_t* label; int w; int h; } resolutions[] = {
        { L"1280 × 720",  1280, 720 },
        { L"1600 × 900",  1600, 900 },
        { L"1920 × 1080", 1920, 1080 },
        { L"2560 × 1440", 2560, 1440 },
        { L"3840 × 2160", 3840, 2160 },
    };

    static const int bitrates[] = { 10000, 20000, 30000, 50000, 80000 };

    if (outWidth) { *outWidth = 0; }
    if (outHeight) { *outHeight = 0; }
    if (outKbps) { *outKbps = 0; }
    if (outFullScreenToggle) { *outFullScreenToggle = 0; }

    HMENU root = CreatePopupMenu();

    HMENU resMenu = CreatePopupMenu();
    for (int i = 0; i < (int)(sizeof(resolutions) / sizeof(resolutions[0])); i++)
    {
        AppendMenuW(resMenu, MF_STRING, 100 + i, resolutions[i].label);
    }
    AppendMenuW(root, MF_POPUP, (UINT_PTR)resMenu, L"分辨率");

    HMENU brMenu = CreatePopupMenu();
    for (int i = 0; i < (int)(sizeof(bitrates) / sizeof(bitrates[0])); i++)
    {
        wchar_t label[64];
        swprintf_s(label, L"%d Mbps", bitrates[i] / 1000);
        AppendMenuW(brMenu, MF_STRING, 200 + i, label);
    }
    AppendMenuW(root, MF_POPUP, (UINT_PTR)brMenu, L"码率");

    AppendMenuW(root, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(root, MF_STRING, 300, L"切换全屏 / 窗口");

    HWND menuOwner = GetAncestor(g_host.child, GA_ROOT);
    if (!menuOwner) { menuOwner = g_host.child; }
    SetForegroundWindow(menuOwner);

    POINT pt = {};
    GetCursorPos(&pt);

    int cmd = (int)TrackPopupMenu(root, TPM_RETURNCMD | TPM_NONOTIFY | TPM_LEFTALIGN | TPM_TOPALIGN,
                                  pt.x, pt.y, 0, menuOwner, nullptr);

    DestroyMenu(root);

    if (cmd >= 100 && cmd < 200)
    {
        const int i = cmd - 100;
        if (outWidth) { *outWidth = resolutions[i].w; }
        if (outHeight) { *outHeight = resolutions[i].h; }
        return 1;
    }

    if (cmd >= 200 && cmd < 300)
    {
        if (outKbps) { *outKbps = bitrates[cmd - 200]; }
        return 2;
    }

    if (cmd == 300)
    {
        if (outFullScreenToggle) { *outFullScreenToggle = 1; }
        return 3;
    }

    return 0;
}

// ---------------- Win32 版串流窗口用的子窗口（WS_CHILD，父窗口是纯 Win32 窗口） ----------------
extern "C" __declspec(dllexport) int ML_HostChildWin32(void* parent, int w, int h)
{
    if (!parent || w < 2 || h < 2)
    {
        return -1;
    }

    if (g_host.child)
    {
        return 0;
    }

    static bool classRegistered2 = false;
    if (!classRegistered2)
    {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = HostWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = nullptr;
        wc.hbrBackground = nullptr;
        wc.lpszClassName = L"MoonlightChildVideoWin32";
        RegisterClassExW(&wc);
        classRegistered2 = true;
    }

    g_host.parent = (HWND)parent;
    g_host.x = 0; g_host.y = 0; g_host.w = w; g_host.h = h;

    g_host.child = CreateWindowExW(
        0,
        L"MoonlightChildVideoWin32", L"",
        WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
        0, 0, w, h, g_host.parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!g_host.child)
    {
        return -2;
    }

    ShowWindow(g_host.child, SW_SHOW);
    // TWF_FINETOUCH（不是 TWF_WANTPALM）：不做手掌拒绝，否则第二根手指会被当手掌剔掉，多点只剩单点
    BOOL touchOk = RegisterTouchWindow(g_host.child, TWF_FINETOUCH);

    // 关掉本机的触摸视觉反馈（触点圆点、长按圆环、点击方框）：
    // 否则手指按住时本地画一个、远程光标再画一个，看起来就是"两个指示标"。
    {
        BOOL off = FALSE;
        const FEEDBACK_TYPE kFeedback[] = {
            FEEDBACK_TOUCH_CONTACTVISUALIZATION,
            FEEDBACK_TOUCH_TAP,
            FEEDBACK_TOUCH_DOUBLETAP,
            FEEDBACK_TOUCH_PRESSANDHOLD,
            FEEDBACK_TOUCH_RIGHTTAP,
            FEEDBACK_GESTURE_PRESSANDTAP,
        };
        for (auto type : kFeedback)
        {
            SetWindowFeedbackSetting(g_host.child, type, 0, sizeof(off), &off);
        }
    }
    SLogTouch("RegisterTouchWindow=%d IsTouchWindow=%d hwnd=%p", (int)touchOk, (int)IsTouchWindow(g_host.child, nullptr), (void*)g_host.child);
    SetTimer(g_host.child, 1, 200, nullptr);

    // 键盘：低级钩子（能拿到 Win 键的按下与抬起，并吞掉本地系统动作）
    g_kbRoot = GetAncestor((HWND)parent, GA_ROOT);
    g_kbSwallow = true;
    EnsureKeyboardHook();

    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                 levels, 2, D3D11_SDK_VERSION,
                                 &g_host.dev, nullptr, &g_host.ctx)))
    {
        return -3;
    }

    if (!CreateHostSwapChain())
    {
        return -4;
    }

    HudInit(g_host.dev);
    HudSetOwner(g_host.child);

    if (ML_Init(g_host.dev, g_host.ctx, g_host.swap, w, h) != 0)
    {
        return -5;
    }

    return 0;
}
// 让外层窗口也能用同一套主机光标（避免鼠标移到标题栏/边框时指针跳回系统箭头）
extern "C" __declspec(dllexport) void ML_SetPointerLock(int locked)
{
    const bool on = locked != 0;
    if (on == g_pointerLocked)
    {
        return;
    }

    g_pointerLocked = on;
    g_hasLastMouse = false;   // 切换后第一帧只当锚点
}

extern "C" __declspec(dllexport) int ML_ApplyCursor(void)
{
    // 同上：形状没变就别重建，否则每次调用都漏一个 HCURSOR
    int visible = 1, id = -1, w = 0, h = 0, hx = 0, hy = 0;
    MS_CursorInfo(&visible, &id, &w, &h, &hx, &hy);

    if (visible && id != g_host.cursorId)
    {
        int built = -1;
        HCURSOR cur = BuildCursorFromHost(&built);
        if (cur)
        {
            if (g_host.cursor) { DestroyCursor(g_host.cursor); }
            g_host.cursor = cur;
            g_host.cursorId = built;
        }
    }

    SetCursor(g_host.cursor);
    return g_host.cursor ? 1 : 0;
}
extern "C" __declspec(dllexport) void ML_SetGearEnabled(int enabled)
{
    g_gearEnabled = enabled != 0;
    if (!g_gearEnabled)
    {
        HudSetGearVisible(0);
    }
}
extern "C" __declspec(dllexport) int ML_GetVideoInfo(int* outCodec, int* outHardware)
{
    if (outCodec)
    {
        // g.codecId 是 AVCodecID（H264=27 / HEVC=173 / AV1=226），归一化成 1/2/3
        switch (g.codecId)
        {
        case AV_CODEC_ID_H264: *outCodec = 1; break;
        case AV_CODEC_ID_HEVC: *outCodec = 2; break;
        case AV_CODEC_ID_AV1:  *outCodec = 3; break;
        default:               *outCodec = 0; break;
        }
    }

    if (outHardware)
    {
        *outHardware = g.software ? 0 : 1;
    }

    return 0;
}
