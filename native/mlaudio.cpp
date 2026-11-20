// mlaudio.cpp —— 音频：Opus 多流解码 → WASAPI 共享模式播放
//
// 数据流：
//   MLC 音频线程 → decodeAndPlaySample(opus 包) → MA_Push
//     → opus_multistream_decode_float → 环形缓冲（float 交织）
//     → 音频线程每 3ms 取一截写进 IAudioRenderClient
//
// 取舍：
//   · 只处理 48kHz 立体声（sc.audioConfiguration = AUDIO_CONFIGURATION_STEREO）。
//     采样率/声道和设备不一致时交给 WASAPI 的 AUTOCONVERTPCM 去转换。
//   · 没有输出设备（或初始化失败）时只记日志、静音跑，绝不让视频连不上。
//   · 环形缓冲 250ms；满了丢最旧的，保证延迟不无限涨。
//
// 导出（mlsession.cpp 的音频回调调用）：
//   int  MA_Init(int audioConfiguration, const OPUS_MULTISTREAM_CONFIGURATION* cfg);
//   void MA_Start(void);
//   void MA_Stop(void);
//   void MA_Shutdown(void);
//   void MA_Push(const void* opusData, int length);
//   void MA_Stats(int* sampleRate, int* channels, long long* playedFrames, long long* underruns);

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propsys.h>

#include "opus.h"
#include "opus_multistream.h"
#include "Limelight.h"

#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

namespace {

// WAVEFORMATEXTENSIBLE 的 32-bit float 子格式（KSDATAFORMAT_SUBTYPE_IEEE_FLOAT），
// 自己写一份，省得为了 ksmedia.h 的 GUID 去链 ksguid.lib
const GUID kFloatSubType =
    {0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}};

constexpr DWORD kSpeakerFrontLeft = 0x1;
constexpr DWORD kSpeakerFrontRight = 0x2;

constexpr int kWasmBufferHns = 200000;     // 20ms
constexpr size_t kRingMs = 250;            // 环形缓冲时长
constexpr int kMaxOpusFrames = 5760;       // Opus 单包最长 120ms @48k

// ---------------------------------------------------------------- 日志

