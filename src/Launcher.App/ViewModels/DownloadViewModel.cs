using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Launcher.Core.Assets;
using Launcher.Core.Downloads;
using Launcher.Core.Versions;
using Serilog;

namespace Launcher.App.ViewModels;

/// <summary>
/// The five download categories shown in the left rail.
/// </summary>
public enum DownloadSection
{
    Minecraft,
    Mod,
    Modpack,
    Shader,
    ResourcePack,
    DataPack,
}

/// <summary>
/// A row of the Minecraft category's version list.
/// </summary>
public sealed record McVersionEntry(
    string Name,
    string Version,
    string ReleaseType,
    string ReleaseDate,
    bool IsLatest = false)
{
    /// <summary>
    /// Block artwork for the version, reusing the same bundled set as the
    /// instance page so a version reads the same in both places.
    /// </summary>
    /// <remarks>
    /// The mapping follows PCL: grass for a plain release, command block for a
    /// snapshot, cobblestone for anything ancient. Fools' versions are snapshots
    /// too, so they share the command block.
    /// </remarks>
    public string IconPath => $"ms-appx:///Assets/Blocks/{BlockIconName.ForReleaseType(ReleaseType)}";
}

/// <summary>
/// One item in a resource list (mod, modpack, shader, data pack).
/// </summary>
/// <remarks>
/// The four resource categories share a shape - title, summary, source, tags,
/// downloads - so they share one record rather than four near-identical ones.
/// </remarks>
public sealed record ResourceEntry(
    string Name,
    string Summary,
    string Source,
    string Category,
    string GameVersions,
    string Downloads,
    string Initial,
    string ProjectId = "",
    string ProjectType = "mod",
    IReadOnlyList<string>? ProjectCategories = null,
    string IconUrl = "",
    IReadOnlyList<string>? SupportedGameVersions = null,
    string WebsiteUrl = "",
    long DownloadCount = 0,
    DateTimeOffset Updated = default) : System.ComponentModel.INotifyPropertyChanged
{
    private string _installedState = "";
    public string InstalledState
    {
        get => _installedState;
        set
        {
            if (_installedState == value) return;
            _installedState = value;
            PropertyChanged?.Invoke(this, new(nameof(InstalledState)));
            PropertyChanged?.Invoke(this, new(nameof(InstalledVisibility)));
        }
    }
    public Microsoft.UI.Xaml.Visibility InstalledVisibility => InstalledState.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    /// <summary>
    /// Single-letter avatar, since none of the placeholder entries ship artwork.
    /// </summary>
    public string AvatarText => string.IsNullOrEmpty(Initial) ? "?" : Initial[..1].ToUpperInvariant();

    public IReadOnlyList<string> Categories => ProjectCategories ?? [];
    public IReadOnlyList<string> Versions => SupportedGameVersions ?? [];
    public string LoaderLabel => string.Join(" / ", Categories.Where(category =>
        category is "fabric" or "forge" or "neoforge" or "quilt" or "iris" or "optifine")
        .Select(category => category switch { "neoforge" => "NeoForge", "optifine" => "OptiFine", _ => char.ToUpperInvariant(category[0]) + category[1..] }));
    public Microsoft.UI.Xaml.Visibility LoaderVisibility => string.IsNullOrEmpty(LoaderLabel)
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public ModrinthProject Project => new(ProjectId, ProjectId, Name, Summary, ProjectType,
        IconUrl, DownloadCount, Categories, Versions, Source, WebsiteUrl, Updated);
}

/// <summary>
/// Download page state: which category is open, and the filters for the
/// resource categories.
/// </summary>
public partial class DownloadViewModel : ObservableObject
{
    // ======== category switching ========

    [ObservableProperty]
    private DownloadSection _section = DownloadSection.Minecraft;

    public bool IsMinecraft => Section == DownloadSection.Minecraft;

    public bool IsResourceSection => Section != DownloadSection.Minecraft;

