// hud.cpp —— 悬浮 HUD（GDI + 分层窗口）
//
// 为什么不用 D2D 画在交换链上：D2D 在 FLIP_SEQUENTIAL 交换链 back buffer 上建 target
// 会返回 E_INVALIDARG（alpha 模式不匹配）。改成独立的分层窗口更稳，也不会被视频盖住
// （分层窗口是视频弹窗的 owned window，永远在它之上）。
//
// 对外接口（mlrender.cpp 调用）：
//   HudInit(void* d3dDevice)                  // 兼容旧签名，内部只建字体
//   HudSetOwner(void* videoHwnd)              // 视频弹窗句柄（owned window 用）
//   HudSetText(text, opacity, x, y)           // x/y 是视频画面像素坐标
//   HudSetGear(x, y, visible)
//   HudTakeGearClick() / HudHitGear(x, y)
//   HudDraw(swapChain, width, height)         // 每帧调用：内容变了才重画

#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <string>

namespace {

struct HudState
{
    std::wstring text;
    float opacity = 1.0f;
    int x = 12;
    int y = 12;
    int gearX = -100;
    int gearY = -100;
    bool gearVisible = false;
};

HudState g_hud;
volatile LONG g_gearClicked = 0;

bool g_gearDragging2 = false;
bool g_gearMoved2 = false;
POINT g_gearDragStart2 = {};
POINT g_gearPosStart2 = {};

HWND g_owner = nullptr;      // 视频弹窗
HWND g_textWnd = nullptr;    // 文字 HUD
HWND g_gearWnd = nullptr;    // 齿轮
HFONT g_font = nullptr;
HFONT g_iconFont = nullptr;
std::wstring g_lastText;
int g_lastX = -99999, g_lastY = -99999, g_lastGearX = -99999, g_lastGearY = -99999;
bool g_lastGearVisible = false;
bool g_hudVisible = true;
float g_lastOpacity = -1.0f;

void HudLog(const char* fmt, ...)
{
    char buf[512] = {};
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    FILE* f = nullptr;
    if (fopen_s(&f, "hud.log", "a") == 0 && f)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "%02d:%02d:%02d %s\n", st.wHour, st.wMinute, st.wSecond, buf);
        fclose(f);
    }
}

void RenderGearHud();
void RenderTextHud();

const wchar_t* kHudClass = L"MoonlightHudLayer";

