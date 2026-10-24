using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Launcher.Core.Downloads;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class PclFileDownloadManagerTests
{
    [Fact]
    public async Task Save_as_downloads_to_the_exact_authorized_external_path_and_can_be_retried()
    {
        var root = NewTempDirectory();
        try
        {
            var bytes = "resource content"u8.ToArray();
            var game = Path.Combine(root, "game");
            var target = Path.Combine(root, "下载 目录", "Sodium 测试.jar");
            using var http = new HttpClient(new RangeHandler(bytes, supportsRanges: false));
            var manager = new PclFileDownloadManager(http);
            var files = new[] { new DownloadFileRequest(target, "资源", Sha1(bytes), bytes.Length, [new Uri("https://example.test/resource.jar")]) };
            var options = new DownloadFileOptions(1, 0, "官方源", game, [], target);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await manager.DownloadFilesAsync(files, options, new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);
                File.ReadAllBytes(target).Should().Equal(bytes);
                File.Delete(target);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_external_paths_without_exact_save_as_authorization(bool authorizeSibling)
    {
        var root = NewTempDirectory();
        try
        {
            var target = Path.Combine(root, "Downloads", "resource.jar");
            using var http = new HttpClient(new ThrowingHandler());
            var manager = new PclFileDownloadManager(http);
            var options = new DownloadFileOptions(1, 0, "官方源", Path.Combine(root, "game"), [],
                authorizeSibling ? Path.Combine(root, "Downloads", "other.jar") : null);
            var action = () => manager.DownloadFilesAsync(
                [new(target, "资源", null, null, [new Uri("https://example.test/resource.jar")])], options,
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);
            await action.Should().ThrowAsync<InvalidDataException>();
            File.Exists(target).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloads_and_merges_parallel_ranges_then_verifies_hash()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(value => (byte)(value % 251)).ToArray();
        var handler = new RangeHandler(bytes, supportsRanges: true);
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(handler);
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "versions", "test", "test.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "客户端", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/client.jar")])],
                new DownloadFileOptions(4, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
            handler.Ranges.Should().HaveCountGreaterThan(1);
            handler.Ranges.Should().Contain(range => range.From == 0 && range.To == 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Falls_back_to_a_single_stream_when_the_source_ignores_range()
    {
        var bytes = Enumerable.Range(0, 800_000).Select(value => (byte)(value % 233)).ToArray();
        var handler = new RangeHandler(bytes, supportsRanges: false);
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(handler);
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "payload.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "客户端", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/payload.jar")])],
                new DownloadFileOptions(4, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Restarts_as_a_single_stream_when_range_stops_working_mid_download()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(value => (byte)(value % 197)).ToArray();
        var handler = new RangeHandler(bytes, supportsRanges: true, ignoreSegments: true);
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(handler);
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "payload.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "客户端", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/payload.jar")])],
                new DownloadFileOptions(4, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
            handler.Ranges.Should().Contain(range => range.From == 0 && range.To == 0);
            handler.FullRequests.Should().BeGreaterThan(0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reuses_a_hash_matched_library_from_another_game_folder()
    {
        var root = NewTempDirectory();
        try
        {
            var game = Path.Combine(root, "game");
            var reused = Path.Combine(root, "reused");
            var relative = Path.Combine("libraries", "example", "library.jar");
            var source = Path.Combine(reused, relative);
            var target = Path.Combine(game, relative);
            var bytes = "verified library"u8.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, bytes);

            using var http = new HttpClient(new ThrowingHandler());
            var manager = new PclFileDownloadManager(http);
            var progress = new InlineProgress<DownloadBatchProgress>(_ => { });
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "依赖库", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/library.jar")])],
                new DownloadFileOptions(4, 0, "官方源", game, [reused]), progress, CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reuses_a_hash_matched_mod_from_another_installed_version()
    {
        var root = NewTempDirectory();
        try
        {
            var game = Path.Combine(root, "game");
            var other = Path.Combine(root, "other");
            var bytes = "reusable mod jar"u8.ToArray();
            var source = Path.Combine(other, "versions", "old-instance", "mods", "example.jar");
            var target = Path.Combine(game, "versions", "new-instance", "mods", "example.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, bytes);

            using var http = new HttpClient(new ThrowingHandler());
            var manager = new PclFileDownloadManager(http);
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "Mod", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/mods/example.jar")])],
                new DownloadFileOptions(4, 0, "官方源", game, [other]),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reused_file_counts_toward_progress_without_listing_it_as_pending_work()
    {
        var root = NewTempDirectory();
        try
        {
            var bytes = "already installed"u8.ToArray();
            var target = Path.Combine(root, "libraries", "example.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes);

            using var http = new HttpClient(new ThrowingHandler());
            var manager = new PclFileDownloadManager(http);
            var reports = new List<DownloadBatchProgress>();
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "依赖库", Sha1(bytes), bytes.Length,
                    [new Uri("https://example.test/example.jar")])],
                new DownloadFileOptions(4, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(reports.Add), CancellationToken.None);

            reports.Should().NotBeEmpty();
            reports[0].Progress.Should().Be(1);
            reports[0].ReusedFiles.Should().Be(1);
            reports.Last().Files.Should().BeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloads_files_without_hash_or_size_metadata()
    {
        var root = NewTempDirectory();
        try
        {
            var bytes = "metadata is optional for some loader files"u8.ToArray();
            using var http = new HttpClient(new FallbackHandler(bytes));
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "loader.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "加载器文件", null, null,
                    [new Uri("https://good.example/loader.jar")])],
                new DownloadFileOptions(4, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Verifies_legacy_installer_files_with_md5_metadata()
    {
        var root = NewTempDirectory();
        try
        {
            var bytes = "legacy forge installer"u8.ToArray();
            var md5 = Convert.ToHexString(MD5.HashData(bytes));
            using var http = new HttpClient(new FallbackHandler(bytes));
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "forge-installer.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "Forge 安装器", null, bytes.Length,
                    [new Uri("https://good.example/forge-installer.jar")], md5)],
                new DownloadFileOptions(2, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Matches_Pcl_thread_range_and_does_not_split_BMCLAPI_downloads()
    {
        var slots = new PclFileDownloadManager().CreateNetworkSlots(999);
        try { slots.CurrentCount.Should().Be(256); }
        finally { slots.Dispose(); }

        var bytes = Enumerable.Range(0, 800_000).Select(value => (byte)(value % 211)).ToArray();
        var handler = new RangeHandler(bytes, supportsRanges: true);
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(handler);
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "asset.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "依赖库", Sha1(bytes), bytes.Length,
                    [new Uri("https://bmclapi2.example/asset.jar")])],
                new DownloadFileOptions(64, 0, "官方源", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            handler.Ranges.Should().BeEmpty();
            File.ReadAllBytes(target).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Falls_back_to_the_next_source_before_retrying_the_source_list()
    {
        var bytes = "download from backup"u8.ToArray();
        var handler = new FallbackHandler(bytes);
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(handler);
            var manager = new PclFileDownloadManager(http);
            var target = Path.Combine(root, "file.jar");
            await manager.DownloadFilesAsync(
                [new DownloadFileRequest(target, "依赖库", Sha1(bytes), bytes.Length,
                    [new Uri("https://bad.example/file.jar"), new Uri("https://good.example/file.jar")])],
                new DownloadFileOptions(2, 0, "自动选择", root, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            File.ReadAllBytes(target).Should().Equal(bytes);
            handler.BadSourceAttempts.Should().Be(1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Rejects_download_targets_outside_the_selected_game_directory()
    {
        var root = NewTempDirectory();
        try
        {
            using var http = new HttpClient(new ThrowingHandler());
            var manager = new PclFileDownloadManager(http);
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var request = new DownloadFileRequest(Path.Combine(root, "outside.jar"), "依赖库", null, null,
                [new Uri("https://example.test/file.jar")]);
            var act = () => manager.DownloadFilesAsync([request],
                new DownloadFileOptions(2, 0, "官方源", game, []),
                new InlineProgress<DownloadBatchProgress>(_ => { }), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));
    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "97launch-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RangeHandler(byte[] bytes, bool supportsRanges, bool ignoreSegments = false) : HttpMessageHandler
    {
        public ConcurrentBag<RangeItemHeaderValue> Ranges { get; } = [];
        public int FullRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requested = request.Headers.Range?.Ranges.SingleOrDefault();
            if (requested is not null) Ranges.Add(requested);
            if (!supportsRanges || requested is null || ignoreSegments && requested.To != 0)
            {
                Interlocked.Increment(ref FullRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }

            var start = checked((int)(requested.From ?? 0));
            var end = checked((int)Math.Min(requested.To ?? bytes.Length - 1, bytes.Length - 1));
            var content = new ByteArrayContent(bytes[start..(end + 1)]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A valid reusable file must not access the network.");
    }

    private sealed class FallbackHandler(byte[] bytes) : HttpMessageHandler
    {
        public int BadSourceAttempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "bad.example")
            {
                Interlocked.Increment(ref BadSourceAttempts);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