    /// <summary>
    /// Title for the resource filter header, so one panel serves four categories.
    /// </summary>
    public string ResourceTitle => Section switch
    {
        DownloadSection.Mod => "搜索 Mod",
        DownloadSection.Modpack => "搜索整合包",
        DownloadSection.Shader => "搜索光影包",
        DownloadSection.ResourcePack => "搜索资源包",
        DownloadSection.DataPack => "搜索数据包",
        _ => "搜索",
    };

    public string ResourceTypeName => Section switch
    {
        DownloadSection.Modpack => "整合包", DownloadSection.Shader => "光影包",
        DownloadSection.ResourcePack => "资源包", DownloadSection.DataPack => "数据包", _ => "Mod",
    };
    public string ResultCountText => $"第 {ResourcePage + 1} 页 · {Resources.Count} 项";
    public int ResourcePage { get; private set; }
    private bool _hasMoreResources;
    public bool CanNextResourcePage => _hasMoreResources;
    public bool CanPreviousResourcePage => ResourcePage > 0;

    partial void OnSectionChanged(DownloadSection value)
    {
        OnPropertyChanged(nameof(IsMinecraft));
        OnPropertyChanged(nameof(IsResourceSection));
        OnPropertyChanged(nameof(ResourceTitle));
        OnPropertyChanged(nameof(ResourceTypeName));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(Loaders));

