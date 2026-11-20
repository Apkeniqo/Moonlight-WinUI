using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace MoonlightWinUI;

/// <summary>
/// 串流页的「传输控制」弹窗：无标题栏、始终置顶、显示在视频之上；
/// 两栏布局（左：分辨率 / 码率滑块；右：全屏），点外面（失去焦点）自动关闭。
/// </summary>
internal sealed partial class ControlWindow : Window
{
    private readonly StreamSettings _settings;
    private readonly Action<int, int, int, bool> _apply;
    private readonly string _host;

    // 主机显示缩放（Sunshine 扩展：/display-scale*）
    private ComboBox? _hostScale;
    private ProgressRing? _hostScaleRing;
    private TextBlock? _hostScaleHint;
    private DisplayScaleInfo? _scaleInfo;
    private bool _scaleApplying;

    private readonly ComboBox _resolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider _bitrate = new() { Minimum = 5, Maximum = 200, StepFrequency = 1 };
    private readonly TextBlock _bitrateText = new() { FontSize = 12, Opacity = 0.7 };
    private readonly ToggleSwitch _fullScreen = new() { Header = "全屏", OnContent = "开", OffContent = "关" };

    private const int BorderNone = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
    private const int CornerRound = 2;                            // DWMWCP_ROUND
    private const int HwndTopmost = -1;      // HWND_TOPMOST
    private const int HwndNoTopmost = -2;    // HWND_NOTOPMOST
    private const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    private bool _initialized;
    private bool _closing;
    private bool _closed;   // Closed 之后所有定时器回调必须立刻退出
    private Microsoft.UI.Xaml.Controls.Grid? _root;
    private Microsoft.UI.Xaml.Media.ScaleTransform? _scale;

    /// <summary>关闭前回调（用来先停止原生跟随，避免关闭瞬间被挪位）。</summary>
    public Action? BeforeClose;

    /// <summary>「退出串流」：停掉串流、回到主窗口。</summary>
    public Action? QuitStream;

    /// <summary>「退出并结束串流」：向主机发 /cancel 结束会话/应用，再关窗（Moonlight 不退出）。</summary>
    public Action? QuitStreamAndExit;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusWatch;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    private long _shownAt;
    private bool _seenActivated;

    public ControlWindow(StreamSettings settings, Action<int, int, int, bool> applyAction, string host = "")
    {
        _settings = settings;
        _apply = applyAction;
        _host = host ?? string.Empty;

        Title = "传输控制";

        var root = new Grid
        {
            Padding = new Thickness(18),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "传输控制",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetColumnSpan(title, 2);
        root.Children.Add(title);

        // 左栏：分辨率 + 码率
        var left = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 12, 0) };

        // 与设置页保持一致：本机分辨率 + 标准列表 + 自定义
        _resolution.Items.Add(new ComboBoxItem { Content = "本机分辨率", Tag = (0, 0) });

        foreach (var res in StreamSettings.Resolutions)
        {
            _resolution.Items.Add(new ComboBoxItem { Content = $"{res.Width} × {res.Height}", Tag = res });
        }

