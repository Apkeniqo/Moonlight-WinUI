// streamwin.cpp —— 纯 Win32 串流窗口（系统标题栏 + 视频子窗口 + 悬浮菜单按钮）
//
// 这版回到「原生标题栏」：不处理 WM_NCCALCSIZE，客户区就在标题栏下方；
// 标题栏按钮由系统按标准布局绘制（✕ 贴住窗口可见边缘，和资源管理器一致）。
// 菜单按钮做成浮窗（popup，圆角矩形），定位在系统标题栏按钮左侧。

#include <windows.h>
#include <windowsx.h>
#include <dwmapi.h>
#include <cstdarg>
#include <cstdio>
#include <string>

#pragma comment(lib, "dwmapi.lib")

extern "C" {
int  ML_HostChildWin32(void* parent, int w, int h);
void ML_Resize(int w, int h);
int  ML_ApplyCursor(void);
void ML_SetGearEnabled(int enabled);
void PERF_Reposition(void);
void PNL_RequestOpen(void);
void PERF_SetTopmost(int on);
}

// 串流窗口移动/缩放时同步回调 C#（面板实时跟随用，比轮询更跟手）
typedef void (*MoveCallback)();
MoveCallback g_moveCallback = nullptr;


namespace {

HWND g_win = nullptr;
int  g_backdropKind = 0;   // 0..2 = 有材质（客户区不画背景，让材质透出来），3 = 无材质
HWND g_menu = nullptr;
HWND g_follow = nullptr;   // WinUI 面板窗口：随串流窗口实时居中
bool g_closed = false;
bool g_fullscreen = false;
int g_aspectW = 16;
bool g_adjustingAspect = false;
int g_aspectH = 9;
std::wstring g_title = L"Moonlight 串流";

int g_menuSize = 44;
bool g_menuHover = false;

WINDOWPLACEMENT g_prevPlacement = { sizeof(WINDOWPLACEMENT) };
LONG_PTR g_prevStyle = 0;

// ---------------- 诊断 ----------------

// 记到 exe 目录的 decoder.log，用来判断"闪退"到底发生在点菜单之前还是之后
void Diag(const char* fmt, ...)
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
        fprintf(f, "%02d:%02d:%02d.%03d [menu] %s\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

// ---------------- 背景 ----------------

void ApplyBackdrop(HWND hwnd)
{
    BOOL dark = TRUE;
    DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof(dark));

    int backdrop = 2;   // Mica
    DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, &backdrop, sizeof(backdrop));

    // 窗口保持 Win11 圆角（画面会跟着圆角走，见 ResizeVideo 的注释）
    int corner = 2;
    DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &corner, sizeof(corner));

    // 恢复系统的 1px 边框（之前设成 DWMWA_COLOR_NONE 去掉边框，结果窗口看着"没边"，
    // 而且圆角只画在标题栏、底部仍是方的）。这里改回默认边框色。
}

// ---------------- 悬浮菜单按钮（圆角浮窗） ----------------

void PaintMenuButton(HWND hwnd)
{
    PAINTSTRUCT ps = {};
    auto dc = BeginPaint(hwnd, &ps);

    RECT rc = {};
    GetClientRect(hwnd, &rc);

    // 圆角矩形底
    HBRUSH bg = CreateSolidBrush(RGB(48, 48, 48));
    auto pen = CreatePen(PS_SOLID, 1, RGB(80, 80, 80));
    auto oldBrush = SelectObject(dc, bg);
    auto oldPen = SelectObject(dc, pen);
    RoundRect(dc, 0, 0, rc.right, rc.bottom, 18, 18);
    SelectObject(dc, oldBrush);
    SelectObject(dc, oldPen);
    DeleteObject(bg);
    DeleteObject(pen);

    // ☰
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(235, 235, 235));
    auto font = CreateFontW(-(int)(g_menuSize * 0.5), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH,
                            L"Segoe MDL2 Assets");
    auto oldFont = SelectObject(dc, font);
    DrawTextW(dc, L"\uE700", -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    SelectObject(dc, oldFont);
    DeleteObject(font);

    EndPaint(hwnd, &ps);
}

