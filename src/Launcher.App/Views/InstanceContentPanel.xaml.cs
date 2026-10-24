using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Launcher.App.Views;

public sealed partial class InstanceContentPanel : UserControl
{
    private readonly InstanceContentService _service = App.Services.GetRequiredService<InstanceContentService>();
    private InstanceContext? _context;
    private ContentKind _kind;
    private CancellationTokenSource? _scan;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly ObservableCollection<ContentRow> _visible = [];
    private List<ContentRow> _rows = [];
    private bool _busy;
    private CancellationTokenSource? _operationCancellation;
    public event Action<bool>? BusyChanged;
    public event Action? ModifyRequested;

    public InstanceContentPanel()
    {
        InitializeComponent();
        FileList.ItemsSource = _visible;
        ScreenshotList.ItemsSource = _visible;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); if (!_busy) await RefreshAsync(); };
        RegisterPropertyChangedCallback(VisibilityProperty, async (_, _) =>
        {
            if (Visibility == Visibility.Visible) await RefreshAsync();
            else { _scan?.Cancel(); _watcher?.Dispose(); _watcher = null; _debounce.Stop(); }
        });
        Loaded += async (_, _) =>
        {
            if (App.MainWindow is { } window) window.Activated += Window_Activated;
            _service.Changed += Content_Changed;
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _scan?.Cancel(); _operationCancellation?.Cancel(); _watcher?.Dispose(); _watcher = null; _debounce.Stop();
            _service.Changed -= Content_Changed;
            if (App.MainWindow is { } window) window.Activated -= Window_Activated;
        };
    }

    public void Load(InstanceItem instance, ContentKind kind)
    {
        _scan?.Cancel(); _watcher?.Dispose(); _watcher = null;
        _context = _service.Context(instance); _kind = kind;
        Heading.Text = kind switch { ContentKind.Mods => "Mod", ContentKind.Saves => "存档", ContentKind.Screenshots => "截图", ContentKind.ResourcePacks => "资源包", _ => "光影包" };
        SearchBox.Text = "";
        FilterBox.ItemsSource = kind == ContentKind.Mods ? new[] { "全部", "已启用", "已禁用", "可更新", "存在问题" }
            : kind is ContentKind.ResourcePacks or ContentKind.ShaderPacks ? new[] { "全部", "已选用", "未选用" } : new[] { "全部" };
        FilterBox.SelectedIndex = 0;
        FilterBox.Visibility = kind is ContentKind.Mods or ContentKind.ResourcePacks or ContentKind.ShaderPacks ? Visibility.Visible : Visibility.Collapsed;
        var screenshots = kind == ContentKind.Screenshots;
        ScreenshotList.Visibility = screenshots ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = screenshots ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.Visibility = FolderImportButton.Visibility = screenshots ? Visibility.Collapsed : Visibility.Visible;
        FolderImportButton.Visibility = kind == ContentKind.Mods ? Visibility.Collapsed : FolderImportButton.Visibility;
        DownloadButton.Visibility = kind is ContentKind.Mods or ContentKind.ResourcePacks or ContentKind.ShaderPacks ? Visibility.Visible : Visibility.Collapsed;
        EnableButton.Visibility = DisableButton.Visibility = UpdateButton.Visibility = CheckUpdatesButton.Visibility = kind == ContentKind.Mods ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllButton.Visibility = RestoreModsButton.Visibility = kind == ContentKind.Mods ? Visibility.Visible : Visibility.Collapsed;
        _rows.Clear(); _visible.Clear();
        if (IsLoaded) _ = RefreshAsync();
    }

    private async void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && IsLoaded && Visibility == Visibility.Visible)
        { if (!_busy) await RefreshAsync(); }
    }

    private void Content_Changed(string path)
    {
        if (_context is not null && GameFolderService.PathsEqual(path, _context.GameDirectory))
            DispatcherQueue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
    }

    public async Task RefreshAsync()
    {
        if (_context is null || !IsLoaded || Visibility != Visibility.Visible) return;
        if (_busy) return;
        _scan?.Cancel(); _scan?.Dispose(); _scan = new CancellationTokenSource();
        var token = _scan.Token; var context = _service.Context(_context.Instance); var kind = _kind;
        _context = context;
        ScanProgress.Visibility = Visibility.Visible;
        try
        {
            var directory = context.DirectoryFor(kind);
            DirectoryLabel.Text = (context.Shared ? "共享目录 · " : "独立目录 · ") + directory;
            ToolTipService.SetToolTip(DirectoryLabel, directory);
            var files = await _service.ScanAsync(context, kind, token);
            token.ThrowIfCancellationRequested();
            var old = _rows.ToDictionary(r => r.Entry.Path, StringComparer.OrdinalIgnoreCase);
            _rows = files.Select(f => new ContentRow(f, kind)
            { Online = old.TryGetValue(f.Path, out var previous) && previous.Entry.Size == f.Size && previous.Entry.Modified == f.Modified ? previous.Online : null }).ToList();
            if (kind == ContentKind.Mods) UpdateDiagnostics();
            if (kind == ContentKind.ResourcePacks)
            {
                var format = await _service.ResourceFormatAsync(context, token);
                token.ThrowIfCancellationRequested();
                foreach (var row in _rows)
                    row.Compatible = format is null || row.Entry.MinimumFormat is null || row.Entry.MaximumFormat is null ? null
                        : format >= row.Entry.MinimumFormat && format <= row.Entry.MaximumFormat;
            }
            ApplyPackStates();
            Filter();
            var errors = _rows.Count(r => r.Entry.Error is not null);
            StatusLabel.Text = $"共 {_rows.Count} 项" + (errors > 0 ? $"，其中 {errors} 项读取异常，可打开详情查看" : "");
            if (kind == ContentKind.Mods && _rows.Any(r => r.DiagnosticText.Length > 0)) StatusLabel.Text += $"，{_rows.Count(r => r.DiagnosticText.Length > 0)} 项存在问题";
            if (kind == ContentKind.Mods && !context.Instance.IsModdable)
                StatusLabel.Text = "当前实例尚未安装 Mod 加载器。可浏览本地文件，安装 Mod 前请先在“修改”中选择加载器。";
            Watch(directory);
            if (kind == ContentKind.Mods) _ = EnrichModsAsync(context, _rows.ToArray(), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Serilog.Log.Warning(ex, "读取实例资源失败：{Instance}，{Kind}", _context?.Instance.Name, _kind);
                StatusLabel.Text = "读取失败：" + ex.Message;
                EmptyTitle.Text = "无法读取目录"; EmptyDetail.Text = ex.Message; EmptyState.Visibility = Visibility.Visible;
            }
        }
        finally { if (_scan?.Token == token) ScanProgress.Visibility = Visibility.Collapsed; }
    }

    private async Task EnrichModsAsync(InstanceContext context, ContentRow[] rows, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var service = App.Services.GetRequiredService<LocalModOnlineService>();
        await Task.WhenAll(rows.Select(async row =>
        {
            try { await gate.WaitAsync(token); } catch (OperationCanceledException) { return; }
            try
            {
                var online = await service.LookupAsync(context, row.Entry, token);
                if (!token.IsCancellationRequested && _rows.Contains(row))
                {
                    row.Online = online;
                    UpdateDiagnostics();
                    if (FilterBox.SelectedIndex >= 3) Filter();
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or JsonException or OperationCanceledException or InvalidDataException) { }
            finally { gate.Release(); }
        }));
    }

    private void UpdateDiagnostics()
    {
        if (_context is null || _kind != ContentKind.Mods) return;
        var issues = ModDiagnostics.Inspect(_rows.Select(r => r.Entry).ToArray(), _context.Instance.MinecraftVersionId, _context.Instance.Loader);
        foreach (var row in _rows)
        {
            var messages = issues[row.Entry.Path].ToList();
            if (row.Entry.Enabled && row.Online is { } online && !Launcher.Core.Downloads.ResourceVersionSelection.IsCompatible(
                    online.Current, _context.Instance.MinecraftVersionId, _context.Instance.Loader.ToLowerInvariant()))
                messages.Add("发布版本不支持当前 Minecraft 或加载器");
            if (row.Entry.Enabled && row.Online is { } known)
                foreach (var conflict in known.Current.Dependencies.Where(d => d.Type == "incompatible"))
                    if (_rows.Any(other => other.Entry.Enabled && other.Online?.Project.Source == known.Project.Source
                        && (string.IsNullOrEmpty(conflict.ProjectId) || other.Online.Project.Id == conflict.ProjectId)
                        && (string.IsNullOrEmpty(conflict.VersionId) || other.Online.Current.Id == conflict.VersionId)))
                        messages.Add("与已启用的 Mod 不兼容：" + (conflict.ProjectId ?? conflict.VersionId));
            row.DiagnosticText = string.Join("；", messages.Distinct());
        }
    }

    private void Watch(string directory)
    {
        _watcher?.Dispose();
        var watch = Directory.Exists(directory) ? directory : _context!.GameDirectory;
        if (!Directory.Exists(watch)) return;
        _watcher = new(watch) { IncludeSubdirectories = Directory.Exists(directory), NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
        _watcher.Changed += changed; _watcher.Created += changed; _watcher.Deleted += changed;
        _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
        _watcher.Error += (_, _) => DispatcherQueue.TryEnqueue(() => StatusLabel.Text = "目录发生了大量变化，请点击刷新重新读取。");
        _watcher.EnableRaisingEvents = true;
    }

    private void Filter()
    {
        if (SearchBox is null) return;
        var search = SearchBox.Text.Trim(); var filter = FilterBox.SelectedIndex;
        var selected = Selected().Select(r => r.Entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _visible.Clear();
        foreach (var row in _rows)
        {
            if (search.Length > 0 && !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.Entry.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.Entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (_kind == ContentKind.Mods && (filter == 1 && !row.Entry.Enabled || filter == 2 && row.Entry.Enabled || filter == 3 && (row.Online?.Update is null || row.Online.IsStale))) continue;
            if (_kind == ContentKind.Mods && filter == 4 && row.DiagnosticText.Length == 0) continue;
            if (_kind is ContentKind.ResourcePacks or ContentKind.ShaderPacks && (filter == 1 && !row.Active || filter == 2 && row.Active)) continue;
            _visible.Add(row);
        }
        foreach (var row in _visible.Where(r => selected.Contains(r.Entry.Path))) ActiveList.SelectedItems.Add(row);
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = search.Length > 0 || filter > 0 ? "没有匹配的内容" : "暂无" + Heading.Text;
        EmptyDetail.Text = search.Length > 0 || filter > 0 ? "试试其他关键词或筛选条件。" : _kind == ContentKind.Screenshots ? "在游戏中按 F2 拍摄截图后，会在这里显示。" : "可以从文件导入，或打开目录放入文件。";
        UpdateSelection();
    }

    private ListViewBase ActiveList => _kind == ContentKind.Screenshots ? ScreenshotList : FileList;
    private ContentRow[] Selected() => ActiveList.SelectedItems.OfType<ContentRow>().ToArray();
    private void UpdateSelection() => SelectionLabel.Text = $"已选 {Selected().Length} 项";
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Filter();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => ActiveList.SelectAll();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ActiveList.SelectedItems.Clear();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _context is null) return;
        _busy = true; Toolbar.IsEnabled = SelectionBar.IsEnabled = false; ScanProgress.Visibility = Visibility.Visible;
        _operationCancellation = new CancellationTokenSource(); BusyChanged?.Invoke(true);
        try { await action(_operationCancellation.Token); }
        catch (OperationCanceledException) { StatusLabel.Text = "操作已取消"; Serilog.Log.Information("实例资源操作已取消：{Instance}，{Kind}", _context.Instance.Name, _kind); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; App.Services.GetRequiredService<ToastService>().ShowError("操作失败", ex.Message); }
        finally
        {
            _busy = false; Toolbar.IsEnabled = SelectionBar.IsEnabled = true; ScanProgress.Visibility = Visibility.Collapsed;
            _operationCancellation?.Dispose(); _operationCancellation = null; BusyChanged?.Invoke(false);
            if (IsLoaded) await RefreshAsync();
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(_ =>
    {
        var directory = _context!.DirectoryFor(_kind); Directory.CreateDirectory(directory); InstanceContentService.Open(directory); return Task.CompletedTask;
    });

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var paths = await ContentUi.PickFilesAsync("导入", _kind == ContentKind.Mods ? [".jar", ".zip", ".litemod", ".disabled", ".old"] : [".zip"]);
        await ImportPathsAsync(paths, token);
    });

    private async void ImportFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var directory = await ContentUi.PickFolderAsync("导入文件夹");
        if (directory is not null) await ImportPathsAsync([directory], token);
    });

    private async Task ImportPathsAsync(string[] paths, CancellationToken token)
    {
        if (paths.Length == 0) return;
        if (_kind == ContentKind.Mods && !_context!.Instance.IsModdable) throw new InvalidOperationException("请先为此实例安装兼容的 Mod 加载器。");
        var conflict = paths.Any(p => File.Exists(Path.Combine(_context!.DirectoryFor(_kind), LocalContentReader.EnabledName(Path.GetFileName(p))))
            || Directory.Exists(Path.Combine(_context!.DirectoryFor(_kind), Path.GetFileName(p))));
        if (conflict && _kind != ContentKind.Saves && !await ContentUi.ConfirmAsync("存在同名文件", "替换前会将已有文件移到回收站。是否继续？", "替换", XamlRoot)) return;
        await _service.ImportAsync(_context!, _kind, paths, conflict, token);
        App.Services.GetRequiredService<ToastService>().Show("导入完成", $"已导入到 {_context!.Instance.Name} 的{Heading.Text}目录");
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        App.Services.GetRequiredService<PendingResourceBrowse>().Request(new(_context.Instance.Id,
            _kind == ContentKind.Mods ? "mod" : _kind == ContentKind.ResourcePacks ? "resourcepack" : "shader", SearchBox.Text));
        App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Download);
    }

    private async void Enable_Click(object sender, RoutedEventArgs e) => await ToggleAsync(Selected(), true);
    private async void Disable_Click(object sender, RoutedEventArgs e) => await ToggleAsync(Selected(), false);
    private Task ToggleAsync(ContentRow[] rows, bool enabled) => RunAsync(token => _service.ToggleModsAsync(_context!, rows.Select(r => r.Entry).ToArray(), enabled, token));
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteAsync(Selected());
    private async Task DeleteAsync(ContentRow[] rows)
    {
        if (rows.Length == 0) return;
        if (!await ContentUi.ConfirmAsync("移到回收站", $"确定删除这 {rows.Length} 项内容？\n" + string.Join("\n", rows.Take(8).Select(r => r.Name)), "移到回收站", XamlRoot)) return;
        await RunAsync(token => _service.DeleteAsync(_context!, _kind, rows.Select(r => r.Entry).ToArray(), token));
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var online = App.Services.GetRequiredService<LocalModOnlineService>(); var failures = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            StatusLabel.Text = $"正在检查 {_rows[i].Name}（{i + 1}/{_rows.Count}）";
            try { _rows[i].Online = await online.LookupAsync(_context!, _rows[i].Entry, token, true); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException) { _rows[i].Online = null; failures++; }
            if (_rows[i].Online?.IsStale == true) failures++;
        }
        UpdateDiagnostics();
        Filter();
        App.Services.GetRequiredService<ToastService>().Show("检查完成", $"{_rows.Count(r => r.Online?.Update is not null && !r.Online.IsStale)} 项可更新" + (failures > 0 ? $"，{failures} 项联网失败" : ""));
    });

    private async void Update_Click(object sender, RoutedEventArgs e) => await UpdateAsync(Selected());
    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await UpdateAsync(_rows.ToArray());
    private Task RepairDependenciesAsync(ContentRow row) => RunAsync(async token =>
    {
        var context = _context!;
        var online = await App.Services.GetRequiredService<LocalModOnlineService>().LookupAsync(context, row.Entry, token, true);
        if (online is null || online.IsStale) throw new IOException("未能识别或刷新此 Mod 的来源，无法自动补齐。请在下载页面搜索所需前置。");
        var hash = await InstanceModificationService.HashAsync(row.Entry.Path, token);
        if (!online.Current.Files.Any(f => string.Equals(f.Sha1, hash, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Mod 文件已发生变化，请刷新后重试。");
        var selection = new ModInstallSelection(online.Project, online.Current, row.Entry, hash);
        App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("补齐 Mod 前置 · " + row.Name, context.Instance.Name,
            context.Root, ["检查并补齐必要前置"], (progress, cancel) => App.Services.GetRequiredService<ModInstallService>()
                .InstallAsync(context, [selection], "{original}", progress, cancel));
        App.Services.GetRequiredService<ToastService>().Show("开始检查前置", row.Name + " 已加入队列，可在传输页面查看进度。");
    });
    private async void RestoreMods_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || !await ContentUi.ConfirmAsync("回退上次 Mod 变更", "将恢复上次安装或批量更新前的 Mod 文件，并撤回当次新增的文件。后续手动修改过的文件会阻止回退。", "回退变更", XamlRoot)) return;
        await RunAsync(async token =>
        {
            await App.Services.GetRequiredService<ModInstallService>().RestoreLastAsync(_context, token);
            App.Services.GetRequiredService<ToastService>().Show("回退完成", "已恢复上次变更前的 Mod 文件。");
        });
    }
    private async Task UpdateAsync(ContentRow[] rows)
    {
        if (rows.Length == 0 || _context is null) return;
        await RunAsync(async token =>
        {
            var service = App.Services.GetRequiredService<LocalModOnlineService>();
            var failures = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                StatusLabel.Text = "检查更新：" + row.Name;
                try { row.Online = await service.LookupAsync(_context, row.Entry, token, true); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or JsonException or IOException or InvalidDataException) { row.Online = null; failures++; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { row.Online = null; failures++; }
                if (row.Online?.IsStale == true) failures++;
            }
            var updates = rows.Where(r => r.Online?.Update is not null && !r.Online.IsStale).ToArray();
            if (updates.Length == 0) { await ContentUi.MessageAsync("没有可用更新", "没有找到适用于当前 Minecraft 和加载器的正式版更新。无法识别来源的文件仍可手动管理。"
                + (failures > 0 ? $"\n{failures} 项未能完成联网检查，请稍后重试。" : ""), XamlRoot); return; }
            var choices = updates.Select(row => new CheckBox { Content = row.Name + "  " + row.Entry.Version + " → " + row.Online!.Update!.Number, IsChecked = true }).ToArray();
            var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
            panel.Children.Add(new TextBlock { Text = "选择需要更新的 Mod。必要前置将一起检查，全部下载完成后统一替换，旧文件可在 Mod 页面回退。"
                + (failures > 0 ? $"\n{failures} 项联网失败，未纳入本次更新。" : ""), TextWrapping = TextWrapping.Wrap });
            foreach (var check in choices) panel.Children.Add(check);
            var dialog = new ContentDialog { Title = $"更新 Mod（{updates.Length}）", Content = new ScrollViewer { Content = panel, MaxHeight = 380 }, PrimaryButtonText = "开始更新", CloseButtonText = "取消" };
            if (await ContentUi.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary) return;
            updates = updates.Where((_, index) => choices[index].IsChecked == true).ToArray();
            if (updates.Length == 0) return;
            var context = _context;
            var batch = updates.Select(r => (r.Entry, r.Online!)).ToArray();
            App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("更新 Mod", context.Instance.Name, context.Root, ["检查前置、下载并替换 Mod"],
                (progress, cancel) => service.UpdateBatchAsync(context, batch, progress, cancel));
            App.Services.GetRequiredService<ToastService>().Show("开始更新", $"{updates.Length} 项 Mod 已加入队列，可在传输页查看进度。");
        });
    }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ContentRow row } element) ShowMenu(element, row); }
    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ContentRow row } element) { ShowMenu(element, row); e.Handled = true; } }
    private async void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ContentRow row }) { if (_kind == ContentKind.Screenshots) await PreviewAsync(row); else await DetailsAsync(row); e.Handled = true; } }

    private void ShowMenu(FrameworkElement element, ContentRow row)
    {
        var menu = new MenuFlyout();
        void Item(string title, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = title }; item.Click += async (_, _) =>
            { try { await action(); } catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("操作失败", ex.Message); } };
            menu.Items.Add(item);
        }
        Item(_kind == ContentKind.Screenshots ? "预览" : "详情", () => _kind == ContentKind.Screenshots ? PreviewAsync(row) : DetailsAsync(row));
        if (_kind == ContentKind.Mods)
        {
            Item(row.Entry.Enabled ? "禁用" : "启用", () => ToggleAsync([row], !row.Entry.Enabled));
            Item("检查并更新", () => UpdateAsync([row]));
            Item("补齐必要前置", () => RepairDependenciesAsync(row));
        }
        if (_kind is ContentKind.ResourcePacks or ContentKind.ShaderPacks)
        {
            Item(row.Active ? "停用" : "选用", () => SetPackAsync(row, !row.Active));
            if (_kind == ContentKind.ResourcePacks && row.Active)
            {
                Item("提高优先级", () => SetPackAsync(row, true, 1));
                Item("降低优先级", () => SetPackAsync(row, true, -1));
            }
        }
        if (_kind == ContentKind.Saves) Item("备份存档", () => ExportFileAsync(row, true));
        Item(_kind == ContentKind.Screenshots ? "另存为" : "导出", () => ExportFileAsync(row, false));
        if (_kind == ContentKind.Screenshots) Item("复制图片", async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(row.Entry.Path);
            var package = new DataPackage(); package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file)); Clipboard.SetContent(package);
        });
        if (Directory.Exists(row.Entry.Path))
            Item("打开文件夹", () => { InstanceContentService.Open(row.Entry.Path); return Task.CompletedTask; });
        else if (_kind == ContentKind.Screenshots)
            Item("用系统查看器打开", () => { InstanceContentService.Open(row.Entry.Path); return Task.CompletedTask; });
        Item("在资源管理器中定位", () => { InstanceContentService.Open(row.Entry.Path, true); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("移到回收站", () => DeleteAsync([row]));
        menu.ShowAt(element);
    }

    private async Task DetailsAsync(ContentRow row)
    {
        var entry = row.Entry;
        var text = $"文件：{entry.FileName}\n版本：{entry.Version}\n位置：{entry.Path}\n\n{entry.Description}";
        if (entry.Error is not null) text += "\n\n" + entry.Error;
        if (row.DiagnosticText.Length > 0) text += "\n\n检查结果：\n" + row.DiagnosticText;
        if (_kind == ContentKind.Mods && entry.RequiredMods is { Count: > 0 })
        {
            var ids = _rows.Where(r => r.Entry.Enabled).Select(r => r.Entry.ModId).ToHashSet();
            text += "\n\n声明的必要前置（这里只检查是否存在）：\n" + string.Join("\n", entry.RequiredMods.Select(id => id + (ids.Contains(id) ? " · 已安装" : " · 未找到或未启用")));
        }
        await ContentUi.MessageAsync(row.Name, text, XamlRoot);
    }

    private async Task PreviewAsync(ContentRow row)
    {
        var shots = _visible.ToArray(); var index = Array.IndexOf(shots, row);
        var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 680, Height = 360 };
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        var viewer = new ScrollViewer { ZoomMode = ZoomMode.Enabled, MinZoomFactor = .25f, MaxZoomFactor = 5, Height = 360, Content = image };
        panel.Children.Add(viewer); panel.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var previous = new Button { Content = "上一张" }; var next = new Button { Content = "下一张" };
        var open = new Button { Content = "系统打开" };
        buttons.Children.Add(previous); buttons.Children.Add(next); buttons.Children.Add(open); panel.Children.Add(buttons);
        void Update() { image.Source = new BitmapImage(new Uri(shots[index].Entry.Path)); label.Text = shots[index].Entry.FileName; previous.IsEnabled = index > 0; next.IsEnabled = index + 1 < shots.Length; viewer.ChangeView(0, 0, 1); }
        previous.Click += (_, _) => { index--; Update(); }; next.Click += (_, _) => { index++; Update(); };
        open.Click += (_, _) => InstanceContentService.Open(shots[index].Entry.Path);
        Update();
        await ContentUi.ShowAsync(new ContentDialog { Title = "截图预览", Content = panel, CloseButtonText = "关闭" }, XamlRoot);
    }

    private async Task ExportFileAsync(ContentRow row, bool backup)
    {
        var context = _context!;
        var directory = Directory.Exists(row.Entry.Path);
        string? destination;
        if (backup)
        {
            var folder = Path.Combine(context.Root, "97Launch", "backups", "worlds"); Directory.CreateDirectory(folder);
            destination = Path.Combine(folder, Path.GetFileName(row.Entry.Path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            if (File.Exists(destination)) destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        }
        else destination = await ContentUi.SaveAsync(row.Entry.FileName, (directory ? "ZIP 压缩文件" : "原始文件", directory ? ".zip" : Path.GetExtension(row.Entry.Path)));
        if (destination is null) return;
        await RunAsync(async token =>
        {
            using var operation = await _service.AcquireAsync(context, token);
            if (directory && destination.StartsWith(row.Entry.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("不能把导出文件放进被导出的存档目录。");
            var temp = destination + ".97launch.tmp";
            try
            {
                await Task.Run(async () =>
                {
                    if (directory)
                    {
                        using var archive = ZipFile.Open(temp, ZipArchiveMode.Create);
                        foreach (var file in InstanceContentService.Files(row.Entry.Path))
                        {
                            token.ThrowIfCancellationRequested();
                            await using var input = File.OpenRead(file); await using var output = archive.CreateEntry(Path.GetRelativePath(row.Entry.Path, file).Replace('\\', '/'), CompressionLevel.Fastest).Open();
                            await input.CopyToAsync(output, token);
                        }
                    }
                    else File.Copy(row.Entry.Path, temp, true);
                }, token);
                File.Move(temp, destination, true);
                App.Services.GetRequiredService<ToastService>().Show(backup ? "备份完成" : "导出完成", destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });
    }

    private List<string> SelectedPacks()
    {
        try { return JsonSerializer.Deserialize<List<string>>(_service.ReadOption(_context!, "options.txt", "resourcePacks:")) ?? []; }
        catch (JsonException) { return []; }
    }
    private void ApplyPackStates()
    {
        if (_context is null) return;
        if (_kind == ContentKind.ResourcePacks)
        {
            var packs = SelectedPacks();
            foreach (var row in _rows) { row.Active = packs.Contains("file/" + row.Entry.FileName) || packs.Contains(row.Entry.FileName); row.RefreshState(); }
        }
        else if (_kind == ContentKind.ShaderPacks)
        {
            var iris = _service.ReadOption(_context, "config/iris.properties", "shaderPack=");
            var opti = _service.ReadOption(_context, "optionsshaders.txt", "shaderPack=");
            var enabled = _service.ReadOption(_context, "config/iris.properties", "enableShaders=") != "false";
            foreach (var row in _rows) { row.Active = enabled && DecodeProperty(iris) == row.Entry.FileName || opti == row.Entry.FileName; row.RefreshState(); }
        }
    }

    private async Task SetPackAsync(ContentRow row, bool enabled, int move = 0) => await RunAsync(async token =>
    {
        var context = _context!;
        if (_kind == ContentKind.ResourcePacks)
        {
            if (enabled && !row.Active && row.Compatible == false
                && !await ContentUi.ConfirmAsync("资源包格式不兼容", "此资源包声明的格式范围与当前 Minecraft 不一致，可能出现纹理缺失。是否仍要选用？", "仍然选用", XamlRoot)) return;
            var packs = SelectedPacks();
            var key = packs.Contains(row.Entry.FileName) ? row.Entry.FileName : "file/" + row.Entry.FileName;
            if (move != 0)
            {
                var index = packs.IndexOf(key); var target = Math.Clamp(index + move, 0, packs.Count - 1);
                if (index >= 0 && target != index) { packs.RemoveAt(index); packs.Insert(target, key); }
            }
            else if (enabled) { if (!packs.Contains(key)) packs.Add(key); }
            else packs.Remove(key);
            await _service.WriteOptionAsync(context, "options.txt", "resourcePacks:", JsonSerializer.Serialize(packs), token);
            List<string> incompatible;
            try { incompatible = JsonSerializer.Deserialize<List<string>>(_service.ReadOption(context, "options.txt", "incompatibleResourcePacks:")) ?? []; }
            catch (JsonException) { incompatible = []; }
            if (enabled && row.Compatible == false) { if (!incompatible.Contains(key)) incompatible.Add(key); }
            else incompatible.Remove(key);
            await _service.WriteOptionAsync(context, "options.txt", "incompatibleResourcePacks:", JsonSerializer.Serialize(incompatible), token);
        }
        else
        {
            var mods = await _service.ScanAsync(context, ContentKind.Mods, token);
            var iris = mods.Any(m => m.Enabled && m.ModId is "iris" or "oculus");
            var manifest = InstanceInstallManifest.TryLoad(context.Instance.Path);
            var opti = manifest?.OptiFineVersion is not null || context.Instance.Loader == "OptiFine" || mods.Any(m => m.Enabled && m.FileName.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase));
            if (!iris && !opti)
            {
                if (await ContentUi.ConfirmAsync("尚未安装光影支持组件", "需要安装与当前 Minecraft 和加载器兼容的 Iris、Oculus 或 OptiFine。是否前往下载支持组件？", "前往下载", XamlRoot))
                {
                    if (context.Instance.Loader is "Fabric" or "NeoForge")
                    {
                        App.Services.GetRequiredService<PendingResourceBrowse>().Request(new(context.Instance.Id, "mod", "Iris"));
                        App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Download);
                    }
                    else ModifyRequested?.Invoke();
                }
                return;
            }
            if (iris)
            {
                await _service.WriteOptionAsync(context, "config/iris.properties", "shaderPack=", enabled ? EncodeProperty(row.Entry.FileName) : "", token);
                await _service.WriteOptionAsync(context, "config/iris.properties", "enableShaders=", enabled ? "true" : "false", token);
            }
            if (opti) await _service.WriteOptionAsync(context, "optionsshaders.txt", "shaderPack=", enabled ? row.Entry.FileName : "OFF", token);
        }
    });

    private static string EncodeProperty(string text) => string.Concat(text.Select(c => c > 127 ? "\\u" + ((int)c).ToString("X4") : c is '\\' or '=' or ':' or ' ' ? "\\" + c : c.ToString()));
    private static string DecodeProperty(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\\u([0-9a-fA-F]{4})|\\(.)", m => m.Groups[1].Success ? ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString() : m.Groups[2].Value);
}

public partial class ContentRow : ObservableObject
{
    public LocalContent Entry { get; }
    public BitmapImage Icon { get; }
    public string Name => Online?.Project.Title ?? Entry.Name;
    public bool IsDisabledMod => _kind == ContentKind.Mods && !Entry.Enabled;
    public Visibility DisabledVisibility => IsDisabledMod ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NormalVisibility => IsDisabledMod ? Visibility.Collapsed : Visibility.Visible;
    public double IconOpacity => IsDisabledMod ? .45 : 1;
    public string Summary => string.Join(" · ", new[] { Entry.Version, Entry.FileName, Entry.Size > 0 ? Bytes(Entry.Size) : "", Entry.Modified.ToString("yyyy/MM/dd HH:mm") }.Where(s => s.Length > 0));
    public bool Active { get; set; }
    public bool? Compatible { get; set; }
    private readonly ContentKind _kind;
    [ObservableProperty] private LocalModOnlineInfo? _online;
    [ObservableProperty] private string _diagnosticText = "";
    public Visibility DiagnosticVisibility => DiagnosticText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    partial void OnDiagnosticTextChanged(string value) => OnPropertyChanged(nameof(DiagnosticVisibility));
    partial void OnOnlineChanged(LocalModOnlineInfo? value) { OnPropertyChanged(nameof(Name)); RefreshState(); }
    public string StateText => Entry.Error ?? (_kind == ContentKind.Mods ? (Entry.Enabled ? "已启用" : "已禁用") + (Online is null ? "" : " · " + Online.Project.Source) + (Online?.IsStale == true ? " · 更新信息未能刷新" : Online?.Update is { } update ? " · 可更新至 " + update.Number : "")
        : _kind == ContentKind.ResourcePacks ? (Active ? "已选用" : "未选用") + (Compatible is true ? " · 格式兼容" : Compatible is false ? " · 格式不兼容" : " · 兼容性待确认")
        : _kind == ContentKind.ShaderPacks ? Active ? "已选用" : "未选用" : Entry.Description);
    public ContentRow(LocalContent entry, ContentKind kind)
    {
        Entry = entry; _kind = kind;
        Icon = new BitmapImage(new Uri(entry.IconPath ?? "ms-appx:///Assets/Blocks/" + (kind == ContentKind.Mods ? "fabric.png" : "vanilla.png"))) { DecodePixelWidth = kind == ContentKind.Screenshots ? 256 : 64 };
    }
    public void RefreshState() => OnPropertyChanged(nameof(StateText));
    public static string Bytes(long length)
    {
        double size = length; string[] units = ["B", "KB", "MB", "GB"]; var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.#} {units[index]}";
    }
}
