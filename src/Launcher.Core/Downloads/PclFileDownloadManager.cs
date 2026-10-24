using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Core.Downloads;

public sealed record DownloadFileRequest(
    string Path,
    string Kind,
    string? Sha1,
    long? Size,
    IReadOnlyList<Uri> Sources,
    string? Md5 = null,
    string? ResourceSource = null,
    string? ProjectId = null,
    string? VersionId = null,
    IReadOnlyList<ModrinthDependency>? Conflicts = null);

public sealed record DownloadFileOptions(
    int MaxThreads,
    int SpeedLimitKb,
    string SourcePreference,
    string GameDirectory,
    IReadOnlyList<string> ReuseRoots,
    string? AuthorizedDestinationPath = null);

public sealed record DownloadBatchProgress(
    double Progress,
    string CurrentFile,
    long BytesPerSecond,
    int RemainingFiles,
    int ReusedFiles,
    int DownloadedFiles,
    IReadOnlyList<DownloadFileProgress>? Files = null);

public enum DownloadFileProgressState { Waiting, Active, Complete, Reused, Failed, Canceled }

public sealed record DownloadFileProgress(
    string Name,
    double? Progress,
    DownloadFileProgressState State,
    string Detail);

/// <summary>File-level downloader with source fallback, verification, reuse, and Range support.</summary>
public sealed class PclFileDownloadManager
{
    private const int BufferSize = 64 * 1024;
    private const long SplitThreshold = 256 * 1024;
    private const long RangeDownloadThreshold = 1024 * 1024;
    private const int MaxVisibleWaitingFiles = 40;
    private static readonly Uri OfficialManifestProbe = new("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly ILogger<PclFileDownloadManager> _log;
    private readonly SemaphoreSlim _sourcePreferenceLock = new(1, 1);
    private readonly SemaphoreSlim _bmclStartGate = new(1, 1);
    private long _lastBmclRequest;
    private string? _automaticSourcePreference;
    private readonly object _rateLock = new();
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private long _nextRateTimestamp;
    private readonly object _speedLock = new();
    private long _downloadedBytesTotal;
    private long _speedWindowBytes;
    private long _speedWindowTimestamp = Stopwatch.GetTimestamp();
    private long _currentSpeed;
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _activeFiles = new(StringComparer.OrdinalIgnoreCase);

    public PclFileDownloadManager() : this(SharedHttp, NullLogger<PclFileDownloadManager>.Instance) { }

    public PclFileDownloadManager(ILogger<PclFileDownloadManager> log) : this(SharedHttp, log) { }

    public PclFileDownloadManager(HttpClient httpClient) : this(httpClient, NullLogger<PclFileDownloadManager>.Instance) { }

    public PclFileDownloadManager(HttpClient httpClient, ILogger<PclFileDownloadManager> log)
    {
        _http = httpClient;
        _log = log;
    }

    public SemaphoreSlim CreateNetworkSlots(int maxThreads)
    {
        var count = Math.Clamp(maxThreads, 1, 256);
        return new SemaphoreSlim(count, count);
    }

    public async Task DownloadFilesAsync(
        IEnumerable<DownloadFileRequest> requests,
        DownloadFileOptions options,
        IProgress<DownloadBatchProgress> progress,
        CancellationToken cancellationToken,
        SemaphoreSlim? sharedNetworkSlots = null)
    {
        var files = requests
            .GroupBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        foreach (var file in files)
        {
            // Save As authorizes exactly the path returned by the file picker.
            if (options.AuthorizedDestinationPath is { } destination)
            {
                if (!Path.IsPathFullyQualified(destination)
                    || !string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"下载目标与所选保存路径不一致：{file.Path}");
            }
            else EnsureInsideDirectory(file.Path, options.GameDirectory);
            if (file.Sources.Any(source => source.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"下载地址仅支持 HTTP/HTTPS：{file.Path}");
        }
        if (files.Length == 0)
        {
            progress.Report(new(1, string.Empty, 0, 0, 0, 0));
            return;
        }

        if (options.SourcePreference == "自动选择" && files.Any(file => file.Sources.Count > 1))
        {
            var preference = await ResolveAutomaticSourcePreferenceAsync(cancellationToken);
            options = options with { SourcePreference = preference };
        }

        var threads = Math.Clamp(options.MaxThreads, 1, 256);
        var communityBatch = files.Any(file => file.Sources.Any(source => source.Host is "cdn.modrinth.com"
            or "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "media.forgecdn.net" or "mod.mcimirror.top"));
        if (communityBatch) threads = Math.Min(threads, 8);
        if (communityBatch) _log.LogInformation("社区资源批次：{Files} 个文件，最多同时处理 {Threads} 个文件", files.Length, threads);
        var networkSlots = sharedNetworkSlots ?? CreateNetworkSlots(threads);
        var fileProgress = new double[files.Length];
        var fileBytes = new long[files.Length];
        var fileReused = new bool[files.Length];
        var fileComplete = new bool[files.Length];
        var fileActive = new bool[files.Length];
        var fileCanceled = new bool[files.Length];
        var fileFailed = new string?[files.Length];
        var fileErrors = new Exception?[files.Length];
        var progressLock = new object();
        var reusedCount = 0;
        var downloadedCount = 0;
        long lastReport = 0;
        var perFileThreads = Math.Max(1, threads / Math.Min(files.Length, threads));
        if (communityBatch) perFileThreads = Math.Min(perFileThreads, 4);

        IReadOnlyList<DownloadFileProgress> SnapshotFiles()
        {
            var visible = new List<DownloadFileProgress>(Math.Min(files.Length, MaxVisibleWaitingFiles + threads));
            var waitingCount = 0;
            var hiddenWaitingCount = 0;
            for (var index = 0; index < files.Length; index++)
            {
                var state = fileFailed[index] is not null ? DownloadFileProgressState.Failed
                    : fileCanceled[index] ? DownloadFileProgressState.Canceled
                    : fileComplete[index] ? (fileReused[index] ? DownloadFileProgressState.Reused : DownloadFileProgressState.Complete)
                    : fileActive[index] ? DownloadFileProgressState.Active
                    : DownloadFileProgressState.Waiting;

                // Completed and reused files add no useful information to the live
                // queue. Large asset indexes can contain thousands of these.
                if (state is DownloadFileProgressState.Complete or DownloadFileProgressState.Reused) continue;
                if (state == DownloadFileProgressState.Waiting && ++waitingCount > MaxVisibleWaitingFiles)
                {
                    hiddenWaitingCount++;
                    continue;
                }

                var detail = state switch
                {
                    DownloadFileProgressState.Failed => fileFailed[index]!,
                    DownloadFileProgressState.Canceled => "已取消",
                    DownloadFileProgressState.Active => files[index].Size is > 0
                        ? $"{Math.Floor(fileProgress[index] * 100):0}%"
                        : "正在下载",
                    _ => "等待中",
                };
                visible.Add(new DownloadFileProgress(Path.GetFileName(files[index].Path),
                    state == DownloadFileProgressState.Active && files[index].Size is not > 0
                        ? null
                        : fileProgress[index], state, detail));
            }

            if (hiddenWaitingCount > 0)
                visible.Add(new DownloadFileProgress($"其余 {hiddenWaitingCount} 项", null,
                    DownloadFileProgressState.Waiting, "等待中"));
            return visible;
        }

        void Report(int index, double value, bool? reused = null, bool complete = false, string? name = null,
            bool? active = null, string? failed = null, bool canceled = false)
        {
            lock (progressLock)
            {
                fileProgress[index] = Math.Clamp(value, 0, 1);
                if (active.HasValue) fileActive[index] = active.Value;
                if (failed is not null) fileFailed[index] = failed;
                if (canceled) { fileActive[index] = false; fileCanceled[index] = true; }
                if (reused.HasValue && fileReused[index] != reused.Value)
                {
                    fileReused[index] = reused.Value;
                    if (reused.Value) reusedCount++;
                    else reusedCount--;
                }
                fileComplete[index] |= complete;
                var average = 0d;
                var totalWeight = 0d;
                for (var i = 0; i < files.Length; i++)
                {
                    var weight = fileReused[i] ? .2 : 1d;
                    totalWeight += weight;
                    average += fileProgress[i] * weight;
                }
                average = totalWeight <= 0 ? 0 : average / totalWeight;
                var now = Stopwatch.GetTimestamp();
                var previous = Volatile.Read(ref lastReport);
                var shouldReport = previous == 0
                    || Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromMilliseconds(100)
                    && Interlocked.CompareExchange(ref lastReport, now, previous) == previous;
                if (!shouldReport && failed is null && !canceled) return;
                if (previous == 0) Interlocked.Exchange(ref lastReport, now);
                var speed = GetCurrentSpeed();
                progress.Report(new(average, name ?? Path.GetFileName(files[index].Path), speed,
                    files.Length - fileComplete.Count(done => done), reusedCount, Volatile.Read(ref downloadedCount), SnapshotFiles()));
            }
        }

        async Task DownloadIndexAsync(int index, CancellationToken token, int fileThreads)
        {
            var file = files[index];
            var key = Path.GetFullPath(file.Path);
            lock (progressLock) { fileFailed[index] = null; fileCanceled[index] = false; }
            fileErrors[index] = null;
            var owned = new Lazy<Task<bool>>(
                () => DownloadOneAsync(file, options, fileThreads, networkSlots,
                    token, bytes =>
                    {
                        Interlocked.Add(ref _downloadedBytesTotal, bytes);
                        var total = file.Size.GetValueOrDefault();
                        var transferred = Interlocked.Add(ref fileBytes[index], bytes);
                        Report(index, total > 0 ? Math.Min(.99, (double)transferred / total) : 0,
                            name: Path.GetFileName(file.Path), active: true);
                    }, () =>
                    {
                        Interlocked.Exchange(ref fileBytes[index], 0);
                        Report(index, 0, name: Path.GetFileName(file.Path), active: true);
                    }),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var lazy = _activeFiles.GetOrAdd(key, owned);
            bool wasReused;
            var downloadTask = lazy.Value;
            try
            {
                // The owner must drain its real task before the batch releases networkSlots.
                wasReused = ReferenceEquals(lazy, owned) ? await downloadTask : await downloadTask.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Report(index, fileProgress[index], name: Path.GetFileName(file.Path), canceled: true);
                throw;
            }
            catch (Exception ex)
            {
                fileErrors[index] = ex;
                Report(index, fileProgress[index], name: Path.GetFileName(file.Path), failed: ex.Message, active: false);
                return;
            }
            finally
            {
                if (downloadTask.IsCompleted)
                    _activeFiles.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, lazy));
                else
                    _ = RemoveCompletedDownloadAsync(key, lazy, downloadTask);
            }

            if (wasReused)
            {
                Report(index, 1, reused: true, complete: true, active: false);
            }
            else
            {
                Interlocked.Increment(ref downloadedCount);
                Report(index, 1, complete: true, active: false);
            }
        }

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length),
                new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken },
                (index, token) => new ValueTask(DownloadIndexAsync(index, token, perFileThreads)));

            var retry = Enumerable.Range(0, files.Length).Where(index => fileErrors[index] is not null).ToArray();
            if (retry.Length > 0) _log.LogInformation("批量下载剩余 {Count} 个失败文件，降低并发逐项重试", retry.Length);
            foreach (var index in retry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DownloadIndexAsync(index, cancellationToken, 1);
            }
            var errors = fileErrors.OfType<Exception>().ToArray();
            if (errors.Length > 0)
                throw new IOException(errors.Length == 1 ? errors[0].Message : $"有 {errors.Length} 个文件下载失败。{errors[0].Message}", new AggregateException(errors));

            progress.Report(new(1, "下载阶段完成", GetCurrentSpeed(),
                0, reusedCount, Volatile.Read(ref downloadedCount), SnapshotFiles()));
        }
        finally
        {
            if (sharedNetworkSlots is null) networkSlots.Dispose();
        }
    }

    private async Task RemoveCompletedDownloadAsync(string key, Lazy<Task<bool>> lazy, Task<bool> downloadTask)
    {
        try { await downloadTask.ConfigureAwait(false); }
        catch { }
        _activeFiles.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, lazy));
    }

    private async Task<bool> DownloadOneAsync(
        DownloadFileRequest file,
        DownloadFileOptions options,
        int maxFileThreads,
        SemaphoreSlim networkSlots,
        CancellationToken cancellationToken,
        Action<int> onBytes,
        Action onAttemptStarted)
    {
        if (await IsValidAsync(file.Path, file.Sha1, file.Size, cancellationToken, file.Md5)) return true;

        var candidate = await FindReusableFileAsync(file, options.GameDirectory, options.ReuseRoots, cancellationToken);
        if (candidate is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            try
            {
                File.Copy(candidate, file.Path, overwrite: true);
                if (await IsValidAsync(file.Path, file.Sha1, file.Size, cancellationToken, file.Md5)) return true;
            }
            catch (IOException) { }
            TryDelete(file.Path);
        }

        if (file.Sources.Count == 0) throw new InvalidDataException($"文件缺少下载地址：{file.Path}");
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        var errors = new List<(Uri Source, Exception Error)>();
        var sources = OrderSources(file.Sources, options.SourcePreference).ToArray();
        var canRetryWithoutRanges = false;
        var retrySourceIndices = new HashSet<int>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                if (attempt == 2 && !retrySourceIndices.Contains(sourceIndex)) continue;
                var source = sources[sourceIndex];
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    onAttemptStarted();
                    await DownloadFromSourceAsync(file, source, options.SpeedLimitKb, maxFileThreads,
                        networkSlots, cancellationToken, onBytes, onAttemptStarted);
                    return false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DeleteParts(file.Path);
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add((source, ex));
                    canRetryWithoutRanges |= maxFileThreads > 1
                        && file.Size is >= RangeDownloadThreshold
                        && IsRangeSource(source);
                    LogSourceFailure(file, source, sourceIndex, attempt, "常规下载", ex);
                    DeleteParts(file.Path);
                    if (attempt == 1 && IsTransientFailure(ex, source))
                    {
                        retrySourceIndices.Add(sourceIndex);
                    }
                }
            }

            if (attempt == 1 && retrySourceIndices.Count > 0)
            {
                var retryDelay = errors
                    .Where(error => retrySourceIndices.Any(index => sources[index] == error.Source))
                    .Select(error => GetRetryDelay(error.Error))
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Max();
                if (retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay, cancellationToken);
            }
        }

        if (canRetryWithoutRanges)
        {
            _log.LogInformation("文件 {File} 的并发下载源均失败，改为逐源单线程重试", Path.GetFileName(file.Path));
            for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                var source = sources[sourceIndex];
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    onAttemptStarted();
                    await DownloadFromSourceAsync(file, source, options.SpeedLimitKb, 1, networkSlots,
                        cancellationToken, onBytes, onAttemptStarted);
                    return false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DeleteParts(file.Path);
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add((source, ex));
                    LogSourceFailure(file, source, sourceIndex, 1, "单线程回退", ex);
                    DeleteParts(file.Path);
                }
            }
        }

        var lastError = errors.LastOrDefault();
        var reason = errors.Count == 0 ? "未知错误" : FormatFailure(lastError.Source, lastError.Error);
        var aggregate = new AggregateException(errors.Select(error =>
            new IOException($"{error.Source.GetLeftPart(UriPartial.Path)}：{error.Error.Message}", error.Error)));
        var failure = new IOException(
            $"下载失败：{Path.GetFileName(file.Path)}。尝试 {errors.Count} 次后仍不可用：{reason}", aggregate);
        _log.LogError(failure, "所有下载源均失败：{File}", Path.GetFileName(file.Path));
        throw failure;
    }

    private async Task<string> ResolveAutomaticSourcePreferenceAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _automaticSourcePreference) is { } cached) return cached;

        await _sourcePreferenceLock.WaitAsync(cancellationToken);
        try
        {
            if (_automaticSourcePreference is { } resolved) return resolved;

            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var response = await _http.GetAsync(
                    OfficialManifestProbe, HttpCompletionOption.ResponseContentRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var elapsedMs = stopwatch.ElapsedMilliseconds;
                _automaticSourcePreference = elapsedMs < 4000
                    ? "Mojang 官方 + BMCLAPI 回退"
                    : "BMCLAPI";
                _log.LogInformation(
                    "自动选择下载源：官方版本清单耗时 {ElapsedMs} ms，优先级为 {Preference}",
                    elapsedMs, _automaticSourcePreference);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _automaticSourcePreference = "BMCLAPI";
                _log.LogWarning(ex, "自动检测官方源失败，下载时优先尝试 BMCLAPI");
            }

            return _automaticSourcePreference ?? "BMCLAPI";
        }
        finally
        {
            _sourcePreferenceLock.Release();
        }
    }

    private void LogSourceFailure(
        DownloadFileRequest file,
        Uri source,
        int sourceIndex,
        int attempt,
        string phase,
        Exception exception)
    {
        var statusCode = exception is HttpRequestException { StatusCode: { } status }
            ? (int)status
            : (int?)null;
        _log.LogWarning(exception,
            "下载失败：文件 {File}，来源 {SourceIndex}/{SourceCount} {Source}，阶段 {Phase}，轮次 {Attempt}，HTTP {StatusCode}",
            Path.GetFileName(file.Path), sourceIndex + 1, file.Sources.Count,
            source.GetLeftPart(UriPartial.Path), phase, attempt, statusCode);
    }

    private static string FormatFailure(Uri source, Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } status })
            return $"{source.Host} 返回 HTTP {(int)status} ({status})";
        return $"{source.Host}：{exception.Message}";
    }

    private static bool IsTransientFailure(Exception exception, Uri source)
    {
        if (exception is HttpRequestException requestException)
        {
            if (requestException.StatusCode is not { } status) return true;
            return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)status >= 500
                || status == HttpStatusCode.Forbidden && IsBmclapi(source);
        }

        return exception is TimeoutException or TaskCanceledException or IOException or InvalidDataException;
    }

    private static TimeSpan GetRetryDelay(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
            ? TimeSpan.FromSeconds(10)
            : exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromMilliseconds(750);

    private async Task DownloadFromSourceAsync(
        DownloadFileRequest file,
        Uri source,
        int speedLimitKb,
        int maxFileThreads,
        SemaphoreSlim networkSlots,
        CancellationToken cancellationToken,
        Action<int> onBytes,
        Action onAttemptStarted)
    {
        var temp = file.Path + ".97launch.part";
        var expectedSize = file.Size;
        var useRange = maxFileThreads > 1 && expectedSize is >= RangeDownloadThreshold
            && IsRangeSource(source);
        if (!useRange)
        {
            await DownloadWholeAsync(source, temp, speedLimitKb, networkSlots, cancellationToken, onBytes);
            await ValidateAndMoveAsync(file, temp, cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        await networkSlots.WaitAsync(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using (response)
                {
                    await WriteResponseAsync(response, temp, speedLimitKb, cancellationToken, onBytes);
                    await ValidateAndMoveAsync(file, temp, cancellationToken);
                }
                return;
            }
        }
        finally { networkSlots.Release(); }

        using (response!)
        {
        var rangeResponse = response!;
        if (rangeResponse.StatusCode != HttpStatusCode.PartialContent
            || rangeResponse.Content.Headers.ContentRange?.Length is not long length
            || length <= 0)
        {
            onAttemptStarted();
            await DownloadWholeAsync(source, temp, speedLimitKb, networkSlots, cancellationToken, onBytes);
            await ValidateAndMoveAsync(file, temp, cancellationToken);
            return;
        }
        if (expectedSize.HasValue && expectedSize.Value != length)
            throw new InvalidDataException($"文件大小不符：{Path.GetFileName(file.Path)}，期望 {expectedSize}，来源报告 {length}。");

        var segmentCount = (int)Math.Clamp(Math.Min(maxFileThreads, (length + SplitThreshold - 1) / SplitThreshold), 1, maxFileThreads);
        var segmentSize = (length + segmentCount - 1) / segmentCount;
        var ranges = Enumerable.Range(0, segmentCount)
            .Select(index => (Index: index, Start: index * segmentSize,
                End: Math.Min(length - 1, (index + 1L) * segmentSize - 1)))
            .Where(range => range.Start <= range.End)
            .ToArray();
        rangeResponse.Dispose();

        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var tasks = ranges.Select(range => DownloadSegmentAsync(source, file.Path, range.Index,
                range.Start, range.End, length, speedLimitKb, networkSlots, linked.Token, onBytes)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                linked.Cancel();
                try { await Task.WhenAll(tasks); } catch { }
                if (tasks.Any(task => task.IsFaulted
                    && task.Exception?.Flatten().InnerExceptions.Any(ex => ex is RangeNotSupportedException) == true))
                {
                    DeleteParts(file.Path);
                    onAttemptStarted();
                    await DownloadWholeAsync(source, temp, speedLimitKb, networkSlots, cancellationToken, onBytes);
                    await ValidateAndMoveAsync(file, temp, cancellationToken);
                    return;
                }
                throw;
            }
        }

        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var range in ranges)
            {
                var part = PartPath(file.Path, range.Index);
                await using var input = File.OpenRead(part);
                await input.CopyToAsync(output, BufferSize, cancellationToken);
            }
        }
        await ValidateAndMoveAsync(file, temp, cancellationToken);
        DeleteParts(file.Path);
        }
    }

    private async Task DownloadSegmentAsync(
        Uri source,
        string filePath,
        int index,
        long start,
        long end,
        long totalSize,
        int speedLimitKb,
        SemaphoreSlim networkSlots,
        CancellationToken cancellationToken,
        Action<int> onBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        await networkSlots.WaitAsync(cancellationToken);
        try
        {
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException();
        var range = response.Content.Headers.ContentRange;
        if (range?.From != start || range.To != end || range.Length != totalSize)
            throw new InvalidDataException("下载源返回的文件分段信息不匹配。");
        var part = PartPath(filePath, index);
        await WriteResponseAsync(response, part, speedLimitKb, cancellationToken, onBytes);
        if (new FileInfo(part).Length != end - start + 1)
            throw new InvalidDataException("下载分段长度不匹配。");
        }
        finally { networkSlots.Release(); }
    }

    private async Task DownloadWholeAsync(
        Uri source,
        string path,
        int speedLimitKb,
        SemaphoreSlim networkSlots,
        CancellationToken cancellationToken,
        Action<int> onBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        await networkSlots.WaitAsync(cancellationToken);
        try
        {
        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await WriteResponseAsync(response, path, speedLimitKb, cancellationToken, onBytes);
        }
        finally { networkSlots.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.UserAgent.Any() && !_http.DefaultRequestHeaders.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd($"97Launch/{typeof(PclFileDownloadManager).Assembly.GetName().Version!.ToString(3)}");
        if (request.RequestUri is { } source && IsBmclapi(source))
        {
            // PCL spaces BMCLAPI request starts by 100 ms to avoid bursts.
            await _bmclStartGate.WaitAsync(cancellationToken);
            try
            {
                var delay = TimeSpan.FromMilliseconds(100) - Stopwatch.GetElapsedTime(_lastBmclRequest);
                if (_lastBmclRequest != 0 && delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                _lastBmclRequest = Stopwatch.GetTimestamp();
            }
            finally { _bmclStartGate.Release(); }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                _log.LogWarning("下载 HTTP {Status}：请求 {Requested}，最终节点 {Final}，响应类型 {ContentType}",
                    (int)response.StatusCode, request.RequestUri?.GetLeftPart(UriPartial.Path),
                    response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path), response.Content.Headers.ContentType?.MediaType);
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("下载源 30 秒内未返回响应。", ex); }
    }

    private async Task WriteResponseAsync(
        HttpResponseMessage response,
        string path,
        int speedLimitKb,
        CancellationToken cancellationToken,
        Action<int> onBytes)
    {
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        while (true)
        {
            await WaitForRateSlotAsync(buffer.Length, speedLimitKb, cancellationToken);
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            int read;
            try { read = await input.ReadAsync(buffer, idleTimeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("下载源 30 秒未返回数据。"); }
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            onBytes(read);
        }
    }

    private async Task WaitForRateSlotAsync(int bytes, int speedLimitKb, CancellationToken cancellationToken)
    {
        if (speedLimitKb <= 0) return;
        await _rateGate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan delay;
            lock (_rateLock)
            {
                var now = Stopwatch.GetTimestamp();
                delay = _nextRateTimestamp <= now ? TimeSpan.Zero
                    : Stopwatch.GetElapsedTime(now, _nextRateTimestamp);
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            lock (_rateLock)
                _nextRateTimestamp = Stopwatch.GetTimestamp()
                    + (long)(bytes / (speedLimitKb * 1024d) * Stopwatch.Frequency);
        }
        finally { _rateGate.Release(); }
    }

    private long GetCurrentSpeed()
    {
        lock (_speedLock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_speedWindowTimestamp, now).TotalSeconds;
            if (elapsed < .1) return _currentSpeed;
            var total = Volatile.Read(ref _downloadedBytesTotal);
            var instantaneous = Math.Max(0, (long)((total - _speedWindowBytes) / elapsed));
            _currentSpeed = instantaneous == 0 ? 0 : (long)(_currentSpeed * .65 + instantaneous * .35);
            _speedWindowBytes = total;
            _speedWindowTimestamp = now;
            return _currentSpeed;
        }
    }

    private async Task ValidateAndMoveAsync(DownloadFileRequest file, string temp, CancellationToken cancellationToken)
    {
        if (!await IsValidAsync(temp, file.Sha1, file.Size, cancellationToken, file.Md5))
        {
            var actualSize = File.Exists(temp) ? new FileInfo(temp).Length : (long?)null;
            string? actualHash = null;
            if (File.Exists(temp) && (file.Sha1 is not null || file.Md5 is not null))
            {
                await using var stream = File.OpenRead(temp);
                actualHash = Convert.ToHexString(file.Sha1 is not null ? await SHA1.HashDataAsync(stream, cancellationToken)
                    : await MD5.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }
            _log.LogWarning("文件校验失败：{File}，期望大小 {ExpectedSize}，实际大小 {ActualSize}，期望哈希 {ExpectedHash}，实际哈希 {ActualHash}",
                Path.GetFileName(file.Path), file.Size, actualSize, file.Sha1 ?? file.Md5, actualHash);
            throw new InvalidDataException($"文件校验失败：{Path.GetFileName(file.Path)}" + (file.Size.HasValue && actualSize != file.Size ? $"（期望 {file.Size} 字节，实际 {actualSize} 字节）" : "（哈希不符）"));
        }
        File.Move(temp, file.Path, overwrite: true);
    }

    private static async Task<string?> FindReusableFileAsync(
        DownloadFileRequest file,
        string gameDirectory,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        if (file.Sha1 is null && file.Md5 is null && file.Size is null) return null;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relative = Path.GetRelativePath(gameDirectory, file.Path);
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
            foreach (var root in roots) candidates.Add(Path.Combine(root, relative));

        var path = file.Path.Replace('\\', '/');
        var categories = new[] { "assets/", "libraries/", "versions/", "mods/", "coremods/", "lib/",
            "resourcepacks/", "texturepacks/", "shaderpacks/" };
        var category = categories.Select(item => (Name: item, Index: path.LastIndexOf(item, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Index >= 0).OrderByDescending(item => item.Index).FirstOrDefault();
        if (category.Name is not null)
        {
            var suffix = path[(category.Index + category.Name.Length)..].Replace('/', Path.DirectorySeparatorChar);
            if (category.Name is "assets/" or "libraries/")
            {
                foreach (var root in roots) candidates.Add(Path.Combine(root, category.Name.TrimEnd('/'), suffix));
            }
            else if (category.Name == "versions/")
            {
                foreach (var root in roots)
                {
                    var versions = Path.Combine(root, "versions");
                    if (!Directory.Exists(versions)) continue;
                    try
                    {
                        foreach (var candidate in Directory.EnumerateFiles(versions,
                                     "*." + Path.GetExtension(file.Path).TrimStart('.'), SearchOption.AllDirectories))
                            candidates.Add(candidate);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            else if (file.Sha1 is not null && file.Size.HasValue)
            {
                foreach (var root in roots)
                {
                    var folder = Path.Combine(root, category.Name.TrimEnd('/'));
                    AddCandidates(folder, candidates, SearchOption.AllDirectories);

                    // PCL also searches resource folders inside each installed version.
                    var versions = Path.Combine(root, "versions");
                    if (!Directory.Exists(versions)) continue;
                    try
                    {
                        foreach (var versionDirectory in Directory.EnumerateDirectories(versions))
                            AddCandidates(Path.Combine(versionDirectory, category.Name.TrimEnd('/')),
                                candidates, SearchOption.AllDirectories);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        else if (Path.GetExtension(file.Path).Equals(".jar", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var root in roots)
            {
                var versions = Path.Combine(root, "versions");
                if (!Directory.Exists(versions)) continue;
                try { foreach (var candidate in Directory.EnumerateFiles(versions, "*.jar", SearchOption.AllDirectories)) candidates.Add(candidate); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase)
                && await IsValidAsync(candidate, file.Sha1, file.Size, cancellationToken, file.Md5)) return candidate;
        }
        return null;
    }

    private static void AddCandidates(string directory, HashSet<string> candidates, SearchOption searchOption)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(directory, "*", searchOption))
                candidates.Add(candidate);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<bool> IsValidAsync(
        string path,
        string? sha1,
        long? size,
        CancellationToken cancellationToken,
        string? md5 = null)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (size.HasValue && info.Length != size.Value) return false;
            if (sha1 is null && md5 is null) return true;
            await using var stream = File.OpenRead(path);
            if (sha1 is not null)
            {
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
                return actual.Equals(sha1, StringComparison.OrdinalIgnoreCase);
            }
            var md5Actual = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken));
            return md5Actual.Equals(md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<Uri> OrderSources(IReadOnlyList<Uri> sources, string preference) => preference switch
    {
        "BMCLAPI" => sources.OrderBy(source => IsBmclapi(source) ? 0 : 1),
        "官方源" => sources.OrderBy(source => IsBmclapi(source) ? 1 : 0),
        _ => sources,
    };

    private static bool IsBmclapi(Uri source) =>
        source.Host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);

    private static bool IsRangeSource(Uri source)
    {
        var host = source.Host;
        return !host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("optifine.net", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("meloong.com", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("momot.rs", StringComparison.OrdinalIgnoreCase);
    }

    private static string PartPath(string path, int index) => $"{path}.97launch.part.{index}";

    private static void DeleteParts(string path)
    {
        TryDelete(path + ".97launch.part");
        var directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var part in Directory.EnumerateFiles(directory!, Path.GetFileName(path) + ".97launch.part.*"))
                TryDelete(part);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureInsideDirectory(string filePath, string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var target = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"下载目标超出游戏目录：{filePath}");
    }

    private sealed class RangeNotSupportedException : IOException { }
}