LRESULT CALLBACK HudWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (hwnd == g_gearWnd)
    {
        switch (msg)
        {
        case WM_LBUTTONDOWN:
            HudLog("gear WM_LBUTTONDOWN");
            g_gearDragging2 = true;
            g_gearMoved2 = false;
            GetCursorPos(&g_gearDragStart2);
            g_gearPosStart2.x = g_hud.gearX;
            g_gearPosStart2.y = g_hud.gearY;
            SetCapture(hwnd);
            return 0;

        case WM_MOUSEMOVE:
            if (g_gearDragging2)
            {
                POINT pt;
                GetCursorPos(&pt);
                const int dx = pt.x - g_gearDragStart2.x;
                const int dy = pt.y - g_gearDragStart2.y;
                if (abs(dx) + abs(dy) > 4)
                {
                    g_gearMoved2 = true;
                }

                int nx = g_gearPosStart2.x + dx;
                int ny = g_gearPosStart2.y + dy;
                RECT rc = {};
                GetClientRect(g_owner, &rc);
                nx = (int)max(0, min(nx, (int)rc.right - 40));
                ny = (int)max(0, min(ny, (int)rc.bottom - 40));
                g_hud.gearX = nx;
                g_hud.gearY = ny;
                RenderGearHud();
            }
            return 0;

        case WM_LBUTTONUP:
            if (g_gearDragging2)
            {
                g_gearDragging2 = false;
                ReleaseCapture();

                HudLog("gear up moved=%d", g_gearMoved2 ? 1 : 0);
                if (!g_gearMoved2)
                {
                    InterlockedExchange(&g_gearClicked, 1);
                }
                else
                {
                    // 吸附到最近的边
                    RECT rc = {};
                    GetClientRect(g_owner, &rc);
                    const int margin = 12;
                    const int size = 40;
                    const int right = (int)rc.right;
                    const int bottom = (int)rc.bottom;
                    const int dl = g_hud.gearX;
                    const int dr = right - g_hud.gearX - size;
                    const int dt = g_hud.gearY;
                    const int db = bottom - g_hud.gearY - size;

                    int best = 0;
                    int bestDist = dr;
                    if (dl < bestDist) { bestDist = dl; best = 1; }
                    if (dt < bestDist) { bestDist = dt; best = 2; }
                    if (db < bestDist) { best = 3; }

                    if (best == 0) { g_hud.gearX = right - size - margin; }
                    else if (best == 1) { g_hud.gearX = margin; }
                    else if (best == 2) { g_hud.gearY = margin; }
                    else { g_hud.gearY = bottom - size - margin; }

                    g_hud.gearX = (int)max(margin, min(g_hud.gearX, right - size - margin));
                    g_hud.gearY = (int)max(margin, min(g_hud.gearY, bottom - size - margin));
                    RenderGearHud();
                }
            }
            return 0;

        default:
            break;
        }
    }

    switch (msg)
    {
    case WM_NCHITTEST: return HTTRANSPARENT;   // 鼠标完全穿透到视频窗口
    case WM_ERASEBKGND: return 1;
    default: break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void EnsureClass()
{
    static bool registered = false;
    if (registered)
    {
        return;
    }

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = HudWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = nullptr;
    wc.hbrBackground = nullptr;
    wc.lpszClassName = kHudClass;
    RegisterClassExW(&wc);
    registered = true;
}

HWND CreateLayerWindow(bool clickable)
{
    EnsureClass();

    DWORD ex = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
    if (!clickable)
    {
        ex |= WS_EX_TRANSPARENT;
    }

    return CreateWindowExW(
        ex,
        kHudClass, L"",
        WS_POPUP,
        0, 0, 1, 1,
        g_owner, nullptr, GetModuleHandleW(nullptr), nullptr);
}

// GDI 绘制不会写 alpha 通道；分层窗口按 alpha 做命中测试，所以要手动补 255
void FixAlpha(void* bits, int width, int height)
{
    auto* px = (unsigned char*)bits;
    if (!px)
    {
        return;
    }

    for (int i = 0; i < width * height; i++)
    {
        unsigned char* p = px + i * 4;
        const bool painted = p[0] || p[1] || p[2];
        p[3] = painted ? 255 : 0;
    }
}

void PushLayer(HWND wnd, HDC memDc, HBITMAP bitmap, int width, int height, int screenX, int screenY)
{
    if (!wnd || !memDc || !bitmap)
    {
        return;
    }

    auto screenDc = GetDC(nullptr);
    auto oldBitmap = SelectObject(memDc, bitmap);

    POINT src = { 0, 0 };
    POINT dst = { screenX, screenY };
    SIZE size = { width, height };
    BYTE alpha = (BYTE)(255 * (g_hud.opacity < 0 ? 0 : (g_hud.opacity > 1 ? 1 : g_hud.opacity)));
    BLENDFUNCTION blend = { AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA };

    UpdateLayeredWindow(wnd, screenDc, &dst, &size, memDc, &src, 0, &blend, ULW_ALPHA);

    SelectObject(memDc, oldBitmap);
    ReleaseDC(nullptr, screenDc);
}

void RenderTextHud()
{
    if (!g_textWnd || g_hud.text.empty() || g_owner == nullptr)
    {
        if (g_textWnd)
        {
            ShowWindow(g_textWnd, SW_HIDE);
        }
        return;
    }

    auto screenDc = GetDC(nullptr);
    auto memDc = CreateCompatibleDC(screenDc);
    ReleaseDC(nullptr, screenDc);

    auto oldFont = SelectObject(memDc, g_font);
    RECT calc = { 0, 0, 0, 0 };
    DrawTextW(memDc, g_hud.text.c_str(), -1, &calc, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
    SelectObject(memDc, oldFont);

    const int padX = 14;
    const int padY = 8;
    const int width = (calc.right - calc.left) + padX * 2;
    const int height = (calc.bottom - calc.top) + padY * 2;

    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = width;
    bi.bmiHeader.biHeight = -height;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    auto bitmap = CreateDIBSection(memDc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap)
    {
        DeleteDC(memDc);
        return;
    }

    auto oldBitmap = SelectObject(memDc, bitmap);

    HBRUSH bg = CreateSolidBrush(RGB(0, 0, 0));
    auto oldBrush = SelectObject(memDc, bg);
    auto oldPen = SelectObject(memDc, GetStockObject(NULL_PEN));
    RoundRect(memDc, 0, 0, width, height, 16, 16);
    SelectObject(memDc, oldBrush);
    SelectObject(memDc, oldPen);
    DeleteObject(bg);

    SetBkMode(memDc, TRANSPARENT);
    SetTextColor(memDc, RGB(255, 255, 255));
    SelectObject(memDc, g_font);
    RECT textRc = { padX, padY, width - padX, height - padY };
    DrawTextW(memDc, g_hud.text.c_str(), -1, &textRc, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER);

    FixAlpha(bits, width, height);

    POINT origin = { 0, 0 };
    ClientToScreen(g_owner, &origin);
    PushLayer(g_textWnd, memDc, bitmap, width, height, origin.x + g_hud.x, origin.y + g_hud.y);
    ShowWindow(g_textWnd, SW_SHOWNOACTIVATE);

    SelectObject(memDc, oldBitmap);
    DeleteObject(bitmap);
    DeleteDC(memDc);
}

void RenderGearHud()
{
    if (!g_gearWnd)
    {
        return;
    }

    if (!g_hud.gearVisible || g_owner == nullptr)
    {
        ShowWindow(g_gearWnd, SW_HIDE);
        return;
    }

    const int size = 40;

    auto screenDc = GetDC(nullptr);
    auto memDc = CreateCompatibleDC(screenDc);
    ReleaseDC(nullptr, screenDc);

    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = size;
    bi.bmiHeader.biHeight = -size;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    auto bitmap = CreateDIBSection(memDc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap)
    {
        DeleteDC(memDc);
        return;
    }

    auto oldBitmap = SelectObject(memDc, bitmap);

    HBRUSH bg = CreateSolidBrush(RGB(40, 40, 40));
    auto oldBrush = SelectObject(memDc, bg);
    auto oldPen = SelectObject(memDc, GetStockObject(NULL_PEN));
    Ellipse(memDc, 0, 0, size, size);
    SelectObject(memDc, oldBrush);
    SelectObject(memDc, oldPen);
    DeleteObject(bg);

    SetBkMode(memDc, TRANSPARENT);
    SetTextColor(memDc, RGB(255, 255, 255));
    SelectObject(memDc, g_iconFont);
    RECT iconRc = { 0, 8, size, size + 4 };
    DrawTextW(memDc, L"\uE713", -1, &iconRc, DT_SINGLELINE | DT_NOPREFIX | DT_CENTER);

    FixAlpha(bits, size, size);

    POINT origin = { 0, 0 };
    ClientToScreen(g_owner, &origin);
    PushLayer(g_gearWnd, memDc, bitmap, size, size, origin.x + g_hud.gearX, origin.y + g_hud.gearY);
    ShowWindow(g_gearWnd, SW_SHOWNOACTIVATE);

    SelectObject(memDc, oldBitmap);
    DeleteObject(bitmap);
    DeleteDC(memDc);
}

}  // namespace

extern "C" __declspec(dllexport) int HudInit(void* d3dDevice)
{
    (void)d3dDevice;

    if (!g_font)
    {
        g_font = CreateFontW(-13, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        g_iconFont = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                 DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                 CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe MDL2 Assets");
        HudLog("HudInit(GDI) font=%d icon=%d", g_font ? 1 : 0, g_iconFont ? 1 : 0);
    }

    return (g_font && g_iconFont) ? 0 : -1;
}

extern "C" __declspec(dllexport) void HudSetOwner(void* videoHwnd)
{
    // HUD 必须挂在顶层窗口上：挂在子窗口上的 owned popup 不会显示在视频之上
    g_owner = videoHwnd ? GetAncestor((HWND)videoHwnd, GA_ROOT) : nullptr;
    if (!g_owner)
    {
        return;
    }

    if (!g_textWnd) { g_textWnd = CreateLayerWindow(false); }
    // 齿轮模块已废弃：不再创建窗口

    HudLog("HudSetOwner owner=%p text=%p gear=%p", g_owner, g_textWnd, g_gearWnd);
}

extern "C" __declspec(dllexport) void HudSetText(const wchar_t* text, float opacity, float x, float y)
{
    // 旧的性能条已下线（改为原生性能卡片，稍后接入），这里先清空文字避免残留
    (void)opacity;
    (void)x;
    (void)y;
    g_hud.text = text ? L"" : L"";
}

// 齿轮模块已停用：保留导出仅为兼容旧调用，内部一切置为不可见
extern "C" __declspec(dllexport) void HudSetGear(int x, int y, int visible)
{
    g_hud.gearX = x;
    g_hud.gearY = y;
    (void)x; (void)y; (void)visible;
    g_hud.gearVisible = 0;
}

extern "C" __declspec(dllexport) void HudSetGearVisible(int visible)
{
    (void)visible;
    g_hud.gearVisible = 0;
    if (g_gearWnd)
    {
        ShowWindow(g_gearWnd, SW_HIDE);
    }
}

extern "C" __declspec(dllexport) void HudGetGear(int* x, int* y)
{
    if (x) { *x = g_hud.gearX; }
    if (y) { *y = g_hud.gearY; }
}

extern "C" __declspec(dllexport) void HudClickGear(void)
{
    InterlockedExchange(&g_gearClicked, 1);
}

extern "C" __declspec(dllexport) int HudTakeGearClick(void)
{
    return InterlockedExchange(&g_gearClicked, 0) != 0 ? 1 : 0;
}

extern "C" __declspec(dllexport) int HudHitGear(int x, int y)
{
    if (!g_hud.gearVisible)
    {
        return 0;
    }

    const int size = 40;
    if (x >= g_hud.gearX && x < g_hud.gearX + size &&
        y >= g_hud.gearY && y < g_hud.gearY + size)
    {
        return 1;   // 只是命中测试，不产生点击（点击由 WM_LBUTTONUP 里的 HudClickGear 触发）
    }

    return 0;
}

extern "C" __declspec(dllexport) void HudSetVisible(int visible)
{
    g_hudVisible = visible != 0;
    if (!visible)
    {
        if (g_textWnd) { ShowWindow(g_textWnd, SW_HIDE); }
        if (g_gearWnd) { ShowWindow(g_gearWnd, SW_HIDE); }
        g_lastText.clear();
        return;
    }

    g_lastText.clear();
    RenderTextHud();
    RenderGearHud();
}

extern "C" __declspec(dllexport) void HudDraw(void* swapChain, int width, int height)
{
    (void)swapChain;

    if (!g_owner || !g_hudVisible || (!g_textWnd && !g_gearWnd))
    {
        return;
    }

    const bool textChanged = g_hud.text != g_lastText || g_hud.x != g_lastX || g_hud.y != g_lastY ||
                             g_hud.opacity != g_lastOpacity;
    if (textChanged)
    {
        g_lastText = g_hud.text;
        g_lastX = g_hud.x;
        g_lastY = g_hud.y;
        g_lastOpacity = g_hud.opacity;
        RenderTextHud();
    }

    const bool gearChanged = g_hud.gearVisible != g_lastGearVisible ||
                             g_hud.gearX != g_lastGearX || g_hud.gearY != g_lastGearY;
    if (gearChanged)
    {
        g_lastGearVisible = g_hud.gearVisible;
        g_lastGearX = g_hud.gearX;
        g_lastGearY = g_hud.gearY;
        RenderGearHud();
    }
}
