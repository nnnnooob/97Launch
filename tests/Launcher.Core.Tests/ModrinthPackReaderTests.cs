using System.IO.Compression;
using FluentAssertions;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class ModrinthPackReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "97launch-pack-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("mods/../../escape.jar")]
    [InlineData("C:/escape.jar")]
    [InlineData("/escape.jar")]
    [InlineData("mods/file:stream.jar")]
    [InlineData("97Launch/InstallManifest.json")]
    public void Rejects_paths_outside_instance_or_reserved_metadata(string relative)
    {
        var read = () => ModrinthPackReader.ResolvePath(Path.Combine(_root, "instance"), relative);
        read.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Reads_client_files_and_applies_client_overrides_after_common_overrides()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "test.mrpack");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "modrinth.index.json", """
                {"formatVersion":1,"game":"minecraft","dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"},"files":[
                {"path":"mods/client.jar","hashes":{"sha1":"1111111111111111111111111111111111111111"},"fileSize":12,"downloads":["https://cdn.modrinth.com/data/client.jar"]},
                {"path":"mods/server.jar","env":{"client":"unsupported"}}]}
                """);
            Write(archive, "overrides/config/test.txt", "common");
            Write(archive, "client-overrides/config/test.txt", "client");
            Write(archive, "server-overrides/config/server.txt", "server");
        }
        var target = Path.Combine(_root, "instance");
        var plan = ModrinthPackReader.Read(archivePath, target);
        plan.MinecraftVersion.Should().Be("1.20.1");
        plan.Loader.Should().Be("Fabric");
        plan.LoaderVersion.Should().Be("0.16.10");
        plan.Files.Should().ContainSingle().Which.Path.Should().Be(Path.Combine(target, "mods", "client.jar"));
        await ModrinthPackReader.ExtractClientOverridesAsync(archivePath, target, CancellationToken.None);
        File.ReadAllText(Path.Combine(target, "config", "test.txt")).Should().Be("client");
        File.Exists(Path.Combine(target, "config", "server.txt")).Should().BeFalse();
    }

    [Fact]
    public void Does_not_report_unfinished_pack_as_an_installed_instance()
    {
        var instance = Path.Combine(_root, "versions", "test");
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "test.json"), """{"id":"test","type":"release","mainClass":"net.minecraft.client.main.Main"}""");
        File.WriteAllText(Path.Combine(instance, ".97launch-installing"), "version");
        var scanner = new InstanceScanner(NullLogger<InstanceScanner>.Instance);
        scanner.Scan(_root).Should().BeEmpty();
        File.Delete(Path.Combine(instance, ".97launch-installing"));
        scanner.Scan(_root).Should().ContainSingle();
    }

    private static void Write(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