void ALog(const char* fmt, ...)
{
    static char path[MAX_PATH] = {};
    if (path[0] == 0)
    {
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        char* slash = strrchr(path, '\\');
        if (slash) { *(slash + 1) = 0; }
        strcat_s(path, "decoder.log");
    }

    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof(line), fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);

    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") == 0 && f)
    {
        fprintf(f, "%02d:%02d:%02d.%03d [audio] %s\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

// ---------------------------------------------------------------- 状态

struct Audio
{
    // 解码（MLC 音频线程）
    std::mutex decLock;
    OpusMSDecoder* decoder = nullptr;
    int sampleRate = 48000;
    int channels = 2;

    // 环形缓冲（帧为单位，float 交织）
    std::mutex ringLock;
    std::vector<float> ring;
    size_t ringFrames = 0;
    size_t readPos = 0;
    size_t writePos = 0;
    size_t count = 0;
    std::atomic<long long> droppedFrames{0};
    std::atomic<long long> decodeErrors{0};

    // WASAPI
    IMMDeviceEnumerator* enumr = nullptr;
    IAudioClient* client = nullptr;
    IAudioRenderClient* render = nullptr;
    UINT32 bufferFrames = 0;
    bool deviceOk = false;

    // 播放线程
    std::thread thread;
    std::atomic<bool> stop{false};
    std::atomic<bool> running{false};
    std::atomic<long long> playedFrames{0};
    std::atomic<long long> underruns{0};
};

Audio g;
bool g_comInit = false;

// ---------------------------------------------------------------- 环形缓冲

// 从环形缓冲取最多 frames 帧到 dst，返回实际取到的帧数
size_t RingPop(float* dst, size_t frames)
{
    std::lock_guard<std::mutex> lock(g.ringLock);

    const size_t n = frames < g.count ? frames : g.count;
    size_t left = n;

    while (left > 0)
    {
        const size_t chunk = (g.ringFrames - g.readPos) < left ? (g.ringFrames - g.readPos) : left;
        memcpy(dst, &g.ring[g.readPos * g.channels], chunk * g.channels * sizeof(float));
        dst += chunk * g.channels;
        g.readPos = (g.readPos + chunk) % g.ringFrames;
        left -= chunk;
    }

    g.count -= n;
    return n;
}

// 写入环形缓冲；满了先丢最旧的
void RingPush(const float* src, size_t frames)
{
    std::lock_guard<std::mutex> lock(g.ringLock);
    if (g.ringFrames == 0 || frames == 0)
    {
        return;
    }

    size_t freeFrames = g.ringFrames - g.count;
    if (frames > freeFrames)
    {
        size_t drop = frames - freeFrames;
        if (drop > g.count) { drop = g.count; }
        g.readPos = (g.readPos + drop) % g.ringFrames;
        g.count -= drop;
        g.droppedFrames += (long long)drop;
        freeFrames += drop;
    }

    size_t n = frames < freeFrames ? frames : freeFrames;
    g.count += n;

    size_t left = n;
    while (left > 0)
    {
        const size_t chunk = (g.ringFrames - g.writePos) < left ? (g.ringFrames - g.writePos) : left;
        memcpy(&g.ring[g.writePos * g.channels], src, chunk * g.channels * sizeof(float));
        src += chunk * g.channels;
        g.writePos = (g.writePos + chunk) % g.ringFrames;
        left -= chunk;
    }
}

// ---------------------------------------------------------------- WASAPI

void DestroyDevice()
{
    g.deviceOk = false;

    if (g.render)
    {
        g.render->Release();
        g.render = nullptr;
    }
    if (g.client)
    {
        g.client->Stop();
        g.client->Release();
        g.client = nullptr;
    }
    if (g.enumr)
    {
        g.enumr->Release();
        g.enumr = nullptr;
    }
    g.bufferFrames = 0;
}

bool CreateDevice()
{
    DestroyDevice();

    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  IID_PPV_ARGS(&g.enumr));
    if (FAILED(hr) || !g.enumr)
    {
        ALog("CoCreateInstance(MMDeviceEnumerator) 失败：0x%08X", (unsigned)hr);
        DestroyDevice();
        return false;
    }

    IMMDevice* device = nullptr;
    hr = g.enumr->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (FAILED(hr) || !device)
    {
        ALog("找不到默认音频输出设备：0x%08X", (unsigned)hr);
        DestroyDevice();
        return false;
    }

    // 把设备名记下来，用户才知道声音去哪儿了
    {
        IPropertyStore* props = nullptr;
        if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &props)) && props)
        {
            PROPVARIANT value = {};
            if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR)
            {
                char name[256] = {};
                WideCharToMultiByte(CP_UTF8, 0, value.pwszVal, -1, name, sizeof(name) - 1, nullptr, nullptr);
                ALog("输出设备：%s", name);
            }
            PropVariantClear(&value);
            props->Release();
        }
    }

    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&g.client);
    device->Release();
    if (FAILED(hr) || !g.client)
    {
        ALog("Activate(IAudioClient) 失败：0x%08X", (unsigned)hr);
        DestroyDevice();
        return false;
    }

    // 我们固定给 48k / 2ch / float32，剩下的交给 WASAPI 自动转换
    WAVEFORMATEXTENSIBLE wf = {};
    wf.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    wf.Format.nChannels = (WORD)g.channels;
    wf.Format.nSamplesPerSec = (DWORD)g.sampleRate;
    wf.Format.wBitsPerSample = 32;
    wf.Format.nBlockAlign = (WORD)(wf.Format.nChannels * sizeof(float));
    wf.Format.nAvgBytesPerSec = wf.Format.nSamplesPerSec * wf.Format.nBlockAlign;
    wf.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    wf.Samples.wValidBitsPerSample = 32;
    wf.dwChannelMask = g.channels == 2 ? (kSpeakerFrontLeft | kSpeakerFrontRight) : kSpeakerFrontLeft;
    wf.SubFormat = kFloatSubType;

    hr = g.client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                              AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                              kWasmBufferHns, 0, &wf.Format, nullptr);
    if (FAILED(hr))
    {
        ALog("IAudioClient::Initialize 失败：0x%08X（%d Hz %d ch）", (unsigned)hr, g.sampleRate, g.channels);
        DestroyDevice();
        return false;
    }

    hr = g.client->GetBufferSize(&g.bufferFrames);
    if (FAILED(hr))
    {
        ALog("GetBufferSize 失败：0x%08X", (unsigned)hr);
        DestroyDevice();
        return false;
    }

    hr = g.client->GetService(IID_PPV_ARGS(&g.render));
    if (FAILED(hr) || !g.render)
    {
        ALog("GetService(IAudioRenderClient) 失败：0x%08X", (unsigned)hr);
        DestroyDevice();
        return false;
    }

    g.deviceOk = true;
    ALog("音频设备就绪：%d Hz %d ch，WASAPI 缓冲 %u 帧（%.0f ms）",
         g.sampleRate, g.channels, g.bufferFrames,
         g.bufferFrames * 1000.0 / g.sampleRate);
    return true;
}

