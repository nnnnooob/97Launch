using System.Text.Json;
using Serilog;

namespace Launcher.App.Services;

/// <summary>
/// The subset of user settings that other parts of the launcher actually read.
/// </summary>
/// <remarks>
/// This is deliberately a small plain record rather than a general settings
/// framework. Every property here has a consumer; anything the settings page
/// shows but nothing consumes stays out until it is wired up, so the stored file
/// never accumulates values that silently do nothing.
///
/// Kept separate from <see cref="ViewModels.SettingsViewModel"/> because the
/// download service must be able to read preferences without depending on a view
/// model, and because defaults for a first run belong in one place.
/// </remarks>
public sealed class LauncherSettings
{
    /// <summary>
    /// When true, a freshly installed instance becomes the selected one and the
    /// launcher switches to it.
    /// </summary>
    /// <remarks>
    /// On by default: after waiting for a download, the user almost always wants
    /// to play what they just installed, and the alternative is hunting for the
    /// new entry in the instance list.
    /// </remarks>
    public bool SelectNewInstance { get; set; } = true;

    /// <summary>Source preference for game files (libraries and assets).</summary>
    public string FileDownloadSource { get; set; } = "自动选择";

    /// <summary>Source preference for the version manifest and version json.</summary>
    public string VersionDownloadSource { get; set; } = "自动选择";

    /// <summary>Source preference for community content (mods, packs, shaders).</summary>
    public string CommunitySource { get; set; } = "自动选择";
    public string ResourceFileNameFormat { get; set; } = Launcher.Core.Downloads.ResourceFileName.Default;

    public int DownloadThreads { get; set; } = 64;
    public int DownloadSpeedLimitKb { get; set; }

    /// <summary>Isolation mode for version folders.</summary>
    public string VersionIsolation { get; set; } = "隔离所有版本";

    /// <summary>Last selected game directory in the instances page.</summary>
    public string SelectedGameFolderPath { get; set; } = "";

    /// <summary>Absolute version-directory path of the last selected launch instance.</summary>
    public string SelectedInstancePath { get; set; } = "";

    /// <summary>Game-folder locations containing Java runtimes downloaded by 97Launch.</summary>
    public List<string> ManagedJavaRuntimeDirectories { get; set; } = [];

    public string WindowTitle { get; set; } = "";
    public string ProcessPriority { get; set; } = "中（平衡）";
    public string IpPreference { get; set; } = "IPv4 优先";
    public string WindowSize { get; set; } = "默认";
    public string WindowWidth { get; set; } = "854";
    public string WindowHeight { get; set; } = "480";
    public bool IsAutoMemory { get; set; } = true;
    public double CustomMemoryGb { get; set; } = 3.0;
    public double MaxMemoryGb { get; set; } = 16.0;
    public string JvmArgsHead { get; set; } = "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True";
    public string GameArgsTail { get; set; } = "";
    public string PreLaunchCommand { get; set; } = "";
    public bool RequireHighPerformanceGpu { get; set; } = true;
    public bool UseJavaExe { get; set; }
    public bool AutoSetChinese { get; set; } = true;
    public DefaultGameSettings GameDefaults { get; set; } = new();
    public string SelectedJavaPath { get; set; } = "";
    public List<string> AdditionalJavaPaths { get; set; } = [];
}

/// <summary>
/// Persists <see cref="LauncherSettings"/> as JSON under the app data folder.
/// </summary>
/// <remarks>
/// Loaded once at startup and saved whenever a bound value changes, so the file
/// is always current and a crash cannot lose a preference. Failures degrade to
/// defaults rather than throwing: a launcher that will not start because its
/// settings file is malformed is worse than one that forgets a checkbox.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SettingsStore>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private DateTimeOffset _lastSaveNotice;

    public SettingsStore()
        : this(Path.Combine(AppInfo.DataDirectory, "settings.json"))
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;
    public LauncherSettings? RequestDefaults { get; set; }

    /// <summary>Reads the settings, falling back to defaults.</summary>
    public LauncherSettings Load()
    {
        if (RequestDefaults is not null) return RequestDefaults;
        try
        {
            if (!File.Exists(_path))
            {
                return new LauncherSettings();
            }

            return JsonSerializer.Deserialize<LauncherSettings>(
                File.ReadAllText(_path), JsonOptions) ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取设置失败，使用默认值：{Path}", _path);
            return new LauncherSettings();
        }
    }

    /// <summary>Writes the settings, ignoring failures.</summary>
    public void Save(LauncherSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            if (DateTimeOffset.UtcNow - _lastSaveNotice > TimeSpan.FromSeconds(2))
            {
                _lastSaveNotice = DateTimeOffset.UtcNow;
                Log.Information("全局设置已保存");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存设置失败：{Path}", _path);
        }
    }
}
