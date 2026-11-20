using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace MoonlightWinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 兜底：DispatcherQueue 定时器 / 后台线程里的未处理异常不走 XAML 的 UnhandledException，
        // 这类崩溃以前是"什么都不留直接闪退"；挂上这两个至少能记进 startup-error.txt。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("TaskScheduler", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log("OnLaunched", ex);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log("UnhandledException", e.Exception);
    }

    private static void Log(string tag, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
                $"[{DateTime.Now:HH:mm:ss}] {tag}: {ex}\n\n");
        }
        catch
        {
            // 连日志都写不了就算了
        }
    }
}
