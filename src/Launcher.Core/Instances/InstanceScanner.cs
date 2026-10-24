using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Instances;

/// <summary>
/// Scans a Minecraft folder's <c>versions</c> directory, following PCL.
/// </summary>
/// <remarks>
/// <para>
/// Reimplements the load-and-classify path of PCL's <c>McInstance</c> and
/// <c>InitMcInstanceListWithoutCache</c>. The reading order, the skip rules and
/// the loader precedence are all taken from the original rather than invented,
/// because getting them subtly different produces a launcher that disagrees with
/// PCL about the same folder on disk.
/// </para>
/// <para>
/// Everything here is synchronous filesystem work and is meant to be called from
/// a background thread; <see cref="ScanAsync"/> is the convenience wrapper that
/// does that for UI callers. A folder holding a few hundred versions reads a few
/// hundred small JSON files, which is far too slow to run on the UI thread.
/// </para>
/// </remarks>
public sealed class InstanceScanner
{
    private readonly ILogger<InstanceScanner> _log;
    private readonly HashSet<string> _scannedFolders = new(StringComparer.OrdinalIgnoreCase);

    public InstanceScanner(ILogger<InstanceScanner> log) => _log = log;

    /// <summary>Snapshot ids: <c>24w14a</c>, <c>1.20.5-pre1</c>, <c>1.20.5-rc1</c>.</summary>
    private static readonly Regex SnapshotPattern = new(
        @"^\d{2}w\d{2}[a-z]$|-(pre|rc)\d+$|^\d+(\.\d+)?-snapshot(?:-?\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Alpha and beta ids: <c>a1.2.6</c>, <c>b1.7.3</c>, <c>c0.30</c>.</summary>
    private static readonly Regex OldPattern = new(
        @"^[abc]\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A plain release id: <c>1.20.1</c>, <c>1.7.10</c>.</summary>
    private static readonly Regex ReleasePattern = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Known April Fools versions (PCL keeps the same kind of table to name them;
    /// <c>GetMcFoolName</c>). Mojang tags these as ordinary snapshots or releases,
    /// so an explicit list is the only reliable signal.
    /// </summary>
    public static readonly HashSet<string> FoolsVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.0",
        "1.RV-Pre1",
        "3D Shareware v1.34",
        "20w14infinite",
        "22w13oneblockatatime",
        "23w13a_or_b",
        "24w14potato",
        "25w14craftmine",
        "15w14a",
        "26w14a",
        "20w14∞",
    };

    /// <summary>
    /// Scans every version folder under a game folder.
    /// </summary>
    public IReadOnlyList<InstanceInfo> Scan(
        string gameFolder,
        CancellationToken cancellationToken = default)
    {
        var versionsDir = Path.Combine(gameFolder, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return [];
        }

        var results = new List<InstanceInfo>();
        bool firstScan;
        lock (_scannedFolders) firstScan = _scannedFolders.Add(Path.GetFullPath(gameFolder));

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(Path.Combine(dir, ".97launch-installing"))) continue;
            var ignore = Path.Combine(dir, ".pclignore");
            if (File.Exists(ignore))
            {
                if (!firstScan) continue;
                try { File.Delete(ignore); }
                catch (IOException ex) { _log.LogWarning(ex, "无法清理忽略标记：{Path}", ignore); }
                catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "无法清理忽略标记：{Path}", ignore); }
            }