// ---------------------------------------------------------------- 播放线程

void AudioThread()
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    const size_t frameBytes = sizeof(float) * (size_t)g.channels;
    bool hadData = false;

    while (!g.stop)
    {
        if (!g.client || !g.render)
        {
            break;
        }

        UINT32 padding = 0;
        if (FAILED(g.client->GetCurrentPadding(&padding)))
        {
            // 设备被拔掉/切换了：重建一次，失败就退出
            ALog("音频设备失效，尝试重建");
            if (!CreateDevice() || FAILED(g.client->Start()))
            {
                g.deviceOk = false;
                break;
            }
            continue;
        }

        const UINT32 avail = g.bufferFrames > padding ? g.bufferFrames - padding : 0;
        if (avail == 0)
        {
            Sleep(3);
            continue;
        }

        BYTE* buffer = nullptr;
        if (FAILED(g.render->GetBuffer(avail, &buffer)) || !buffer)
        {
            Sleep(5);
            continue;
        }

        const size_t got = RingPop((float*)buffer, avail);
        if (got < avail)
        {
            // 数据不够就补静音，别让引擎爆音
            memset(buffer + got * frameBytes, 0, (avail - got) * frameBytes);
            if (got == 0 && hadData)
            {
                g.underruns++;
            }
        }
        hadData = got > 0;

        g.render->ReleaseBuffer(avail, 0);
        g.playedFrames += (long long)avail;

        Sleep(3);
    }

    CoUninitialize();
}

// ---------------------------------------------------------------- 导出

} // namespace

