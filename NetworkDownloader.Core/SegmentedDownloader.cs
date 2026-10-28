using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;

namespace NetworkDownloader.Core;

public sealed record SegmentRange(long Start, long End);
public sealed record SegmentLayout(int Version, SegmentRange[] Ranges);

internal sealed class SegmentedDownloader(HttpClient client)
{
    private const long MinimumSegmentSize = 256 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    public async Task<bool> TryDownloadAsync(Uri url, string destination, DownloadMetadata? saved,
        int connectionCount, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (saved?.Segments is not null)
        {
            ValidateLayout(saved);
            if (saved.Url != url.AbsoluteUri) throw new InvalidDataException("分片临时文件属于另一个下载地址。");
        }
        else if (File.Exists(destination + ".part") && new FileInfo(destination + ".part").Length > 0)
        {
            // Existing single-connection downloads keep their contiguous byte offset.
            progress?.Report(new(0, saved?.Total, 0, "检测到旧版续传文件，继续使用单连接…"));
            return false;
        }

        var remote = await ProbeAsync(url, progress, cancellationToken).ConfigureAwait(false);
        if (remote is null || remote.Total < MinimumSegmentSize * 2)
        {
            progress?.Report(new(0, remote?.Total, 0, "服务器不支持安全分片或文件较小，使用单连接…"));
            return false;
        }

        var directory = destination + ".part.segments";
        var total = remote.Total!.Value;
        var sameResource = saved?.Segments is not null && saved.Total == remote.Total &&
            (saved.ETag is not null ? saved.ETag == remote.ETag :
                saved.LastModified.HasValue && saved.LastModified == remote.LastModified);
        SegmentLayout layout;
        if (sameResource)
        {
            layout = saved!.Segments!;
        }
        else
        {
            if (Directory.Exists(directory) && saved?.Segments is null)
                throw new InvalidDataException("分片目录已存在但缺少匹配的续传信息，请选择其他保存目录。");
            if (saved?.Segments is not null)
                progress?.Report(new(0, remote.Total, 0, "文件版本已变化，重新下载所有分片…"));
            var count = (int)Math.Min(Math.Max(2, connectionCount), remote.Total!.Value / MinimumSegmentSize);
            layout = CreateLayout(remote.Total!.Value, count);
            Directory.CreateDirectory(directory);
            // Old bytes must be invalidated BEFORE new validators are persisted.
            for (var i = 0; i < Math.Max(layout.Ranges.Length, saved?.Segments?.Ranges.Length ?? 0); i++)
            {
                await using var empty = new FileStream(PartPath(destination, i), FileMode.Create,
                    FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            }
        }

        var metadata = remote with { Segments = layout };
        var lengths = new long[layout.Ranges.Length];
        for (var i = 0; i < lengths.Length; i++)
        {
            var partPath = PartPath(destination, i);
            lengths[i] = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (lengths[i] > Length(layout.Ranges[i]))
                throw new InvalidDataException("分片临时文件超过预期长度，已停止下载。");
        }
        Directory.CreateDirectory(directory);
        await DownloadService.SaveMetadataAsync(destination + ".part.json", metadata, cancellationToken).ConfigureAwait(false);

        var limit = Math.Min(connectionCount, layout.Ranges.Length);
        var tracker = new AggregateProgress(lengths, metadata.Total!.Value, limit, progress);
        tracker.Report(force: true);
        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? firstError = null;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, layout.Ranges.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = limit,
                CancellationToken = workersCancellation.Token
            }, async (index, token) =>
            {
                try { await DownloadPartAsync(url, destination, index, metadata, tracker, token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    await workersCancellation.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (firstError is RangeNoLongerSupportedException)
            {
                progress?.Report(new(0, remote.Total, 0, "服务器拒绝分片，改用单连接重新下载…"));
                return false;
            }
            ExceptionDispatchInfo.Capture(firstError ?? ex).Throw();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(total, total, 0, "分片完成，正在合并文件…"));
        await using (var output = new FileStream(destination + ".part", FileMode.Create, FileAccess.Write,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (var i = 0; i < layout.Ranges.Length; i++)
            {
                await using var input = new FileStream(PartPath(destination, i), FileMode.Open, FileAccess.Read,
                    FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != Length(layout.Ranges[i])) throw new InvalidDataException("合并前发现分片长度异常。");
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != total) throw new InvalidDataException("合并后的文件长度不正确。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(destination + ".part", destination, overwrite: false);
        CleanupParts(destination);
        TryDelete(destination + ".part.json");
        progress?.Report(new(total, total, 0, "下载完成"));
        return true;
    }

    private async Task<DownloadMetadata?> ProbeAsync(Uri url, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                progress?.Report(new(0, null, 0, "正在检测服务器分片下载能力…"));
                using var request = CreateRequest(url, 0, 0, null);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable or
                    HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) return null;
                response.EnsureSuccessStatusCode();
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null ||
                    !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != 0 || range.To != 0 ||
                    range.Length is not > 0 || (response.Content.Headers.ContentLength is { } length && length != 1) ||
                    HasEncoding(response)) return null;
                var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                var modified = response.Content.Headers.LastModified;
                return etag is null && !modified.HasValue ? null : new(url.AbsoluteUri, etag, modified, range.Length);
            }
            catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadPartAsync(Uri url, string destination, int index, DownloadMetadata metadata,
        AggregateProgress tracker, CancellationToken cancellationToken)
    {
        var range = metadata.Segments!.Ranges[index];
        var partPath = PartPath(destination, index);
        for (var attempt = 0; ; attempt++)
        {
            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (offset == Length(range)) return;
            try
            {
                using var request = CreateRequest(url, range.Start + offset, range.End, metadata);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new RangeNoLongerSupportedException();
                response.EnsureSuccessStatusCode();
                var returned = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || returned is null ||
                    !returned.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
                    returned.From != range.Start + offset || returned.To != range.End || returned.Length != metadata.Total ||
                    (response.Content.Headers.ContentLength is { } length && length != Length(range) - offset) || HasEncoding(response))
                    throw new InvalidDataException("服务器返回了错误的分片范围，已停止以防文件损坏。");
                if ((metadata.ETag is not null && response.Headers.ETag is not null &&
                        metadata.ETag != response.Headers.ETag.ToString()) ||
                    (metadata.ETag is null && response.Content.Headers.LastModified.HasValue &&
                        metadata.LastModified != response.Content.Headers.LastModified))
                    throw new RangeNoLongerSupportedException();

                await using var output = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (output.Length != offset) throw new InvalidDataException("分片文件被其他程序修改。");
                output.Position = offset;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[128 * 1024];
                var written = offset;
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(ReadTimeout);
                    int count;
                    try { count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    { throw new IOException("分片 30 秒未收到数据。", ex); }
                    if (count == 0) break;
                    if (count > Length(range) - written)
                    {
                        output.SetLength(offset);
                        tracker.Set(index, offset);
                        throw new InvalidDataException("分片数据超过预期长度。");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    written += count;
                    tracker.Set(index, written);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (written != Length(range)) throw new IOException("分片接收不完整。");
                return;
            }
            catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                tracker.Report(force: true, message: $"分片 {index + 1} 中断，正在重试（{attempt + 1}/2）…");
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static HttpRequestMessage CreateRequest(Uri url, long start, long end, DownloadMetadata? metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            // HTTP/1.1 gives each simultaneous range its own bound TCP connection.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.UserAgent.ParseAdd("NetworkDownloader/1.0");
        if (metadata is not null)
            request.Headers.IfRange = metadata.ETag is not null
                ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(metadata.ETag))
                : new RangeConditionHeaderValue(metadata.LastModified!.Value);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("分片连接或响应头等待超时。", ex); }
    }

    private static SegmentLayout CreateLayout(long total, int count)
    {
        var ranges = new SegmentRange[count];
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var size = total / count + (i < total % count ? 1 : 0);
            ranges[i] = new(start, start + size - 1);
            start += size;
        }
        return new(1, ranges);
    }

    private static void ValidateLayout(DownloadMetadata metadata)
    {
        var layout = metadata.Segments!;
        if (metadata.Total is not > 0 || layout.Version != 1 || layout.Ranges is null ||
            layout.Ranges.Length is < 2 or > 16 || (metadata.ETag is null && !metadata.LastModified.HasValue))
            throw new InvalidDataException("分片续传信息损坏。");
        long next = 0;
        foreach (var range in layout.Ranges)
        {
            if (range is null || range.Start != next || range.End < range.Start || range.End >= metadata.Total.Value)
                throw new InvalidDataException("分片续传范围不连续或超出文件长度。");
            next = range.End + 1;
        }
        if (next != metadata.Total.Value) throw new InvalidDataException("分片续传范围未覆盖完整文件。");
    }

    private static long Length(SegmentRange range) => range.End - range.Start + 1;
    private static string PartPath(string destination, int index) =>
        Path.Combine(destination + ".part.segments", $"segment-{index:D2}.part");
    private static bool IsTransient(Exception ex) => ex is HttpRequestException { StatusCode: null } or IOException;
    private static bool HasEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase));

    internal static void CleanupParts(string destination)
    {
        for (var i = 0; i < 16; i++) TryDelete(PartPath(destination, i));
        // Never recursively delete: leave any unexpected user files in place.
        try { Directory.Delete(destination + ".part.segments", recursive: false); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class RangeNoLongerSupportedException : Exception;

    private sealed class AggregateProgress(long[] lengths, long total, int limit, IProgress<DownloadProgress>? progress)
    {
        private readonly object gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly long initial = lengths.Sum();
        private TimeSpan lastReport;

        public void Set(int index, long length)
        {
            Interlocked.Exchange(ref lengths[index], length);
            Report();
        }

        public void Report(bool force = false, string? message = null)
        {
            lock (gate)
            {
                if (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(150)) return;
                long downloaded = 0;
                for (var i = 0; i < lengths.Length; i++) downloaded += Interlocked.Read(ref lengths[i]);
                progress?.Report(new(downloaded, total, Math.Max(0, downloaded - initial) / Math.Max(.001, clock.Elapsed.TotalSeconds),
                    message ?? $"多线程下载（最多 {limit} 个连接）"));
                lastReport = clock.Elapsed;
            }
        }
    }
}
