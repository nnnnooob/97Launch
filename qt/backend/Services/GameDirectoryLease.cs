using System.Security.Cryptography;
using System.Text;

namespace Launcher.App.Services;

internal static class GameDirectoryLease
{
    public static IDisposable Acquire(string directory, bool exclusive)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant())));
        var folder = Path.Combine(AppInfo.DataDirectory, "directory-locks");
        Directory.CreateDirectory(folder);
        try { return new FileStream(Path.Combine(folder, key + ".lock"), FileMode.OpenOrCreate,
            exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read); }
        catch (IOException) { throw new IOException("此游戏目录正在启动、运行或修改文件，请等待任务完成。"); }
    }
}
