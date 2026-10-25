using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.App.Services;

public sealed record JavaRuntimeInfo(
    string Name,
    string Version,
    string Vendor,
    string Architecture,
    string Path,
    bool IsManual);

/// <summary>Finds Java executables and reads their real runtime metadata.</summary>
public static partial class JavaRuntimeDetector
{
    private static readonly Regex VersionPattern = VersionRegex();
    private static readonly Regex ArchPattern = ArchRegex();
    private static readonly Regex VendorPattern = VendorRegex();

    public static async Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(
        IEnumerable<string>? additionalPaths = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? managedRuntimeRoots = null,
        string? gameDirectory = null)
    {
        var candidates = await Task.Run(() => CandidatePaths(additionalPaths, managedRuntimeRoots, gameDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
        Serilog.Log.Information("Java 搜索候选：{Count} 个程序路径", candidates.Count);
        using var concurrency = new SemaphoreSlim(4);
        var probes = candidates.Select(async candidate =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProbeAsync(candidate.Path, candidate.IsManual, cancellationToken,
                    (path, reason) => Serilog.Log.Warning("Java 检查失败：{Path}，{Reason}", path, reason)).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });

        return (await Task.WhenAll(probes).ConfigureAwait(false))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => VersionSortKey(item.Version))
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<JavaRuntimeInfo?> ProbeAsync(
        string path,
        bool isManual = false,
        CancellationToken cancellationToken = default,
        Action<string, string>? onProbeFailure = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            onProbeFailure?.Invoke(path, "Java executable is missing.");
            return null;
        }

        try
        {
            path = NormalizeExecutablePath(path);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-XshowSettings:properties -version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                onProbeFailure?.Invoke(path, "Process.Start returned no process.");
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(true);
                onProbeFailure?.Invoke(path, "Java version probe timed out after 15 seconds.");
                return null;
            }

            var output = (await stderr.ConfigureAwait(false)) + Environment.NewLine
                + (await stdout.ConfigureAwait(false));
            var versionMatch = VersionPattern.Match(output);
            if (!versionMatch.Success)
            {
                onProbeFailure?.Invoke(path, $"Java version output was not recognized (exit code {process.ExitCode}).");
                return null;
            }

            var version = versionMatch.Groups[1].Value;
            var arch = ArchPattern.Match(output);
            var vendor = VendorPattern.Match(output);
            var name = $"Java {version}";

            return new JavaRuntimeInfo(
                name,
                version,
                vendor.Success ? vendor.Groups[1].Value.Trim() : VendorFromPath(path),
                arch.Success ? NormalizeArchitecture(arch.Groups[1].Value) : ArchitectureFromPath(path),
                Path.GetFullPath(path),
                isManual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onProbeFailure?.Invoke(path, ex.ToString());
            return null;
        }
    }

    // java.exe and javaw.exe in the same bin directory share one runtime.
    // The launch service independently chooses the console or windowed executable.
    public static string NormalizeExecutablePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        if (string.Equals(Path.GetFileName(fullPath), "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var consolePath = Path.Combine(Path.GetDirectoryName(fullPath)!, "java.exe");
            if (File.Exists(consolePath)) return consolePath;
        }
        return fullPath;
    }

