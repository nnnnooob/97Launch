using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;

namespace Launcher.App.ViewModels;

/// <summary>
/// Left rail of the instance settings dialog.
/// </summary>
public enum InstanceSection
{
    Overview,
    Settings,
    Modify,
    Export,
    Saves,
    Screenshots,
    Mods,
    ResourcePacks,
    ShaderPacks,
}

/// <summary>
/// Per-instance settings. Mirrors the global settings structure but every value
/// applies to one instance only.
/// </summary>
public partial class InstanceSettingsViewModel : ObservableObject
{
    private readonly LogBuffer _log;
    private readonly SettingsStore _settingsStore;
    private readonly InstanceLaunchSettingsStore _instanceSettingsStore;
    private readonly SettingsViewModel _settingsViewModel;
    private string? _instancePath;
    private InstanceItem? _memoryInstance;
    private MemoryAllocationPlan? _memoryPreview;
    private MemoryAllocationPlan? _actualMemoryPreview;
    private bool _loadingSettings;

    public InstanceSettingsViewModel(
        LogBuffer log,
        SettingsStore settingsStore,
        InstanceLaunchSettingsStore instanceSettingsStore,
        SettingsViewModel settingsViewModel)
    {
        _log = log;
        _settingsStore = settingsStore;
        _instanceSettingsStore = instanceSettingsStore;
        _settingsViewModel = settingsViewModel;
    }

    // ======== section ========

    [ObservableProperty]
    private InstanceSection _section = InstanceSection.Settings;

    public bool IsOverview => Section == InstanceSection.Overview;
    public bool IsSettings => Section == InstanceSection.Settings;
    public bool IsModify => Section == InstanceSection.Modify;
    public bool IsExport => Section == InstanceSection.Export;
    public bool IsSaves => Section == InstanceSection.Saves;
    public bool IsScreenshots => Section == InstanceSection.Screenshots;
    public bool IsMods => Section == InstanceSection.Mods;
    public bool IsResourcePacks => Section == InstanceSection.ResourcePacks;
    public bool IsShaderPacks => Section == InstanceSection.ShaderPacks;
    public bool IsResources => Section is InstanceSection.Mods or InstanceSection.Saves or InstanceSection.Screenshots or InstanceSection.ResourcePacks or InstanceSection.ShaderPacks;

