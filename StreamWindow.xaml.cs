using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MoonlightWinUI;

/// <summary>
/// 独立串流窗口：D3D11 视频面、键鼠/指针锁定、状态栏都在这里。
/// 由 <see cref="MainWindow"/> 在「演示流 / 连接桌面」时创建；关窗即收流（MS_Stop + ML_Close）。
/// </summary>
public sealed partial class StreamWindow : Window
{
    private readonly string _host;
    private readonly bool _demo;
    private readonly StreamSettings _settings;
    private readonly int _appId;

    // 绝对鼠标的虚拟光标（流画面像素坐标）
    private double _absX;
    private double _absY;
    private int _fittedStreamW;
    private int _fittedStreamH;
    private int _absW;
    private int _absH;
    private bool _absReady;
    private int _absLogCount;
    private NativeMouse.POINT _lastCursorPhys;
    private readonly string? _appTitle;
    private VideoSurface? _video;

    /// <summary>当前连接的主机（主窗口「重新配对/重连」时要用）。</summary>
    public string Host => _host;

    public bool IsDemo => _demo;

    public int AppId => _appId;

    public string? AppTitle => _appTitle;

    public bool QuitAfterStream => _settings.QuitAfterStream;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _videoFrames;
    private double _lastVideoReport = -1;
    private bool _isActive = true;
    private bool _autoLockPending;

