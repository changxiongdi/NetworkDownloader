using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetworkDownloader.Core;

public sealed record DownloadProgress(long Downloaded, long? Total, double BytesPerSecond, string Message, bool IsChecking = false,
    int? ActiveHttpRequests = null);

public sealed record DownloadMetadata(string Url, string? ETag, DateTimeOffset? LastModified, long? Total,
    SegmentLayout? Segments = null);

public sealed class DownloadService(HttpClient client, AdaptiveHttpSession? session = null)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    public async Task DownloadAsync(Uri url, string destination, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken, int connectionCount = 1)
    {
        if (url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo))
            throw new ArgumentException("请输入 HTTP/HTTPS 地址；不支持 URL 内嵌账号密码。", nameof(url));
        if (connectionCount is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(connectionCount), "下载连接数必须在 1–16 之间。");
        if (session is not null) progress = new AdaptiveProgress(progress, session);
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择新的文件名。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var lease = new FileStream(destination + ".part.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择新的文件名。");
        var saved = await ReadMetadataAsync(destination + ".part.json", cancellationToken);
        if ((session is not null || connectionCount > 1 || saved?.Segments is not null) &&
            await new SegmentedDownloader(client, session).TryDownloadAsync(url, destination, saved,
                session is null ? connectionCount : 16, progress, cancellationToken)) return;

        session?.SetWorkerCapacity(1);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(url, destination, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested &&
                (ex is HttpRequestException { StatusCode: null } ||
                 ex is IOException))
            {
                progress?.Report(new(GetPartialLength(destination), null, 0, $"连接中断，正在重试（{attempt + 1}/2）…"));
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task DownloadAttemptAsync(Uri url, string destination, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken, bool allowResume = true)
    {
        var partialPath = destination + ".part";
        var metadataPath = partialPath + ".json";
        var offset = GetPartialLength(destination);
        var previous = await ReadMetadataAsync(metadataPath, cancellationToken);
        if (offset > 0 && (previous is null || previous.Url != url.AbsoluteUri))
            throw new InvalidDataException("临时文件属于其他地址或缺少续传信息，请选择新的文件名。");

        // Without a validator, appending could silently combine two different versions.
        var canResume = allowResume && offset > 0 && previous is { Segments: null } &&
            (previous.ETag is not null || previous.LastModified.HasValue);
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.UserAgent.ParseAdd("NetworkDownloader/1.0");
        if (canResume)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = previous!.ETag is not null
                ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(previous.ETag))
                : new RangeConditionHeaderValue(previous.LastModified!.Value);
        }

        progress?.Report(new(offset, previous?.Total, 0, canResume ? "正在连接并申请断点续传…" : "正在连接…"));
        using var response = await SendWithTimeoutAsync(request, cancellationToken);
        // A 416 does not prove that the local bytes match the current remote resource.
        // Restart explicitly; never publish an unverified partial file as complete.
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && canResume)
        {
            response.Dispose(); // Release the actual-request budget before recursive retry.
            progress?.Report(new(0, null, 0, "服务器拒绝续传，正在重新下载…"));
            await DownloadAttemptAsync(url, destination, progress, cancellationToken, allowResume: false);
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("服务器未遵循 identity 编码，无法安全下载和续传。");

        var append = response.StatusCode == HttpStatusCode.PartialContent;
        long? total;
        if (append)
        {
            var range = response.Content.Headers.ContentRange;
            if (!canResume || range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
                range.From != offset || !range.To.HasValue || !range.Length.HasValue ||
                range.To.Value != range.Length.Value - 1 || range.To.Value < offset ||
                (response.Content.Headers.ContentLength.HasValue &&
                 response.Content.Headers.ContentLength.Value != range.To.Value - offset + 1))
                throw new InvalidDataException("服务器返回的 Content-Range 与续传位置不一致，已停止以防文件损坏。");
            if (previous!.Total.HasValue && previous.Total != range.Length)
                throw new InvalidDataException("远程文件大小已变化，无法追加现有临时文件。请换一个文件名重新下载。");
            if (previous.ETag is not null && response.Headers.ETag is not null &&
                previous.ETag != response.Headers.ETag.ToString())
                throw new InvalidDataException("远程文件标识已变化，无法安全续传。");
            if (previous.ETag is null && previous.LastModified.HasValue &&
                response.Content.Headers.LastModified.HasValue &&
                previous.LastModified != response.Content.Headers.LastModified)
                throw new InvalidDataException("远程文件修改时间已变化，无法安全续传。");
            total = range.Length;
        }
        else
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"服务器返回了不支持的下载状态：{(int)response.StatusCode}。");
            offset = 0;
            total = response.Content.Headers.ContentLength;
        }

        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        var metadata = new DownloadMetadata(url.AbsoluteUri, etag ?? (append ? previous?.ETag : null),
            response.Content.Headers.LastModified ?? (append ? previous?.LastModified : null), total);

        long finalLength;
        await using (var output = new FileStream(partialPath, append ? FileMode.Open : FileMode.Create,
            FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (append && output.Length != offset)
                throw new InvalidDataException("临时文件被其他程序修改，已停止下载。");
            output.Position = offset;
            // Persist only after truncating a restarted file, so old bytes cannot inherit new validators.
            await SaveMetadataAsync(metadataPath, metadata, cancellationToken);
            if (previous?.Segments is not null) SegmentedDownloader.CleanupParts(destination);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[128 * 1024];
            var downloaded = offset;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            while (true)
            {
                int count;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(ReadTimeout);
                    try { count = await input.ReadAsync(buffer, timeout.Token); }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    { throw new IOException("30 秒未收到数据。", ex); }
                }
                if (count == 0) break;
                if (total.HasValue && downloaded + count > total.Value)
                    throw new InvalidDataException("实际数据超过声明的文件大小。");
                var writeStart = Stopwatch.GetTimestamp();
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                session?.RecordWrite(count, Stopwatch.GetElapsedTime(writeStart));
                downloaded += count;
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(150))
                {
                    progress?.Report(new(downloaded, total, (downloaded - offset) / Math.Max(clock.Elapsed.TotalSeconds, .001), "正在下载"));
                    lastReport = clock.Elapsed;
                }
            }
            await output.FlushAsync(cancellationToken);
            if (total.HasValue && downloaded != total.Value)
                throw new IOException("接收的数据不完整，已保留临时文件。");
            cancellationToken.ThrowIfCancellationRequested();
            finalLength = downloaded;
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(partialPath, destination, overwrite: false);
        // Cleanup failure must not turn a successful download into an error/retry.
        try { File.Delete(metadataPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        progress?.Report(new(finalLength, total ?? finalLength, 0, "下载完成"));
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The adaptive handler times only the network operation, not fair-budget / Retry-After waiting.
        if (session is not null) return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("连接或响应头等待超时。", ex); }
    }

    private sealed class AdaptiveProgress(IProgress<DownloadProgress>? target, AdaptiveHttpSession session) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => target?.Report(value with
        {
            BytesPerSecond = value.BytesPerSecond > 0 ? session.BytesPerSecond : 0,
            ActiveHttpRequests = session.ActiveRequests,
            Message = value.Message + " · " + session.Status
        });
    }

    private static long GetPartialLength(string destination) =>
        File.Exists(destination + ".part") ? new FileInfo(destination + ".part").Length : 0;

    internal static async Task<DownloadMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DownloadMetadata>(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (JsonException ex)
        { throw new InvalidDataException("续传信息损坏，请选择其他目录，或手动移除对应临时文件。", ex); }
    }

    internal static async Task SaveMetadataAsync(string path, DownloadMetadata metadata, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(metadata), cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
