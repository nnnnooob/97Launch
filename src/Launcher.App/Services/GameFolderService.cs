using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace Launcher.App.Services;

/// <summary>
/// Owns the Minecraft folder: which folders exist, which one is selected, and
/// the standard subdirectories inside them.
/// </summary>
/// <remarks>
/// Mirrors PCL's behaviour in <c>ModMinecraft.McFolderListLoadSub</c>: it looks
/// for a <c>versions</c> folder next to the launcher, falls back to the official
/// launcher's <c>%APPDATA%\.minecraft</c>, and creates <c>.minecraft\versions</c>
/// when nothing usable exists. Doing this on first run is what makes the
/// instance page show something instead of an empty rail.
/// </remarks>
public sealed class GameFolderService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GameFolderService>();

    /// <summary>
    /// Standard directories every Minecraft folder is expected to have. Creating
    /// them up front means a fresh install behaves the same as an existing one:
    /// the mod list, saves and resource packs are empty folders rather than
    /// missing ones.
    /// </summary>
    private static readonly string[] StandardSubdirectories =
    [
        "versions",
        "mods",
        "saves",
        "resourcepacks",
        "shaderpacks",
        "config",
        "logs",
        "crash-reports",
    ];

    /// <summary>
    /// Launcher-private marker directory, mirroring PCL's <c>PCL\</c> folder. Kept
    /// inside each game folder so a copied folder carries its launcher metadata
    /// with it.
    /// </summary>
    public const string MetadataFolderName = "97Launch";

    private readonly WindowStateStore _store;

    public GameFolderService(WindowStateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// The directory that holds the launcher's game folders, i.e. PCL's
    /// <c>Paths.Base</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a released build this is simply the executable's folder, so
    /// <c>.minecraft</c> sits next to the launcher exactly as PCL does it.
    /// </para>
    /// <para>
    /// A debug build uses the repository root as its launcher root. Its built-in
    /// folder is therefore the repository-root <c>.minecraft</c>, while the
    /// existing <c>debug\.minecraft</c> is registered separately as a removable
    /// debug folder.
    /// </para>
    /// </remarks>
    public static string LauncherRoot { get; } = ResolveLauncherRoot();

    private const string BuiltInFolderDisplayName = "内置文件夹";
    private const string BuiltInFolderDirectoryName = ".minecraft";

    private static string ResolveLauncherRoot()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

#if DEBUG
        // Walk up out of bin\Debug\<tfm>\<rid> looking for the workspace root.
        // The depth is not assumed: the marker file is searched for, so a custom
        // OutputPath or an unusual target framework still resolves correctly.
        //   win-x64 -> net8.0-... -> Debug -> bin -> Launcher.App -> src -> root
        var probe = new DirectoryInfo(exeDir);
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            probe = probe.Parent;

            if (probe is not null
                && File.Exists(Path.Combine(probe.FullName, "Directory.Build.props")))
            {
                return probe.FullName;
            }
        }
#endif

        return exeDir;
    }

    /// <summary>
    /// Built-in first, official launcher second, then the remaining folders in
    /// discovery order. Paths determine priority so custom names do not alter it.
    /// </summary>
    public List<GameFolderRecord> Discover()
    {
        var folders = new List<GameFolderRecord>();

        var local = LauncherRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(Path.Combine(local, "versions")))
        {
            folders.Add(new GameFolderRecord("当前文件夹", local, IsDefault: true));
        }

        // Debug data lives in debug\.minecraft and is handled like a folder added
        // with the picker: it is persisted in the custom-folder list, can be
        // renamed/removed there, and removing it never deletes its files.
#if DEBUG
        var debugFolderPath = Path.Combine(local, "debug", ".minecraft");
        var debugRegistrationMarker = Path.Combine(local, "debug", ".97launch-folder-registered");
        var customFolders = _store.LoadGameFolders();
        var debugEntryIndex = customFolders.FindIndex(folder => PathsEqual(folder.Path, debugFolderPath));

        // Register this debug-only folder once. If the user later removes it from
        // the rail, the marker prevents discovery from silently adding it back.
        if (!File.Exists(debugRegistrationMarker))
        {
            EnsureGameFolder(debugFolderPath);
            if (debugEntryIndex < 0)
            {
                customFolders.Add(new GameFolderRecord(
                    "调试文件夹", debugFolderPath, IsDefault: false, IsUserAdded: true));
            }
            else
            {
                customFolders[debugEntryIndex] = customFolders[debugEntryIndex] with
                {
                    Name = "调试文件夹",
                    IsDefault = false,
                    IsUserAdded = true,
                };
            }

            _store.SaveGameFolders(customFolders);
            Directory.CreateDirectory(Path.GetDirectoryName(debugRegistrationMarker)!);
            File.WriteAllText(debugRegistrationMarker, "registered");
            debugEntryIndex = customFolders.FindIndex(folder => PathsEqual(folder.Path, debugFolderPath));
        }

        if (debugEntryIndex >= 0 && Directory.Exists(debugFolderPath))
        {
            folders.Add(customFolders[debugEntryIndex] with
            {
                IsDefault = false,
                IsUserAdded = true,
            });
        }
