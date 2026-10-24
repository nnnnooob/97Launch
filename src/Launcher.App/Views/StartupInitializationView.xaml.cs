using System.Diagnostics;
using Launcher.App.Controls;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed partial class StartupInitializationView : UserControl
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler? RetryRequested;

    public StartupInitializationView()
    {
        InitializeComponent();
        Loaded += (_, _) => _loaded.TrySetResult();
    }

    internal async Task RunAsync(CancellationToken token)
    {
        await _loaded.Task.WaitAsync(token);
        var steps = new[] { DataStep, InstanceStep, JavaStep, SkinStep };
        var labels = new[] { "检查本地数据", "扫描游戏实例", "检查 Java 环境", "准备皮肤预览" };
        for (var i = 0; i < steps.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            steps[i].Text = "●  " + labels[i];
            PreparationDetail.Text = labels[i] + "…";
            PreparationProgress.IsIndeterminate = true;
            string detail;
            switch (i)
            {
                case 0:
                    await Task.Run(AppInfo.EnsureDirectories, token);
                    _ = App.Services.GetRequiredService<SettingsViewModel>();
                    detail = "本地数据已就绪";
                    break;
                case 1:
                    var instances = App.Services.GetRequiredService<InstancesViewModel>();
                    await (instances.InitialLoad.IsFaulted || instances.InitialLoad.IsCanceled
                        ? instances.ReloadFoldersAsync() : instances.InitialLoad).WaitAsync(token);
                    detail = $"已检查 {instances.Folders.Count} 个游戏文件夹，发现 {instances.Folders.Sum(f => f.InstanceCount)} 个实例";
                    break;
                case 2:
                    var settings = App.Services.GetRequiredService<SettingsViewModel>();
                    await settings.RefreshJavaAsync(token);
                    detail = settings.JavaRuntimes.Count > 0 ? $"发现 {settings.JavaRuntimes.Count} 个 Java 环境" : "未发现 Java，启动游戏时可按需下载";
                    break;
                default:
                    var skin = new SkinModelView();
                    PreviewHost.Content = skin;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    bool ready;
                    try { ready = await skin.PrepareAsync(token); }
                    catch (TimeoutException) { ready = false; }
                    detail = ready ? "皮肤预览已就绪" : "已准备兼容预览，可继续使用启动器";
                    break;
            }
            token.ThrowIfCancellationRequested();
            steps[i].Text = "✓  " + labels[i];
            PreparationDetail.Text = detail;
            PreparationProgress.IsIndeterminate = false;
            PreparationProgress.Value = i + 1;
            Serilog.Log.Information("首次初始化：{Stage}，耗时 {Elapsed} ms，{Detail}", labels[i], watch.ElapsedMilliseconds, detail);
        }
        PreparationDetail.Text = "准备完成，正在打开主窗口…";
    }

    internal void ShowError(string message)
    {
        PreparationProgress.IsIndeterminate = false;
        PreparationDetail.Text = "可以重新准备，或关闭窗口稍后再试。";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        RetryButton.Visibility = Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
