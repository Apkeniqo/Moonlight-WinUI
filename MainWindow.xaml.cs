using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Graphics;
using Windows.UI;

namespace MoonlightWinUI;

/// <summary>
/// 启动器窗口：电脑卡片、配对、打开串流窗口。
/// 画面本身在 <see cref="StreamWindow"/> 里（每次连接新开一个窗口）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private StreamWindow? _streamWindow;
    private (string Host, bool Demo, int AppId, string? AppTitle)? _pendingOpen;
    private string? _currentHost;
    private int _currentHostPort = 47984;
    private string? _lastNavTag;
    private List<HostEntry> _hosts = new();
    private readonly Dictionary<string, TextBlock> _statusLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamSettings _settings = StreamSettings.Load();

    /// <summary>主窗口实例（串流窗口要在这里弹错误提示）。</summary>
    public static MainWindow? Instance { get; private set; }
    private FrameworkElement? _activePage;
    private bool _settingsReady;
    private long _lastAppLoadTick;
    private int _cardRowsRetries;   // 面板没生成时的有限重试次数
    private long _lastRunningCheckTick;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        Title = $"Moonlight V+ for PC  v{AppVersion.Current}";
        TitleText.Text = $"Moonlight V+ for PC   v{AppVersion.Current}";

        // WinUI 3 的关键能力：内容延伸进标题栏，但最小化/最大化/关闭仍由系统绘制
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootGrid.RequestedTheme = ElementTheme.Dark;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "backdrop.log"),
                $"{DateTime.Now:HH:mm:ss.fff} ===== 启动 v{AppVersion.Current} ====={Environment.NewLine}");
        }
        catch { }
        RootGrid.Loaded += (_, _) => ApplyBackdrop(_settings.BackdropKind);   // 按设置恢复，别写死 Mica

        // 单行卡片的两侧渐隐：滚动/改尺寸时重算每张卡的透明度
        AppScroll.ViewChanged += (_, _) => UpdateEdgeFade();
        AppScroll.SizeChanged += (_, _) => UpdateAppCardSize();
        HostPage.SizeChanged += (_, _) => UpdateAppCardSize();
        AppScroll.SizeChanged += (_, _) => UpdateEdgeFade();

        // AppWindow.Resize 收的是物理像素；按工作区留边，别超过屏幕
        RootGrid.Loaded += (_, _) =>
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var w = Math.Min(1500, work.Width - 80);
            var h = Math.Min(900, work.Height - 80);
            AppWindow.Resize(new SizeInt32(w, h));
        };

        BuildDeviceCards();
        InitSettingsControls();

        // 无头配对：ML_PAIR=<host> 启动即配对，结束后自动退出（方便脚本/批处理）
        var pairHost = Environment.GetEnvironmentVariable("ML_PAIR");
        if (!string.IsNullOrWhiteSpace(pairHost))
        {
            _ = RunHeadlessPairingAsync(pairHost.Trim());
        }

        // 自检：ML_TLS_TEST=<host> 只验「客户端证书 + HTTPS」这一段
        var tlsHost = Environment.GetEnvironmentVariable("ML_TLS_TEST");
        if (!string.IsNullOrWhiteSpace(tlsHost))
        {
            _ = RunTlsProbeAsync(tlsHost.Trim());
        }

        // 无头连接：ML_AUTOSTART=<host> 启动即开串流窗口（=demo 则演示流）
        var autoHost = Environment.GetEnvironmentVariable("ML_AUTOSTART");
        if (!string.IsNullOrWhiteSpace(autoHost))
        {
            var target = autoHost.Trim();
            RootGrid.Loaded += (_, _) =>
                OpenStreamWindow(target, demo: string.Equals(target, "demo", StringComparison.OrdinalIgnoreCase));
        }

        // 关主窗口时把串流窗口一起收掉，别留一个孤儿窗口占着主机会话
        // 回到主窗口（串流结束、切回前台）时轻量刷新"运行中"标记：
        // 只改 AppEntry.IsRunning，不重建列表，不会闪。
        Activated += async (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) { return; }
            if (HostPage.Visibility != Visibility.Visible) { return; }
            if (_currentHost is not { } host) { return; }
            if (Environment.TickCount64 - _lastRunningCheckTick < 2000) { return; }

            try { await RefreshRunningFlagsAsync(host); } catch { }
        };
        Closed += (_, _) =>
        {
            var stream = _streamWindow;
            _streamWindow = null;

            try { stream?.Close(); } catch { }

            // 主窗口关掉就是退出。WinUI 3 只要还有活窗口（比如隐藏过的控制面板）就不会
            // 结束进程，表现为任务管理器里留一个没有界面的后台程序，这里显式退出兜底。
            // 再挂一个 5 秒看门狗：万一原生会话收尾（LiStopConnection）卡住，
            // 直接强制结束，绝不留后台残留。
            var watchdog = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(5000);
                Environment.Exit(0);
            })
            { IsBackground = true, Name = "exit-watchdog" };
            watchdog.Start();

            Application.Current.Exit();
        };
    }

    // ---------------- 设备卡片 ----------------

    private void BuildDeviceCards()
    {
        _hosts = HostStore.Load();
        DeviceList.Items.Clear();
        _statusLabels.Clear();

        foreach (var host in _hosts)
        {
            var card = BuildCard(host, "检测中…", out var statusText);
            DeviceList.Items.Add(card);
            _statusLabels[host.Address] = statusText;
        }

        _ = RefreshHostStatusAsync();
    }

    private FrameworkElement BuildCard(HostEntry host, string status, out TextBlock statusText)
    {
        var icon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(12),
            // 统一灰色底（微软商店那种中性配色），靠图标本身区分
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 58)),
            Child = new FontIcon
            {
                Glyph = "\uE7F4",     // 统一用 PC 图标，底色区分设备
                FontSize = 26,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        statusText = new TextBlock
        {
            Text = status,
            FontSize = 12,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var texts = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(new TextBlock
        {
            Text = host.DisplayName,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        texts.Children.Add(statusText);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(texts);

        var card = new Border
        {
            Height = 96,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = row
        };
        card.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        card.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        // 整张卡片可点：进主机详情页，读 /applist（Tag 带整条记录，端口/显示名都在里面）
        // slot 固定尺寸（卡片 ×1.05），卡片在里面整体缩放，命中层不缩放。
        var slot = FluentCard.CreateSlot(card, DeviceList.Items.Count);
        slot.Tag = host;
        slot.Tapped += DeviceCard_Tapped;
        slot.RightTapped += DeviceCard_RightTapped;
        return slot;
    }
    /// <summary>电脑页的「刷新」按钮：重新探测所有主机的在线状态（探测期间转圈并禁用按钮）。</summary>
    private async void RefreshHostsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHostsButton.IsEnabled = false;
        HostsProbeRing.IsActive = true;

        try
        {
            await RefreshHostStatusAsync();
        }
        catch
        {
            // 探测本身是尽力而为，异常不冒泡到 UI
        }
        finally
        {
            HostsProbeRing.IsActive = false;
            RefreshHostsButton.IsEnabled = true;
        }
    }

    /// <summary>在线状态是真探测：TCP 连主机配置的 HTTP 端口（默认 47989），1.2s 超时。</summary>
    private async Task RefreshHostStatusAsync()
    {
        var snapshot = _hosts.ToList();
        if (snapshot.Count == 0)
        {
            return;
        }

        var probes = snapshot.Select(h => ProbeHostAsync(h.Address, h.Port)).ToArray();
        var results = await Task.WhenAll(probes);

        DispatcherQueue.TryEnqueue(() =>
        {
            for (var i = 0; i < snapshot.Count && i < results.Length; i++)
            {
                var host = snapshot[i];
                if (!_statusLabels.TryGetValue(host.Address, out var label))
                {
                    continue;
                }

                var online = results[i];
                label.Text = online
                    ? (string.Equals(host.DisplayName, host.Address, StringComparison.OrdinalIgnoreCase)
                        ? "在线"
                        : $"在线 · {host.Address}")
                    : "离线";
            }
        });
    }

    private static async Task<bool> ProbeHostAsync(string host, int port = 47989, int timeoutMs = 1200)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return finished == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }
    /// <summary>应用卡片：套用和电脑卡片一样的磁贴动画（按顺序错开进场）。</summary>
    private void AppCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border card)
        {
            var index = AppList.Items.IndexOf(card.DataContext);
            FluentCard.AttachInPlace(card, index > 0 ? index : 0);
        }

        // 面板此时一定生成了：把「应用卡片行数」应用上去（之前排队重试都跑在布局之前，等于没生效）
        ApplyAppCardRows();
    }
    private void DeviceCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HostEntry host } element)
        {
            return;
        }

        var menu = new MenuFlyout();

        var details = new MenuFlyoutItem { Text = "查看详情" };
        details.Click += async (_, _) => await ShowHostDetailsAsync(host);
        menu.Items.Add(details);

        var wol = new MenuFlyoutItem { Text = "远程唤醒（WOL）" };
        wol.Click += async (_, _) => await WakeHostAsync(host);
        menu.Items.Add(wol);

        menu.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "删除电脑" };
        remove.Click += async (_, _) => await RemoveHostAsync(host);
        menu.Items.Add(remove);

        menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private async Task ShowHostDetailsAsync(HostEntry host)
    {
        var lines = new List<string>
        {
            $"名称：{host.DisplayName}",
            $"地址：{host.Address}",
            $"HTTP 端口：{host.Port}",
            $"HTTPS 端口：{host.HttpsPort}",
            $"MAC：{(string.IsNullOrWhiteSpace(host.Mac) ? "（未记录）" : host.Mac)}",
        };

        if (!string.IsNullOrWhiteSpace(host.Mac) == false)
        {
            var mac = await HostApi.GetMacAsync(host.Address, host.Port);
            if (!string.IsNullOrWhiteSpace(mac))
            {
                host.Mac = mac!;
                HostStore.Save(_hosts);
                lines[^1] = $"MAC：{mac}";
            }
        }

        var dialog = new ContentDialog
        {
            Title = "电脑详情",
            Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async Task WakeHostAsync(HostEntry host)
    {
        var mac = host.Mac;
        if (string.IsNullOrWhiteSpace(mac))
        {
            mac = await HostApi.GetMacAsync(host.Address, host.Port) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(mac))
            {
                host.Mac = mac;
                HostStore.Save(_hosts);
            }
        }

        if (string.IsNullOrWhiteSpace(mac))
        {
            await new ContentDialog
            {
                Title = "远程唤醒",
                Content = new TextBlock { Text = "没有记录这台电脑的 MAC 地址，无法发送唤醒包。先连一次（或主机在线时查看详情）再试。", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot,
            }.ShowAsync();
            return;
        }

        var ok = WakeOnLan.Send(mac);
        await new ContentDialog
        {
            Title = "远程唤醒",
            Content = new TextBlock { Text = ok ? $"已向 {mac} 发送魔术包。" : "发送失败，请检查网络/防火墙。", TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    private async Task RemoveHostAsync(HostEntry host)
    {
        var dialog = new ContentDialog
        {
            Title = "删除电脑",
            Content = new TextBlock { Text = $"确定删除「{host.DisplayName}」吗？只删除本地记录，不影响主机。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _hosts.Remove(host);
        HostStore.Save(_hosts);
        BuildDeviceCards();
    }
    private void DeviceCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HostEntry host })
        {
            _ = ShowHostPageAsync(host);
        }
    }

    // ---------------- 主机详情页（应用列表） ----------------

    private async Task ShowHostPageAsync(HostEntry host)
    {
        _currentHost = host.Address;
        _currentHostPort = host.HttpsPort;
        SettingsHost.Visibility = Visibility.Collapsed;
        SwitchPage(ComputersPage, HostPage);
        HostTitle.Text = host.DisplayName;
        HostStatus.Text = string.Empty;
        HostMessage.Text = string.Empty;
        AppList.ItemsSource = null;

        await LoadAppsAsync(host.Address, host.HttpsPort);
    }

    /// <summary>
    /// 只刷新"运行中"标记：查主机的 currentgame，改已有 AppEntry 的 IsRunning。
    /// 不重建列表，所以不会有闪烁。
    /// </summary>
    private async Task RefreshRunningFlagsAsync(string host)
    {
        if (AppList.ItemsSource is not IEnumerable<AppEntry> apps)
        {
            return;
        }

        var list = apps.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _lastRunningCheckTick = Environment.TickCount64;

        var current = await Task.Run(() => HostApi.GetCurrentGame(host, _currentHostPort));

        // 等的时候可能已经换主机/离开页面
        if (!string.Equals(_currentHost, host, StringComparison.OrdinalIgnoreCase) ||
            HostPage.Visibility != Visibility.Visible)
        {
            return;
        }

        foreach (var app in list)
        {
            app.SetRunning(current != 0 && app.Id == current);
        }

        HostStatus.Text = current == 0
            ? $"{list.Count} 个应用"
            : $"{list.Count} 个应用 · 运行中：{list.FirstOrDefault(a => a.IsRunning)?.Title ?? current.ToString()}";
    }
    private async Task LoadAppsAsync(string host, int httpsPort = 47984)
    {
        HostLoading.IsActive = true;
        HostMessage.Text = "正在读取应用列表…";
        HostStatus.Text = string.Empty;
        AppList.ItemsSource = null;

        var result = await Task.Run(() => HostApi.GetAppList(host, httpsPort));
        _lastAppLoadTick = Environment.TickCount64;

        // 等的时候用户可能已经返回或换了主机
        if (!string.Equals(_currentHost, host, StringComparison.OrdinalIgnoreCase) ||
            HostPage.Visibility != Visibility.Visible)
        {
            return;
        }

        HostLoading.IsActive = false;
        HostMessage.Text = result.Success ? string.Empty : result.Message;
        HostStatus.Text = result.Success ? result.Message : "读取失败";
        AppList.ItemsSource = result.Apps;
        _cardRowsRetries = 0;
        ApplyAppCardRows();
        UpdateAppCardSize();
        AppScroll.ChangeView(0, null, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(UpdateEdgeFade);   // 等布局完成再算位置

        if (result.Success)
        {
            _ = LoadBoxArtAsync(host, result.Apps, httpsPort);
        }
    }

    /// <summary>逐张拉主机的封面图（3 路上限）；离开页面或换主机就停。</summary>
    private async Task LoadBoxArtAsync(string host, IReadOnlyList<AppEntry> apps, int httpsPort = 47984)
    {
        using var gate = new SemaphoreSlim(3);

        var tasks = apps.Select(async app =>
        {
            await gate.WaitAsync();
            try
            {
                if (!IsCurrent(host))
                {
                    return;
                }

                var bytes = await Task.Run(() => HostApi.GetBoxArt(host, app.Id, httpsPort));
                if (bytes is null || !IsCurrent(host))
                {
                    return;   // 主机没给这张图，保持占位图
                }

                var bitmap = await DecodeImageAsync(bytes);
                if (bitmap is not null && IsCurrent(host))
                {
                    app.BoxArt = bitmap;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        bool IsCurrent(string h) =>
            string.Equals(_currentHost, h, StringComparison.OrdinalIgnoreCase) &&
            HostPage.Visibility == Visibility.Visible;
    }

    private static async Task<BitmapImage?> DecodeImageAsync(byte[] bytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            writer.Dispose();

            stream.Seek(0);

            // 卡片只有 170 DIP 宽，按 340 px 解码就够，省内存
            var bitmap = new BitmapImage { DecodePixelWidth = 340 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void HostBackButton_Click(object sender, RoutedEventArgs e)
    {
        _currentHost = null;
        SettingsHost.Visibility = Visibility.Collapsed;
        SwitchPage(HostPage, ComputersPage);
    }

    private async void HostRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHost is { } host)
        {
            await LoadAppsAsync(host, _currentHostPort);
        }
    }

    private async void AppCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) { return; }
        if (element.DataContext is not AppEntry app) { return; }
        if (_currentHost is not { } host) { return; }

        // 正在运行的卡片：先问要做什么（恢复串流 / 结束主机上的应用）
        if (app.IsRunning)
        {
            ShowRunningAppMenu(element, host, app, e.GetPosition(element));
            return;
        }

        // 主机上已有别的应用在跑：问是恢复它，还是结束它并启动这张卡片
        try
        {
            var current = await Task.Run(() => HostApi.GetCurrentGame(host, _currentHostPort));
            if (current > 0 && current != app.Id)
            {
                var running = (AppList.ItemsSource as IEnumerable<AppEntry>)?.FirstOrDefault(a => a.Id == current);
                ShowSwitchAppMenu(element, host, running, app, e.GetPosition(element));
                return;
            }
        }
        catch
        {
            // 查询失败就按普通启动走
        }

        OpenStreamWindow(host, demo: false, app.Id, app.Title);
    }

    /// <summary>主机已有应用在跑时点别的卡片：恢复运行中的应用 / 结束它并启动新应用。</summary>
    private void ShowSwitchAppMenu(FrameworkElement target, string host, AppEntry? running, AppEntry targetApp,
                                   Windows.Foundation.Point position)
    {
        var flyout = new MenuFlyout();

        var runningTitle = running?.Title ?? "正在运行的应用";
        var runningId = running?.Id ?? 0;

        var resume = new MenuFlyoutItem
        {
            Text = $"恢复运行中的应用（{runningTitle}）",
            Icon = new FontIcon { Glyph = "\uE768" },
        };
        resume.Click += (_, _) => OpenStreamWindow(host, demo: false,
            runningId > 0 ? runningId : targetApp.Id, runningTitle);

        var switchTo = new MenuFlyoutItem
        {
            Text = $"退出当前应用并启动「{targetApp.Title}」",
            Icon = new FontIcon { Glyph = "\uE71A" },
        };
        switchTo.Click += async (_, _) =>
        {
            HostStatus.Text = $"正在结束「{runningTitle}」…";
            try
            {
                await Task.Run(() => HostApi.CancelRunningApp(host, _currentHostPort));
            }
            catch { }

            await Task.Delay(600);   // 给主机一点时间回收会话
            OpenStreamWindow(host, demo: false, targetApp.Id, targetApp.Title);
        };

        flyout.Items.Add(resume);
        flyout.Items.Add(switchTo);

        flyout.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    /// <summary>运行中卡片的菜单：恢复串流 / 让主机退出当前应用。</summary>
    private void ShowRunningAppMenu(FrameworkElement target, string host, AppEntry app, Windows.Foundation.Point position)
    {
        var flyout = new MenuFlyout();

        var resume = new MenuFlyoutItem
        {
            Text = "恢复串流",
            Icon = new FontIcon { Glyph = "\uE768" },   // ▶
        };
        resume.Click += (_, _) => OpenStreamWindow(host, demo: false, app.Id, app.Title);

        var stop = new MenuFlyoutItem
        {
            Text = "退出当前应用并结束串流",
            Icon = new FontIcon { Glyph = "\uE71A" },   // 取消
        };
        stop.Click += async (_, _) => await EndRunningAppAsync(host, app);

        flyout.Items.Add(resume);
        flyout.Items.Add(stop);

        flyout.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    /// <summary>让主机结束当前应用（Sunshine /cancel），然后把"运行中"标记刷新掉。</summary>
    private async Task EndRunningAppAsync(string host, AppEntry app)
    {
        HostStatus.Text = $"正在让主机退出「{app.Title}」…";

        bool ok;
        try
        {
            ok = await Task.Run(() => HostApi.CancelRunningApp(host, _currentHostPort));
        }
        catch
        {
            ok = false;
        }

        if (!string.Equals(_currentHost, host, StringComparison.OrdinalIgnoreCase)) { return; }

        HostStatus.Text = ok ? $"已请求主机退出「{app.Title}」" : $"让主机退出「{app.Title}」失败";
        await RefreshRunningFlagsAsync(host);
    }

    /// <summary>
    /// 两侧渐隐：按卡片在视口里露出的比例给透明度（完全在视口里 = 1，
    /// 被边缘裁掉越多越淡），滚动时实时更新，视觉上就是两端淡出。
    /// </summary>
    /// <summary>
    /// 滚动区两侧渐隐：贴边的一侧淡出覆盖层（Opacity 跟着滚动距离过渡），
    /// 滚到最左/最右时对应那侧完全不显示。
    /// </summary>
    /// <summary>
    /// 两侧渐隐：按卡片在视口里露出的比例给透明度（完全在视口里 = 1，
    /// 被边缘裁掉越多越淡），滚动时实时更新，视觉上就是两端淡出。
    /// </summary>
    private void UpdateEdgeFade()
    {
        var viewport = AppScroll.ActualWidth;
        if (viewport <= 1)
        {
            return;
        }

        foreach (var item in AppList.Items)
        {
            if (AppList.ContainerFromItem(item) is not FrameworkElement container)
            {
                continue;
            }

            var left = container.TransformToVisual(AppScroll).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            var width = container.ActualWidth;
            if (width <= 0)
            {
                continue;
            }

            var visible = Math.Min(left + width, viewport) - Math.Max(left, 0);
            var fraction = Math.Clamp(visible / width, 0, 1);

            // smoothstep，进出边缘时过渡更软
            container.Opacity = fraction * fraction * (3 - 2 * fraction);
        }
    }

    /// <summary>单行卡片：一格滚轮横移一张卡（170 + 32 间距），带平滑动画。</summary>
    private void AppScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(AppScroll).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var cardStep = (AppList.Items.OfType<AppEntry>().FirstOrDefault()?.SlotWidth ?? 170 * 1.05) + 8;
        var notches = Math.Max(1, Math.Abs(delta) / 120);
        var direction = delta > 0 ? -1 : 1;
        var target = Math.Clamp(
            AppScroll.HorizontalOffset + direction * notches * cardStep,
            0,
            AppScroll.ScrollableWidth);

        AppScroll.ChangeView(target, null, null, disableAnimation: false);
        e.Handled = true;
    }

    // ---------------- 串流窗口（画面独立） ----------------

    private void OpenStreamWindow(string host, bool demo, int appId = 1, string? appTitle = null)
    {
        if (_streamWindow is { } existing)
        {
            // 已经在放同一个流：直接把窗口提到前面
            if (existing.IsDemo == demo &&
                string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) &&
                (demo || existing.AppId == appId))
            {
                existing.Activate();
                return;
            }

            // 换流：先关旧窗口（它会 MS_Stop + ML_Close），关完再开新的
            _pendingOpen = (host, demo, appId, appTitle);
            existing.Closed += OnStreamWindowClosedThenReopen;
            existing.Close();
            return;
        }

        var window = new StreamWindow(host, demo, _settings.Clone(), appId, appTitle);
        window.Closed += OnStreamWindowClosed;
        _streamWindow = window;
        window.Activate();
    }

    private void OnStreamWindowClosed(object? sender, WindowEventArgs args)
    {
        var quitAfter = sender is StreamWindow w && w.QuitAfterStream;

        if (sender is StreamWindow window)
        {
            window.Closed -= OnStreamWindowClosed;
        }

        _streamWindow = null;

        // 只有不是「换流重开」的时候才按设置退出
        if (quitAfter && _pendingOpen is null)
        {
            Application.Current.Exit();
        }
    }

    private void OnStreamWindowClosedThenReopen(object? sender, WindowEventArgs args)
    {
        if (sender is StreamWindow window)
        {
            window.Closed -= OnStreamWindowClosedThenReopen;
        }

        OnStreamWindowClosed(sender, args);

        if (_pendingOpen is { } pending)
        {
            _pendingOpen = null;
            OpenStreamWindow(pending.Host, pending.Demo, pending.AppId, pending.AppTitle);
        }
    }

    // ---------------- 设置页 ----------------

    private async void CustomFpsButton_Click(object sender, RoutedEventArgs e)
    {
        var box = new NumberBox
        {
            Header = "刷新率（Hz）",
            Minimum = 10,
            Maximum = 240,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = _settings.Fps > 0 ? _settings.Fps : 60,
        };

        var dialog = new ContentDialog
        {
            Title = "添加自定义刷新率",
            Content = box,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || double.IsNaN(box.Value))
        {
            return;
        }

        var fps = (int)Math.Round(box.Value);
        if (fps < 10 || fps > 240)
        {
            PersistSettings("刷新率超出范围（10–240 Hz）");
            return;
        }

        if (!_settings.CustomRefreshRates.Contains(fps))
        {
            _settings.CustomRefreshRates.Add(fps);
            FpsBox.Items.Add(new ComboBoxItem { Content = $"{fps} Hz", Tag = fps });
        }

        _settings.Fps = fps;
        FpsBox.SelectedIndex = FpsBox.Items.Count - 1;
        PersistSettings($"已添加刷新率：{fps} Hz");
    }

    private async void CustomResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        var widthBox = new NumberBox
        {
            Header = "宽度",
            Minimum = 320,
            Maximum = 7680,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = _settings.Width > 0 ? _settings.Width : 1920,
        };

        var heightBox = new NumberBox
        {
            Header = "高度",
            Minimum = 200,
            Maximum = 4320,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = _settings.Height > 0 ? _settings.Height : 1080,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        panel.Children.Add(widthBox);
        panel.Children.Add(heightBox);

        var dialog = new ContentDialog
        {
            Title = "添加自定义分辨率",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (double.IsNaN(widthBox.Value) || double.IsNaN(heightBox.Value))
        {
            PersistSettings("自定义分辨率未填写");
            return;
        }

        var w = (int)Math.Round(widthBox.Value);
        var h = (int)Math.Round(heightBox.Value);

        if (w < 320 || h < 200 || w > 7680 || h > 4320)
        {
            PersistSettings("自定义分辨率超出范围（320×200 ~ 7680×4320）");
            return;
        }

        var key = w + "x" + h;
        if (!_settings.CustomResolutions.Contains(key))
        {
            _settings.CustomResolutions.Add(key);
            ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{w} × {h}", Tag = (w, h) });
        }

        _settings.Width = w;
        _settings.Height = h;
        ResolutionBox.SelectedIndex = ResolutionBox.Items.Count - 1;
        PersistSettings($"已添加分辨率：{w} × {h}");
    }

    private void InitSettingsControls()
    {
        // 「本机分辨率」放在最前，Tag 为 (0,0)，选它时按当前显示器解析
        ResolutionBox.Items.Add(new ComboBoxItem { Content = "本机分辨率", Tag = (0, 0) });

        foreach (var (width, height) in StreamSettings.Resolutions)
        {
            ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{width} × {height}", Tag = (width, height) });
        }

        foreach (var custom in _settings.CustomResolutions)
        {
            var parts = custom.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var cw) && int.TryParse(parts[1], out var ch))
            {
                ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{cw} × {ch}", Tag = (cw, ch) });
            }
        }

        foreach (var fps in StreamSettings.RefreshRates)
        {
            FpsBox.Items.Add(new ComboBoxItem { Content = $"{fps} Hz", Tag = fps });
        }

        _settingsReady = false;

        var resolutionIndex = Array.FindIndex(
            StreamSettings.Resolutions,
            r => r.Width == _settings.Width && r.Height == _settings.Height);
        ResolutionBox.SelectedIndex = resolutionIndex >= 0 ? resolutionIndex : 2;   // 默认 1920×1080

        var fpsIndex = Array.IndexOf(StreamSettings.RefreshRates, _settings.Fps);
        FpsBox.SelectedIndex = fpsIndex >= 0 ? fpsIndex : 1;                        // 默认 60

        BitrateBox.Value = Math.Clamp(_settings.BitrateKbps / 1000.0, 5, 100);
        MouseSensitivitySlider.Value = Math.Clamp(_settings.MouseSensitivity, 0.3, 2.0);
        MouseSensitivityText.Text = $"{_settings.MouseSensitivity:F2}×";
        CodecBox.SelectedIndex = Math.Clamp(_settings.Codec, 0, 3);
        WindowModeBox.SelectedIndex = _settings.FullScreen ? 1 : 0;
        SwapMouseSwitch.IsOn = _settings.SwapMouseButtons;
        ReverseScrollSwitch.IsOn = _settings.ReverseScroll;
        ClipboardImageSwitch.IsOn = _settings.ClipboardImageSync;

        if (_settings.BackdropKind >= 0 && _settings.BackdropKind <= 3)
        {
            BackdropBox.SelectedIndex = _settings.BackdropKind;
        }

        AppRowsBox.SelectedIndex = (_settings.AppCardRows == 1) ? 0 : 1;
        PerfOverlaySwitch.IsOn = _settings.ShowPerformanceOverlay;
        OverlayOpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity * 100, 20, 100);
        OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value:F0}%";
        OverlayPositionBox.SelectedIndex = Math.Clamp(_settings.OverlayPosition, 0, 5);
        KeepAwakeSwitch.IsOn = _settings.KeepAwake;
        QuitAfterSwitch.IsOn = _settings.QuitAfterStream;
        SettingsPathText.Text = "配置文件：" + StreamSettings.FilePath;
        UpdateBitrateText();

        _settingsReady = true;

        // 材质和卡片行数要等窗口就绪后再应用：加载期间直接设 SystemBackdrop 会偶发 XAML 崩溃
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackdrop(_settings.BackdropKind);
            ApplyAppCardRows();
        });
    }

    private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || ResolutionBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not ValueTuple<int, int> resolution)
        {
            return;
        }

        _settings.Width = resolution.Item1;
        _settings.Height = resolution.Item2;

        // Tag=(0,0) 表示「本机分辨率」：取主屏物理分辨率（A 方案）
        if (_settings.Width <= 0 || _settings.Height <= 0)
        {
            var (nativeWidth, nativeHeight) = ScreenInfo.PrimaryPixels();
            _settings.Width = nativeWidth;
            _settings.Height = nativeHeight;
        }

        PersistSettings($"{_settings.Width}×{_settings.Height}");

        // 立刻生效：串流中直接把新分辨率/码率/全屏推给当前会话
        _streamWindow?.ApplyStreamSettings(_settings.Width, _settings.Height, _settings.BitrateKbps, _settings.FullScreen);
    }

    private void FpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || FpsBox.SelectedItem is not ComboBoxItem item || item.Tag is not int fps)
        {
            return;
        }

        _settings.Fps = fps;
        PersistSettings($"{_settings.Width}×{_settings.Height}@{fps}");
    }

    private void MouseSensitivitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.MouseSensitivity = Math.Round(e.NewValue, 2);
        MouseSensitivityText.Text = $"{_settings.MouseSensitivity:F2}×";
        PersistSettings($"指针灵敏度 {_settings.MouseSensitivity:F2}×");
    }

    private void BitrateBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_settingsReady || double.IsNaN(e.NewValue))
        {
            return;
        }

        _settings.BitrateKbps = (int)Math.Round(e.NewValue) * 1000;
        UpdateBitrateText();
        PersistSettings($"{_settings.BitrateKbps / 1000.0:F0} Mbps");
        _streamWindow?.ApplyStreamSettings(_settings.Width, _settings.Height, _settings.BitrateKbps, _settings.FullScreen);
    }
    private void CodecBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || CodecBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.Codec = CodecBox.SelectedIndex;
        var name = CodecBox.SelectedIndex switch
        {
            1 => "H.264",
            2 => "HEVC",
            3 => "AV1（软件解码）",
            _ => "自动",
        };
        PersistSettings("编码：" + name);
    }

    private void CornerStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || CornerStyleBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.OverlayCornerSingleLine = CornerStyleBox.SelectedIndex == 0;
        PersistSettings(_settings.OverlayCornerSingleLine ? "性能信息：四角单行" : "性能信息：四角多行");
    }

    private async void OverlayItemsButton_Click(object sender, RoutedEventArgs e)
    {
        var items = new (string Label, int Bit)[]
        {
            ("分辨率", 1 << 6),
            ("FPS", 1 << 0),
            ("编码格式", 1 << 1),
            ("解码耗时", 1 << 2),
            ("网络延迟", 1 << 3),
            ("丢包率", 1 << 4),
            ("码率", 1 << 5),
        };

        var panel = new StackPanel { Spacing = 8 };
        var boxes = new List<CheckBox>();

        foreach (var it in items)
        {
            var cb = new CheckBox
            {
                Content = it.Label,
                IsChecked = (_settings.OverlayItems & it.Bit) != 0,
                Tag = it.Bit,
            };
            boxes.Add(cb);
            panel.Children.Add(cb);
        }

        var dialog = new ContentDialog
        {
            Title = "性能信息显示项目",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var mask = 0;
        foreach (var cb in boxes)
        {
            if (cb.IsChecked == true)
            {
                mask |= (int)cb.Tag!;
            }
        }

        _settings.OverlayItems = mask;
        PersistSettings($"性能信息项目：0x{mask:X}");
    }

    private void WindowModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || WindowModeBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.FullScreen = WindowModeBox.SelectedIndex == 1;
        PersistSettings(WindowModeBox.SelectedIndex == 1 ? "窗口模式：全屏" : "窗口模式：窗口");
        _streamWindow?.ApplyStreamSettings(_settings.Width, _settings.Height, _settings.BitrateKbps, _settings.FullScreen);
    }

    private void SwapMouseSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.SwapMouseButtons = SwapMouseSwitch.IsOn;
        PersistSettings(SwapMouseSwitch.IsOn ? "交换鼠标左右键：开" : "交换鼠标左右键：关");
    }

    private void ReverseScrollSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.ReverseScroll = ReverseScrollSwitch.IsOn;
        PersistSettings(ReverseScrollSwitch.IsOn ? "反转滚轮：开" : "反转滚轮：关");
    }

    private void ClipboardImageSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.ClipboardImageSync = ClipboardImageSwitch.IsOn;
        PersistSettings(ClipboardImageSwitch.IsOn ? "剪贴板图片：开" : "剪贴板图片：关");
    }

    private void PerfOverlaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.ShowPerformanceOverlay = PerfOverlaySwitch.IsOn;
        PersistSettings(PerfOverlaySwitch.IsOn ? "显示性能信息：开" : "显示性能信息：关");
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (OverlayOpacityText is not null)
        {
            OverlayOpacityText.Text = $"{e.NewValue:F0}%";
        }

        if (!_settingsReady)
        {
            return;
        }

        _settings.OverlayOpacity = Math.Clamp(e.NewValue / 100.0, 0.2, 1.0);
        PersistSettings($"性能信息透明度：{e.NewValue:F0}%");
    }

    private void OverlayPositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || OverlayPositionBox.SelectedIndex < 0)
        {
            return;
        }

        _settings.OverlayPosition = OverlayPositionBox.SelectedIndex;
        var names = new[] { "左上", "上中", "右上", "左下", "下中", "右下" };
        UpdateCornerRowVisibility();
        PersistSettings("性能信息位置：" + names[Math.Clamp(_settings.OverlayPosition, 0, names.Length - 1)]);
    }

    /// <summary>四角（0/2/3/5）才显示「四角单行/多行」选项。</summary>
    private void UpdateCornerRowVisibility()
    {
        var idx = OverlayPositionBox.SelectedIndex;
        var isCorner = idx == 0 || idx == 2 || idx == 3 || idx == 5;
        CornerRow.Visibility = isCorner ? Visibility.Visible : Visibility.Collapsed;
    }
    private void KeepAwakeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.KeepAwake = KeepAwakeSwitch.IsOn;
        PersistSettings(KeepAwakeSwitch.IsOn ? "保持屏幕常亮：开" : "保持屏幕常亮：关");
    }

    private void QuitAfterSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        _settings.QuitAfterStream = QuitAfterSwitch.IsOn;
        PersistSettings(QuitAfterSwitch.IsOn ? "串流结束后退出应用：开" : "串流结束后退出应用：关");
    }

    private void UpdateBitrateText()
    {
        if (BitrateText is not null)
        {
            BitrateText.Text = $"{BitrateBox.Value:F0} Mbps";
        }
    }

    /// <summary>主窗口上弹一个错误对话框（串流重连失败等场景用）。</summary>
    public async System.Threading.Tasks.Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
                XamlRoot = RootGrid.XamlRoot,
            };

            await dialog.ShowAsync();
        }
        catch
        {
            // 弹不出来就算了，别把异常抛到 UI 线程
        }
    }

    private void PersistSettings(string summary)
    {
        _settings.Save();
        SettingsStatusText.Text = $"已保存：{summary}";
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new StreamSettings();

        _settingsReady = false;
        ResolutionBox.SelectedIndex = 2;    // 1920×1080
        FpsBox.SelectedIndex = 1;           // 60
        BitrateBox.Value = defaults.BitrateKbps / 1000.0;
        CodecBox.SelectedIndex = 0;         // 自动
        WindowModeBox.SelectedIndex = 0;    // 窗口
        SwapMouseSwitch.IsOn = defaults.SwapMouseButtons;
        AppRowsBox.SelectedIndex = defaults.AppCardRows == 1 ? 0 : 1;
        ReverseScrollSwitch.IsOn = defaults.ReverseScroll;
        ClipboardImageSwitch.IsOn = defaults.ClipboardImageSync;
        PerfOverlaySwitch.IsOn = defaults.ShowPerformanceOverlay;
        OverlayOpacitySlider.Value = defaults.OverlayOpacity * 100;
        OverlayPositionBox.SelectedIndex = 0;
        KeepAwakeSwitch.IsOn = defaults.KeepAwake;
        QuitAfterSwitch.IsOn = defaults.QuitAfterStream;
        _settingsReady = true;

        _settings.Width = defaults.Width;
        _settings.Height = defaults.Height;
        _settings.Fps = defaults.Fps;
        _settings.BitrateKbps = defaults.BitrateKbps;
        _settings.Codec = defaults.Codec;
        _settings.FullScreen = defaults.FullScreen;
        _settings.SwapMouseButtons = defaults.SwapMouseButtons;
        _settings.AbsoluteMouse = defaults.AbsoluteMouse;
        _settings.MouseSensitivity = defaults.MouseSensitivity;
        _settings.ReverseScroll = defaults.ReverseScroll;
        _settings.ClipboardImageSync = defaults.ClipboardImageSync;
        _settings.AppCardRows = defaults.AppCardRows;
        _settings.ShowPerformanceOverlay = defaults.ShowPerformanceOverlay;
        _settings.HideRemoteCursor = defaults.HideRemoteCursor;
        _settings.OverlayOpacity = defaults.OverlayOpacity;
        _settings.OverlayPosition = defaults.OverlayPosition;
        _settings.KeepAwake = defaults.KeepAwake;
        _settings.QuitAfterStream = defaults.QuitAfterStream;
        _settings.Save();

        UpdateBitrateText();
        SettingsStatusText.Text = "已恢复默认：1920×1080@60 · 20 Mbps";
    }

    /// <summary>保存后如果正开着串流，关掉它并用新参数重开（参数只在建连时读取）。</summary>
    // ---------------- 配对（/pair，Sunshine 兼容） ----------------

    /// <summary>右上角「配对电脑」：弹出模态对话框（主界面此时不可操作），填 IP/端口后开始配对。</summary>
    /// <summary>侧边栏「配对电脑」入口。</summary>
    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && (item.Tag as string) == "pair")
        {
            // 配对不是页面：点完把选中项退回「电脑」，避免高亮停在配对上
            if (Nav.MenuItems.Count > 0 && Nav.MenuItems[0] is NavigationViewItem first)
            {
                Nav.SelectedItem = first;
            }

            _ = StartPairingAsync();
        }
    }

    private async System.Threading.Tasks.Task StartPairingAsync()
    {
        var ipBox = new TextBox
        {
            Header = "IP / 主机名",
            Text = string.Empty,
            PlaceholderText = "例如 192.168.1.100 或 mycomputer.top",
        };
        var portBox = new TextBox
        {
            Header = "端口",
            Text = "47989",
            PlaceholderText = "HTTP 端口，HTTPS 会自动取 -5",
        };
        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Text = "点「连接」后，请在主机的网页（https://<IP>:47990）输入弹出的 PIN。",
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(ipBox);
        panel.Children.Add(portBox);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "配对电脑",
            Content = panel,
            PrimaryButtonText = "连接",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            var succeeded = false;
            var pairedHost = string.Empty;
            var pairedPort = 47989;
            try
            {
                var host = ipBox.Text.Trim();
                pairedHost = host;
                var port = int.TryParse(portBox.Text.Trim(), out var parsed) && parsed > 6 ? parsed : 47989;
                pairedPort = port;

                if (host.Length == 0)
                {
                    status.Text = "请先填 IP 或主机名。";
                    return;
                }

                // 配对要等主机网页确认，可能几十秒：先挂住对话框，别让它关掉
                args.Cancel = true;
                ipBox.IsEnabled = false;
                portBox.IsEnabled = false;

                // 换 IP 的话主机还是那一台：先拿 hostname 看本地列表里是不是已经有它，
                // 有就直接把地址换成新的（不需要重新配对）。
                // /serverinfo 的 PairStatus 不能当判据 —— 它是按 uniqueid 判的，
                // uid 与主机记录的 uuid 不一致时会返回 0，但证书其实一直被接受。
                status.Text = "正在检查这台电脑是否添加过…";
                var knownName = await HostApi.GetHostnameAsync(host, port);
                var alreadyKnown =
                    (!string.IsNullOrWhiteSpace(knownName) &&
                     _hosts.Any(h => string.Equals(h.Name, knownName, StringComparison.OrdinalIgnoreCase))) ||
                    _hosts.Any(h => string.Equals(h.Address, host, StringComparison.OrdinalIgnoreCase)) ||
                    await HostApi.IsPairedAsync(host, port);

                if (alreadyKnown)
                {
                    status.Text = $"这台电脑已经在列表里，直接把地址换成 {host}（不需要重新配对）";
                    succeeded = true;
                }
                else
                {
                    var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
                    status.Text = $"PIN {pin} —— 请在 https://{host}:{port + 1} 的 PIN 页输入并确认…";

                    var (success, message) = await RunPairingAsync(host, pin, port);
                    status.Text = success ? $"配对成功：{host}" : $"配对失败：{message}";
                    succeeded = success;

                    if (!success)
                    {
                        ipBox.IsEnabled = true;
                        portBox.IsEnabled = true;
                    }
                }
            }
            finally
            {
                deferral.Complete();
            }

            if (succeeded)
            {
                await AddHostAfterPairingAsync(pairedHost, pairedPort);
                await Task.Delay(600);
                dialog.Hide();
            }
        };

        await dialog.ShowAsync();
    }

    /// <summary>配对成功后把这台电脑写进 hosts.json，并重建卡片（显示名优先用主机自己报的 hostname）。</summary>
    private async Task AddHostAfterPairingAsync(string address, int port = 47989)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var name = await HostApi.GetHostnameAsync(address, port);
        var added = HostStore.AddOrUpdate(_hosts, address, name, port);
        HostStore.Save(_hosts);
        BuildDeviceCards();
        PairingLog($"host list {(added ? "+" : "~")} {address}:{port}（{name ?? "未知主机名"}）");
    }

    private async Task<(bool Success, string Message)> RunPairingAsync(string host, string pin, int httpPort = 47989)
    {
        var baseDir = AppContext.BaseDirectory;
        var uniqueIdPath = System.IO.Path.Combine(baseDir, "creds", "uniqueid.txt");
        var uniqueId = System.IO.File.ReadAllText(uniqueIdPath).Trim();

        var result = await Pairing.PairAsync(
            host,
            pin,
            System.IO.Path.Combine(baseDir, "creds", "client.crt"),
            System.IO.Path.Combine(baseDir, "creds", "client.key"),
            uniqueId,
            httpPort,
            PairingLog);

        PairingLog($"result host={host} port={httpPort} pin={pin} success={result.Success} message={result.Message}");
        return (result.Success, result.Message);
    }

    /// <summary>ML_PAIR=&lt;host&gt; [ML_PAIR_PIN=1234]：不点界面，直接配对，结束后退出（脚本用）。</summary>
    private async Task RunHeadlessPairingAsync(string host)
    {
        var pin = Environment.GetEnvironmentVariable("ML_PAIR_PIN");
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 4 || !int.TryParse(pin, out _))
        {
            pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
        }

        PairingLog($"headless start host={host} pin={pin}");

        var (success, message) = await RunPairingAsync(host, pin);
        PairingLog($"headless done success={success} message={message}");
        await Task.Delay(300);
        DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
    }

    private static void PairingLog(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "pairing.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>ML_TLS_TEST=&lt;host&gt;：只验客户端证书 + HTTPS，结束退出。</summary>
    private async Task RunTlsProbeAsync(string host)
    {
        var baseDir = AppContext.BaseDirectory;
        var uniqueId = System.IO.File.ReadAllText(System.IO.Path.Combine(baseDir, "creds", "uniqueid.txt")).Trim();
        var result = await Pairing.ProbeTlsAsync(
            host,
            System.IO.Path.Combine(baseDir, "creds", "client.crt"),
            System.IO.Path.Combine(baseDir, "creds", "client.key"),
            uniqueId);
        PairingLog("tls probe: " + result);
        await Task.Delay(200);
        DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
    }

    // ---------------- 材质 ----------------

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady) { return; }   // 加载期间的 SelectedIndex 赋值不要触发材质切换
        if (BackdropBox.SelectedIndex >= 0)
        {
            ApplyBackdrop(BackdropBox.SelectedIndex);
        }
    }

    private void AppRowsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || RootGrid is null) { return; }

        _cardRowsRetries = 0;
        var rows = AppRowsBox.SelectedIndex == 0 ? 1 : 2;
        if (_settings.AppCardRows != rows)
        {
            _settings.AppCardRows = rows;
            PersistSettings(rows == 1 ? "应用卡片行数：单行" : "应用卡片行数：双行");
        }

        ApplyAppCardRows();
    }

    private void ApplyBackdrop(int index)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "backdrop.log"),
                $"{DateTime.Now:HH:mm:ss.fff} ApplyBackdrop({index}){Environment.NewLine}");
        }
        catch { }

        // XAML 解析期间 SelectedIndex 会先触发一次 SelectionChanged，此时控件还没就绪
        if (RootGrid is null)
        {
            return;
        }

        if (_settingsReady && _settings.BackdropKind != index)
        {
            _settings.BackdropKind = index;
            PersistSettings("窗口材质：" + index);
        }

        switch (index)
        {
            case 0:
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                RootGrid.Background = null;
                break;
            case 1:
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                RootGrid.Background = null;
                break;
            case 2:
                SystemBackdrop = new DesktopAcrylicBackdrop();
                RootGrid.Background = null;
                break;
            default:
                SystemBackdrop = null;
                RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
                break;
        }
    }

    // ---------------- 导航 ----------------

    /// <summary>切页时的过渡：淡入 + 从下方 16px 上滑（Composition，无布局开销）。</summary>
    private void AnimatePageIn(FrameworkElement page)
    {
        if (page is null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(page);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        visual.Properties.InsertVector3("Translation", new Vector3(0f, 24f, 0f));
        visual.Opacity = 0f;

        // 微软商店那种缓动：起步快、收尾柔
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(220);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(300);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>出页：快速淡出后隐藏（和进页的滑动叠在一起，像商店的页面切换）。</summary>
    private void AnimatePageOut(FrameworkElement page)
    {
        if (page is null || page.Visibility != Visibility.Visible)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(page);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(120);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3(0f, -12f, 0f));
        slide.Duration = TimeSpan.FromMilliseconds(120);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (ReferenceEquals(page, _activePage))
            {
                return;   // 又切回来了：别把当前页隐藏掉
            }

            page.Visibility = Visibility.Collapsed;
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        };
    }

    /// <summary>切页：旧页淡出、新页淡入上滑。</summary>
    private void SwitchPage(FrameworkElement? outgoing, FrameworkElement incoming)
    {
        if (outgoing is not null && !ReferenceEquals(outgoing, incoming))
        {
            AnimatePageOut(outgoing);
        }

        _activePage = incoming;
        // 刷新按钮只在「电脑」页显示（它挂在内容容器上，位置贴右上角）
        RefreshHostsPanel.Visibility = ReferenceEquals(incoming, ComputersPage) ? Visibility.Visible : Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;
        AnimatePageIn(incoming);
    }
    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag as string;

        if (tag is not null && tag != "pair")
        {
            // NavigationView 有时会重复触发同一个选中项（重新激活/重载时），
            // 那时不能把主机详情页关掉
            if (tag == _lastNavTag)
            {
                return;
            }

            _lastNavTag = tag;
            var showSettings = tag == "settings";

            // 从侧边栏切页时退出主机详情页
            _currentHost = null;
            HostPage.Visibility = Visibility.Collapsed;

            var incomingPage = showSettings ? (FrameworkElement)SettingsHost : ComputersPage;
            var outgoingPage = showSettings ? (FrameworkElement)ComputersPage : SettingsHost;
            SwitchPage(outgoingPage, incomingPage);

            // 进设置页时刷新「主机显示缩放」的主机列表
            if (showSettings)
            {
                RefreshScaleHosts();
            }

            // 回到电脑页时重新探一遍在线状态
            if (!showSettings)
            {
                _ = RefreshHostStatusAsync();
            }

            Title = tag switch
            {
                "settings" => "设置 - Moonlight V+ for PC",
                "help" => "帮助 - Moonlight V+ for PC",
                _ => "Moonlight V+ for PC"
            };
        }
    }
    // ---------------- 主机显示缩放（Sunshine 扩展：/display-scale*） ----------------

    private string? _scaleHost;
    private DisplayScaleInfo? _scaleInfo;
    private bool _scaleApplying;

    /// <summary>设置页打开时刷新主机下拉，并读取第一台主机的缩放档位。</summary>
    private void RefreshScaleHosts()
    {
        var previous = (ScaleHostBox.SelectedItem as ComboBoxItem)?.Tag as string;

        ScaleHostBox.SelectionChanged -= ScaleHostBox_SelectionChanged;
        ScaleHostBox.Items.Clear();
        foreach (var host in _hosts)
        {
            ScaleHostBox.Items.Add(new ComboBoxItem { Content = host.DisplayName, Tag = host.Address });
        }
        ScaleHostBox.SelectionChanged += ScaleHostBox_SelectionChanged;

        if (ScaleHostBox.Items.Count == 0)
        {
            HostScaleBox.Items.Clear();
            HostScaleBox.IsEnabled = false;
            HostScaleHint.Text = "还没有已配对的电脑，先在「配对电脑」里添加一台。";
            return;
        }

        var index = 0;
        if (!string.IsNullOrEmpty(previous))
        {
            for (var i = 0; i < ScaleHostBox.Items.Count; i++)
            {
                if (ScaleHostBox.Items[i] is ComboBoxItem item && (string?)item.Tag == previous)
                {
                    index = i;
                    break;
                }
            }
        }

        ScaleHostBox.SelectedIndex = index;   // 触发读取
    }

    private async void ScaleHostBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady) { return; }

        var host = (ScaleHostBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(host)) { return; }

        _scaleHost = host;
        _scaleInfo = null;
        HostScaleBox.Items.Clear();
        HostScaleBox.IsEnabled = false;
        HostScaleRing.IsActive = true;
        HostScaleHint.Text = $"正在读取 {host} 的显示缩放…";

        var info = await System.Threading.Tasks.Task.Run(() => HostApi.GetDisplayScale(host));

        HostScaleRing.IsActive = false;
        _scaleInfo = info;

        if (info is null)
        {
            HostScaleHint.Text = "读取失败：主机离线、未配对，或该主机不支持显示缩放接口。";
            return;
        }

        if (!info.IsAvailable)
        {
            var current = info.CurrentScalePercent is int value ? $"{value}%" : "未知";
            HostScaleHint.Text = $"主机不支持通过接口调整缩放（当前 {current}）。";
            return;
        }

        _scaleApplying = true;
        foreach (var percent in info.SupportedScalePercents)
        {
            HostScaleBox.Items.Add(new ComboBoxItem { Content = $"{percent}%", Tag = percent });
        }

        for (var i = 0; i < HostScaleBox.Items.Count; i++)
        {
            if (HostScaleBox.Items[i] is ComboBoxItem item && item.Tag is int percent &&
                percent == info.CurrentScalePercent)
            {
                HostScaleBox.SelectedIndex = i;
                break;
            }
        }
        _scaleApplying = false;

        HostScaleBox.IsEnabled = true;
        HostScaleHint.Text = $"当前 {info.CurrentScalePercent ?? 0}% · {info.FriendlyName}（{info.DisplayName}）";
    }

    private async void HostScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || _scaleApplying || _scaleInfo is null || string.IsNullOrWhiteSpace(_scaleHost))
        {
            return;
        }

        if ((HostScaleBox.SelectedItem as ComboBoxItem)?.Tag is not int percent) { return; }
        if (percent == _scaleInfo.CurrentScalePercent) { return; }

        var host = _scaleHost;
        var displayName = _scaleInfo.DisplayName;
        var deviceId = _scaleInfo.DeviceId;

        HostScaleRing.IsActive = true;
        HostScaleHint.Text = $"正在把 {host} 的显示缩放设置为 {percent}%…";

        var result = await System.Threading.Tasks.Task.Run(
            () => HostApi.SetDisplayScale(host, percent, displayName, deviceId));

        HostScaleRing.IsActive = false;

        if (result is null || !result.Success)
        {
            HostScaleHint.Text = "设置失败：" + (result?.StatusMessage ?? "主机无响应");
            return;
        }

        var now = result.CurrentScalePercent ?? percent;
        HostScaleHint.Text = result.RestartRequired
            ? $"已接受 {percent}%，主机需要重启显示后才生效。"
            : result.EffectiveImmediately
                ? $"已设置为 {now}%（立即生效）"
                : $"主机已接受 {percent}%，稍后生效。";

        _scaleInfo = _scaleInfo with { CurrentScalePercent = now };
    }

    /// <summary>应用卡片大小跟着整页高度走（两行排列，保持 170:240 封面比例）。</summary>
    /// <summary>把「应用卡片行数」应用到面板上（1 或 2 行），并重算卡片尺寸。</summary>
    private void ApplyAppCardRows()
    {
        var __diagRows = _settings.AppCardRows == 1 ? 1 : 2;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "backdrop.log"),
                $"{DateTime.Now:HH:mm:ss.fff} ApplyAppCardRows(rows={__diagRows}, panel={(AppList.ItemsPanelRoot?.GetType().Name ?? "null")}, items={AppList.Items.Count}){Environment.NewLine}");
        }
        catch { }

        var rows = _settings.AppCardRows == 1 ? 1 : 2;

        if (AppList.ItemsPanelRoot is HorizontalGridPanel panel)
        {
            _cardRowsRetries = 0;
            if (panel.Rows != rows) { panel.Rows = rows; }
            panel.Spacing = rows == 1 ? 32 : 20;   // 单行时间距放大
            UpdateAppCardSize();
            return;
        }

        // 面板还没生成：只在列表已经有内容时重试，且最多 3 次（之前无上限导致主线程被灌满卡死）
        if (AppList.Items.Count == 0 || _cardRowsRetries >= 3)
        {
            return;
        }

        _cardRowsRetries++;
        DispatcherQueue.TryEnqueue(ApplyAppCardRows);
    }

    private void UpdateAppCardSize()
    {
        var available = HostPage.ActualHeight;
        if (available < 200)
        {
            // 在设置页里切「应用卡片行数」时 HostPage 是隐藏的（高度 0），
            // 这里直接 return 就导致卡片尺寸永远不更新 —— 看起来就是"行数切换没生效"。
            // 用窗口内容区高度兜底，保证任何时候都能算出尺寸。
            available = RootGrid.ActualHeight;
            if (available < 200)
            {
                return;
            }
        }

        // 两行：卡片（含标题和 12px 下边距）合计约占页面高度的 86%，再扣掉行距
        var rows = _settings.AppCardRows == 1 ? 1 : 2;

        // 顺手同步面板行数：UpdateAppCardSize 在 SizeChanged / 卡片加载后都会调用，时机可靠
        if (AppList.ItemsPanelRoot is HorizontalGridPanel rowsPanel && rowsPanel.Rows != rows)
        {
            rowsPanel.Rows = rows;
            rowsPanel.Spacing = rows == 1 ? 32 : 20;
        }
        // 格子高度是卡片的 1.05 倍（给悬停放大留空间），这里除以 1.05 保持整体高度不变
        var height = Math.Clamp((available * 0.86 - (rows - 1) * 20) / rows / 1.05 - 40, 110, 300);
        if (rows == 1)
        {
            height *= 0.8;   // 单行时卡片缩到 80%
        }
        var width = height * 170.0 / 240.0;

        foreach (var app in AppList.Items.OfType<AppEntry>())
        {
            app.CardHeight = height;
            app.CardWidth = width;
        }
    }
}
