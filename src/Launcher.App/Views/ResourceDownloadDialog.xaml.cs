using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Launcher.App.Views;

public sealed record ResourceInstallChoice(ResourceEntry Project, ModrinthVersion Version, string InstanceName, bool SaveAs);

public sealed partial class ResourceDownloadDialog : UserControl
{
    private readonly CommunityResourceService _service = App.Services.GetRequiredService<CommunityResourceService>();
    private readonly Dictionary<string, ModrinthProject> _dependencyProjects = [];
    private readonly List<(Button Button, ModrinthVersion Version, FontIcon Mark, TextBlock State)> _rows = [];
    private IReadOnlyList<InstalledProjectFile> _installed = [];
    private InstanceContext? _instanceContext;
    private readonly List<(StackPanel Host, IReadOnlyList<ModrinthVersion> Versions)> _dependencyHosts = [];
    private CancellationTokenSource? _lifetime;
    private ResourceEntry? _project;
    private ModrinthVersion? _selected;
    private IReadOnlyList<ModrinthVersion> _versions = [];
    private DownloadSection _section;
    private string _targetVersion = "";
    private string _targetLoader = "";
    private string? _preferredVersion;
    private string _generatedPackName = "";
    private bool _canInstall;
    private bool _updating;
    private bool _restoreScroll;
    private double _savedOffset;
    private Button? _lastDependency;
    private Action? _close;
    private Action? _back;
    private Func<ModrinthProject, string?, Task>? _openDependency;
    private Func<ResourceInstallChoice, CancellationToken, Task<bool>>? _install;

