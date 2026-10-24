using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Install;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Launcher.App.Views;

/// <summary>Install configuration opened from a Minecraft version card.</summary>
public sealed partial class DownloadInstallDialog : UserControl
{
    private static readonly string[] SupportedLoaders = ["Forge", "NeoForge", "Fabric", "LiteLoader", "OptiFine", "Fabric API", "OptiFabric"];
    private readonly Dictionary<string, IReadOnlyList<LoaderVersionRow>> _versionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoaderVersionRow> _selectedComponents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoaderControls> _additionalControls = new(StringComparer.OrdinalIgnoreCase);
    private string _version = string.Empty;
    private string _gameDirectory = string.Empty;
    private string _vanillaIconPath = "ms-appx:///Assets/Blocks/vanilla.png";
    private string? _selectedLoader;
    private string? _selectedLoaderVersion;
    private string? _expandedLoader;
    private bool _isUpdatingInstanceName;
    private bool _instanceNameWasEdited;
    private bool _fabricApiAutoSelectAllowed;
    private bool _optiFabricAutoSelectAllowed;
    private Task? _fabricApiAutoSelectTask;
    private Task? _optiFabricAutoSelectTask;

    public DownloadInstallDialog()
    {
        InitializeComponent();
        BuildAdditionalPickers();
        InstanceNameBox.TextChanging += InstanceNameBox_TextChanging;
        InstanceNameBox.TextChanged += (_, _) => UpdateModificationComparison();
    }