    public static bool IsSameRuntime(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(NormalizeExecutablePath(left), NormalizeExecutablePath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<(string Path, bool IsManual)> CandidatePaths(
        IEnumerable<string>? additionalPaths,
        IEnumerable<string>? managedRuntimeRoots,
        string? gameDirectory,
        CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, bool IsManual)>();

        void Add(string? value, bool isManual = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var path = NormalizeExecutablePath(value);
                if (File.Exists(path) && seen.Add(path)) candidates.Add((path, isManual));
            }
            catch
            {
                // Skip invalid paths and continue scanning other locations.
            }
        }

        foreach (var path in additionalPaths ?? []) Add(path, true);
        // Keep runtimes installed by WinUI3 discoverable after changing data directories.
        var legacyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97Launch");
        var legacy = new SettingsStore(Path.Combine(legacyDirectory, "settings.json")).Load();
        foreach (var path in legacy.AdditionalJavaPaths ?? []) Add(path);
        Add(legacy.SelectedJavaPath);
        Add(Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin", "java.exe"));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory)) Add(Path.Combine(directory.Trim().Trim('"'), "java.exe"));
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(roaming, ".minecraft", "runtime"),
            Path.Combine(roaming, ".hmcl", "java"),
            Path.Combine(roaming, "ATLauncher", "runtimes", "minecraft"),
            Path.Combine(roaming, "ModrinthApp", "meta", "java_versions"),
            Path.Combine(roaming, "PrismLauncher", "java"),
            Path.Combine(user, "curseforge", "minecraft", "Install", "runtime"),
            Path.Combine(user, ".jdks"),
            Path.Combine(user, ".sdkman", "candidates", "java"),
            Path.Combine(local, ".ftba", "bin", "runtime"),
            Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Curse", "Minecraft", "Install", "runtime"),
            Path.Combine(AppInfo.DataDirectory, "runtimes", "java"),
            Path.Combine(legacyDirectory, "runtimes", "java"),
        };
        roots.AddRange(managedRuntimeRoots ?? []);
        roots.AddRange(legacy.ManagedJavaRuntimeDirectories ?? []);
        foreach (var home in (Environment.GetEnvironmentVariable("JDK_HOME") + ";" + Environment.GetEnvironmentVariable("JAVA_HOME")).Split(';', StringSplitOptions.RemoveEmptyEntries))
            roots.Add(home.Trim().Trim('"'));
        foreach (var programRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Amazon Corretto", "Zulu", "BellSoft", "Semeru" })
                roots.Add(Path.Combine(programRoot, vendor));
            roots.Add(Path.Combine(programRoot, "Minecraft Launcher", "runtime"));
            roots.Add(Path.Combine(programRoot, "Minecraft", "runtime"));
            try
            {
                var microsoft = Path.Combine(programRoot, "Microsoft");
                if (Directory.Exists(microsoft)) roots.AddRange(Directory.EnumerateDirectories(microsoft, "jdk*"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var folder in new[] { gameDirectory, AppInfo.ApplicationDirectory, Path.Combine(AppInfo.ApplicationDirectory, ".minecraft"), legacy.SelectedGameFolderPath })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            foreach (var relative in new[] { "runtime", "runtimes", "java", Path.Combine(".97Launch", "runtimes", "java") })
                roots.Add(Path.Combine(folder, relative));
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "java.exe", options))
                { token.ThrowIfCancellationRequested(); Add(path); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Serilog.Log.Debug(ex, "跳过无法读取的 Java 搜索目录：{Path}", root); }
        }

        return candidates;
    }

    private static double VersionSortKey(string version)
    {
        var match = VersionPattern.Match($"version \"{version}\"");
        return match.Success && Version.TryParse(match.Groups[1].Value.Split('_')[0], out var parsed)
            ? parsed.Major * 1000 + parsed.Minor * 10 + parsed.Build
            : 0;
    }

    private static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" => "x64",
        "x86" or "i386" or "i486" or "i586" or "i686" => "x86",
        "aarch64" or "arm64" => "ARM64",
        _ => value,
    };

    private static string ArchitectureFromPath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return normalized.Contains("x86", StringComparison.Ordinal) ? "x86"
            : normalized.Contains("arm64", StringComparison.Ordinal) ? "ARM64"
            : "x64 / 未知";
    }

    private static string VendorFromPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.Contains("adoptium")) return "Eclipse Adoptium";
        if (normalized.Contains("oracle")) return "Oracle";
        if (normalized.Contains("microsoft")) return "Microsoft";
        return "Java Runtime";
    }

    [GeneratedRegex("""(?:version "|openjdk version "|java version ")([^"\s]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("os\\.arch\\s*=\\s*([^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchRegex();

    [GeneratedRegex("java\\.vendor\\s*=\\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorRegex();
}
