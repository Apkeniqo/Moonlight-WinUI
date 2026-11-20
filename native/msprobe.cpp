// msprobe.cpp —— 控制台探针：不开 WinUI，直接验证 mlsession 的
//   /serverinfo → /launch → LiStartConnection → 视频包
//
// 用法（在 native 目录下）：
//   msprobe.exe [host] [appid] [秒数]
//   默认 127.0.0.1 1 30
//
// 产物：屏幕上的阶段变化 + decoder.log 里的详细日志。

#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

extern "C" {
__declspec(dllimport) int MS_Init(const char* certPemPath, const char* keyPemPath, const char* uniqueId);
__declspec(dllimport) int MS_Status(char* stage, int stageLen);
__declspec(dllimport) int MS_Start(const char* host, int width, int height, int fps, int bitrateKbps, int appId);
__declspec(dllimport) void MS_Stop(void);
__declspec(dllimport) void MS_Stats(long long* packets, long long* bytes, int* connected);
}

static std::string ExeDir()
{
    char path[MAX_PATH] = {};
    GetModuleFileNameA(nullptr, path, MAX_PATH);
    char* slash = strrchr(path, '\\');
    if (slash) { *(slash + 1) = 0; }
    return path;
}

int main(int argc, char** argv)
{
    SetConsoleTitleA("mlsession probe");
    const std::string dir = ExeDir();

    const std::string host = (argc > 1) ? argv[1] : "127.0.0.1";
    const int appId = (argc > 2) ? atoi(argv[2]) : 1;
    const int seconds = (argc > 3) ? atoi(argv[3]) : 30;

    const std::string cert = dir + "creds\\client.crt";
    const std::string key = dir + "creds\\client.key";

    char uid[64] = {};

    // uniqueid 从文件读，省得每次手敲
    FILE* f = nullptr;
    const std::string uidPath = dir + "creds\\uniqueid.txt";
    if (fopen_s(&f, uidPath.c_str(), "rb") == 0 && f)
    {
        size_t n = fread(uid, 1, sizeof(uid) - 1, f);
        fclose(f);
        while (n > 0 && (uid[n - 1] == '\r' || uid[n - 1] == '\n' || uid[n - 1] == ' ')) { uid[--n] = 0; }
    }
    if (uid[0] == 0)
    {
        strcpy_s(uid, "5325b8a3fdd68731");
    }

    printf("== mlsession probe ==\n");
    printf("host=%s appid=%d cert=%s uid=%s\n", host.c_str(), appId, cert.c_str(), uid);

    int rc = MS_Init(cert.c_str(), key.c_str(), uid);
    printf("MS_Init -> %d\n", rc);
    if (rc != 0)
    {
        printf("初始化失败，看看 decoder.log\n");
        return 1;
    }

    rc = MS_Start(host.c_str(), 1920, 1080, 60, 20000, appId);
    printf("MS_Start -> %d\n", rc);
    if (rc != 0)
    {
        return 1;
    }

    char stage[512] = {};
    int lastState = -99;
    std::string lastStage;
    long long packets = 0;
    long long bytes = 0;
    int connected = 0;

    // 每秒打一次；连上并且有包之后再跑 5 秒就收工
    const DWORD start = GetTickCount();
    int streamingSeconds = 0;

    for (;;)
    {
        Sleep(1000);

        const int state = MS_Status(stage, sizeof(stage));
        MS_Stats(&packets, &bytes, &connected);

        if (state != lastState || lastStage != stage)
        {
            printf("[%3us] state=%2d %s（包 %lld，%.1f MB，connected=%d）\n",
                   (GetTickCount() - start) / 1000, state, stage, packets,
                   bytes / 1048576.0, connected);
            lastState = state;
            lastStage = stage;
        }
        else if (state == 2)
        {
            printf("[%3us]      … %lld 包，%.2f MB，平均 %.0f KB/s\n",
                   (GetTickCount() - start) / 1000, packets, bytes / 1048576.0,
                   bytes / 1024.0 / ((GetTickCount() - start) / 1000.0));
        }

        if (state == -1)
        {
            break;
        }

        if (state == 2 && connected)
        {
            streamingSeconds++;
            if (streamingSeconds >= 5)
            {
                printf("串流 5 秒成功，收工。\n");
                break;
            }
        }

        if ((GetTickCount() - start) / 1000 >= (DWORD)seconds)
        {
            printf("到时间了，收工。\n");
            break;
        }
    }

    printf("MS_Stop…\n");
    MS_Stop();
    printf("done.\n");
    return 0;
}
