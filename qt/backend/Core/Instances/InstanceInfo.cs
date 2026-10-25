namespace Launcher.Core.Instances;

/// <summary>
/// How an instance is classified. Mirrors PCL's <c>McInstance.McInstanceState</c>.
/// </summary>
public enum InstanceKind
{
    /// <summary>Vanilla release (<c>Original</c> in PCL).</summary>
    Original,

    /// <summary>Snapshot, pre-release or release candidate.</summary>
    Snapshot,

    /// <summary>Alpha or beta build (<c>Old</c> in PCL).</summary>
    Old,

    /// <summary>April Fools version.</summary>
    Fool,

    /// <summary>Fabric.</summary>
    Fabric,

    /// <summary>Forge.</summary>
    Forge,

    /// <summary>NeoForge.</summary>
    NeoForge,

    /// <summary>OptiFine, standalone or installed as a mod.</summary>
    OptiFine,

    /// <summary>LiteLoader.</summary>
    LiteLoader,

    /// <summary>Folder exists but the version cannot be launched.</summary>
    Error,
}

/// <summary>
/// One discovered Minecraft instance.
/// </summary>
/// <remarks>
/// An immutable snapshot of what the disk said at scan time. Nothing here reads
/// the filesystem again, so a card can be shown long after its folder is gone;
/// callers that need current state re-scan.
/// </remarks>
public sealed record InstanceInfo(
    string Name,
    string Path,
    string VanillaName,
    InstanceKind Kind,
    bool HasOptiFine,
    string? OptiFineVersion,
    bool HasLiteLoader,
    string? FabricVersion,
    string? ForgeVersion,
    string? NeoForgeVersion,
    DateTime ReleaseTime,
    string Info,
    bool IsStar,
    bool IsCustomNamed)
{
    /// <summary>
    /// Whether the instance can take mods.
    /// </summary>
    /// <remarks>
    /// PCL's criterion, verbatim: any of Forge, NeoForge, LiteLoader or Fabric.
    /// OptiFine alone does <em>not</em> qualify, because OptiFine is loaded as a
    /// jar patch and other mods still cannot be installed.
    /// </remarks>
    public bool IsModdable => FabricVersion is not null
        || ForgeVersion is not null
        || NeoForgeVersion is not null
        || HasLiteLoader;

    /// <summary>
    /// Loader name for the card chip, in PCL's display order.
    /// </summary>
    public string LoaderDisplay => Kind switch
    {
        InstanceKind.Fabric => "Fabric",
        InstanceKind.Forge => "Forge",
        InstanceKind.NeoForge => "NeoForge",
        InstanceKind.OptiFine => "OptiFine",
        InstanceKind.LiteLoader => "LiteLoader",
        InstanceKind.Snapshot => "快照",
        InstanceKind.Old => "远古版",
        InstanceKind.Fool => "愚人节版",
        InstanceKind.Error => "无法识别",
        _ => "原版",
    };

    /// <summary>
    /// Bundled block artwork for this instance, matching PCL's mapping
    /// (<c>ModMinecraft.vb:669-690</c>).
    /// </summary>
    /// <remarks>
    /// A file name rather than an <c>ms-appx</c> URI: Core has no idea how the
    /// shell resolves assets, so it reports the intent and the view decides the
    /// scheme. <see cref="Assets.BlockIconName"/> holds the shared mapping.
    /// </remarks>
    public string IconName => Assets.BlockIconName.For(Kind);
}
