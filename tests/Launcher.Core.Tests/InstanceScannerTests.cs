using FluentAssertions;
using Launcher.Core.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// Filesystem tests for <see cref="InstanceScanner"/>.
/// </summary>
/// <remarks>
/// These build real folder layouts in a temp directory rather than mocking IO:
/// the scanner's job is precisely to interpret what is on disk (skip rules,
/// inheritance, loader detection), and a mock would just assert the
/// implementation back at itself. Each test gets its own directory so they can
/// run in parallel.
/// </remarks>
public sealed class InstanceScannerTests : IDisposable
{
    private readonly string _root;
    private readonly InstanceScanner _scanner;

    public InstanceScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "97launch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "versions"));
        _scanner = new InstanceScanner(NullLogger<InstanceScanner>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    /// <summary>Writes a version folder containing the given json.</summary>
    private string WriteVersion(string name, string json, string? fileName = null)
    {
        var dir = Path.Combine(_root, "versions", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, (fileName ?? name) + ".json"), json);
        return dir;
    }

    private static string Vanilla(string id, string release = "2024-01-01T00:00:00+00:00") => $$"""
        {
          "id": "{{id}}",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "releaseTime": "{{release}}",
          "libraries": []
        }
        """;

    private static string WithLoader(string id, string inheritsFrom, string libraryCoord) => $$"""
        {
          "id": "{{id}}",
          "inheritsFrom": "{{inheritsFrom}}",
          "mainClass": "net.minecraft.launchwrapper.Launch",
          "libraries": [ { "name": "{{libraryCoord}}" } ]
        }
        """;

    // ---- baseline ----