    partial void OnSectionChanged(InstanceSection value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsModify));
        OnPropertyChanged(nameof(IsExport));
        OnPropertyChanged(nameof(IsSaves));
        OnPropertyChanged(nameof(IsScreenshots));
        OnPropertyChanged(nameof(IsMods));
        OnPropertyChanged(nameof(IsResourcePacks));
        OnPropertyChanged(nameof(IsShaderPacks));
        OnPropertyChanged(nameof(IsResources));
        OnPropertyChanged(nameof(SectionTitle));
    }

    /// <summary>
    /// Page title shown at the top of the right pane.
    /// </summary>
    public string SectionTitle => Section switch
    {
        InstanceSection.Settings => "设置",
        InstanceSection.Modify => "修改",
        InstanceSection.Export => "导出",
        InstanceSection.Saves => "存档",
        InstanceSection.Screenshots => "截图",
        InstanceSection.Mods => "Mod",
        InstanceSection.ResourcePacks => "资源包",
        InstanceSection.ShaderPacks => "光影包",
        _ => "概览",
    };

    // ======== instance identity ========

    [ObservableProperty]
    private string _instanceName = "";

    [ObservableProperty]
    private string _gameVersion = "";

    [ObservableProperty]
    private string _loaderVersion = "";

    [ObservableProperty]
    private int _launchCount;

    [ObservableProperty]
    private string _iconPath = "ms-appx:///Assets/Blocks/vanilla.png";

    public string LaunchCountText => $"启动 {LaunchCount} 次";

    partial void OnLaunchCountChanged(int value) => OnPropertyChanged(nameof(LaunchCountText));

    /// <summary>
    /// Loads the instance this dialog was opened for.
    /// </summary>
    public void Load(InstanceItem instance)
    {
        _memoryInstance = instance;
        InstanceName = instance.Name;
        GameVersion = instance.GameVersion;
        LoaderVersion = instance.LoaderVersion;
        IconPath = instance.IconPath;
        _instancePath = instance.Path;
        LoadLaunchSettings();
        _ = RefreshJavaRuntimesAsync();
        Reload();
        RefreshMemoryPreview();
    }

    public ObservableCollection<JavaRuntime> AvailableJavaRuntimes => _settingsViewModel.JavaRuntimes;

    public IReadOnlyList<string> JavaModes { get; } =
    [
        "自动选择并按需下载",
        "使用实例文件夹中的 Java",
        "强制指定 Java",
    ];

    public bool IsForcedJava => JavaMode == "强制指定 Java";

    public string JavaModeHint => JavaMode switch
    {
        "使用实例文件夹中的 Java" => "只在此实例目录中查找，不会自动下载。",
        "强制指定 Java" => "固定使用下方选择的 Java；不可用时会提示。",
        _ => "按当前 Minecraft 与加载器要求选择；缺少兼容版本时自动下载。",
    };

    private async Task RefreshJavaRuntimesAsync()
    {
        var instancePath = _instancePath;
        var selectedPath = JavaPath;
        try
        {
            await _settingsViewModel.RefreshJavaAsync();
            if (_instancePath != instancePath) return;
            // Refreshing the ComboBox items can temporarily clear SelectedValue.
            var desiredPath = string.IsNullOrWhiteSpace(JavaPath) ? selectedPath : JavaPath;
            var runtime = AvailableJavaRuntimes.FirstOrDefault(item =>
                JavaRuntimeDetector.IsSameRuntime(item.Path, desiredPath));
            JavaPath = runtime?.Path ?? desiredPath;
        }
        catch (Exception ex) { _log.Append($"刷新 Java 列表失败：{ex.Message}", LogEntryKind.Warning); }
    }

    private void LoadLaunchSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _instanceSettingsStore.Load(_instancePath!, _settingsStore.Load());
            UseGlobalLaunchOptions = settings.UseGlobalLaunchOptions;
            UseGlobalMemory = settings.UseGlobalMemory;
            UseGlobalAdvancedOptions = settings.UseGlobalAdvancedOptions;
            VersionIsolation = settings.VersionIsolation;
            WindowTitle = settings.WindowTitle;
            ProcessPriority = settings.ProcessPriority;
            IpPreference = settings.IpPreference;
            WindowSize = settings.WindowSize;
            IsAutoMemory = settings.IsAutoMemory;
            CustomMemoryGb = settings.CustomMemoryGb;
            JvmArgsHead = settings.JvmArgsHead;
            GameArgsTail = settings.GameArgsTail;
            PreLaunchCommand = settings.PreLaunchCommand;
            RequireHighPerformanceGpu = settings.RequireHighPerformanceGpu;
            UseJavaExe = settings.UseJavaExe;
            JavaMode = settings.JavaMode;
            JavaPath = settings.JavaPath;
            OnPropertyChanged(nameof(IsCustomMemory));
            OnPropertyChanged(nameof(CustomMemoryDisplay));
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void PersistLaunchSettings()
    {
        if (_loadingSettings || string.IsNullOrWhiteSpace(_instancePath)) return;
        _instanceSettingsStore.Save(_instancePath, new InstanceLaunchSettings
        {
            VersionIsolation = VersionIsolation,
            UseGlobalLaunchOptions = UseGlobalLaunchOptions,
            UseGlobalMemory = UseGlobalMemory,
            UseGlobalAdvancedOptions = UseGlobalAdvancedOptions,
            WindowTitle = WindowTitle,
            ProcessPriority = ProcessPriority,
            IpPreference = IpPreference,
            WindowSize = WindowSize,
            IsAutoMemory = IsAutoMemory,
            CustomMemoryGb = CustomMemoryGb,
            JvmArgsHead = JvmArgsHead,
            GameArgsTail = GameArgsTail,
            PreLaunchCommand = PreLaunchCommand,
            RequireHighPerformanceGpu = RequireHighPerformanceGpu,
            UseJavaExe = UseJavaExe,
            JavaMode = JavaMode,
            JavaPath = JavaPath,
        });
    }

    /// <summary>
    /// Resets every per-open value. Called each time the dialog opens so a stale
    /// search box, leftover selection or previous instance's details never leak
    /// into the next open.
    /// </summary>
    public void Reload()
    {
        Section = InstanceSection.Settings;
        LaunchCount = string.IsNullOrWhiteSpace(_instancePath) ? 0 : InstanceUsageStore.Read(_instancePath).LaunchCount;
    }

    // ======== 启动选项 ========

    [ObservableProperty]
    private bool _useGlobalLaunchOptions = true;

    [ObservableProperty]
    private bool _useGlobalMemory = true;

    [ObservableProperty]
    private bool _useGlobalAdvancedOptions = true;

    [ObservableProperty]
    private string _versionIsolation = "隔离可安装 Mod 的版本";

    [ObservableProperty]
    private string _windowTitle = "";

    [ObservableProperty]
    private string _processPriority = "中（平衡）";

    [ObservableProperty]
    private string _ipPreference = "IPv4 优先";

    [ObservableProperty]
    private string _windowSize = "默认";

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

    public IReadOnlyList<string> WindowSizes { get; } = ["默认", "854 × 480", "1280 × 720", "1920 × 1080"];

    // ======== 游戏内存 ========

    [ObservableProperty]
    private bool _isAutoMemory = true;

    [ObservableProperty]
    private double _customMemoryGb = 4.0;

    public bool IsCustomMemory => !IsAutoMemory;

    public string CustomMemoryDisplay => $"{CustomMemoryGb:0.0} GB";

    public string AutoMemoryDisplay => _memoryPreview is null ? "自动配置（等待内存信息）" : $"自动配置（预计 {_memoryPreview.HeapGb:0.0} GB）";
    public string MemoryHint => (_actualMemoryPreview?.Summary ?? "暂时无法读取内存信息") + "。自动模式在启动时重新估算。";
    public double MaximumMemoryGb => _settingsViewModel.MaxMemoryGb;

    public void RefreshMemoryPreview()
    {
        try
        {
            var global = _settingsStore.Load();
            var options = _instancePath is null ? InstanceLaunchSettings.From(global) : _instanceSettingsStore.Load(_instancePath, global);
            var automatic = options.IsAutoMemory;
            options.IsAutoMemory = true;
            _memoryPreview = MemoryAllocationService.Calculate(_memoryInstance, options, global);
            options.IsAutoMemory = automatic;
            _actualMemoryPreview = automatic ? _memoryPreview : MemoryAllocationService.Calculate(_memoryInstance, options, global);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { _memoryPreview = _actualMemoryPreview = null; }
        OnPropertyChanged(nameof(AutoMemoryDisplay));
        OnPropertyChanged(nameof(MemoryHint));
        OnPropertyChanged(nameof(MaximumMemoryGb));
    }

    partial void OnIsAutoMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustomMemory));
        OverrideMemory();
    }

    partial void OnCustomMemoryGbChanged(double value)
    {
        OnPropertyChanged(nameof(CustomMemoryDisplay));
        OverrideMemory();
    }

    partial void OnVersionIsolationChanged(string value) => OverrideLaunchOptions();
    partial void OnWindowTitleChanged(string value) => OverrideLaunchOptions();
    partial void OnProcessPriorityChanged(string value) => OverrideLaunchOptions();
    partial void OnIpPreferenceChanged(string value) => OverrideLaunchOptions();
    partial void OnWindowSizeChanged(string value) => OverrideLaunchOptions();
    partial void OnJvmArgsHeadChanged(string value) => OverrideAdvancedOptions();
    partial void OnGameArgsTailChanged(string value) => OverrideAdvancedOptions();
    partial void OnPreLaunchCommandChanged(string value) => OverrideAdvancedOptions();
    partial void OnRequireHighPerformanceGpuChanged(bool value) => OverrideAdvancedOptions();
    partial void OnUseJavaExeChanged(bool value) => OverrideAdvancedOptions();

    partial void OnUseGlobalLaunchOptionsChanged(bool value) => GlobalModeChanged(value);
    partial void OnUseGlobalMemoryChanged(bool value) => GlobalModeChanged(value);
    partial void OnUseGlobalAdvancedOptionsChanged(bool value) => GlobalModeChanged(value);

    private void OverrideLaunchOptions()
    {
        if (_loadingSettings) return;
        UseGlobalLaunchOptions = false;
        PersistLaunchSettings();
        RefreshMemoryPreview();
    }

    private void OverrideMemory()
    {
        if (_loadingSettings) return;
        UseGlobalMemory = false;
        PersistLaunchSettings();
        RefreshMemoryPreview();
    }

    private void OverrideAdvancedOptions()
    {
        if (_loadingSettings) return;
        UseGlobalAdvancedOptions = false;
        PersistLaunchSettings();
    }

    private void GlobalModeChanged(bool value)
    {
        if (_loadingSettings) return;
        PersistLaunchSettings();
        if (value) LoadLaunchSettings();
        RefreshMemoryPreview();
    }

    // ======== 高级启动选项 ========

    [ObservableProperty]
    private string _jvmArgsHead = "-XX:+UseG1GC";

    [ObservableProperty]
    private string _gameArgsTail = "";

    [ObservableProperty]
    private string _preLaunchCommand = "";

    [ObservableProperty]
    private bool _requireHighPerformanceGpu;

    [ObservableProperty]
    private bool _useJavaExe;

    [ObservableProperty]
    private string _javaMode = "自动选择并按需下载";

    [ObservableProperty]
    private string _javaPath = "";

    partial void OnJavaModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsForcedJava));
        OnPropertyChanged(nameof(JavaModeHint));
        PersistJavaSettings();
    }

    partial void OnJavaPathChanged(string value) => PersistJavaSettings();

    private void PersistJavaSettings()
    {
        if (_loadingSettings || string.IsNullOrWhiteSpace(_instancePath)) return;
        var settings = _instanceSettingsStore.Load(_instancePath, _settingsStore.Load());
        settings.JavaMode = JavaMode;
        settings.JavaPath = JavaPath;
        _instanceSettingsStore.Save(_instancePath, settings);
    }

}