void ApplyRoundRegion(HWND hwnd)
{
    RECT rc = {};
    GetClientRect(hwnd, &rc);
    auto rgn = CreateRoundRectRgn(0, 0, rc.right + 1, rc.bottom + 1, 18, 18);
    SetWindowRgn(hwnd, rgn, TRUE);
}

int g_menuX = -1;
int g_menuY = -1;
bool g_menuDragging = false;
bool g_menuMoved = false;
POINT g_menuDragStart = {};

void ClampMenu()
{
    RECT rc = {};
    GetClientRect(g_win, &rc);
    const int size = g_menuSize;
    if (g_menuX < 4) { g_menuX = 4; }
    if (g_menuY < 4) { g_menuY = 4; }
    if (g_menuX > rc.right - size - 4) { g_menuX = rc.right - size - 4; }
    if (g_menuY > rc.bottom - size - 4) { g_menuY = rc.bottom - size - 4; }
}

// 实测非客户区尺寸（窗口矩形 - 客户区）：ncW 含左右边框，ncH 含标题栏 + 下边框。
// 用它而不是 AdjustWindowRect 估算，DPI 变过或被系统微调过时也不会算错。
bool GetNonClientSize(HWND hwnd, int* ncW, int* ncH)
{
    RECT wr = {};
    RECT cr = {};
    if (!hwnd || !GetWindowRect(hwnd, &wr) || !GetClientRect(hwnd, &cr))
    {
        if (ncW) { *ncW = 0; }
        if (ncH) { *ncH = 0; }
        return false;
    }

    if (ncW) { *ncW = (int)((wr.right - wr.left) - (cr.right - cr.left)); }
    if (ncH) { *ncH = (int)((wr.bottom - wr.top) - (cr.bottom - cr.top)); }
    return true;
}

