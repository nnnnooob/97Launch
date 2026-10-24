using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed partial class ServerCatalogPanel : UserControl
{
    private readonly ServerCatalogService _catalog = App.Services.GetRequiredService<ServerCatalogService>();
    private readonly LauncherAuthorizationService _authorization = App.Services.GetRequiredService<LauncherAuthorizationService>();
    private readonly LaunchViewModel _launch = App.Services.GetRequiredService<LaunchViewModel>();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _lifetime;
    private bool _refreshing;
    private string? _deviceId;
    private string? _directoryState;

    public ObservableCollection<ServerCardViewModel> Servers { get; } = [];
    public Func<CatalogServer, string, Task>? LaunchRequested { get; set; }

    public ServerCatalogPanel()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += Panel_Loaded;
        Unloaded += Panel_Unloaded;
    }

    private async void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _launch.PropertyChanged -= LaunchChanged;
        _launch.PropertyChanged += LaunchChanged;
        _authorization.Changed -= AuthorizationChanged;
        _authorization.Changed += AuthorizationChanged;
        UpdateLaunchState();
        _timer.Start();
        await RefreshAsync();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _lifetime?.Cancel();
        _launch.PropertyChanged -= LaunchChanged;
        _authorization.Changed -= AuthorizationChanged;
    }

    private void LaunchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchViewModel.CanLaunch) or nameof(LaunchViewModel.InstanceName)
            or nameof(LaunchViewModel.SelectedInstanceId) or nameof(LaunchViewModel.ReadinessDisplay))
            UpdateLaunchState();
    }

    private void AuthorizationChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_authorization.IsAuthorized)
        {
            _lifetime?.Cancel();
            Servers.Clear();
            return;
        }
        if (_deviceId != _authorization.Device?.Id)
        {
            _lifetime?.Cancel();
            Servers.Clear();
            _deviceId = _authorization.Device?.Id;
        }
        if (IsLoaded && (_lifetime is null || _lifetime.IsCancellationRequested))
        {
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
            await RefreshAsync();
        }
    });

    private void UpdateLaunchState()
    {
        TargetHint.Text = _launch.SelectedInstanceId is null ? "请先选择要启动的本地实例。" : $"启动目标：{_launch.InstanceName}";
        foreach (var server in Servers) server.CanLaunch = _launch.CanLaunch;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(manual: true);

    private async Task RefreshAsync(bool manual = false)
    {
        if (_refreshing || _lifetime is null || _lifetime.IsCancellationRequested || !_authorization.IsAuthorized) return;
        var token = _lifetime.Token;
        _refreshing = true;
        if (manual) Serilog.Log.Information("手动刷新服务器目录");
        RefreshIcon.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible; RefreshButton.IsEnabled = false;
        try
        {
            if (_deviceId != _authorization.Device?.Id) { Servers.Clear(); _deviceId = _authorization.Device?.Id; }
            var data = await _catalog.ListAsync(token);
            token.ThrowIfCancellationRequested();
            if (data.Items is null || data.Items.Length > 100) throw new InvalidDataException("服务器目录超出当前支持的范围。");
            var ids = new HashSet<string>();
            var rows = data.Items.OrderBy(s => s.Order).ThenBy(s => s.Name).ToArray();
            foreach (var row in rows)
            {
                if (!Guid.TryParse(row.Id, out _) || !ids.Add(row.Id)) throw new InvalidDataException("服务器目录包含无效条目。");
                ServerCatalogService.ValidateAddress(row);
            }
            var directoryState = string.Join("|", rows.Select(row => row.Id + ":" + row.State)) + ":" + data.Stale;
            if (manual || _directoryState != directoryState)
                Serilog.Log.Information("服务器目录已更新：{Count} 个服务器，{Ready} 个可加入，缓存过期={Stale}", rows.Length, rows.Count(row => row.CanJoin), data.Stale);
            _directoryState = directoryState;
            foreach (var old in Servers.Where(s => !ids.Contains(s.Id)).ToArray()) Servers.Remove(old);
            for (var i = 0; i < rows.Length; i++)
            {
                var value = data.Stale ? rows[i] with { CanJoin = false, State = "unknown" } : rows[i];
                var card = Servers.FirstOrDefault(s => s.Id == value.Id);
                if (card is null) { card = new(value); Servers.Insert(Math.Min(i, Servers.Count), card); }
                else
                {
                    card.Update(value);
                    var index = Servers.IndexOf(card);
                    if (index != i) Servers.Move(index, i);
                }
            }
            ServerCount.Text = $"{Servers.Count} 个";
            EmptyTitle.Text = "暂无已开放的服务器";
            EmptyDetail.Text = "管理员发布服务器后会显示在这里。";
            EmptyState.Visibility = Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusBar.IsOpen = data.Stale;
            StatusBar.Message = data.Stale ? "服务器状态暂时无法更新，请稍后刷新。" : "";
            UpdateLaunchState();
            using var limiter = new SemaphoreSlim(4);
            await Task.WhenAll(Servers.Select(async card =>
            {
                await limiter.WaitAsync(token);
                try
                {
                    var icon = _catalog.IconAsync(card.Server, token);
                    var latency = ServerCatalogService.LatencyAsync(card.Server, token);
                    card.IconPath = await icon;
                    var ms = await latency;
                    card.LatencyMs = ms;
                }
                finally { limiter.Release(); }
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            foreach (var card in Servers) card.Update(card.Server with { State = "unknown", CanJoin = false });
            StatusBar.Message = ex is HttpRequestException or OperationCanceledException
                ? "暂时无法获取服务器列表，请检查网络后刷新。" : ex.Message;
            StatusBar.IsOpen = true;
            EmptyTitle.Text = "暂时无法获取服务器";
            EmptyDetail.Text = "点击右上角刷新，重新连接。";
            EmptyState.Visibility = Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Serilog.Log.Warning("服务器目录刷新未完成：{ErrorType}", ex.GetType().Name);
        }
        finally
        {
            _refreshing = false;
            RefreshIcon.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; RefreshButton.IsEnabled = true;
        }
    }

    private void ServerCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ServerCard card) return;
        card.InstallRequested -= InstallRequested;
        card.InstallRequested += InstallRequested;
        card.JoinRequested -= JoinRequested;
        card.JoinRequested += JoinRequested;
    }

    private async void JoinRequested(ServerCardViewModel card)
    {
        if (!card.CanJoin || LaunchRequested is null || _launch.SelectedInstanceId is not { } selected) return;
        card.IsWorking = true; card.IsJoining = true;
        try
        {
            var fresh = await _catalog.GetAsync(card.Id, _lifetime?.Token ?? CancellationToken.None);
            card.Update(fresh);
            if (!fresh.CanJoin) throw new InvalidOperationException("服务器尚未就绪或正在维护，请稍后再试。");
            if (_launch.SelectedInstanceId != selected) throw new InvalidOperationException("主页选中的实例已变化，请重新点击启动。");
            var instance = App.Services.GetRequiredService<InstancesViewModel>().FindInstance(selected);
            if (instance is null) throw new InvalidOperationException("当前实例已不存在，请重新选择。");
            Serilog.Log.Information("请求启动并加入服务器：{Server}（{Address}），当前实例：{Instance}", fresh.Name, MinecraftServerList.Address(fresh), instance.Name);
            var versionMismatch = Version.TryParse(fresh.Minecraft, out var expected) && Version.TryParse(instance.MinecraftVersionId, out var actual) && expected != actual;
            var requiredLoader = Loader(fresh.Loader);
            var loaderMismatch = requiredLoader.Length > 0 && requiredLoader != Loader(instance.Loader);
            if (versionMismatch || loaderMismatch)
            {
                var confirm = await App.Services.GetRequiredService<IDialogService>().ConfirmAsync("实例可能不兼容",
                    $"将使用主页选中的「{instance.Name}」（{instance.MinecraftVersionId} / {instance.Loader}）。\n服务器建议：{fresh.Minecraft} / {fresh.Loader}。\n仍要启动此实例并尝试进入服务器吗？",
                    "继续启动", "取消", XamlRoot);
                if (!confirm) { Serilog.Log.Information("已取消加入服务器：{Server}，实例版本或加载器可能不兼容", fresh.Name); return; }
            }
            await LaunchRequested(fresh, selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法进入服务器", ex.Message); }
        finally { card.IsWorking = false; card.IsJoining = false; }
    }

    private static string Loader(string value)
    {
        var text = (value ?? "").ToLowerInvariant();
        return new[] { "neoforge", "fabric", "quilt", "forge" }.FirstOrDefault(text.Contains) ?? "";
    }

    private async void InstallRequested(ServerCardViewModel card)
    {
        if (!card.CanInstall) return;
        card.IsWorking = true;
        try
        {
            Serilog.Log.Information("获取服务器关联实例：{Server}", card.Name);
            var pack = await _catalog.GetPackAsync(card.Id, _lifetime?.Token ?? CancellationToken.None);
            var instances = App.Services.GetRequiredService<InstancesViewModel>();
            if (instances.IsLoading) await instances.ReloadFoldersAsync();
            if (instances.Folders.Count == 0) throw new InvalidOperationException("请先在实例页面添加游戏文件夹。");
            var folders = new ComboBox { ItemsSource = instances.Folders.ToArray(), DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
            folders.SelectedItem = instances.SelectedFolder ?? instances.Folders[0];
            var name = new TextBox { MaxLength = 100, PlaceholderText = "实例名称" };
            var info = new TextBlock { Text = $"{pack.Name} / {pack.Version}\n{pack.Minecraft} · {pack.Loader}\n{pack.Size / 1048576d:0.0} MiB", TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12, MinWidth = 280, MaxWidth = 440 };
            content.Children.Add(info);
            content.Children.Add(new TextBlock { Text = "安装到" }); content.Children.Add(folders);
            content.Children.Add(new TextBlock { Text = "新实例名称" }); content.Children.Add(name);
            content.Children.Add(new TextBlock { Text = "安装成功后自动选中新实例并返回启动页。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            string Root() => (folders.SelectedItem as GameFolder)?.Path ?? throw new InvalidOperationException("请选择游戏文件夹。");
            name.Text = AvailableName(pack.Name + "-" + pack.Version, Root());
            var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            content.Children.Add(error);
            var dialog = new ContentDialog { Title = "获取相关实例", Content = content, PrimaryButtonText = "安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    ModrinthModpackInstaller.ValidateName(name.Text.Trim());
                    var target = Path.Combine(Root(), "versions", name.Text.Trim());
                    if (File.Exists(target) || Directory.Exists(target) && !File.Exists(Path.Combine(target, ".97launch-installing")))
                        throw new IOException("同名实例已存在，请更换名称。");
                    if (HasPendingTarget(target)) throw new IOException("该实例已有安装任务，请在传输页面查看。");
                }
                catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            var result = await App.Services.GetRequiredService<IDialogService>().ShowAsync(dialog, _lifetime?.Token ?? CancellationToken.None);
            if (result != ContentDialogResult.Primary) { Serilog.Log.Information("已取消安装服务器关联实例：{Server}", card.Name); return; }
            var root = Root(); var instanceName = name.Text.Trim();
            App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("安装相关实例 · " + pack.Name,
                instanceName, root, ["下载客户端整合包", "读取整合包清单", "安装游戏与资源", "整理实例文件"],
                (progress, token) => _catalog.InstallAsync(card.Id, pack, root, instanceName, progress, token),
                refreshInstances: true, returnToLaunchOnCompletion: true);
            App.Services.GetRequiredService<ToastService>().Show("已加入安装队列", $"{pack.Name} → {instanceName}");
            App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Transfer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法获取相关实例", ex.Message); }
        finally { card.IsWorking = false; }
    }

    private static bool HasPendingTarget(string path) => App.Services.GetRequiredService<TransferViewModel>().Jobs.Any(j =>
        (j.State is TransferJobState.Active or TransferJobState.Queued) && GameFolderService.PathsEqual(Path.Combine(j.GameDirectory, "versions", j.InstanceName), path));

    private static string AvailableName(string requested, string root)
    {
        var clean = string.Concat(requested.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (clean.Length > 70) clean = clean[..70];
        if (string.IsNullOrWhiteSpace(clean)) clean = "服务器整合包";
        if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) clean = "_" + clean;
        var name = clean;
        for (var i = 2; Directory.Exists(Path.Combine(root, "versions", name)) || File.Exists(Path.Combine(root, "versions", name)) || HasPendingTarget(Path.Combine(root, "versions", name)); i++) name = $"{clean} ({i})";
        return name;
    }
}