    public ResourceDownloadDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            App.Services.GetRequiredService<InstanceContentService>().Changed += InstalledContent_Changed;
            if (_restoreScroll && _lifetime is not null) await RefreshInstalledAsync(_lifetime.Token);
            if (!_restoreScroll) { CloseButton.Focus(FocusState.Programmatic); return; }
            _restoreScroll = false;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded) return;
                _lastDependency?.Focus(FocusState.Programmatic);
                ContentScroll.ChangeView(null, _savedOffset, null, true);
            });
        };
        Unloaded += (_, _) => App.Services.GetRequiredService<InstanceContentService>().Changed -= InstalledContent_Changed;
    }

    public void SetTargetInstance(InstanceItem? instance) => _instanceContext = instance is null ? null
        : App.Services.GetRequiredService<InstanceContentService>().Context(instance);

    private void InstalledContent_Changed(string path)
    {
        if (_instanceContext is not null && GameFolderService.PathsEqual(path, _instanceContext.GameDirectory))
            DispatcherQueue.TryEnqueue(async () => { if (_lifetime is not null) await RefreshInstalledAsync(_lifetime.Token); });
    }

    private async Task RefreshInstalledAsync(CancellationToken token)
    {
        if (_instanceContext is null || _section != DownloadSection.Mod || _project is null) return;
        try
        {
            var installed = await App.Services.GetRequiredService<ModInstallService>().FindInstalledAsync(_instanceContext, _project.Project, _versions, token);
            token.ThrowIfCancellationRequested();
            _installed = installed;
            UpdateSelection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CompatibilityHint.Text = "无法读取本地安装状态：" + ex.Message; CompatibilityHint.Visibility = Visibility.Visible; }
    }

    public void SetHost(Action close, Func<ResourceInstallChoice, CancellationToken, Task<bool>> install,
        Func<ModrinthProject, string?, Task> openDependency, Action? back = null)
    {
        _close = close; _install = install; _openDependency = openDependency; _back = back;
        BackButton.Visibility = back is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SuspendForNavigation() => _savedOffset = ContentScroll.VerticalOffset;
    public void ResumeFromNavigation() => _restoreScroll = true;

    public async Task LoadAsync(ResourceEntry project, DownloadSection section, string gameVersion,
        string loader, string target, bool canInstall, string? preferredVersion = null)
    {
        Cancel();
        _lifetime = new();
        _project = project; _section = section; _targetVersion = gameVersion; _targetLoader = loader;
        _preferredVersion = preferredVersion; _canInstall = canInstall; _selected = null; _versions = [];
        _installed = [];
        _restoreScroll = false; _lastDependency = null;
        _dependencyProjects.Clear(); _rows.Clear(); _dependencyHosts.Clear();
        ProjectName.Text = project.Name;
        ToolTipService.SetToolTip(ProjectName, project.Name);
        ProjectDescription.Text = project.Summary;
        ToolTipService.SetToolTip(ProjectDescription, project.Summary);
        UpdateProjectMetadata();
        ProjectIcon.Source = Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var icon) ? new BitmapImage(icon) : null;
        PackNameBox.Text = string.Concat(project.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        _generatedPackName = PackNameBox.Text;
        PackNamePanel.Visibility = section == DownloadSection.Modpack ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Content = "安装";
        TargetText.Text = target;
        ToolTipService.SetToolTip(TargetText, target);
        SelectedVersionText.Text = "正在获取版本列表";
        ContentScroll.ChangeView(null, 0, null, true);
        await FetchVersionsAsync();
    }

    public void Cancel() { _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; }

    private void UpdateProjectMetadata()
    {
        if (_project is null) return;
        var metadata = $"{_project.Source} · 下载 {FormatCount(_project.DownloadCount)} 次";
        if (_project.Updated != default) metadata += " · 更新于 " + Age(_project.Updated);
        else if (_versions.Count > 0) metadata += " · 最近发布 " + Age(_versions.Max(v => v.Published));
        ProjectSource.Text = metadata;
        ToolTipService.SetToolTip(ProjectSource, metadata + " · " + TypeName(_section));
    }

    private async Task FetchVersionsAsync()
    {
        if (_project is null || _lifetime is null) return;
        var token = _lifetime.Token;
        LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false; RetryButton.Visibility = Visibility.Collapsed;
        VersionGroups.Children.Clear();
        InstallButton.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            var versions = await _service.GetVersionsAsync(_project.Project, token);
            token.ThrowIfCancellationRequested();
            _versions = versions.Where(version => _project.Source == "CurseForge" || _section switch
            {
                DownloadSection.DataPack => version.Loaders.Contains("datapack"),
                DownloadSection.Mod => !version.Loaders.All(loader => loader is "datapack" or "minecraft"),
                _ => true,
            }).ToArray();
            _updating = true;
            var games = ResourceVersionSelection.SortGames(_versions.SelectMany(v => v.GameVersions).Prepend(_targetVersion)
                .Where(v => v.Length > 0).Select(ResourceVersionSelection.GameFilterName));
            GameVersionFilters.ItemsSource = new[] { "全部" }.Concat(games).ToArray();
            GameVersionFilters.SelectedIndex = 0;
            var loaders = _versions.SelectMany(v => v.Loaders).Where(l => l is not ("minecraft" or "datapack"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(ResourceVersionSelection.LoaderName).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToArray();
            LoaderFilters.ItemsSource = new[] { "全部" }.Concat(loaders).ToArray();
            LoaderFilters.SelectedIndex = 0;
            LoaderFilters.Visibility = LoaderFilterLabel.Visibility = loaders.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _updating = false;
            UpdateProjectMetadata();
            await RefreshInstalledAsync(token);
            token.ThrowIfCancellationRequested();
            BuildVersionGroups();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            ErrorBar.Message = "无法获取版本列表：" + ex.Message;
            ErrorBar.IsOpen = true; RetryButton.Visibility = Visibility.Visible;
            SelectedVersionText.Text = "版本获取失败";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _updating = false; LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void BuildVersionGroups()
    {
        if (_updating || _project is null) return;
        var game = GameVersionFilters.SelectedItem as string ?? "全部";
        var loader = LoaderFilters.SelectedItem as string ?? "全部";
        var groups = ResourceVersionSelection.Group(_versions, _targetVersion, _targetLoader,
            game == "全部" ? null : game, loader == "全部" ? null : loader.ToLowerInvariant(),
            StableOnlyBox.IsChecked == true, _section == DownloadSection.Modpack);
        var filtered = groups.SelectMany(group => group.Versions).DistinctBy(version => version.Id).ToArray();
        _selected = filtered.FirstOrDefault(v => v.Id == (_selected?.Id ?? _preferredVersion))
            ?? filtered.FirstOrDefault(v => IsCompatible(v) && v.Type == "release")
            ?? filtered.FirstOrDefault(IsCompatible) ?? filtered.FirstOrDefault();
        VersionGroups.Children.Clear(); _rows.Clear(); _dependencyHosts.Clear();
        var targetTitle = string.IsNullOrEmpty(_targetLoader) || _targetLoader == "datapack" ? _targetVersion
            : $"{ResourceVersionSelection.LoaderName(_targetLoader)} {_targetVersion}";
        foreach (var group in groups) AddGroup(group, group.Title == targetTitle && _section != DownloadSection.Modpack);
        if (groups.Count == 0) VersionGroups.Children.Add(new TextBlock
        { Text = "没有匹配的发布版本，请调整筛选条件。", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        UpdateSelection();
    }

    private Expander MakeExpander(object header, object content, bool expanded, bool nested = false)
    {
        var expander = new Expander
        {
            Header = header, Content = content, IsExpanded = expanded, MinHeight = nested ? 32 : 40,
            Padding = new Thickness(nested ? 2 : 6), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderBrush = LocalBrush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(nested ? 0 : 1),
        };
        foreach (var key in new[] { "ExpanderChevronBackground", "ExpanderChevronPointerOverBackground", "ExpanderChevronPressedBackground" })
            expander.Resources[key] = Transparent();
        if (nested)
        {
            expander.Resources["ExpanderHeaderBackground"] = Transparent();
            expander.Resources["ExpanderHeaderBorderBrush"] = Transparent();
            expander.Resources["ExpanderHeaderBorderPointerOverBrush"] = Transparent();
            expander.Resources["ExpanderHeaderBorderPressedBrush"] = Transparent();
            expander.Background = Transparent();
        }
        return expander;
    }

    private void AddGroup(ResourceVersionGroup group, bool target)
    {
        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = new TextBlock { Text = (target ? "所选版本：" : "") + group.Title + $"（{group.Versions.Count}）", FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (target) header.Foreground = LocalBrush("AccentTextFillColorPrimaryBrush");
        var expander = MakeExpander(header, stack, group.IsExpanded);
        void Populate()
        {
            if (stack.Children.Count > 0) return;
            if (group.Versions.Count == 0)
            {
                stack.Children.Add(new TextBlock { Text = StableOnlyBox.IsChecked == true
                    ? "暂无匹配的正式版本，可取消“仅正式版”查看。" : "暂无适合此实例的版本。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
                return;
            }
            var dependencies = new StackPanel { Spacing = 4 };
            stack.Children.Add(dependencies); _dependencyHosts.Add((dependencies, group.Versions));
            stack.Children.Add(new TextBlock { Text = "版本列表", FontSize = 12, Margin = new Thickness(8, 4, 8, 2), Foreground = LocalBrush("TextFillColorSecondaryBrush") });
            foreach (var version in group.Versions) stack.Children.Add(CreateVersionRow(version));
            UpdateSelection();
        }
        expander.Expanding += (_, _) => Populate();
        VersionGroups.Children.Add(expander);
        if (group.IsExpanded) Populate();
    }

    private Button CreateVersionRow(ModrinthVersion version)
    {
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        content.Children.Add(ReleaseIcon(version.Type));
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labels, 1);
        labels.Children.Add(new TextBlock { Text = version.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = new List<string> { version.PrimaryFile?.Name ?? version.Number };
        if (version.Downloads is { } count) details.Add("下载 " + FormatCount(count) + " 次");
        if (version.Published != default) details.Add("更新于 " + Age(version.Published));
        details.Add(version.Type switch { "release" => "正式版", "beta" => "测试版", _ => "预览版" });
        var description = string.Join(" · ", details);
        labels.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = LocalBrush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var state = new TextBlock { FontSize = 12, Foreground = LocalBrush("AccentTextFillColorPrimaryBrush"), Visibility = Visibility.Collapsed };
        labels.Children.Add(state);
        content.Children.Add(labels);
        var mark = new FontIcon { Glyph = "\uE73E", FontSize = 13, Foreground = LocalBrush("AccentTextFillColorPrimaryBrush"), Visibility = Visibility.Collapsed };
        Grid.SetColumn(mark, 2); content.Children.Add(mark);
        var row = new Button { Content = content, Style = (Style)Resources["ResourceRowButtonStyle"] };
        ToolTipService.SetToolTip(row, version.Name + "\n" + description + (version.PrimaryFile is { } file ? "\n" + FormatSize(file.Size) : "\n需前往项目主页下载"));
        row.Click += (_, _) => { _selected = version; UpdateSelection(); };
        _rows.Add((row, version, mark, state));
        return row;
    }

    private static Grid ReleaseIcon(string type)
    {
        var (letter, color) = type switch
        {
            "release" => ("R", Windows.UI.Color.FromArgb(255, 32, 151, 175)),
            "beta" => ("B", Windows.UI.Color.FromArgb(255, 100, 158, 56)),
            _ => ("A", Windows.UI.Color.FromArgb(255, 204, 130, 31)),
        };
        var icon = new Grid { Width = 30, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        Polygon Face(Point[] points, double opacity)
        {
            var face = new Polygon { Fill = new SolidColorBrush(color), Opacity = opacity };
            foreach (var point in points) face.Points.Add(point);
            return face;
        }
        icon.Children.Add(Face([new(15, 0), new(30, 8), new(15, 16), new(0, 8)], .65));
        icon.Children.Add(Face([new(0, 8), new(15, 16), new(15, 34), new(0, 26)], 1));
        icon.Children.Add(Face([new(15, 16), new(30, 8), new(30, 26), new(15, 34)], .82));
        icon.Children.Add(new TextBlock { Text = letter, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) });
        return icon;
    }

    private bool IsCompatible(ModrinthVersion version) => _section == DownloadSection.Modpack
        || ResourceVersionSelection.IsCompatible(version, _targetVersion,
            _project?.Source == "CurseForge" && _section != DownloadSection.Mod ? "" : _targetLoader);

    private void UpdateSelection()
    {
        if (_section == DownloadSection.Modpack && _selected is not null && _project is not null && PackNameBox.Text == _generatedPackName)
        {
            _generatedPackName = string.Concat($"{_project.Name}-{_selected.Number}".Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
            PackNameBox.Text = _generatedPackName;
        }
        foreach (var (row, version, mark, state) in _rows)
        {
            var installedFiles = _installed.Where(f => f.Version?.Id == version.Id).ToArray();
            state.Text = installedFiles.Length > 0 ? "已安装" + (installedFiles.All(f => !f.File.Enabled) ? " · 已禁用" : "") : "";
            state.Visibility = installedFiles.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var selected = version.Id == _selected?.Id;
            row.Background = selected ? LocalBrush("ResourceRowSelectedBrush") : Transparent();
            mark.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(row, selected ? "已选择" : "选择此发布版本");
        }
        SelectedVersionText.Text = _selected is null ? "请选择发布版本" : _selected.Number;
        SaveButton.IsEnabled = _selected?.PrimaryFile is not null;
        var alreadyInstalled = _selected is not null && _installed.Any(f => f.Version?.Id == _selected.Id);
        InstallButton.Content = "安装";
        InstallButton.IsEnabled = !alreadyInstalled && _selected?.PrimaryFile is not null && _canInstall && IsCompatible(_selected);
        CompatibilityHint.Text = _selected?.PrimaryFile is null && _selected is not null ? "请前往项目主页下载此版本"
            : _selected is not null && _canInstall && !IsCompatible(_selected) ? "与目标实例不兼容，仅可另存为" : "";
        if (alreadyInstalled) CompatibilityHint.Text = _installed.Any(f => f.Version?.Id == _selected!.Id && !f.File.Enabled)
            ? "此版本已安装但已禁用，请在实例的 Mod 页面启用。" : "此版本已安装，无需重复下载。";
        CompatibilityHint.Visibility = CompatibilityHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(SelectedVersionText, _selected?.Name ?? "请选择发布版本");
        foreach (var (host, versions) in _dependencyHosts)
        {
            var version = versions.FirstOrDefault(v => v.Id == _selected?.Id) ?? versions.FirstOrDefault(v => v.Type == "release") ?? versions[0];
            if (host.Tag as string == version.Id) continue;
            host.Tag = version.Id;
            _ = LoadDependenciesAsync(host, version);
        }
    }

    private async Task LoadDependenciesAsync(StackPanel host, ModrinthVersion selected)
    {
        host.Children.Clear();
        if (_section == DownloadSection.Modpack || _lifetime is null || _project is null) return;
        var token = _lifetime.Token;
        var source = _project.Source;
        var dependencies = selected.Dependencies.Where(d => d.Type == "required")
            .DistinctBy(d => string.IsNullOrEmpty(d.ProjectId) ? d.VersionId : d.ProjectId).ToArray();
        if (dependencies.Length == 0) return;
        var rows = new StackPanel { Spacing = 2 };
        var section = MakeExpander($"必选前置资源（{dependencies.Length}）", rows, DialogCard.ActualHeight >= 600, true);
        host.Children.Add(section);
        foreach (var dependency in dependencies)
        {
            var key = !string.IsNullOrEmpty(dependency.ProjectId) ? dependency.ProjectId : dependency.VersionId;
            if (string.IsNullOrEmpty(key)) continue;
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var image = new Image { Width = 34, Height = 34, Stretch = Stretch.Uniform };
            content.Children.Add(image);
            var labels = new StackPanel { Spacing = 3 };
            Grid.SetColumn(labels, 1);
            var name = new TextBlock { Text = "正在获取前置信息…", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = "必选 · 安装时自动检查并补齐", FontSize = 11,
                Foreground = LocalBrush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            labels.Children.Add(name); labels.Children.Add(detail); content.Children.Add(labels);
            var arrow = new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = LocalBrush("TextFillColorSecondaryBrush") };
            Grid.SetColumn(arrow, 2); content.Children.Add(arrow);
            var row = new Button { Content = content, Style = (Style)Resources["ResourceRowButtonStyle"], IsEnabled = false };
            rows.Children.Add(row);
            ModrinthProject? resolvedProject = null;
            async Task FetchProject()
            {
                try
                {
                    var cacheKey = source + ":" + key;
                    if (!_dependencyProjects.TryGetValue(cacheKey, out var project))
                    {
                        project = await _service.GetDependencyProjectAsync(source, dependency, token);
                        if (token.IsCancellationRequested || host.Tag as string != selected.Id) return;
                        _dependencyProjects[cacheKey] = project;
                    }
                    if (token.IsCancellationRequested || host.Tag as string != selected.Id) return;
                    name.Text = project.Title; detail.Text = project.Description;
                    resolvedProject = project;
                    if (Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var icon)) image.Source = new BitmapImage(icon);
                    row.IsEnabled = true;
                    ToolTipService.SetToolTip(row, project.Title + " · 查看详情，必选前置将自动安装");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch
                {
                    if (token.IsCancellationRequested || host.Tag as string != selected.Id) return;
                    name.Text = "前置资源 " + key; detail.Text = "信息获取失败，点击重试；安装时仍会检查";
                    row.IsEnabled = true;
                }
            }
            row.Click += async (_, _) =>
            {
                if (token.IsCancellationRequested) return;
                if (resolvedProject is null) { row.IsEnabled = false; await FetchProject(); return; }
                if (_openDependency is null) return;
                _lastDependency = row;
                try { await _openDependency(resolvedProject, dependency.VersionId); }
                catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法打开前置资源", ex.Message); }
            };
            await FetchProject();
        }
    }

    private async Task SubmitAsync(bool saveAs)
    {
        if (_project is null || _selected is null || _install is null || _lifetime is null) return;
        InstallButton.IsEnabled = SaveButton.IsEnabled = false;
        var token = _lifetime.Token;
        try
        {
            if (await _install(new(_project, _selected, PackNameBox.Text.Trim(), saveAs), token)) (_back ?? _close)?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法开始下载", ex.Message); }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                InstallButton.Content = "安装";
                UpdateSelection();
            }
        }
    }

    private Brush LocalBrush(string name)
    {
        if (Resources.TryGetValue(name, out var value)) return (Brush)value;
        if (Resources.ThemeDictionaries.TryGetValue(ActualTheme == ElementTheme.Dark ? "Default" : "Light", out var theme)
            && theme is ResourceDictionary dictionary && dictionary.TryGetValue(name, out value)) return (Brush)value;
        return (Brush)Application.Current.Resources[name];
    }
    private static SolidColorBrush Transparent() => new(Microsoft.UI.Colors.Transparent);
    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DialogCard.Width = Math.Max(0, Math.Min(900, e.NewSize.Width - 32));
        DialogCard.Height = Math.Max(0, Math.Min(700, e.NewSize.Height - 32));
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_updating) BuildVersionGroups(); }
    private void StableOnly_Changed(object sender, RoutedEventArgs e) { if (IsLoaded && !_updating) BuildVersionGroups(); }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await FetchVersionsAsync();
    private async void Install_Click(object sender, RoutedEventArgs e) => await SubmitAsync(false);
    private async void Save_Click(object sender, RoutedEventArgs e) => await SubmitAsync(true);
    private void Close_Click(object sender, RoutedEventArgs e) => _close?.Invoke();
    private void Back_Click(object sender, RoutedEventArgs e) => _back?.Invoke();
    private void Dialog_KeyDown(object sender, KeyRoutedEventArgs e)
    { if (e.Key == Windows.System.VirtualKey.Escape) { (_back ?? _close)?.Invoke(); e.Handled = true; } }
    private async void ProjectWebsite_Click(object sender, RoutedEventArgs e)
    { if (_project is not null) await Windows.System.Launcher.LaunchUriAsync(new Uri(CommunityResourceService.Website(_project.Project))); }
    private void CopyName_Click(object sender, RoutedEventArgs e)
    { var data = new DataPackage(); data.SetText(_project?.Name ?? ""); Clipboard.SetContent(data); }
    private void CopyLink_Click(object sender, RoutedEventArgs e)
    { if (_project is null) return; var data = new DataPackage(); data.SetText(CommunityResourceService.Website(_project.Project)); Clipboard.SetContent(data); }
    private static string TypeName(DownloadSection section) => section switch
    { DownloadSection.Modpack => "整合包", DownloadSection.Shader => "光影包", DownloadSection.ResourcePack => "资源包", DownloadSection.DataPack => "数据包", _ => "Mod" };
    private static string FormatSize(long size) => size >= 1048576 ? $"{size / 1048576d:0.#} MB" : $"{Math.Max(1, size / 1024d):0.#} KB";
    private static string FormatCount(long count) => count >= 100_000_000 ? $"{count / 100_000_000d:0.#} 亿" : count >= 10_000 ? $"{count / 10000d:0.#} 万" : count.ToString("N0");
    private static string Age(DateTimeOffset date)
    {
        var age = DateTimeOffset.Now - date;
        return age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? $"{Math.Floor(age.TotalMinutes):0} 分钟前"
            : age.TotalDays < 1 ? $"{Math.Floor(age.TotalHours):0} 小时前" : age.TotalDays < 31 ? $"{Math.Floor(age.TotalDays):0} 天前" : date.LocalDateTime.ToString("yyyy-MM-dd");
    }
}
