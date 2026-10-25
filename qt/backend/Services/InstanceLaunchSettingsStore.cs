using System.Globalization;

namespace Launcher.App.Services;

/// <summary>Stores launcher-owned options beside one Minecraft version.</summary>
public sealed class InstanceLaunchSettingsStore
{
    private DateTimeOffset _lastSaveNotice;
    private string? _lastSavedInstance;
    public InstanceLaunchSettings Load(string instancePath, LauncherSettings defaults)
    {
        var settings = InstanceLaunchSettings.From(defaults);
        var file = GetFilePath(instancePath);
        if (!File.Exists(file)) return settings;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;
                var key = line[..split].Trim();
                var value = line[(split + 1)..];
                switch (key)
                {
                    case "UseGlobalLaunchOptions" when bool.TryParse(value, out var globalLaunch): settings.UseGlobalLaunchOptions = globalLaunch; break;
                    case "UseGlobalMemory" when bool.TryParse(value, out var globalMemory): settings.UseGlobalMemory = globalMemory; break;
                    case "UseGlobalAdvancedOptions" when bool.TryParse(value, out var globalAdvanced): settings.UseGlobalAdvancedOptions = globalAdvanced; break;
                    case "VersionIsolation": settings.VersionIsolation = value; break;
                    case "WindowTitle": settings.WindowTitle = value; break;
                    case "ProcessPriority": settings.ProcessPriority = value; break;
                    case "IpPreference": settings.IpPreference = value; break;
                    case "WindowSize": settings.WindowSize = value; break;
                    case "WindowWidth": settings.WindowWidth = value; break;
                    case "WindowHeight": settings.WindowHeight = value; break;
                    case "IsAutoMemory" when bool.TryParse(value, out var auto): settings.IsAutoMemory = auto; break;
                    case "CustomMemoryGb" when double.TryParse(value, CultureInfo.InvariantCulture, out var memory): settings.CustomMemoryGb = memory; break;
                    case "JvmArgsHead": settings.JvmArgsHead = value; break;
                    case "GameArgsTail": settings.GameArgsTail = value; break;
                    case "PreLaunchCommand": settings.PreLaunchCommand = value; break;
                    case "RequireHighPerformanceGpu" when bool.TryParse(value, out var gpu): settings.RequireHighPerformanceGpu = gpu; break;
                    case "UseJavaExe" when bool.TryParse(value, out var java): settings.UseJavaExe = java; break;
                    case "JavaMode": settings.JavaMode = value; break;
                    case "JavaPath": settings.JavaPath = value; break;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取实例设置失败，使用全局默认值：{Instance}", Path.GetFileName(instancePath));
            return InstanceLaunchSettings.From(defaults);
        }

        if (settings.UseGlobalLaunchOptions)
        {
            settings.VersionIsolation = defaults.VersionIsolation;
            settings.WindowTitle = defaults.WindowTitle;
            settings.ProcessPriority = defaults.ProcessPriority;
            settings.IpPreference = defaults.IpPreference;
            settings.WindowSize = defaults.WindowSize;
        }
        if (settings.UseGlobalMemory)
        {
            settings.IsAutoMemory = defaults.IsAutoMemory;
            settings.CustomMemoryGb = defaults.CustomMemoryGb;
        }
        if (settings.UseGlobalAdvancedOptions)
        {
            settings.JvmArgsHead = defaults.JvmArgsHead;
            settings.GameArgsTail = defaults.GameArgsTail;
            settings.PreLaunchCommand = defaults.PreLaunchCommand;
            settings.RequireHighPerformanceGpu = defaults.RequireHighPerformanceGpu;
            settings.UseJavaExe = defaults.UseJavaExe;
        }

        return settings;
    }

    public void Save(string instancePath, InstanceLaunchSettings settings)
    {
        try
        {
            var file = GetFilePath(instancePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file,
            [
                $"UseGlobalLaunchOptions={settings.UseGlobalLaunchOptions}",
                $"UseGlobalMemory={settings.UseGlobalMemory}",
                $"UseGlobalAdvancedOptions={settings.UseGlobalAdvancedOptions}",
                $"VersionIsolation={settings.VersionIsolation}",
                $"WindowTitle={settings.WindowTitle}",
                $"ProcessPriority={settings.ProcessPriority}",
                $"IpPreference={settings.IpPreference}",
                $"WindowSize={settings.WindowSize}",
                $"WindowWidth={settings.WindowWidth}",
                $"WindowHeight={settings.WindowHeight}",
                $"IsAutoMemory={settings.IsAutoMemory}",
                $"CustomMemoryGb={settings.CustomMemoryGb.ToString(CultureInfo.InvariantCulture)}",
                $"JvmArgsHead={settings.JvmArgsHead}",
                $"GameArgsTail={settings.GameArgsTail}",
                $"PreLaunchCommand={settings.PreLaunchCommand}",
                $"RequireHighPerformanceGpu={settings.RequireHighPerformanceGpu}",
                $"UseJavaExe={settings.UseJavaExe}",
                $"JavaMode={settings.JavaMode}",
                $"JavaPath={settings.JavaPath}",
            ]);
            if (_lastSavedInstance != instancePath || DateTimeOffset.UtcNow - _lastSaveNotice > TimeSpan.FromSeconds(2))
            {
                _lastSavedInstance = instancePath;
                _lastSaveNotice = DateTimeOffset.UtcNow;
                Serilog.Log.Information("实例设置已保存：{Instance}", Path.GetFileName(instancePath));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "保存实例设置失败：{Instance}", Path.GetFileName(instancePath));
        }
    }

    public static string GetFilePath(string instancePath)
        => Path.Combine(instancePath, "97Launch", "Setup.ini");
}

public sealed class InstanceLaunchSettings
{
    public bool UseGlobalLaunchOptions { get; set; } = true;
    public bool UseGlobalMemory { get; set; } = true;
    public bool UseGlobalAdvancedOptions { get; set; } = true;
    public string VersionIsolation { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string ProcessPriority { get; set; } = "";
    public string IpPreference { get; set; } = "";
    public string WindowSize { get; set; } = "";
    public string WindowWidth { get; set; } = "";
    public string WindowHeight { get; set; } = "";
    public bool IsAutoMemory { get; set; }
    public double CustomMemoryGb { get; set; }
    public string JvmArgsHead { get; set; } = "";
    public string GameArgsTail { get; set; } = "";
    public string PreLaunchCommand { get; set; } = "";
    public bool RequireHighPerformanceGpu { get; set; }
    public bool UseJavaExe { get; set; }
    public string JavaMode { get; set; } = "自动选择并按需下载";
    public string JavaPath { get; set; } = "";

    public static InstanceLaunchSettings From(LauncherSettings source) => new()
    {
        VersionIsolation = source.VersionIsolation,
        WindowTitle = source.WindowTitle,
        ProcessPriority = source.ProcessPriority,
        IpPreference = source.IpPreference,
        WindowSize = source.WindowSize,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        IsAutoMemory = source.IsAutoMemory,
        CustomMemoryGb = source.CustomMemoryGb,
        JvmArgsHead = source.JvmArgsHead,
        GameArgsTail = source.GameArgsTail,
        PreLaunchCommand = source.PreLaunchCommand,
        RequireHighPerformanceGpu = source.RequireHighPerformanceGpu,
        UseJavaExe = source.UseJavaExe,
    };
}