#endif

        // The launcher-owned folder lives beside the executable in release builds.
        // In debug builds it is also created at the repository root as a distinct
        // entry, while debug\.minecraft remains the removable debug-only entry.
        var builtInPath = Path.Combine(local, BuiltInFolderDirectoryName);
        EnsureGameFolder(builtInPath);
        folders.Add(new GameFolderRecord(
            BuiltInFolderDisplayName,
            builtInPath,
            IsDefault: true));

        // Discover other immediate children that look like Minecraft folders.
        // They are labelled as local folders; never duplicate our built-in path.
        try
        {
            foreach (var child in Directory.GetDirectories(local))
            {
                if (PathsEqual(child, builtInPath))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(child, "versions")))
                {
                    folders.Add(new GameFolderRecord("当前文件夹", child, IsDefault: false));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "扫描启动器目录下的 Minecraft 文件夹失败");
        }

        // The official launcher's folder, when it exists. Not created here: an
        // empty official folder is not the same thing as an absent one.
        var official = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");
        if (Directory.Exists(Path.Combine(official, "versions"))
            && !folders.Any(f => PathsEqual(f.Path, official)))
        {
            folders.Add(new GameFolderRecord("官方启动器文件夹", official, IsDefault: false));
        }

        // User-added folders, persisted by the store. Marked as such so the rail
        // knows these are the only ones it may rename or remove.
        foreach (var custom in _store.LoadGameFolders())
        {
            if (!Directory.Exists(custom.Path)) continue;
            var index = folders.FindIndex(f => PathsEqual(f.Path, custom.Path));
            if (index >= 0)
            {
                folders[index] = folders[index] with { Name = custom.Name };
            }
            else
            {
                folders.Add(custom with { IsUserAdded = true });
            }
        }

        return folders.OrderBy(folder => PathsEqual(folder.Path, builtInPath) ? 0
            : PathsEqual(folder.Path, official) ? 1 : 2).ToList();
    }

    public static string ResolveAddedFolder(string selectedPath)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
        if (path.Contains('!') || path.Contains(';')) throw new IOException("Minecraft 文件夹路径中不能含有感叹号或分号。");
        if (!CanWriteFolder(path)) throw new UnauthorizedAccessException("没有访问该文件夹的权限，请选择可读写的文件夹。");
        if (!CanWriteFolder(Path.Combine(path, "versions")))
        {
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                if (!CanWriteFolder(Path.Combine(child, "versions"))) continue;
                path = child;
                break;
            }
        }
        var versions = Path.Combine(path, "versions");
        Directory.CreateDirectory(versions);
        if (!CanWriteFolder(versions)) throw new UnauthorizedAccessException("没有访问该文件夹的 versions 目录的权限。");
        return path;
    }

    private static bool CanWriteFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var probe = Path.Combine(path, ".97launch-permission-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            file.WriteByte(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>
    /// Creates a game folder's standard subdirectories if they are missing.
    /// </summary>
    public void EnsureGameFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            foreach (var sub in StandardSubdirectories)
            {
                Directory.CreateDirectory(Path.Combine(path, sub));
            }

            Directory.CreateDirectory(Path.Combine(path, MetadataFolderName));
            EnsureLauncherProfiles(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "初始化游戏文件夹失败：{Path}", path);
        }
    }

    /// <summary>
    /// Ensures <c>launcher_profiles.json</c> exists.
    /// </summary>
    /// <remarks>
    /// Neither Minecraft itself nor most mod installers need this, but the
    /// vanilla launcher refuses to start without it, and a user who opens the
    /// folder with the official launcher would otherwise see an error. The
    /// contents are the minimal valid skeleton rather than a real profile list.
    /// </remarks>
    private static void EnsureLauncherProfiles(string path)
    {
        var file = Path.Combine(path, "launcher_profiles.json");
        if (File.Exists(file))
        {
            return;
        }

        var skeleton = new JsonObject
        {
            ["profiles"] = new JsonObject(),
            ["settings"] = new JsonObject { ["language"] = "zh_CN" },
            ["version"] = 3,
        };

        File.WriteAllText(
            file,
            skeleton.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>True when two paths point at the same folder.</summary>
    public static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// A Minecraft folder as shown in the rail.
/// </summary>
public sealed record GameFolderRecord(string Name, string Path, bool IsDefault, bool IsUserAdded = false)
{
    /// <summary><c>versions</c> directory for this folder.</summary>
    public string VersionsDirectory => System.IO.Path.Combine(Path, "versions");
}