extern "C" {

// 前置声明（MA_Init 里要先清干净上一次的会话）
__declspec(dllexport) void MA_Stop(void);
__declspec(dllexport) void MA_Shutdown(void);

__declspec(dllexport) int MA_Init(int audioConfiguration, const OPUS_MULTISTREAM_CONFIGURATION* cfg)
{
    MA_Shutdown();

    if (!cfg)
    {
        ALog("MA_Init：没有 Opus 配置");
        return 0;
    }

    g.sampleRate = cfg->sampleRate > 0 ? cfg->sampleRate : 48000;
    g.channels = cfg->channelCount > 0 ? cfg->channelCount : 2;

    ALog("Opus 配置：config=0x%X %d Hz %d ch，streams=%d coupled=%d samplesPerFrame=%d",
         audioConfiguration, g.sampleRate, g.channels,
         cfg->streams, cfg->coupledStreams, cfg->samplesPerFrame);

    // 现在只协商立体声；多声道要按 mapping 重排，先拒绝但不禁用视频
    if (g.channels < 1 || g.channels > 2)
    {
        ALog("暂不支持 %d 声道，本次静音", g.channels);
        return 0;
    }

    int err = OPUS_OK;
    {
        std::lock_guard<std::mutex> lock(g.decLock);
        g.decoder = opus_multistream_decoder_create(g.sampleRate, g.channels,
                                                    cfg->streams, cfg->coupledStreams,
                                                    cfg->mapping, &err);
        if (err != OPUS_OK || !g.decoder)
        {
            ALog("opus_multistream_decoder_create 失败：%s", opus_strerror(err));
            g.decoder = nullptr;
            return 0;
        }
    }

    {
        std::lock_guard<std::mutex> lock(g.ringLock);
        g.ringFrames = (size_t)g.sampleRate * kRingMs / 1000;
        g.ring.assign(g.ringFrames * (size_t)g.channels, 0.0f);
        g.readPos = g.writePos = g.count = 0;
    }

    g.droppedFrames = 0;
    g.decodeErrors = 0;
    g.playedFrames = 0;
    g.underruns = 0;

    if (!g_comInit)
    {
        // MTA：设备对象跨线程用（创建在会话线程，使用在音频线程）
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        g_comInit = true;
    }

    if (!CreateDevice())
    {
        ALog("没有可用的音频输出设备，本次只播视频");
        return 0; // 不影响视频
    }

    return 0;
}

__declspec(dllexport) void MA_Start(void)
{
    if (!g.deviceOk || !g.client)
    {
        return; // 没设备：静音
    }

    if (g.running)
    {
        return;
    }

    if (FAILED(g.client->Start()))
    {
        ALog("IAudioClient::Start 失败");
        return;
    }

    g.stop = false;
    g.thread = std::thread(AudioThread);
    g.running = true;
    ALog("音频播放开始");
}

__declspec(dllexport) void MA_Stop(void)
{
    if (!g.running && !g.thread.joinable())
    {
        return;
    }

    g.stop = true;
    if (g.thread.joinable())
    {
        g.thread.join();
    }
    if (g.client)
    {
        g.client->Stop();
    }

    g.running = false;
    ALog("音频播放停止（已播 %lld 帧，欠载 %lld 次，丢 %lld 帧，解码错误 %lld）",
         g.playedFrames.load(), g.underruns.load(),
         g.droppedFrames.load(), g.decodeErrors.load());
}

__declspec(dllexport) void MA_Shutdown(void)
{
    MA_Stop();

    {
        std::lock_guard<std::mutex> lock(g.decLock);
        if (g.decoder)
        {
            opus_multistream_decoder_destroy(g.decoder);
            g.decoder = nullptr;
        }
    }

    {
        std::lock_guard<std::mutex> lock(g.ringLock);
        g.ring.clear();
        g.ringFrames = g.readPos = g.writePos = g.count = 0;
    }

    DestroyDevice();

    if (g_comInit)
    {
        CoUninitialize();
        g_comInit = false;
    }
}

__declspec(dllexport) void MA_Push(const void* opusData, int length)
{
    if (!opusData || length <= 0 || !g.deviceOk)
    {
        return;
    }

    thread_local std::vector<float> pcm;

    std::lock_guard<std::mutex> lock(g.decLock);
    if (!g.decoder)
    {
        return;
    }

    const size_t need = (size_t)kMaxOpusFrames * (size_t)g.channels;
    if (pcm.size() < need)
    {
        pcm.resize(need);
    }

    const int frames = opus_multistream_decode_float(g.decoder,
                                                     (const unsigned char*)opusData, length,
                                                     pcm.data(), kMaxOpusFrames, 0);
    if (frames < 0)
    {
        g.decodeErrors++;
        return;
    }

    RingPush(pcm.data(), (size_t)frames);
}

__declspec(dllexport) void MA_Stats(int* sampleRate, int* channels,
                                   long long* playedFrames, long long* underruns)
{
    if (sampleRate) { *sampleRate = g.sampleRate; }
    if (channels) { *channels = g.deviceOk ? g.channels : 0; }
    if (playedFrames) { *playedFrames = g.playedFrames.load(); }
    if (underruns) { *underruns = g.underruns.load(); }
}

} // extern "C"
