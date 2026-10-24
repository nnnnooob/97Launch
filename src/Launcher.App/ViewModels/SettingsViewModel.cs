using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;

namespace Launcher.App.ViewModels;

/// <summary>
/// The settings categories shown in the left rail.
/// </summary>
public enum SettingsSection
{
    Launch,
    Java,
    GameDefaults,
    Manage,
    About,
    Update,
}

/// <summary>
/// A detected Java runtime.
/// </summary>
public sealed record JavaRuntime(
    string Name,
    string Version,
    string Vendor,
    string Architecture,
    string Path,
    bool IsManual)
{
    public string DirectoryPath => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string SourceText => IsManual ? "手动添加"
        : Path.Contains("\\.97Launch\\runtimes\\", StringComparison.OrdinalIgnoreCase)
            ? "启动器下载" : "自动发现";
    public string MetadataText => $"{Vendor} · {Architecture} · {SourceText}";
}

/// <summary>
/// Settings page state: the selected category and every option value.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    // ======== category ========

    [ObservableProperty]
    private SettingsSection _section = SettingsSection.Launch;

    public bool IsLaunch => Section == SettingsSection.Launch;

    public bool IsJava => Section == SettingsSection.Java;
    public bool IsGameDefaults => Section == SettingsSection.GameDefaults;

    public bool IsManage => Section == SettingsSection.Manage;

    public bool IsAbout => Section == SettingsSection.About;

    public bool IsUpdate => Section == SettingsSection.Update;

    partial void OnSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsLaunch));
        OnPropertyChanged(nameof(IsJava));
        OnPropertyChanged(nameof(IsGameDefaults));
        OnPropertyChanged(nameof(IsManage));
        OnPropertyChanged(nameof(IsAbout));
        OnPropertyChanged(nameof(IsUpdate));
    }

    // ======== 启动 · 启动选项 ========

    [ObservableProperty]
    private string _versionIsolation = "隔离所有版本";

    [ObservableProperty]
    private string _windowTitle = "";

    [ObservableProperty]
    private string _processPriority = "中（平衡）";

    [ObservableProperty]
    private string _ipPreference = "IPv4 优先";

    /// <summary>
    /// Window size: either one of the presets, or "自定义" to use the two numbers.
    /// </summary>
    [ObservableProperty]
    private string _windowSize = "默认";

    /// <summary>
    /// Custom size as text, so the box can be edited freely without an int
    /// converter or fighting the slider on every keystroke.
    /// </summary>
    [ObservableProperty]
    private string _windowWidth = "854";

    [ObservableProperty]
    private string _windowHeight = "480";

    public bool IsCustomWindowSize => WindowSize == "自定义";

    /// <summary>
    /// Slider runs the opposite way from the auto radio, so the custom slider
    /// needs the negated flag rather than a converter.
    /// </summary>
    public bool IsCustomMemory => !IsAutoMemory;

    partial void OnIsAutoMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustomMemory));
        Persist();
        RefreshMemoryPreview();
    }

    partial void OnWindowSizeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomWindowSize));
        Persist();
    }

    public IReadOnlyList<string> VersionIsolations { get; } =
    [
        "关闭",
        "隔离可安装 Mod 的版本",
        "隔离非正式版",
        "隔离可安装 Mod 的版本与非正式版",
        "隔离所有版本",
    ];

    public IReadOnlyList<string> ProcessPriorities { get; } =
    [
        "高（优先保证游戏运行）",
        "中（平衡）",
        "低（优先保证其他程序运行）",
    ];

    public IReadOnlyList<string> IpPreferences { get; } = ["IPv4 优先", "IPv6 优先", "系统默认"];

    public IReadOnlyList<string> WindowSizes { get; } = ["默认", "854 × 480", "1280 × 720", "1920 × 1080", "自定义"];

    // ======== 启动 · 游戏内存 ========

    /// <summary>
    /// True for automatic allocation, false for the custom slider.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoMemory = true;

    [ObservableProperty]
    private double _customMemoryGb = 3.0;

    [ObservableProperty]
    private double _maxMemoryGb = 16.0;

    private InstanceItem? _memoryInstance;
    private MemoryAllocationPlan? _memoryPreview;
    private MemoryAllocationPlan? _actualMemoryPreview;
    public double AutoMemoryGb => _memoryPreview?.HeapGb ?? 0;
    public string AutoMemoryDisplay => _memoryPreview is null ? "自动配置（等待内存信息）" : $"自动配置（预计 {AutoMemoryGb:0.0} GB）";
    public string MemoryHint => (_actualMemoryPreview?.Summary ?? "暂时无法读取内存信息") + "。自动模式在启动时重新估算。";

    public void SetMemoryInstance(InstanceItem? instance)
    {
        _memoryInstance = instance;
        RefreshMemoryPreview();
    }

    public void RefreshMemoryPreview()
    {
        try
        {
            var global = _store?.Load() ?? new LauncherSettings();
            global.MaxMemoryGb = MaxMemoryGb;
            var options = _memoryInstance is null ? InstanceLaunchSettings.From(global)
                : new InstanceLaunchSettingsStore().Load(_memoryInstance.Path, global);
            options.IsAutoMemory = true;
            _memoryPreview = MemoryAllocationService.Calculate(_memoryInstance, options, global);
            options.IsAutoMemory = IsAutoMemory;
            options.CustomMemoryGb = CustomMemoryGb;
            _actualMemoryPreview = IsAutoMemory ? _memoryPreview : MemoryAllocationService.Calculate(_memoryInstance, options, global);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { _memoryPreview = _actualMemoryPreview = null; }
        OnPropertyChanged(nameof(AutoMemoryGb));
        OnPropertyChanged(nameof(AutoMemoryDisplay));
        OnPropertyChanged(nameof(MemoryHint));
    }

    public string CustomMemoryDisplay => $"{CustomMemoryGb:0.0} GB";

    partial void OnMaxMemoryGbChanged(double value)
    {
        Persist();
        RefreshMemoryPreview();
    }

    partial void OnCustomMemoryGbChanged(double value)
    {
        OnPropertyChanged(nameof(CustomMemoryDisplay));
        Persist();
        RefreshMemoryPreview();
    }

    // ======== 启动 · 高级启动选项 ========

    [ObservableProperty]
    private string _jvmArgsHead = "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True";

    [ObservableProperty]
    private string _gameArgsTail = "";

    [ObservableProperty]
    private string _preLaunchCommand = "";

    [ObservableProperty]
    private bool _requireHighPerformanceGpu = true;

    [ObservableProperty]
    private bool _useJavaExe;

    // ======== Java ========

    [ObservableProperty]
    private JavaRuntime? _selectedJava;

    [ObservableProperty]
    private string _javaStatus = "尚未搜索 Java";

    [ObservableProperty]
    private bool _isJavaBusy;

    [ObservableProperty]
    private string _selectedJavaPath = "";

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];

    private List<string> _additionalJavaPaths = [];
    private readonly SemaphoreSlim _javaRefreshLock = new(1, 1);

    public SettingsViewModel()
    {
        // Pull the persisted values in. Assigning through the properties rather
        // than the backing fields keeps the change handlers in play, which is what
        // publishes the loaded values to anything already subscribed.
        var store = AppServiceProvider.Current.GetService(typeof(SettingsStore)) as SettingsStore;
        if (store is null)
        {
            return;
        }

        _store = store;
        if (AppServiceProvider.Current.GetService(typeof(JavaRuntimeChangeNotifier)) is JavaRuntimeChangeNotifier runtimeChanges)
            runtimeChanges.RuntimesChanged += OnJavaRuntimesChanged;
        var saved = store.Load();

        _selectNewInstance = saved.SelectNewInstance;
        _fileDownloadSource = saved.FileDownloadSource;
        _versionDownloadSource = saved.VersionDownloadSource;
        _maxThreads = saved.DownloadThreads;
        _speedLimitKb = saved.DownloadSpeedLimitKb;
        _communitySource = saved.CommunitySource;
        _fileNameFormat = Launcher.Core.Downloads.ResourceFileName.Examples.Contains(saved.ResourceFileNameFormat)
            ? saved.ResourceFileNameFormat : Launcher.Core.Downloads.ResourceFileName.Default;
        _versionIsolation = saved.VersionIsolation;
        _windowTitle = saved.WindowTitle;
        _processPriority = saved.ProcessPriority;
        _ipPreference = saved.IpPreference;
        _windowSize = saved.WindowSize;
        _windowWidth = saved.WindowWidth;
        _windowHeight = saved.WindowHeight;
        _isAutoMemory = saved.IsAutoMemory;
        _customMemoryGb = saved.CustomMemoryGb;
        _maxMemoryGb = saved.MaxMemoryGb;
        _jvmArgsHead = saved.JvmArgsHead;
        _gameArgsTail = saved.GameArgsTail;
        _preLaunchCommand = saved.PreLaunchCommand;
        _requireHighPerformanceGpu = saved.RequireHighPerformanceGpu;
        _useJavaExe = saved.UseJavaExe;
        _autoSetChinese = saved.AutoSetChinese;
        _selectedJavaPath = saved.SelectedJavaPath;
        _additionalJavaPaths = saved.AdditionalJavaPaths ?? [];
        RefreshMemoryPreview();
    }

    private void OnJavaRuntimesChanged(object? sender, EventArgs e)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess)
            _ = RefreshJavaAsync();
        else
            dispatcher.TryEnqueue(() => _ = RefreshJavaAsync());
    }

    partial void OnSelectedJavaChanged(JavaRuntime? value)
    {
        OnPropertyChanged(nameof(CanRemoveSelectedJava));
        OnPropertyChanged(nameof(HasSelectedJava));
        OnPropertyChanged(nameof(SelectedJavaName));
        OnPropertyChanged(nameof(SelectedJavaMetadata));
        OnPropertyChanged(nameof(SelectedJavaExecutable));
        if (value is null) return;
        SelectedJavaPath = value.Path;
        Persist();
    }

    public bool CanRemoveSelectedJava => SelectedJava?.IsManual == true && !IsJavaBusy;
    public bool HasSelectedJava => SelectedJava is not null;
    public bool HasJavaRuntimes => JavaRuntimes.Count > 0;
    public bool ShowJavaEmpty => !IsJavaBusy && !HasJavaRuntimes;
    public bool CanManageJava => !IsJavaBusy;
    public string SelectedJavaName => SelectedJava?.Name ?? "选择一个 Java 查看详情";
    public string SelectedJavaMetadata => SelectedJava?.MetadataText ?? "启动游戏时会按实例要求自动选择兼容版本。";
    public string SelectedJavaExecutable => SelectedJava?.Path ?? "";

    partial void OnIsJavaBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanManageJava));
        OnPropertyChanged(nameof(CanRemoveSelectedJava));
        OnPropertyChanged(nameof(ShowJavaEmpty));
    }

    public async Task RefreshJavaAsync(CancellationToken cancellationToken = default)
    {
        await _javaRefreshLock.WaitAsync(cancellationToken);
        IsJavaBusy = true;
        Serilog.Log.Information("开始搜索 Java 运行环境");
        JavaStatus = "正在搜索并验证 Java…";
        try
        {
            var detected = await JavaRuntimeDetector.DetectAsync(
                _additionalJavaPaths,
                cancellationToken,
                _store?.Load().ManagedJavaRuntimeDirectories,
                _store?.Load().SelectedGameFolderPath);
            var selectedPath = SelectedJavaPath;
            JavaRuntimes.Clear();
            foreach (var runtime in detected)
            {
                JavaRuntimes.Add(new JavaRuntime(
                    runtime.Name,
                    runtime.Version,
                    runtime.Vendor,
                    runtime.Architecture,
                    runtime.Path,
                    runtime.IsManual));
            }

            SelectedJava = JavaRuntimes.FirstOrDefault(item =>
                JavaRuntimeDetector.IsSameRuntime(item.Path, selectedPath));

            if (SelectedJava is null && string.IsNullOrWhiteSpace(SelectedJavaPath) && JavaRuntimes.Count > 0)
            {
                SelectedJava = JavaRuntimes[0];
            }

            JavaStatus = SelectedJava is null && !string.IsNullOrWhiteSpace(SelectedJavaPath)
                ? $"已选 Java 不可用 · {JavaRuntimes.Count} 个可用版本"
                : JavaRuntimes.Count > 0
                    ? $"检测到 {JavaRuntimes.Count} 个 Java 运行环境"
                    : "未找到 Java，可手动添加 java.exe";
            Serilog.Log.Information("Java 搜索完成：{Count} 个可用运行环境", JavaRuntimes.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            JavaStatus = "Java 搜索已取消";
            Serilog.Log.Information("Java 搜索已取消");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "搜索 Java 失败");
            JavaStatus = "搜索失败，请重新搜索或手动添加 Java";
        }
        finally
        {
            OnPropertyChanged(nameof(HasJavaRuntimes));
            IsJavaBusy = false;
            _javaRefreshLock.Release();
        }
    }

    public async Task<bool> AddJavaAsync(string path, CancellationToken cancellationToken = default)
    {
        var runtime = await JavaRuntimeDetector.ProbeAsync(path, isManual: true, cancellationToken);
        if (runtime is null)
        {
            Serilog.Log.Warning("手动添加 Java 失败：所选文件无法识别为可用运行环境");
            JavaStatus = "所选文件无法识别为可用的 Java 运行环境";
            return false;
        }

        if (!_additionalJavaPaths.Any(path => JavaRuntimeDetector.IsSameRuntime(path, runtime.Path)))
        {
            _additionalJavaPaths.Add(runtime.Path);
        }

        SelectedJavaPath = runtime.Path;
        Persist();
        await RefreshJavaAsync(cancellationToken);
        SelectedJava = JavaRuntimes.FirstOrDefault(item =>
            JavaRuntimeDetector.IsSameRuntime(item.Path, runtime.Path));
        return SelectedJava is not null;
    }

    public async Task RemoveSelectedJavaAsync()
    {
        if (SelectedJava is not { IsManual: true } runtime) return;

        _additionalJavaPaths.RemoveAll(path =>
            JavaRuntimeDetector.IsSameRuntime(path, runtime.Path));
        if (JavaRuntimeDetector.IsSameRuntime(SelectedJavaPath, runtime.Path))
        {
            SelectedJavaPath = "";
        }
        SelectedJava = null;
        Persist();
        await RefreshJavaAsync();
    }

    private readonly SettingsStore? _store;

    /// <summary>
    /// Writes the settings that have a consumer elsewhere.
    /// </summary>
    /// <remarks>
    /// Saving on every change instead of on window close means a crash or a
    /// force-quit cannot lose a preference, and the download service always sees
    /// the current value. The file is a few hundred bytes, so the cost is nil.
    /// </remarks>
    private void Persist()
    {
        if (_store is null) return;
        var settings = _store.Load();
        settings.SelectNewInstance = SelectNewInstance;
        settings.FileDownloadSource = FileDownloadSource;
        settings.VersionDownloadSource = VersionDownloadSource;
        settings.DownloadThreads = MaxThreads;
        settings.DownloadSpeedLimitKb = SpeedLimitKb;
        settings.CommunitySource = CommunitySource;
        settings.ResourceFileNameFormat = FileNameFormat;
        settings.VersionIsolation = VersionIsolation;
        settings.WindowTitle = WindowTitle;
        settings.ProcessPriority = ProcessPriority;
        settings.IpPreference = IpPreference;
        settings.WindowSize = WindowSize;
        settings.WindowWidth = WindowWidth;
        settings.WindowHeight = WindowHeight;
        settings.IsAutoMemory = IsAutoMemory;
        settings.CustomMemoryGb = CustomMemoryGb;
        settings.MaxMemoryGb = MaxMemoryGb;
        settings.JvmArgsHead = JvmArgsHead;
        settings.GameArgsTail = GameArgsTail;
        settings.PreLaunchCommand = PreLaunchCommand;
        settings.RequireHighPerformanceGpu = RequireHighPerformanceGpu;
        settings.UseJavaExe = UseJavaExe;
        settings.AutoSetChinese = AutoSetChinese;
        settings.SelectedJavaPath = SelectedJavaPath;
        settings.AdditionalJavaPaths = [.. _additionalJavaPaths];
        _store.Save(settings);
    }

    partial void OnSelectNewInstanceChanged(bool value) => Persist();

    partial void OnFileDownloadSourceChanged(string value) => Persist();

    partial void OnVersionDownloadSourceChanged(string value) => Persist();

    partial void OnCommunitySourceChanged(string value) => Persist();
    partial void OnFileNameFormatChanged(string value) => Persist();

    partial void OnVersionIsolationChanged(string value)
    {
        Persist();
        RefreshMemoryPreview();
    }

    partial void OnWindowTitleChanged(string value) => Persist();
    partial void OnProcessPriorityChanged(string value) => Persist();
    partial void OnIpPreferenceChanged(string value) => Persist();
    partial void OnWindowWidthChanged(string value) => Persist();
    partial void OnWindowHeightChanged(string value) => Persist();
    partial void OnJvmArgsHeadChanged(string value) => Persist();
    partial void OnGameArgsTailChanged(string value) => Persist();
    partial void OnPreLaunchCommandChanged(string value) => Persist();
    partial void OnRequireHighPerformanceGpuChanged(bool value) => Persist();
    partial void OnUseJavaExeChanged(bool value) => Persist();
    partial void OnAutoSetChineseChanged(bool value) => Persist();

    // ======== 管理 · 游戏资源获取偏好 ========

    [ObservableProperty]
    private string _fileDownloadSource = "自动选择";

    [ObservableProperty]
    private string _versionDownloadSource = "自动选择";

    [ObservableProperty]
    private int _maxThreads = 64;

    [ObservableProperty]
    private int _speedLimitKb = 0;

    /// <summary>
    /// Speed limit as a readable string; zero means unlimited.
    /// </summary>
    public string SpeedLimitText => SpeedLimitKb <= 0 ? "不限速" : $"{SpeedLimitKb} KB/s";

    partial void OnSpeedLimitKbChanged(int value)
    {
        OnPropertyChanged(nameof(SpeedLimitText));
        Persist();
    }

    partial void OnMaxThreadsChanged(int value) => Persist();

    [ObservableProperty]
    private bool _selectNewInstance = true;

    [ObservableProperty]
    private bool _upgradeAuthlib;

    public IReadOnlyList<string> FileSources { get; } =
        ["自动选择", "官方源", "BMCLAPI", "Mojang 官方 + BMCLAPI 回退"];

    public IReadOnlyList<string> VersionSources { get; } =
        ["自动选择", "官方源", "BMCLAPI", "Minecraft Wiki"];

    // ======== 管理 · 社区资源获取偏好 ========

    [ObservableProperty]
    private string _communitySource = "自动选择";

    [ObservableProperty]
    private string _fileNameFormat = Launcher.Core.Downloads.ResourceFileName.Default;

    public IReadOnlyList<string> CommunitySources { get; } =
        ["自动选择", "Modrinth", "CurseForge"];

    public IReadOnlyList<string> FileNameFormats { get; } =
        Launcher.Core.Downloads.ResourceFileName.Examples;

    // ======== 管理 · 辅助功能 ========

    [ObservableProperty]
    private bool _autoSetChinese = true;

    // ======== 关于 ========

    public string VersionDisplay => AppInfo.Version;

    public string DataDirectory => AppInfo.DataDirectory;

    public string LogDirectory => AppInfo.LogDirectory;
}
