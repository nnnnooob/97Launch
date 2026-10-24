using System.Security.Cryptography;
using System.Text;

namespace Launcher.App.Services;

public sealed class CommunityCredentials
{
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "curseforge-key.dat");

    public string? ReadKey()
    {
        var configured = Environment.GetEnvironmentVariable("LAUNCHER_CURSEFORGE_API_KEY");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        try
        {
            return File.Exists(_path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

}
