using System.Text.Json;

namespace Launcher.App.Services;

/// <summary>
/// Window geometry remembered across runs.
/// </summary>
public sealed record WindowState(int Width, int Height, int X, int Y)
{
    /// <summary>
    /// Default size for a first run: wide enough for the sidebar, a page and the
    /// log pane side by side without anything feeling cramped.
    /// </summary>
    public static WindowState Default { get; } = new(1440, 860, int.MinValue, int.MinValue);

    /// <summary>
    /// Smallest width that still shows the launch form and the log pane side by
    /// side, derived from the shell's own layout rather than picked by eye:
    /// <code>
    ///   sidebar            176   (OpenPaneLength in MainWindow.xaml)
    ///   page padding        48   (24 left + 24 right in LaunchPage.xaml)
    ///   launch form         420  (MinWidth of the form column)
    ///   column gap          16   (middle spacer column)
    ///   log pane            340  (fixed log column width)
    ///   frame + scrollbars  32   (window border, non-client chrome, scrollbar)
    ///                     ----
    ///                     1032
    /// </code>
    /// Below this the fixed-width log column starts eating the form, which is
    /// exactly what the minimum exists to prevent. These numbers are duplicated
    /// from XAML by necessity — the window must be sized before layout runs — so
    /// changing a column width there means revisiting this constant.
    /// </summary>
    public const int MinWidth = 1032;

    /// <summary>
    /// Tall enough for the stacked launch cards plus the action button with the
    /// log pane still visible alongside; below this the page scrolls, which is
    /// tolerable but not the intended default.
    /// </summary>
    public const int MinHeight = 640;

    public bool HasPosition => X != int.MinValue && Y != int.MinValue;

    public WindowState Clamp() => new(
        Math.Max(Width, MinWidth),
        Math.Max(Height, MinHeight),
        X,
        Y);
}

/// <summary>
/// Persists <see cref="WindowState"/> to a JSON file under the app data folder.
/// </summary>
/// <remarks>
/// Deliberately small and synchronous: it is read once at startup and written on
/// resize/close, so a full settings framework would be overhead. Writes are
/// best-effort — a failed save must never take the launcher down, since the
/// worst case is simply reopening at the default size.
/// </remarks>
public sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public WindowStateStore()
        : this(Path.Combine(AppInfo.DataDirectory, "window.json"))
    {
    }

    public WindowStateStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    /// <summary>
    /// Loads the saved geometry, or <see cref="WindowState.Default"/> when there
    /// is nothing usable on disk.
    /// </summary>
    public WindowState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return WindowState.Default;
            }

            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<WindowState>(json, JsonOptions);

            // A truncated or hand-edited file can deserialize to nonsense
            // dimensions; Clamp keeps the window on screen and usable rather
            // than trusting whatever was written.
            return state is null ? WindowState.Default : state.Clamp();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not read window state from {Path}", _path);
            return WindowState.Default;
        }
    }

    /// <summary>Saves the geometry, ignoring failures.</summary>
    public void Save(WindowState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not save window state to {Path}", _path);
        }
    }

    // ======== user-added game folders ========

    private string GameFoldersPath => Path.Combine(
        Path.GetDirectoryName(_path)!,
        "gamefolders.json");

    /// <summary>
    /// Loads folders the user added explicitly.
    /// </summary>
    /// <remarks>
    /// Only custom folders are stored. Autodiscovered ones are re-found on every
    /// start, so persisting them would duplicate them as the disk changes.
    /// </remarks>
    public List<GameFolderRecord> LoadGameFolders()
    {
        try
        {
            var file = GameFoldersPath;
            if (!File.Exists(file))
            {
                return [];
            }

            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<List<GameFolderRecord>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not read game folders from {Path}", GameFoldersPath);
            return [];
        }
    }

    /// <summary>Saves the user's custom folder list, ignoring failures.</summary>
    public void SaveGameFolders(IEnumerable<GameFolderRecord> folders)
    {
        try
        {
            var custom = folders.Where(f => !f.IsDefault);
            File.WriteAllText(
                GameFoldersPath,
                JsonSerializer.Serialize(custom.ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not save game folders to {Path}", GameFoldersPath);
        }
    }
}
