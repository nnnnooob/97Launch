using System.Security.Cryptography;
using System.Text;

namespace Launcher.App.Services;

public sealed class CommunityCredentials
{
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "curseforge-key.dat");

    public string? ReadKey()
    {
        var configured = Environment.GetEnvironmentVariable("LAUNCHER_CURSEFORGE_API_KEY") ?? Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        using (var bundled = typeof(CommunityCredentials).Assembly.GetManifestResourceStream("Launcher.CurseForgeApiKey"))
        {
            if (bundled is not null)
            {
                using var reader = new StreamReader(bundled, Encoding.UTF8);
                var key = reader.ReadToEnd().Trim();
                if (key.Length > 0) return key;
            }
        }
        try
        {
            var path = File.Exists(_path) ? _path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97Launch", "curseforge-key.dat");
            return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

}