    public StreamWindow(string host, bool demo, StreamSettings settings, int appId = 1, string? appTitle = null)
    {
        InitializeComponent();

        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "env.log"), $"ML_CHILDVIDEO={Environment.GetEnvironmentVariable("ML_CHILDVIDEO")}\n"); } catch { }

        ChildDiag($"ctor: useChildVideo={_useChildVideo}");

        _host = host;
        _demo = demo;
        _settings = settings.Clone();
        StreamSettings.Saved += OnSettingsSaved;
        _appId = appId;
        _appTitle = appTitle;
        _autoLockPending = _settings.AutoPointerLock;

        // 和主界面同一套窗口材质 + 自绘标题栏（内容延伸到标题栏，材质才铺得满）
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        UpdateTitle();

        RootGrid.Loaded += (_, _) =>
        {
            ApplyWindowMode();
            FitVideoSurface();
            PositionOverlayButton();

            // 拦 Win 键（XAML 收不到它）
            _streamHwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var subOk = WindowSubclass.InstallForAllChildren(_streamHwnd, OnStreamSetCursor) > 0;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"), $"{DateTime.Now:HH:mm:ss} subclass install hwnd={_streamHwnd} ok={subOk}\n"); } catch { }
            NativeMouse.CursorOwnerWindow = _streamHwnd;
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange)
                {
                    DispatcherQueue.TryEnqueue(UpdateChildVideoRect);
                }
            };

            NativeKeyboardHook.ShouldSwallowKey = OnStreamKeyHook;
            NativeKeyboardHook.Install();

            // 测试钩子：ML_SHOWCONTROL=1 时自动展开控制浮层（方便截图/验证布局）
            if (Environment.GetEnvironmentVariable("ML_SHOWCONTROL") == "1")
            {
                ApplyDock(peek: false);
                ToggleControlPanel();
            }
        };
        // 试验：用反射设置 ProtectedCursor（Q&A 里说的客户端区域方案）
        try
        {
            var prop = typeof(Microsoft.UI.Xaml.UIElement).GetProperty(
                "ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (prop is not null)
            {
                var cursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.IBeam);
                prop.SetValue(RootGrid, cursor);
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"),
                    $"{DateTime.Now:HH:mm:ss} ProtectedCursor set via reflection: {prop.Name}\n");
            }
            else
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"),
                    $"{DateTime.Now:HH:mm:ss} ProtectedCursor property not found\n");
            }
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"),
                $"{DateTime.Now:HH:mm:ss} ProtectedCursor failed: {ex.GetType().Name} {ex.Message}\n");
        }
        InitOverlayBehavior();
        OverlayCanvas.SizeChanged += (_, _) => PositionOverlayButton();

        // 设置里的「显示性能信息」：控制顶部浮窗
        PerfOverlay.Visibility = _settings.ShowPerformanceOverlay ? Visibility.Visible : Visibility.Collapsed;
        ApplyPerfOverlayStyle();
        if (_settings.ShowPerformanceOverlay)
        {
            _perfTimer.Interval = TimeSpan.FromMilliseconds(500);
            _perfTimer.Tick += PerfTimer_Tick;
            _perfTimer.Start();
        }

        // 设置里的「保持屏幕常亮」
        if (_settings.KeepAwake)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        }

        VideoArea.SizeChanged += (_, _) => FitVideoSurface();
        VideoPanel.SizeChanged += OnVideoPanelSizeChanged;
        CompositionTarget.Rendering += OnRendering;

        // 渲染兜底：Win32 串流模式下这个 WinUI 窗口是隐藏的（AppWindow.Hide），
        // 一旦 XAML 合成器停摆（窗口被遮挡/最小化/系统降频），CompositionTarget.Rendering
        // 就不再送帧，原生视频窗口会停在最后一帧 —— 表现就是"画面卡住"，而应用本身还活着。
        // 这里用定时器看着：Rendering 超过 40ms 没来就自己驱动一帧。
        _renderWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _renderWatchdog.Tick += (_, _) =>
        {
            if (_video is null)
            {
                return;
            }
            if (Environment.TickCount64 - _lastCompositionTickMs < 40)
            {
                return;   // Rendering 正常，不插手
            }
            RenderTick();
        };
        _renderWatchdog.Start();

        StartClipboardSync();

        // 渲染泵心跳：UI 线程要是 3 秒没跑过 RenderTick（被原生调用阻塞之类），
        // 从后台线程记一行日志，用来区分"解码卡住"和"UI 线程被卡住"。
        _lastRenderTickMs = Environment.TickCount64;
        StartRenderHeartbeat();

        // 切走窗口就解锁：不然指针一直藏着，用户会以为鼠标坏了
        Activated += (_, e) =>
        {
            _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
            if (!_isActive)
            {
                ExitPointerLock("window deactivated");
                return;
            }

            // 回到窗口（比如关掉开始菜单/别的窗口）：绝对模式先把主机光标对到本地指针上
            if (_settings.AbsoluteMouse)
            {
                DispatcherQueue.TryEnqueue(SyncAbsoluteToSystemCursor);
            }
        };

        // 关窗先解锁（恢复系统指针），再 MS_Stop / ML_Close，别把主机会话挂着
        Closed += (_, _) =>
        {
            _closed = true;
            ExitPointerLock("window closed");
            _perfTimer.Stop();
            _perfTimer.Tick -= PerfTimer_Tick;
            WindowSubclass.Uninstall();
            NativeKeyboardHook.ShouldSwallowKey = null;
            NativeKeyboardHook.Uninstall();
            _video?.SendKey(NativeKeyboardHook.VkLeftWin, false);
            _video?.SendKey(NativeKeyboardHook.VkRightWin, false);
            _cursorTimer.Stop();
            // 性能卡片定时器（500ms）以前没停：关窗后 _video 被 Dispose 并置 null，
            // 它的下一拍就 _video.GetPerf() → NullReferenceException → 闪退。
            // "退出串流 / 退出并结束串流"必现就是这个原因。
            _perfCardTimer?.Stop();
            _connectTimeoutTimer?.Stop();
            _peekTimer.Stop();
            StreamSettings.Saved -= OnSettingsSaved;
            _renderWatchdog?.Stop();
            _clipboardTimer?.Stop();
            _connWatch?.Stop();
            _pointerTimer?.Stop();
            NativeMouse.RestoreSystemCursor();
            _gamepad?.Dispose();
            _gamepad = null;
            _video?.Dispose();
            _video = null;
            SetThreadExecutionState(EsContinuous);   // 解除防休眠

            // 控制面板是隐藏窗口（SafeClose 里 AppWindow.Hide），不显式关掉的话
            // WinUI 认为还有活窗口 —— 关主窗口后进程会赖在后台，只能去任务管理器结束。
            try { _controlWindow?.Close(); } catch { }
            _controlWindow = null;
        };
    }

    // ---- 性能浮窗：每 0.5s 取一次 native 的 6 个指标 ----
    // ---- 悬浮按钮：贴边停靠，不用时半隐藏 ----
    private bool _overlayPlaced;
    private bool _draggingButton;
    private bool _buttonMoved;
    private Windows.Foundation.Point _dragStartPoint;
    private double _dragStartLeft;
    private double _dragStartTop;
    private string _dockSide = "right";
    private double _dockFraction = 0.08;
    private bool _peeked = true;
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    private const double DockMargin = 6;
    private const double PeekVisible = 12;

    private void InitOverlayBehavior()
    {
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (!_draggingButton && ControlPanel.Visibility != Visibility.Visible)
            {
                ApplyDock(peek: true);
            }
        };

        ControlButton.PointerEntered += (_, _) =>
        {
            _peekTimer.Stop();
            ApplyDock(peek: false);
        };

        ControlButton.PointerExited += (_, _) =>
        {
            if (!_draggingButton && ControlPanel.Visibility != Visibility.Visible)
            {
                _peekTimer.Start();
            }
        };

        ControlPanel.SizeChanged += (_, _) =>
        {
            if (ControlPanel.Visibility == Visibility.Visible)
            {
                PositionControlPanel();
            }
        };
    }

    private void SchedulePeek()
    {
        if (!_draggingButton && ControlPanel.Visibility != Visibility.Visible)
        {
            _peekTimer.Start();
        }
    }

    private void PositionOverlayButton()
    {
        if (OverlayCanvas.ActualWidth < 2 || OverlayCanvas.ActualHeight < 2)
        {
            return;
        }

        _overlayPlaced = true;
        ApplyDock(_peeked);
    }

    /// <summary>把按钮摆到当前停靠边上；peek = true 时只露出一小条。</summary>
    private void ApplyDock(bool peek)
    {
        var canvasW = OverlayCanvas.ActualWidth;
        var canvasH = OverlayCanvas.ActualHeight;
        if (canvasW < 2 || canvasH < 2)
        {
            return;
        }

        var w = ControlButton.Width;
        var h = ControlButton.Height;
        _peeked = peek;
        _dockFraction = Math.Clamp(_dockFraction, 0, 1);

        double left;
        double top;
        switch (_dockSide)
        {
            case "left":
                left = peek ? PeekVisible - w : DockMargin;
                top = DockMargin + _dockFraction * Math.Max(0, canvasH - h - DockMargin * 2);
                break;
            case "top":
                top = peek ? PeekVisible - h : DockMargin;
                left = DockMargin + _dockFraction * Math.Max(0, canvasW - w - DockMargin * 2);
                break;
            case "bottom":
                top = peek ? canvasH - PeekVisible : canvasH - h - DockMargin;
                left = DockMargin + _dockFraction * Math.Max(0, canvasW - w - DockMargin * 2);
                break;
            default:
                left = peek ? canvasW - PeekVisible : canvasW - w - DockMargin;
                top = DockMargin + _dockFraction * Math.Max(0, canvasH - h - DockMargin * 2);
                break;
        }

        Canvas.SetLeft(ControlButton, left);
        Canvas.SetTop(ControlButton, top);

        if (ControlPanel.Visibility == Visibility.Visible)
        {
            PositionControlPanel();
        }
    }

    /// <summary>松手后吸附到最近的一条边，并记住沿边位置。</summary>
    private void SnapToNearestEdge()
    {
        var canvasW = OverlayCanvas.ActualWidth;
        var canvasH = OverlayCanvas.ActualHeight;
        var w = ControlButton.Width;
        var h = ControlButton.Height;
        var left = Canvas.GetLeft(ControlButton);
        var top = Canvas.GetTop(ControlButton);
        if (double.IsNaN(left)) { left = 0; }
        if (double.IsNaN(top)) { top = 0; }

        var dLeft = left;
        var dRight = canvasW - w - left;
        var dTop = top;
        var dBottom = canvasH - h - top;

        var min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
        if (min == dLeft)
        {
            _dockSide = "left";
            _dockFraction = FractionOf(top, canvasH - h);
        }
        else if (min == dRight)
        {
            _dockSide = "right";
            _dockFraction = FractionOf(top, canvasH - h);
        }
        else if (min == dTop)
        {
            _dockSide = "top";
            _dockFraction = FractionOf(left, canvasW - w);
        }
        else
        {
            _dockSide = "bottom";
            _dockFraction = FractionOf(left, canvasW - w);
        }

        ApplyDock(peek: false);
        SchedulePeek();
    }

    private static double FractionOf(double value, double range)
        => range <= 1 ? 0 : Math.Clamp(value / range, 0, 1);

    private void ControlButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 只认「左键按住」拖动：鼠标不是左键按下、或者触摸/笔，都不开始拖
        var point = e.GetCurrentPoint(OverlayCanvas);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _peekTimer.Stop();
        ApplyDock(peek: false);

        _draggingButton = true;
        _buttonMoved = false;
        _dragStartPoint = e.GetCurrentPoint(OverlayCanvas).Position;
        _dragStartLeft = Canvas.GetLeft(ControlButton);
        _dragStartTop = Canvas.GetTop(ControlButton);
        if (double.IsNaN(_dragStartLeft)) { _dragStartLeft = 0; }
        if (double.IsNaN(_dragStartTop)) { _dragStartTop = 0; }
        ControlButton.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ControlButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingButton)
        {
            return;
        }

        var p = e.GetCurrentPoint(OverlayCanvas).Position;
        var dx = p.X - _dragStartPoint.X;
        var dy = p.Y - _dragStartPoint.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 4)
        {
            _buttonMoved = true;
        }

        var canvasW = OverlayCanvas.ActualWidth;
        var canvasH = OverlayCanvas.ActualHeight;
        var left = Math.Clamp(_dragStartLeft + dx, 0, Math.Max(0, canvasW - ControlButton.Width));
        var top = Math.Clamp(_dragStartTop + dy, 0, Math.Max(0, canvasH - ControlButton.Height));
        Canvas.SetLeft(ControlButton, left);
        Canvas.SetTop(ControlButton, top);

        if (ControlPanel.Visibility == Visibility.Visible)
        {
            PositionControlPanel();
        }

        e.Handled = true;
    }

    private void ControlButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ControlButton.ReleasePointerCapture(e.Pointer);
        _draggingButton = false;

        if (_buttonMoved)
        {
            // 拖过 = 重新停靠到最近的边
            SnapToNearestEdge();
        }
        else
        {
            ToggleControlPanel();
        }

        e.Handled = true;
    }

    private void ControlButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _draggingButton = false;
    }

    private void PositionControlPanel()
    {
        var canvasW = OverlayCanvas.ActualWidth;
        var canvasH = OverlayCanvas.ActualHeight;
        if (canvasW < 2 || canvasH < 2)
        {
            return;
        }

        // 浮层宽度不能超出画面区，否则右半边会被裁掉
        ControlPanel.MaxWidth = Math.Max(200, canvasW - 24);

        var panelW = ControlPanel.ActualWidth > 0
            ? ControlPanel.ActualWidth
            : Math.Min(ControlPanel.Width, ControlPanel.MaxWidth);
        var panelH = ControlPanel.ActualHeight > 0 ? ControlPanel.ActualHeight : 320;

        var buttonLeft = Canvas.GetLeft(ControlButton);
        var buttonTop = Canvas.GetTop(ControlButton);
        if (double.IsNaN(buttonLeft)) { buttonLeft = canvasW - ControlButton.Width - DockMargin; }
        if (double.IsNaN(buttonTop)) { buttonTop = DockMargin; }

        double left;
        double top;

        if (_dockSide == "left")
        {
            left = buttonLeft + ControlButton.Width + 8;
            top = buttonTop;
        }
        else
        {
            left = buttonLeft + ControlButton.Width - panelW;
            top = buttonTop + ControlButton.Height + 8;
        }

        if (left + panelW > canvasW)
        {
            left = canvasW - panelW;
        }

        if (top + panelH > canvasH)
        {
            top = buttonTop - panelH - 8;
        }

        Canvas.SetLeft(ControlPanel, Math.Clamp(left, 0, Math.Max(0, canvasW - panelW)));
        Canvas.SetTop(ControlPanel, Math.Clamp(top, 0, Math.Max(0, canvasH - panelH)));
    }
    /// <summary>悬浮按钮：切换传输控制浮层（拖动过就不算点击）。</summary>
    private ControlWindow? _controlWindow;
    // ---- 断线自动重连 ----
    private DispatcherTimer? _connWatch;
    private int _reconnectAttempts;
    private bool _reconnecting;
    private bool _wasConnected;
    private bool _gaveUpReconnect;
    private const int MaxReconnectAttempts = 3;

    private DispatcherTimer? _panelFollowTimer;   // 面板打开期间跟随串流窗口移动
    private int _panelW, _panelH;                 // 面板打开时的尺寸（跟随重定位要复用）
    private (int L, int T, int W, int H) _panelAnchor;

    private void ToggleControlPanel()
    {
        if (_useChildVideo)
        {
            // Win32 弹窗模式：齿轮直接弹出原生菜单（分辨率/码率/全屏），不再开窗口
            ShowGearMenu();
            return;
        }

        if (ControlPanel.Visibility != Visibility.Visible)
        {
            SyncControlsFromSettings();
            _peekTimer.Stop();
            ApplyDock(peek: false);

            ControlPanel.Visibility = Visibility.Visible;
            PositionControlPanel();
            // 布局完成后再算一次，避免第一次打开时尺寸还是 0 导致位置偏掉
            DispatcherQueue.TryEnqueue(PositionControlPanel);
            return;
        }

        ControlPanel.Visibility = Visibility.Collapsed;
    }

    private Flyout? _gearFlyout;
    private ComboBox? _menuResolution;
    private Slider? _menuBitrate;
    private TextBlock? _menuBitrateText;
    private ToggleSwitch? _menuFullScreen;

    /// <summary>原生弹窗「应用」回调（由 native 以函数指针调用）。</summary>
    private void OnPanelApply(int width, int height, int kbps, int fullscreen)
    {
        ApplyStreamSettings(width, height, kbps, fullscreen != 0);
    }
    /// <summary>用 WinUI 的 ControlWindow 做串流页弹窗（Fluent 外观），中心对齐串流窗口。</summary>
    /// <summary>
    /// 面板跟随：面板现在只在"串流窗口失焦"时才收起，所以串流窗口移动/缩放时
    /// 必须让它跟着重新居中，否则会飘在原地。120ms 轮询窗口矩形，变化才动。
    /// </summary>
    private void StartPanelFollow()
    {
        _panelFollowTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };   // 只做兜底
        if (_panelFollowTimer.IsEnabled)
        {
            return;
        }

        _panelFollowTimer.Tick += PanelFollowTick;
        _panelFollowTimer.Start();
    }

    private void StopPanelFollow()
    {
        if (_panelFollowTimer is null)
        {
            return;
        }

        _panelFollowTimer.Stop();
        _panelFollowTimer.Tick -= PanelFollowTick;
    }

    private void PanelFollowTick(object? sender, object e) => RepositionPanelNow();   // 兜底（正常由 native 回调实时触发）

    /// <summary>按串流窗口当前矩形把面板重新居中；窗口没变就直接返回。</summary>
    private void RepositionPanelNow()
    {
        if (_closed || _controlWindow is null || _video is null)
        {
            StopPanelFollow();
            return;
        }

        try
        {
            if (!GetWindowRect(_video.StreamWindowHandle, out var r))
            {
                return;
            }

            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < 80 || h < 80)
            {
                return;   // 最小化/被收起时不动
            }

            if (r.Left == _panelAnchor.L && r.Top == _panelAnchor.T &&
                w == _panelAnchor.W && h == _panelAnchor.H)
            {
                return;   // 没动
            }

            _panelAnchor = (r.Left, r.Top, w, h);

            var menuW = Math.Min(_panelW, Math.Max(320, w - 40));
            var menuH = Math.Min(_panelH, Math.Max(240, h - 40));
            var x = r.Left + (w - menuW) / 2;
            var y = r.Top + (h - menuH) / 2;

            _controlWindow.Reposition(x, y, menuW, menuH);
        }
        catch
        {
            // 跟随失败无所谓，下一拍再试
        }
    }

    private void ShowFluentPanel()
    {
        var scale = VideoHost.XamlRoot?.RasterizationScale ?? 1.0;
        if (_video is not null && GetWindowRect(_video.StreamWindowHandle, out var rect))
        {
            var winW = rect.Right - rect.Left;
            var winH = rect.Bottom - rect.Top;
            var menuW = Math.Min((int)Math.Round(620 * scale), Math.Max(420, winW - 80));
            var menuH = Math.Min((int)Math.Round(420 * scale), Math.Max(320, winH - 80));
            var x = rect.Left + (winW - menuW) / 2;
            var y = rect.Top + (winH - menuH) / 2;

            if (_controlWindow is null)
            {
                _controlWindow = new ControlWindow(_settings, ApplyStreamSettings, _host);
                _controlWindow.QuitStream = () => { try { Close(); } catch { } };
                _controlWindow.QuitStreamAndExit = () => { try { TryEndRemoteApp(); } catch { } try { Close(); } catch { } };
                _controlWindow.Closed += (_, _) => { try { _video?.SetFollowWindow(IntPtr.Zero); } catch { } _controlWindow = null; StopPanelFollow(); };
            }

            _controlWindow.ShowAt(x, y, scale, menuW, menuH);

            _panelW = menuW;
            _panelH = menuH;
            _panelAnchor = (rect.Left, rect.Top, winW, winH);
            StartPanelFollow();   // 面板不再是"失焦即关"，串流窗口移动时它得跟着走
        }
    }
    private void ShowGearMenu()
    {
        // 纯 Win32 串流窗口：用 GDI 自绘的原生控制弹窗
        if (_useWin32Stream && _video is not null)
        {
            _panelApplyDelegate ??= new VideoSurface.PanelApplyDelegate(OnPanelApply);
            _video.SetPanelApplyCallback(_panelApplyDelegate);
            ShowFluentPanel();
            return;
        }
        // 弹窗始终在画面正中：算屏幕坐标（视频弹窗客户区原点 + 居中偏移）
        var scale = VideoHost.XamlRoot?.RasterizationScale ?? 1.0;
        var videoW = (int)Math.Round(VideoHost.ActualWidth * scale);
        var videoH = (int)Math.Round(VideoHost.ActualHeight * scale);

        var origin = default(NativeMouse.POINT);
        ClientToScreen(_streamHwnd, ref origin);

        // 以整个视频区域为基准居中（VideoArea 覆盖视频画面）
        var areaOrigin = VideoArea.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var areaW = (int)Math.Round(VideoArea.ActualWidth * scale);
        var areaH = (int)Math.Round(VideoArea.ActualHeight * scale);
        var menuW = (int)Math.Round(520 * scale);
        var menuH = (int)Math.Round(360 * scale);
        var x = (int)Math.Round(origin.X + areaOrigin.X * scale + (areaW - menuW) / 2.0);
        var y = (int)Math.Round(origin.Y + areaOrigin.Y * scale + (areaH - menuH) / 2.0);

        if (_controlWindow is null)
        {
            _controlWindow = new ControlWindow(_settings, ApplyStreamSettings, _host);
            _controlWindow.Closed += (_, _) => _controlWindow = null;
        }

        _controlWindow.ShowAt(x, y, scale);
    }

    /// <summary>请求主机结束当前运行的应用（Sunshine /cancel），结束后关闭串流窗口。</summary>
    private bool TryEndRemoteApp() => HostApi.CancelRunningApp(_host, 47984);

    /// <summary>码率即时生效：走 Sunshine 基地版的 /bitrate（同一会话，不重连）。</summary>
    private bool TryChangeBitrateOnHost(int kbps)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var ok = NativeTls.TryGet(
                _host, 47984,
                $"/bitrate?bitrate={kbps}&clientname=MoonlightWinUI",
                System.IO.Path.Combine(baseDir, "creds", "client.crt"),
                System.IO.Path.Combine(baseDir, "creds", "client.key"),
                out var body, out var error);

            StreamText.Text = ok ? $"码率已切换：{kbps / 1000.0:F0} Mbps" : $"改码率失败：{error}";
            return ok && body.Contains("status_code=\"200\"");
        }
        catch (Exception ex)
        {
            StreamText.Text = "改码率失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>分辨率：基地版要求走控制流协议，HTTP 会返回 400 → 返回 false 由调用方回退重连。</summary>
    private bool TryChangeResolutionOnHost(int width, int height)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var ok = NativeTls.TryGet(
                _host, 47984,
                $"/stream/settings?type=0&value={width}x{height}&clientname=MoonlightWinUI",
                System.IO.Path.Combine(baseDir, "creds", "client.crt"),
                System.IO.Path.Combine(baseDir, "creds", "client.key"),
                out var body, out _);

            return ok && body.Contains("status_code=\"200\"");
        }
        catch
        {
            return false;
        }
    }

    private void ControlClose_Click(object sender, RoutedEventArgs e)
    {
        ControlPanel.Visibility = Visibility.Collapsed;
        if (_useChildVideo)
        {
            _video?.ShowChildVideo(true);
            _video?.SetHudVisible(true);
        }
        SchedulePeek();
    }

    private void UpdateFullScreenButton()
    {
        if (ControlFullScreenButton is not null)
        {
            ControlFullScreenButton.Content = _settings.FullScreen ? "窗口化" : "全屏";
        }
    }

    /// <summary>全屏/窗口切换按钮：点一下立刻切，不等「应用」。</summary>
    private void ControlFullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.FullScreen = !_settings.FullScreen;
        _settings.Save();
        ApplyWindowMode();
        UpdateFullScreenButton();

        // 全屏切换后窗口尺寸变了，重新摆一下控制浮层
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ControlPanel.Visibility == Visibility.Visible)
            {
                PositionControlPanel();
            }
        });
    }
    private void ControlBitrate_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ControlBitrateText is not null)
        {
            ControlBitrateText.Text = $"{e.NewValue:F0} Mbps";
        }
    }
    private void SyncControlsFromSettings()
    {
        if (ControlResolution.Items.Count == 0)
        {
            foreach (var r in StreamSettings.Resolutions)
            {
                ControlResolution.Items.Add(new ComboBoxItem { Content = $"{r.Width} × {r.Height}", Tag = r });
            }
        }

        var current = Array.FindIndex(StreamSettings.Resolutions, r => r.Width == _settings.Width && r.Height == _settings.Height);
        ControlResolution.SelectedIndex = current >= 0 ? current : 2;
        ControlBitrate.Value = Math.Clamp(_settings.BitrateKbps / 1000.0, 5, 100);
        ControlBitrateText.Text = $"{ControlBitrate.Value:F0} Mbps";
        UpdateFullScreenButton();
        ControlStatusText.Text = string.Empty;
    }
    private async void ControlApply_Click(object sender, RoutedEventArgs e)
    {
        var wantWidth = _settings.Width;
        var wantHeight = _settings.Height;
        if (ControlResolution.SelectedItem is ComboBoxItem item && item.Tag is ValueTuple<int, int> res)
        {
            wantWidth = res.Item1;
            wantHeight = res.Item2;
        }

        var wantBitrate = (int)Math.Round(ControlBitrate.Value) * 1000;
        ControlStatusText.Text = string.Empty;
        ApplyStreamSettings(wantWidth, wantHeight, wantBitrate, _settings.FullScreen);

        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>应用分辨率/码率/全屏（控制面板和独立控制窗口共用）。</summary>
    internal void ApplyStreamSettings(int wantWidth, int wantHeight, int wantBitrate, bool wantFullScreen)
    {

        // 兜底：0/负 的分辨率一律按「本机分辨率」处理，绝不能让 0x0 流进设置和请求
        if (wantWidth <= 0 || wantHeight <= 0)
        {
            var (nativeWidth, nativeHeight) = ScreenInfo.PrimaryPixels();
            wantWidth = wantWidth > 0 ? wantWidth : nativeWidth;
            wantHeight = wantHeight > 0 ? wantHeight : nativeHeight;
            ChildDiag($"resolution: 收到非法值，按本机 {wantWidth}x{wantHeight} 处理");
        }

        var resolutionChanged = wantWidth != _settings.Width || wantHeight != _settings.Height;
        var bitrateChanged = wantBitrate != _settings.BitrateKbps;
        var fullScreenChanged = wantFullScreen != _settings.FullScreen;

        if (fullScreenChanged)
        {
            _settings.FullScreen = wantFullScreen;
            _settings.Save();
            ApplyWindowMode();
        }

        // 1) 码率：即时生效，不重连
        if (bitrateChanged)
        {
            _settings.BitrateKbps = wantBitrate;
            _settings.Save();
            TryChangeBitrateOnHost(wantBitrate);
        }

        // 2) 分辨率：先试基地版的 HTTP 接口；不支持就走重连（控制流协议我们还没接）
        if (resolutionChanged)
        {
            _settings.Width = wantWidth;
            _settings.Height = wantHeight;
            _settings.Save();

            // 优先走 Sunshine 控制流协议（0x5507）：主机支持时即时生效、不重连
            var resRc = _video is null ? -1 : _video.RequestResolution(wantWidth, wantHeight);
            if (resRc == 0)
            {
                UpdateTitle();
                _video?.SetVideoAspect(wantWidth, wantHeight);
                ChildDiag($"resolution: control-stream ok {wantWidth}x{wantHeight}");
            }
            else
            {
                // 老版本主机不支持 → 回退重连
                UpdateTitle();
                ChildDiag($"resolution: control-stream rc={resRc}, fallback restart");
                _ = RestartStreamAsync();
            }
        }
        else if (!bitrateChanged && !fullScreenChanged)
        {
            ControlStatusText.Text = "设置已保存";
        }
    }

    private async System.Threading.Tasks.Task RestartStreamAsync()
    {
        if (_demo || _video is null)
        {
            return;
        }

        ShowStatus("正在按新参数重连…");
        _video.StopStream();
        await System.Threading.Tasks.Task.Delay(400);

        var rc = _video.StartStream(
            _host, _settings.Width, _settings.Height, _settings.Fps, _settings.BitrateKbps,
            _appId, _settings.Codec);

        // 重连会把主机侧的光标模式重置成默认值（视频内合成），必须重新申请一次本地光标，
        // 否则画面里会重新出现"远程光标"
        _cursorModeApplied = false;
        _cursorModeAttempts = 0;
        _appliedCursorShape = -1;
        _localCursorHidden = false;

        if (rc == 0)
        {
            _ = DispatcherQueue.TryEnqueue(ApplyRemoteCursorMode);
        }

        StreamText.Text = rc == 0
            ? $"已按新参数重连：{_settings.Width}×{_settings.Height} · {_settings.BitrateKbps / 1000.0:F0} Mbps…"
            : $"MS_Start 失败：{rc}";
    }

    /// <summary>把设置里的透明度 / 位置应用到性能浮窗。</summary>
    private bool _cursorModeApplied;
    private int _cursorModeAttempts;
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int _appliedCursorShape = -1;

    /// <summary>跟随主机光标：可见性 + 形状（主机在本地光标模式下推 LI_CURSOR_UPDATE）。</summary>
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _cursorBitmap;
    private int _cursorTickLog;

    /// <summary>主机推来的 BGRA 像素 → WriteableBitmap（尺寸变了就重建）。</summary>
    private void UpdateCursorBitmap(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return;
        }

        if (_cursorBitmap is null || _cursorBitmap.PixelWidth != width || _cursorBitmap.PixelHeight != height)
        {
            _cursorBitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
            CursorImage.Width = width;
            CursorImage.Height = height;
        }

        using (var stream = _cursorBitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, System.IO.SeekOrigin.Begin);
            stream.Write(bgra, 0, width * height * 4);
        }

        _cursorBitmap.Invalidate();
        CursorImage.Source = _cursorBitmap;
    }

    /// <summary>光标跟着主机：形状用主机推的位图自己画，系统指针藏起来（Parsec 式）。</summary>
    private Microsoft.UI.Input.InputSystemCursorShape _cursorSystemShape =
        Microsoft.UI.Input.InputSystemCursorShape.Arrow;

    /// <summary>从主机位图猜一个最接近的系统光标形状（WinUI 只支持内置形状）。</summary>


    private readonly DispatcherTimer _cursorVisibilityTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _localCursorHidden;

    /// <summary>只同步「可见性」：主机光标不可见时把本地指针隐藏，可见时恢复（形状不管）。</summary>
    private void CursorVisibility_Tick(object? sender, object e)
    {
        if (_video is null)
        {
            return;
        }

        var state = _video.GetCursorState();
        if (!state.Visible)
        {
            if (!_localCursorHidden)
            {
                _localCursorHidden = true;
                // 计数可能被 XAML 加过，循环压到 -1
                while (NativeMouse.ShowCursor(false) >= 0) { }
            }
        }
        else if (_localCursorHidden)
        {
            _localCursorHidden = false;
            while (NativeMouse.ShowCursor(true) < 0) { }
        }
    }
    private void CursorTimer_Tick(object? sender, object e)
    {
        if (!_cursorModeApplied || _video is null)
        {
            return;
        }

        var state = _video.GetCursorState();

        if (!state.Visible)
        {
            NativeMouse.HideStreamCursor();
            return;
        }

        if (state.ShapeId != _appliedCursorShape)
        {
            var pixels = _video.GetCursorShape();
            if (pixels is not null &&
                NativeMouse.SetStreamCursor(pixels, state.Width, state.Height, state.HotX, state.HotY))
            {
                _appliedCursorShape = state.ShapeId;
            }
        }
        else
        {
            NativeMouse.ReapplyStreamCursor();
        }
    }
    /// <summary>连接成功后把光标模式切到「本地渲染」：主机不再把光标画进视频（Parsec 式）。</summary>
    /// <summary>低级键盘钩子回调：Win 键本地吞掉、转发给主机（否则会弹本地开始菜单、主机会卡住 Win）。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativeMouse.POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out WinRect rect);

    private int _setCursorCalls;
    private bool _cursorLocalMode;
    private bool _firstFrameShown;
    private GamepadInput? _gamepad;
    private long _lastIdrTick;
    private int _stallTicks;
    private DispatcherTimer? _perfCardTimer;
    private bool _closed;   // StreamWindow 已关闭：所有定时器回调必须提前退出
    private DispatcherTimer? _renderWatchdog;   // Rendering 停摆时的兜底驱动
    private long _lastCompositionTickMs;        // 只由 CompositionTarget.Rendering 更新
    private long _lastRenderTickMs;             // RenderTick 心跳（跨线程用 Interlocked）
    private long _lastPumpWarnMs;               // 渲染泵停摆日志节流
    private DispatcherTimer? _clipboardTimer;        // 轮询主机剪贴板
    private bool _clipboardHooked;
    private bool _suppressClipboardSend;
    private string _lastClipboardText = string.Empty;
    private int _clipboardChangeToken;

    /// <summary>按设置刷新性能卡片（500ms）。</summary>
    private void StartPerfCard()
    {
        if (_video is null)
        {
            return;
        }

        var pos = _settings.ShowPerformanceOverlay ? Math.Clamp(_settings.OverlayPosition, 0, 5) : 5;
        if (!_settings.ShowPerformanceOverlay)
        {
            _video.PerfUpdate(Array.Empty<string>(), true);
            return;
        }

        _video.PerfInit(_video.StreamWindowHandle);
        _video.PerfConfig(pos, _settings.OverlayItems, _settings.OverlayCornerSingleLine, _settings.OverlayOpacity);

        _perfCardTimer?.Stop();
        _perfCardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _perfCardTimer.Tick += (_, _) =>
        {
            // 关窗后 _video 会被 Dispose/置空，这一拍必须自己退出（定时器里抛异常 = 闪退）
            if (_closed || _video is null)
            {
                _perfCardTimer?.Stop();
                return;
            }

            try
            {
                var p = _video.GetPerf();
                var single = pos == 1 || pos == 4 || _settings.OverlayCornerSingleLine;
                var lines = new List<string>(6);

                if ((_settings.OverlayItems & 1) != 0) { lines.Add($"{p.Fps:F0} fps"); }
                if ((_settings.OverlayItems & 2) != 0)
                {
                    var (vc, hw) = _video.GetVideoInfo();
                    var name = vc switch { 1 => "H264", 2 => "HEVC", 3 => "AV1", _ => _settings.Codec == 2 ? "HEVC" : _settings.Codec == 1 ? "H264" : "AV1" };
                    lines.Add(hw ? name : "CPU");
                }

                if ((_settings.OverlayItems & 4) != 0) { lines.Add($"编码/解码：{p.EncodeMs:F1} ms/{p.DecodeMs:F1} ms"); }
                if ((_settings.OverlayItems & 8) != 0) { lines.Add($"网络：{p.RttMs:F1} ms"); }
                if ((_settings.OverlayItems & 16) != 0) { lines.Add($"丢包：{p.LossPercent:F1}%"); }
                if ((_settings.OverlayItems & 32) != 0) { lines.Add($"{p.BitrateKbps / 1000.0:F1} Mbps"); }

                _video.PerfUpdate(lines.ToArray(), single);
            }
            catch
            {
                // 定时器回调里绝不能把异常抛出去
                _perfCardTimer?.Stop();
            }
        };
        _perfCardTimer.Start();

        // 手柄输入（P1）+ 震动（P2）
        if (_gamepad is null && _video is not null)
        {
            _gamepad = new GamepadInput(_video);
            _gamepad.Start();
        }
    }

    private bool _testResDone;
    private VideoSurface.PanelApplyDelegate? _panelApplyDelegate;

    private bool _hudGearInitialized;
    private int _hudLastW;

    private IntPtr _streamHwnd;
    // 默认用 Win32 弹窗渲染（光标可控、HUD 可用）；设 ML_CHILDVIDEO=0 退回纯 WinUI3
    private readonly bool _useChildVideo = Environment.GetEnvironmentVariable("ML_CHILDVIDEO") != "0";

    // 纯 Win32 串流窗口（Mica 背景）；设 ML_WIN32_STREAM=0 退回原来的弹窗方案
    private readonly bool _useWin32Stream = Environment.GetEnvironmentVariable("ML_WIN32_STREAM") != "0";

    private bool _childVideoCreated;

    /// <summary>WM_SETCURSOR 回调：把主机推来的光标形状/可见性应用到系统指针。</summary>
    private bool OnStreamSetCursor(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WindowSubclass.WmSetCursor || _video is null)
        {
            return false;
        }

        var state = _video.GetCursorState();
        if (!state.Visible)
        {
            NativeMouse.HideStreamCursor();
            return true;
        }

        if (state.ShapeId != _appliedCursorShape)
        {
            var pixels = _video.GetCursorShape();
            if (pixels is not null &&
                NativeMouse.SetStreamCursor(pixels, state.Width, state.Height, state.HotX, state.HotY))
            {
                _appliedCursorShape = state.ShapeId;
            }
        }
        else
        {
            NativeMouse.ReapplyStreamCursor();
        }

        return true;
    }
    /// <summary>启动"游戏内自动锁定"检测（250ms）。</summary>
    private void StartPointerAutoLock()
    {
        _pointerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        if (_pointerTimer.IsEnabled)
        {
            return;
        }

        _pointerTimer.Tick += PointerAutoLock_Tick;
        _pointerTimer.Start();
    }

    private void PointerAutoLock_Tick(object? sender, object e)
    {
        if (_closed || _demo || _video is null)
        {
            _pointerTimer?.Stop();
            return;
        }

        var state = _video.GetCursorState();

        // 诊断：每 2 秒一刀
        {
            var nowTick = Environment.TickCount64;
            if (nowTick - _lastCursorDiagTick > 2000)
            {
                _lastCursorDiagTick = nowTick;
                ChildDiag($"cursorTick visible={state.Visible} locked={_pointerLocked} applied={_cursorModeApplied}");
            }
        }

        // 主机不画光标 → 游戏内（游戏把指针藏了）→ 锁定：相对位移 + 夹在画面内 + 隐藏本地光标
        // 主机画光标   → 桌面 / 有 UI 的应用 → 解锁，鼠标自由移到串流窗口外
        if (!state.Visible && !_pointerLocked)
        {
            if (TryEnterPointerLock(force: false, gameAuto: true))
            {
                ChildDiag("pointerLock=1（主机光标隐藏 → 游戏内，自动锁定）");
            }
        }
        else if (state.Visible && _pointerLocked && !_pointerLockForced)
        {
            ExitPointerLock("host cursor visible（桌面/UI）");
        }
    }

    private bool OnStreamKeyHook(int vk, bool isDown)
    {
        if (_video is null || !_isActive)
        {
            return false;
        }

        if (vk is NativeKeyboardHook.VkLeftWin or NativeKeyboardHook.VkRightWin)
        {
            _video.SendKey(vk, isDown);
            return true;
        }

        return false;
    }
    private void ApplyRemoteCursorMode()
    {
        if (_cursorModeApplied || _demo || _video is null)
        {
            return;
        }

        var features = _video.HostFeatures;
        if ((features & 0x40) == 0)
        {
            // 主机不支持 LI_FF_CURSOR_SHAPE：只能继续用视频里的光标
            return;
        }

        // 原生窗口模式一律用本地光标：零延迟，形状由 WM_SETCURSOR 同步
        var wantLocal = _useChildVideo || (_settings.AbsoluteMouse && _settings.HideRemoteCursor);
        _cursorLocalMode = wantLocal;
        var rc = _video.SetCursorMode(wantLocal ? 1 : 0);
        _cursorModeAttempts++;
        if (rc == 0 && wantLocal)
        {
            _cursorModeApplied = true;

            // 可见性同步（两种模式都做）；形状同步只有原生弹窗模式能做
            // 可见性同步已停用（ShowCursor 会造成指针闪烁）
            // _cursorVisibilityTimer.Tick += CursorVisibility_Tick;
            // _cursorVisibilityTimer.Start();

            // Win32 窗口模式：形状同步交给视频窗口自己的 WM_SETCURSOR。
            // 这个 33ms 定时器是从 UI 线程 SetCursor 的，全局生效 —— 会连带主窗口一起闪。
            if (_useChildVideo && !_useWin32Stream)
            {
                _cursorTimer.Tick += CursorTimer_Tick;
                _cursorTimer.Start();
            }

            // 游戏内自动锁定：Win32 窗口模式下 _cursorTimer 不启动，
            // 所以单独一个 timer（250ms），所有模式都跑。
            StartPointerAutoLock();

            // 3 秒后记一笔：主机到底有没有推光标更新（诊断用）
            DispatcherQueue.TryEnqueue(async () =>
            {
                await System.Threading.Tasks.Task.Delay(3000);
                if (_video is null)
                {
                    return;
                }

                var state = _video.GetCursorState();
                try
                {
                    var pixels = _video.GetCursorShape();
                    var alpha = 0;
                    if (pixels is not null)
                    {
                        for (var k = 3; k < pixels.Length; k += 4)
                        {
                            if (pixels[k] != 0) { alpha++; }
                        }
                    }

                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "cursor-diag.log"),
                        $"{DateTime.Now:HH:mm:ss} updates={_video.CursorUpdateCount} visible={state.Visible} shape={state.ShapeId} {state.Width}x{state.Height} bytes={pixels?.Length ?? 0} alpha={alpha} hot={state.HotX},{state.HotY}\n");
                }
                catch
                {
                }
            });
        }
        else if (rc != 0)
        {
            if (_cursorModeAttempts >= 5)
            {
                _cursorModeApplied = true;
                StreamText.Text = $"光标模式切换失败：{rc}";
            }
        }
        else
        {
            _cursorModeApplied = true;   // 显式要"视频内合成"，切一次就够
        }
    }
    // ---------------- 剪贴板同步（Sunshine 0x5508） ----------------
    // 主机 → 客户端：控制流回调入队，这里 700ms 轮询一次，写系统剪贴板。
    // 客户端 → 主机：Clipboard.ContentChanged 触发时发送。两边都记住最后处理的文本，避免回声。
    /// <summary>设置保存后实时跟随：材质下拉框一改，正在串流的窗口立刻换材质。</summary>
    private void OnSettingsSaved(StreamSettings saved)
    {
        if (_closed)
        {
            return;
        }

        if (saved.BackdropKind == _settings.BackdropKind)
        {
            return;
        }

        _settings.BackdropKind = saved.BackdropKind;

        try
        {
            _video?.SetBackdrop(saved.BackdropKind);
            ChildDiag($"backdrop → {saved.BackdropKind}");
        }
        catch
        {
        }
    }

    // ---------------- 断线自动重连 ----------------
    // 远端退出 Sunshine / 网络中断 → StreamStats().Connected 变 false。
    // 连续重连 MaxReconnectAttempts 次仍失败 → 关闭串流并在主窗口弹错误提示。
    private void StartConnectionWatch()
    {
        if (_demo)
        {
            return;
        }

        _connWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        if (!_connWatch.IsEnabled)
        {
            _connWatch.Tick += ConnectionWatchTick;
            _connWatch.Start();
        }
    }

    private async void ConnectionWatchTick(object? sender, object e)
    {
        if (_closed || _demo || _video is null || _reconnecting || _gaveUpReconnect || !_firstFrameShown)
        {
            return;
        }

        bool connected;
        try
        {
            var (_, _, isConnected) = _video.StreamStats();
            connected = isConnected;
        }
        catch
        {
            return;
        }

        if (connected)
        {
            if (_wasConnected && _reconnectAttempts > 0)
            {
                ChildDiag("reconnect: 已恢复连接");
                StreamText.Text = "已重新连接";
            }

            _wasConnected = true;
            _reconnectAttempts = 0;
            return;
        }

        if (!_wasConnected)
        {
            return;   // 还没连上过，不当作断线
        }

        _reconnecting = true;
        try
        {
            _reconnectAttempts++;
            ChildDiag($"reconnect: 第 {_reconnectAttempts} 次尝试");

            if (_reconnectAttempts > MaxReconnectAttempts)
            {
                _gaveUpReconnect = true;
                _connWatch?.Stop();
                ChildDiag("reconnect: 超出次数，结束串流");

                var host = _host;
                var message = $"与 {host} 的连接已中断，自动重连 {MaxReconnectAttempts} 次仍未成功，串流已结束。\n"
                            + "请检查主机是否退出 Sunshine、网络是否可达，然后重新连接。";

                try { Close(); } catch { }
                var main = MainWindow.Instance;
                if (main is not null)
                {
                    await main.ShowErrorAsync("串流连接中断", message);
                }
                return;
            }

            ShowStatus($"连接中断，正在重连（{_reconnectAttempts}/{MaxReconnectAttempts}）…");
            _video.StopStream();
            await System.Threading.Tasks.Task.Delay(1200);

            if (_closed || _video is null)
            {
                return;
            }

            var rc = _video.StartStream(
                _host, _settings.Width, _settings.Height, _settings.Fps, _settings.BitrateKbps,
                _appId, _settings.Codec);

            ChildDiag($"reconnect: StartStream rc={rc}");

            // 重连后主机的光标模式会重置，重新申请本地光标
            _cursorModeApplied = false;
            _cursorModeAttempts = 0;
            _appliedCursorShape = -1;
            _localCursorHidden = false;
            if (rc == 0)
            {
                DispatcherQueue.TryEnqueue(ApplyRemoteCursorMode);
            }
        }
        catch (Exception ex)
        {
            ChildDiag($"reconnect: 异常 {ex.Message}");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void StartClipboardSync()
    {
        if (_clipboardHooked)
        {
            return;
        }

        _clipboardHooked = true;

        try { Clipboard.ContentChanged += Clipboard_ContentChanged; } catch { }

        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _clipboardTimer.Tick += (_, _) => PollHostClipboard();
        _clipboardTimer.Start();
    }

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        if (_suppressClipboardSend || _video is null)
        {
            return;
        }

        var token = ++_clipboardChangeToken;
        _ = DebouncedSendClipboardAsync(token);
    }


    /// <summary>复制时 ContentChanged 常常连发好几次，等 150ms 只处理最后一次。</summary>
    private async Task DebouncedSendClipboardAsync(int token)
    {
        await Task.Delay(150);
        if (token != _clipboardChangeToken)
        {
            return;
        }

        await SendLocalClipboardAsync();
    }

    private async Task SendLocalClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content is null)
            {
                return;
            }


            // 图片优先（复制截图时剪贴板里通常同时有位图和一段文本描述）
            if (_settings.ClipboardImageSync && content.Contains(StandardDataFormats.Bitmap))
            {
                var png = await EncodeClipboardImagePngAsync(content);
                if (png is { Length: > 0 })
                {
                    if (png.Length > 65000)
                    {
                        // 超过控制帧上限：先传 HTTPS blob，再发 kind=3 引用
                        await UploadClipboardImageAsync(png);
                    }
                    else
                    {
                        var imageRc = _video?.SendClipboardPng(png) ?? -1;
                        ChildDiag($"clipboard → host image {png.Length} bytes rc={imageRc}");
                    }
                    return;
                }
            }

            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text) || text == _lastClipboardText)
            {
                return;
            }

            _lastClipboardText = text;
            var rc = _video?.SendClipboardText(text) ?? -1;
            ChildDiag($"clipboard → host {text.Length} chars rc={rc}");
        }
        catch
        {
            // 剪贴板被别的程序占着之类的，忽略
        }
    }

    /// <summary>把剪贴板里的位图编码成 PNG（失败返回 null）。</summary>
    private static async Task<byte[]?> EncodeClipboardImagePngAsync(DataPackageView content)
    {
        try
        {
            var reference = await content.GetBitmapAsync();
            using var input = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);
            using var software = await decoder.GetSoftwareBitmapAsync();

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(software);
            await encoder.FlushAsync();

            output.Seek(0);
            var bytes = new byte[output.Size];
            await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把主机推来的 PNG 写进系统剪贴板。</summary>
    private async Task SetClipboardImageAsync(byte[] png)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));

            _suppressClipboardSend = true;
            try
            {
                Clipboard.SetContent(package);
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => _suppressClipboardSend = false);
            }
        }
        catch
        {
        }
    }

    /// <summary>大图：先 POST 到主机的 /api/v1/clipboard/blob，再发 kind=3 引用。</summary>
    private async Task UploadClipboardImageAsync(byte[] png)
    {
        try
        {
            var creds = System.IO.Path.Combine(AppContext.BaseDirectory, "creds");
            var cert = System.IO.Path.Combine(creds, "client.crt");
            var key = System.IO.Path.Combine(creds, "client.key");
            var host = _host;

            var (id, error) = await Task.Run(() =>
            {
                var ok = NativeTls.TryUploadBlob(host, 47984, "image/png", png, cert, key, out var blobId, out var err);
                return ok ? (Id: blobId, Error: string.Empty) : (Id: string.Empty, Error: err);
            });

            if (string.IsNullOrEmpty(id))
            {
                ChildDiag($"clipboard → host image 上传失败：{error}");
                return;
            }

            var json = $"{{\"id\":\"{id}\",\"mime\":\"image/png\",\"size\":{png.Length}}}";
            var rc = _video?.SendClipboardRef(json) ?? -1;
            ChildDiag($"clipboard → host image {png.Length} bytes via blob id={id} rc={rc}");
        }
        catch (Exception ex)
        {
            ChildDiag($"clipboard → host image 上传异常：{ex.Message}");
        }
    }

    /// <summary>收到 kind=3 引用：按 id 从 HTTPS blob 下载，图片就写进系统剪贴板。</summary>
    private async Task DownloadClipboardRefAsync(string json)
    {
        try
        {
            var id = NativeTls.ExtractJsonString(json, "id");
            var mime = NativeTls.ExtractJsonString(json, "mime");
            if (string.IsNullOrEmpty(id))
            {
                ChildDiag($"clipboard ← host ref 里没有 id：{json}");
                return;
            }

            var creds = System.IO.Path.Combine(AppContext.BaseDirectory, "creds");
            var cert = System.IO.Path.Combine(creds, "client.crt");
            var key = System.IO.Path.Combine(creds, "client.key");
            var host = _host;

            var (bytes, error) = await Task.Run(() =>
            {
                var ok = NativeTls.TryDownloadBlob(host, 47984, id, cert, key, out var data, out var err);
                return ok ? (Bytes: data, Error: string.Empty) : (Bytes: Array.Empty<byte>(), Error: err);
            });

            if (bytes.Length == 0)
            {
                ChildDiag($"clipboard ← host ref 下载失败：{error}");
                return;
            }

            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                if (!_settings.ClipboardImageSync)
                {
                    return;
                }

                await SetClipboardImageAsync(bytes);
                ChildDiag($"clipboard ← host image {bytes.Length} bytes (blob id={id})");
                return;
            }

            ChildDiag($"clipboard ← host ref mime={mime}（文件同步已移除，忽略 {bytes.Length} 字节）");
        }
        catch (Exception ex)
        {
            ChildDiag($"clipboard ← host ref 异常：{ex.Message}");
        }
    }

    private async void PollHostClipboard()
    {
        try
        {
            var item = _video.PollClipboard();
            if (item is null)
            {
                return;
            }

            var (kind, payload) = item.Value;
            if (payload.Length == 0)
            {
                return;
            }

            if (kind == 2)
            {
                // PNG 图片：设置里关掉就直接丢掉
                if (!_settings.ClipboardImageSync)
                {
                    return;
                }

                await SetClipboardImageAsync(payload);
                ChildDiag($"clipboard ← host image {payload.Length} bytes");
                return;
            }
            if (kind == 3)
            {
                // 图片/文件各自的开关在 DownloadClipboardRefAsync 里按 mime 判断
                await DownloadClipboardRefAsync(System.Text.Encoding.UTF8.GetString(payload));
                return;
            }

            if (kind != 1)
            {
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(payload);
            if (string.IsNullOrEmpty(text) || text == _lastClipboardText)
            {
                return;
            }

            _lastClipboardText = text;

            // 写系统剪贴板：期间抑制回发，避免"主机→本地→又发回主机"的回声
            _suppressClipboardSend = true;
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => _suppressClipboardSend = false);
            }

            ChildDiag($"clipboard ← host {text.Length} chars");
        }
        catch
        {
        }
    }


    private static void ChildDiag(string text)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "childdiag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {text}\n");
        }
        catch
        {
        }
    }
    /// <summary>把原生子窗口摆到视频矩形上（含信箱留边）。</summary>
    private void UpdateChildVideoRect()
    {
        ChildDiag($"UpdateChildVideoRect: use={_useChildVideo} created={_childVideoCreated} host={VideoHost.ActualWidth:F0}x{VideoHost.ActualHeight:F0}");
        if (!_useChildVideo || VideoHost.ActualWidth < 2 || VideoHost.ActualHeight < 2)
        {
            return;
        }

        // 纯 Win32 串流窗口：视频由它自己承载，不需要摆位置
        if (_useWin32Stream)
        {
            if (_childVideoCreated)
            {
                return;
            }

            // 0,0 = 由原生按主屏工作区（物理像素）的 70% 计算
            var winW = 0;
            var winH = 0;
            _video ??= VideoSurface.CreateWin32Stream();
            var rc2 = _video.CreateStreamWindow($"Moonlight 串流 - {_host}", winW, winH);
            ChildDiag($"CreateStreamWindow rc={rc2}");

            if (rc2 == 0)
            {
                _video.SetBackdrop(_settings.BackdropKind);   // 串流窗口材质跟主窗口一致
                _video.SetMoveCallback(RepositionPanelNow);   // 窗口一移动就实时挪面板
                StartPerfCard();   // 性能信息卡片（原生分层窗口，设置里可配位置/项目）
            }

            if (rc2 == 0)
            {
                _childVideoCreated = true;
                AppWindow.Hide();          // 串流窗口改成纯 Win32，WinUI 窗口收起来
                StartChildStream();
            }
            else
            {
                StreamText.Text = $"Win32 串流窗口创建失败：{rc2}";
            }

            return;
        }

        var scale = VideoHost.XamlRoot?.RasterizationScale ?? 1.0;
        var origin = VideoHost.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var pt = new NativeMouse.POINT
        {
            X = (int)Math.Round(origin.X * scale),
            Y = (int)Math.Round(origin.Y * scale),
        };
        ClientToScreen(_streamHwnd, ref pt);

        var x = pt.X;
        var y = pt.Y;
        var w = (int)Math.Round(VideoHost.ActualWidth * scale);
        var h = (int)Math.Round(VideoHost.ActualHeight * scale);

        if (!_childVideoCreated)
        {
            _video = VideoSurface.CreateChildWindow(_streamHwnd, x, y, w, h);
            _childVideoCreated = _video is not null;
            if (!_childVideoCreated)
            {
                StreamText.Text = "原生子窗口创建失败";
                return;
            }

            StartChildStream();
            return;
        }

        _video?.MoveChild(x, y, w, h);
    }

    /// <summary>连接步骤文字：直接画在 HUD 层（视频之上），居中出现。</summary>
    private void ShowConnectStatus(string text)
    {
        if (!_useWin32Stream || _video is null)
        {
            StreamText.Text = text;
            return;
        }

        var scale = VideoHost.XamlRoot?.RasterizationScale ?? 1.0;
        var w = Math.Max(200, (int)Math.Round(VideoHost.ActualWidth * scale));
        var h = Math.Max(120, (int)Math.Round(VideoHost.ActualHeight * scale));
        var x = Math.Max(16, (w - text.Length * 14) / 2);
        _video.SetHudText(text, 1.0, x, Math.Max(16, h / 2));
        _video.SetHudGear(0, 0, false);
    }

    private void HideConnectStatus()
    {
        if (_useWin32Stream && _video is not null)
        {
            _video.SetHudText(string.Empty, 1.0, 0, 0);
            _video.SetHudGear(0, 0, false);
        }
    }
    private DispatcherTimer? _connectTimeoutTimer;

    /// <summary>连接超时：到点还没首帧就提示并关掉串流窗口。</summary>
    private void StartConnectTimeout()
    {
        _connectTimeoutTimer?.Stop();
        _connectTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _connectTimeoutTimer.Tick += (_, _) =>
        {
            _connectTimeoutTimer?.Stop();
            if (_video is null || _childVideoCreated == false)
            {
                return;
            }

            // 已经出画面就不算超时
            if (_video.GetPerf().Fps > 0)
            {
                return;
            }

            _video.ShowWindowError("连接超时", "20 秒内没有收到视频画面，请检查主机是否在线或换一台试试。");
            Close();
        };
        _connectTimeoutTimer.Start();
    }
    private void StartChildStream()
    {
        if (_video is null)
        {
            return;
        }

        if (_demo)
        {
            // 原生子窗口模式也要能播演示流：直接把本地 H.264 当网络包喂进去，
            // 否则窗口里只有背景色（0 fps），本地就没法验证"面板遮挡视频"这类问题。
            var demoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "test-1080p60.h264");
            var started = _video.StartDemoFile(demoPath, 60.0);
            StreamText.Text = started ? "演示流（原生子窗口）" : $"演示流启动失败：{demoPath}";
            return;
        }

        var rc = _video.StartStream(
            _host, _settings.Width, _settings.Height, _settings.Fps, _settings.BitrateKbps,
            _appId, _settings.Codec);

        if (_useWin32Stream)
        {
            // 告诉原生窗口流的宽高比，窗口缩放时做 letterbox 而不是拉伸
            _video.SetVideoAspect(_settings.Width, _settings.Height);
            _video.SetWindowStatus(rc == 0
                ? "正在建立视频会话…"
                : $"启动失败：MS_Start 返回 {rc}");
            StartConnectTimeout();
        }

        StreamText.Text = rc == 0
            ? $"连接 {_host} · {_appTitle ?? "appid " + _appId}（{_settings.Width}×{_settings.Height}@{_settings.Fps}）…"
            : $"MS_Start 失败：{rc}";
    }
    private void ApplyPerfOverlayStyle()
    {
        PerfOverlay.Opacity = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);

        switch (Math.Clamp(_settings.OverlayPosition, 0, 5))
        {
            case 0:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Top;
                PerfOverlay.Margin = new Thickness(12, 12, 0, 0);
                break;
            case 1:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Center;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Top;
                PerfOverlay.Margin = new Thickness(0, 12, 0, 0);
                break;
            case 2:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Right;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Top;
                PerfOverlay.Margin = new Thickness(0, 12, 12, 0);
                break;
            case 3:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                PerfOverlay.Margin = new Thickness(12, 0, 0, 12);
                break;
            case 4:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Center;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                PerfOverlay.Margin = new Thickness(0, 0, 0, 12);
                break;
            default:
                PerfOverlay.HorizontalAlignment = HorizontalAlignment.Right;
                PerfOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                PerfOverlay.Margin = new Thickness(0, 0, 12, 12);
                break;
        }
    }
    private readonly DispatcherTimer _perfTimer = new();

    /// <summary>性能浮窗：0.5 s 刷新一次 fps / 编解码 / 网络 / 丢包 / 码率。</summary>
    private void PerfTimer_Tick(object? sender, object e)
    {
        if (_video is null)
        {
            return;
        }

        var p = _video.GetPerf();
        PerfFps.Text = $"{p.Fps:F0} fps";

        var decoder = _video.DecoderInfo;
        PerfCodec.Text = p.EncodeMs > 0
            ? $"{decoder} · 编解码 {p.EncodeMs:F1} / {p.DecodeMs:F1} ms"
            : $"{decoder} · 编解码 — / {p.DecodeMs:F1} ms";

        PerfRtt.Text = $"网络 {p.RttMs:F1} ms";
        PerfLoss.Text = $"丢包 {p.LossPercent:F1}%";
        PerfBitrate.Text = $"{p.BitrateKbps / 1000.0:F1} Mbps";

        if (_useChildVideo)
        {
            PerfOverlay.Visibility = Visibility.Collapsed;   // HUD 已经画在弹窗上了，别再显示一份
        }

        // 诊断钩子：ML_TEST_RES=1920x1080 → 首帧后自动请求一次控制流改分辨率
        if (_useWin32Stream && _childVideoCreated && _video is not null && !_testResDone && p.Fps > 0)
        {
            var spec = Environment.GetEnvironmentVariable("ML_TEST_RES");
            if (!string.IsNullOrWhiteSpace(spec))
            {
                _testResDone = true;
                var parts = spec.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var tw) && int.TryParse(parts[1], out var th))
                {
                    var trc = _video.RequestResolution(tw, th);
                    ChildDiag($"TEST: RequestResolution({tw}x{th}) rc={trc}");
                    if (trc == 0)
                    {
                        _video.SetVideoAspect(tw, th);
                    }
                }
            }
        }

        // 标题栏菜单按钮（native 子窗口）被点击 → 带当前设置打开面板
        if (_useWin32Stream && _childVideoCreated && _video is not null &&
            _video.TakePanelOpenRequest())
        {
            _video.SetPanelApplyCallback(_panelApplyDelegate ??= new VideoSurface.PanelApplyDelegate(OnPanelApply));
            ShowFluentPanel();
        }

        // 纯 Win32 串流窗口：用户点关闭就结束这路串流
        if (_useWin32Stream && _childVideoCreated && _video is not null && p.Fps > 0 && !_firstFrameShown)
        {
            _firstFrameShown = true;
            StartConnectionWatch();   // 首帧之后盯着连接状态，断了自动重连
            _video.SetWindowVideoReady(true);
            _connectTimeoutTimer?.Stop();
        }

        if (_useWin32Stream && _childVideoCreated && _video is not null && _video.IsStreamWindowClosed())
        {
            _video.DestroyStreamWindow();
            _childVideoCreated = false;
            Close();
            return;
        }
        // 子窗口（原生弹窗）模式：HUD 画在视频上
        if (_useChildVideo && _video is not null)
        {
            var text = $"{p.Fps:F0} fps   {decoder}   {p.EncodeMs:F1}/{p.DecodeMs:F1} ms   网络 {p.RttMs:F1} ms   丢包 {p.LossPercent:F1}%   {p.BitrateKbps / 1000.0:F1} Mbps";
            // HUD 坐标用「视频窗口像素」，不是流的像素（否则齿轮会被算到窗口外面）
            var hudScale = VideoHost.XamlRoot?.RasterizationScale ?? 1.0;
            var w = Math.Max(200, (int)Math.Round(VideoHost.ActualWidth * hudScale));
            var h = Math.Max(120, (int)Math.Round(VideoHost.ActualHeight * hudScale));
            var hudX = _settings.OverlayPosition is 0 or 3 ? 16.0 : _settings.OverlayPosition is 2 or 5 ? Math.Max(16.0, w - 620) : 16.0;
            var hudY = _settings.OverlayPosition is 3 or 4 or 5 ? Math.Max(16.0, h - 56) : 14.0;
            _video.SetHudText(text, _settings.OverlayOpacity, hudX, hudY);
            // 齿轮位置只初始化一次；之后由 native 负责拖动吸附和自动隐藏
            if (!_hudGearInitialized)
            {
                _hudGearInitialized = true;
                _hudLastW = w;
                _video.SetHudGear((int)Math.Max(8, w - 56), 12, true);
            }

            if (_video.TakeHudGearClick())
            {
                ToggleControlPanel();
            }
        }

    }
    // SetThreadExecutionState 的常量（保持屏幕常亮用）
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>标题栏文字（也在任务栏显示）：主机 · 应用 · 分辨率@帧率。</summary>
    private void UpdateTitle()
    {
        Title = $"Moonlight 串流 - {_host} · {_appTitle ?? "Desktop"}  {_settings.Width}×{_settings.Height}@{_settings.Fps}";

        if (TitleText is not null)
        {
            TitleText.Text = Title;
        }
    }

    /// <summary>窗口 / 全屏切换；全屏时不要自绘标题栏。</summary>
    private bool _fullscreenApplied;

    private void ApplyWindowMode()
    {
        // 纯 Win32 串流窗口模式：全屏交给原生窗口，不要动 WinUI 的 presenter
        if (_useWin32Stream)
        {
            _video?.SetStreamWindowFullscreen(_settings.FullScreen);
            return;
        }

        if (_settings.FullScreen)
        {
            AppTitleBar.Visibility = Visibility.Collapsed;

            if (!_fullscreenApplied)
            {
                _fullscreenApplied = true;

                // 无边框最大化，而不是独占全屏：独占全屏从屏幕顶部往外拖会触发系统"还原窗口"手势
                if (AppWindow.Presenter is OverlappedPresenter op)
                {
                    op.SetBorderAndTitleBar(false, false);
                    op.Maximize();
                }
                else
                {
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
            }

            DispatcherQueue.TryEnqueue(FitVideoSurface);
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateChildVideoRect();

                if (ControlPanel.Visibility == Visibility.Visible)
                {
                    PositionControlPanel();
                }
            });
            return;
        }

        AppTitleBar.Visibility = Visibility.Visible;

        if (_fullscreenApplied)
        {
            _fullscreenApplied = false;

            if (AppWindow.Presenter is OverlappedPresenter op2)
            {
                op2.SetBorderAndTitleBar(true, false);
                if (op2.State == OverlappedPresenterState.Maximized)
                {
                    op2.Restore();
                }
            }
            else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            }
        }

        ResizeToWorkArea();
        DispatcherQueue.TryEnqueue(UpdateChildVideoRect);
    }
    private void ResizeToWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;

        // 视频区按所选分辨率的宽高比开窗；底部状态栏大约 70 DIP，算进客户区
        var aspect = Math.Max(0.5, (double)_settings.Width / _settings.Height);
        var titleHeight = (int)Math.Round(36 * scale);
        var statusHeight = (int)Math.Round(70 * scale);
        var maxVideoWidth = Math.Min(1500, work.Width - 80);
        var maxVideoHeight = work.Height - 80 - statusHeight - titleHeight;
        var videoWidth = Math.Min(maxVideoWidth, (int)Math.Round(maxVideoHeight * aspect));
        var videoHeight = (int)Math.Round(videoWidth / aspect);

        var clientWidth = videoWidth;
        var clientHeight = videoHeight + statusHeight + titleHeight;

        AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - clientWidth) / 2,
            work.Y + (work.Height - clientHeight) / 2));
    }

    // ---------------- 视频面（D3D11 → SwapChainPanel） ----------------

    /// <summary>视频面按流的宽高比等比缩放，居中；多余部分留给窗口背景（不再拉伸变形）。</summary>
    private void FitVideoSurface()
    {
        var areaW = VideoArea.ActualWidth;
        var areaH = VideoArea.ActualHeight;
        if (areaW < 2 || areaH < 2)
        {
            return;
        }

        var streamW = _video?.StreamWidth ?? 0;
        var streamH = _video?.StreamHeight ?? 0;
        if (streamW <= 0 || streamH <= 0)
        {
            streamW = Math.Max(1, _settings.Width);
            streamH = Math.Max(1, _settings.Height);
        }

        var aspect = (double)streamW / streamH;
        double width, height;
        if (areaW / areaH > aspect)
        {
            height = areaH;
            width = areaH * aspect;
        }
        else
        {
            width = areaW;
            height = areaW / aspect;
        }

        VideoHost.Width = Math.Round(width);
        VideoHost.Height = Math.Round(height);

        if (_useChildVideo)
        {
            DispatcherQueue.TryEnqueue(UpdateChildVideoRect);
        }
    }
    private void OnVideoPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 2 || e.NewSize.Height < 2)
        {
            return;
        }

        if (_pointerLocked)
        {
            // 面板尺寸变了，夹取矩形得跟着更新，否则指针会跑到画面外
            var rect = PanelScreenRect();
            NativeMouse.ClipCursorTo(ref rect);
            CenterCursor(rect);
            _lastPointerPos = new Windows.Foundation.Point(e.NewSize.Width / 2, e.NewSize.Height / 2);
            _awaitCenter = true;
        }

        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var w = (int)(e.NewSize.Width * scale);
        var h = (int)(e.NewSize.Height * scale);

        if (_video is not null)
        {
            _video.Resize(w, h);
            return;
        }

        if (_useChildVideo)
        {
            ChildDiag($"OnVideoPanelSizeChanged(child): video={( _video is not null)} size={e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
            UpdateChildVideoRect();
            return;
        }

        try
        {
            _video = new VideoSurface(VideoPanel, w, h, System.IO.Path.Combine(AppContext.BaseDirectory, "test-1080p60.h264"));

            if (_demo)
            {
                StreamText.Text = "演示流（本地 1080p60）";
            }
            else
            {
                var rc = _video.StartStream(
                    _host, _settings.Width, _settings.Height, _settings.Fps, _settings.BitrateKbps,
                    _appId, _settings.Codec);
                var codecName = _settings.Codec switch
                {
                    1 => "H.264",
                    2 => "HEVC",
                    3 => "AV1",
                    _ => "自动",
                };
                StreamText.Text = rc == 0
                    ? $"连接 {_host} · {_appTitle ?? "appid " + _appId}（{_settings.Width}×{_settings.Height}@{_settings.Fps}，{_settings.BitrateKbps / 1000.0:F0} Mbps，编码 {codecName}）…"
                    : $"MS_Start 失败：{rc}";
            }
        }
        catch (Exception ex)
        {
            StreamText.Text = "视频面初始化失败：" + ex.Message;
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "video-error.txt"),
                    ex.ToString());
            }
            catch
            {
            }
        }
    }

    /// <summary>在画面正中显示连接/状态文字（并让转圈重新转起来）。</summary>
    private void ShowStatus(string text)
    {
        StreamText.Text = text;
        StatusOverlay.Visibility = Visibility.Visible;
        ConnectRing.IsActive = true;
    }

    private void HideStatus()
    {
        StatusOverlay.Visibility = Visibility.Collapsed;
        ConnectRing.IsActive = false;
    }

    /// <summary>渲染泵心跳：UI 线程 3 秒没跑过 RenderTick 就记一行日志（区分"解码卡"和"UI 线程被卡"）。</summary>
    private void StartRenderHeartbeat()
    {
        var thread = new System.Threading.Thread(() =>
        {
            while (!_closed)
            {
                System.Threading.Thread.Sleep(1000);

                if (_closed || _video is null) { continue; }

                var idle = Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastRenderTickMs);
                if (idle < 3000 || idle > 600000) { continue; }
                if (Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastPumpWarnMs) < 5000) { continue; }

                System.Threading.Interlocked.Exchange(ref _lastPumpWarnMs, Environment.TickCount64);
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "render-heartbeat.log"),
                        $"{DateTime.Now:HH:mm:ss.fff} !! 渲染泵停摆 {idle} ms（UI 线程被阻塞？）{Environment.NewLine}");
                }
                catch { }
            }
        })
        { IsBackground = true, Name = "render-heartbeat" };

        thread.Start();
    }
    private void OnRendering(object? sender, object e)
    {
        _lastCompositionTickMs = Environment.TickCount64;
        RenderTick();
    }

    private void RenderTick()
    {
        if (_video is null)
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref _lastRenderTickMs, Environment.TickCount64);

        var t0 = _clock.Elapsed.TotalMilliseconds;
        try
        {
            _video.Render();
        }
        catch (Exception ex)
        {
            ShowStatus("渲染失败：" + ex.Message);
            _video = null;
            return;
        }

        var cost = _clock.Elapsed.TotalMilliseconds - t0;
        _videoFrames++;

        var now = _clock.Elapsed.TotalSeconds;
        if (_lastVideoReport < 0 || now - _lastVideoReport >= 1.0)
        {
            var fps = _lastVideoReport < 0 ? 0 : _videoFrames / (now - _lastVideoReport);
            _ = fps;
            _videoFrames = 0;
            _lastVideoReport = now;

            var (streamState, streamStage) = _video.StreamStatus();

            // 流分辨率已知或变了：按新宽高比重新摆放视频面
            var knownW = _video.StreamWidth;
            var knownH = _video.StreamHeight;
            if (knownW > 0 && knownH > 0 && (knownW != _fittedStreamW || knownH != _fittedStreamH))
            {
                _fittedStreamW = knownW;
                _fittedStreamH = knownH;
                FitVideoSurface();
            }

            if (streamState >= 2)
            {
                ApplyRemoteCursorMode();
            }

            // 首帧上屏后把中间的「准备中」浮层收掉
            if (StatusOverlay.Visibility == Visibility.Visible &&
                _video.DecodedFrames > 0 &&
                (streamState == 2 || _demo))
            {
                HideStatus();
            }

            if (_pointerLocked && !_pointerLockForced && streamState != 2)
            {
                ExitPointerLock("stream ended");
            }

            // 设置里开了「连接后自动锁定指针」：真正开流后锁一次，用户手动释放后不再抢
            if (_autoLockPending && !_pointerLocked && streamState == 2 && _isActive)
            {
                if (TryEnterPointerLock(force: false))
                {
                    _autoLockPending = false;
                }
            }

            if (streamState != 0)
            {
                var (packets, bytes, _) = _video.StreamStats();
                var (rate, channels, played, underruns) = _video.AudioStats();
                var audioText = channels > 0
                    ? $"音频 {rate / 1000.0:F1}k/{channels}ch 已播 {played / (double)Math.Max(1, rate):F1}s 欠载 {underruns}"
                    : "音频未就绪";
                StreamText.Text = $"{streamStage}（{packets} 包 / {bytes / 1048576.0:F1} MB · {audioText}）";
            }

            RunSelfTest();
            RunPointerLockSelfTest();
        }
    }

    // ---------------- 指针锁定（relative capture） ----------------

    private const string PointerHintDefault = "点一下画面：给焦点并锁定指针（Ctrl+Alt+Shift+Q 释放）";
    private const string PointerHintLocked = "指针已锁定 · Ctrl+Alt+Shift+Q 释放，或切走窗口";

    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<int> _mouseButtonsDown = new();
    private Windows.Foundation.Point _lastPointerPos;
    private bool _pointerTracking;
    private bool _pointerLocked;
    private long _lastCursorDiagTick;   // cursorTick 诊断节流
    private DispatcherTimer? _pointerTimer;   // 游戏内自动锁定检测（所有窗口模式都要跑）
    private bool _pointerLockForced;
    private int _hideSteps;
    private NativeMouse.POINT _cursorSavePos;
    private bool _awaitCenter;
    private int _lockTestStage;
    private readonly System.Diagnostics.Stopwatch _lockTestClock = new();

    private void VideoPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 点画面先把传输控制浮层收起来（这一下不发给主机）
        if (ControlPanel.Visibility == Visibility.Visible)
        {
            ControlPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (_video is null)
        {
            return;
        }

        // WinUI 的键盘事件只发给有焦点的元素：点一下画面顺便把焦点拿过来
        VideoPanel.Focus(FocusState.Pointer);
        VideoPanel.CapturePointer(e.Pointer);

        var point = e.GetCurrentPoint(VideoPanel);
        _lastPointerPos = point.Position;
        _pointerTracking = true;

        if (point.Properties.IsLeftButtonPressed) { SendMouseButton(1, true); }
        if (point.Properties.IsMiddleButtonPressed) { SendMouseButton(2, true); }
        if (point.Properties.IsRightButtonPressed) { SendMouseButton(3, true); }

        // 左键点画面 = 进入相对捕获；演示流没有主机可发，只当取焦点。
        // gameAuto 让绝对鼠标设置不再挡住它 —— 窗口化游戏里主机光标是可见的，
        // 自动判定进不了锁定，这时候点一下画面就能切到相对模式。
        if (point.Properties.IsLeftButtonPressed && !_pointerLocked)
        {
            TryEnterPointerLock(force: false, gameAuto: true);
        }

        e.Handled = true;
    }

    private void VideoPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_video is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(VideoPanel);
        if (!point.Properties.IsLeftButtonPressed) { SendMouseButton(1, false); }
        if (!point.Properties.IsMiddleButtonPressed) { SendMouseButton(2, false); }
        if (!point.Properties.IsRightButtonPressed) { SendMouseButton(3, false); }

        if (_mouseButtonsDown.Count == 0 && !_pointerLocked)
        {
            _pointerTracking = false;
            VideoPanel.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
    }

    private void VideoPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_video is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(VideoPanel).Position;
        _lastPointerPos = pos;

        if (_pointerLocked)
        {
            HandleLockedPointerMove(pos);
            e.Handled = true;
            return;
        }

        // 绝对模式：没锁定也把指针在画面里的位置直接映射过去。
        // 这样本地光标永远压在主机光标上 —— 从开始菜单/别的窗口回来时一眼就能对上，
        // 不会再出现"要刚好挪到远程光标上才对齐"的情况。
        if (_settings.AbsoluteMouse)
        {
            if (SyncAbsoluteToPoint(pos))
            {
                e.Handled = true;
            }

            return;
        }

        if (!_pointerTracking)
        {
            return;
        }

        var (dxDip, dyDip) = (pos.X - _lastPointerPos.X, pos.Y - _lastPointerPos.Y);
        _lastPointerPos = pos;
        SendPointerDelta(dxDip, dyDip);

        e.Handled = true;
    }

    /// <summary>
    /// 把「面板里的 DIP 坐标」映射成流画面坐标并立刻发绝对位置。
    /// 同时把虚拟光标挪到这个点，这样紧接着锁定指针是从这里继续，不会跳。
    /// </summary>
    private bool SyncAbsoluteToPoint(Windows.Foundation.Point pos)
    {
        var video = _video;
        var w = VideoPanel.ActualWidth;
        var h = VideoPanel.ActualHeight;
        if (video is null || w < 2 || h < 2)
        {
            return false;
        }

        video.RefreshStreamSize();
        var streamW = video.StreamWidth > 0 ? video.StreamWidth : _settings.Width;
        var streamH = video.StreamHeight > 0 ? video.StreamHeight : _settings.Height;

        var x = Math.Clamp((int)Math.Round(pos.X / w * streamW), 0, streamW - 1);
        var y = Math.Clamp((int)Math.Round(pos.Y / h * streamH), 0, streamH - 1);

        _absX = x;
        _absY = y;
        _absW = streamW;
        _absH = streamH;
        _absReady = true;

        var rc = video.SendMousePosition(x, y, streamW, streamH);
        if (_absLogCount < 8)
        {
            _absLogCount++;
            PointerLockLog($"abs(sync) {x},{y} / {streamW}x{streamH} rc={rc}");
        }

        return true;
    }

    /// <summary>拖拽（未锁定）时用：DIP 增量 → 主机像素。</summary>
    private void SendPointerDelta(double dxDip, double dyDip)
    {
        var (dx, dy) = ScaleToHost(dxDip, dyDip);
        SendPointerDeltaHost(dx, dy);
    }

    /// <summary>主机像素增量 → 绝对坐标（默认）或相对事件。</summary>
    private void SendPointerDeltaHost(int dx, int dy)
    {
        var video = _video;
        if (video is null)
        {
            return;
        }

        if (_settings.AbsoluteMouse)
        {
            // 没动就绝对不要发坐标：锁定后本地指针会被钉到画面中心，
            // 这时若因为"零位移"事件发一次绝对位置，主机光标就会瞬间跳到中心。
            if (dx == 0 && dy == 0)
            {
                return;
            }

            // 坐标系必须和 SyncAbsoluteToPoint 用同一套：**流的分辨率**，
            // 不是 VideoWidth（那是交换链尺寸）。用错了会让虚拟光标在每次移动时
            // 被重置到画面中心 —— 表现就是"一回到串流窗口鼠标就归中"。
            video.RefreshStreamSize();
            var w = video.StreamWidth > 0 ? video.StreamWidth : Math.Max(1, _settings.Width);
            var h = video.StreamHeight > 0 ? video.StreamHeight : Math.Max(1, _settings.Height);

            if (!_absReady)
            {
                // 只有第一次（还没同步过位置）才放中心；之后一律沿用当前位置
                _absX = w / 2.0;
                _absY = h / 2.0;
                _absW = w;
                _absH = h;
                _absReady = true;
            }
            else if (_absW != w || _absH != h)
            {
                // 流分辨率变了：按比例缩放原位置，别跳到中心
                _absX = _absX / Math.Max(1, _absW) * w;
                _absY = _absY / Math.Max(1, _absH) * h;
                _absW = w;
                _absH = h;
            }

            _absX = Math.Clamp(_absX + dx, 0, w - 1);
            _absY = Math.Clamp(_absY + dy, 0, h - 1);
            var rc = video.SendMousePosition((int)Math.Round(_absX), (int)Math.Round(_absY), w, h);
            if (_absLogCount < 8)
            {
                _absLogCount++;
                PointerLockLog($"abs {(int)_absX},{(int)_absY} / {w}x{h} rc={rc} d=({dx},{dy})");
            }

            return;
        }

        if (dx != 0 || dy != 0)
        {
            video.SendMouseMove(dx, dy);
        }
    }

    /// <summary>用系统光标当前所在位置同步一次（窗口激活 / 进入锁定前用）。</summary>
    private void SyncAbsoluteToSystemCursor()
    {
        if (_video is null)
        {
            return;
        }

        var rect = PanelScreenRect();
        if (rect.Width < 2 || rect.Height < 2 || !NativeMouse.GetCursorPos(out var p))
        {
            return;
        }

        if (p.X < rect.Left || p.X > rect.Right || p.Y < rect.Top || p.Y > rect.Bottom)
        {
            return; // 指针不在画面里就不动主机光标
        }

        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var dipX = (p.X - rect.Left) / scale;
        var dipY = (p.Y - rect.Top) / scale;
        SyncAbsoluteToPoint(new Windows.Foundation.Point(dipX, dipY));
    }

    /// <summary>滚轮：一格 = 1，正数向上（可选反转）。</summary>
    private void VideoPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_video is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(VideoPanel).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            var clicks = delta / 120;
            if (_settings.ReverseScroll)
            {
                clicks = -clicks;
            }

            _video.SendMouseScroll(clicks);
        }

        e.Handled = true;
    }

    private void VideoPanel_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pointerTracking = false;
    }

    /// <summary>
    /// 进入相对捕获：藏起系统指针、夹在画面矩形里、钉到中心。
    /// 只有真串流（state=2）才进；演示流没有主机可发，点了只当取焦点。
    /// </summary>
    private bool TryEnterPointerLock(bool force, bool gameAuto = false)
    {
        if (_pointerLocked || _video is null)
        {
            return false;
        }

        // 绝对模式不需要相对捕获：本地指针不动、主机光标按位置直接映射，
        // 也就不存在"归中"和"藏不起来的光标"问题。
        // 但游戏内自动锁定（gameAuto）必须绕过它 —— 游戏要的是相对位移，
        // 绝对模式下玩家转了鼠标视角是不动的（这就是 v1.0.50 的老问题）。
        if (_settings.AbsoluteMouse && !gameAuto)
        {
            return false;
        }

        if (!force && _video.StreamStatus().State != 2)
        {
            return false;
        }

        var rect = PanelScreenRect();
        if (rect.Width < 2 || rect.Height < 2)
        {
            return false;
        }

        // 绝对模式：锁定前先把主机光标对到**本地指针真实位置**（不是上次归中的中心），
        // 这样"点回画面"那一下主机光标就在你点的地方，不会被拉回中心。
        if (_settings.AbsoluteMouse)
        {
            SyncAbsoluteToSystemCursor();
        }

        NativeMouse.GetCursorPos(out _cursorSavePos);
        _pointerLocked = true;
        _pointerLockForced = force;
        _awaitCenter = true;

        var hideCount = NativeMouse.ShowCursor(false);
        // 计数可能被别的程序顶成正数；一直减到负值为止，并记下减了几次，退出时原样加回去
        _hideSteps = 1;
        while (hideCount >= 0 && _hideSteps < 8)
        {
            hideCount = NativeMouse.ShowCursor(false);
            _hideSteps++;
        }

        // WinUI 3 每个元素都会自己 SetCursor，ShowCursor 压不住；
        // 而它的光标走输入系统，WM_SETCURSOR 不会被派发到窗口过程（试过子类化，收不到）。
        // 可行做法：锁定期间用定时器反复 SetCursor(NULL)，把框架设回来的光标再抹掉。

        NativeMouse.ClipCursorTo(ref rect);
        CenterCursor(rect);

        // Win32 串流窗口模式下鼠标事件进的是原生窗口，不在 WinUI 面板上 ——
        // 让 native 把 WM_MOUSEMOVE 翻译成相对位移，否则锁定后仍在发绝对坐标（视角几乎不动）。
        _video?.SetPointerLock(true);

        NativeMouse.GetCursorPos(out var centered);

        _lastCursorPhys = centered;
        _lastPointerPos = new Windows.Foundation.Point(VideoPanel.ActualWidth / 2, VideoPanel.ActualHeight / 2);
        _awaitCenter = true;
        _pointerTracking = true;
        // 指针锁定后不再显示底部提示（那一行已经删掉）

        PointerLockLog(
            $"enter rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) " +
            $"panel={VideoPanel.ActualWidth:F0}x{VideoPanel.ActualHeight:F0} " +
            $"saved=({_cursorSavePos.X},{_cursorSavePos.Y}) centered=({centered.X},{centered.Y}) " +
            $"hideCount={hideCount} force={force}");
        return true;
    }

    /// <summary>退出捕获：解夹取、恢复系统指针和原位置、抬起所有按下的键。</summary>
    private void ExitPointerLock(string reason)
    {
        if (!_pointerLocked)
        {
            return;
        }

        _pointerLocked = false;
        _pointerLockForced = false;
        _awaitCenter = false;

        _video?.SetPointerLock(false);
        NativeMouse.ClipCursorFree(IntPtr.Zero);
        var showCount = 0;
        for (var i = 0; i < _hideSteps; i++)
        {
            showCount = NativeMouse.ShowCursor(true);
        }
        _hideSteps = 0;

        NativeMouse.SetCursorPos(_cursorSavePos.X, _cursorSavePos.Y);

        ReleaseAllInput();

        PointerLockLog($"exit ({reason}) showCount={showCount}");
    }

    private void HandleLockedPointerMove(Windows.Foundation.Point pos)
    {
        var w = VideoPanel.ActualWidth;
        var h = VideoPanel.ActualHeight;
        if (w < 2 || h < 2)
        {
            return;
        }

        var rect = PanelScreenRect();
        var pw = Math.Max(1, rect.Right - rect.Left);
        var ph = Math.Max(1, rect.Bottom - rect.Top);

        // 刚做过 SetCursorPos：下一帧的真实位置只当锚点，不算移动
        if (_awaitCenter)
        {
            NativeMouse.GetCursorPos(out _lastCursorPhys);
            _awaitCenter = false;
            return;
        }

        // 增量一律以「系统光标真实位置」为准：WinUI 的 PointerMoved 里可能混着
        // 居中前排队的事件（旧坐标），拿它算 delta 就会把主机光标带偏。
        if (!NativeMouse.GetCursorPos(out var now))
        {
            return;
        }

        var dxPhys = now.X - _lastCursorPhys.X;
        var dyPhys = now.Y - _lastCursorPhys.Y;
        _lastCursorPhys = now;

        // 指针被外部瞬移（SetCursorPos 居中、窗口激活、切 DPI）：这类事件一律丢掉。
        // 正常鼠标两次事件之间跳不了 250 物理像素，拿这个当阈值最稳。
        if (Math.Abs(dxPhys) > 250 || Math.Abs(dyPhys) > 250)
        {
            return;
        }

        SendPointerDeltaPhysical(dxPhys, dyPhys, pw, ph);

        // 贴近边缘就把指针钉回中心：否则增量被窗口边界吃掉，3D 视角转不动
        var margin = (int)Math.Min(60.0 * (VideoPanel.XamlRoot?.RasterizationScale ?? 1.0),
                                   Math.Min(pw, ph) * 0.2);
        if (now.X < rect.Left + margin || now.X > rect.Right - margin ||
            now.Y < rect.Top + margin || now.Y > rect.Bottom - margin)
        {
            CenterCursor(rect);
            NativeMouse.GetCursorPos(out _lastCursorPhys);
            _awaitCenter = true;
        }
    }

    /// <summary>面板坐标（DIP）→ 主机像素：乘"视频分辨率 / 面板尺寸"。</summary>
    private (int Dx, int Dy) ScaleToHost(double dx, double dy)
    {
        var video = _video;
        if (video is null || VideoPanel.ActualWidth <= 0 || VideoPanel.ActualHeight <= 0)
        {
            return ((int)Math.Round(dx), (int)Math.Round(dy));
        }

        video.RefreshStreamSize();
        var streamW = video.StreamWidth > 0 ? video.StreamWidth : _settings.Width;
        var streamH = video.StreamHeight > 0 ? video.StreamHeight : _settings.Height;

        // 面板尺寸是 DIP，流是像素：先换到物理像素再比
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var sx = streamW / (VideoPanel.ActualWidth * scale);
        var sy = streamH / (VideoPanel.ActualHeight * scale);

        return ((int)Math.Round(dx * sx), (int)Math.Round(dy * sy));
    }

    /// <summary>物理像素增量 → 主机像素（流分辨率 / 面板物理尺寸）。</summary>
    private void SendPointerDeltaPhysical(int dxPhys, int dyPhys, int panelWidthPhys, int panelHeightPhys)
    {
        var video = _video;
        if (video is null)
        {
            return;
        }

        video.RefreshStreamSize();
        var streamW = video.StreamWidth > 0 ? video.StreamWidth : _settings.Width;
        var streamH = video.StreamHeight > 0 ? video.StreamHeight : _settings.Height;

        var sx = streamW / (double)Math.Max(1, panelWidthPhys);
        var sy = streamH / (double)Math.Max(1, panelHeightPhys);

        SendPointerDeltaHost((int)Math.Round(dxPhys * sx), (int)Math.Round(dyPhys * sy));
    }

    /// <summary>
    /// 面板在屏幕上的物理像素矩形：WinUI 坐标是 DIP，SetCursorPos/ClipCursor 收的是物理像素，
    /// 所以要 乘 RasterizationScale → ClientToScreen。
    /// </summary>
    private NativeMouse.RECT PanelScreenRect()
    {
        var transform = VideoPanel.TransformToVisual(null);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var bottomRight = transform.TransformPoint(new Windows.Foundation.Point(VideoPanel.ActualWidth, VideoPanel.ActualHeight));
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;

        var p1 = new NativeMouse.POINT { X = (int)Math.Round(topLeft.X * scale), Y = (int)Math.Round(topLeft.Y * scale) };
        var p2 = new NativeMouse.POINT { X = (int)Math.Round(bottomRight.X * scale), Y = (int)Math.Round(bottomRight.Y * scale) };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMouse.ClientToScreen(hwnd, ref p1);
        NativeMouse.ClientToScreen(hwnd, ref p2);

        return new NativeMouse.RECT { Left = p1.X, Top = p1.Y, Right = p2.X, Bottom = p2.Y };
    }

    private static bool CenterCursor(NativeMouse.RECT rect)
    {
        var ok = NativeMouse.SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        if (!ok)
        {
            PointerLockLogStatic($"center failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        return ok;
    }

    private static void PointerLockLogStatic(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "pointer-lock.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void VideoPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_video is null || e.Key == Windows.System.VirtualKey.None)
        {
            return;
        }

        // Ctrl+Alt+Shift+Q 释放指针（和 Moonlight 的默认快捷键一致）；其余按键照常发给主机
        if (_pointerLocked &&
            e.Key == Windows.System.VirtualKey.Q &&
            NativeMouse.IsKeyDown(NativeMouse.VkControl) &&
            NativeMouse.IsKeyDown(NativeMouse.VkMenu) &&
            NativeMouse.IsKeyDown(NativeMouse.VkShift))
        {
            ExitPointerLock("hotkey");
            e.Handled = true;
            return;
        }

        var vk = (int)e.Key;
        if (_keysDown.Add(vk))
        {
            _video.SendKey(vk, true);
        }

        e.Handled = true;
    }

    private void VideoPanel_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_video is null)
        {
            return;
        }

        var vk = (int)e.Key;
        _keysDown.Remove(vk);
        _video.SendKey(vk, false);

        e.Handled = true;
    }

    // 焦点一走就把指针解锁、把按下的键/键鼠都抬起来，别让主机那边卡键
    private void VideoPanel_LostFocus(object sender, RoutedEventArgs e)
    {
        ExitPointerLock("lost focus");
    }

    private void SendMouseButton(int button, bool down)
    {
        if (_video is null)
        {
            return;
        }

        // Qt: swapmousebuttons —— 左右键互换（按下/抬起都换，状态集合里记的是换过之后的键）
        if (_settings.SwapMouseButtons)
        {
            button = button switch
            {
                1 => 3,
                3 => 1,
                _ => button,
            };
        }

        if (down ? !_mouseButtonsDown.Add(button) : !_mouseButtonsDown.Remove(button))
        {
            return;
        }

        _video.SendMouseButton(button, down);
    }

    private void ReleaseAllInput()
    {
        if (_video is null)
        {
            return;
        }

        foreach (var vk in _keysDown)
        {
            _video.SendKey(vk, false);
        }
        _keysDown.Clear();

        foreach (var button in _mouseButtonsDown.ToArray())
        {
            _video.SendMouseButton(button, false);
        }
        _mouseButtonsDown.Clear();

        _pointerTracking = false;
    }

    // ---------------- 自测（无头验证） ----------------

    private readonly System.Diagnostics.Stopwatch _streamClock = new();
    private int _selfTestStep;

    private void RunSelfTest()
    {
        if (_video is null || Environment.GetEnvironmentVariable("ML_SELFTEST") != "1")
        {
            return;
        }

        var (state, _) = _video.StreamStatus();
        if (state != 2)
        {
            _selfTestStep = 0;
            _streamClock.Reset();
            return;
        }

        if (_selfTestStep == 0)
        {
            _streamClock.Restart();
            _selfTestStep = 1;
            SelfTestLog("streaming，开始计时");
        }

        var t = _streamClock.Elapsed.TotalSeconds;

        if (_selfTestStep == 1 && t >= 2.0)
        {
            // 相对移动没法指定绝对位置：先往左上顶一大截（系统会把指针夹在 (0,0)），
            // 再走 (300,200)，这样主机上的落点就是可预期的，方便截图验证。
            var clamp = _video.SendMouseMove(-3000, -3000);
            var move = _video.SendMouseMove(300, 200);
            var rightDown = _video.SendMouseButton(3, true);
            var rightUp = _video.SendMouseButton(3, false);
            SelfTestLog($"t={t:F1}s clamp={clamp} move={move} rightDown={rightDown} rightUp={rightUp}");
            _selfTestStep = 2;
        }
        else if (_selfTestStep == 2 && t >= 5.0)
        {
            var escDown = _video.SendKey(0x1B, true);   // ESC
            var escUp = _video.SendKey(0x1B, false);
            SelfTestLog($"t={t:F1}s escDown={escDown} escUp={escUp}");
            _selfTestStep = 3;
        }
    }

    private static void SelfTestLog(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "input-selftest.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>ML_LOCKTEST=1：演示流下也进锁两秒，验证藏指针/夹取/钉中心/恢复这一串。</summary>
    private void RunPointerLockSelfTest()
    {
        if (Environment.GetEnvironmentVariable("ML_LOCKTEST") != "1" || _video is null)
        {
            return;
        }

        switch (_lockTestStage)
        {
            case 0 when _video.VideoWidth > 0:
                _lockTestStage = 1;
                _lockTestClock.Restart();
                break;
            case 1 when _lockTestClock.Elapsed.TotalSeconds >= 2.0:
                _lockTestStage = 2;
                _lockTestClock.Restart();
                PointerLockLog($"selftest enter={TryEnterPointerLock(force: true)}");
                break;
            case 2 when _lockTestClock.Elapsed.TotalSeconds >= 2.0:
                ExitPointerLock("selftest");
                _lockTestStage = 3;
                break;
        }
    }

    private static void PointerLockLog(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "pointer-lock.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
