using FluentAssertions;
using Launcher.Core.Instances;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// Classification tests for <see cref="InstanceScanner.Classify"/>.
/// </summary>
/// <remarks>
/// This is PCL's precedence chain and it is easy to break silently: a wrong order
/// still produces <em>a</em> kind, just the wrong one, and the mistake only shows
/// up as an instance sitting in the wrong section. The cases below pin each rule
/// and, more importantly, the precedence between rules that overlap.
/// </remarks>
public sealed class InstanceClassificationTests
{
    [Theory]
    [InlineData("1.20.1")]
    [InlineData("1.7.10")]
    [InlineData("1.0")]
    public void Plain_release_is_original(string version)
        => InstanceScanner.Classify(version, false, false, null, null, null)
            .Should().Be(InstanceKind.Original);

    [Theory]
    [InlineData("24w14a")]
    [InlineData("1.20.5-pre1")]
    [InlineData("1.20.5-rc1")]
    [InlineData("1.16-snapshot")]
    public void Snapshot_ids_are_snapshots(string version)
        => InstanceScanner.Classify(version, false, false, null, null, null)
            .Should().Be(InstanceKind.Snapshot);

    [Theory]
    [InlineData("a1.2.6")]
    [InlineData("b1.7.3")]
    [InlineData("c0.30")]
    public void Alpha_and_beta_are_old(string version)
        => InstanceScanner.Classify(version, false, false, null, null, null)
            .Should().Be(InstanceKind.Old);

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.RV-Pre1")]
    [InlineData("24w14potato")]
    [InlineData("23w13a_or_b")]
    public void April_fools_versions_are_fools(string version)
        => InstanceScanner.Classify(version, false, false, null, null, null)
            .Should().Be(InstanceKind.Fool);

    [Fact]
    public void Each_loader_is_recognised()
    {
        InstanceScanner.Classify("1.20.1", false, false, "0.15.7", null, null)
            .Should().Be(InstanceKind.Fabric);
        InstanceScanner.Classify("1.20.1", false, false, null, "47.1.3", null)
            .Should().Be(InstanceKind.Forge);
        InstanceScanner.Classify("1.20.1", false, false, null, null, "20.4.30")
            .Should().Be(InstanceKind.NeoForge);
        InstanceScanner.Classify("1.20.1", false, true, null, null, null)
            .Should().Be(InstanceKind.LiteLoader);
        InstanceScanner.Classify("1.20.1", true, false, null, null, null)
            .Should().Be(InstanceKind.OptiFine);
    }

    [Fact]
    public void Fabric_beats_forge_when_both_present()
    {
        // The chain is an else-if, so the first loader that fires wins. Fabric is
        // tested first; a regression that swaps the order would flip this.
        InstanceScanner.Classify("1.20.1", false, false, "0.15.7", "47.1.3", "20.4.30")
            .Should().Be(InstanceKind.Fabric);

        InstanceScanner.Classify("1.20.1", false, false, null, "47.1.3", "20.4.30")
            .Should().Be(InstanceKind.Forge);
    }

    [Fact]
    public void Liteloader_overrides_optifine()
    {
        // Both are plain assignments in order, so the later one wins.
        InstanceScanner.Classify("1.20.1", true, true, null, null, null)
            .Should().Be(InstanceKind.LiteLoader);
    }

    [Fact]
    public void Loader_overrides_fools_and_snapshot()
    {
        // A modded 24w14potato install is a moddable instance, not a curio.
        InstanceScanner.Classify("24w14potato", false, false, "0.15.7", null, null)
            .Should().Be(InstanceKind.Fabric);

        InstanceScanner.Classify("24w14a", false, false, null, "47.1.3", null)
            .Should().Be(InstanceKind.Forge);
    }

    [Fact]
    public void Old_short_circuits_everything()
    {
        // Old is returned before any loader test, exactly as PCL does.
        InstanceScanner.Classify("b1.7.3", false, true, "0.15.7", "47.1.3", null)
            .Should().Be(InstanceKind.Old);
    }

    [Theory]
    [InlineData("broken-test")]
    [InlineData("my custom folder")]
    [InlineData("")]
    public void Unrecognised_ids_without_a_loader_are_errors(string version)
        => InstanceScanner.Classify(version, false, false, null, null, null)
            .Should().Be(InstanceKind.Error);

    [Fact]
    public void Unrecognised_id_with_a_loader_is_still_moddable()
    {
        // The "not a Minecraft version" guard only applies to vanilla, so a
        // hand-wrapped Forge instance under an odd folder name still shows up.
        InstanceScanner.Classify("my pack", false, false, null, "47.1.3", null)
            .Should().Be(InstanceKind.Forge);
    }
}
