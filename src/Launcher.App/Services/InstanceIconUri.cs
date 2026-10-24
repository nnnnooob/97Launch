namespace Launcher.App.Services;

/// <summary>
/// View-model friendly adapter for instance block asset URIs.
/// </summary>
internal static class InstanceIconUri
{
    public static string For(Launcher.Core.Instances.InstanceKind kind)
        => $"ms-appx:///Assets/Blocks/{Launcher.Core.Assets.BlockIconName.For(kind)}";
}
