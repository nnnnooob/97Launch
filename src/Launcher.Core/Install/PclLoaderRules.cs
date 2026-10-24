using System.Text.RegularExpressions;

namespace Launcher.Core.Install;

/// <summary>Compatibility and version mapping rules mirrored from PCL's install picker.</summary>
public static class PclLoaderRules
{
    public static bool CanSelectPrimary(string? selectedLoader, string candidate)
        => string.IsNullOrWhiteSpace(selectedLoader)
            || selectedLoader.Equals(candidate, StringComparison.OrdinalIgnoreCase);

    public static bool SupportsOptiFineWithFabric(string minecraftVersion)
        => !TryGetVersion(minecraftVersion, out var version) || version <= new Version(1, 20, 4);

    public static bool IsForgeOptiFineRangeUnsupported(string minecraftVersion)
        => TryGetVersion(minecraftVersion, out var version)
            && version >= new Version(1, 13)
            && version <= new Version(1, 14, 3);

    public static bool SupportsOptiFabric(string minecraftVersion)
        => !TryGetVersion(minecraftVersion, out var version)
            || version < new Version(1, 14)
            || version >= new Version(1, 16);

    public static bool IsOptiFineCompatibleWithForge(
        string? requiredForgeVersion,
        string minecraftVersion,
        string forgeVersion)
    {
        if (requiredForgeVersion is null) return false;
        if (string.IsNullOrWhiteSpace(requiredForgeVersion)) return true;
        if (requiredForgeVersion.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return false;

        var forgeMinecraft = Regex.Match(forgeVersion, @"^(?<mc>\d+(?:\.\d+){1,2})-").Groups["mc"].Value;
        if (forgeMinecraft.Length > 0 && !forgeMinecraft.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            return false;
        var actual = forgeMinecraft.Length > 0 ? forgeVersion[(forgeMinecraft.Length + 1)..] : forgeVersion;
        var required = Regex.Match(requiredForgeVersion, @"\d+(?:\.\d+)*").Value;
        if (required.Length == 0) return false;
        if (required.Contains('.'))
            return Version.TryParse(actual, out var actualVersion)
                && Version.TryParse(required, out var requiredVersion)
                && actualVersion.CompareTo(requiredVersion) == 0;
        return actual.Split('.').LastOrDefault()?.Equals(required, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Maps a raw NeoForge Maven version to the Minecraft version it inherits.</summary>
    public static bool TryMapNeoForgeMinecraftVersion(string apiVersion, out string minecraftVersion)
    {
        minecraftVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(apiVersion)) return false;

        if (apiVersion.StartsWith("1.20.1-", StringComparison.Ordinal))
        {
            minecraftVersion = "1.20.1";
            return true;
        }

        if (apiVersion.StartsWith("0.", StringComparison.Ordinal))
        {
            var segments = apiVersion.Split('-', 2)[0].Split('.');
            if (segments.Length < 3 || string.IsNullOrWhiteSpace(segments[1])) return false;
            minecraftVersion = segments[1];
            return true;
        }

        var versionPart = apiVersion.Split('-', 2)[0];
        var snapshotIndex = versionPart.IndexOf('+');
        var numericPart = snapshotIndex >= 0 ? versionPart[..snapshotIndex] : versionPart;
        if (!Version.TryParse(numericPart, out var parsed) || parsed.Build < 0) return false;

        var inherited = parsed.Major >= 24
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}"
            : $"1.{parsed.Major}.{parsed.Minor}";
        if (inherited.EndsWith(".0", StringComparison.Ordinal))
            inherited = inherited[..^2];

        var plusIndex = apiVersion.IndexOf('+');
        if (plusIndex >= 0 && plusIndex < apiVersion.Length - 1)
            inherited += "-" + apiVersion[(plusIndex + 1)..];
        minecraftVersion = inherited;
        return true;
    }

    private static bool TryGetVersion(string value, out Version version)
    {
        var numeric = Regex.Match(value, @"\d+(?:\.\d+){1,3}").Value;
        if (Version.TryParse(numeric, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }
}
