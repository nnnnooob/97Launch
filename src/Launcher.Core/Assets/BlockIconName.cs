using Launcher.Core.Instances;

namespace Launcher.Core.Assets;

/// <summary>
/// Maps an instance kind to the vanilla block texture used as its card icon.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core rather than the view because two pages need the same mapping
/// (<c>InstancesPage</c> cards and the download page's version rows) and a
/// second copy would drift. Only the bare file name is returned; turning it into
/// an <c>ms-appx:///</c> URI is the shell's job, since a headless caller has no
/// such scheme.
/// </para>
/// <para>
/// The choice of block follows PCL's <c>McInstance.Icon</c> table: one recognisable
/// texture per loader so a row can be identified before reading it.
/// </para>
/// </remarks>
public static class BlockIconName
{
    /// <summary>File name (including extension) of the block texture for a kind.</summary>
    public static string For(InstanceKind kind) => kind switch
    {
        InstanceKind.Fabric => "fabric.png",
        InstanceKind.Forge => "forge.png",
        InstanceKind.NeoForge => "neoforge.png",
        InstanceKind.Fool => "fools.png",
        InstanceKind.Snapshot => "snapshot.png",
        InstanceKind.Old => "cobblestone.png",
        InstanceKind.OptiFine => "grasspath.png",
        InstanceKind.LiteLoader => "egg.png",
        InstanceKind.Error => "redstone.png",
        _ => "vanilla.png",
    };

    /// <summary>
    /// Block texture for a version-release row on the download page.
    /// </summary>
    /// <param name="releaseType">
    /// One of the Chinese release-type labels produced by the download page
    /// (<c>正式版</c>, <c>快照</c>, <c>远古版</c>, <c>愚人节版</c>, …).
    /// </param>
    /// <remarks>
    /// Falls back to the vanilla grass block for anything unrecognised. The
    /// download page used to fall back to a <c>other.png</c> that is not in the
    /// asset folder at all, which rendered as a blank square.
    /// </remarks>
    public static string ForReleaseType(string? releaseType) => releaseType switch
    {
        "正式版" => "vanilla.png",
        "预览版" or "快照" or "预发布版" or "发布候选" => "snapshot.png",
        "远古版" => "cobblestone.png",
        "愚人节版" => "fools.png",
        _ => "vanilla.png",
    };
}
