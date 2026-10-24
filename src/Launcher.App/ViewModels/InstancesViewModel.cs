using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Launcher.Core.Instances;
using Serilog;

namespace Launcher.App.ViewModels;

/// <summary>
/// Instance browser state: a list of game folders on the left, and the
/// instances they contain grouped into three tiers on the right.
/// </summary>
/// <remarks>
/// The grouping is a product decision rather than a sort, and it follows PCL:
/// "can install mods" is a property of the loader, and "rarely used" is a
/// property of how old the game version is. Keeping them as three fixed sections
/// lets the user build a stable mental map instead of scanning a flat list.
/// </remarks>
public partial class InstancesViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<InstancesViewModel>();

    private readonly GameFolderService _folders;
    private readonly InstanceScanner _scanner;
    private readonly LaunchViewModel _launch;
    private readonly SettingsStore _settings;
    private string? _lastSelectedInstanceId;
    private string? _lastSelectedInstancePath;
    private CancellationTokenSource? _scanCancellation;

    // ======== folders ========

    [ObservableProperty]
    private GameFolder? _selectedFolder;

    private string? _selectedFolderPath;

    public string? FolderSelectionPath => SelectedFolder?.Path ?? _selectedFolderPath;

    public ObservableCollection<GameFolder> Folders { get; } = [];

    // ======== search ========

    [ObservableProperty]
    private string _searchText = string.Empty;

    // ======== section expansion ========

    /// <summary>Moddable instances start expanded: this is the common case.</summary>
    [ObservableProperty]
    private bool _isModdableExpanded = true;

    [ObservableProperty]
    private bool _isRegularExpanded = true;

    /// <summary>Rarely-used instances start collapsed to keep the page short.</summary>
    [ObservableProperty]
    private bool _isRareExpanded;

    [ObservableProperty]
    private bool _isErrorExpanded;

    // ======== sections ========

    public ObservableCollection<InstanceItem> ModdableInstances { get; } = [];

    public ObservableCollection<InstanceItem> RegularInstances { get; } = [];

    public ObservableCollection<InstanceItem> RareInstances { get; } = [];

    public ObservableCollection<InstanceItem> ErrorInstances { get; } = [];

    public IReadOnlyList<InstanceItem> AvailableInstances => _allModdable.Concat(_allRegular).Concat(_allRare).Concat(_allErrors).ToArray();

    /// <summary>
    /// The instance currently selected in any of the three sections.
    /// </summary>
    /// <remarks>
    /// One shared slot rather than a flag per section: exactly one instance can
    /// be the selection, and three sections each holding their own choice would
    /// let the page show two "current" instances at once.
    /// </remarks>
    [ObservableProperty]
    private InstanceItem? _selectedInstance;

    /// <summary>True when the selected folder has no visible instances.</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isLoading;

    public InstancesViewModel(
        GameFolderService folders,
        InstanceScanner scanner,
        LaunchViewModel launch,
        SettingsStore settings)
    {
        _folders = folders;
        _scanner = scanner;
        _launch = launch;
        _settings = settings;
        var saved = settings.Load();
        _selectedFolderPath = saved.SelectedGameFolderPath;
        if (!string.IsNullOrWhiteSpace(saved.SelectedInstancePath))
        {
            try
            {
                _lastSelectedInstancePath = System.IO.Path.GetFullPath(saved.SelectedInstancePath);
                _lastSelectedInstanceId = InstanceIdentity.ForPath(_lastSelectedInstancePath);
                _selectedFolderPath = System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetDirectoryName(_lastSelectedInstancePath)) ?? _selectedFolderPath;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                _lastSelectedInstanceId = null;
                _lastSelectedInstancePath = null;
                saved.SelectedInstancePath = "";
                _settings.Save(saved);
                Log.Warning(ex, "保存的实例路径无效，已清除选择");
            }
        }

        _ = ReloadFoldersAsync();
    }

    partial void OnSelectedFolderChanged(GameFolder? value)
    {
        if (value is null)
        {
            // Collection rebuilds and item replacement can transiently clear the
            // two-way ListView selection. Preserve the selected path during those
            // controlled scans; a real user deselection outside loading clears it.
            if (!IsLoading)
            {
                _selectedFolderPath = null;
                PersistSelectedFolderPath("");
                OnPropertyChanged(nameof(FolderSelectionPath));
            }

            return;
        }

        var pathChanged = !GameFolderService.PathsEqual(
            _selectedFolderPath ?? string.Empty,
            value.Path);
        _selectedFolderPath = value.Path;
        OnPropertyChanged(nameof(FolderSelectionPath));

        if (!IsLoading)
        {
            PersistSelectedFolderPath(value.Path);
            if (pathChanged)
            {
                _ = LoadInstancesAsync();
            }
        }
    }

    partial void OnSelectedInstanceChanged(InstanceItem? value)
    {
        if (value is null)
        {
            return;
        }

        _lastSelectedInstanceId = value.Id;
        _lastSelectedInstancePath = value.Path;
        _launch.ApplyInstance(value);
    }


    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(HasVisibleInstances));
    }

    public bool HasVisibleInstances => ModdableInstances.Count + RegularInstances.Count + RareInstances.Count + ErrorInstances.Count > 0;

    /// <summary>Re-runs the current filter, e.g. after the search box changed.</summary>
    public void Refilter()
    {
        ApplyFilter();
        OnPropertyChanged(nameof(HasVisibleInstances));
        if (_lastSelectedInstanceId is not null)
        {
            SelectedInstance = FindInstance(_lastSelectedInstanceId);
        }
    }

    /// <summary>Finds an instance in the unfiltered scan snapshot.</summary>
    public InstanceItem? FindInstance(string id)
        => AvailableInstances
            .FirstOrDefault(item => item.Id == id);

    /// <summary>Removes a deleted instance from the remembered launch selection.</summary>
    public void ForgetDeletedInstance(string id)
    {
        var wasRemembered = _lastSelectedInstanceId == id;
        if (_lastSelectedInstanceId == id)
        {
            _lastSelectedInstanceId = null;
            _lastSelectedInstancePath = null;
        }

        if (_launch.SelectedInstanceId == id || wasRemembered && _launch.SelectedInstanceId is null)
        {
            _launch.ClearInstance();
        }
    }

    /// <summary>
    /// Rescans the disk for game folders and reloads the selected one.
    /// </summary>
    /// <remarks>
    /// The previously selected folder is kept selected by path, so a refresh does
    /// not bounce the user back to the first folder in the rail.
    /// </remarks>
    public async Task ReloadFoldersAsync()
    {
        var previous = SelectedFolder?.Path ?? _selectedFolderPath;
        var previousInstanceId = _lastSelectedInstanceId ?? _launch.SelectedInstanceId;

        // Clearing the rail makes the ListView drop its selection, which calls
        // back into LoadInstances while Folders is half rebuilt. Suppress that,
        // then load once at the end when the list is whole.
        IsLoading = true;
        ClearVisibleInstances();
        var previousScan = _scanCancellation;
        _scanCancellation = new CancellationTokenSource();
        previousScan?.Cancel();
        previousScan?.Dispose();
        var token = _scanCancellation.Token;
        try
        {
            Folders.Clear();
            var records = await Task.Run(_folders.Discover, token);
            var folderRows = new List<GameFolder>(records.Count);
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var scanned = await _scanner.ScanAsync(record.Path, token);
                folderRows.Add(new GameFolder(
                    record.Name,
                    record.Path,
                    scanned.Count(info => info.Kind != InstanceKind.Error),
                    record.IsDefault,
                    scanned.Count,
                    record.IsUserAdded));
            }

            foreach (var row in folderRows)
            {
                Folders.Add(row);
            }

            SelectedFolder =
                Folders.FirstOrDefault(f => previous is not null
                    && GameFolderService.PathsEqual(f.Path, previous))
                ?? Folders.FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            IsLoading = false;
        }

        // Selection is assigned while the rail is rebuilding, so its change
        // handler deliberately does not persist it. Save the restored path now;
        // if the old folder disappeared this also records the fallback choice.
        PersistSelectedFolderPath(SelectedFolder?.Path ?? "");

        await LoadInstancesAsync();
        if (previousInstanceId is not null)
        {
            var restored = FindInstance(previousInstanceId);
            if (restored is not null)
            {
                SelectedInstance = restored;
            }
            else if (_launch.SelectedInstanceId == previousInstanceId
                     && Folders.All(folder => !GameFolderService.PathsEqual(
                         System.IO.Path.GetDirectoryName(_launch.SelectedInstancePath ?? string.Empty) ?? string.Empty,
                         System.IO.Path.Combine(folder.Path, "versions"))))
            {
                _launch.ClearInstance();
            }
        }
    }

    private void PersistSelectedFolderPath(string path)
    {
        var saved = _settings.Load();
        if (GameFolderService.PathsEqual(saved.SelectedGameFolderPath, path)
            || (string.IsNullOrWhiteSpace(saved.SelectedGameFolderPath) && string.IsNullOrWhiteSpace(path)))
        {
            return;
        }

        saved.SelectedGameFolderPath = path;
        _settings.Save(saved);
    }

    /// <summary>
    /// Scans the selected folder and distributes the results into the three
    /// sections.
    /// </summary>
    /// <remarks>
    /// The grouping itself is PCL's, from <see cref="InstanceGrouper"/>. PCL shows
    /// six cards (收藏 / 可安装 Mod / 常规 / 不常用 / 愚人节 / 错误); this page's three
    /// sections are a fixed product decision, so the six map onto them: starred and
    /// moddable both land in 可安装模组 (a starred vanilla install still belongs to
    /// the user's shortlist), and April Fools and error cards go to 不常用 because
    /// neither is an everyday target.
    /// </remarks>
    public async Task LoadInstancesAsync()
    {
        // Selection changes and explicit refreshes can overlap. Cancel the older
        // walk so a slow disk cannot overwrite the newly-selected folder later.
        var previous = _scanCancellation;
        _scanCancellation = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        var token = _scanCancellation.Token;

        var folder = SelectedFolder;
        if (folder is null)
        {
            SelectedInstance = null;
            if (_launch.SelectedInstanceId is not null
                && _launch.SelectedInstancePath is not null
                && Folders.All(candidate => !GameFolderService.PathsEqual(
                    System.IO.Path.GetDirectoryName(_launch.SelectedInstancePath) ?? string.Empty,
                    System.IO.Path.Combine(candidate.Path, "versions"))))
            {
                _launch.ClearInstance();
            }

            ClearVisibleInstances();
            ApplyFilter();
            IsEmpty = true;
            return;
        }

        IsLoading = true;
        ClearVisibleInstances();
        try
        {
            _folders.EnsureGameFolder(folder.Path);
            var scanned = await _scanner.ScanAsync(folder.Path, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(folder, SelectedFolder))
            {
                return;
            }

            LoadScannedInstances(folder, scanned);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer folder selection or refresh owns the page now.
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearVisibleInstances()
    {
        _allModdable = [];
        _allRegular = [];
        _allRare = [];
        _allErrors = [];
        ModdableInstances.Clear();
        RegularInstances.Clear();
        RareInstances.Clear();
        ErrorInstances.Clear();
        IsEmpty = true;
    }

    private static InstanceItem CreateInstanceItem(InstanceInfo info)
        => new(
            info.Name,
            info.Path,
            GameVersionText(info),
            info.LoaderDisplay,
            info.IsModdable,
            InstanceIconUri.For(info.Kind),
            info.Kind == InstanceKind.Error ? info.Info : null,
            info.Kind is not (InstanceKind.Snapshot or InstanceKind.Old or InstanceKind.Fool or InstanceKind.Error),
            LoaderVersionText(info),
            info.VanillaName);

    private static string LoaderVersionText(InstanceInfo info)
    {
        var version = info.FabricVersion ?? info.ForgeVersion ?? info.NeoForgeVersion
            ?? info.OptiFineVersion;
        return string.IsNullOrWhiteSpace(version) ? info.LoaderDisplay : $"{info.LoaderDisplay} {version}";
    }

    private static string GameVersionText(InstanceInfo info)
    {
        var loaderSeparator = info.Info.IndexOf(", ", StringComparison.Ordinal);
        return loaderSeparator < 0 ? info.Info : info.Info[..loaderSeparator];
    }

    private void LoadScannedInstances(GameFolder folder, IReadOnlyList<InstanceInfo> scanned)
    {
        var previousSelectedId = _lastSelectedInstanceId ?? _launch.SelectedInstanceId;
        var previousSelectedPath = _lastSelectedInstancePath ?? _launch.SelectedInstancePath;
        SelectedInstance = null;
        var grouped = InstanceGrouper.Group(scanned);

        // The three lists are derived from these snapshots, so a search filters
        // the scan result rather than a previously filtered view.
        _allModdable = [];
        _allRegular = [];
        _allRare = [];
        _allErrors = [];
        foreach (var (type, items) in grouped)
        {
            foreach (var info in items)
            {
                var item = CreateInstanceItem(info);
                if (info.Kind == InstanceKind.Error) { _allErrors.Add(item); continue; }
                switch (type)
                {
                    case InstanceCardType.Api:
                    case InstanceCardType.Star:
                        _allModdable.Add(item);
                        break;

                    case InstanceCardType.OriginalLike:
                        _allRegular.Add(item);
                        break;

                    default:
                        // Rubbish, Fool and Error share the rarely-used section.
                        _allRare.Add(item);
                        break;
                }
            }
        }

        // Keep the rail's count in step with what the scan found.
        //
        // The row is replaced rather than mutated because GameFolder is an
        // immutable record. That is what can re-raise SelectionChanged; the
        // _isLoading guard in LoadInstances absorbs the resulting re-entry, so the
        // count update cannot clear the sections that were just filled.
        var index = Folders.IndexOf(folder);
        var validCount = scanned.Count(info => info.Kind != InstanceKind.Error);
        if (index >= 0 && (Folders[index].ScannedCount != scanned.Count || Folders[index].InstanceCount != validCount))
        {
            var currentRow = Folders[index];
            var updatedFolder = currentRow with
            {
                ScannedCount = scanned.Count,
                InstanceCount = validCount,
            };
            var isSelectedFolder = GameFolderService.PathsEqual(
                SelectedFolder?.Path ?? _selectedFolderPath ?? string.Empty,
                currentRow.Path);

            // Replace the bound row and rebind the selection by stable path. The
            // selection callback ignores same-path replacements, preventing a
            // duplicate scan while avoiding the ListView's stale-object deselection.
            Folders[index] = updatedFolder;
            if (isSelectedFolder)
            {
                SelectedFolder = updatedFolder;
                OnPropertyChanged(nameof(FolderSelectionPath));
            }
        }

        IsEmpty = _allModdable.Count + _allRegular.Count + _allRare.Count + _allErrors.Count == 0;
        ApplyFilter();

        var restoredSelection = AvailableInstances
            .FirstOrDefault(item => item.Id == previousSelectedId);
        if (restoredSelection is not null)
        {
            SelectedInstance = restoredSelection;
        }
        else if (previousSelectedId is not null
                 && (_launch.SelectedInstanceId is null || GameFolderService.PathsEqual(
                     System.IO.Path.GetDirectoryName(previousSelectedPath ?? string.Empty) ?? string.Empty,
                     System.IO.Path.Combine(folder.Path, "versions"))))
        {
            ForgetDeletedInstance(previousSelectedId);
            Log.Warning("上次选中的实例已不存在，已清除选择：{Path}", previousSelectedPath);
        }
        else if (_launch.SelectedInstanceId is null)
        {
            SelectedInstance = _allModdable
                .Concat(_allRegular)
                .Concat(_allRare)
                .FirstOrDefault();
        }
        else if (GameFolderService.PathsEqual(
                     System.IO.Path.GetDirectoryName(_launch.SelectedInstancePath ?? string.Empty) ?? string.Empty,
                     System.IO.Path.Combine(folder.Path, "versions")))
        {
            // The selected instance disappeared from the folder being rescanned.
            // Clear the stale launch-page identity rather than pointing settings at
            // a different same-named instance.
            _launch.ClearInstance();
        }

        Log.Information(
            "已加载实例：{Folder}，可识别 {Count} 个，错误 {Errors} 个",
            folder.Path,
            validCount,
            _allErrors.Count);
    }

    /// <summary>
    /// Pushes the current search term into the three visible collections.
    /// </summary>
    /// <remarks>
    /// Filtering by rebuilding the collections rather than hiding item containers
    /// means the section counts and the visible rows cannot disagree, and it keeps
    /// the page free of any dependency on the ListView's internal containers.
    /// </remarks>
    private void ApplyFilter()
    {
        var term = SearchText.Trim();

        FilterInto(ModdableInstances, _allModdable, term);
        FilterInto(RegularInstances, _allRegular, term);
        FilterInto(RareInstances, _allRare, term);
        FilterInto(ErrorInstances, _allErrors, term);
    }

    // Unfiltered scan results. Kept separate from the bound collections so that
    // clearing the search restores exactly what was scanned.
    private List<InstanceItem> _allModdable = [];
    private List<InstanceItem> _allRegular = [];
    private List<InstanceItem> _allRare = [];
    private List<InstanceItem> _allErrors = [];

    private static void FilterInto(
        ObservableCollection<InstanceItem> target,
        IReadOnlyList<InstanceItem> source,
        string term)
    {
        var matches = term.Length == 0
            ? source
            : source.Where(i =>
                i.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || i.GameVersion.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || i.Loader.Contains(term, StringComparison.CurrentCultureIgnoreCase));

        Replace(target, matches);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>
/// A game folder row in the left rail.
/// </summary>
public sealed record GameFolder(
    string Name,
    string Path,
    int InstanceCount,
    bool IsDefault = false,
    int? ScannedCount = null,
    bool IsUserAdded = false)
{
    /// <summary>Secondary line in the folder list: where it lives.</summary>
    public string PathDisplay => Path;

    /// <summary>Trailing count badge; hidden for zero so empty folders stay clean.</summary>
    public string CountDisplay => (ScannedCount ?? InstanceCount).ToString();

    /// <summary>
    /// Whether the folder was added by the user, which is what allows renaming it
    /// or removing it from the list.
    /// </summary>
    /// <remarks>
    /// Autodiscovered folders are re-found on every scan, so offering "remove"
    /// would be a lie: it would reappear on the next refresh. Only user-added
    /// folders are stored in the settings file and can therefore be forgotten.
    /// </remarks>
    public bool IsCustom => IsUserAdded;
}

/// <summary>
/// One instance card.
/// </summary>
/// <param name="GameVersion">
/// Chip text: the Minecraft version. For an unreadable instance this carries the
/// reason instead, which is more useful than an empty chip.
/// </param>
public sealed record InstanceItem(
    string Name,
    string Path,
    string GameVersion,
    string Loader,
    bool IsModdable,
    string IconPath,
    string? Error = null,
    bool IsRelease = true,
    string? LoaderVersionDisplay = null,
    string MinecraftVersionId = "")
{
    /// <summary>
    /// Stable identity for the instance, independent of its display fields.
    /// </summary>
    /// <remarks>
    /// A record compares by value, so two instances with identical fields would
    /// be indistinguishable to the selection logic. The id keeps a card's
    /// identity stable when its name or loader is edited later.
    /// </remarks>
    public string Id { get; init; } = Launcher.Core.Instances.InstanceIdentity.ForPath(Path);

    /// <summary>
    /// Chip text: the loader when there is one, otherwise the version type.
    /// </summary>
    public string LoaderDisplay => Loader;

    /// <summary>
    /// Alias kept for the launch page and the settings dialog, which read the
    /// loader chip under its original name.
    /// </summary>
    public string LoaderVersion => LoaderVersionDisplay ?? Loader;

    /// <summary>Secondary chip text: always the Minecraft version.</summary>
    public string GameVersionDisplay => GameVersion;

    /// <summary>True when the version could not be read, so the card can warn.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);
}