void PositionMenuButton()
{
    if (!g_win || !g_menu || g_fullscreen)
    {
        return;
    }

    if (g_menuX < 0 || g_menuY < 0)
    {
        // 首次：放窗口右上角
        RECT rc = {};
        GetClientRect(g_win, &rc);
        g_menuX = rc.right - g_menuSize - 16;
        g_menuY = 14;
    }

    ClampMenu();
    SetWindowPos(g_menu, HWND_TOP, g_menuX, g_menuY, g_menuSize, g_menuSize, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    ApplyRoundRegion(g_menu);
}

// 松手后吸附到最近的边
void SnapMenuToEdge()
{
    RECT rc = {};
    GetClientRect(g_win, &rc);

    const int size = g_menuSize;
    const int margin = 12;
    const int dl = g_menuX;
    const int dr = (rc.right - size) - g_menuX;
    const int dt = g_menuY;
    const int db = (rc.bottom - size) - g_menuY;

    int best = 0;
    int bestDist = dr;
    if (dl < bestDist) { bestDist = dl; best = 1; }
    if (dt < bestDist) { bestDist = dt; best = 2; }
    if (db < bestDist) { best = 3; }

    if (best == 0) { g_menuX = rc.right - size - margin; }
    else if (best == 1) { g_menuX = margin; }
    else if (best == 2) { g_menuY = margin; }
    else { g_menuY = rc.bottom - size - margin; }

    ClampMenu();
    SetWindowPos(g_menu, HWND_TOP, g_menuX, g_menuY, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

LRESULT CALLBACK MenuProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_PAINT:
        PaintMenuButton(hwnd);
        return 0;

    case WM_MOUSELEAVE:
        g_menuHover = false;
        SetLayeredWindowAttributes(hwnd, 0, 140, LWA_ALPHA);   // 未悬停：半透明
        return 0;

    case WM_SETCURSOR:
        SetCursor(LoadCursor(nullptr, IDC_ARROW));
        return TRUE;

    case WM_LBUTTONDOWN:
    {
        g_menuDragging = true;
        g_menuMoved = false;
        POINT pt = { GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
        ClientToScreen(hwnd, &pt);
        g_menuDragStart = pt;
        SetCapture(hwnd);
        return 0;
    }

    case WM_MOUSEMOVE:
        if (!g_menuHover)
        {
            g_menuHover = true;
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            TRACKMOUSEEVENT tme = {};
            tme.cbSize = sizeof(TRACKMOUSEEVENT);
            tme.dwFlags = TME_LEAVE;
            tme.hwndTrack = hwnd;
            TrackMouseEvent(&tme);
        }
        if (g_menuDragging && g_win)
        {
            POINT pt = {};
            GetCursorPos(&pt);
            const int dx = pt.x - g_menuDragStart.x;
            const int dy = pt.y - g_menuDragStart.y;
            if (abs(dx) + abs(dy) > 3) { g_menuMoved = true; }

            POINT origin = { dx, dy };
            RECT rc = {};
            GetClientRect(g_win, &rc);
            g_menuX = origin.x + (g_menuX >= 0 ? g_menuX : rc.right - 32 - 16);
            g_menuY = origin.y + (g_menuY >= 0 ? g_menuY : 14);
            ClampMenu();
            SetWindowPos(hwnd, HWND_TOP, g_menuX, g_menuY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            g_menuDragStart = pt;
        }
        return 0;

    case WM_LBUTTONUP:
        if (g_menuDragging)
        {
            g_menuDragging = false;
            ReleaseCapture();
            if (g_menuMoved)
            {
                SnapMenuToEdge();
            }
            else
            {
                Diag("menu click → PNL_RequestOpen");
                PNL_RequestOpen();   // 由 C# 带当前设置打开面板
            }
            return 0;
        }
        break;

    case WM_ERASEBKGND:
        return 1;

    default:
        break;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

void CenterFollowWindow()
{
    if (!g_win || !g_follow || !IsWindow(g_follow) || !IsWindowVisible(g_follow))
    {
        return;
    }

    RECT client = {};
    GetClientRect(g_win, &client);

    // 弹窗尺寸由 C#（ControlWindow.ShowAt）决定，这里只负责居中。
    // 必须按弹窗的**真实**尺寸算：这段以前按 0.72*client 算出一个"期望尺寸"，
    // 但 SetWindowPos 带的是 SWP_NOSIZE（不改变尺寸），于是每次重新居中都会把
    // 左上角算偏（真实 620x420 vs 算出来的 910x512 → 左移 ~150px、上移 ~30px）。
    // 串流窗口一收到 WM_ACTIVATE（点弹窗外/关弹窗时都会）就会看到这一跳。
    RECT panel = {};
    if (!GetWindowRect(g_follow, &panel))
    {
        return;
    }

    const int pw = panel.right - panel.left;
    const int ph = panel.bottom - panel.top;
    if (pw <= 0 || ph <= 0)
    {
        return;
    }

    POINT pt = { (client.right - pw) / 2, (client.bottom - ph) / 2 };
    ClientToScreen(g_win, &pt);
    SetWindowPos(g_follow, nullptr, pt.x, pt.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}

// ---------------- 视频布局 ----------------

void ResizeVideo()
{
    if (!g_win)
    {
        return;
    }

    RECT rc = {};
    GetClientRect(g_win, &rc);

    const int availW = (int)max(2, (int)rc.right);
    const int availH = (int)max(2, (int)rc.bottom);

    // 保持流的宽高比：比例一致时正好铺满客户区（窗口已经是直角，四角不再被裁）；
    // 比例不一致时上下/左右留黑边。
    int vw = availW;
    int vh = (int)((double)availW * g_aspectH / g_aspectW);
    if (vh > availH)
    {
        vh = availH;
        vw = (int)((double)availH * g_aspectW / g_aspectH);
    }

    vw = (int)max(2, vw);
    vh = (int)max(2, vh);

    const int vx = (availW - vw) / 2;
    const int vy = (availH - vh) / 2;

    auto child = FindWindowExW(g_win, nullptr, L"MoonlightChildVideoWin32", nullptr);
    if (child)
    {
        SetWindowPos(child, nullptr, vx, vy, vw, vh, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    ML_Resize(vw, vh);
}

// ---------------- 全屏 ----------------

void ToggleFullscreen(HWND hwnd)
{
    {
        // 全屏排查日志：显示器 / 窗口 / 客户区 / 视频子窗口
        MONITORINFO dmi = { sizeof(dmi) };
        GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY), &dmi);
        RECT dwr = {}, dcr = {};
        GetWindowRect(hwnd, &dwr);
        GetClientRect(hwnd, &dcr);
        char buf[512] = {};
        std::snprintf(buf, sizeof(buf),
            "ToggleFullscreen(enter=%d) monitor=%dx%d@%d,%d window=%dx%d@%d,%d client=%dx%d dpi=%u",
            g_fullscreen ? 0 : 1,
            (int)(dmi.rcMonitor.right - dmi.rcMonitor.left), (int)(dmi.rcMonitor.bottom - dmi.rcMonitor.top),
            (int)dmi.rcMonitor.left, (int)dmi.rcMonitor.top,
            (int)(dwr.right - dwr.left), (int)(dwr.bottom - dwr.top), (int)dwr.left, (int)dwr.top,
            (int)dcr.right, (int)dcr.bottom, GetDpiForWindow(hwnd));
        Diag("%s", buf);
    }

    if (!g_fullscreen)
    {
        g_prevStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);
        GetWindowPlacement(hwnd, &g_prevPlacement);

        MONITORINFO mi = { sizeof(mi) };
        GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY), &mi);

        // 关键：先把 g_fullscreen 置位、并临时关掉"收敛到流比例"，否则下面 SetWindowPos
        // 触发的 WM_SIZE 里 g_fullscreen 还是 false，会被兜底逻辑把窗口缩回流比例（全屏露桌面）。
        g_fullscreen = true;
        g_adjustingAspect = true;

        SetWindowLongPtrW(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
        SetWindowPos(hwnd, HWND_TOP,
                     mi.rcMonitor.left, mi.rcMonitor.top,
                     mi.rcMonitor.right - mi.rcMonitor.left,
                     mi.rcMonitor.bottom - mi.rcMonitor.top,
                     SWP_FRAMECHANGED | SWP_NOOWNERZORDER);

        g_adjustingAspect = false;

        // 去掉 DWM 画的 1px 边框（否则全屏四周会有一条白边）
        int borderNone = (int)0xFFFFFFFE;   // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, &borderNone, sizeof(borderNone));

        // 全屏不要圆角（否则四角露背景）
        int cornerNone = 1;   // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &cornerNone, sizeof(cornerNone));


        // 全屏时彻底关掉旧的圆形 HUD 齿轮
        ML_SetGearEnabled(0);

        ResizeVideo();       // 进入全屏后立刻按整屏重新居中
    }
    else
    {
        // 先复位置，后面的 SetWindowPos/SWP_FRAMECHANGED 触发的 WM_NCCALCSIZE
        // 才会按"窗口模式"把左右/下边框补掉（否则画面会一直缩在边框里，直到下次改窗口大小）
        g_fullscreen = false;

        SetWindowLongPtrW(hwnd, GWL_STYLE, g_prevStyle);
        SetWindowPlacement(hwnd, &g_prevPlacement);
        SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                     SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER);

        // 退出全屏恢复窗口圆角
        int borderDefault = (int)0xFFFFFFFF;   // DWMWA_COLOR_DEFAULT：退出全屏恢复系统边框
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, &borderDefault, sizeof(borderDefault));
        int cornerRound = 2;   // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &cornerRound, sizeof(cornerRound));
    }

    if (g_menu)
    {
        ShowWindow(g_menu, SW_SHOW);
        PositionMenuButton();
    }

    ResizeVideo();
}

// ---------------- 窗口过程 ----------------

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_SIZING:
    {
        // 按当前视频比例锁死窗口比例，缩放时不再出现黑边
        if (g_aspectW <= 0 || g_aspectH <= 0)
        {
            return TRUE;
        }

        RECT* r = (RECT*)lp;

        // 边框 + 标题栏占用的尺寸（实测，不要用 AdjustWindowRect 估）
        int frameW = 0;
        int frameH = 0;
        GetNonClientSize(hwnd, &frameW, &frameH);

        int clientW = (r->right - r->left) - frameW;
        int clientH = (r->bottom - r->top) - frameH;
        if (clientW < 64) { clientW = 64; }
        if (clientH < 64) { clientH = 64; }

        const double target = (double)g_aspectW / (double)g_aspectH;
        int newW = clientW;
        int newH = clientH;

        switch (wp)
        {
        case WMSZ_LEFT:
        case WMSZ_RIGHT:
            newH = (int)(clientW / target);
            break;

        case WMSZ_TOP:
        case WMSZ_BOTTOM:
            newW = (int)(clientH * target);
            break;

        default:   // 四个角：按超出较少的一边收敛
            if ((double)clientW / (double)clientH > target)
            {
                newW = (int)(clientH * target);
            }
            else
            {
                newH = (int)(clientW / target);
            }
            break;
        }

        if (newW < 64) { newW = 64; }
        if (newH < 64) { newH = 64; }

        const int winW = newW + frameW;
        const int winH = newH + frameH;

        switch (wp)
        {
        case WMSZ_LEFT:
        case WMSZ_TOPLEFT:
        case WMSZ_BOTTOMLEFT:
            r->left = r->right - winW;
            break;
        default:
            r->right = r->left + winW;
            break;
        }

        switch (wp)
        {
        case WMSZ_TOP:
        case WMSZ_TOPLEFT:
        case WMSZ_TOPRIGHT:
            r->top = r->bottom - winH;
            break;
        default:
            r->bottom = r->top + winH;
            break;
        }

        return TRUE;
    }

    case WM_SIZE:
        // 全屏时窗口必须正好铺满显示器：任何路径（含异步的 SetVideoAspect / 外部 resize）
        // 想把窗口改小都在这里被拉回来，否则下面会露出桌面。
        if (g_fullscreen)
        {
            MONITORINFO mi = { sizeof(mi) };
            GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY), &mi);
            RECT wr = {};
            GetWindowRect(hwnd, &wr);
            const int mw = mi.rcMonitor.right - mi.rcMonitor.left;
            const int mh = mi.rcMonitor.bottom - mi.rcMonitor.top;
            if (wr.left != mi.rcMonitor.left || wr.top != mi.rcMonitor.top ||
                (wr.right - wr.left) != mw || (wr.bottom - wr.top) != mh)
            {
                SetWindowPos(hwnd, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top, mw, mh,
                             SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        CenterFollowWindow();

        if (g_fullscreen)
        {
            MONITORINFO smi = { sizeof(smi) };
            GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY), &smi);
            RECT swr = {}, scr = {};
            GetWindowRect(hwnd, &swr);
            GetClientRect(hwnd, &scr);
            Diag("WM_SIZE 全屏: window=%dx%d@%d,%d client=%dx%d monitor=%dx%d",
                 (int)(swr.right - swr.left), (int)(swr.bottom - swr.top), (int)swr.left, (int)swr.top,
                 (int)scr.right, (int)scr.bottom,
                 (int)(smi.rcMonitor.right - smi.rcMonitor.left), (int)(smi.rcMonitor.bottom - smi.rcMonitor.top));
        }

        // 兜底：非交互式改变尺寸（脚本、最大化切换等）也把客户区收敛到视频比例
        if (!g_adjustingAspect && g_aspectW > 0 && g_aspectH > 0 && !g_fullscreen && !IsZoomed(g_win))
        {
            RECT crc = {};
            GetClientRect(g_win, &crc);
            if (crc.right > 64 && crc.bottom > 16)
            {
                const int wantH = (int)(crc.right * (double)g_aspectH / (double)g_aspectW);
                if (abs(wantH - crc.bottom) > 1)
                {
                    int fw = 0;
                    int fh = 0;
                    GetNonClientSize(g_win, &fw, &fh);

                    g_adjustingAspect = true;
                    SetWindowPos(g_win, nullptr, 0, 0, crc.right + fw, wantH + fh,
                                 SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    g_adjustingAspect = false;
                }
            }
        }

        ResizeVideo();
        if (g_moveCallback) { g_moveCallback(); }   // 缩放时也让面板实时跟随
        PERF_Reposition();
        PositionMenuButton();
        return 0;

    case WM_MOVE:
        PositionMenuButton();
        CenterFollowWindow();
        PERF_Reposition();   // 性能卡片跟随窗口实时定位
        if (g_moveCallback) { g_moveCallback(); }   // 通知 C# 重新定位控制面板
        break;

    case WM_ACTIVATE:
        // 性能栏始终置顶：焦点切走后也要保持可见（用户要求），不再随激活状态降级
        PERF_SetTopmost(1);
        PositionMenuButton();
        CenterFollowWindow();
        PERF_Reposition();
        if (g_moveCallback) { g_moveCallback(); }
        break;

    case WM_SETCURSOR:
        // 光标只由「视频子窗口」自己设置（它的 WM_SETCURSOR 会应用远程光标）。
        // 外层窗口一律交回 DefWindowProc：边框/标题栏用系统本地光标，
        // 避免两边同时 SetCursor 造成远程/本地光标高频闪烁。
        break;

    case WM_ERASEBKGND:
    {
        // 全屏（WS_POPUP）上材质不渲染，必须自己填窗口背景色，否则黑边是纯黑
        // 有材质且不是全屏时不画背景：让 DWM 的 Mica/丙烯酸透出来；
        if (g_backdropKind < 3 && !g_fullscreen)
        {
            return 1;
        }

        PAINTSTRUCT ps = {};
        auto dc = BeginPaint(hwnd, &ps);
        RECT rc = {};
        GetClientRect(hwnd, &rc);
        HBRUSH bg = CreateSolidBrush(RGB(32, 32, 32));   // 窗口背景色，和标题栏/主界面一致
        FillRect(dc, &rc, bg);
        DeleteObject(bg);
        EndPaint(hwnd, &ps);
        return 1;
    }

    case WM_CLOSE:
        g_closed = true;
        ShowWindow(hwnd, SW_HIDE);
        return 0;

    case WM_DESTROY:
        g_win = nullptr;
        return 0;

    default:
        break;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

}  // namespace

// 窗口材质：主窗口用 WinUI 的 MicaBackdrop/DesktopAcrylicBackdrop，串流窗口是原生窗口，
// 这里按同一个选项设置 DWMWA_SYSTEMBACKDROP_TYPE，两边的标题栏材质才一致。
// 窗口材质：主窗口是 WinUI 窗口，标题栏是它自绘的纯色 #202020（Mica 只体现在正文区）。
// 串流窗口是原生窗口，如果只设 DWMWA_SYSTEMBACKDROP_TYPE，标题栏会带 Mica  tints，
// 看着比主窗口亮，两边对不上。这里统一：材质设 NONE + 标题栏颜色钉成 #202020。
// 窗口材质：串流窗口用系统深色标题栏（和 WinUI 主窗口的标题栏一致）。
// 之前开着 Mica，深色壁纸下标题栏会带一点点 tints，比主窗口亮 2~7 个色阶；
// 这里关掉材质，交给 DWM 画标准深色标题栏（active/inactive 的明暗也和系统一致）。
// 窗口材质：跟主窗口用同一个选项（0=Mica 1=Mica Alt 2=Acrylic 3=无）。
// 主窗口走 WinUI 的 MicaBackdrop/DesktopAcrylicBackdrop，串流窗口是原生窗口，
// 用 DWMWA_SYSTEMBACKDROP_TYPE 设同一种材质，标题栏观感才一致。
extern "C" __declspec(dllexport) void SUI_SetBackdrop(int kind)
{
    if (!g_win)
    {
        return;
    }

    int type = 2;   // DWMSBT_MAINWINDOW = Mica
    switch (kind)
    {
    case 0: type = 2; break;   // Mica
    case 1: type = 4; break;   // Mica Alt (DWMSBT_TABBEDWINDOW)
    case 2: type = 3; break;   // Acrylic (DWMSBT_TRANSIENTWINDOW)
    default: type = 1; break;  // DWMSBT_NONE
    }

    DwmSetWindowAttribute(g_win, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, &type, sizeof(type));

    g_backdropKind = (kind >= 0 && kind <= 3) ? kind : 0;

    // 让材质铺满整个窗口（含客户区），视频没盖住的地方（黑边/首帧前）才看得到材质
    MARGINS margins = {};
    if (g_backdropKind < 3)
    {
        margins.cxLeftWidth = -1;
        margins.cxRightWidth = -1;
        margins.cyTopHeight = -1;
        margins.cyBottomHeight = -1;
    }

    DwmExtendFrameIntoClientArea(g_win, &margins);

    // 让客户区立刻重绘一次（背景的取/不取会变）
    InvalidateRect(g_win, nullptr, TRUE);
}

extern "C" __declspec(dllexport) int SUI_Create(const wchar_t* title, int w, int h)
{
    if (g_win)
    {
        return 0;
    }

    if (title)
    {
        g_title = title;
    }

    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW mc = {};
        mc.cbSize = sizeof(mc);
        mc.lpfnWndProc = MenuProc;
        mc.hInstance = GetModuleHandleW(nullptr);
        mc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        mc.lpszClassName = L"MoonlightMenuButton";
        RegisterClassExW(&mc);

        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = WndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = L"MoonlightStreamWindow";
        RegisterClassExW(&wc);
        registered = true;
    }

    // 默认尺寸 = 主屏工作区的 70%（物理像素）
    if (w <= 0 || h <= 0)
    {
        MONITORINFO mi = { sizeof(mi) };
        GetMonitorInfoW(MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY), &mi);
        w = (int)((mi.rcWork.right - mi.rcWork.left) * 0.7);
        h = (int)((mi.rcWork.bottom - mi.rcWork.top) * 0.7);
        // 按流比例收敛，避免窗口比例不对时出现黑边
        const int fitH = (int)(w * (double)g_aspectH / g_aspectW);
        if (h > fitH) { h = fitH; }
    }

    // 窗口尺寸 = 客户区 + 非客户区（标准原生标题栏）
    RECT rc = { 0, 0, w, h };
    AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);

    g_closed = false;
    g_fullscreen = false;

    g_win = CreateWindowExW(
        0, L"MoonlightStreamWindow", g_title.c_str(),
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT,
        rc.right - rc.left, rc.bottom - rc.top,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!g_win)
    {
        return -1;
    }

    ML_SetGearEnabled(0);   // 原生窗口模式不再使用 HUD 圆形齿轮
    ApplyBackdrop(g_win);
    ShowWindow(g_win, SW_SHOW);

    RECT client = {};
    GetClientRect(g_win, &client);

    const int rc2 = ML_HostChildWin32(g_win, client.right, client.bottom);
    if (rc2 != 0)
    {
        return -3;
    }

    const UINT dpi = GetDpiForWindow(g_win);
    g_menuSize = (int)(44 * dpi / 96);
    if (g_menuSize < 32) { g_menuSize = 32; }

    g_menu = CreateWindowExW(WS_EX_LAYERED, L"MoonlightMenuButton", L"", WS_CHILD | WS_VISIBLE,
                             0, 0, g_menuSize, g_menuSize, g_win, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (g_menu)
    {
        SetLayeredWindowAttributes(g_menu, 0, 140, LWA_ALPHA);
    }

    ResizeVideo();
    PositionMenuButton();
    return 0;
}

