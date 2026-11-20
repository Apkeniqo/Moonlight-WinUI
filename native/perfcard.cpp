// perfcard.cpp —— 串流画面上的性能信息卡片（GDI 普通窗口 + 圆角区域，不透明、可读性好）
//
//   PERF_Init(void* ownerRoot)                                     // 绑定串流窗口
//   PERF_SetConfig(int pos, int items, int cornerSingle, double opacity)
//   PERF_Update(const wchar_t* const* lines, int count, int singleLine)
//   PERF_Reposition()                                              // 窗口移动/缩放时重定位
//   PERF_Destroy()

#include <windows.h>
#include <string>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include <vector>

namespace {

HWND g_owner = nullptr;
HWND g_card = nullptr;

int g_pos = 0;
int g_items = 0x3F;
int g_cornerSingle = 1;
double g_opacity = 1.0;
int g_topmost = 0;   // 全屏时置 1：卡片要压在整屏串流窗口上面

int g_single = 1;
std::vector<std::wstring> g_lines;

const int kPadX = 12;
const int kPadY = 4;

// 诊断：写到 exe 目录的 decoder.log（和 streamwin 的 Diag 同一个文件）
void PLog(const char* fmt, ...)
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
        fprintf(f, "%02d:%02d:%02d.%03d [perf] %s\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

HFONT MakeFont(int px)
{
    return CreateFontW(-px, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                       OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       DEFAULT_PITCH, L"Segoe UI");
}

void DrawCardBg(HDC dc, int w, int h, int radius)
{
    auto brush = CreateSolidBrush(RGB(12, 12, 12));
    auto pen = CreatePen(PS_SOLID, 1, RGB(60, 60, 60));
    auto ob = SelectObject(dc, brush);
    auto op = SelectObject(dc, pen);
    RoundRect(dc, 0, 0, w, h, radius, radius);
    SelectObject(dc, ob);
    SelectObject(dc, op);
    DeleteObject(brush);
    DeleteObject(pen);
}

void Measure(HDC dc, int& outW, int& outH, int& lineH)
{
    const UINT dpi = g_owner ? GetDpiForWindow(g_owner) : 96;
    const double sc = dpi / 96.0;

    lineH = (int)(16 * sc);
    const int padX = (int)(kPadX * sc);
    const int padY = (int)(kPadY * sc);

    auto font = MakeFont((int)(12 * sc));
    auto old = SelectObject(dc, font);

    int w = 0;
    int h = 0;

    if (g_single)
    {
        std::wstring joined;
        for (size_t i = 0; i < g_lines.size(); i++)
        {
            if (i) { joined += L"   "; }
            joined += g_lines[i];
        }

        SIZE sz = {};
        GetTextExtentPoint32W(dc, joined.c_str(), (int)joined.size(), &sz);
        w = sz.cx + padX * 2;
        h = lineH + padY * 2;
    }
    else
    {
        int maxW = 0;
        for (const auto& line : g_lines)
        {
            SIZE sz = {};
            GetTextExtentPoint32W(dc, line.c_str(), (int)line.size(), &sz);
            if (sz.cx > maxW) { maxW = sz.cx; }
        }

        w = maxW + padX * 2;
        h = (int)g_lines.size() * (lineH + (int)(3 * sc)) + padY * 2 - (int)(3 * sc);
    }

    SelectObject(dc, old);
    DeleteObject(font);

    outW = w < 80 ? 80 : w;
    outH = h < 26 ? 26 : h;
}

void PositionCard(int w, int h)
{
    if (!g_owner || !g_card)
    {
        return;
    }

    RECT rc = {};
    GetClientRect(g_owner, &rc);

    const UINT dpi = GetDpiForWindow(g_owner);
    const int margin = (int)(12 * dpi / 96.0);

    int x = margin;
    int y = margin;

    switch (g_pos)
    {
    case 0: x = margin; y = margin; break;
    case 1: x = (rc.right - w) / 2; y = margin; break;
    case 2: x = rc.right - w - margin; y = margin; break;
    case 3: x = margin; y = rc.bottom - h - margin; break;
    case 4: x = (rc.right - w) / 2; y = rc.bottom - h - margin; break;
    case 5: x = rc.right - w - margin; y = rc.bottom - h - margin; break;
    default: break;
    }

    POINT origin = { 0, 0 };
    ClientToScreen(g_owner, &origin);

    // 挂在串流窗口之上：全屏时串流窗口会被 SetWindowPos(HWND_TOP) 提到最前，
    // 用 SWP_NOZORDER 的话卡片会被压在下面（表现就是"全屏看不到性能栏"）。
    HWND after = g_topmost ? HWND_TOPMOST : g_owner;   // 串流窗口前台时置顶，否则普通层级
    SetWindowPos(g_card, after, origin.x + x, origin.y + y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    static int lastX = -1, lastY = -1, lastW = -1, lastH = -1, lastTop = -1, lastVis = -1;
    const int vis = IsWindowVisible(g_card) ? 1 : 0;
    if (origin.x + x != lastX || origin.y + y != lastY || w != lastW || h != lastH ||
        g_topmost != lastTop || vis != lastVis)
    {
        lastX = origin.x + x; lastY = origin.y + y; lastW = w; lastH = h;
        lastTop = g_topmost; lastVis = vis;
        PLog("PositionCard pos=%d card=%dx%d@%d,%d owner=%dx%d@%d,%d topmost=%d visible=%d",
             g_pos, w, h, origin.x + x, origin.y + y, rc.right, rc.bottom, origin.x, origin.y,
             g_topmost, vis);
    }
}

void Redraw()
{
    if (!g_owner || !g_card)
    {
        return;
    }

    if (g_lines.empty())
    {
        ShowWindow(g_card, SW_HIDE);
        return;
    }

    HDC screen = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(screen);

    int w = 0;
    int h = 0;
    int lineH = 0;
    Measure(mem, w, h, lineH);

    DeleteDC(mem);
    ReleaseDC(nullptr, screen);

    const UINT dpi = GetDpiForWindow(g_owner);
    const int radius = (int)(10 * dpi / 96.0);

    auto rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2);
    SetWindowRgn(g_card, rgn, TRUE);

    PositionCard(w, h);
    SetLayeredWindowAttributes(g_card, 0, (BYTE)(255 * g_opacity), LWA_ALPHA);

    InvalidateRect(g_card, nullptr, TRUE);
    UpdateWindow(g_card);
}

LRESULT CALLBACK CardProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_NCHITTEST:
        return HTTRANSPARENT;   // 输入穿透到视频窗口

    case WM_PAINT:
    {
        PAINTSTRUCT ps = {};
        HDC dc = BeginPaint(hwnd, &ps);

        RECT rc = {};
        GetClientRect(hwnd, &rc);

        const UINT dpi = g_owner ? GetDpiForWindow(g_owner) : 96;
        const double sc = dpi / 96.0;

        HBRUSH black = CreateSolidBrush(RGB(0, 0, 0));
        FillRect(dc, &rc, black);
        DeleteObject(black);

        DrawCardBg(dc, rc.right, rc.bottom, (int)(10 * sc));

        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));

