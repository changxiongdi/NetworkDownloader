using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkDownloader.Core;

internal static class SegmentedTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Create(string root)
    {
        var bytes = new byte[2 * 1024 * 1024 + 17];
        new Random(93).NextBytes(bytes);
        var url = new Uri("https://parallel.test/file.bin");

        yield return ("Parallel ranges overlap in time and merge in correct byte order", async () =>
        {
            var path = NewPath();
            var active = 0;
            var maximum = 0;
            var ranges = new ConcurrentBag<(long Start, long End)>();
            using var client = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                if (!IsProbe(request))
                {
                    Check(request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact,
                        "Parallel worker did not request independent HTTP/1.1 connections");
                    var range = request.Headers.Range!.Ranges.Single();
                    ranges.Add((range.From!.Value, range.To!.Value));
                    InterlockedMax(ref maximum, Interlocked.Increment(ref active));
                    await Task.Delay(40, token);
                    Interlocked.Decrement(ref active);
                }
                return Respond(request, bytes);
            }));
            var reports = new ConcurrentBag<DownloadProgress>();
            await new DownloadService(client).DownloadAsync(url, path, new InlineProgress(reports.Add), CancellationToken.None, 4);
            Check(maximum == 4 && ranges.Count == 4, "Expected four simultaneous ranges");
            long next = 0;
            foreach (var range in ranges.OrderBy(x => x.Start))
            {
                Check(range.Start == next, "Ranges overlap or leave a gap");
                next = range.End + 1;
            }
            Check(next == bytes.Length, "Ranges do not cover the file");
            Check(reports.All(p => p.Downloaded >= 0 && (!p.Total.HasValue || p.Downloaded <= p.Total)), "Progress exceeds total");
            await AssertFile(path, bytes);
            Check(!Directory.Exists(path + ".part.segments") && !File.Exists(path + ".part.json"), "Completed sidecars not cleaned");
        });

        yield return ("Parallel cancellation preserves all offsets and resumes with fewer workers", async () =>
        {
            var path = NewPath();
            using var cancellation = new CancellationTokenSource();
            using (var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                var response = Respond(request, bytes);
                if (!IsProbe(request))
                {
                    var range = request.Headers.Range!.Ranges.Single();
                    var content = new StreamContent(new PacedStream(bytes[(int)range.From!.Value..((int)range.To!.Value + 1)]));
                    content.Headers.ContentRange = response.Content.Headers.ContentRange;
                    content.Headers.ContentLength = response.Content.Headers.ContentLength;
                    response.Content = content;
                }
                return Task.FromResult(response);
            })))
            {
                var progress = new InlineProgress(p =>
                {
                    if (p.Message.StartsWith("多线程", StringComparison.Ordinal) && p.Downloaded > 0) cancellation.Cancel();
                });
                await Throws<OperationCanceledException>(() => new DownloadService(client).DownloadAsync(url, path, progress, cancellation.Token, 4));
            }
            var saved = await ReadMetadata(path);
            var expected = saved.Segments!.Ranges.Select((range, i) => range.Start + PartLength(path, i)).ToArray();
            Check(expected.Where((offset, i) => offset > saved.Segments.Ranges[i].Start).Any(), "No bytes retained");
            Check(!File.Exists(path) && DownloadDestination.Resolve(url, Path.GetDirectoryName(path)!) == path, "Resume destination lost");
            var resumed = new ConcurrentBag<long>();
            using var resume = new HttpClient(new AsyncHandler((request, _) =>
            {
                if (!IsProbe(request)) resumed.Add(request.Headers.Range!.Ranges.Single().From!.Value);
                return Task.FromResult(Respond(request, bytes));
            }));
            await new DownloadService(resume).DownloadAsync(url, path, null, CancellationToken.None, 2);
            Check(resumed.All(expected.Contains), "A saved segment restarted from the wrong offset");
            await AssertFile(path, bytes);
        });

        yield return ("Completed segments are skipped when resuming with one worker", async () =>
        {
            var path = NewPath();
            var layout = await Seed(path, bytes, "\"v1\"", [int.MaxValue, 16384, 0, int.MaxValue]);
            var requested = new ConcurrentBag<long>();
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                if (!IsProbe(request)) requested.Add(request.Headers.Range!.Ranges.Single().From!.Value);
                return Task.FromResult(Respond(request, bytes));
            }));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 1);
            Check(requested.Count == 2 && requested.Contains(layout.Ranges[1].Start + 16384) && requested.Contains(layout.Ranges[2].Start),
                "Finished segments were downloaded again or partial offsets were lost");
            await AssertFile(path, bytes);
        });

        yield return ("No Range support automatically falls back to a full single request", async () =>
        {
            var path = NewPath();
            var calls = 0;
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                if (Interlocked.Increment(ref calls) == 2) Check(request.Headers.Range is null, "Fallback still requested a range");
                return Task.FromResult(Full(bytes));
            }));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4);
            Check(calls == 2, "Unexpected fallback request count");
            await AssertFile(path, bytes);
        });

        yield return ("No resource validator disables parallel downloads safely", async () =>
        {
            var path = NewPath();
            var calls = 0;
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                Interlocked.Increment(ref calls);
                var response = Respond(request, bytes);
                response.Headers.ETag = null;
                return Task.FromResult(response);
            }));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 8);
            Check(calls == 2, "Unsafe concurrent requests without a validator");
            await AssertFile(path, bytes);
        });

        yield return ("Legacy single-connection partial files still resume with parallel selected", async () =>
        {
            var path = NewPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".part", bytes[..16384]);
            await File.WriteAllTextAsync(path + ".part.json", JsonSerializer.Serialize(new DownloadMetadata(url.AbsoluteUri, "\"v1\"", null, bytes.Length)));
            var calls = 0;
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                Interlocked.Increment(ref calls);
                Check(request.Headers.Range!.Ranges.Single().From == 16384, "Legacy download was unnecessarily restarted/probed");
                return Task.FromResult(Respond(request, bytes));
            }));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4);
            Check(calls == 1, "Unexpected request count for legacy resume");
            await AssertFile(path, bytes);
        });

        yield return ("Changed remote ETag discards old segment bytes before reuse", async () =>
        {
            var path = NewPath();
            await Seed(path, bytes, "\"v1\"", [int.MaxValue, 4096, 8192, int.MaxValue]);
            var changed = bytes.Select(b => (byte)(b ^ 0x55)).ToArray();
            using var client = new HttpClient(new AsyncHandler((request, _) => Task.FromResult(Respond(request, changed, "\"v2\""))));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 8);
            await AssertFile(path, changed);
        });

        yield return ("Invalid range in a worker never publishes a corrupt merged file", async () =>
        {
            var path = NewPath();
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                var response = Respond(request, bytes);
                if (!IsProbe(request))
                {
                    var range = request.Headers.Range!.Ranges.Single();
                    response.Content.Headers.ContentRange = new(range.From!.Value + 1, range.To!.Value, bytes.Length);
                }
                return Task.FromResult(response);
            }));
            await Throws<InvalidDataException>(() => new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4));
            Check(!File.Exists(path), "Corrupt merged file published");
        });

        yield return ("Server ignoring ranges after the probe falls back without mixing bytes", async () =>
        {
            var path = NewPath();
            using var client = new HttpClient(new AsyncHandler((request, _) => Task.FromResult(
                IsProbe(request) ? Respond(request, bytes) : Full(bytes))));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4);
            await AssertFile(path, bytes);
            Check(!Directory.Exists(path + ".part.segments"), "Obsolete segment files were not cleaned");
        });

        yield return ("Interrupted segment retries from the bytes already written", async () =>
        {
            var path = NewPath();
            var failed = 0;
            var resumed = false;
            using var client = new HttpClient(new AsyncHandler((request, _) =>
            {
                var response = Respond(request, bytes);
                var range = request.Headers.Range!.Ranges.Single();
                if (!IsProbe(request) && range.From == 0 && Interlocked.Exchange(ref failed, 1) == 0)
                {
                    var content = new StreamContent(new FaultingStream(bytes[..((int)range.To!.Value + 1)]));
                    content.Headers.ContentLength = response.Content.Headers.ContentLength;
                    content.Headers.ContentRange = response.Content.Headers.ContentRange;
                    response.Content = content;
                }
                else if (range.From == 16384) resumed = true;
                return Task.FromResult(response);
            }));
            await new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4);
            Check(resumed, "Failed segment did not resume from byte 16384");
            await AssertFile(path, bytes);
        });

        yield return ("Cancel at merge retains complete segments for a request-free merge retry", async () =>
        {
            var path = NewPath();
            using var cancellation = new CancellationTokenSource();
            using (var client = new HttpClient(new AsyncHandler((request, _) => Task.FromResult(Respond(request, bytes)))))
            {
                var progress = new InlineProgress(p => { if (p.Message.Contains("正在合并", StringComparison.Ordinal)) cancellation.Cancel(); });
                await Throws<OperationCanceledException>(() => new DownloadService(client).DownloadAsync(url, path, progress, cancellation.Token, 4));
            }
            Check(!File.Exists(path), "Cancelled merge published a file");
            using var resume = new HttpClient(new AsyncHandler((request, _) =>
            {
                Check(IsProbe(request), "Complete segments were downloaded again during merge retry");
                return Task.FromResult(Respond(request, bytes));
            }));
            await new DownloadService(resume).DownloadAsync(url, path, null, CancellationToken.None, 2);
            await AssertFile(path, bytes);
        });

        yield return ("Tampered segment layout is rejected before issuing any request", async () =>
        {
            var path = NewPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var metadata = new DownloadMetadata(url.AbsoluteUri, "\"v1\"", null, bytes.Length,
                new(1, [new(0, 100), new(102, bytes.Length - 1)]));
            await File.WriteAllTextAsync(path + ".part.json", JsonSerializer.Serialize(metadata));
            using var client = new HttpClient(new AsyncHandler((_, _) => throw new Exception("Should not request")));
            await Throws<InvalidDataException>(() => new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None, 4));
        });

        yield return ("Connection count outside 1-16 is rejected", async () =>
        {
            using var client = new HttpClient(new AsyncHandler((_, _) => throw new Exception("Should not request")));
            await Throws<ArgumentOutOfRangeException>(() => new DownloadService(client).DownloadAsync(url, NewPath(), null, CancellationToken.None, 0));
            await Throws<ArgumentOutOfRangeException>(() => new DownloadService(client).DownloadAsync(url, NewPath(), null, CancellationToken.None, 17));
        });

        yield return ("Real parallel TCP connections all use the selected Windows source address", async () =>
        {
            var loopback = NetworkInterface.GetAllNetworkInterfaces().First(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            var adapter = new NetworkAdapter(loopback.Id, loopback.Name, IPAddress.Loopback,
                loopback.GetIPProperties().GetIPv4Properties()!.Index, "Loopback");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var peers = new ConcurrentBag<IPEndPoint>();
            var active = 0;
            var maximum = 0;
            var serving = Serve();
            using var client = BoundHttpClientFactory.Create(adapter);
            var path = NewPath();
            await new DownloadService(client).DownloadAsync(new Uri($"http://127.0.0.1:{port}/file.bin"), path, null, timeout.Token, 4);
            await serving;
            Check(maximum >= 2 && peers.Count == 5 && peers.All(p => p.Address.Equals(adapter.Address)), "Parallel connections not bound as expected");
            await AssertFile(path, bytes);

            async Task Serve()
            {
                var handlers = new List<Task>();
                for (var i = 0; i < 5; i++) handlers.Add(ServePeer(await listener.AcceptTcpClientAsync(timeout.Token)));
                await Task.WhenAll(handlers);
            }
            async Task ServePeer(TcpClient peer)
            {
                using (peer)
                {
                    peers.Add((IPEndPoint)peer.Client.RemoteEndPoint!);
                    await using var stream = peer.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    long start = 0, end = bytes.Length - 1;
                    while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                    {
                        if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                        var pair = line[13..].Split('-');
                        start = long.Parse(pair[0]);
                        end = long.Parse(pair[1]);
                    }
                    var probe = start == 0 && end == 0;
                    if (!probe)
                    {
                        InterlockedMax(ref maximum, Interlocked.Increment(ref active));
                        await Task.Delay(70, timeout.Token);
                    }
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Length: {end - start + 1}\r\nContent-Range: bytes {start}-{end}/{bytes.Length}\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, timeout.Token);
                    await stream.WriteAsync(bytes.AsMemory((int)start, (int)(end - start + 1)), timeout.Token);
                    if (!probe) Interlocked.Decrement(ref active);
                }
            }
        });

        string NewPath() => Path.Combine(root, "parallel-" + Guid.NewGuid().ToString("N"), "file.bin");
        async Task<SegmentLayout> Seed(string path, byte[] source, string etag, int[] lengths)
        {
            var ranges = new SegmentRange[4];
            var next = 0;
            Directory.CreateDirectory(path + ".part.segments");
            for (var i = 0; i < ranges.Length; i++)
            {
                var size = source.Length / 4 + (i < source.Length % 4 ? 1 : 0);
                ranges[i] = new(next, next + size - 1);
                await File.WriteAllBytesAsync(PartPath(path, i), source[next..(next + Math.Min(size, lengths[i]))]);
                next += size;
            }
            var layout = new SegmentLayout(1, ranges);
            await File.WriteAllTextAsync(path + ".part.json", JsonSerializer.Serialize(new DownloadMetadata(url.AbsoluteUri, etag, null, source.Length, layout)));
            return layout;
        }
    }

    private static string PartPath(string path, int index) => Path.Combine(path + ".part.segments", $"segment-{index:D2}.part");
    private static long PartLength(string path, int index) => File.Exists(PartPath(path, index)) ? new FileInfo(PartPath(path, index)).Length : 0;
    private static async Task<DownloadMetadata> ReadMetadata(string path) =>
        JsonSerializer.Deserialize<DownloadMetadata>(await File.ReadAllTextAsync(path + ".part.json"))!;
    private static bool IsProbe(HttpRequestMessage request) =>
        request.Headers.Range?.Ranges.Single() is { From: 0, To: 0 };
    private static HttpResponseMessage Respond(HttpRequestMessage request, byte[] bytes, string etag = "\"v1\"")
    {
        if (request.Headers.Range is null) return Full(bytes, etag);
        var range = request.Headers.Range.Ranges.Single();
        var start = (int)range.From!.Value;
        var end = (int)(range.To ?? (bytes.Length - 1));
        var response = Full(bytes[start..(end + 1)], etag);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new(start, end, bytes.Length);
        return response;
    }
    private static HttpResponseMessage Full(byte[] bytes, string etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.ETag = new(etag);
        return response;
    }
    private static async Task AssertFile(string path, byte[] expected) =>
        Check(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(expected)), "SHA-256 mismatch");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do { current = Volatile.Read(ref target); if (current >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
    private sealed class PacedStream(byte[] bytes) : NonSeekStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(4096, buffer.Length)], cancellationToken);
        }
    }
    private sealed class FaultingStream(byte[] bytes) : NonSeekStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 16384) throw new IOException("Simulated stream interruption");
            return base.ReadAsync(buffer[..Math.Min(16384, buffer.Length)], cancellationToken);
        }
    }
}
