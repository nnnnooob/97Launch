using FluentAssertions;
using Launcher.Core.Versions;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// Tests for <see cref="MinecraftVersionService.Parse"/>.
/// </summary>
/// <remarks>
/// The manifest is Mojang's document, not ours, so parsing has to survive shapes
/// we did not anticipate: missing URLs, unparseable dates, extra fields. The
/// samples below are trimmed fixtures in the real format rather than invented
/// JSON, because those are the fields the parser actually reads.
/// </remarks>
public sealed class VersionManifestTests
{
    private const string Sample = """
        {
          "latest": { "release": "1.21.1", "snapshot": "24w33a" },
          "versions": [
            {
              "id": "24w33a",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/aaaa/24w33a.json",
              "time": "2024-08-15T10:10:10+00:00",
              "releaseTime": "2024-08-15T10:10:10+00:00",
              "sha1": "aaaa",
              "complianceLevel": 1
            },
            {
              "id": "1.21.1",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/bbbb/1.21.1.json",
              "time": "2024-08-08T11:00:00+00:00",
              "releaseTime": "2024-08-08T11:00:00+00:00",
              "sha1": "bbbb",
              "complianceLevel": 1
            },
            {
              "id": "b1.7.3",
              "type": "old_beta",
              "url": "https://piston-meta.mojang.com/v1/packages/cccc/b1.7.3.json",
              "time": "2011-07-08T00:00:00+00:00",
              "releaseTime": "2011-07-08T00:00:00+00:00"
            }
          ]
        }
        """;

    [Fact]
    public void Parses_every_version_entry()
        => MinecraftVersionService.Parse(Sample).Should().HaveCount(3);

    [Fact]
    public void Results_are_sorted_newest_first()
        => MinecraftVersionService.Parse(Sample).Select(v => v.Id)
            .Should().ContainInOrder("24w33a", "1.21.1", "b1.7.3");

    [Fact]
    public void Types_are_classified()
    {
        var parsed = MinecraftVersionService.Parse(Sample);

        parsed.Single(v => v.Id == "1.21.1").IsRelease.Should().BeTrue();
        parsed.Single(v => v.Id == "24w33a").IsSnapshot.Should().BeTrue();
        parsed.Single(v => v.Id == "b1.7.3").IsOld.Should().BeTrue();
    }

    [Fact]
    public void Urls_are_carried_through()
    {
        var release = MinecraftVersionService.Parse(Sample).Single(v => v.Id == "1.21.1");
        release.Url.Should().Be("https://piston-meta.mojang.com/v1/packages/bbbb/1.21.1.json");
    }

    [Fact]
    public void Sha1_is_preserved_for_metadata_validation()
        => MinecraftVersionService.Parse(Sample).Single(v => v.Id == "1.21.1")
            .Sha1.Should().Be("bbbb");

    [Fact]
    public void Entries_without_an_id_are_skipped()
    {
        const string json = """
            { "versions": [ { "type": "release", "url": "x" }, { "id": "1.20.1", "type": "release" } ] }
            """;

        MinecraftVersionService.Parse(json).Should().ContainSingle()
            .Which.Id.Should().Be("1.20.1");
    }

    [Fact]
    public void Missing_url_becomes_empty_rather_than_throwing()
    {
        const string json = """
            { "versions": [ { "id": "1.20.1", "type": "release" } ] }
            """;

        MinecraftVersionService.Parse(json).Single().Url.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_date_falls_back_to_default()
    {
        const string json = """
            { "versions": [ { "id": "1.20.1", "type": "release", "releaseTime": "not a date" } ] }
            """;

        MinecraftVersionService.Parse(json).Single().ReleaseTime.Should().Be(default(DateTime));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"versions": null}""")]
    [InlineData("""{"versions": []}""")]
    [InlineData("not json at all")]
    public void Degenerate_documents_yield_no_versions(string json)
        => MinecraftVersionService.Parse(json).Should().BeEmpty();
}
