// panel.cpp —— 串流窗口内的原生控制面板（GDI 自绘，Fluent 风格，随窗口缩放）
//
// 布局在 620×420 的"设计尺寸"里定义，实际窗口大小按串流窗口的 72% 取，
// 所有坐标/字号按 g_scale 缩放，所以窗口变大面板也变大。

#include <windows.h>
#include <windowsx.h>
#include <cstdio>
#include <cmath>

namespace {

HWND g_parent = nullptr;
HWND g_panel = nullptr;

const int kDesignW = 620;
const int kDesignH = 420;

int g_width = kDesignW;
int g_height = kDesignH;
double g_scale = 1.0;
const double kMaxScale = 1.55;
int g_animTick = 0;
int g_targetW = 0;
int g_targetH = 0;
RECT g_targetRect = {};

int g_selectedRes = 3;
int g_bitrate = 20;
bool g_fullscreen = false;
bool g_draggingSlider = false;
bool g_dropdownOpen = false;

typedef void (*ApplyCallback)(int width, int height, int kbps, int fullscreen);
ApplyCallback g_apply = nullptr;

struct Res
{
    const wchar_t* label;
    int w;
    int h;
};

const Res kRes[] = {
    { L"1280 × 720", 1280, 720 },
    { L"1600 × 900", 1600, 900 },
    { L"1920 × 1080", 1920, 1080 },
    { L"2560 × 1440", 2560, 1440 },
    { L"3840 × 2160", 3840, 2160 },
};

const int kResCount = 5;

// 设计单位 → 实际像素
int S(int v)
{
    return (int)lround(v * g_scale);
}

// ---------------- 布局（全部基于设计尺寸） ----------------

RECT TitleRect() { return { S(24), S(20), S(400), S(56) }; }

RECT ComboRect()
{
    RECT r = { S(24), S(88), S(344), S(128) };
    return r;
}

RECT DropItemRect(int index)
{
    RECT c = ComboRect();
    RECT r = { c.left, c.bottom + S(4) + index * S(42), c.right, c.bottom + S(4) + (index + 1) * S(42) - S(4) };
    return r;
}

RECT FsPillRect()
{
    RECT r = { S(378), S(94), S(422), S(122) };
    return r;
}

RECT FsTextRect()
{
    RECT r = { S(434), S(90), S(596), S(126) };
    return r;
}

RECT SliderLabelRect()
{
    RECT r = { S(24), S(150), S(344), S(176) };
    return r;
}

RECT SliderRect()
{
    RECT r = { S(24), S(188), S(344), S(196) };
    return r;
}

RECT BitrateTextRect()
{
    RECT r = { S(24), S(206), S(344), S(232) };
    return r;
}

RECT ApplyRect()
{
    RECT r = { S(384), S(340), S(484), S(384) };
    return r;
}

RECT CloseRect()
{
    RECT r = { S(496), S(340), S(596), S(384) };
    return r;
}

bool Hit(const RECT& r, int x, int y)
{
    return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
}

void FillRound(HDC dc, const RECT& r, COLORREF fill, COLORREF border, int radius)
{
    auto brush = CreateSolidBrush(fill);
    auto pen = CreatePen(PS_SOLID, 1, border);
    auto oldBrush = SelectObject(dc, brush);
    auto oldPen = SelectObject(dc, pen);
    RoundRect(dc, r.left, r.top, r.right, r.bottom, radius, radius);
    SelectObject(dc, oldBrush);
    SelectObject(dc, oldPen);
    DeleteObject(brush);
    DeleteObject(pen);
}

HFONT MakeFont(int designHeight, bool bold = false)
{
    return CreateFontW(-S(designHeight), 0, 0, 0, bold ? FW_SEMIBOLD : FW_NORMAL, FALSE, FALSE, FALSE,
                       DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       DEFAULT_PITCH, L"Segoe UI");
}

void Paint(HWND hwnd)
{
    PAINTSTRUCT ps = {};
    auto dc = BeginPaint(hwnd, &ps);

    RECT rc = {};
    GetClientRect(hwnd, &rc);

    // 面板底
    FillRound(dc, rc, RGB(32, 32, 32), RGB(58, 58, 58), S(18));

    SetBkMode(dc, TRANSPARENT);

    auto titleFont = MakeFont(20, true);
    auto textFont = MakeFont(15);
    auto smallFont = MakeFont(13);

    // 标题
    auto oldFont = SelectObject(dc, titleFont);
    SetTextColor(dc, RGB(245, 245, 245));
    {
        RECT tr = TitleRect();
        DrawTextW(dc, L"传输控制", -1, &tr, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
    }

    SelectObject(dc, textFont);
    SetTextColor(dc, RGB(210, 210, 210));

    // 分辨率标签
    {
        RECT lr = SliderLabelRect();
        lr.top = ComboRect().top - S(26);
        lr.bottom = ComboRect().top - S(4);
        DrawTextW(dc, L"分辨率", -1, &lr, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
    }

    // 下拉框
    {
        RECT c = ComboRect();
        FillRound(dc, c, RGB(48, 48, 48), RGB(92, 92, 92), S(8));

        RECT tr = c;
        tr.left += S(14);
        tr.right -= S(40);
        SetTextColor(dc, RGB(235, 235, 235));
        DrawTextW(dc, kRes[g_selectedRes].label, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        RECT ar = { c.right - S(34), c.top, c.right - S(10), c.bottom };
        auto iconFont = MakeFont(12);
        auto oldIcon = SelectObject(dc, iconFont);
        DrawTextW(dc, g_dropdownOpen ? L"\uE70E" : L"\uE70D", -1, &ar,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(dc, oldIcon);
        DeleteObject(iconFont);
        SetTextColor(dc, RGB(210, 210, 210));
    }

    // 全屏：胶囊 + 右侧文字（留足间距，不重叠）
    {
        RECT pill = FsPillRect();
        FillRound(dc, pill, g_fullscreen ? RGB(0, 120, 215) : RGB(70, 70, 70), RGB(96, 96, 96), S(24));

        const int knobR = S(8);
        const int knobCy = (pill.top + pill.bottom) / 2;
        const int knobCx = g_fullscreen ? pill.right - S(16) : pill.left + S(16);
        auto knob = CreateSolidBrush(RGB(245, 245, 245));
        auto oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
        auto oldBrush = SelectObject(dc, knob);
        Ellipse(dc, knobCx - knobR, knobCy - knobR, knobCx + knobR, knobCy + knobR);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(knob);

        RECT tr = FsTextRect();
        DrawTextW(dc, L"全屏", -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }

    // 码率
    {
        RECT lr = SliderLabelRect();
        wchar_t buf[64];
        swprintf_s(buf, L"码率：%d Mbps", g_bitrate);
        DrawTextW(dc, buf, -1, &lr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        RECT s = SliderRect();
        const int trackH = S(6);
        RECT track = { s.left, s.top, s.right, s.top + trackH };
        FillRound(dc, track, RGB(78, 78, 78), RGB(78, 78, 78), trackH);

        const double t = (g_bitrate - 5) / 95.0;
        const int thumbX = s.left + (int)(t * (s.right - s.left));

        RECT filled = { s.left, s.top, thumbX, s.top + trackH };
        FillRound(dc, filled, RGB(0, 120, 215), RGB(0, 120, 215), trackH);

        const int thumbR = S(9);
        const int cy = s.top + trackH / 2;
        auto thumb = CreateSolidBrush(RGB(0, 120, 215));
        auto pen = CreatePen(PS_SOLID, 2, RGB(240, 240, 240));
        auto oldBrush = SelectObject(dc, thumb);
        auto oldPen = SelectObject(dc, pen);
        Ellipse(dc, thumbX - thumbR, cy - thumbR, thumbX + thumbR, cy + thumbR);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(thumb);
        DeleteObject(pen);

        RECT bt = BitrateTextRect();
        SelectObject(dc, smallFont);
        SetTextColor(dc, RGB(170, 170, 170));
        swprintf_s(buf, L"%d Mbps", g_bitrate);
        DrawTextW(dc, buf, -1, &bt, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }

    // 按钮
    SelectObject(dc, textFont);
    {
        RECT a = ApplyRect();
        FillRound(dc, a, RGB(0, 120, 215), RGB(0, 120, 215), S(8));
        SetTextColor(dc, RGB(255, 255, 255));
        DrawTextW(dc, L"应用", -1, &a, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        RECT c = CloseRect();
        FillRound(dc, c, RGB(58, 58, 58), RGB(96, 96, 96), S(8));
        SetTextColor(dc, RGB(235, 235, 235));
        DrawTextW(dc, L"关闭", -1, &c, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }

    // 展开列表
    if (g_dropdownOpen)
    {
        for (int i = 0; i < kResCount; i++)
        {
            RECT r = DropItemRect(i);
            const bool sel = (i == g_selectedRes);
            FillRound(dc, r, sel ? RGB(0, 120, 215) : RGB(44, 44, 44), RGB(80, 80, 80), S(6));

            RECT tr = r;
            tr.left += S(14);
            SetTextColor(dc, RGB(240, 240, 240));
            DrawTextW(dc, kRes[i].label, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        }
        SetTextColor(dc, RGB(210, 210, 210));
    }

    SelectObject(dc, oldFont);
    DeleteObject(titleFont);
    DeleteObject(textFont);
    DeleteObject(smallFont);

    EndPaint(hwnd, &ps);
}

void ApplyAndNotify()
{
    if (g_apply)
    {
        g_apply(kRes[g_selectedRes].w, kRes[g_selectedRes].h, g_bitrate * 1000, g_fullscreen ? 1 : 0);
    }
}

void SetBitrateFromX(int x)
{
    RECT s = SliderRect();
    double t = (double)(x - s.left) / (s.right - s.left);
    if (t < 0) { t = 0; }
    if (t > 1) { t = 1; }
    g_bitrate = 5 + (int)(t * 95);
}

LRESULT CALLBACK PanelProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_PAINT:
        Paint(hwnd);
        return 0;

    case WM_SETCURSOR:
        SetCursor(LoadCursor(nullptr, IDC_ARROW));
        return TRUE;

    case WM_LBUTTONDOWN:
    {
        const int x = GET_X_LPARAM(lp);
        const int y = GET_Y_LPARAM(lp);

        // 列表展开时优先命中列表项
        if (g_dropdownOpen)
        {
            for (int i = 0; i < kResCount; i++)
            {
                if (Hit(DropItemRect(i), x, y))
                {
                    g_selectedRes = i;
                    g_dropdownOpen = false;
                    InvalidateRect(hwnd, nullptr, TRUE);
                    return 0;
                }
            }

            g_dropdownOpen = false;
            InvalidateRect(hwnd, nullptr, TRUE);
            return 0;
        }

        if (Hit(ComboRect(), x, y))
        {
            g_dropdownOpen = true;
            InvalidateRect(hwnd, nullptr, TRUE);
            return 0;
        }

        if (Hit(FsPillRect(), x, y) || Hit(FsTextRect(), x, y))
        {
            g_fullscreen = !g_fullscreen;
            InvalidateRect(hwnd, nullptr, TRUE);
            return 0;
        }

        {
            RECT s = SliderRect();
            RECT hit = { s.left - S(10), s.top - S(12), s.right + S(10), s.top + S(24) };
            if (Hit(hit, x, y))
            {
                g_draggingSlider = true;
                SetCapture(hwnd);
                SetBitrateFromX(x);
                InvalidateRect(hwnd, nullptr, TRUE);
                return 0;
            }
        }

        if (Hit(ApplyRect(), x, y))
        {
            ApplyAndNotify();
            return 0;
        }

        if (Hit(CloseRect(), x, y))
        {
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }

        return 0;
    }

    case WM_MOUSEMOVE:
        if (g_draggingSlider)
        {
            SetBitrateFromX(GET_X_LPARAM(lp));
            InvalidateRect(hwnd, nullptr, TRUE);
            return 0;
        }
        break;

    case WM_LBUTTONUP:
        if (g_draggingSlider)
        {
            g_draggingSlider = false;
            ReleaseCapture();
            return 0;
        }
        break;

    case WM_TIMER:
        if (wp == 1 && g_targetW > 0)
        {
            g_animTick++;
            double k = g_animTick / 8.0;
            if (k > 1.0) { k = 1.0; }
            double e = 1.0 - (1.0 - k) * (1.0 - k);
            double f = 0.94 + 0.06 * e;

            int w = (int)(g_targetW * f);
            int h = (int)(g_targetH * f);
            int cx = g_targetRect.left + (g_targetW - w) / 2;
            int cy = g_targetRect.top + (g_targetH - h) / 2;

            SetLayeredWindowAttributes(hwnd, 0, (BYTE)(255 * e), LWA_ALPHA);
            SetWindowPos(hwnd, HWND_TOP, cx, cy, w, h, SWP_NOACTIVATE);

            if (g_animTick >= 8)
            {
                KillTimer(hwnd, 1);
                SetWindowPos(hwnd, HWND_TOP, g_targetRect.left, g_targetRect.top,
                             g_targetW, g_targetH, SWP_NOACTIVATE);
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            }

            InvalidateRect(hwnd, nullptr, TRUE);
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

}  // namespace

extern "C" __declspec(dllexport) void PNL_SetApplyCallback(void* callback)
{
    g_apply = (ApplyCallback)callback;
}

extern "C" __declspec(dllexport) void PNL_SetState(int width, int height, int kbps, int fullscreen)
{
    for (int i = 0; i < kResCount; i++)
    {
        if (kRes[i].w == width && kRes[i].h == height)
        {
            g_selectedRes = i;
            break;
        }
    }

    if (kbps > 0)
    {
        g_bitrate = kbps / 1000;
        if (g_bitrate < 5) { g_bitrate = 5; }
        if (g_bitrate > 100) { g_bitrate = 100; }
    }

    g_fullscreen = fullscreen != 0;
    g_dropdownOpen = false;
}

// 尺寸按父窗口的 72% 取，最小 620×420，最大 2 倍
extern "C" __declspec(dllexport) void PNL_Show(void* parent, int width, int height)
{
    g_parent = (HWND)parent;
    if (!g_parent)
    {
        return;
    }

    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = PanelProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hbrBackground = nullptr;
        wc.lpszClassName = L"MoonlightControlPanel";
        RegisterClassExW(&wc);
        registered = true;
    }

    if (!g_panel)
    {
        g_panel = CreateWindowExW(WS_EX_LAYERED, L"MoonlightControlPanel", L"", WS_CHILD,
                                  0, 0, 10, 10, g_parent, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!g_panel)
        {
            return;
        }
    }

    RECT client = {};
    GetClientRect(g_parent, &client);

    if (width <= 0 || height <= 0)
    {
        int w = (int)(client.right * 0.72);
        int h = (int)(client.bottom * 0.72);
        if (w < kDesignW) { w = kDesignW; }
        if (h < kDesignH) { h = kDesignH; }
        width = w;
        height = h;
    }

    g_width = width;
    g_height = height;
    {
        double s = (double)width / kDesignW;
        if (s > kMaxScale) { s = kMaxScale; }
        g_scale = s;
        width = (int)(kDesignW * s);
        height = (int)(kDesignH * s);
    }

    auto rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, S(18), S(18));
    SetWindowRgn(g_panel, rgn, TRUE);

    g_targetW = width;
    g_targetH = height;
    g_targetRect.left = (client.right - width) / 2;
    g_targetRect.top = (client.bottom - height) / 2;
    g_targetRect.right = g_targetRect.left + width;
    g_targetRect.bottom = g_targetRect.top + height;

    SetLayeredWindowAttributes(g_panel, 0, 0, LWA_ALPHA);
    SetWindowPos(g_panel, HWND_TOP, g_targetRect.left, g_targetRect.top, width, height,
                 SWP_NOACTIVATE | SWP_SHOWWINDOW);

    g_animTick = 0;
    SetTimer(g_panel, 1, 12, nullptr);
    InvalidateRect(g_panel, nullptr, TRUE);
}

extern "C" __declspec(dllexport) void PNL_Center(void)
{
    if (!g_panel || !g_parent || !IsWindowVisible(g_panel))
    {
        return;
    }

    RECT client = {};
    GetClientRect(g_parent, &client);
    SetWindowPos(g_panel, HWND_TOP, (client.right - g_width) / 2, (client.bottom - g_height) / 2,
                 g_width, g_height, SWP_NOACTIVATE);
}

extern "C" __declspec(dllexport) void PNL_Hide(void)
{
    if (g_panel)
    {
        ShowWindow(g_panel, SW_HIDE);
    }
}

extern "C" __declspec(dllexport) int PNL_IsVisible(void)
{
    return g_panel && IsWindowVisible(g_panel) ? 1 : 0;
}
static volatile LONG g_openRequest = 0;

extern "C" __declspec(dllexport) void PNL_RequestOpen(void)
{
    InterlockedExchange(&g_openRequest, 1);
}

extern "C" __declspec(dllexport) int PNL_TakeOpenRequest(void)
{
    return InterlockedExchange(&g_openRequest, 0) != 0 ? 1 : 0;
}