        _allResources.Clear();
        Resources.Clear();
        ResourceStatus = string.Empty;
    }

    // ======== Minecraft: collapsible version groups ========

    [ObservableProperty]
    private bool _isReleaseExpanded;

    [ObservableProperty]
    private bool _isPreviewExpanded;

    [ObservableProperty]
    private bool _isOldExpanded;

    [ObservableProperty]
    private bool _isFoolsExpanded;

    public ObservableCollection<McVersionEntry> LatestVersions { get; } = [];

    public ObservableCollection<McVersionEntry> ReleaseVersions { get; } = [];

    public ObservableCollection<McVersionEntry> PreviewVersions { get; } = [];

    public ObservableCollection<McVersionEntry> OldVersions { get; } = [];

    public ObservableCollection<McVersionEntry> FoolsVersions { get; } = [];

    /// <summary>True while the manifest is being fetched.</summary>
    [ObservableProperty]
    private bool _isLoadingVersions;

    /// <summary>Shown when the manifest could not be fetched at all.</summary>
    [ObservableProperty]
    private string? _versionLoadError;

    private bool _versionsLoaded;

    /// <summary>
    /// Loads Mojang's version manifest and distributes it across the five groups.
    /// </summary>
    /// <remarks>
    /// The groups map onto Mojang's own <c>type</c> field, which is exactly what
    /// the vanilla launcher uses: releases, snapshots, and the alpha/beta builds.
    /// April Fools versions have no type of their own, so they are recognised by
    /// id against a known list and pulled out of the snapshot group.
    ///
    /// Only the newest build of each kind goes in 最新版本; everything else is
    /// filed under its group.
    /// </remarks>
    public async Task LoadVersionsAsync(bool forceRefresh = false)
    {
        if (_versionsLoaded && !forceRefresh)
        {
            return;
        }

        IsLoadingVersions = true;
        VersionLoadError = null;

        try
        {
            var manifest = await _versions.GetVersionsAsync(forceRefresh).ConfigureAwait(true);

            if (manifest.Count == 0)
            {
                VersionLoadError = "无法获取版本列表，请检查网络连接后重试。";
                Log.Warning("版本清单为空");
                return;
            }

            LatestVersions.Clear();
            ReleaseVersions.Clear();
            PreviewVersions.Clear();
            OldVersions.Clear();
            FoolsVersions.Clear();

            var releases = manifest.Where(v => v.IsRelease).ToList();
            var peers = manifest.Where(v => v.IsSnapshot || v.IsOld).ToList();

            // The newest two releases, plus whatever shipped most recently
            // overall, make up the "latest" group: that is what a user opening
            // this page wants one click away.
            foreach (var latest in releases.Take(2))
            {
                LatestVersions.Add(ToEntry(latest));
            }

            var newest = manifest.FirstOrDefault();
            if (newest is not null
                && !newest.IsRelease
                && !FoolsVersionIds.Contains(newest.Id))
            {
                LatestVersions.Add(ToEntry(newest));
            }

            foreach (var release in releases.Skip(2))
            {
                ReleaseVersions.Add(ToEntry(release));
            }

            foreach (var peer in peers.OrderByDescending(v => v.ReleaseTime))
            {
                if (FoolsVersionIds.Contains(peer.Id))
                {
                    FoolsVersions.Add(ToEntry(peer, "愚人节版"));
                }
                else if (peer.IsOld)
                {
                    OldVersions.Add(ToEntry(peer, "远古版"));
                }
                else
                {
                    PreviewVersions.Add(ToEntry(peer));
                }
            }

            var knownVersions = manifest.Where(version => version.IsRelease).Select(version => version.Id).Distinct().ToArray();
            GameVersions.Clear();
            GameVersions.Add("全部");
            foreach (var version in knownVersions) GameVersions.Add(version);
            _versionsLoaded = true;
            Log.Information(
                "版本列表已加载：最新 {Latest}，正式版 {Release}，预览版 {Preview}，远古 {Old}，愚人节 {Fools}",
                LatestVersions.Count, ReleaseVersions.Count, PreviewVersions.Count,
                OldVersions.Count, FoolsVersions.Count);
        }
        catch (Exception ex)
        {
            VersionLoadError = $"加载版本列表失败：{ex.Message}";
            Log.Warning(ex, "加载版本列表失败");
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    /// <summary>
    /// Builds a card entry from a manifest row.
    /// </summary>
    private static McVersionEntry ToEntry(ManifestVersion version, string? typeOverride = null)
    {
        var type = typeOverride ?? version.Type switch
        {
            "release" => "正式版",
            "snapshot" when version.Id.Contains("-pre", StringComparison.OrdinalIgnoreCase) => "预发布版",
            "snapshot" when version.Id.Contains("-rc", StringComparison.OrdinalIgnoreCase) => "发布候选",
            "snapshot" => "快照",
            "old_alpha" => "远古版",
            "old_beta" => "远古版",
            _ => version.Type,
        };

        var date = version.ReleaseTime == default
            ? ""
            : version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");

        return new McVersionEntry(version.Id, version.Id, type, date);
    }

    // ======== resource filters: collapsed by default ========

    [ObservableProperty]
    private bool _isFilterExpanded;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedSource = "全部";

    [ObservableProperty]
    private string _selectedTag = "全部";

    [ObservableProperty]
    private string _selectedSort = "热度";

    [ObservableProperty]
    private string _selectedGameVersion = "全部";

    [ObservableProperty]
    private string _selectedLoader = "任意加载器";

    [ObservableProperty]
    private string _selectedResolution = "全部分辨率";

    [ObservableProperty]
    private bool _isResourceLoading;

    [ObservableProperty]
    private string _resourceStatus = string.Empty;

    public IReadOnlyList<string> Sources { get; } = ["全部", "Modrinth", "CurseForge"];

    public IReadOnlyList<string> Tags => Section switch
    {
        DownloadSection.Shader or DownloadSection.ResourcePack => ["全部", "原版风格", "写实风格", "幻想风格", "装饰"],
        _ => ["全部", "科技", "魔法", "冒险", "优化", "装饰", "实用", "世界生成"],
    };

    public IReadOnlyList<string> Sorts { get; } = ["热度", "最新", "下载量"];

    public ObservableCollection<string> GameVersions { get; } =
        ["全部", "1.21.4", "1.21.1", "1.20.6", "1.20.1", "1.19.2", "1.16.5", "1.12.2"];

    public IReadOnlyList<string> Loaders => Section == DownloadSection.Shader
        ? ["任意加载器", "Iris", "OptiFine"]
        : ["任意加载器", "Fabric", "Forge", "NeoForge", "Quilt"];
    public IReadOnlyList<string> Resolutions { get; } = ["全部分辨率", "16x", "32x", "64x", "128x", "256x", "512x+"];

    /// <summary>
    /// Filters applied to <see cref="AllResources"/> when they are set.
    /// </summary>
    public int ActiveFilterCount =>
        (SelectedSource != "全部" ? 1 : 0)
        + (SelectedTag != "全部" ? 1 : 0)
        + (SelectedSort != "热度" ? 1 : 0)
        + (SelectedGameVersion != "全部" ? 1 : 0)
        + (SelectedLoader != "任意加载器" ? 1 : 0);

    // ======== resource lists ========

    private readonly List<ResourceEntry> _allResources = [];
    private CancellationTokenSource? _resourceSearch;
    private bool _resettingFilters;

    public ObservableCollection<ResourceEntry> Resources { get; } = [];

    private readonly MinecraftVersionService _versions;
    private readonly CommunityResourceService _community;
    private readonly SettingsStore _settings;

    private static readonly ILogger Log = Serilog.Log.ForContext<DownloadViewModel>();

    /// <summary>
    /// April Fools versions. Mojang tags these as ordinary snapshots, so the only
    /// way to file them separately is by id; the list mirrors the set PCL
    /// special-cases in its version naming.
    /// </summary>
    private static readonly HashSet<string> FoolsVersionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.0",
        "1.RV-Pre1",
        "3D Shareware v1.34",
        "20w14infinite",
        "20w14a",
        "22w13oneblockatatime",
        "23w13a_or_b",
        "24w14potato",
        "25w14craftmine",
    };

    public DownloadViewModel(MinecraftVersionService versions, CommunityResourceService community, SettingsStore settings)
    {
        _versions = versions;
        _community = community;
        _settings = settings;
        _selectedSource = settings.Load().CommunitySource is "Modrinth" or "CurseForge" ? settings.Load().CommunitySource : "全部";
    }

    partial void OnSearchTextChanged(string value) => SearchFromFirstPage();

    partial void OnSelectedSourceChanged(string value) => SearchFromFirstPage();

    partial void OnSelectedTagChanged(string value) => SearchFromFirstPage();

    partial void OnSelectedSortChanged(string value) => SearchFromFirstPage();

    partial void OnSelectedGameVersionChanged(string value) => SearchFromFirstPage();

    partial void OnSelectedLoaderChanged(string value) => SearchFromFirstPage();
    partial void OnSelectedResolutionChanged(string value) => SearchFromFirstPage();

    private void SearchFromFirstPage()
    {
        if (_resettingFilters) return;
        ResourcePage = 0;
        _ = SearchResourcesAsync();
    }

    public async Task ChangeResourcePageAsync(int delta)
    {
        if (IsResourceLoading) return;
        ResourcePage = Math.Max(0, ResourcePage + delta);
        await SearchResourcesAsync();
    }

    public async Task SearchResourcesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsResourceSection || _resettingFilters) return;
        _resourceSearch?.Cancel();
        _resourceSearch?.Dispose();
        var search = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _resourceSearch = search;
        var token = search.Token;

        IsResourceLoading = true;
        var searchFinished = false;
        ResourceStatus = string.Empty;
        try
        {
            await Task.Delay(250, token);
            var type = Section switch
            {
                DownloadSection.Modpack => "modpack",
                DownloadSection.Shader => "shader",
                DownloadSection.ResourcePack => "resourcepack",
                DownloadSection.DataPack => "datapack",
                _ => "mod",
            };
            var category = CategoryTag(SelectedTag);
            var loader = Section == DownloadSection.DataPack ? "datapack"
                : Section is DownloadSection.ResourcePack ? "任意加载器" : SelectedLoader;
            var result = await _community.SearchAsync(SelectedSource, SearchText, type, SelectedGameVersion,
                loader, SelectedSort, ResourcePage * 50, category,
                Section == DownloadSection.ResourcePack && SelectedResolution != "全部分辨率" ? SelectedResolution : null, token,
                new Progress<ResourceSearchResult>(partial =>
                {
                    if (!searchFinished && !token.IsCancellationRequested && ReferenceEquals(_resourceSearch, search))
                        ApplyResourceResult(partial);
                }));
            if (token.IsCancellationRequested) return;
            searchFinished = true;
            ApplyResourceResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "社区资源搜索失败");
            if (!token.IsCancellationRequested)
            {
                ResourceStatus = "获取资源失败：" + ex.Message;
            }
        }
        finally
        {
            searchFinished = true;
            if (ReferenceEquals(_resourceSearch, search))
            {
                IsResourceLoading = false;
                OnPropertyChanged(nameof(ResultCountText));
                OnPropertyChanged(nameof(CanNextResourcePage));
                OnPropertyChanged(nameof(CanPreviousResourcePage));
            }
        }
    }

    private void ApplyResourceResult(ResourceSearchResult result)
    {
        var projects = result.Projects;
        _hasMoreResources = result.HasMore;

        _allResources.Clear();
        _allResources.AddRange(projects.Select(project => new ResourceEntry(
            project.Title,
            project.Description,
            project.Source,
            project.Categories.Select(MapCategory).FirstOrDefault(value => value != "其他") ?? "其他",
            string.Join(" / ", project.GameVersions.Take(3)),
            FormatDownloads(project.Downloads),
            project.Title,
            project.Id,
            project.ProjectType,
            project.Categories,
            project.IconUrl,
            project.GameVersions,
            project.WebsiteUrl,
            project.Downloads,
            project.Updated)));
        ApplyResourceFilter();
        ResourceStatus = result.Warning;
        if (Resources.Count == 0 && ResourceStatus.Length == 0) ResourceStatus = "没有匹配结果，试试放宽筛选条件。";
    }

    /// <summary>
    /// Returns every filter to its default and clears the search box.
    /// </summary>
    /// <remarks>
    /// Assignments go through the generated setters, so each one re-runs the
    /// filter. That is wasteful but harmless at this list size, and it keeps a
    /// reset correct even if an individual setter is later given a side effect.
    /// </remarks>
    public void ResetResourceFilters()
    {
        _resettingFilters = true;
        SearchText = string.Empty;
        SelectedSource = _settings.Load().CommunitySource is "Modrinth" or "CurseForge" ? _settings.Load().CommunitySource : "全部";
        SelectedTag = "全部";
        SelectedSort = "热度";
        SelectedGameVersion = "全部";
        SelectedLoader = "任意加载器";
        SelectedResolution = "全部分辨率";
        ResourcePage = 0;
        _resettingFilters = false;
        if (IsResourceSection) _ = SearchResourcesAsync();
    }

    /// <summary>
    /// Recomputes <see cref="Resources"/> from the pool and the current filters.
    /// </summary>
    /// <remarks>
    /// A full rebuild rather than an in-place diff: the lists are small, and
    /// rebuilding keeps the section header count and the visible items from ever
    /// disagreeing, which in-place filtering in the view would allow.
    /// </remarks>
    public void ApplyResourceFilter()
    {
        OnPropertyChanged(nameof(ActiveFilterCount));

        IEnumerable<ResourceEntry> matches = _allResources;

        matches = SelectedSort switch
        {
            "名称" => matches.OrderBy(m => m.Name, StringComparer.CurrentCulture),
            _ => matches,
        };

        Resources.Clear();
        foreach (var item in matches)
        {
            Resources.Add(item);
        }
        OnPropertyChanged(nameof(ResultCountText));
    }

    private static string? CategoryTag(string tag) => tag switch
    {
        "科技" => "technology", "魔法" => "magic", "冒险" => "adventure", "优化" => "optimization",
        "装饰" => "decoration", "实用" => "utility", "世界生成" => "worldgen",
        "原版风格" => "vanilla-like", "写实风格" => "realistic", "幻想风格" => "fantasy", _ => null,
    };

    private static string MapCategory(string category) => category.ToLowerInvariant() switch
    {
        "technology" => "科技",
        "magic" => "魔法",
        "adventure" => "冒险",
        "optimization" => "优化",
        "decoration" => "装饰",
        "utility" => "实用",
        "worldgen" => "世界生成",
        _ => "其他",
    };

    private static string FormatDownloads(long downloads) => downloads switch
    {
        >= 100_000_000 => $"{downloads / 100_000_000d:0.#} 亿",
        >= 10_000 => $"{downloads / 10_000d:0.#} 万",
        _ => downloads.ToString("N0"),
    };
}
