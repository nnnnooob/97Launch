using System.Text.Json;

namespace Launcher.Core.Instances;

/// <summary>Records the base game version behind a merged launcher profile.</summary>
public sealed record InstanceInstallManifest(
    int SchemaVersion,
    string MinecraftVersion,
    string? Loader,
    string? LoaderVersion,
    string? LiteLoaderVersion = null,
    string? OptiFineVersion = null,
    string? FabricApiVersion = null,
    string? OptiFabricVersion = null,
    IReadOnlyList<ManagedComponentFile>? ManagedFiles = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string GetPath(string instancePath)
        => Path.Combine(instancePath, "97Launch", "InstallManifest.json");

    public static InstanceInstallManifest? TryLoad(string instancePath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<InstanceInstallManifest>(File.ReadAllText(GetPath(instancePath)), JsonOptions);
            return manifest is { SchemaVersion: > 0 } && !string.IsNullOrWhiteSpace(manifest.MinecraftVersion)
                ? manifest
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string instancePath)
    {
        var path = GetPath(instancePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed record ManagedComponentFile(string Path, string Sha1, string Component);
