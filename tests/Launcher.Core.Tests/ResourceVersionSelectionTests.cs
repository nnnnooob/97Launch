using FluentAssertions;
using Launcher.Core.Downloads;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class ResourceVersionSelectionTests
{
    [Theory]
    [InlineData("26.3")]
    [InlineData("1.21.11")]
    [InlineData("26.4-snapshot-5")]
    public void Only_the_target_version_and_loader_group_is_expanded(string game)
    {
        var old = Version("old", game, "fabric", 1);
        var latest = Version("latest", game, "fabric", 3);
        var otherLoader = Version("neoforge", game, "neoforge", 4);
        var otherGame = Version("other", "1.20.1", "fabric", 5);
        var groups = ResourceVersionSelection.Group([old, otherLoader, otherGame, latest], game, "fabric", null, null, true, false);

        groups[0].Title.Should().Be("Fabric " + game);
        groups[0].Versions.Select(version => version.Id).Should().Equal("latest", "old");
        groups.Where(group => group.IsExpanded).Should().ContainSingle().Which.Should().Be(groups[0]);
        ResourceVersionSelection.IsCompatible(otherLoader, game, "fabric").Should().BeFalse();
        ResourceVersionSelection.IsCompatible(latest, "正式版 " + game, "fabric").Should().BeFalse();
    }

    [Fact]
    public void Keeps_an_empty_target_group_instead_of_opening_an_unrelated_group()
    {
        var beta = Version("beta", "26.3", "fabric", 2) with { Type = "beta" };
        var other = Version("other", "1.21.11", "fabric", 1);
        var groups = ResourceVersionSelection.Group([beta, other], "26.3", "fabric", null, null, true, false);
        groups[0].IsExpanded.Should().BeTrue();
        groups[0].Versions.Should().BeEmpty();
        groups[1].IsExpanded.Should().BeFalse();
        ResourceVersionSelection.Group([beta, other], "26.3", "fabric", null, null, false, false)
            [0].Versions.Should().ContainSingle().Which.Id.Should().Be("beta");
    }

    [Fact]
    public void Switching_target_opens_the_new_target_group()
    {
        var versions = new[] { Version("one", "26.3", "fabric", 2), Version("two", "1.21.11", "neoforge", 1) };
        var groups = ResourceVersionSelection.Group(versions, "1.21.11", "neoforge", "1.21.11", "neoforge", true, false);
        groups.Should().ContainSingle().Which.Title.Should().Be("NeoForge 1.21.11");
        groups[0].Versions.Should().ContainSingle().Which.Id.Should().Be("two");
        groups[0].IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Resource_packs_match_the_game_without_a_mod_loader_requirement()
    {
        var pack = Version("pack", "26.3", "minecraft", 1);
        ResourceVersionSelection.IsCompatible(pack, "26.3", "").Should().BeTrue();
        ResourceVersionSelection.Group([pack], "26.3", "", "26.3", null, true, false)
            [0].Versions.Should().ContainSingle();
    }

    private static ModrinthVersion Version(string id, string game, string loader, int day) =>
        new(id, id, id, "release", new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), [game], [loader], [], []);
}