extern "C" __declspec(dllexport) int SUI_IsClosed(void)
{
    return g_closed ? 1 : 0;
}

extern "C" __declspec(dllexport) void SUI_Destroy(void)
{
    if (g_menu)
    {
        DestroyWindow(g_menu);
        g_menu = nullptr;
    }

    if (g_win)
    {
        DestroyWindow(g_win);
        g_win = nullptr;
    }

    g_closed = false;
}

extern "C" __declspec(dllexport) void SUI_SetTitle(const wchar_t* title)
{
    if (g_win && title)
    {
        g_title = title;
        SetWindowTextW(g_win, title);
    }
}

extern "C" __declspec(dllexport) void SUI_SetFullscreen(int fullscreen)
{
    if (g_win && (fullscreen != 0) != g_fullscreen)
    {
        ToggleFullscreen(g_win);
    }
}

extern "C" __declspec(dllexport) int SUI_ToggleFullscreen(void)
{
    if (!g_win)
    {
        return 0;
    }

    ToggleFullscreen(g_win);
    return g_fullscreen ? 1 : 0;
}

extern "C" __declspec(dllexport) void* SUI_GetHwnd(void)
{
    return g_win;
}

extern "C" __declspec(dllexport) void SUI_SetStatus(const wchar_t* text)
{
    (void)text;
}

