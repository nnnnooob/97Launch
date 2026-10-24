using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

/// <summary>
/// Download page: a category rail on the left, and the matching list on the
/// right.
/// </summary>
public sealed partial class DownloadPage : Page
{
    private static readonly LogBuffer Log = App.Services.GetRequiredService<LogBuffer>();

    private static readonly IDialogService Dialogs = App.Services.GetRequiredService<IDialogService>();

    public DownloadViewModel ViewModel { get; } = App.Services.GetRequiredService<DownloadViewModel>();

    public DownloadPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _installedScan?.Cancel();
            App.Services.GetRequiredService<InstanceContentService>().Changed -= InstalledContent_Changed;
            _resourceDialog?.Cancel();
            foreach (var previous in _resourceHistory) previous.Cancel();
            _resourceHistory.Clear();
            if (_resourceDialog is not null && InstallDialogHost.Children.Contains(_resourceDialog))
            {
                InstallDialogHost.Children.Clear();
                InstallDialogHost.Visibility = Visibility.Collapsed;
            }
        };
        ViewModel.Resources.CollectionChanged += (_, _) => UpdateResultsState();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DownloadViewModel.ResourceStatus) or nameof(DownloadViewModel.IsResourceLoading))
                UpdateResultsState();
            if (args.PropertyName == nameof(DownloadViewModel.IsResourceLoading) && !ViewModel.IsResourceLoading && IsLoaded)
                _ = RefreshInstalledResourcesAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<InstanceContentService>().Changed += InstalledContent_Changed;
        var browse = App.Services.GetRequiredService<PendingResourceBrowse>().Take();
        if (browse is not null)
        {
            _requestedResourceTargetId = browse.InstanceId;
            SectionList.SelectedItem = SectionList.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == browse.Section);
        }
        UpdateCounts();
        UpdateResultsState();

        await LoadVersionsAsync();
        if (ViewModel.IsResourceSection)
        {
            await RefreshResourceTargetsAsync();
            if (browse is not null) ViewModel.SearchText = browse.Search;
            await ViewModel.SearchResourcesAsync();
        }
    }

    private string? _requestedResourceTargetId;

    /// <summary>
    /// Fetches the version manifest and refreshes everything derived from it.
    /// </summary>
    /// <remarks>
    /// Run from Loaded rather than the constructor: the fetch is asynchronous, and
    /// the page must appear immediately with its group headers visible while the
    /// list fills in behind them.
    /// </remarks>
    private async Task LoadVersionsAsync(bool forceRefresh = false)
    {
        VersionLoadingBar.Visibility = Visibility.Visible;
        VersionErrorPanel.Visibility = Visibility.Collapsed;

        await ViewModel.LoadVersionsAsync(forceRefresh);

        VersionLoadingBar.Visibility = Visibility.Collapsed;
        UpdateCounts();

        if (ViewModel.VersionLoadError is { Length: > 0 } error)
        {
            VersionErrorText.Text = error;
            VersionErrorPanel.Visibility = Visibility.Visible;
        }
    }

    private async void RetryVersions_Click(object sender, RoutedEventArgs e)
        => await LoadVersionsAsync(forceRefresh: true);

    private void UpdateCounts()
    {
        LatestCount.Text = $"{ViewModel.LatestVersions.Count} 个";
        ReleaseCount.Text = $"{ViewModel.ReleaseVersions.Count} 个";
        PreviewCount.Text = $"{ViewModel.PreviewVersions.Count} 个";
        OldCount.Text = $"{ViewModel.OldVersions.Count} 个";
        FoolsCount.Text = $"{ViewModel.FoolsVersions.Count} 个";
    }

    /// <summary>
    /// Opens the install flow for the version behind the clicked card.
    /// </summary>
    private void VersionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: McVersionEntry entry })
        {
            return;
        }

        _installDialog ??= new DownloadInstallDialog();
        _installDialog.SetHost(CloseInstallDialog);
        var instances = App.Services.GetRequiredService<InstancesViewModel>();
        var gameDirectory = instances.FolderSelectionPath;
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            gameDirectory = App.Services.GetRequiredService<GameFolderService>().Discover().FirstOrDefault()?.Path;
        }
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            Log.Append("没有可用的 Minecraft 文件夹，无法创建安装任务");
            return;
        }

        _installDialog.Load(entry.Version, gameDirectory, entry.IconPath);
        InstallDialogHost.Children.Clear();
        InstallDialogHost.Children.Add(_installDialog);
        InstallDialogHost.Visibility = Visibility.Visible;
    }

    private DownloadInstallDialog? _installDialog;

    private void CloseInstallDialog()
    {
        InstallDialogHost.Children.Clear();
        InstallDialogHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Applies the "select the newly installed instance" preference after an
    /// install finishes.
    /// </summary>
    /// <remarks>
    /// The preference lives on the settings page, but the install that has to
    /// honour it happens here. Reading it at the moment of completion rather than
    /// caching it at page load means toggling the switch in settings takes effect
    /// on the very next install without a restart.
    ///
    /// Selecting is routed through <see cref="PendingInstanceSelection"/> instead
    /// of touching another page's view model: the instances page and the launch
    /// page each pick the request up as they load, which is the same
    /// one-shot-request pattern the launch page already uses for instance
    /// settings.
    /// </remarks>
    private void ApplyInstallSelection(string instanceId)
    {
        var store = App.Services.GetRequiredService<SettingsStore>();
        if (!store.Load().SelectNewInstance)
        {
            Log.Append($"已安装实例 {instanceId}，按设置未自动切换");
            return;
        }

        App.Services.GetRequiredService<PendingInstanceSelection>().Request(instanceId);
        Log.Append($"已安装并请求选择实例：{instanceId}");
    }

    // ======== resources ========

    private ResourceDownloadDialog? _resourceDialog;
    private readonly Stack<ResourceDownloadDialog> _resourceHistory = new();
    private bool _updatingTargets;
    private readonly SemaphoreSlim _targetGate = new(1, 1);
    private readonly InstancesViewModel _instances = App.Services.GetRequiredService<InstancesViewModel>();
    private CancellationTokenSource? _installedScan;

    private void InstalledContent_Changed(string path)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (InstallTargetCombo.SelectedItem is DownloadTarget { Instance: { } instance }
                && GameFolderService.PathsEqual(GetGameDirectory(instance), path)) _ = RefreshInstalledResourcesAsync();
        });
    }

    private async Task RefreshInstalledResourcesAsync()
    {
        _installedScan?.Cancel(); _installedScan?.Dispose(); _installedScan = new();
        var token = _installedScan.Token;
        foreach (var project in ViewModel.Resources) project.InstalledState = "";
        if (!IsLoaded || ViewModel.Section != DownloadSection.Mod || InstallTargetCombo.SelectedItem is not DownloadTarget { Instance: { } instance }) return;
        try
        {
            var content = App.Services.GetRequiredService<InstanceContentService>();
            var context = content.Context(instance);
            var files = await content.ScanAsync(context, Launcher.Core.Instances.ContentKind.Mods, token);
            var online = App.Services.GetRequiredService<LocalModOnlineService>();
            using var gate = new SemaphoreSlim(4);
            var identified = new List<(Launcher.Core.Instances.LocalContent File, LocalModOnlineInfo Online)>();
            await Task.WhenAll(files.Select(async file =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var info = await online.LookupAsync(context, file, token);
                    if (info is not null) identified.Add((file, info));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidDataException) { }
                finally { gate.Release(); }
            }));
            token.ThrowIfCancellationRequested();
            foreach (var project in ViewModel.Resources)
            {
                var installed = identified.Where(f => f.Online.Project.Source == project.Source && f.Online.Project.Id == project.ProjectId).ToArray();
                if (installed.Length > 0) project.InstalledState = (installed.Any(f => f.Online.Update is not null && !f.Online.IsStale) ? "可更新" : "已安装")
                    + (installed.Any(f => f.Online.IsStale) ? " · 更新信息未能刷新" : "")
                    + (installed.All(f => !f.File.Enabled) ? " · 已禁用" : "") + (installed.Length > 1 ? " · 存在重复文件" : "");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Append("读取 Mod 安装状态失败：" + ex.Message, LogEntryKind.Error); }
    }

    private sealed record DownloadTarget(string Label, InstanceItem? Instance, GameFolder? Folder);
    private sealed record WorldTarget(string Name, string Path);

    private async void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListViewItem { Tag: string tag }) return;
        var section = tag switch
        {
            "mod" => DownloadSection.Mod, "modpack" => DownloadSection.Modpack,
            "shader" => DownloadSection.Shader, "resourcepack" => DownloadSection.ResourcePack,
            "datapack" => DownloadSection.DataPack, _ => DownloadSection.Minecraft,
        };
        if (ViewModel.Section == section) return;
        ViewModel.Section = section;
        ViewModel.ResetResourceFilters();
        if (IsLoaded && ViewModel.IsResourceSection) await RefreshResourceTargetsAsync();
        UpdateResultsState();
    }

    private async Task RefreshResourceTargetsAsync()
    {
        await _targetGate.WaitAsync();
        try
        {
            if (_instances.Folders.Count == 0) await _instances.ReloadFoldersAsync();
            else await _instances.LoadInstancesAsync();
            if (!ViewModel.IsResourceSection) return;
            var previous = InstallTargetCombo.SelectedItem as DownloadTarget;
            _updatingTargets = true;
            var pack = ViewModel.Section == DownloadSection.Modpack;
            InstallTargetLabel.Text = pack ? "游戏文件夹" : "安装到";
            var targets = pack
                ? _instances.Folders.Select(folder => new DownloadTarget(folder.Name, null, folder)).ToArray()
                : _instances.AvailableInstances.Where(instance => !instance.HasError && Directory.Exists(instance.Path))
                    .Select(instance => new DownloadTarget($"{instance.Name} · {instance.GameVersion} {instance.Loader}", instance, null)).ToArray();
            InstallTargetCombo.ItemsSource = targets;
            InstallTargetCombo.SelectedItem = targets.FirstOrDefault(target => _requestedResourceTargetId is not null && target.Instance?.Id == _requestedResourceTargetId)
                ?? targets.FirstOrDefault(target => pack
                    ? target.Folder?.Path == previous?.Folder?.Path || target.Folder?.Path == _instances.FolderSelectionPath
                    : target.Instance?.Id == previous?.Instance?.Id || target.Instance?.Id == _instances.SelectedInstance?.Id)
                ?? targets.FirstOrDefault();
            _requestedResourceTargetId = null;
            WorldTargetPanel.Visibility = ViewModel.Section == DownloadSection.DataPack ? Visibility.Visible : Visibility.Collapsed;
            var showLoader = ViewModel.Section is DownloadSection.Mod or DownloadSection.Modpack or DownloadSection.Shader;
            ResourceLoaderCombo.Visibility = showLoader ? Visibility.Visible : Visibility.Collapsed;
            ResourceLoaderColumn.Width = new GridLength(showLoader ? 1 : 0, GridUnitType.Star);
            var resolution = ViewModel.Section == DownloadSection.ResourcePack;
            ResolutionCombo.Visibility = resolution ? Visibility.Visible : Visibility.Collapsed;
            ResolutionColumn.Width = new GridLength(resolution ? 1 : 0, GridUnitType.Star);
            ResourceHeading.Text = "热门" + ViewModel.ResourceTypeName;
            _updatingTargets = false;
            await ApplyResourceTargetAsync();
        }
        catch (Exception ex)
        {
            _updatingTargets = false;
            Log.Append("读取资源安装目标失败：" + ex.Message, LogEntryKind.Error);
            App.Services.GetRequiredService<ToastService>().ShowError("读取安装目标失败", "请在实例页面刷新游戏文件夹后重试。");
        }
        finally { _targetGate.Release(); }
    }

    private async Task ApplyResourceTargetAsync()
    {
        _ = RefreshInstalledResourcesAsync();
        if (InstallTargetCombo.SelectedItem is not DownloadTarget { Instance: { } instance }) return;
        if (!string.IsNullOrWhiteSpace(instance.MinecraftVersionId))
        {
            if (!ViewModel.GameVersions.Contains(instance.MinecraftVersionId))
                ViewModel.GameVersions.Add(instance.MinecraftVersionId);
            ViewModel.SelectedGameVersion = instance.MinecraftVersionId;
        }
        if (ViewModel.Section == DownloadSection.Mod)
            ViewModel.SelectedLoader = ResolveLoader(instance.Loader) is { } loader
                ? ViewModel.Loaders.FirstOrDefault(value => value.Equals(loader, StringComparison.OrdinalIgnoreCase)) ?? "任意加载器"
                : "任意加载器";
        if (ViewModel.Section != DownloadSection.DataPack) return;
        var previous = (WorldTargetCombo.SelectedItem as WorldTarget)?.Path;
        var directory = GetGameDirectory(instance);
        var worlds = await Task.Run(() =>
        {
            var saves = System.IO.Path.Combine(directory, "saves");
            return Directory.Exists(saves)
                ? Directory.GetDirectories(saves).Where(world => File.Exists(System.IO.Path.Combine(world, "level.dat")))
                    .Select(world => new WorldTarget(System.IO.Path.GetFileName(world), world)).ToArray()
                : [];
        });
        if ((InstallTargetCombo.SelectedItem as DownloadTarget)?.Instance?.Id != instance.Id) return;
        WorldTargetCombo.ItemsSource = worlds;
        WorldTargetCombo.SelectedItem = worlds.FirstOrDefault(world => world.Path == previous) ?? worlds.FirstOrDefault();
    }

    private string GetGameDirectory(InstanceItem instance)
    {
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(instance.Path))!;
        var global = App.Services.GetRequiredService<SettingsStore>().Load();
        var local = App.Services.GetRequiredService<InstanceLaunchSettingsStore>().Load(instance.Path, global);
        return MinecraftLaunchService.ResolveGameDirectory(instance, root, local, global);
    }

    private async void InstallTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingTargets && IsLoaded) await ApplyResourceTargetAsync();
    }

    private void DownloadLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        => CategoryColumn.Width = new GridLength(e.NewSize.Width < 1000 ? 172 : 216);

    private void ResourceSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ViewModel.SearchText = sender.Text;
    }

    private async void ResourceSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => await ViewModel.SearchResourcesAsync();

    private void GameVersionFilter_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        ViewModel.SelectedGameVersion = args.Text.Trim();
        args.Handled = true;
    }

    private void ResetFilter_Click(object sender, RoutedEventArgs e) => ViewModel.ResetResourceFilters();
    private async void PreviousResources_Click(object sender, RoutedEventArgs e) => await ViewModel.ChangeResourcePageAsync(-1);
    private async void NextResources_Click(object sender, RoutedEventArgs e) => await ViewModel.ChangeResourcePageAsync(1);

    private void UpdateResultsState()
    {
        if (ResourceList is null) return;
        var empty = ViewModel.Resources.Count == 0;
        ResourceLoadingRing.Visibility = ViewModel.IsResourceLoading && empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintPanel.Visibility = empty && !ViewModel.IsResourceLoading ? Visibility.Visible : Visibility.Collapsed;
        ResourceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = string.IsNullOrWhiteSpace(ViewModel.ResourceStatus)
            ? "没有找到匹配的资源，试试其他关键词或放宽筛选条件。"
            : ViewModel.ResourceStatus;
        ResourceErrorBar.IsOpen = !empty && !string.IsNullOrWhiteSpace(ViewModel.ResourceStatus);
        ResourceErrorBar.Message = ViewModel.ResourceStatus;
    }

    private void ResourceIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image { Parent: Grid grid } image && grid.Children[0] is TextBlock fallback)
        {
            image.Visibility = Visibility.Collapsed;
            fallback.Visibility = Visibility.Visible;
        }
    }

    private async void ResourceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ResourceEntry resource) return;
        _resourceDialog?.Cancel();
        foreach (var previous in _resourceHistory) previous.Cancel();
        _resourceHistory.Clear();
        _resourceDialog = new ResourceDownloadDialog();
        var target = InstallTargetCombo.SelectedItem as DownloadTarget;
        var instance = target?.Instance;
        var section = ViewModel.Section;
        var game = instance?.MinecraftVersionId ?? "";
        var loader = section == DownloadSection.Mod ? ResolveLoader(instance?.Loader ?? "") ?? ""
            : section == DownloadSection.DataPack ? "datapack"
            : section == DownloadSection.Shader && ViewModel.SelectedLoader != "任意加载器" ? ViewModel.SelectedLoader.ToLowerInvariant() : "";
        var world = WorldTargetCombo.SelectedItem as WorldTarget;
        var canInstall = section == DownloadSection.Modpack ? target?.Folder is not null
            : instance is not null && Directory.Exists(instance.Path)
                && (section != DownloadSection.Mod || !string.IsNullOrEmpty(loader))
                && (section != DownloadSection.DataPack || world is not null);
        var targetText = section == DownloadSection.Modpack ? target?.Folder?.Name + " / 新实例"
            : instance is null ? "选择安装目标后可安装，或使用另存为"
            : section == DownloadSection.DataPack ? instance.Name + " / " + (world?.Name ?? "未选择存档") + " / datapacks"
            : instance.Name + " / " + ResourceSubdirectory(section);
        ConfigureResourceDialog(_resourceDialog, section, target, world, game, loader, targetText, canInstall);
        InstallDialogHost.Children.Clear();
        InstallDialogHost.Children.Add(_resourceDialog);
        InstallDialogHost.Visibility = Visibility.Visible;
        _resourceDialog.Focus(FocusState.Programmatic);
        await _resourceDialog.LoadAsync(resource, section, game, loader, targetText, canInstall);
    }

    private void CloseResourceDialog()
    {
        _resourceDialog?.Cancel();
        foreach (var previous in _resourceHistory) previous.Cancel();
        _resourceHistory.Clear();
        InstallDialogHost.Children.Clear();
        InstallDialogHost.Visibility = Visibility.Collapsed;
        ResourceList.Focus(FocusState.Programmatic);
    }

    private void ConfigureResourceDialog(ResourceDownloadDialog dialog, DownloadSection section, DownloadTarget? target,
        WorldTarget? world, string game, string loader, string targetText, bool canInstall)
    {
        dialog.SetTargetInstance(target?.Instance);
        dialog.SetHost(CloseResourceDialog, (choice, token) => StartResourceDownloadAsync(choice, section, target, world, token),
            async (project, preferredVersion) =>
            {
                if (_resourceDialog != dialog) return;
                if (_resourceHistory.Count >= 16)
                {
                    App.Services.GetRequiredService<ToastService>().ShowError("请先返回上一级", "前置资源浏览层级过深。");
                    return;
                }
                var next = new ResourceDownloadDialog();
                dialog.SuspendForNavigation();
                _resourceHistory.Push(dialog);
                var nextSection = project.ProjectType switch
                {
                    "resourcepack" => DownloadSection.ResourcePack, "shader" => DownloadSection.Shader,
                    "datapack" => DownloadSection.DataPack, "modpack" => DownloadSection.Modpack, _ => DownloadSection.Mod,
                };
                var nextLoader = nextSection == DownloadSection.Mod ? ResolveLoader(target?.Instance?.Loader ?? "") ?? ""
                    : nextSection == DownloadSection.DataPack ? "datapack" : "";
                var nextTarget = target?.Instance is { } instance ? instance.Name + " / " + ResourceSubdirectory(nextSection) : targetText;
                var nextCanInstall = target?.Instance is not null && nextSection != DownloadSection.Modpack
                    && (nextSection != DownloadSection.Mod || nextLoader.Length > 0) && (nextSection != DownloadSection.DataPack || world is not null);
                ConfigureResourceDialog(next, nextSection, target, world, game, nextLoader, nextTarget, nextCanInstall);
                _resourceDialog = next;
                InstallDialogHost.Children.Clear();
                InstallDialogHost.Children.Add(next);
                var entry = new ResourceEntry(project.Title, project.Description, project.Source, "", string.Join(" / ", project.GameVersions.Take(3)),
                    project.Downloads.ToString("N0"), project.Title, project.Id, project.ProjectType, project.Categories, project.IconUrl,
                    project.GameVersions, project.WebsiteUrl, project.Downloads, project.Updated);
                await next.LoadAsync(entry, nextSection, game, nextLoader, nextTarget, nextCanInstall, preferredVersion);
            }, _resourceHistory.Count > 0 ? ReturnToResource : null);
    }

    private void ReturnToResource()
    {
        if (!_resourceHistory.TryPop(out var previous)) return;
        _resourceDialog?.Cancel();
        previous.ResumeFromNavigation();
        _resourceDialog = previous;
        InstallDialogHost.Children.Clear();
        InstallDialogHost.Children.Add(previous);
    }

    private async Task<bool> StartResourceDownloadAsync(ResourceInstallChoice choice, DownloadSection section,
        DownloadTarget? target, WorldTarget? world, CancellationToken cancellationToken)
    {
        var transfer = App.Services.GetRequiredService<TransferViewModel>();
        var file = choice.Version.PrimaryFile ?? throw new InvalidOperationException("该版本没有可下载文件。");
        var gameRoot = target?.Folder?.Path ?? (target?.Instance is { } selected
            ? System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(selected.Path))! : _instances.FolderSelectionPath)
            ?? System.IO.Path.Combine(GameFolderService.LauncherRoot, ".minecraft");
        if (choice.SaveAs)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ResourceFileName.Format(choice.Project.Name, file.Name,
                App.Services.GetRequiredService<SettingsStore>().Load().ResourceFileNameFormat) };
            var extension = System.IO.Path.GetExtension(file.Name);
            picker.FileTypeChoices.Add("资源文件", new[] { string.IsNullOrWhiteSpace(extension) ? ".zip" : extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var destination = await picker.PickSaveFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (destination is null) return false;
            transfer.EnqueueFileDownload("下载 " + choice.Project.Name, "另存为", gameRoot,
                [new(destination.Path, "资源", file.Sha1, file.Size, CommunityResourceService.DownloadSources(file.Url).ToArray(), file.Md5)],
                choice.Project.Name, "所选保存位置", destination.Path);
            return true;
        }
        if (section == DownloadSection.Modpack)
        {
            ModrinthModpackInstaller.ValidateName(choice.InstanceName);
            var packDirectory = System.IO.Path.Combine(gameRoot, "versions", choice.InstanceName);
            var marker = System.IO.Path.Combine(packDirectory, ".97launch-installing");
            var canResume = File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellationToken) == choice.Project.Source + ":" + choice.Version.Id;
            if (Directory.Exists(packDirectory) && !canResume
                || transfer.Jobs.Any(job => job.InstanceName == choice.InstanceName && job.GameDirectory == gameRoot
                    && job.State is TransferJobState.Active or TransferJobState.Queued))
                throw new InvalidOperationException("同名实例已存在或正在安装，请修改实例名称。");
            transfer.EnqueueResourceInstall("安装整合包 · " + choice.Project.Name, "实例：" + choice.InstanceName, gameRoot,
                choice.InstanceName, (progress, token) => App.Services.GetRequiredService<ModrinthModpackInstaller>()
                    .InstallAsync(file, choice.Version.Id, gameRoot, choice.InstanceName, progress, token, choice.Project.Source),
                choice.Project.Name, choice.InstanceName);
            return true;
        }
        var instance = target?.Instance ?? throw new InvalidOperationException("请先选择安装目标。");
        if (!Directory.Exists(instance.Path)) throw new InvalidOperationException("目标实例已不存在，请刷新实例列表。");
        var effectiveDirectory = GetGameDirectory(instance);
        string destinationDirectory;
        if (section == DownloadSection.DataPack)
        {
            if (world is null || !Directory.Exists(world.Path)) throw new InvalidOperationException("请先选择有效的存档世界。");
            var sessions = App.Services.GetRequiredService<GameSessionManager>().ActiveSessions;
            if (sessions.Any(session => session.InstancePath == instance.Path
                || System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(session.InstancePath)) == effectiveDirectory))
                throw new InvalidOperationException("请先关闭使用此游戏目录的实例，再安装数据包。");
            var lockPath = System.IO.Path.Combine(world.Path, "session.lock");
            if (File.Exists(lockPath))
            {
                try { using var worldLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { throw new InvalidOperationException("这个世界正在使用，请退出世界后再安装数据包。"); }
            }
            destinationDirectory = System.IO.Path.Combine(world.Path, "datapacks");
        }
        else destinationDirectory = System.IO.Path.Combine(effectiveDirectory, ResourceSubdirectory(section));
        if (section == DownloadSection.Mod)
        {
            var context = App.Services.GetRequiredService<InstanceContentService>().Context(instance);
            App.Services.GetRequiredService<InstanceContentService>().EnsureWritable(context);
            var mods = App.Services.GetRequiredService<ModInstallService>();
            var versions = await App.Services.GetRequiredService<CommunityResourceService>().GetVersionsAsync(choice.Project.Project, cancellationToken);
            var installed = await mods.FindInstalledAsync(context, choice.Project.Project, versions, cancellationToken);
            if (installed.Any(f => f.Version?.Id == choice.Version.Id))
                throw new InvalidOperationException("此版本已安装" + (installed.Any(f => f.Version?.Id == choice.Version.Id && !f.File.Enabled) ? "，请在实例 Mod 页面启用。" : "。"));
            if (installed.Count > 0 && !await ContentUi.ConfirmAsync("替换已有 Mod", "此实例已安装：\n"
                + string.Join("\n", installed.Select(f => f.File.Name + " " + f.File.Version))
                + "\n\n将替换为 " + choice.Version.Name + "，同时检查必要前置并备份旧文件。", "替换并安装", XamlRoot)) return false;
            var format = App.Services.GetRequiredService<SettingsStore>().Load().ResourceFileNameFormat;
            if (installed.Count > 1) throw new InvalidOperationException("此项目存在多个已安装文件，请先在实例 Mod 页面处理重复文件。");
            var original = installed.SingleOrDefault()?.File;
            var originalHash = installed.SingleOrDefault()?.Sha1;
            var selection = new ModInstallSelection(choice.Project.Project, choice.Version, original, originalHash);
            transfer.EnqueueOperation("安装 Mod · " + choice.Project.Name, instance.Name, gameRoot, ["检查前置、下载并安装 Mod"],
                (progress, token) => mods.InstallAsync(context, [selection], format, progress, token));
            App.Services.GetRequiredService<ToastService>().Show("开始下载", choice.Project.Name + " 已加入队列，可在传输页面查看进度。");
            return true;
        }
        var loader = section == DownloadSection.Mod ? ResolveLoader(instance.Loader) ?? ""
            : section == DownloadSection.DataPack ? "datapack"
            : choice.Version.Loaders.FirstOrDefault(value => value is "iris" or "optifine") ?? "";
        var files = await App.Services.GetRequiredService<CommunityResourceService>()
            .ResolveFilesAsync(choice.Project.Project, instance.MinecraftVersionId, loader, destinationDirectory,
                choice.Version.Id, new HashSet<string>(), App.Services.GetRequiredService<SettingsStore>().Load().ResourceFileNameFormat,
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transfer.EnqueueFileDownload($"下载 {ViewModel.ResourceTypeName} · {choice.Project.Name}", "实例：" + instance.Name, gameRoot, files,
            choice.Project.Name, instance.Name);
        return true;
    }

    private static string ResourceSubdirectory(DownloadSection section) => section switch
    {
        DownloadSection.Shader => "shaderpacks", DownloadSection.ResourcePack => "resourcepacks",
        DownloadSection.DataPack => "datapacks", _ => "mods",
    };

    private static string? ResolveLoader(string value)
    {
        if (value.Contains("Fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (value.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (value.Contains("Forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        if (value.Contains("Quilt", StringComparison.OrdinalIgnoreCase)) return "quilt";
        return null;
    }
}