        foreach (var custom in _settings.CustomResolutions)
        {
            var parts = custom.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var cw) && int.TryParse(parts[1], out var ch))
            {
                _resolution.Items.Add(new ComboBoxItem { Content = $"{cw} × {ch}", Tag = (cw, ch) });
            }
        }

        left.Children.Add(Label("分辨率"));
        left.Children.Add(_resolution);
        left.Children.Add(Label("码率（Mbps）"));
        _bitrate.ValueChanged += (_, e) => _bitrateText.Text = $"{e.NewValue:F0} Mbps";
        left.Children.Add(_bitrate);
        left.Children.Add(_bitrateText);

        // 右栏：全屏 + 说明
        var right = new StackPanel { Spacing = 8 };
        right.Children.Add(_fullScreen);

        // 主机显示缩放（DPI）：主机支持时可直接改，立即生效、不需要重连
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scaleRow.Children.Add(Label("主机缩放"));
        _hostScale = new ComboBox { Width = 110, IsEnabled = false, PlaceholderText = "读取…" };
        _hostScale.SelectionChanged += HostScale_SelectionChanged;
        scaleRow.Children.Add(_hostScale);
        _hostScaleRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scaleRow.Children.Add(_hostScaleRing);
        right.Children.Add(scaleRow);

        _hostScaleHint = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
        };
        right.Children.Add(_hostScaleHint);
        right.Children.Add(new TextBlock
        {
            Text = "改分辨率会重连，码率即时生效。",
            FontSize = 12,
            Opacity = 0.6,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);
        Grid.SetRow(body, 1);
        Grid.SetColumnSpan(body, 2);
        root.Children.Add(body);

        // 底部按钮
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var apply = new Button
        {
            Content = "应用",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        apply.Click += (_, _) => ApplyAndKeepOpen();

        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => DispatcherQueue.TryEnqueue(() => SafeClose());

        var quitStream = new Button { Content = "退出串流" };
        quitStream.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            SafeClose();
            QuitStream?.Invoke();
        });

        var quitApp = new Button { Content = "退出并结束串流" };
        quitApp.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            SafeClose();
            QuitStreamAndExit?.Invoke();
        });

        buttons.Children.Add(quitStream);
        buttons.Children.Add(quitApp);
        buttons.Children.Add(apply);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        _root = root;
        _scale = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 1, ScaleY = 1, CenterX = 0, CenterY = 0 };
        root.RenderTransform = _scale;
        root.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        Content = root;

        // 材质跟主窗口/串流窗口一致（设置里选的那种）
        ApplyBackdrop(_settings.BackdropKind);
        StreamSettings.Saved += OnSettingsSaved;
        Closed += (_, _) => StreamSettings.Saved -= OnSettingsSaved;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;   // 不要压在其他应用之上，只跟随串流窗口
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        ExtendsContentIntoTitleBar = true;
        // 不要 SetTitleBar(root)：那会把整个面板当成标题栏，点哪都在拖窗口，按钮全点不动

        // 去掉窗口边框（DWM 的 1px 白边 + 非客户区）
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var borderNone = BorderNone;
            var cornerRound = CornerRound;
            DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderNone, sizeof(int));
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref cornerRound, sizeof(int));

            var ncPolicy = 2;   // DWMNCRP_DISABLED
            DwmSetWindowAttribute(hwnd, 2 /* DWMWA_NCRENDERING_POLICY */, ref ncPolicy, sizeof(int));

            const int GWL_STYLE = -16;
            const long WS_CAPTION = 0x00C00000;
            const long WS_THICKFRAME = 0x00040000;
            const long WS_BORDER = 0x00800000;
            const long WS_DLGFRAME = 0x00400000;

            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

            // SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001);
        }
        catch
        {
        }

        // 点弹窗外（失去焦点）自动关闭
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.CodeActivated ||
                e.WindowActivationState == WindowActivationState.PointerActivated)
            {
                _seenActivated = true;
                return;
            }

            // 第一次 Deactivated 常常是打开时的瞬时状态，忽略；之后的才真正关闭。
            // 这里绝对不能 Close()：窗口一旦销毁，_focusWatch / _anim 两个定时器还在跑，
            // 下一拍就会去碰已销毁的 AppWindow / XAML 元素 —— 那是 UI 线程未处理异常，
            // 直接闪退（而且 Application.UnhandledException 未必能拦到）。统一走 SafeClose()。
            // 只有前台切到"别的进程"才关；全屏串流窗口（本进程）抢前台不算离开
            var fgNow = GetForegroundWindow();
            GetWindowThreadProcessId(fgNow, out var fgPid);
            if (_seenActivated && Environment.TickCount64 - _shownAt > 1200 && fgPid != (uint)Environment.ProcessId)
            {
                DispatcherQueue.TryEnqueue(() => SafeClose());
            }
        };

        // 兜底：万一窗口还是被强制销毁，先把两个定时器停掉，防止回调访问已销毁对象
        Closed += (_, _) =>
        {
            _closed = true;
            Diag("Closed");
            try { _focusWatch?.Stop(); } catch { }
            try { _anim?.Stop(); } catch { }
            _animDone = null;
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.8,
        Foreground = new SolidColorBrush(Colors.White),
    };

    private void SyncFromSettings()
    {
        var index = Array.FindIndex(StreamSettings.Resolutions,
            r => r.Width == _settings.Width && r.Height == _settings.Height);

        if (index >= 0)
        {
            _resolution.SelectedIndex = index + 1;   // +1：第一项是「本机分辨率」
        }
        else
        {
            // 自定义分辨率 / 本机分辨率：按 Tag 精确匹配，找不到就落到 1080p
            var found = -1;
            for (var i = 0; i < _resolution.Items.Count; i++)
            {
                if (_resolution.Items[i] is ComboBoxItem ci && ci.Tag is ValueTuple<int, int> t &&
                    ((t.Item1 == _settings.Width && t.Item2 == _settings.Height) ||
                     (t.Item1 == 0 && t.Item2 == 0)))
                {
                    found = i;
                    break;
                }
            }

            _resolution.SelectedIndex = found >= 0 ? found : 1 + (index >= 0 ? index : 2);
        }

        _bitrate.Value = Math.Clamp(_settings.BitrateKbps / 1000.0, 5, 200);
        _bitrateText.Text = $"{_bitrate.Value:F0} Mbps";
        _fullScreen.IsOn = _settings.FullScreen;
    }

    /// <summary>在屏幕坐标 (x, y) 处显示（调用方算好画面正中的左上角）。</summary>
    public bool IsOpen => !_closing && _root is not null && IsWindowVisibleHwnd();

    private bool IsWindowVisibleHwnd()
    {
        try
        {
            var h = PanelHwnd;
            return h != IntPtr.Zero && IsWindowVisible(h);
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);

    // 弹窗生命周期诊断：写 popup-diag.log。崩溃时至少能看出最后一步走到哪。
    /// <summary>读取主机当前显示缩放与档位（主机不支持/离线时禁用下拉）。</summary>
    private async Task LoadHostScaleAsync()
    {
        if (string.IsNullOrWhiteSpace(_host) || _hostScale is null || _hostScaleHint is null)
        {
            return;
        }

        if (_hostScaleRing is not null) { _hostScaleRing.IsActive = true; }
        _hostScaleHint.Text = "正在读取主机缩放…";

        var info = await Task.Run(() => HostApi.GetDisplayScale(_host));

        if (_hostScaleRing is not null) { _hostScaleRing.IsActive = false; }
        _scaleInfo = info;
        _hostScale.Items.Clear();

        if (info is null)
        {
            _hostScaleHint.Text = "读取失败（主机离线或未配对）";
            return;
        }

        if (!info.IsAvailable)
        {
            var current = info.CurrentScalePercent is int value ? $"{value}%" : "未知";
            _hostScaleHint.Text = $"主机不支持调整缩放（当前 {current}）";
            return;
        }

        _scaleApplying = true;
        foreach (var percent in info.SupportedScalePercents)
        {
            _hostScale.Items.Add(new ComboBoxItem { Content = $"{percent}%", Tag = percent });
        }

        for (var i = 0; i < _hostScale.Items.Count; i++)
        {
            if (_hostScale.Items[i] is ComboBoxItem item && item.Tag is int percent &&
                percent == info.CurrentScalePercent)
            {
                _hostScale.SelectedIndex = i;
                break;
            }
        }
        _scaleApplying = false;

        _hostScale.IsEnabled = true;
        _hostScaleHint.Text = $"当前 {info.CurrentScalePercent ?? 0}% · {info.FriendlyName}";
    }

    private async void HostScale_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scaleApplying || _scaleInfo is null || _hostScale is null || _hostScaleHint is null)
        {
            return;
        }

        if ((_hostScale.SelectedItem as ComboBoxItem)?.Tag is not int percent) { return; }
        if (percent == _scaleInfo.CurrentScalePercent) { return; }

        var displayName = _scaleInfo.DisplayName;
        var deviceId = _scaleInfo.DeviceId;

        _scaleApplying = true;
        if (_hostScaleRing is not null) { _hostScaleRing.IsActive = true; }
        _hostScaleHint.Text = $"正在设置为 {percent}%…";

        var result = await Task.Run(() => HostApi.SetDisplayScale(_host, percent, displayName, deviceId));

        if (_hostScaleRing is not null) { _hostScaleRing.IsActive = false; }
        _scaleApplying = false;

        if (result is null || !result.Success)
        {
            _hostScaleHint.Text = "设置失败：" + (result?.StatusMessage ?? "主机无响应");
            return;
        }

        var now = result.CurrentScalePercent ?? percent;
        _hostScaleHint.Text = result.RestartRequired
            ? $"已接受 {percent}%，主机需重启显示"
            : result.EffectiveImmediately
                ? $"已设置为 {now}%"
                : $"已接受 {percent}%，稍后生效";

        _scaleInfo = _scaleInfo with { CurrentScalePercent = now };
    }

    private static void Diag(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "popup-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    public void ShowAt(int x, int y, double scale, int width = 520, int height = 360)
    {
        if (_closed)
        {
            return;
        }

        Diag($"ShowAt enter hwnd={PanelHwnd} at={x},{y} {width}x{height}");

        if (!_initialized)
        {
            _initialized = true;
            SyncFromSettings();
        }

        // 上一次的关闭动画可能还没跑完：先停掉。否则它跑完时会把刚打开的窗口再隐藏一遍，
        // 留下"WinUI 认为可见、HWND 实际隐藏"的不一致状态。
        try { _anim?.Stop(); } catch { }
        _animDone = null;
        _closing = false;
        if (_root is not null) { _root.Opacity = 1; }
        if (_scale is not null) { _scale.ScaleX = 1; _scale.ScaleY = 1; }

        // 尺寸由调用方按串流窗口大小算好（物理像素）
        try
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            _shownAt = Environment.TickCount64;
        }
        catch
        {
            return;   // 窗口状态异常时直接放弃这次打开，避免 stowed exception 崩进程
        }
        Activate();
        SetForegroundWindow(PanelHwnd);   // 确保面板真正成为前台，点别处才能触发关闭

        // 全屏串流窗口是整屏 WS_POPUP，普通 z-order 会被它盖住（表现为"闪一下就消失"）。
        // 面板显示期间置顶，关闭时恢复。
        SetWindowPos(PanelHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        Diag($"ShowAt topmost fg=0x{GetForegroundWindow().ToInt64():X} self=0x{PanelHwnd.ToInt64():X}");

        // 每次打开都刷新一次（主机可能在别处改过缩放）
        _ = LoadHostScaleAsync();

        // 失焦关闭：轮询前台窗口，比 Activated 事件可靠（WinUI 有时不派发 Deactivated）
        if (_focusWatch is null)
        {
            _focusWatch = DispatcherQueue.CreateTimer();
            _focusWatch.Interval = TimeSpan.FromMilliseconds(200);
            _focusWatch.IsRepeating = true;
            _focusWatch.Tick += (_, _) =>
            {
                if (_closed)
                {
                    try { _focusWatch?.Stop(); } catch { }
                    return;
                }

                try
                {
                    if (Environment.TickCount64 - _shownAt < 1200)   // 全屏切换/抢前台需要时间，放宽到 1.2s
                    {
                        return;
                    }

                    var self = PanelHwnd;
                    if (self == IntPtr.Zero)
                    {
                        return;
                    }

                    var fg = GetForegroundWindow();
                    if (fg == self || IsChild(self, fg))
                    {
                        return;
                    }

                    // 前台是本进程的其它窗口（典型：全屏串流窗口）也保持打开。
                    // 全屏下 SetForegroundWindow 经常抢不到前台，之前会导致面板刚弹出就被关掉。
                    GetWindowThreadProcessId(fg, out var fgProcess);
                    if (fgProcess == (uint)Environment.ProcessId)
                    {
                        return;
                    }

                    _focusWatch?.Stop();
                    DispatcherQueue.TryEnqueue(() => SafeClose());
                }
                catch
                {
                    // 定时器回调里抛异常 = UI 线程闪退，必须吞掉
                }
            };
        }

        _focusWatch.Start();
        AnimateOpen();
        Diag("ShowAt done");
    }

    private const int kAnimOpenMs = 160;    // 打开：0.16s
    private const int kAnimCloseMs = 130;   // 关闭：0.13s
    private const int kAnimStepMs = 16;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _anim;
    private int _animFrame;
    private int _animFrames;
    private bool _animIsOpen;
    private Action? _animDone;

    private static double EaseOut(double k) => 1 - (1 - k) * (1 - k);

    private void StartAnim(bool open, Action? done)
    {
        _anim?.Stop();
        if (_root is null) { done?.Invoke(); return; }

        _animIsOpen = open;
        _animDone = done;
        _animFrame = 0;
        _animFrames = Math.Max(1, (open ? kAnimOpenMs : kAnimCloseMs) / kAnimStepMs);

        if (_anim is null)
        {
            _anim = DispatcherQueue.CreateTimer();
            _anim.Interval = TimeSpan.FromMilliseconds(kAnimStepMs);
            _anim.IsRepeating = true;
            _anim.Tick += (_, _) => AnimTick();
        }

        _anim.Start();
    }

    private void AnimTick()
    {
        if (_closed)
        {
            try { _anim?.Stop(); } catch { }
            return;
        }

        try
        {
            _animFrame++;
            double k = (double)_animFrame / _animFrames;
            if (k > 1) { k = 1; }
            double e = EaseOut(k);

            if (_root is not null)
            {
                _root.Opacity = _animIsOpen ? e : 1 - e;
            }

            if (_scale is not null && _animIsOpen)
            {
                var s = 0.94 + 0.06 * e;
                _scale.ScaleX = s;
                _scale.ScaleY = s;
            }

            if (k < 1) { return; }

            _anim?.Stop();
            var done = _animDone;
            _animDone = null;

            if (!_animIsOpen)
            {
                _closing = false;
                // 注意：这里不要再用 AppWindow.Move(-32000,-32000) 把窗口挪出屏幕。
                // 隐藏用 done() 里的 AppWindow.Hide() 就够了；移出屏幕会触发显示器/DPI 变化，
                // 反复开关时很容易把 WinUI 的窗口状态机搞崩（stowed exception），而且会闪一下。
            }

            done?.Invoke();
            Diag($"anim {(_animIsOpen ? "open" : "close")} done");
        }
        catch
        {
            // 定时器回调不能抛：抛出去就是 UI 线程未处理异常 → 闪退
            try { _anim?.Stop(); } catch { }
            _animDone = null;
            _closing = false;
        }
    }

    private void AnimateOpen()
    {
        if (_root is null || _scale is null) { return; }

        // 起始态：透明 + 略微缩小。
        // 注意：缩放必须动画回 1 —— 之前只给 Opacity 加了动画，ScaleX/ScaleY 停在 0.94，
        // 内容比窗口小 6%，四周露出一圈窗口底色（纯黑），就是"弹窗黑边"。
        _root.Opacity = 0;
        _scale.ScaleX = 0.94;
        _scale.ScaleY = 0.94;

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _root);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.94,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scale, _scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scale, property);
            sb.Children.Add(scale);
        }

        try
        {
            sb.Begin();
        }
        catch
        {
            // 动画起不来也要保证最终态是对的（否则又会露出黑边）
            _root.Opacity = 1;
            _scale.ScaleX = 1;
            _scale.ScaleY = 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>隐藏面板（不销毁窗口，下次直接复用）。</summary>
    public void HidePanel()
    {
        try { _focusWatch?.Stop(); } catch { }
        try { ShowWindow(PanelHwnd, 0 /* SW_HIDE */); } catch { }
        _closing = false;
    }

    private void AnimateCloseThenDone(Action done)
    {
        // 关闭动画统一走 StartAnim（DispatcherQueueTimer）。
        // 不要用 Storyboard：窗口隐藏后它的 Completed 可能永远不触发；而原来那句
        // "兜底 TryEnqueue(done)" 又会立刻触发隐藏，导致 Completed 在下次打开后才跑到，
        // 把刚显示的窗口再隐藏一遍（WinUI 认为可见 / HWND 实际隐藏，后续操作就危险了）。
        StartAnim(false, done);
    }

    /// <summary>关一次就够，重复调用不会抛。</summary>
    /// <summary>按设置里的材质切换（0=Mica 1=Mica Alt 2=Acrylic 3=无）。</summary>
    private void ApplyBackdrop(int kind)
    {
        switch (kind)
        {
            case 0:
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                if (_root is not null) { _root.Background = null; }
                break;
            case 1:
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                if (_root is not null) { _root.Background = null; }
                break;
            case 2:
                SystemBackdrop = new DesktopAcrylicBackdrop();
                if (_root is not null) { _root.Background = null; }
                break;
            default:
                SystemBackdrop = null;
                if (_root is not null)
                {
                    _root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                }
                break;
        }
    }

    private void OnSettingsSaved(StreamSettings saved)
    {
        if (saved.BackdropKind == _settings.BackdropKind)
        {
            return;
        }

        _settings.BackdropKind = saved.BackdropKind;
        ApplyBackdrop(saved.BackdropKind);
    }

    private void SafeClose()
    {
        try { SetWindowPos(PanelHwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); } catch { }
        if (_closing)
        {
            return;
        }

        _closing = true;
        Diag("SafeClose");
        try { _focusWatch?.Stop(); } catch { }
        try { BeforeClose?.Invoke(); } catch { }
        try { AnimateCloseThenDone(() => { try { AppWindow.Hide(); } catch { } _closing = false; }); } catch { try { AppWindow.Hide(); } catch { } _closing = false; }
    }

    /// <summary>面板窗口的 HWND（给原生窗口做实时跟随）。窗口已销毁时返回 0。</summary>
    public IntPtr PanelHwnd
    {
        get
        {
            try
            {
                return Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>只移动/缩放，不激活（弹窗跟随串流窗口用）。</summary>
    public void Reposition(int x, int y, int width, int height)
        => AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

    private void ApplyAndKeepOpen()
    {
        var width = _settings.Width;
        var height = _settings.Height;
        if (_resolution.SelectedItem is ComboBoxItem item && item.Tag is ValueTuple<int, int> res)
        {
            width = res.Item1;
            height = res.Item2;
        }

        // Tag=(0,0) 是「本机分辨率」：这里必须解析成主屏真实分辨率，
        // 否则会把 0x0 传给 ApplyStreamSettings → RequestResolution 直接失败 → 回退重连并卡住。
        if (width <= 0 || height <= 0)
        {
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            width = (int)Math.Round(GetSystemMetrics(0) * scale);
            height = (int)Math.Round(GetSystemMetrics(1) * scale);
        }

        var bitrate = (int)Math.Round(_bitrate.Value) * 1000;
        _apply(width, height, bitrate, _fullScreen.IsOn);
    }
}
