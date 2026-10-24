using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Launcher.App.Views;

public sealed partial class DownloadInstallDialog
{
    private InstanceContext? _modificationContext;
    private InstanceInstallManifest? _originalManifest;
    private readonly Dictionary<string, LoaderVersionRow> _originalRows = new(StringComparer.OrdinalIgnoreCase);
    private bool _inlineModification;
    private bool _restoringModification;
    private bool _submittingModification;
    private int _modificationLoadId;
    public event Action? ModificationSubmitted;

    public void ReleaseModificationView()
    {
        _modificationLoadId++;
        _modificationContext = null;
        _originalManifest = null;
        _restoringModification = false;
    }

    private void ResetModificationMode()
    {
        ReleaseModificationView();
        _originalRows.Clear();
        _inlineModification = false;
        ModificationOptions.Visibility = Visibility.Collapsed;
        ModificationMinecraft.ItemsSource = null;
        ModificationComparisons.Children.Clear();
        ModificationStatus.Text = string.Empty;
        HeaderBackButton.Visibility = Visibility.Visible;
        InstanceNameBox.IsEnabled = ModificationMinecraft.IsEnabled = true;
        foreach (var loader in SupportedLoaders) GetLoaderControls(loader).Card.IsHitTestVisible = true;
        StartInstallLabel.Text = "开始下载"; StartInstallButton.IsEnabled = true;
        DialogOverlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0));
        DialogCard.MaxWidth = 860; DialogCard.MaxHeight = 560; DialogCard.Padding = new Thickness(20);
        DialogCard.BorderThickness = new Thickness(1);
    }

    public async Task LoadForModificationAsync(InstanceItem instance)
    {
        var content = App.Services.GetRequiredService<InstanceContentService>();
        var context = content.Context(instance);
        Load(instance.MinecraftVersionId, context.Root, "ms-appx:///Assets/Blocks/vanilla.png");
        _modificationContext = context; _inlineModification = true;
        var loadId = _modificationLoadId;
        DialogOverlay.Padding = new Thickness(0); DialogOverlay.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DialogCard.MaxWidth = double.PositiveInfinity; DialogCard.MaxHeight = double.PositiveInfinity;
        DialogCard.Padding = new Thickness(12); DialogCard.BorderThickness = new Thickness(0);
        ModificationOptions.Visibility = Visibility.Visible; StartInstallLabel.Text = "开始修改";
        HeaderBackButton.Visibility = Visibility.Collapsed;
        StartInstallButton.IsEnabled = false; ModificationStatus.Text = "正在读取已安装组件…";
        ModificationOriginalName.Text = "原名：" + instance.Name;
        RecoverModificationButton.Visibility = File.Exists(InstanceModificationService.JournalPath(instance.Path)) ? Visibility.Visible : Visibility.Collapsed;
        if (App.Services.GetRequiredService<TransferViewModel>().HasPendingModification(instance.Id))
        {
            ModificationStatus.Text = "此实例已有修改任务，请在传输页面查看进度。";
            return;
        }
        try
        {
            var manifest = await App.Services.GetRequiredService<InstanceModificationService>().ReadInstalledAsync(context, CancellationToken.None);
            if (loadId != _modificationLoadId) return;
            _originalManifest = manifest;
            await RestoreManifestAsync(manifest, true);
            if (loadId != _modificationLoadId) return;
            _restoringModification = true;
            try
            {
                var versions = await App.Services.GetRequiredService<Launcher.Core.Versions.MinecraftVersionService>().GetVersionsAsync();
                if (loadId != _modificationLoadId) return;
                ModificationMinecraft.ItemsSource = new[] { manifest.MinecraftVersion }.Concat(versions.Select(v => v.Id)).Distinct().ToArray();
                ModificationMinecraft.SelectedItem = manifest.MinecraftVersion;
            }
            catch (Exception ex)
            {
                if (loadId != _modificationLoadId) return;
                ModificationMinecraft.ItemsSource = new[] { manifest.MinecraftVersion };
                ModificationMinecraft.SelectedIndex = 0;
                ModificationStatus.Text = "Minecraft 列表暂时获取失败，可重开页面重试：" + ex.Message;
            }
            finally { if (loadId == _modificationLoadId) _restoringModification = false; }
            UpdateModificationComparison();
        }
        catch (Exception ex) { if (loadId == _modificationLoadId) ModificationStatus.Text = "无法还原已安装组件：" + ex.Message; }
    }

    private async Task RestoreManifestAsync(InstanceInstallManifest manifest, bool original)
    {
        var context = _modificationContext!; var generation = _modificationLoadId;
        _restoringModification = true;
        _version = manifest.MinecraftVersion; _versionCache.Clear(); _selectedComponents.Clear();
        _selectedLoader = manifest.Loader; _selectedLoaderVersion = manifest.LoaderVersion;
        _fabricApiAutoSelectAllowed = _optiFabricAutoSelectAllowed = false;
        _fabricApiAutoSelectTask = _optiFabricAutoSelectTask = null;
        CollapseLoaderPanels();
        try
        {
            var values = new Dictionary<string, string?>
            {
                [manifest.Loader ?? ""] = manifest.LoaderVersion, ["LiteLoader"] = manifest.LiteLoaderVersion,
                ["OptiFine"] = manifest.OptiFineVersion, ["Fabric API"] = manifest.FabricApiVersion, ["OptiFabric"] = manifest.OptiFabricVersion,
            };
            foreach (var pair in values.Where(p => p.Key.Length > 0 && p.Value is not null))
            {
                var icon = new BitmapImage(new Uri(GetLoaderIcon(pair.Key)));
                LoaderVersionRow row;
                try
                {
                    var versions = await App.Services.GetRequiredService<MinecraftInstallService>().GetLoaderVersionsAsync(pair.Key, _version);
                    if (generation != _modificationLoadId) return;
                    var rows = versions.Select(v => new LoaderVersionRow(pair.Key, v.Version, v.Detail, icon, v.IsStable, v.IsRecommended, Id: v.Id, Compatibility: v.Compatibility)).ToArray();
                    _versionCache[pair.Key] = rows;
                    row = rows.FirstOrDefault(v => v.Id == pair.Value || v.Version == pair.Value)
                        ?? rows.FirstOrDefault(v => pair.Key == "OptiFine" && v.Version.Replace(' ', '_').EndsWith(pair.Value!.Replace("HD_U_", "").Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
                        ?? new(pair.Key, pair.Value!, "当前已安装，远程列表中未找到", icon, false, Id: pair.Value);
                }
                catch { row = new(pair.Key, pair.Value!, "当前已安装，可保留或移除", icon, false, Id: pair.Value); }
                if (generation != _modificationLoadId) return;
                if (original) _originalRows[pair.Key] = row with { Id = pair.Value };
                if (pair.Key is not ("Forge" or "NeoForge" or "Fabric")) _selectedComponents[pair.Key] = row with { Id = pair.Value };
            }
            _instanceNameWasEdited = false; UpdateSuggestedInstanceName();
            _instanceNameWasEdited = InstanceNameBox.Text != context.Instance.Name;
            _isUpdatingInstanceName = true; InstanceNameBox.Text = context.Instance.Name; _isUpdatingInstanceName = false;
            UpdateInstanceIcon(); UpdateAdditionalPickerVisibility(); UpdateLoaderHeaderStates();
            foreach (var pair in _versionCache) PopulateLoaderVersions(pair.Key, pair.Value);
            ModificationStatus.Text = "选择需要调整的组件，修改前会显示差异。";
        }
        finally { if (generation == _modificationLoadId) _restoringModification = false; }
    }

    private string? InstallId(string key)
    {
        if (!_selectedComponents.TryGetValue(key, out var row)) return null;
        return _originalManifest?.MinecraftVersion == _version && _originalRows.TryGetValue(key, out var original) && original.Version == row.Version
            ? original.Id ?? original.Version : row.Id ?? row.Version;
    }

    private InstallRequest ModificationRequest() => new(_version, InstanceNameBox.Text.Trim(), _gameDirectory,
        _selectedLoader, _selectedLoaderVersion, InstallId("LiteLoader"), InstallId("OptiFine"), InstallId("Fabric API"), InstallId("OptiFabric"));

    private void UpdateModificationComparison()
    {
        if (_modificationContext is null || _originalManifest is null || _restoringModification || ModificationComparisons is null) return;
        var request = ModificationRequest();
        ModificationComparisons.Children.Clear();
        var values = new List<(string Name, string? Old, string? New)>
        {
            ("Minecraft", _originalManifest.MinecraftVersion, _version),
            ("加载器", _originalManifest.Loader is null ? null : _originalManifest.Loader + " " + _originalManifest.LoaderVersion, _selectedLoader is null ? null : _selectedLoader + " " + _selectedLoaderVersion),
        };
        foreach (var key in new[] { "LiteLoader", "OptiFine", "Fabric API", "OptiFabric" })
            values.Add((key, _originalRows.GetValueOrDefault(key)?.Version, _selectedComponents.GetValueOrDefault(key)?.Version));
        foreach (var value in values.Where(v => v.Old != v.New))
        {
            var panel = new Grid { ColumnSpacing = 8 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.Children.Add(new TextBlock { Text = $"{value.Name}：{value.Old ?? "无"} → {value.New ?? "无"}", TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var restore = new Button { Content = "恢复", Padding = new Thickness(8, 3, 8, 3), Tag = value.Name };
            restore.Click += RestoreComponent_Click; Grid.SetColumn(restore, 1); panel.Children.Add(restore);
            ModificationComparisons.Children.Add(panel);
        }
        var changed = ModificationComparisons.Children.Count > 0 || request.Name != _modificationContext.Instance.Name;
        StartInstallButton.IsEnabled = changed && !string.IsNullOrWhiteSpace(request.Name)
            && !_submittingModification
            && !App.Services.GetRequiredService<TransferViewModel>().HasPendingModification(_modificationContext.Instance.Id)
            && !File.Exists(InstanceModificationService.JournalPath(_modificationContext.Instance.Path));
    }

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_originalManifest is null) return;
        var generation = _modificationLoadId;
        await RestoreManifestAsync(_originalManifest, false);
        if (generation != _modificationLoadId || _originalManifest is null) return;
        _restoringModification = true; ModificationMinecraft.SelectedItem = _originalManifest.MinecraftVersion; _restoringModification = false;
        UpdateModificationComparison();
    }

    private async void RestoreComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || _originalManifest is null) return;
        if (key is "Minecraft" or "加载器") { RestoreOriginal_Click(sender, e); return; }
        _selectedComponents.Remove(key);
        if (_originalRows.TryGetValue(key, out var row))
        {
            if (key is "Fabric API" or "OptiFabric" && _selectedLoader != "Fabric")
            { ModificationStatus.Text = "请先恢复 Fabric，再恢复此组件。"; return; }
            _selectedComponents[key] = row;
        }
        UpdateAdditionalPickerVisibility(); UpdateSuggestedInstanceName(); UpdateInstanceIcon(); UpdateModificationComparison();
        await Task.CompletedTask;
    }

    private async void ModificationMinecraft_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringModification || _modificationContext is null || ModificationMinecraft.SelectedItem is not string version || version == _version) return;
        var generation = ++_modificationLoadId;
        var selected = new Dictionary<string, string>();
        if (_selectedLoader is not null) selected[_selectedLoader] = _selectedLoaderVersion!;
        foreach (var pair in _selectedComponents) selected[pair.Key] = pair.Value.Id ?? pair.Value.Version;
        _version = version; _versionCache.Clear(); _selectedComponents.Clear(); _selectedLoader = _selectedLoaderVersion = null;
        _fabricApiAutoSelectAllowed = _optiFabricAutoSelectAllowed = false; _fabricApiAutoSelectTask = _optiFabricAutoSelectTask = null;
        CollapseLoaderPanels(); StartInstallButton.IsEnabled = false; ModificationStatus.Text = "正在查找兼容组件…";
        foreach (var pair in selected)
        {
            try
            {
                var versions = await App.Services.GetRequiredService<MinecraftInstallService>().GetLoaderVersionsAsync(pair.Key, version);
                if (generation != _modificationLoadId) return;
                var found = versions.FirstOrDefault(v => v.Version == pair.Value || v.Id == pair.Value) ?? versions.FirstOrDefault(v => v.IsStable) ?? versions.FirstOrDefault();
                if (found is null || GetLoaderBlockReason(pair.Key) is not null) continue;
                var row = new LoaderVersionRow(pair.Key, found.Version, found.Detail, new BitmapImage(new Uri(GetLoaderIcon(pair.Key))), found.IsStable, found.IsRecommended, Id: found.Id, Compatibility: found.Compatibility);
                if (pair.Key is "Forge" or "NeoForge" or "Fabric") { _selectedLoader = pair.Key; _selectedLoaderVersion = row.Version; }
                else if (pair.Key is not ("Fabric API" or "OptiFabric") || _selectedLoader == "Fabric") _selectedComponents[pair.Key] = row;
            }
            catch (Exception ex) { if (generation != _modificationLoadId) return; ModificationStatus.Text = pair.Key + " 列表获取失败：" + ex.Message; }
        }
        if (generation != _modificationLoadId) return;
        UpdateAdditionalPickerVisibility(); UpdateSuggestedInstanceName(); UpdateInstanceIcon(); UpdateModificationComparison();
        ModificationStatus.Text = "已尝试保留原组件；无兼容版本的组件未选择，请检查修改差异。";
    }

    private async Task StartModificationAsync()
    {
        if (_submittingModification || _modificationContext is not { } context || _originalManifest is null) return;
        var generation = _modificationLoadId;
        var request = ModificationRequest();
        _submittingModification = true;
        StartInstallButton.IsEnabled = false;
        try
        {
            ModrinthModpackInstaller.ValidateName(request.Name);
            App.Services.GetRequiredService<InstanceContentService>().EnsureWritable(context);
            var warnings = "将按以下选择修改实例，提交前保留备份。\n\n" + string.Join("\n", ModificationComparisons.Children.OfType<Grid>().Select(g => ((TextBlock)g.Children[0]).Text));
            if (request.Name != context.Instance.Name) warnings += $"\n名称：{context.Instance.Name} → {request.Name}";
            if (context.Shared) warnings += "\n\n此实例使用共享游戏目录，Mod 与存档可能与其他实例共用。修改不会自动迁移这些文件。";
            if (_originalManifest?.MinecraftVersion != request.Version || _originalManifest?.Loader != request.Loader || _originalManifest?.LoaderVersion != request.LoaderVersion)
                warnings += "\n\n已有 Mod、资源包和存档可能不兼容修改后的版本。";
            if (!await ContentUi.ConfirmAsync("确认修改实例", warnings, "开始修改", XamlRoot)) return;
            if (generation != _modificationLoadId) return;
            var service = App.Services.GetRequiredService<InstanceModificationService>();
            App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("修改实例 · " + context.Instance.Name, request.Name, context.Root,
                ["准备新版本", "提交修改"], (progress, token) => service.ModifyAsync(context, request, progress, token), true,
                sourceInstanceId: context.Instance.Id, returnToLaunchOnCompletion: true);
            ModificationSubmitted?.Invoke();
        }
        catch (Exception ex) { if (generation == _modificationLoadId) ModificationStatus.Text = ex.Message; }
        finally { _submittingModification = false; if (generation == _modificationLoadId) UpdateModificationComparison(); }
    }

    private async void RecoverModification_Click(object sender, RoutedEventArgs e)
    {
        if (_modificationContext is not { } context) return;
        var generation = _modificationLoadId;
        if (!await ContentUi.ConfirmAsync("恢复修改前的文件", "此操作会还原未完成修改涉及的文件。是否从备份恢复？", "恢复备份", XamlRoot) || generation != _modificationLoadId) return;
        try
        {
            await App.Services.GetRequiredService<InstanceModificationService>().RecoverAsync(context, CancellationToken.None);
            if (generation == _modificationLoadId) await LoadForModificationAsync(context.Instance);
        }
        catch (Exception ex) { if (generation == _modificationLoadId) ModificationStatus.Text = "恢复失败：" + ex.Message; }
    }
}