extern "C" __declspec(dllexport) void SUI_SetVideoReady(int ready)
{
    (void)ready;
}

extern "C" __declspec(dllexport) void SUI_ShowError(const wchar_t* title, const wchar_t* text)
{
    MessageBoxW(g_win, text ? text : L"", title ? title : L"Moonlight 串流", MB_OK | MB_ICONERROR | MB_TOPMOST);
}

extern "C" __declspec(dllexport) void SUI_SetVideoAspect(int width, int height)
{
    if (width > 0 && height > 0)
    {
        g_aspectW = width;
        g_aspectH = height;

        // 比例变了：窗口模式下把客户区收敛到同一比例；**全屏/最大化时不能动窗口**，
        // 否则整屏窗口会被改成流比例（比如 2560x1600 → 2560x1440），下面露出桌面。
        if (g_win && !g_fullscreen && !IsZoomed(g_win))
        {
            RECT rc = {};
            GetClientRect(g_win, &rc);

            if (rc.right > 64 && rc.bottom > 16)
            {
                int frameW = 0;
                int frameH = 0;
                GetNonClientSize(g_win, &frameW, &frameH);

                const int newClientH = (int)(rc.right * (double)g_aspectH / (double)g_aspectW);
                if (newClientH > 16 && newClientH != rc.bottom)
                {
                    g_adjustingAspect = true;
                    SetWindowPos(g_win, nullptr, 0, 0,
                                 rc.right + frameW, newClientH + frameH,
                                 SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    g_adjustingAspect = false;
                }
            }
        }

        ResizeVideo();
        PERF_Reposition();
    }
}
extern "C" __declspec(dllexport) void SUI_SetFollowWindow(void* panel)
{
    g_follow = (HWND)panel;
}

// 串流窗口移动/缩放时同步回调 C#（面板实时跟随，比轮询跟手）
extern "C" __declspec(dllexport) void SUI_SetMoveCallback(void* callback)
{
    g_moveCallback = (MoveCallback)callback;
}

