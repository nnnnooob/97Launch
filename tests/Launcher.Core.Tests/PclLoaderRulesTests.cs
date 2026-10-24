using FluentAssertions;
using Launcher.Core.Install;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class PclLoaderRulesTests
{
    [Theory]
    [InlineData(null, "Forge", true)]
    [InlineData("Forge", "Forge", true)]
    [InlineData("Forge", "Fabric", false)]
    [InlineData("NeoForge", "Forge", false)]
    public void Primary_loader_selection_matches_pcl(string? selected, string candidate, bool expected)
        => PclLoaderRules.CanSelectPrimary(selected, candidate).Should().Be(expected);

    [Theory]
    [InlineData("1.20.4", true)]
    [InlineData("1.20.5", false)]
    [InlineData("26.3", false)]
    public void Fabric_OptiFine_compatibility_matches_pcl(string version, bool expected)
        => PclLoaderRules.SupportsOptiFineWithFabric(version).Should().Be(expected);

    [Theory]
    [InlineData("1.12.2", false)]
    [InlineData("1.13", true)]
    [InlineData("1.14.3", true)]
    [InlineData("1.14.4", false)]
    public void Forge_and_OptiFine_exclusion_range_matches_pcl(string version, bool expected)
        => PclLoaderRules.IsForgeOptiFineRangeUnsupported(version).Should().Be(expected);

    [Theory]
    [InlineData("1.13.2", true)]
    [InlineData("1.14", false)]
    [InlineData("1.15.2", false)]
    [InlineData("1.16.1", true)]
    [InlineData("1.20.4", true)]
    public void OptiFabric_support_range_matches_pcl(string version, bool expected)
        => PclLoaderRules.SupportsOptiFabric(version).Should().Be(expected);

    [Theory]
    [InlineData("47.1.82", "1.20.1", "47.1.82", true)]
    [InlineData("47.1.82", "1.20.1", "47.1.83", false)]
    [InlineData("82", "1.20.1", "47.1.82", true)]
    [InlineData("", "1.20.1", "47.1.82", true)]
    [InlineData(null, "1.20.1", "47.1.82", false)]
    [InlineData("N/A", "1.20.1", "47.1.82", false)]
    [InlineData("47.1.82", "1.19.4", "1.20.1-47.1.82", false)]
    public void OptiFine_Forge_requirement_matches_pcl(
        string? requirement, string minecraftVersion, string forgeVersion, bool expected)
        => PclLoaderRules.IsOptiFineCompatibleWithForge(requirement, minecraftVersion, forgeVersion)
            .Should().Be(expected);

    [Theory]
    [InlineData("1.20.1-47.1.99", "1.20.1")]
    [InlineData("20.4.30-beta", "1.20.4")]
    [InlineData("26.1.0.0-alpha.1+snapshot-1", "26.1-snapshot-1")]
    [InlineData("0.25w14craftmine.3-beta", "25w14craftmine")]
    public void NeoForge_api_versions_map_to_the_pcl_minecraft_version(string apiVersion, string expected)
    {
        PclLoaderRules.TryMapNeoForgeMinecraftVersion(apiVersion, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("0.25w14craftmine")]
    public void Invalid_NeoForge_api_versions_are_rejected(string apiVersion)
        => PclLoaderRules.TryMapNeoForgeMinecraftVersion(apiVersion, out _).Should().BeFalse();
}