        auto font = MakeFont((int)(12 * sc));
        auto oldFont = SelectObject(dc, font);

        const int padX = (int)(kPadX * sc);
        const int padY = (int)(kPadY * sc);

        if (g_single)
        {
            std::wstring joined;
            for (size_t i = 0; i < g_lines.size(); i++)
            {
                if (i) { joined += L"   "; }
                joined += g_lines[i];
            }

            RECT tr = { padX, 0, rc.right - padX, rc.bottom };
            DrawTextW(dc, joined.c_str(), -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        }
        else
        {
            const int lineH = (int)(16 * sc);
            int y = padY;
            for (const auto& line : g_lines)
            {
                RECT tr = { padX, y, rc.right - padX, y + lineH + (int)(2 * sc) };
                DrawTextW(dc, line.c_str(), -1, &tr, DT_LEFT | DT_SINGLELINE | DT_NOPREFIX);
                y += lineH + (int)(3 * sc);
            }
        }

        SelectObject(dc, oldFont);
        DeleteObject(font);
        EndPaint(hwnd, &ps);
        return 0;
    }

    case WM_ERASEBKGND:
        return 1;

    default:
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

}  // namespace

extern "C" __declspec(dllexport) void PERF_Init(void* ownerRoot)
{
    // 重连/重开窗口后 owner 变了：旧卡片留着会挂到已销毁的窗口上（看不见）
    if (g_card && g_owner != (HWND)ownerRoot)
    {
        DestroyWindow(g_card);
        g_card = nullptr;
        g_lines.clear();
    }

    g_owner = (HWND)ownerRoot;
    if (!g_owner || g_card)
    {
        return;
    }

    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = CardProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hbrBackground = nullptr;
        wc.lpszClassName = L"MoonlightPerfCard";
        RegisterClassExW(&wc);
        registered = true;
    }

    // 层级由"串流窗口是否前台"决定（PERF_SetTopmost），不在创建时固定
    g_card = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                             L"MoonlightPerfCard", L"", WS_POPUP,
                             0, 0, 10, 10, g_owner, nullptr, GetModuleHandleW(nullptr), nullptr);
}

extern "C" __declspec(dllexport) void PERF_SetConfig(int pos, int items, int cornerSingle, double opacity)
{
    g_pos = pos;
    g_items = items;
    g_cornerSingle = cornerSingle ? 1 : 0;
    g_opacity = opacity < 0.25 ? 0.25 : (opacity > 1.0 ? 1.0 : opacity);

    if (g_card)
    {
        SetLayeredWindowAttributes(g_card, 0, (BYTE)(255 * g_opacity), LWA_ALPHA);
    }
}

extern "C" __declspec(dllexport) void PERF_Update(const wchar_t* const* lines, int count, int singleLine)
{
    g_lines.clear();
    if (lines && count > 0)
    {
        for (int i = 0; i < count; i++)
        {
            if (lines[i] && lines[i][0])
            {
                g_lines.push_back(lines[i]);
            }
        }
    }

    g_single = singleLine ? 1 : 0;
    Redraw();
}

extern "C" __declspec(dllexport) void PERF_Reposition(void)
{
    if (!g_card || !g_owner || g_lines.empty())
    {
        return;
    }

    HDC screen = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(screen);
    int w = 0;
    int h = 0;
    int lineH = 0;
    Measure(mem, w, h, lineH);
    DeleteDC(mem);
    ReleaseDC(nullptr, screen);

    PositionCard(w, h);
}

// 全屏/退出全屏时调用：全屏下串流窗口是整屏 WS_POPUP 且会被提到最前，
// 卡片必须 topmost 才能稳定压在它上面。
extern "C" __declspec(dllexport) void PERF_SetTopmost(int on)
{
    g_topmost = on ? 1 : 0;
    PLog("PERF_SetTopmost(%d) card=%p", g_topmost, (void*)g_card);

    if (g_card)
    {
        SetWindowPos(g_card, g_topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Redraw();
    }
}

extern "C" __declspec(dllexport) void PERF_Destroy(void)
{
    if (g_card)
    {
        DestroyWindow(g_card);
        g_card = nullptr;
    }

    g_owner = nullptr;
    g_lines.clear();
}
