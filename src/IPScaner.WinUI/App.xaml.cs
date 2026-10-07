using IPScaner.Core.Logging;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;

namespace IPScaner.WinUI;

/// <summary>
/// Application entry point. The Windows App SDK generates the <c>Main</c> and
/// bootstraps the runtime for this unpackaged app, so this class only handles
/// lifetime and unhandled-error reporting.
/// </summary>
public partial class App : Application
{
    /// <summary>The single main window; kept for dialog parenting and activation.</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Instance.Log("AppDomain", "未处理异常: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Instance.Log("Task", "未观察的任务异常: " + e.Exception.Message);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Initialize();

        MainWindow = new MainWindow();
        MainWindow.Activate();

        AppLog.Instance.Log(nameof(App), $"IPScaner WinUI 启动 (管理员={AppServices.Current.IsElevated})");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Instance.Log(nameof(App), "UI 未处理异常: " + e.Exception);
        // Keep the window alive where possible; log-only matches the original's
        // silent handlers while still leaving a trace on disk.
        e.Handled = true;
    }

    /// <summary>Flushes the log writer during shutdown.</summary>
    public static void ShutdownLogging() => AppLog.Instance.Shutdown();
}
