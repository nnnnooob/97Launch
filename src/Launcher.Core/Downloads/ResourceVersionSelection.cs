namespace Launcher.Core.Downloads;

public sealed record ResourceVersionGroup(string Title, IReadOnlyList<ModrinthVersion> Versions, bool IsExpanded);

public static class ResourceVersionSelection
{
    public static bool IsCompatible(ModrinthVersion version, string gameVersion, string loader) =>
        !string.IsNullOrEmpty(gameVersion) && version.GameVersions.Contains(gameVersion)
        && (string.IsNullOrEmpty(loader) || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    public static string LoaderName(string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge", "optifine" => "OptiFine", "liteloader" => "LiteLoader",
        "fabric" => "Fabric", "forge" => "Forge", "quilt" => "Quilt", "iris" => "Iris", _ => loader,
    };

    public static string GameFilterName(string game)
    {
        if (game.Contains("snapshot", StringComparison.OrdinalIgnoreCase) || game.Contains('w')
            || game.Contains("-pre") || game.Contains("-rc")) return "快照版";
        if (game.StartsWith("1.") || Version.TryParse(game, out _)) return string.Join(".", game.Split('.').Take(2));
        if (game.Length == 0 || !char.IsDigit(game[0])) return "远古版";
        return game;
    }

    private static bool Matches(string game, string? filter) => filter is null || game == filter || GameFilterName(game) == filter;

    public static IEnumerable<string> SortGames(IEnumerable<string> games) => games.Distinct().OrderByDescending(
        game => Version.TryParse(game, out var version) ? version : new Version(0, 0))
        .ThenBy(game => game == "快照版" ? 0 : game == "远古版" ? 1 : 2).ThenByDescending(game => game, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ResourceVersionGroup> Group(IReadOnlyList<ModrinthVersion> versions,
        string targetVersion, string targetLoader, string? gameFilter, string? loaderFilter,
        bool stableOnly, bool modpack)
    {
        var filtered = versions.Where(version => (gameFilter is null || version.GameVersions.Any(game => Matches(game, gameFilter)))
            && (loaderFilter is null || version.Loaders.Contains(loaderFilter, StringComparer.OrdinalIgnoreCase))
            && (!stableOnly || version.Type == "release"))
            .OrderByDescending(version => version.Published).ToArray();
        var result = new List<ResourceVersionGroup>();
        var targetTitle = string.IsNullOrEmpty(targetLoader) || targetLoader == "datapack"
            ? targetVersion : $"{LoaderName(targetLoader)} {targetVersion}";
        var hasTarget = !modpack && !string.IsNullOrEmpty(targetVersion)
            && Matches(targetVersion, gameFilter)
            && (loaderFilter is null || string.Equals(loaderFilter, targetLoader, StringComparison.OrdinalIgnoreCase));
        if (hasTarget)
            result.Add(new(targetTitle, filtered.Where(v => IsCompatible(v, targetVersion, targetLoader)).ToArray(), true));
        var groups = new Dictionary<string, List<ModrinthVersion>>();
        foreach (var version in filtered)
        foreach (var game in version.GameVersions.Where(game => Matches(game, gameFilter)).DefaultIfEmpty("其他版本"))
        foreach (var loader in (modpack ? new[] { "" } : version.Loaders.Where(loader => loaderFilter is null
                     || loader.Equals(loaderFilter, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty("").ToArray()))
        {
            var title = string.IsNullOrEmpty(loader) || loader is "minecraft" or "datapack" ? game : $"{LoaderName(loader)} {game}";
            if (hasTarget && (title == targetTitle || string.IsNullOrEmpty(targetLoader) && game == targetVersion)) continue;
            if (!groups.TryGetValue(title, out var list)) groups[title] = list = [];
            if (!list.Any(item => item.Id == version.Id)) list.Add(version);
        }
        foreach (var group in groups.OrderByDescending(pair => pair.Value.SelectMany(v => v.GameVersions)
                     .Where(game => pair.Key.EndsWith(game, StringComparison.Ordinal)).Select(game => Version.TryParse(game, out var version) ? version : new Version(0, 0))
                     .DefaultIfEmpty(new Version(0, 0)).Max()).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            result.Add(new(group.Key, group.Value, result.Count == 0 && (modpack || string.IsNullOrEmpty(targetVersion))));
        return result;
    }
}
