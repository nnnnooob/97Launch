namespace Launcher.App.Services.Minecraft;

/// <summary>
/// The standard folder layout of a .minecraft directory.
/// </summary>
/// <remarks>
/// These names are fixed by the game itself: mods, resource packs and worlds only
/// load from the exact folder the game looks in, so they cannot be renamed or
/// localised. Keeping the list in one place means the first-run setup and any
/// later repair pass create the same tree.
/// </remarks>
public static class GameFolderLayout
{
    /// <summary>
    /// Subdirectories created inside a game folder on first use.
    /// </summary>
    /// <remarks>
    /// Covers what vanilla, the loaders and common utilities expect. Creating them
    /// up front is what makes a freshly installed game folder usable: several
    /// loaders and resource pack pickers do not create their own directories and
    /// silently show an empty list when the folder is missing.
    /// </remarks>
    public static readonly string[] Subdirectories =
    [
        "versions",
        "libraries",
        "assets",
        "indexes",
        "objects",
        "logs",
        "crash-reports",
        "saves",
        "screenshots",
        "resourcepacks",
        "shaderpacks",
        "mods",
        "config",
        "defaultconfigs",
        "server-resource-packs",
    ];

    /// <summary>
    /// Creates the game folder and its subdirectories if they are missing.
    /// </summary>
    /// <returns>False when the folder cannot be written, so the caller can report it.</returns>
    /// <remarks>
    /// Failures are returned rather than thrown because a first run on a read-only
    /// or full volume should still let the launcher start; only the parts needing a
    /// game folder stop working.
    /// </remarks>
    public static bool Ensure(string gameDirectory)
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);

            foreach (var name in Subdirectories)
            {
                Directory.CreateDirectory(Path.Combine(gameDirectory, name));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "无法创建游戏目录 {Path}", gameDirectory);
            return false;
        }
    }
}
