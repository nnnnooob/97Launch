using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Serilog;
using Serilog.Events;

namespace Launcher.App;

/// <summary>
/// Application entry point.
/// </summary>
/// <remarks>
/// <c>Program.Main</c> handles update commands before starting XAML and
/// constructing this class. Logging is therefore
/// configured in the constructor before anything else touches the container,
/// so failures during startup are captured rather than lost.
/// </remarks>
public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public static IServiceProvider Services => AppServiceProvider.Current;
    private MainWindow? _shellWindow;
    private AuthorizationWindow? _authorizationWindow;
    private DispatcherQueue _dispatcher = null!;
    private readonly DispatcherTimer _authorizationTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _switchingWindows;
    private bool _shuttingDown;

    public App()
    {
        ConfigureLogging();

        Log.Information("97Launch {Version} 正在启动", AppInfo.Version);
        Log.Information("日志目录：{Path}", AppInfo.LogDirectory);
        Log.Information("程序目录：{Path}", AppInfo.ApplicationDirectory);
        Log.Information("资源目录：{Path}", AppContext.BaseDirectory);

        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log.Debug("App constructor completed");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Debug("OnLaunched entered");

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var authorization = Services.GetRequiredService<LauncherAuthorizationService>();
        authorization.Changed += AuthorizationChanged;
        _authorizationTimer.Tick += async (_, _) => await authorization.CheckAsync();
        ShowAuthorizationWindow();
        _ = InitializeApplicationAsync(authorization);
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, UpdateBootstrap.AcknowledgeStartup);
    }

    private async Task InitializeApplicationAsync(LauncherAuthorizationService authorization)
    {
        await authorization.InitializeAsync();
        try { await Services.GetRequiredService<AppUpdateService>().InitializeAsync(); }
        catch (Exception ex) { Log.Warning("初始化更新服务失败：{ErrorType}", ex.GetType().Name); }
    }

    internal static void RequestExitForUpdate()
    {
        if (Current is not App app) return;
        if (app._shellWindow is not null) app._shellWindow.Close();
        else if (app._authorizationWindow is not null) app._authorizationWindow.Close();
        else app.Exit();
    }

    private void AuthorizationChanged(object? sender, EventArgs e)
    {
        // Complete the service event before closing the view that is also listening to it.
        _dispatcher.TryEnqueue(UpdateAuthorizedWindow);
    }

    private void ShowAuthorizationWindow()
    {
        if (_authorizationWindow is null)
        {
            _authorizationWindow = new AuthorizationWindow();
            _authorizationWindow.Closed += WindowClosed;
        }
        MainWindow = _authorizationWindow;
        _authorizationWindow.Activate();
        _authorizationWindow.ApplyFixedGeometry();
        Log.Information("独立验证窗口已显示，等待设备授权");
    }

    private void UpdateAuthorizedWindow()
    {
        if (_shuttingDown || _switchingWindows) return;
        _switchingWindows = true;
        try
        {
            var authorization = Services.GetRequiredService<LauncherAuthorizationService>();
            if (authorization.IsAuthorized)
            {
                if (_authorizationWindow is null && _shellWindow is not null) return;
                var first = _shellWindow is null;
                if (first)
                {
                    _shellWindow = new MainWindow();
                }
                MainWindow = _shellWindow;
                _shellWindow!.ResumeAfterAuthorization();
                _shellWindow.Activate();
                if (first)
                {
                    _shellWindow.ApplySavedGeometry();
                    _shellWindow.Closed += WindowClosed;
                }
                _authorizationTimer.Start();
                var previous = _authorizationWindow;
                _authorizationWindow = null;
                previous?.Close();
                Log.Information("授权通过：主窗口已显示，独立验证窗口已关闭");
                if (first) StartAuthorizedServices();
            }
            else
            {
                _authorizationTimer.Stop();
                if (_authorizationWindow is not null) return;
                _shellWindow?.SuspendForAuthorization();
                ShowAuthorizationWindow();
                _shellWindow?.AppWindow.Hide();
            }
        }
        finally { _switchingWindows = false; }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        if (_switchingWindows || _shuttingDown) return;
        _shuttingDown = true;
        Log.Information("97Launch 正在退出");
        _authorizationTimer.Stop();
        var authorization = Services.GetRequiredService<LauncherAuthorizationService>();
        authorization.Changed -= AuthorizationChanged;
        authorization.CancelPending();
        if (!ReferenceEquals(sender, _shellWindow)) _shellWindow?.Close();
        if (!ReferenceEquals(sender, _authorizationWindow)) _authorizationWindow?.Close();
        MainWindow = null;
        Exit();
    }

    internal static void StartAuthorizedServices()
    {
        // Seed the in-app log so the pane shows the launch sequence instead of
        // starting blank; it doubles as confirmation that the sink is wired up.
        var activity = Services.GetRequiredService<Services.LogBuffer>();
        activity.Append($"{AppInfo.Name} {AppInfo.Version} 已启动");
        activity.Append($"数据目录：{AppInfo.DataDirectory}");

        Log.Debug("Device authorization accepted; main shell initialized");
        _ = PreloadNetworkMetadataAsync();
    }

    private static async Task PreloadNetworkMetadataAsync()
    {
        try
        {
            await Services.GetRequiredService<MinecraftInstallService>().PreloadNetworkMetadataAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动时预取版本和加载器信息失败；打开下载页时仍可按需获取");
        }
    }

    /// <summary>
    /// Sets up Serilog to write a rolling file under the user's local app data.
    /// </summary>
    /// <remarks>
    /// The file sink is the point of this: XAML failures surface as native
    /// stowed exceptions that print nothing to stdout, so without a file there
    /// is no record of what went wrong. Debug output is also wired up for
    /// debugger-attached sessions.
    /// </remarks>
    private static void ConfigureLogging()
    {
        AppInfo.EnsureDirectories();

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                new SafeLogFormatter(),
                AppInfo.LogFilePath,
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: false)
            .WriteTo.Sink(LogBuffer.Shared, LogEventLevel.Information);

#if DEBUG
        config = config.WriteTo.Debug(new SafeLogFormatter());
#endif

        Log.Logger = config.CreateLogger();
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled XAML exception: {Type}: {Message}",
            e.Exception?.GetType().FullName, e.Exception?.Message);

        // WinRT projects some failures with the managed stack already stripped,
        // so walk the inner chain and dump every frame we can still reach.
        var current = e.Exception;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            Log.Error("Inner[{Depth}] {Type}: {Message}",
                depth, current.GetType().FullName, current.Message);
            Log.Error("Trace: {StackTrace}", current.StackTrace ?? "(none)");
            current = current.InnerException;
            depth++;
        }

        // Keep the process alive; a single failed interaction should not take
        // the whole launcher down.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled domain exception");
        Log.CloseAndFlush();
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
