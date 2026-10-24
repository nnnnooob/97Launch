using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Launcher.App.Views;

public sealed partial class InstancesPage : Page
{
    public InstancesViewModel ViewModel { get; }

    public InstancesPage()
    {
        ViewModel = App.Services.GetRequiredService<InstancesViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        InstanceScroller.ViewChanged += OnScrollerViewChanged;
    }

    private IDialogService Dialogs => App.Services.GetRequiredService<IDialogService>();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        CloseSettingsDialog();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateSectionCounts();
        }
        else
        {
            DispatcherQueue.TryEnqueue(UpdateSectionCounts);
        }
    }

    private static LogBuffer Log => App.Services.GetRequiredService<LogBuffer>();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSectionCounts();
        UpdateSectionVisuals();

        // The list starts at the top, so the floating button has no job yet.
        UpdateScrollTopVisibility();

        ApplyPendingSelection();
        OpenRequestedInstanceSettings();
    }

    /// <summary>
    /// Applies a selection requested by another page, e.g. a fresh install.
    /// </summary>
    /// <remarks>
    /// Only the selection is applied, not a navigation: the launch page is the
    /// one that switches pages, and having both act on the same request would
    /// fight over which page ends up on screen. Whichever page loads first takes
    /// the request, and the other sees nothing pending.
    /// </remarks>
    private async void ApplyPendingSelection()
    {
        var id = App.Services.GetRequiredService<PendingInstanceSelection>().Take();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var instance = FindLoadedInstance(id);
        if (instance is null)
        {
            await ViewModel.ReloadFoldersAsync();
            instance = FindLoadedInstance(id);
        }

        if (instance is null)
        {
            Log.Append($"找不到请求选择的实例 ID「{id}」。", LogEntryKind.Warning);
            return;
        }

        ViewModel.SelectedInstance = instance;
        Log.Append($"已选中新安装的实例：{instance.Name}");
    }

    /// <summary>
    /// Opens the settings dialog for an instance requested by another page.
    /// </summary>
    /// <remarks>
    /// The launch page's gear cannot open this dialog itself, because the instance
    /// list - and so the exact instance to edit - belongs to this page. It records
    /// the instance name and navigates here instead; the request is read once, on
    /// load, and cleared so revisiting the page does not reopen the dialog.
    /// </remarks>
    private async void OpenRequestedInstanceSettings()
    {
        var id = App.Services.GetRequiredService<PendingInstanceSettings>().Take();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var instance = FindLoadedInstance(id);

        // Only an exact identity match opens the dialog. If the launch page was
        // showing an instance that is not in any listed folder, opening some other
        // instance's settings would edit the wrong thing, so do nothing instead.
        if (instance is null)
        {
            await ViewModel.ReloadFoldersAsync();
            instance = FindLoadedInstance(id);
        }

        if (instance is null)
        {
            Log.Append($"找不到实例 ID「{id}」，未打开实例设置。", LogEntryKind.Warning);
            return;
        }

        ShowInstanceSettings(instance);
    }

    private InstanceItem? FindLoadedInstance(string id)
        => ViewModel.FindInstance(id);

    // ======== instance card icons ========

    /// <summary>
    /// Swaps in the generic cube when a block icon cannot be loaded.
    /// </summary>
    /// <remarks>
    /// The Image is visible by default so a successful load needs no code path at
    /// all; only the failure case has to touch the tree.
    /// </remarks>
    private void BlockIcon_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image && image.Parent is Grid grid)
        {
            image.Visibility = Visibility.Collapsed;

            foreach (var child in grid.Children)
            {
                if (child is Viewbox fallback)
                {
                    fallback.Visibility = Visibility.Visible;
                }
            }

            Log.Append($"实例图标加载失败（{e.ErrorMessage}），已回退到通用图标。", LogEntryKind.Warning);
        }
    }

    private void UpdateSectionVisuals()
    {
        ModdableSection.IsExpanded = ViewModel.IsModdableExpanded;
        RegularSection.IsExpanded = ViewModel.IsRegularExpanded;
        RareSection.IsExpanded = ViewModel.IsRareExpanded;
        ErrorSection.IsExpanded = ViewModel.IsErrorExpanded;
    }

    private void UpdateSectionCounts()
    {
        var moddableCount = ViewModel.ModdableInstances.Count;
        var regularCount = ViewModel.RegularInstances.Count;
        var rareCount = ViewModel.RareInstances.Count;
        var errorCount = ViewModel.ErrorInstances.Count;
        ModdableCount.Text = moddableCount > 0 ? $"{moddableCount} 个" : string.Empty;
        RegularCount.Text = regularCount > 0 ? $"{regularCount} 个" : string.Empty;
        RareCount.Text = rareCount > 0 ? $"{rareCount} 个" : string.Empty;
        ErrorCount.Text = errorCount > 0 ? $"{errorCount} 个" : string.Empty;
        ErrorSection.Visibility = errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        ModdableSection.Visibility = moddableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModdableList.Visibility = moddableCount > 0 && ViewModel.IsModdableExpanded
            ? Visibility.Visible : Visibility.Collapsed;
        RegularSection.Visibility = regularCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        RegularList.Visibility = regularCount > 0 && ViewModel.IsRegularExpanded
            ? Visibility.Visible : Visibility.Collapsed;
        RareSection.Visibility = rareCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        RareList.Visibility = rareCount > 0 && ViewModel.IsRareExpanded
            ? Visibility.Visible : Visibility.Collapsed;

        var hasAny = moddableCount + regularCount + rareCount + errorCount > 0;
        var isFilteredEmpty = !ViewModel.IsEmpty && !string.IsNullOrWhiteSpace(ViewModel.SearchText);
        var showEmpty = ViewModel.IsLoading || !hasAny;
        SearchToolbar.Visibility = ViewModel.IsEmpty
            ? Visibility.Collapsed : Visibility.Visible;
        InstanceSections.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = ViewModel.IsLoading
            ? "正在获取实例列表"
            : isFilteredEmpty ? "没有匹配的实例" : "无可用实例";
        EmptyMessage.Text = ViewModel.IsLoading
            ? "正在获取当前文件夹的实例列表，请稍候。"
            : isFilteredEmpty
                ? "没有符合当前搜索条件的实例，请修改搜索内容。"
                : "未找到任何游戏实例，请先下载一个游戏实例。\n也可以在左侧添加游戏文件夹；支持已有游戏目录和空文件夹。";
        EmptyDivider.Visibility = ViewModel.IsLoading || isFilteredEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyDownloadButton.Visibility = ViewModel.IsLoading || isFilteredEmpty
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ======== search ========

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only user typing should re-filter; programmatic text changes (and the
        // box restoring its own text on load) would otherwise fight the filter.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        ViewModel.SearchText = sender.Text;
        ApplySearchFilter();
    }

    /// <summary>
    /// Re-filters the three sections for the current search term.
    /// </summary>
    /// <remarks>
    /// Filtering happens in the view model, which rebuilds the bound collections
    /// from the unfiltered scan result. An earlier version walked realized item
    /// containers instead; that broke whenever a row had not been realized yet,
    /// and this way the section counts and the visible rows cannot disagree.
    /// </remarks>
    private void ApplySearchFilter()
    {
        ViewModel.Refilter();
        UpdateSectionCounts();
    }

    // ======== scroll to top ========

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateScrollTopVisibility();

    private void UpdateScrollTopVisibility()
    {
        // 200px in is enough that the button does not flicker on a one-notch
        // wheel tick, and late enough that it does not cover the first card.
        const double Threshold = 200;
        ScrollTopButton.Visibility = InstanceScroller.VerticalOffset > Threshold
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollTop_Click(object sender, RoutedEventArgs e)
    {
        InstanceScroller.ChangeView(null, 0, null);
    }

    private void EmptyDownload_Click(object sender, RoutedEventArgs e)
        => App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Download);

    // ======== instance actions ========

    /// <summary>
    /// Jumps to the launch page with the tapped instance selected.
    /// </summary>
    /// <remarks>
    /// Tapped on the card itself rather than ListView's ItemClick: the lists use
    /// <c>SelectionMode="None"</c> (a persistent highlight would imply a
    /// selection this page never keeps), and in that mode ItemClick never fires.
    /// Handling the tap directly also keeps the three row buttons independent -
    /// they set Handled on their own presses.
    /// </remarks>
    /// <summary>
    /// Jumps to the launch page with the clicked instance selected.
    /// </summary>
    /// <remarks>
    /// Driven by a transparent full-card Button rather than a Tapped handler: the
    /// insted card lives inside a ListView, whose internal ScrollViewer consumes
    /// tap gestures before they surface as Tapped, so a gesture-based handler
    /// never fired. A Button gets its own input path, and the card content above
    /// it is hit-test-disabled so the click reaches it from anywhere on the row.
    /// </remarks>
    private void InstanceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstanceItem instance })
        {
            Serilog.Log.Warning("InstanceCard_Click: DataContext was not an InstanceItem");
            return;
        }

        ViewModel.SelectedInstance = instance;

        App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Launch);

        Log.Append($"切换到实例：{instance.Name}（{instance.GameVersion} · {instance.LoaderDisplay}）");
    }

    private async void InstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        var instance = (sender as FrameworkElement)?.DataContext as InstanceItem;
        if (instance is null)
        {
            Serilog.Log.Warning("InstanceSettings_Click: DataContext was not an InstanceItem");
            await Dialogs.ShowNotImplementedAsync("实例设置", XamlRoot);
            return;
        }

        ShowInstanceSettings(instance);
    }

    /// <summary>
    /// Opens the settings dialog for one instance.
    /// </summary>
    private void ShowInstanceSettings(InstanceItem instance)
    {
        _dialog ??= new InstanceSettingsDialog();
        _dialog.SetHost(CloseSettingsDialog);
        _dialog.Load(instance);

        // Hosted inside this page, not on the window: the dialog then disappears
        // with the page when the user navigates away, rather than hovering over a
        // page it has nothing to do with.
        DialogHost.Children.Clear();
        DialogHost.Children.Add(_dialog);
        DialogHost.Visibility = Visibility.Visible;
    }

    private void CloseSettingsDialog()
    {
        DialogHost.Children.Clear();
        DialogHost.Visibility = Visibility.Collapsed;
    }

    private InstanceSettingsDialog? _dialog;

    /// <summary>
    /// Opens the instance's folder in Explorer.
    /// </summary>
    /// <remarks>
    /// Opening the version folder itself, not the game root: the reason to press
    /// this is almost always to look at that instance's mods or crash reports,
    /// and the version folder is where both live when isolation is on.
    /// </remarks>
    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void InstanceOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstanceItem instance)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(instance.Path))
            {
                await ShowMessageAsync("目录不存在", $"实例目录已不存在：{instance.Path}");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = instance.Path,
                UseShellExecute = true,
            });

            Log.Append($"打开实例目录：{instance.Path}");
        }
        catch (Exception ex)
        {
            Log.Append($"打开目录失败：{ex.Message}", LogEntryKind.Error);
        }
    }

    /// <summary>
    /// Deletes an instance folder, after confirming.
    /// </summary>
    /// <remarks>
    /// Deletion goes to the Recycle Bin rather than a hard delete, so a
    /// mis-click costs the user a trip to the bin instead of their saves. The
    /// prompt names the instance so there is no doubt which one is going.
    /// </remarks>
    private async void InstanceDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstanceItem instance)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "删除实例",
            Content = $"确定要删除「{instance.Name}」吗？\n\n文件夹：{instance.Path}\n\n文件会被移到回收站。",
            PrimaryButtonText = "移到回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await RecycleDirectoryAsync(instance.Path);
            ViewModel.ForgetDeletedInstance(instance.Id);
            if (ViewModel.SelectedInstance?.Id == instance.Id)
            {
                ViewModel.SelectedInstance = null;
            }

            Log.Append($"已删除实例：{instance.Name}");
            await ViewModel.LoadInstancesAsync();
            UpdateSectionCounts();
            UpdateSectionVisuals();
        }
        catch (Exception ex)
        {
            Log.Append($"删除实例失败：{ex.Message}", LogEntryKind.Error);
        }
    }

    /// <summary>
    /// Sends a directory to the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Uses the Windows Shell recycle operation (<c>SHFileOperation</c>) directly,
    /// keeping paths out of an evaluated command line and avoiding PowerShell's
    /// quoting, availability and timeout hazards.
    /// </remarks>
    private static Task RecycleDirectoryAsync(string path)
    {
        // ShellFileOperation is synchronous. Run it off the UI thread so the
        // shell's confirmation/progress UI can complete without freezing XAML.
        return Task.Run(() =>
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"实例文件夹不存在：{path}");
            }

            var operation = new Win32.SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = Win32.FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI,
            };

            var result = Win32.SHFileOperation(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted)
            {
                throw new IOException($"无法将实例移入回收站（错误码 {result}）。");
            }
        });
    }

    private static string NameOf(object sender)
        => (sender as FrameworkElement)?.Tag as string ?? "实例";

    // ======== folder actions ========

    /// <summary>
    /// Adds an existing game folder, following PCL's <c>Add_Click</c> /
    /// <c>AddFolder</c> flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected directory need not contain a game yet. PCL checks access,
    /// looks for a child with versions, then keeps the selected directory when
    /// no existing game root is found.
    /// </para>
    /// <para>
    /// Uses the native Windows folder picker, then validates the selected directory
    /// and resolves a Minecraft root before adding it to the rail.
    /// </para>
    /// </remarks>
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_addingFolder) return;
        if (App.Services.GetRequiredService<TransferViewModel>().RemainingTaskCount > 0)
        {
            App.Services.GetRequiredService<ToastService>().ShowError("暂时无法添加文件夹", "请在下载或实例修改任务完成后再添加游戏文件夹。");
            return;
        }
        _addingFolder = true;
        try { await AddFolderAsync(); }
        catch (Exception ex)
        {
            Log.Append("添加文件夹失败：" + ex.Message, LogEntryKind.Error);
            App.Services.GetRequiredService<ToastService>().ShowError("添加文件夹失败", ex.Message);
        }
        finally { _addingFolder = false; }
    }

    private bool _addingFolder;

    private async Task AddFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            CommitButtonText = "选择文件夹",
        };
        picker.FileTypeFilter.Add("*");

        // The picker is a native Windows dialog. WinUI 3 unpackaged windows need
        // their owner HWND initialized explicitly so the dialog is correctly
        // parented and remains in front of this application.
        var window = App.MainWindow;
        if (window is null)
        {
            Log.Append("无法打开文件夹选择窗口：主窗口尚未就绪。", LogEntryKind.Error);
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            Win32.GetWindowHandle(window));

        Windows.Storage.StorageFolder? picked;
        try
        {
            picked = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            Log.Append($"打开文件夹选择窗口失败：{ex.Message}", LogEntryKind.Error);
            return;
        }

        if (picked is null)
        {
            return;
        }

        var path = picked.Path;

        // PCL rejects these characters because they break its settings serialisation.
        if (path.Contains('!') || path.Contains(';'))
        {
            throw new IOException("Minecraft 文件夹路径中不能含有感叹号或分号。");
        }

        if (path.Length == 0 || !Directory.Exists(path))
        {
            Log.Append($"文件夹不存在：{path}", LogEntryKind.Warning);
            return;
        }

        var defaultName = DefaultFolderName(path);
        var name = await AskFolderNameAsync(defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (App.Services.GetRequiredService<TransferViewModel>().RemainingTaskCount > 0)
            throw new InvalidOperationException("请在下载或实例修改任务完成后再添加游戏文件夹。");
        path = await Task.Run(() => GameFolderService.ResolveAddedFolder(path));

        var store = App.Services.GetRequiredService<WindowStateStore>();
        var existing = store.LoadGameFolders();
        var listed = ViewModel.Folders.FirstOrDefault(f => GameFolderService.PathsEqual(f.Path, path));

        // PCL renames an already-listed folder instead of adding a duplicate.
        var index = existing.FindIndex(f => GameFolderService.PathsEqual(f.Path, path));
        if (index >= 0)
        {
            existing[index] = existing[index] with { Name = name };
        }
        else
        {
            existing.Add(new GameFolderRecord(name, path, IsDefault: false, IsUserAdded: true));
        }

        store.SaveGameFolders(existing);
        App.Services.GetRequiredService<GameFolderService>().EnsureGameFolder(path);
        if (listed is null)
        {
            try { await Task.Run(() => ConfigureRootModpack(path)); }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "文件夹已添加，但未能自动识别根目录整合包：{Path}", path);
            }
        }

        // Select the newly added folder after the async rediscovery has rebuilt
        // the rail; selecting before then races the collection rebuild.
        await ViewModel.ReloadFoldersAsync();
        ViewModel.SelectedFolder =
            ViewModel.Folders.FirstOrDefault(f => GameFolderService.PathsEqual(f.Path, path))
            ?? ViewModel.SelectedFolder;
        UpdateSectionCounts();
        UpdateSectionVisuals();

        Log.Append($"已添加文件夹「{name}」：{path}");
        App.Services.GetRequiredService<ToastService>().Show(
            listed is null ? "文件夹已添加" : listed.Name == name ? "此文件夹已在列表中" : "文件夹名称已更新", name);
    }

    private static void ConfigureRootModpack(string path)
    {
        // PCL treats a small root-level pack as shared so its existing mods load.
        var mods = Path.Combine(path, "mods");
        var versions = Path.Combine(path, "versions");
        if (!Directory.Exists(mods) || Directory.EnumerateFiles(mods).Take(3).Count() < 3
            || Directory.EnumerateDirectories(versions).Take(4).Count() > 3) return;
        var scanner = App.Services.GetRequiredService<InstanceScanner>();
        var defaults = App.Services.GetRequiredService<SettingsStore>().Load();
        var settingsStore = App.Services.GetRequiredService<InstanceLaunchSettingsStore>();
        foreach (var instance in scanner.Scan(path).Where(i => i.IsModdable && i.Kind != InstanceKind.Error))
        {
            var isolatedMods = Path.Combine(instance.Path, "mods");
            if (Directory.Exists(isolatedMods) && Directory.EnumerateFiles(isolatedMods).Any()) return;
            var settings = settingsStore.Load(instance.Path, defaults);
            settings.UseGlobalLaunchOptions = false;
            settings.VersionIsolation = "关闭";
            settingsStore.Save(instance.Path, settings);
            Serilog.Log.Information("按根目录整合包规则关闭版本隔离：{Instance}", instance.Name);
        }
    }

    /// <summary>
    /// PCL's default display name: the folder's own name, or its parent's when the
    /// folder is called <c>.minecraft</c>, since ".minecraft" says nothing.
    /// </summary>
    private static string DefaultFolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        if (string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            name = parent is null ? name : Path.GetFileName(parent);
        }

        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(':'))
        {
            name = "Minecraft";
        }

        return name.Length > 40 ? name[..39] : name;
    }

    /// <summary>
    /// Asks for the display name, validating it the way PCL does.
    /// </summary>
    private async Task<string?> AskFolderNameAsync(string defaultName)
    {
        var input = new TextBox
        {
            Text = defaultName,
            Margin = new Thickness(0, 8, 0, 0),
            MaxLength = 30,
        };

        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "输入该文件夹在左侧列表中显示的名称。",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                },
                input,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "输入显示名称",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = input.Text.Trim();

        // ">" and "|" are PCL's separators in its folder setting; keeping them out
        // means a name can never be mistaken for a path when the file is read back.
        if (name.Length == 0 || name.Contains('>') || name.Contains('|'))
        {
            Log.Append("文件夹名称不能为空，且不能包含 > 或 |", LogEntryKind.Warning);
            return null;
        }

        return name;
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await ContentUi.PickFilesAsync("选择整合包", ".mrpack", ".zip");
            if (files.Length == 0) return;
            if (files.Length > 1) throw new InvalidOperationException("请一次选择一个整合包。");
            var root = ViewModel.FolderSelectionPath ?? ViewModel.Folders.FirstOrDefault()?.Path
                ?? throw new InvalidOperationException("请先选择游戏文件夹。");
            var name = await ContentUi.PromptAsync("导入整合包的实例名称", System.IO.Path.GetFileNameWithoutExtension(files[0]), XamlRoot);
            if (string.IsNullOrWhiteSpace(name)) return;
            ModrinthModpackInstaller.ValidateName(name);
            App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("导入整合包 · " + name, name, root,
                ["读取整合包", "安装游戏与资源", "整理实例"], (progress, token) =>
                    App.Services.GetRequiredService<LocalModpackService>().InstallAsync(files[0], root, name, progress, token),
                refreshInstances: true, returnToLaunchOnCompletion: true);
            App.Services.GetRequiredService<ToastService>().Show("开始导入整合包", "已加入任务队列，可在传输页查看进度。");
        }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法导入整合包", ex.Message); }
    }

    // ======== folder context menu ========

    /// <summary>The folder a context-menu item was raised on.</summary>
    private GameFolder? FolderOf(object sender)
        => (sender as FrameworkElement)?.DataContext as GameFolder ?? _contextFolder;

    /// <summary>
    /// Renames a user-added folder.
    /// </summary>
    /// <remarks>
    /// Only the display name changes; the folder on disk is untouched, which is
    /// exactly what PCL does - this is a label in the rail, not a filesystem
    /// rename that could break a user's setup.
    /// </remarks>
    private GameFolder? _contextFolder;

    private void FolderRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameFolder folder })
        {
            return;
        }

        _contextFolder = folder;
        ViewModel.SelectedFolder = folder;
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateFolderMenuItem("重命名", FolderRename_Click, folder.IsCustom, "\uE8AC"));
        flyout.Items.Add(CreateFolderMenuItem("刷新", FolderRefresh_Click, true, "\uE72C"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateFolderMenuItem("在资源管理器中打开", FolderOpen_Click, true, "\uE838"));
        flyout.Items.Add(CreateFolderMenuItem("从列表中移除", FolderRemove_Click, folder.IsCustom, "\uE74D"));
        flyout.ShowAt(sender as FrameworkElement, e.GetPosition(sender as UIElement));
        e.Handled = true;
    }

    private static MenuFlyoutItem CreateFolderMenuItem(
        string text,
        RoutedEventHandler handler,
        bool enabled,
        string glyph)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Icon = new FontIcon { Glyph = glyph };
        item.Click += handler;
        return item;
    }

    private async void FolderRename_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderOf(sender);
        if (folder is null || !folder.IsCustom)
        {
            return;
        }

        var name = await AskFolderNameAsync(folder.Name);
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name)
        {
            return;
        }

        var store = App.Services.GetRequiredService<WindowStateStore>();
        var existing = store.LoadGameFolders();
        var index = existing.FindIndex(f => GameFolderService.PathsEqual(f.Path, folder.Path));
        if (index < 0)
        {
            return;
        }

        existing[index] = existing[index] with { Name = name };
        store.SaveGameFolders(existing);

        await ViewModel.ReloadFoldersAsync();
        ViewModel.SelectedFolder =
            ViewModel.Folders.FirstOrDefault(f => GameFolderService.PathsEqual(f.Path, folder.Path))
            ?? ViewModel.SelectedFolder;
        UpdateSectionCounts();
        UpdateSectionVisuals();

        Log.Append($"文件夹已重命名为「{name}」");
    }

    /// <summary>Rescans just this folder and reselects it.</summary>
    private async void FolderRefresh_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderOf(sender);
        if (folder is null)
        {
            return;
        }

        await ViewModel.ReloadFoldersAsync();
        ViewModel.SelectedFolder =
            ViewModel.Folders.FirstOrDefault(f => GameFolderService.PathsEqual(f.Path, folder.Path))
            ?? ViewModel.SelectedFolder;
        UpdateSectionCounts();
        UpdateSectionVisuals();

        Log.Append($"已刷新文件夹：{folder.Path}");
    }

    /// <summary>Opens the folder in Explorer.</summary>
    private void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderOf(sender);
        if (folder is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder.Path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Append($"打开文件夹失败：{ex.Message}", LogEntryKind.Error);
        }
    }

    /// <summary>
    /// Removes a user-added folder from the rail.
    /// </summary>
    /// <remarks>
    /// Removes the entry only, never the files: the folder was on disk before the
    /// user added it here, and it stays there afterwards. Autodiscovered folders
    /// cannot be removed because they would reappear on the next scan; PCL makes
    /// the same distinction and offers "delete" only for folders it owns.
    /// </remarks>
    private async void FolderRemove_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderOf(sender);
        if (folder is null || !folder.IsCustom)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "从列表中移除",
            Content = $"确定要把「{folder.Name}」从列表中移除吗？\n\n文件夹：{folder.Path}\n\n磁盘上的文件不会被删除。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var store = App.Services.GetRequiredService<WindowStateStore>();
        var existing = store.LoadGameFolders();
        existing.RemoveAll(f => GameFolderService.PathsEqual(f.Path, folder.Path));
        store.SaveGameFolders(existing);

        await ViewModel.ReloadFoldersAsync();
        UpdateSectionCounts();
        UpdateSectionVisuals();

        Log.Append($"已从列表移除文件夹「{folder.Name}」（文件未被删除）");
    }

    /// <summary>
    /// Rescans the disk and rebuilds the section lists.
    /// </summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ReloadFoldersAsync();
        UpdateSectionCounts();
        UpdateSectionVisuals();
        Log.Append("已刷新实例列表");
    }
}
