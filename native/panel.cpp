// 原生控制面板已废弃：现在用 WinUI 控制面板（ControlWindow.cs）。
// 这里只保留两个仍在使用的接口：
//   PNL_SetApplyCallback —— C# 注册"应用设置"回调（历史遗留，保留以免破坏调用方）
//   PNL_RequestOpen / PNL_TakeOpenRequest —— 串流窗口标题栏菜单按钮的"请求打开面板"握手
#include <windows.h>

typedef void (*ApplyCallback)(int width, int height, int kbps, int fullscreen);

static ApplyCallback g_apply = nullptr;
static volatile LONG g_openRequest = 0;

extern "C" __declspec(dllexport) void PNL_SetApplyCallback(void* callback)
{
    g_apply = (ApplyCallback)callback;
}

extern "C" __declspec(dllexport) void PNL_RequestOpen(void)
{
    InterlockedExchange(&g_openRequest, 1);
}

extern "C" __declspec(dllexport) int PNL_TakeOpenRequest(void)
{
    return InterlockedExchange(&g_openRequest, 0) != 0 ? 1 : 0;
}
