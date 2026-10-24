using FluentAssertions;
using Launcher.Core.Instances;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// Tests for <see cref="InstanceIdentity"/>.
/// </summary>
/// <remarks>
/// The point of this type is that one folder is one instance, no matter how the
/// path was typed. These cases are the exact variations a user produces: copying
/// a path from Explorer (backslashes, maybe a trailing one), from a shell
/// (forward slashes), and with different casing.
/// </remarks>
public sealed class InstanceIdentityTests
{
    [Theory]
    [InlineData(@"C:\Games\.minecraft\versions\1.20.1")]
    [InlineData(@"C:\Games\.minecraft\versions\1.20.1\")]
    [InlineData(@"c:\games\.minecraft\versions\1.20.1")]
    [InlineData(@"C:/Games/.minecraft/versions/1.20.1")]
    [InlineData(@"C:\Games\.minecraft\versions\1.20.1   ")]
    public void Same_folder_in_any_spelling_is_the_same_id(string path)
        => InstanceIdentity.ForPath(path)
            .Should().Be(InstanceIdentity.ForPath(@"C:\Games\.minecraft\versions\1.20.1"));

    [Fact]
    public void Different_folders_get_different_ids()
        => InstanceIdentity.ForPath(@"C:\mc\versions\1.20.1")
            .Should().NotBe(InstanceIdentity.ForPath(@"C:\mc\versions\1.20.4"));

    [Fact]
    public void Id_is_stable_across_calls()
    {
        const string Path = @"D:\Minecraft\versions\1.20.1-forge";
        var first = InstanceIdentity.ForPath(Path);

        for (var i = 0; i < 5; i++)
        {
            InstanceIdentity.ForPath(Path).Should().Be(first);
        }
    }

    [Fact]
    public void Id_is_short_and_filename_safe()
    {
        var id = InstanceIdentity.ForPath(@"C:\Games\.minecraft\versions\1.20.1");

        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void Normalize_keeps_the_drive_and_separators()
        => InstanceIdentity.NormalizePath(@"c:/Games/mc/")
            .Should().Be(@"C:\GAMES\MC");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_rejected(string path)
    {
        var act = () => InstanceIdentity.ForPath(path);
        act.Should().Throw<ArgumentException>();
    }
}
