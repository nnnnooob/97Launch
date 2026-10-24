using System.Reflection;

namespace Launcher.App;

/// <summary>
/// Identity and filesystem locations for the running application.
/// </summary>
/// <remarks>
/// Kept in one place so the version shown in the status bar, written to logs,
/// and reported by diagnostics cannot drift apart.
/// </remarks>
public static class AppInfo
{
    /// <summary>Product name used in window titles and log headers.</summary>
    public const string Name = "97Launch";

    /// <summary>The distributable EXE's directory, independent of single-file extraction.</summary>
    public static string ApplicationDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Per-user data root: <c>%LOCALAPPDATA%\97Launch</c>.
    /// </summary>
    /// <remarks>
    /// LocalApplicationData rather than Roaming: caches, logs and downloaded
    /// game assets are machine-specific and can be large, so they must not be
    /// synchronised to other machines.
    /// </remarks>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Name);

    /// <summary>Directory holding rolling log files.</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Log file path handed to Serilog. The date is appended by the rolling
    /// interval rather than baked in here.
    /// </summary>
    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "launcher-.log");

    /// <summary>
    /// Display version, e.g. <c>v0.1.0-alpha.1-dev</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the assembly version set in Directory.Build.props so the
    /// UI, logs and any crash report agree.
    /// </remarks>
    public static string Version { get; } = BuildVersion();
    public static int BuildNumber { get; } = int.TryParse(typeof(AppInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildNumber")?.Value,
        out var number) ? number : 0;

    /// <summary>Ensures the directories Serilog writes into exist.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string BuildVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the source-revision suffix that the SDK appends ("1.2.3+abc123").
            var plus = informational.IndexOf('+');
            return plus >= 0 ? $"v{informational[..plus]}" : $"v{informational}";
        }

        var version = assembly.GetName().Version;
        return version is null ? "v0.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
