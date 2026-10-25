using System.Runtime.InteropServices;
using System.Text.Json;
using Launcher.App.ViewModels;
namespace Launcher.App {
    public static class AppInfo {
        public static string ApplicationDirectory => Path.GetDirectoryName(TargetExecutable)!;
        public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97LaunchQt");
        public const string Name = "97Launch";
        public static string TargetExecutable => Environment.GetEnvironmentVariable("LAUNCH_PORTABLE_EXE") is { Length: > 0 } portable
            ? Path.GetFullPath(portable) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "97LaunchQt.exe"));
        private static System.Diagnostics.FileVersionInfo TargetVersion => System.Diagnostics.FileVersionInfo.GetVersionInfo(TargetExecutable);
        public static string Version => TargetVersion.ProductVersion?.Split('+')[0] ?? "0.1.0";
        public static int BuildNumber => TargetVersion.FilePrivatePart;
    }
    internal static class Win32 {
        public const int FO_DELETE = 3, FO_RENAME = 4;
        public const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 4, FOF_NOERRORUI = 0x400;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT { public IntPtr hwnd; public int wFunc; public string pFrom; public string? pTo; public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string path, IntPtr context, out IntPtr pidl, uint attributes, out uint output);
        [DllImport("shell32.dll")] public static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
    }
}
namespace Launcher.App.ViewModels {
    public sealed record InstanceItem(string Name, string Path, string GameVersion, string Loader, bool IsModdable, string IconPath,
        string? Error = null, bool IsRelease = true, string? LoaderVersionDisplay = null, string MinecraftVersionId = "") {
        public string Id => Launcher.Core.Instances.InstanceIdentity.ForPath(Path);
        public bool HasError => !string.IsNullOrEmpty(Error);
        public string LoaderVersion => LoaderVersionDisplay ?? Loader;
    }
}
namespace Launcher.App.Services {
    public sealed record GameFolderRecord(string Name, string Path);
    public sealed class GameFolderService {
        public static string LauncherRoot => AppInfo.ApplicationDirectory;
        public string Root { get; set; } = "";
        public List<GameFolderRecord> Discover() {
            var result = new List<GameFolderRecord> { new("当前文件夹", Root) };
            var path = Path.Combine(AppInfo.DataDirectory, "instances.json");
            if (File.Exists(path)) {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Folders", out var folders)) foreach (var row in folders.EnumerateArray())
                    if (row.TryGetProperty("Path", out var value) && Directory.Exists(value.GetString())) result.Add(new("", value.GetString()!));
            }
            return result;
        }
        public static bool PathsEqual(string a, string b) => Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