    [Fact]
    public void Finds_a_single_vanilla_instance()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));

        var result = _scanner.Scan(_root);

        result.Should().ContainSingle();
        var info = result[0];
        info.Name.Should().Be("1.20.1");
        info.VanillaName.Should().Be("1.20.1");
        info.Kind.Should().Be(InstanceKind.Original);
    }

    [Fact]
    public void Missing_versions_folder_yields_nothing()
        => _scanner.Scan(Path.Combine(_root, "does-not-exist")).Should().BeEmpty();

    // ---- skip rules ----

    [Fact]
    public void Empty_folder_is_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "versions", "leftover"));

        _scanner.Scan(_root).Should().BeEmpty();
    }

    [Fact]
    public void Launcher_bookkeeping_folders_are_skipped()
    {
        // "cache", "BLClient" and "PCL" are launcher directories that sit under
        // versions/ in some setups; PCL skips them unless a same-named json exists.
        foreach (var name in new[] { "cache", "BLClient", "PCL" })
        {
            var dir = Path.Combine(_root, "versions", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bookkeeping.dat"), "x");
        }

        _scanner.Scan(_root).Should().BeEmpty();
    }

    [Fact]
    public void Bookkeeping_folder_with_a_matching_json_is_treated_as_a_version()
    {
        WriteVersion("PCL", Vanilla("1.20.1"));

        _scanner.Scan(_root).Should().ContainSingle()
            .Which.Name.Should().Be("PCL");
    }

    // ---- error states ----

    [Fact]
    public void Folder_without_json_is_an_error_card()
    {
        var dir = Path.Combine(_root, "versions", "nojson");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "something.dat"), "x");

        var info = _scanner.Scan(_root).Should().ContainSingle().Subject;
        info.Kind.Should().Be(InstanceKind.Error);
        info.Info.Should().Contain("未找到版本 JSON");
    }

    [Fact]
    public void Json_without_mainclass_is_an_error()
    {
        WriteVersion("1.20.1", """{ "id": "1.20.1", "type": "release" }""");

        var info = _scanner.Scan(_root).Should().ContainSingle().Subject;
        info.Kind.Should().Be(InstanceKind.Error);
        info.Info.Should().Contain("mainClass");
    }

    [Fact]
    public void Broken_json_is_an_error_not_a_crash()
    {
        WriteVersion("1.20.1", "{ this is not json");

        var info = _scanner.Scan(_root).Should().ContainSingle().Subject;
        info.Kind.Should().Be(InstanceKind.Error);
        info.Info.Should().Contain("解析失败");
    }

    [Fact]
    public void Missing_prerequisite_is_reported_by_name()
    {
        WriteVersion("1.20.1-forge-47.1.3",
            WithLoader("1.20.1-forge-47.1.3", "1.20.1", "net.minecraftforge:forge:1.20.1-47.1.3"));

        var info = _scanner.Scan(_root).Should().ContainSingle().Subject;
        info.Kind.Should().Be(InstanceKind.Error);
        info.Info.Should().Contain("1.20.1");
        info.Info.Should().Contain("前置");
    }

    // ---- inheritance and loader detection ----

    [Fact]
    public void Forge_is_detected_through_inheritance()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        WriteVersion("1.20.1-forge-47.1.3",
            WithLoader("1.20.1-forge-47.1.3", "1.20.1", "net.minecraftforge:forge:1.20.1-47.1.3"));

        var forge = _scanner.Scan(_root).Single(i => i.Name == "1.20.1-forge-47.1.3");

        forge.Kind.Should().Be(InstanceKind.Forge);
        forge.VanillaName.Should().Be("1.20.1");
        forge.ForgeVersion.Should().Be("47.1.3");
        forge.IsModdable.Should().BeTrue();
    }

    [Fact]
    public void Fabric_is_detected_from_coordinates()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        WriteVersion("fabric-1.20.1",
            WithLoader("fabric-1.20.1", "1.20.1", "net.fabricmc:fabric-loader:0.15.7"));

        var fabric = _scanner.Scan(_root).Single(i => i.Name == "fabric-1.20.1");

        fabric.Kind.Should().Be(InstanceKind.Fabric);
        fabric.FabricVersion.Should().Be("0.15.7");
    }

    [Fact]
    public void Pcl_merged_fabric_profile_uses_install_manifest_without_parent_version()
    {
        var dir = WriteVersion("26.3-Fabric 0.19.5", """
            {
              "id": "26.3-Fabric 0.19.5",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.19.5" } ]
            }
            """);
        new InstanceInstallManifest(1, "26.3", "Fabric", "0.19.5").Save(dir);

        var fabric = _scanner.Scan(_root).Should().ContainSingle().Subject;

        fabric.Kind.Should().Be(InstanceKind.Fabric);
        fabric.Name.Should().Be("26.3-Fabric 0.19.5");
        fabric.VanillaName.Should().Be("26.3");
        fabric.FabricVersion.Should().Be("0.19.5");
    }

    [Fact]
    public void NeoForge_is_not_mistaken_for_forge()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        WriteVersion("neoforge-1.20.1",
            WithLoader("neoforge-1.20.1", "1.20.1", "net.neoforged:neoforge:20.4.30"));

        var neo = _scanner.Scan(_root).Single(i => i.Name == "neoforge-1.20.1");

        neo.Kind.Should().Be(InstanceKind.NeoForge);
        neo.ForgeVersion.Should().BeNull();
        neo.NeoForgeVersion.Should().Be("20.4.30");
    }

    [Fact]
    public void Vanilla_base_version_is_found_even_when_loader_is_listed_first()
    {
        // Directory enumeration order is not guaranteed, so the scanner must not
        // depend on the prerequisite being read before its child.
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        WriteVersion("1.20.1-forge-47.1.3",
            WithLoader("1.20.1-forge-47.1.3", "1.20.1", "net.minecraftforge:forge:1.20.1-47.1.3"));

        var all = _scanner.Scan(_root);

        all.Should().HaveCount(2);
        all.Should().Contain(i => i.Kind == InstanceKind.Original);
        all.Should().Contain(i => i.Kind == InstanceKind.Forge);
    }

    [Fact]
    public void Optifine_in_a_library_coordinate_is_detected()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        WriteVersion("1.20.1-OptiFine",
            WithLoader("1.20.1-OptiFine", "1.20.1", "optifine:OptiFine:1.20.1_HD_U_I5"));

        var of = _scanner.Scan(_root).Single(i => i.Name == "1.20.1-OptiFine");

        of.Kind.Should().Be(InstanceKind.OptiFine);
        of.OptiFineVersion.Should().Be("1.20.1_HD_U_I5");
        of.IsModdable.Should().BeFalse("OptiFine alone cannot take other mods");
    }

    [Fact]
    public void Inheritance_cycle_terminates()
    {
        // Two jsons inheriting from each other would loop forever without the
        // depth cap.
        WriteVersion("a", WithLoader("a", "b", "net.minecraftforge:forge:1.20.1-47.1.3"));
        WriteVersion("b", WithLoader("b", "a", "net.minecraftforge:forge:1.20.1-47.1.3"));

        var result = _scanner.Scan(_root);

        // Both still produce a card; the point is that this returns at all.
        result.Should().HaveCount(2);
    }

    // ---- pcl metadata ----

    [Fact]
    public void Star_flag_is_read_from_pcl_setup_ini()
    {
        var dir = WriteVersion("1.20.1", Vanilla("1.20.1"));
        var pcl = Path.Combine(dir, "PCL");
        Directory.CreateDirectory(pcl);
        File.WriteAllText(Path.Combine(pcl, "Setup.ini"), "IsStar: true\r\nHidden: false\r\n");

        _scanner.Scan(_root).Single().IsStar.Should().BeTrue();
    }

    [Fact]
    public void Malformed_setup_ini_is_ignored()
    {
        var dir = WriteVersion("1.20.1", Vanilla("1.20.1"));
        var pcl = Path.Combine(dir, "PCL");
        Directory.CreateDirectory(pcl);
        File.WriteAllText(Path.Combine(pcl, "Setup.ini"), "garbage without colons\r\nIsStar:\r\n");

        _scanner.Scan(_root).Single().IsStar.Should().BeFalse();
    }

    // ---- async wrapper ----

    [Fact]
    public async Task ScanAsync_returns_the_same_result_as_the_sync_scan()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));

        var viaAsync = await _scanner.ScanAsync(_root);
        var viaSync = _scanner.Scan(_root);

        viaAsync.Select(i => i.Name).Should().BeEquivalentTo(viaSync.Select(i => i.Name));
    }

    [Fact]
    public async Task ScanAsync_honours_cancellation()
    {
        WriteVersion("1.20.1", Vanilla("1.20.1"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await _scanner.ScanAsync(_root, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