            var name = Path.GetFileName(dir);
            var info = TryRead(dir, name, versionsDir);
            if (info is not null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Runs <see cref="Scan"/> off the calling thread.
    /// </summary>
    /// <remarks>
    /// The scan is pure filesystem work with no thread affinity, so a plain
    /// <see cref="Task.Run(Action)"/> is enough; there is nothing to marshal back
    /// because the result is returned rather than applied to the UI.
    /// </remarks>
    public Task<IReadOnlyList<InstanceInfo>> ScanAsync(
        string gameFolder,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(gameFolder, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads one version folder, applying PCL's skip rules first.
    /// </summary>
    /// <returns><c>null</c> when the folder is skipped entirely.</returns>
    private InstanceInfo? TryRead(string dir, string name, string versionsDir)
    {
        // PCL skips empty folders: they are failed downloads, not instances.
        try
        {
            if (!Directory.EnumerateFiles(dir).Any())
            {
                _log.LogInformation("跳过空文件夹：{Path}", dir);
                return null;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "无法读取文件夹：{Path}", dir);
            return null;
        }

        // PCL skips cache/BLClient/PCL unless a json with the same name exists,
        // because those are launcher bookkeeping directories that very occasionally
        // double as real versions.
        if (name is "cache" or "BLClient" or "PCL"
            && !File.Exists(Path.Combine(dir, name + ".json")))
        {
            _log.LogInformation("跳过可能不是版本文件夹的项目：{Path}", dir);
            return null;
        }

        try
        {
            return Load(dir, name, versionsDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取实例失败：{Path}", dir);
            return Error(dir, name, ex.Message);
        }
    }

    /// <summary>
    /// Reads the version json and derives everything the card needs.
    /// </summary>
    private InstanceInfo Load(string dir, string name, string versionsDir)
    {
        JsonObject? root;
        try
        {
            root = InstanceProfileReader.Read(dir);
        }
        catch (Exception ex)
        {
            return Error(dir, name, "版本 JSON 解析失败：" + ex.Message);
        }

        if (root is null)
        {
            return Error(dir, name, "版本 JSON 解析失败");
        }

        var inherit = root["inheritsFrom"]?.GetValue<string>() ?? "";
        var chain = ReadChain(root, inherit, versionsDir, name);
        var merged = chain.AsEnumerable().Reverse().Aggregate(new JsonObject(), InstanceProfileReader.Merge);

        // PCL checks the expanded JSON so a child can inherit its main class.
        if (merged["mainClass"] is null)
        {
            return Error(dir, name, "版本 JSON 中没有 mainClass 项");
        }

        // Resolve inheritsFrom, and fail when the prerequisite is missing. PCL
        // reports exactly this, which is far more useful than "unrecognised".
        // The full json chain, this version last, used to detect loaders the way
        // PCL's McVersion does: by the merged libraries and main class.
        var installManifest = inherit.Length == 0 ? InstanceInstallManifest.TryLoad(dir) : null;
        var manifestVersion = installManifest?.MinecraftVersion;
        var vanillaName = string.IsNullOrWhiteSpace(manifestVersion)
            ? ResolveVanillaName(merged, inherit, name, dir)
            : manifestVersion;

        if (string.IsNullOrEmpty(vanillaName))
        {
            return Error(dir, name, "版本号获取失败：结果为空");
        }

        var librariesText = string.Join("\n", chain.Select(c => c["libraries"]?.ToJsonString() ?? ""));
        var mainClasses = string.Join("\n", chain.Select(c => c["mainClass"]?.GetValue<string>() ?? ""));

        // PCL reads OptiFine from the version name/library, and treats a
        // Forge-style "OptiFine as a mod" install the same way.
        var optiFineVersion = DetectOptiFine(merged, librariesText)
            ?? installManifest?.OptiFineVersion;
        var hasOptiFine = optiFineVersion is not null;

        var liteLoader = librariesText.Contains("liteloader", StringComparison.OrdinalIgnoreCase);

        var fabricVersion = DetectLoaderVersion(merged, librariesText, "net.fabricmc", "fabric-loader")
            ?? DetectLoaderVersion(merged, librariesText, "org.quiltmc", "quilt-loader");
        var forgeVersion = DetectForge(merged, librariesText, mainClasses);
        var neoForgeVersion = DetectNeoForge(merged, librariesText, mainClasses);
        if (neoForgeVersion is not null) { neoForgeVersion = ArgumentValue(merged, "--fml.neoForgeVersion") ?? ArgumentValue(merged, "--fml.forgeVersion") ?? neoForgeVersion; forgeVersion = null; }

        // PCL's classification order: Fool / Snapshot first, then OptiFine,
        // LiteLoader, then Fabric > Forge > NeoForge (first match wins).
        var kind = Classify(
            vanillaName, hasOptiFine, liteLoader, fabricVersion, forgeVersion, neoForgeVersion);

        var releaseTime = ParseReleaseTime(root, chain);
        if (vanillaName == "Unknown") kind = InstanceKind.Error;
        else if (kind is InstanceKind.Original or InstanceKind.Snapshot or InstanceKind.Fool or InstanceKind.Error)
        {
            var jsonType = merged["type"]?.ToString();
            var fixedDate = releaseTime.ToUniversalTime().AddHours(2);
            if (jsonType == "fool" || fixedDate.Month == 4 && fixedDate.Day == 1 && jsonType == "snapshot") kind = InstanceKind.Fool;
            else if (jsonType is "snapshot" or "pending" || name.Contains("combat", StringComparison.OrdinalIgnoreCase)) kind = InstanceKind.Snapshot;
        }

        return new InstanceInfo(
            name,
            dir,
            vanillaName,
            kind,
            hasOptiFine,
            optiFineVersion,
            liteLoader,
            fabricVersion,
            forgeVersion,
            neoForgeVersion,
            releaseTime,
            Describe(kind, vanillaName, fabricVersion, forgeVersion, neoForgeVersion, optiFineVersion),
            IsStar: ReadIniMarker(dir, "IsStar"),
            IsCustomNamed: false);
    }

    private static InstanceInfo Error(string dir, string name, string message)
        => new(name, dir, "Unknown", InstanceKind.Error,
            false, null, false, null, null, null,
            DateTime.MinValue, message, false, false);

    /// <summary>
    /// Builds the inheritance chain, this version first and the base last.
    /// </summary>
    /// <remarks>
    /// A cycle would otherwise loop forever; the depth cap turns a corrupt folder
    /// into a clean failure instead of a hang.
    /// </remarks>
    private static List<JsonObject> ReadChain(JsonObject root, string inherit, string versionsDir, string name)
    {
        var chain = new List<JsonObject> { root };
        var currentName = inherit;
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };

        while (currentName.Length > 0)
        {
            InstanceProfileReader.ValidateParentName(currentName);
            if (++depth >= 16 || !seen.Add(currentName)) throw new InvalidDataException("版本依赖项出现嵌套或循环：" + currentName);
            JsonObject parent;
            try { parent = InstanceProfileReader.Read(Path.Combine(versionsDir, currentName)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new InvalidDataException($"需要安装 {currentName} 作为前置版本", ex); }
            chain.Add(parent);
            currentName = parent["inheritsFrom"]?.GetValue<string>() ?? "";
        }

        return chain;
    }

    /// <summary>
    /// The vanilla version id, taken from the end of the inheritance chain.
    /// </summary>
    /// <remarks>
    /// A base version's vanilla name is its own id, and PCL falls back to the
    /// folder name when the json has no usable id. Falling back here matters
    /// because a hand-copied folder often carries a stale <c>id</c> from the
    /// version it was copied from.
    /// </remarks>
    private static string ResolveVanillaName(JsonObject root, string inherit, string folderName, string directory)
    {
        var date = ParseReleaseTime(root, []);
        if (date.Year is > 2000 and < 2013) return "Old";
        if (root["type"]?.ToString() == "pending") return "pending";
        if (root["clientVersion"] is { } client) return client.ToString();
        if (ArgumentValue(root, "--fml.mcVersion") is { Length: > 0 } fml) return fml;
        if (inherit.Length > 0) return root["jar"]?.ToString() ?? inherit;
        var download = Regex.Match(root["downloads"]?.ToJsonString() ?? "", @"launcher\.mojang\.com/mc/game/([^/]+)");
        if (download.Success) return download.Groups[1].Value;
        var libraries = root["libraries"]?.ToJsonString() ?? "";
        foreach (var pattern in new[] { @"net\.minecraftforge:(?:forge|fmlloader):([0-9]{1,2}\.[0-9.]+)", @"optifine:OptiFine:([0-9]{1,2}\.[0-9.]+)", @"(?:fabricmc|quiltmc):intermediary:([0-9a-zA-Z.+_-]+)" })
        {
            var match = Regex.Match(libraries, pattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }
        if (root["jar"] is { } jar) return jar.ToString();
        try
        {
            var jarPath = Path.Combine(directory, Path.GetFileName(directory) + ".jar");
            if (File.Exists(jarPath))
            {
                using var archive = ZipFile.OpenRead(jarPath);
                if (archive.GetEntry("version.json") is { Length: < 1048576 } entry)
                {
                    using var stream = entry.Open();
                    var json = JsonNode.Parse(stream);
                    if (json?["name"]?.ToString() is { Length: > 0 and < 32 } jarName) return jarName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        const string versionPattern = @"(?:[1-9][0-9]w[0-9]{2}[a-g]|(?:1|[2-9][0-9])\.[0-9]+(?:\.[0-9]+)?(?:-(?:pre|rc|snapshot-?)[0-9]*| Pre-Release(?: [0-9])?)?)";
        foreach (var candidate in new[] { root["id"]?.ToString() ?? "", folderName })
        {
            if (FoolsVersions.Contains(candidate) || OldPattern.IsMatch(candidate)) return candidate;
            var match = Regex.Match(candidate, versionPattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
        }
        var withoutLibraries = (JsonObject)root.DeepClone(); withoutLibraries.Remove("libraries");
        var guess = Regex.Match(withoutLibraries.ToJsonString(), versionPattern, RegexOptions.IgnoreCase);
        return guess.Success ? guess.Value : "Unknown";
    }

    private static string? ArgumentValue(JsonObject root, string name)
    {
        if (root["arguments"]?["game"] is not JsonArray args) return null;
        for (var i = 0; i + 1 < args.Count; i++) if (args[i]?.ToString() == name) return args[i + 1]?.ToString();
        return null;
    }

    /// <summary>
    /// Detects OptiFine and its version.
    /// </summary>
    /// <remarks>
    /// Modern OptiFine ships as a Forge/standalone mod, so its version lives in a
    /// library coordinate like <c>optifine:OptiFine:1.20.1_HD_U_I5</c>. Older
    /// installs encode it in the version id instead.
    /// </remarks>
    private static string? DetectOptiFine(JsonObject root, string librariesText)
    {
        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var entry in libraries)
            {
                var coord = entry?["name"]?.GetValue<string>();
                if (coord is null || !coord.Contains("optifine", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = coord.Split(':');
                if (parts.Length >= 3)
                {
                    return parts[2];
                }
            }
        }

        var id = root["id"]?.GetValue<string>() ?? "";
        if (id.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            var dash = id.IndexOf("OptiFine", StringComparison.OrdinalIgnoreCase);
            return id[(dash + "OptiFine".Length)..].Trim('-', '_') is { Length: > 0 } tail
                ? tail
                : "未知版本";
        }

        if (librariesText.Contains("optifine", StringComparison.OrdinalIgnoreCase))
        {
            return "未知版本";
        }

        return null;
    }

    /// <summary>Extracts a loader version from library coordinates.</summary>
    private static string? DetectLoaderVersion(
        JsonObject root, string librariesText, string groupMarker, string artifactMarker)
    {
        if (!librariesText.Contains(groupMarker, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var entry in libraries)
            {
                var coord = entry?["name"]?.GetValue<string>();
                var parts = coord?.Split(':');
                if (parts is { Length: >= 3 } && parts[0] == groupMarker && parts[1] == artifactMarker)
                {
                    return string.IsNullOrWhiteSpace(parts[2]) ? "未知版本" : parts[2];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Detects Forge.
    /// </summary>
    /// <remarks>
    /// NeoForge's coordinates are <c>net.neoforged</c>, which does not contain
    /// <c>net.minecraftforge</c>, so a plain substring test is safe here.
    /// </remarks>
    private static string? DetectForge(
        JsonObject root, string librariesText, string mainClasses)
    {
        var byLibrary = librariesText.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase);
        var byMain = mainClasses.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);

        if (!byLibrary && !byMain)
        {
            return null;
        }

        if (ArgumentValue(root, "--fml.forgeVersion") is { Length: > 0 } argumentVersion)
            return argumentVersion;

        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var entry in libraries)
            {
                var coord = entry?["name"]?.GetValue<string>();
                var parts = coord?.Split(':');
                if (parts is { Length: >= 3 } && parts[0] == "net.minecraftforge"
                    && parts[1] is "forge" or "fmlloader" or "minecraftforge")
                {
                    // Forge uses "1.20.1-47.1.3"; the trailing part is the loader.
                    var version = parts[2];
                    var dash = version.IndexOf('-');
                    return dash >= 0 ? version[(dash + 1)..] : version;
                }
            }
        }

        return "未知版本";
    }

    private static string? DetectNeoForge(JsonObject root, string librariesText, string mainClasses)
    {
        var byLibrary = librariesText.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase);
        var byMain = mainClasses.Contains("neoforged", StringComparison.OrdinalIgnoreCase);

        if (!byLibrary && !byMain)
        {
            return null;
        }

        if ((ArgumentValue(root, "--fml.neoForgeVersion") ?? ArgumentValue(root, "--fml.forgeVersion")) is { Length: > 0 } argumentVersion)
            return argumentVersion;

        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var entry in libraries)
            {
                var coord = entry?["name"]?.GetValue<string>();
                var parts = coord?.Split(':');
                if (parts is { Length: >= 3 } && parts[0] == "net.neoforged" && parts[1] is "neoforge" or "forge")
                {
                    return parts[2];
                }
            }
        }

        return "未知版本";
    }

    /// <summary>
    /// PCL's state machine, in its original precedence order
    /// (<c>ModMinecraft.vb:635-663</c>).
    /// </summary>
    public static InstanceKind Classify(
        string vanillaName,
        bool hasOptiFine,
        bool hasLiteLoader,
        string? fabric,
        string? forge,
        string? neoForge)
    {
        if (vanillaName.Length == 0)
        {
            return InstanceKind.Error;
        }

        // Alpha and beta builds. PCL's McVersion normalises these to the sentinel
        // "Old" before McInstance sees them, so matching that string alone would
        // never fire on a real folder; the ids themselves have to be recognised.
        if (OldPattern.IsMatch(vanillaName)
            || string.Equals(vanillaName, "Old", StringComparison.OrdinalIgnoreCase))
        {
            return InstanceKind.Old;
        }

        var kind = InstanceKind.Original;

        // PCL never overwrites an earlier assignment with a "weaker" one; these
        // are plain sequential overrides, so the last one to fire wins.
        if (FoolsVersions.Contains(vanillaName) || vanillaName.StartsWith("2.0", StringComparison.OrdinalIgnoreCase)
            || vanillaName.StartsWith("20w14inf", StringComparison.OrdinalIgnoreCase))
        {
            kind = InstanceKind.Fool;
        }
        else if (SnapshotPattern.IsMatch(vanillaName))
        {
            kind = InstanceKind.Snapshot;
        }

        if (hasOptiFine)
        {
            kind = InstanceKind.OptiFine;
        }

        if (hasLiteLoader)
        {
            kind = InstanceKind.LiteLoader;
        }

        if (fabric is not null)
        {
            kind = InstanceKind.Fabric;
        }
        else if (forge is not null)
        {
            kind = InstanceKind.Forge;
        }
        else if (neoForge is not null)
        {
            kind = InstanceKind.NeoForge;
        }

        // A version id that is not a Minecraft version and carries no loader is
        // an unrelated folder, not a release.
        if (kind == InstanceKind.Original && !LooksLikeMinecraftVersion(vanillaName))
        {
            return InstanceKind.Error;
        }

        return kind;
    }

    private static bool LooksLikeMinecraftVersion(string id)
        => ReleasePattern.IsMatch(id) || SnapshotPattern.IsMatch(id) || OldPattern.IsMatch(id);

    private static DateTime ParseReleaseTime(JsonObject root, List<JsonObject> chain)
    {
        foreach (var json in chain)
        {
            var raw = json["releaseTime"]?.GetValue<string>();
            if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
        }

        return new DateTime(1970, 1, 1, 15, 0, 0);
    }

    /// <summary>
    /// PCL's <c>VersionDisplayName</c> (<c>ModMinecraft.vb:746-781</c>).
    /// </summary>
    internal static string Describe(
        InstanceKind kind,
        string vanillaName,
        string? fabric,
        string? forge,
        string? neoForge,
        string? optiFine)
    {
        var loaders = "";
        if (forge is not null)
        {
            loaders += ", Forge" + (forge == "未知版本" ? "" : " " + forge);
        }

        if (neoForge is not null)
        {
            loaders += ", NeoForge" + (neoForge == "未知版本" ? "" : " " + neoForge);
        }

        if (fabric is not null)
        {
            loaders += ", Fabric" + (fabric == "未知版本" ? "" : " " + fabric);
        }

        if (optiFine is not null)
        {
            loaders += ", OptiFine" + (optiFine == "未知版本"
                ? ""
                : " " + optiFine.Replace("-", " ").Replace("_", " "));
        }

        var info = kind switch
        {
            InstanceKind.Old => "远古版本",
            InstanceKind.Fool => "愚人节版本 " + vanillaName,
            InstanceKind.Error => "无法识别该版本",
            _ when vanillaName.Contains("pre", StringComparison.OrdinalIgnoreCase) => "预发布版 " + vanillaName,
            _ when vanillaName.Contains("rc", StringComparison.OrdinalIgnoreCase) => "发布候选 " + vanillaName,
            _ when vanillaName.Contains("experimental", StringComparison.OrdinalIgnoreCase) => "实验性快照" + vanillaName,
            _ when vanillaName == "pending" => "实验性快照",
            InstanceKind.Snapshot => "快照版 " + vanillaName.Replace("-snapshot", ""),
            _ => "正式版 " + vanillaName,
        };

        return (info + loaders).Replace("_", "-");
    }

    /// <summary>
    /// Reads a per-instance preference marker from PCL's own setup file.
    /// </summary>
    /// <remarks>
    /// Deliberately reads <c>PCL\Setup.ini</c> rather than a 97Launch-specific
    /// file: a user who already curates their list in PCL expects those stars and
    /// hidden flags to carry over, and duplicating the metadata would let the two
    /// launchers disagree.
    /// </remarks>
    private static bool ReadIniMarker(string dir, string key)
    {
        var file = Path.Combine(dir, "PCL", "Setup.ini");
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed[(key.Length + 1)..].Trim();
                return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("1", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A malformed setup file just means no marker.
        }

        return false;
    }
}
