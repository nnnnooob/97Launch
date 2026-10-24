using System.IO;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Launcher.App.Views;

public sealed partial class LaunchPage : Page
{
    private FileSystemWatcher? _instanceFolderWatcher;
    private bool _isLaunchPageVisible;

    public LaunchViewModel ViewModel { get; }

    public LaunchPage()
    {
        ViewModel = App.Services.GetRequiredService<LaunchViewModel>();
        InitializeComponent();
        ServerPanel.LaunchRequested = StartSelectedInstanceAsync;

        var instances = App.Services.GetRequiredService<InstancesViewModel>();
        instances.Folders.CollectionChanged += (_, _) => RefreshInstanceCount(instances);
        RefreshInstanceCount(instances);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        BindDefaultInstance();

        UpdateReadinessDot();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isLaunchPageVisible = true;
        SkinModel.SetPageActive(true);
        WatchSelectedInstanceFolder();
        ViewModel.RefreshInstanceStatus();
        RefreshInstanceCount(App.Services.GetRequiredService<InstancesViewModel>());
        UpdateReadinessDot();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isLaunchPageVisible = false;
        StopWatchingSelectedInstanceFolder();
        SkinModel.SetPageActive(false);
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchViewModel.IsInstanceReady))
        {
            UpdateReadinessDot();
        }

        if (_isLaunchPageVisible && e.PropertyName == nameof(LaunchViewModel.SelectedInstancePath))
        {
            WatchSelectedInstanceFolder();
        }
    }

    private void WatchSelectedInstanceFolder()
    {
        StopWatchingSelectedInstanceFolder();

        var instancePath = ViewModel.SelectedInstancePath;
        if (string.IsNullOrWhiteSpace(instancePath))
        {
            return;
        }

        if (!Directory.Exists(instancePath))
        {
            ClearMissingSelectedInstance(instancePath);
            return;
        }

        var parentPath = System.IO.Path.GetDirectoryName(instancePath);
        if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(parentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
            };
            watcher.Deleted += (_, args) =>
            {
                if (GameFolderService.PathsEqual(args.FullPath, instancePath))
                {
                    ClearMissingSelectedInstance(instancePath);
                }
            };
            watcher.Renamed += (_, args) =>
            {
                if (GameFolderService.PathsEqual(args.OldFullPath, instancePath))
                {
                    ClearMissingSelectedInstance(instancePath);
                }
            };
            watcher.Error += (_, _) => RecoverFromInstanceWatcherError(instancePath);

            _instanceFolderWatcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Append($"无法监视实例文件夹变化：{ex.Message}", LogEntryKind.Warning);
        }
    }

    private void ClearMissingSelectedInstance(string instancePath)
    {
        if (Directory.Exists(instancePath))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!GameFolderService.PathsEqual(ViewModel.SelectedInstancePath ?? string.Empty, instancePath)
                || Directory.Exists(instancePath))
            {
                return;
            }

            var instances = App.Services.GetRequiredService<InstancesViewModel>();
            var instanceId = ViewModel.SelectedInstanceId;
            if (instanceId is not null)
            {
                instances.ForgetDeletedInstance(instanceId);
            }
            else
            {
                ViewModel.ClearInstance();
            }

            _ = instances.ReloadFoldersAsync();
        });
    }

    private void RecoverFromInstanceWatcherError(string instancePath)
    {
        if (!Directory.Exists(instancePath))
        {
            ClearMissingSelectedInstance(instancePath);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isLaunchPageVisible
                && GameFolderService.PathsEqual(ViewModel.SelectedInstancePath ?? string.Empty, instancePath)
                && Directory.Exists(instancePath))
            {
                WatchSelectedInstanceFolder();
            }
        });
    }

    private void StopWatchingSelectedInstanceFolder()
    {
        _instanceFolderWatcher?.Dispose();
        _instanceFolderWatcher = null;
    }

    private void RefreshInstanceCount(InstancesViewModel instances)
        => ViewModel.SetInstalledInstanceCount(instances.Folders.Sum(folder => folder.InstanceCount));

    /// <summary>
    /// Points the hero card at a real instance on first entry.
    /// </summary>
    /// <remarks>
    /// The view model starts with a placeholder name, which would describe an
    /// instance that does not exist and would make the gear's "open this
    /// instance's settings" request unmatchable. Binding the first listed instance
    /// keeps the card and the instance list describing the same thing. Only applies
    /// while the placeholder is still in place, so an instance chosen on the
    /// instances page is never overwritten on return.
    ///
    /// A pending selection from an install wins over the placeholder: if the user
    /// just installed a version and asked to have it selected, the hero card must
    /// show that version rather than whatever happens to be first in the list.
    /// </remarks>
    private async void BindDefaultInstance()
    {
        var instances = App.Services.GetRequiredService<InstancesViewModel>();

        var requested = App.Services.GetRequiredService<PendingInstanceSelection>().Take();
        if (requested is not null)
        {
            var target = instances.ModdableInstances
                .Concat(instances.RegularInstances)
                .Concat(instances.RareInstances)
                .FirstOrDefault(item => item.Id == requested);

            if (target is null)
            {
                await instances.ReloadFoldersAsync();
                target = instances.FindInstance(requested);
            }

            if (target is not null)
            {
                instances.SelectedInstance = target;
                Log.Append($"已切换到新安装的实例：{target.Name}");
                return;
            }

            Log.Append($"新安装的实例 ID「{requested}」未出现在列表中", LogEntryKind.Warning);
        }

        // The initial scan restores the saved instance. Do not replace it with a
        // placeholder or first item while the collections are still being built.
        if (instances.IsLoading) return;

        if (!ViewModel.HasPlaceholderInstance
            && (ViewModel.SelectedInstancePath is null
                || instances.Folders.All(folder => !GameFolderService.PathsEqual(System.IO.Path.Combine(folder.Path, "versions"), System.IO.Path.GetDirectoryName(ViewModel.SelectedInstancePath) ?? string.Empty))))
        {
            return;
        }

        var instancesInFolder = instances.ModdableInstances
            .Concat(instances.RegularInstances)
            .Concat(instances.RareInstances)
            .ToArray();

        var selected = instancesInFolder.FirstOrDefault(item => item.Id == ViewModel.SelectedInstanceId)
            ?? instancesInFolder.FirstOrDefault();

        if (selected is not null)
        {
            instances.SelectedInstance = selected;
        }
        else
        {
            ViewModel.ClearInstance();
        }
    }

    private IDialogService Dialogs => App.Services.GetRequiredService<IDialogService>();

    // Output goes to the page's log pane, which is shared through the container
    // so it survives navigation between pages.
    private static LogBuffer Log => App.Services.GetRequiredService<LogBuffer>();

    /// <summary>
    /// Colours the readiness dot white when the instance can start and red when
    /// it cannot.
    /// </summary>
    /// <remarks>
    /// Set in code rather than bound: WinUI 3 has no DataTrigger, and a
    /// Brush-returning property on the view model would drag XAML media types
    /// into it. Two states is not worth a converter class.
    /// </remarks>
    private void UpdateReadinessDot()
    {
        ReadinessDot.Background = new SolidColorBrush(
            ViewModel.IsInstanceReady
                ? Color.FromArgb(255, 0xFF, 0xFF, 0xFF)  // white reads on the green gradient
                : Color.FromArgb(255, 0xFF, 0x6B, 0x6B)); // error red
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        => await StartSelectedInstanceAsync(null, null);

    private async Task StartSelectedInstanceAsync(CatalogServer? server, string? expectedInstanceId)
    {
        if (!ViewModel.CanLaunch) return;
        if (expectedInstanceId is not null && expectedInstanceId != ViewModel.SelectedInstanceId)
        {
            App.Services.GetRequiredService<ToastService>().ShowError("未启动实例", "主页选中的实例已变化，请重新点击启动。");
            return;
        }

        if (ViewModel.RequiresJavaInstallation)
        {
            var switchMode = ViewModel.JavaInstallRequiresAutomaticMode;
            var message = ViewModel.JavaHint + (switchMode
                ? "\n\n是否切换为自动选择兼容 Java，缺少时再下载安装？"
                : "\n\n是否下载并安装兼容的 Java？");
            if (!await Dialogs.ConfirmAsync(ViewModel.HasJavaVersionMismatch ? "Java 版本不匹配" : "需要安装 Java",
                message, switchMode ? "自动选择并启动" : "下载并启动", "取消", XamlRoot))
                return;

            if (switchMode) ViewModel.UseAutomaticJavaForCurrentInstance();
        }

        Log.Append($"准备启动实例：{ViewModel.InstanceName}（{ViewModel.GameVersion}）");
        Log.Append($"加载器：{ViewModel.LoaderDisplay} · 账号：{ViewModel.LoginMode}");
        try
        {
            if (expectedInstanceId is not null && expectedInstanceId != ViewModel.SelectedInstanceId)
                throw new InvalidOperationException("主页选中的实例已变化，请重新点击启动。");
            var result = await ViewModel.StartLaunchAsync(server);
            Log.Append($"游戏进程已启动：PID {result.ProcessId} · Java {result.JavaVersion}");
        }
        catch (OperationCanceledException)
        {
            Log.Append("启动任务已取消。");
        }
        catch (Exception ex)
        {
            Log.Append($"启动失败：{ex.Message}", LogEntryKind.Error);
            App.Services.GetRequiredService<ToastService>().ShowError("启动失败", ex.Message);
        }
    }

    private void StopGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id }) ViewModel.StopGame(id);
    }

    private void GameAction_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.Background = new SolidColorBrush(Color.FromArgb(20, 196, 43, 28));
        button.Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
    }

    private void GameAction_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
    }

    private void StopAllGames_Click(object sender, RoutedEventArgs e)
        => ViewModel.StopAllGames();

    private void InstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        // The gear opens the instance page's own settings dialog rather than a
        // second copy of it here: the instance list, and therefore the instance
        // the dialog acts on, belongs to the instances page. Record which instance
        // to open, then navigate; the instances page picks the request up as it
        // loads.
        if (ViewModel.SelectedInstanceId is null)
        {
            Log.Append("当前没有可设置的实例。", LogEntryKind.Warning);
            return;
        }

        App.Services.GetRequiredService<PendingInstanceSettings>()
            .Request(ViewModel.SelectedInstanceId);

        App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Instances);
    }
}