    private void BuildAdditionalPickers()
    {
        foreach (var (name, icon) in new[]
                 {
                     ("LiteLoader", "egg.png"), ("OptiFine", "grasspath.png"),
                     ("Fabric API", "fabric.png"), ("OptiFabric", "fabric.png"),
                 })
        {
            var header = new Button
            {
                Style = (Style)Resources["LoaderHeaderStyle"],
                Tag = name,
            };
            header.Click += Loader_Click;
            var headerGrid = new Grid { ColumnSpacing = 12 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(new Image
            {
                Width = 24,
                Height = 24,
                Source = new BitmapImage(new Uri($"ms-appx:///Assets/Blocks/{icon}")),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            });
            var title = new TextBlock { Text = name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);
            var detail = new TextBlock { Text = "点击选择版本", Style = (Style)Resources["LoaderHeaderDetailStyle"] };
            Grid.SetColumn(detail, 2);
            headerGrid.Children.Add(detail);
            var chevron = new FontIcon { Glyph = "\uE70D", Style = (Style)Resources["LoaderChevronStyle"] };
            Grid.SetColumn(chevron, 3);
            headerGrid.Children.Add(chevron);
            header.Content = headerGrid;

            var latest = new Button
            {
                Style = (Style)Resources["LoaderVersionButtonStyle"],
                Visibility = Visibility.Collapsed,
            };
            latest.Click += SelectLatestVersion_Click;
            var latestGrid = new Grid { ColumnSpacing = 12 };
            latestGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            latestGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            latestGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var latestIcon = new Image { Width = 30, Height = 30, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            latestGrid.Children.Add(latestIcon);
            var latestInfo = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var latestVersion = new TextBlock { FontSize = 14 };
            var latestDetail = new TextBlock { Style = (Style)Resources["LoaderVersionDetailStyle"] };
            latestInfo.Children.Add(latestVersion);
            latestInfo.Children.Add(latestDetail);
            Grid.SetColumn(latestInfo, 1);
            latestGrid.Children.Add(latestInfo);
            var newest = new TextBlock { Text = "最新版本", Style = (Style)Resources["LoaderBadgeStyle"] };
            Grid.SetColumn(newest, 2);
            latestGrid.Children.Add(newest);
            latest.Content = latestGrid;

            var label = new TextBlock { Style = (Style)Resources["LoaderSectionLabelStyle"] };
            var versions = new ItemsControl { ItemTemplate = (DataTemplate)Resources["LoaderVersionRowTemplate"] };
            var panel = new StackPanel { Style = (Style)Resources["LoaderPanelStyle"], Visibility = Visibility.Collapsed };
            panel.Children.Add(new Border { Style = (Style)Resources["LoaderDividerStyle"] });
            panel.Children.Add(latest);
            panel.Children.Add(label);
            panel.Children.Add(versions);

            var clear = new Button
            {
                Style = (Style)Resources["LoaderClearButtonStyle"],
                Content = new FontIcon { Glyph = "\uE8BB", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 14 },
                Tag = name,
                Visibility = Visibility.Collapsed,
            };
            clear.Click += ClearComponent_Click;
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(clear, $"清除 {name}");
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.Children.Add(header);
            Grid.SetColumn(clear, 1);
            headerRow.Children.Add(clear);

            var content = new StackPanel();
            content.Children.Add(headerRow);
            content.Children.Add(panel);
            var card = new Border { Style = (Style)Resources["LoaderCardStyle"], Child = content };
            AdditionalOptionsPanel.Children.Add(card);
            _additionalControls[name] = new LoaderControls(header, clear, card, card, panel, latest, latestIcon, latestVersion, latestDetail, versions, label, detail, chevron);
        }
    }

    internal Action? Host { get; set; }

    public void SetHost(Action close) => Host = close;

    public void Load(string version, string gameDirectory, string vanillaIconPath)
    {
        ResetModificationMode();
        _version = version;
        _gameDirectory = gameDirectory;
        _vanillaIconPath = vanillaIconPath;
        _selectedLoader = null;
        _selectedLoaderVersion = null;
        _selectedComponents.Clear();
        _fabricApiAutoSelectAllowed = false;
        _optiFabricAutoSelectAllowed = false;
        _fabricApiAutoSelectTask = null;
        _optiFabricAutoSelectTask = null;
        _expandedLoader = null;
        _versionCache.Clear();
        _instanceNameWasEdited = false;
        _isUpdatingInstanceName = true;
        InstanceNameBox.Text = version;
        _isUpdatingInstanceName = false;
        InstanceIcon.Source = new BitmapImage(new Uri(_vanillaIconPath));
        SelectionHint.Text = string.Empty;
        ResetLoaderHeaders();
        CollapseLoaderPanels();
        UpdateAdditionalPickerVisibility();
    }

    private void DialogOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_inlineModification) return;
        var inset = e.NewSize.Width < 980 || e.NewSize.Height < 680 ? 16 : 40;
        DialogOverlay.Padding = new Thickness(inset);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Host?.Invoke();

    private async void Loader_Click(object sender, RoutedEventArgs e)
    {
        var generation = _modificationLoadId;
        var minecraftVersion = _version;
        if (sender is not Button { Tag: string loader } || !SupportedLoaders.Contains(loader)) return;
        if (GetLoaderBlockReason(loader) is { } reason)
        {
            SelectionHint.Text = reason;
            return;
        }
        var controls = GetLoaderControls(loader);
        if (_expandedLoader == loader)
        {
            controls.Panel.Visibility = Visibility.Collapsed;
            controls.Chevron.Glyph = "\uE70D";
            _expandedLoader = null;
            UpdateLoaderHeaderStates();
            return;
        }

        CollapseLoaderPanels();
        _expandedLoader = loader;
        controls.Panel.Visibility = Visibility.Visible;
        controls.Chevron.Glyph = "\uE70E";
        UpdateLoaderHeaderStates();
        if (_versionCache.TryGetValue(loader, out var cached))
        {
            PopulateLoaderVersions(loader, cached);
            return;
        }

        controls.HeaderDetail.Text = "正在加载版本…";
        controls.AllVersionsLabel.Text = "正在获取版本列表…";
        controls.LatestButton.Visibility = Visibility.Collapsed;
        SelectionHint.Text = string.Empty;
        try
        {
            var service = App.Services.GetRequiredService<MinecraftInstallService>();
            var versions = await service.GetLoaderVersionsAsync(loader, minecraftVersion);
            if (generation != _modificationLoadId || minecraftVersion != _version) return;
            var icon = new BitmapImage(new Uri(GetLoaderIcon(loader)));
            var rows = versions.Select(item => new LoaderVersionRow(
                loader, item.Version, item.Detail, icon, item.IsStable, item.IsRecommended,
                Id: item.Id, Compatibility: item.Compatibility)).ToArray();
            _versionCache[loader] = rows;
            PopulateLoaderVersions(loader, rows);
            if (rows.Length == 0)
                SelectionHint.Text = $"Minecraft {_version} 没有可用的 {loader} 版本。";
            else
                SelectionHint.Text = string.Empty;
        }
        catch (Exception ex)
        {
            if (generation != _modificationLoadId || minecraftVersion != _version) return;
            controls.HeaderDetail.Text = "加载失败";
            controls.AllVersionsLabel.Text = "无法获取版本列表";
            controls.LatestButton.Visibility = Visibility.Collapsed;
            SelectionHint.Text = $"{loader} 版本列表加载失败：{ex.Message}";
        }
    }

    private void PopulateLoaderVersions(string loader, IReadOnlyList<LoaderVersionRow> rows)
    {
        if (loader == "OptiFine")
        {
            if (_selectedLoader == "Forge")
                rows = rows.Where(row => IsOptiFineCompatibleWithForge(row, _selectedLoaderVersion ?? string.Empty)).ToArray();
            if (_selectedLoader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(_version)) rows = [];
            if (_selectedLoader == "NeoForge") rows = [];
        }
        if (loader == "Forge" && _selectedComponents.TryGetValue("OptiFine", out var selectedOptiFine))
            rows = rows.Where(row => !PclLoaderRules.IsForgeOptiFineRangeUnsupported(_version)
                && IsOptiFineCompatibleWithForge(selectedOptiFine, row.Version)).ToArray();
        var controls = GetLoaderControls(loader);
        var selectedVersion = GetSelectedValue(loader);
        controls.AllVersions.ItemsSource = rows.Select(row => selectedVersion is not null
            && (string.Equals(row.Id, selectedVersion, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Version, selectedVersion, StringComparison.OrdinalIgnoreCase))
                ? row with { Badge = "✓" }
                : row).ToArray();
        controls.AllVersionsLabel.Text = rows.Count == 0 ? "无可用版本" : $"全部版本 ({rows.Count})";
        controls.LatestButton.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (loader == "Forge")
        {
            var recommendedRow = (ContentControl)FindName("ForgeRecommendedVersionRow");
            var recommendedLatest = rows.FirstOrDefault(row => row.IsStable) ?? rows.FirstOrDefault();
            var forgeRecommendation = rows.FirstOrDefault(row => row.IsRecommended);
            if (forgeRecommendation is not null && recommendedLatest is not null && forgeRecommendation.Version != recommendedLatest.Version)
            {
                recommendedRow.Content = forgeRecommendation with { Badge = "推荐版" };
                recommendedRow.Visibility = Visibility.Visible;
            }
            else
            {
                recommendedRow.Content = null;
                recommendedRow.Visibility = Visibility.Collapsed;
            }
        }
        if (rows.Count == 0)
        {
            UpdateLoaderHeaderStates();
            controls.HeaderDetail.Text = "无可用版本";
            return;
        }

        var latestRow = rows.FirstOrDefault(row => row.IsStable) ?? rows[0];
        controls.LatestButton.Tag = latestRow;
        controls.LatestVersion.Text = controls.LatestButton.Tag is LoaderVersionRow latest
            ? latest.Version
            : rows[0].Version;
        controls.LatestDetail.Text = controls.LatestButton.Tag is LoaderVersionRow latestDisplay
            ? latestDisplay.Detail
            : rows[0].Detail;
        controls.LatestIcon.Source = latestRow.Icon;
        controls.HeaderDetail.Text = GetSelectedValue(loader) ?? "点击选择版本";
        UpdateLoaderHeaderStates();
    }

    private void SelectLatestVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LoaderVersionRow row }) SelectLoaderVersion(row);
    }

    private void SelectLoaderVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LoaderVersionRow row }) SelectLoaderVersion(row);
    }

    private void LoaderVersionRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LoaderVersionRow row } button)
            button.Background = row.Badge == "✓"
                ? (Microsoft.UI.Xaml.Media.Brush)Resources["ResourceRowSelectedBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void SelectLoaderVersion(LoaderVersionRow row)
    {
        if (GetLoaderBlockReason(row.Loader) is { } blockReason)
        {
            SelectionHint.Text = blockReason;
            return;
        }
        if (row.Loader is "Forge" or "NeoForge" or "Fabric")
        {
            if (!PclLoaderRules.CanSelectPrimary(_selectedLoader, row.Loader))
            {
                SelectionHint.Text = GetLoaderBlockReason(row.Loader) ?? "请先清除当前加载器，再选择其他加载器。";
                return;
            }
            var wasFabricSelected = _selectedLoader == "Fabric";
            var hadOptiFine = _selectedComponents.ContainsKey("OptiFine");
            _selectedLoader = row.Loader;
            _selectedLoaderVersion = row.Id ?? row.Version;
            if (row.Loader != "Fabric")
            {
                _selectedComponents.Remove("Fabric API");
                _selectedComponents.Remove("OptiFabric");
                _fabricApiAutoSelectAllowed = false;
                _optiFabricAutoSelectAllowed = false;
            }
            if (row.Loader == "NeoForge")
            {
                _selectedComponents.Remove("OptiFine");
            }
            else if (row.Loader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(_version))
            {
                _selectedComponents.Remove("OptiFine");
                _selectedComponents.Remove("OptiFabric");
                _optiFabricAutoSelectAllowed = false;
            }
            else if (row.Loader == "Forge" && _selectedComponents.TryGetValue("OptiFine", out var selectedOptiFine)
                && !IsOptiFineCompatibleWithForge(selectedOptiFine, _selectedLoaderVersion ?? string.Empty))
            {
                _selectedComponents.Remove("OptiFine");
            }
            if (row.Loader == "Fabric")
            {
                if (!wasFabricSelected) _fabricApiAutoSelectAllowed = true;
                if (_fabricApiAutoSelectAllowed && !_selectedComponents.ContainsKey("Fabric API"))
                    _fabricApiAutoSelectTask = AutoSelectDependencyAsync("Fabric API");
                if (_selectedComponents.ContainsKey("OptiFine") && (!wasFabricSelected || !hadOptiFine))
                {
                    _optiFabricAutoSelectAllowed = true;
                    if (PclLoaderRules.SupportsOptiFabric(_version) && !_selectedComponents.ContainsKey("OptiFabric"))
                        _optiFabricAutoSelectTask = AutoSelectDependencyAsync("OptiFabric");
                }
            }
        }
        else
        {
            _selectedComponents[row.Loader] = row;
            if (row.Loader == "OptiFine" && _selectedLoader == "Fabric")
            {
                _optiFabricAutoSelectAllowed = true;
                if (PclLoaderRules.SupportsOptiFabric(_version))
                {
                    _selectedComponents.Remove("OptiFabric");
                    _optiFabricAutoSelectTask = AutoSelectDependencyAsync("OptiFabric");
                }
            }
        }
        UpdateInstanceIcon();
        UpdateSuggestedInstanceName();
        UpdateAdditionalPickerVisibility();
        CollapseLoaderPanels();
        _expandedLoader = null;
        UpdateLoaderHeaderStates();
        SelectionHint.Text = string.Empty;
    }

    private async Task AutoSelectDependencyAsync(string component)
    {
        var generation = _modificationLoadId;
        var minecraftVersion = _version;
        try
        {
            var service = App.Services.GetRequiredService<MinecraftInstallService>();
            var options = await service.GetLoaderVersionsAsync(component, minecraftVersion);
            if (generation != _modificationLoadId || minecraftVersion != _version) return;
            var selected = options.FirstOrDefault(option => !string.IsNullOrWhiteSpace(option.Id));
            if (selected is null) return;
            if (component == "Fabric API" && (!_fabricApiAutoSelectAllowed || _selectedLoader != "Fabric")) return;
            if (component == "OptiFabric" && (!_optiFabricAutoSelectAllowed || _selectedLoader != "Fabric"
                || !_selectedComponents.ContainsKey("OptiFine"))) return;
            if (_selectedComponents.ContainsKey(component)) return;

            var icon = new BitmapImage(new Uri(GetLoaderIcon(component)));
            _selectedComponents[component] = new LoaderVersionRow(
                component, selected.Version, selected.Detail, icon, selected.IsStable,
                selected.IsRecommended, Id: selected.Id, Compatibility: selected.Compatibility);
            UpdateInstanceIcon();
            UpdateSuggestedInstanceName();
            UpdateLoaderHeaderStates();
        }
        catch (Exception ex)
        {
            if (generation != _modificationLoadId || minecraftVersion != _version) return;
            SelectionHint.Text = $"无法自动选择 {component}：{ex.Message}";
        }
    }

    private void ResetLoaderHeaders()
    {
        foreach (var loader in SupportedLoaders)
        {
            var controls = GetLoaderControls(loader);
            controls.HeaderDetail.Text = "点击选择版本";
            controls.ClearButton.Visibility = Visibility.Collapsed;
            controls.LatestButton.Visibility = Visibility.Collapsed;
            controls.LatestButton.Tag = null;
            controls.AllVersions.ItemsSource = null;
            controls.AllVersionsLabel.Text = string.Empty;
        }
        var recommendedRow = (ContentControl)FindName("ForgeRecommendedVersionRow");
        recommendedRow.Content = null;
        recommendedRow.Visibility = Visibility.Collapsed;
        UpdateLoaderHeaderStates();
    }

    private void ClearComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string component }) return;
        if (component is "Forge" or "NeoForge" or "Fabric")
        {
            if (_selectedLoader != component) return;
            _selectedLoader = null;
            _selectedLoaderVersion = null;
            if (component == "Fabric")
            {
                _selectedComponents.Remove("Fabric API");
                _selectedComponents.Remove("OptiFabric");
                _fabricApiAutoSelectAllowed = false;
                _optiFabricAutoSelectAllowed = false;
                _fabricApiAutoSelectTask = null;
                _optiFabricAutoSelectTask = null;
            }
        }
        else
        {
            _selectedComponents.Remove(component);
            if (component == "Fabric API")
            {
                _fabricApiAutoSelectAllowed = false;
                _fabricApiAutoSelectTask = null;
            }
            if (component == "OptiFine")
            {
                _selectedComponents.Remove("OptiFabric");
                _optiFabricAutoSelectAllowed = false;
                _optiFabricAutoSelectTask = null;
            }
            if (component == "OptiFabric")
            {
                _optiFabricAutoSelectAllowed = false;
                _optiFabricAutoSelectTask = null;
            }
        }

        UpdateInstanceIcon();
        UpdateSuggestedInstanceName();
        UpdateAdditionalPickerVisibility();
        UpdateLoaderHeaderStates();
        SelectionHint.Text = string.Empty;
    }

    private void CollapseLoaderPanels()
    {
        foreach (var loader in SupportedLoaders)
        {
            var controls = GetLoaderControls(loader);
            controls.Panel.Visibility = Visibility.Collapsed;
            controls.Chevron.Glyph = "\uE70D";
        }
    }

    private LoaderControls GetLoaderControls(string loader)
    {
        if (_additionalControls.TryGetValue(loader, out var controls)) return controls;
        return new LoaderControls(
            (Button)FindName($"{loader}Header"),
            (Button)FindName($"{loader}ClearButton"),
            (Border)FindName($"{loader}Card"),
            null,
            (StackPanel)FindName($"{loader}VersionPanel"),
            (Button)FindName($"{loader}LatestButton"),
            (Image)FindName($"{loader}LatestIcon"),
            (TextBlock)FindName($"{loader}LatestVersion"),
            (TextBlock)FindName($"{loader}LatestDetail"),
            (ItemsControl)FindName($"{loader}AllVersions"),
            (TextBlock)FindName($"{loader}AllVersionsLabel"),
            (TextBlock)FindName($"{loader}HeaderDetail"),
            (FontIcon)FindName($"{loader}Chevron"));
    }

    private string? GetSelectedValue(string component)
        => component is "Forge" or "NeoForge" or "Fabric"
            ? _selectedLoader == component ? _selectedLoaderVersion : null
            : _selectedComponents.TryGetValue(component, out var row) ? row.Version : null;

    private string? GetLoaderBlockReason(string loader)
    {
        if (loader is "Forge" or "NeoForge" or "Fabric")
        {
            if (!PclLoaderRules.CanSelectPrimary(_selectedLoader, loader))
                return $"与 {_selectedLoader} 不兼容";
            if (loader == "NeoForge" && _selectedComponents.ContainsKey("OptiFine"))
                return "与 OptiFine 不兼容";
            if (loader == "Fabric" && _selectedComponents.ContainsKey("OptiFine")
                && !PclLoaderRules.SupportsOptiFineWithFabric(_version))
                return "与 OptiFine 不兼容";
            if (loader == "Forge" && _selectedComponents.ContainsKey("OptiFine")
                && PclLoaderRules.IsForgeOptiFineRangeUnsupported(_version))
                return "与 OptiFine 不兼容";
            return null;
        }

        if (loader == "OptiFine")
        {
            if (_selectedLoader == "NeoForge") return "与 NeoForge 不兼容";
            if (_selectedLoader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(_version))
                return "与 Fabric 不兼容";
            if (_selectedLoader == "Forge" && PclLoaderRules.IsForgeOptiFineRangeUnsupported(_version))
                return "与 Forge 不兼容";
            if (_selectedLoader == "Forge" && _versionCache.TryGetValue("OptiFine", out var optiFineVersions)
                && !optiFineVersions.Any(row => IsOptiFineCompatibleWithForge(row, _selectedLoaderVersion ?? string.Empty)))
                return "无兼容的 Forge 版本";
        }
        return null;
    }

    private void UpdateLoaderHeaderStates()
    {
        foreach (var loader in SupportedLoaders)
        {
            var controls = GetLoaderControls(loader);
            var selected = GetSelectedValue(loader);
            var reason = GetLoaderBlockReason(loader);
            controls.Header.IsEnabled = reason is null;
            controls.HeaderDetail.Text = selected ?? reason ?? "点击选择版本";
            controls.ClearButton.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
            controls.Card.ClearValue(Border.BorderBrushProperty);
            if (selected is not null || _expandedLoader == loader)
                controls.Header.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            else
                controls.Header.ClearValue(Button.ForegroundProperty);
        }
    }

    private void UpdateInstanceIcon()
    {
        var iconPath = _selectedLoader switch
        {
            "Fabric" => "ms-appx:///Assets/Blocks/fabric.png",
            "Forge" => "ms-appx:///Assets/Blocks/forge.png",
            "NeoForge" => "ms-appx:///Assets/Blocks/neoforge.png",
            _ when _selectedComponents.ContainsKey("LiteLoader") => "ms-appx:///Assets/Blocks/egg.png",
            _ when _selectedComponents.ContainsKey("OptiFine") => "ms-appx:///Assets/Blocks/grasspath.png",
            _ => _vanillaIconPath,
        };
        InstanceIcon.Source = new BitmapImage(new Uri(iconPath));
    }

    private void UpdateAdditionalPickerVisibility()
    {
        if (_additionalControls.Count == 0) return;
        if (Version.TryParse(_version.Split('-', '+')[0], out var version))
            _additionalControls["LiteLoader"].Group!.Visibility = version < new Version(1, 13) ? Visibility.Visible : Visibility.Collapsed;
        _additionalControls["Fabric API"].Group!.Visibility = _selectedLoader == "Fabric" ? Visibility.Visible : Visibility.Collapsed;
        _additionalControls["OptiFabric"].Group!.Visibility = _selectedLoader == "Fabric"
            && _selectedComponents.ContainsKey("OptiFine") && PclLoaderRules.SupportsOptiFabric(_version)
                ? Visibility.Visible : Visibility.Collapsed;
        UpdateLoaderHeaderStates();
        UpdateModificationComparison();
    }

    private bool IsOptiFineCompatibleWithForge(LoaderVersionRow optiFine, string forgeVersion)
        => PclLoaderRules.IsOptiFineCompatibleWithForge(optiFine.Compatibility, _version, forgeVersion);

    private static string GetLoaderIcon(string loader)
        => loader switch
        {
            "OptiFine" => "ms-appx:///Assets/Blocks/grasspath.png",
            "LiteLoader" => "ms-appx:///Assets/Blocks/egg.png",
            "Fabric API" or "OptiFabric" => "ms-appx:///Assets/Blocks/fabric.png",
            _ => $"ms-appx:///Assets/Blocks/{loader.ToLowerInvariant()}.png",
        };

    private void InstanceNameBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs e)
    {
        if (!_isUpdatingInstanceName)
        {
            _instanceNameWasEdited = true;
        }
    }

    private void UpdateSuggestedInstanceName()
    {
        if (_instanceNameWasEdited) return;

        var name = _version;
        if (_selectedLoader is not null && _selectedLoaderVersion is not null)
        {
            var loaderVersion = _selectedLoaderVersion.Split('+', 2)[0];
            name += _selectedLoader switch
            {
                "Fabric" => $"-Fabric {loaderVersion}",
                "Forge" => $"-Forge_{_selectedLoaderVersion}",
                "NeoForge" => $"-NeoForge_{loaderVersion}",
                _ => "",
            };
        }
        foreach (var key in new[] { "LiteLoader", "OptiFine" })
        {
            if (!_selectedComponents.TryGetValue(key, out var component)) continue;
            var componentVersion = component.Version.StartsWith(_version + " ", StringComparison.OrdinalIgnoreCase)
                ? component.Version[(_version.Length + 1)..].Replace(' ', '_')
                : component.Version.Replace(' ', '_');
            name += key switch
            {
                "LiteLoader" => "-LiteLoader",
                "OptiFine" => $"-OptiFine_{componentVersion}",
                _ => string.Empty,
            };
        }

        _isUpdatingInstanceName = true;
        InstanceNameBox.Text = name;
        _isUpdatingInstanceName = false;
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        var generation = _modificationLoadId;
        if (string.IsNullOrWhiteSpace(InstanceNameBox.Text))
        {
            SelectionHint.Text = "请先输入实例名称。";
            InstanceNameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_selectedLoader == "Fabric" && _fabricApiAutoSelectAllowed
            && !_selectedComponents.ContainsKey("Fabric API"))
            await (_fabricApiAutoSelectTask ??= AutoSelectDependencyAsync("Fabric API"));
        if (generation != _modificationLoadId) return;
        if (_selectedLoader == "Fabric" && _selectedComponents.ContainsKey("OptiFine")
            && _optiFabricAutoSelectAllowed && PclLoaderRules.SupportsOptiFabric(_version)
            && !_selectedComponents.ContainsKey("OptiFabric"))
            await (_optiFabricAutoSelectTask ??= AutoSelectDependencyAsync("OptiFabric"));
        if (generation != _modificationLoadId) return;

        if (_selectedComponents.ContainsKey("Fabric API") && _selectedLoader != "Fabric")
        {
            SelectionHint.Text = "Fabric API 需要同时选择 Fabric。";
            return;
        }
        if (_selectedComponents.ContainsKey("OptiFabric")
            && (_selectedLoader != "Fabric" || !_selectedComponents.ContainsKey("OptiFine")))
        {
            SelectionHint.Text = "OptiFabric 需要同时选择 Fabric 和 OptiFine。";
            return;
        }
        if (_selectedLoader == "NeoForge" && _selectedComponents.ContainsKey("OptiFine"))
        {
            SelectionHint.Text = "PCL 不支持 NeoForge 与 OptiFine 组合安装。";
            return;
        }
        if (_selectedLoader == "Forge" && _selectedComponents.TryGetValue("OptiFine", out var optiFine)
            && !IsOptiFineCompatibleWithForge(optiFine, _selectedLoaderVersion ?? string.Empty))
        {
            SelectionHint.Text = "所选 OptiFine 版本与当前 Forge 不兼容。";
            return;
        }
        if (_inlineModification)
        {
            await StartModificationAsync();
            return;
        }
        var transfer = App.Services.GetRequiredService<TransferViewModel>();
        transfer.EnqueueInstall(
            _version,
            InstanceNameBox.Text.Trim(),
            _gameDirectory,
            _selectedLoader,
            _selectedLoaderVersion,
            _selectedComponents.GetValueOrDefault("LiteLoader")?.Version,
            _selectedComponents.GetValueOrDefault("OptiFine")?.Id,
            _selectedComponents.GetValueOrDefault("Fabric API")?.Id,
            _selectedComponents.GetValueOrDefault("OptiFabric")?.Id);
        Host?.Invoke();
        App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Transfer);
    }

    private sealed record LoaderVersionRow(
        string Loader,
        string Version,
        string Detail,
        BitmapImage Icon,
        bool IsStable,
        bool IsRecommended = false,
        string Badge = "",
        string? Id = null,
        string? Compatibility = null);

    private sealed record LoaderControls(
        Button Header,
        Button ClearButton,
        Border Card,
        FrameworkElement? Group,
        StackPanel Panel,
        Button LatestButton,
        Image LatestIcon,
        TextBlock LatestVersion,
        TextBlock LatestDetail,
        ItemsControl AllVersions,
        TextBlock AllVersionsLabel,
        TextBlock HeaderDetail,
        FontIcon Chevron);
}
