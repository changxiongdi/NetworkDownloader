using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NetworkDownloader.Core;

internal static class AdaptiveHttpTests
{
    private static readonly Uri Url = new("https://auto.test/file.bin");
    private static AdaptiveHttpOptions Fast => new()
    {
        GlobalLimit = 8, AdapterLimit = 6, OriginLimit = 4, TaskLimit = 4,
        SampleInterval = TimeSpan.FromMilliseconds(20), EvaluationInterval = TimeSpan.FromMilliseconds(100),
        Cooldown = TimeSpan.FromMilliseconds(200)
    };

    public static IEnumerable<(string Name, Func<Task> Run)> Create(string root)
    {
        yield return ("Adaptive policy probes gradually, rolls back diminishing returns and honors cooldown", () =>
        {
            var options = new AdaptiveHttpOptions { TaskLimit = 8 };
            var policy = new AdaptiveHttpPolicy(options);
            policy.Evaluate(TimeSpan.FromSeconds(1), 100, new()); Check(policy.Target == 1, "Did not warm up");
            policy.Evaluate(TimeSpan.FromSeconds(5), 100, new()); Check(policy.Target == 2, "No initial probe");
            policy.Evaluate(TimeSpan.FromSeconds(10), 120, new()); Check(policy.Target == 2, "Ignored cooldown");
            policy.Evaluate(TimeSpan.FromSeconds(20), 140, new()); Check(policy.Target == 3, "No throughput-driven increase");
            policy.Evaluate(TimeSpan.FromSeconds(35), 141, new()); Check(policy.Target == 2, "Diminishing return not rolled back");
            policy.Evaluate(TimeSpan.FromSeconds(40), 500, new()); Check(policy.Target == 2, "Probed too early after rollback");
            return Task.CompletedTask;
        });
        foreach (var (name, pressure) in new[]
        {
            ("CPU", new HttpResourcePressure(Cpu: .95)),
            ("disk write latency", new HttpResourcePressure(WriteMilliseconds: 80)),
            ("selected NIC including other traffic", new HttpResourcePressure(AdapterBytesPerSecond: 12_000_000, LinkBitsPerSecond: 100_000_000))
        })
            yield return ($"Adaptive policy backs off for {name}", () =>
            {
                var policy = new AdaptiveHttpPolicy(new());
                policy.Evaluate(TimeSpan.FromSeconds(5), 100, new());
                policy.Evaluate(TimeSpan.FromSeconds(20), 200, new());
                Check(policy.Target == 3, "Policy did not grow");
                policy.Evaluate(TimeSpan.FromSeconds(25), 400, pressure);
                Check(policy.Target == 1, "Resource pressure ignored");
                return Task.CompletedTask;
            });

        yield return ("Adaptive shared leases enforce global, adapter, origin and task limits fairly", async () =>
        {
            var controller = new AdaptiveHttpController(Fast with { GlobalLimit = 2, AdapterLimit = 1, OriginLimit = 1 }, _ => new());
            using var a = controller.Register("nic-a", Url);
            using var b = controller.Register("nic-b", Url);
            var other = new Uri("https://other.test/file");
            using var c = controller.Register("nic-b", other);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var first = await a.AcquireAsync(Url, timeout.Token);
            var originBlocked = b.AcquireAsync(Url, timeout.Token);
            using var independent = await c.AcquireAsync(other, timeout.Token);
            Check(!originBlocked.IsCompleted, "Origin limit exceeded / unrelated origin blocked");
            var globalBlocked = a.AcquireAsync(new("https://third.test/"), timeout.Token);
            Check(!globalBlocked.IsCompleted, "Global / task limit exceeded");
            first.Dispose();
            // B is still adapter-blocked by C. A may use its released global share.
            using var next = await globalBlocked.WaitAsync(timeout.Token);
            Check(!originBlocked.IsCompleted, "Adapter limit exceeded");
            independent.Dispose();
            using var granted = await originBlocked.WaitAsync(timeout.Token);
            Check(a.ActiveRequests == 1 && b.ActiveRequests == 1 && c.ActiveRequests == 0, "Wrong actual lease counts");
        });

        yield return ("Adaptive cancellation removes queued grants without leaking capacity", async () =>
        {
            var controller = new AdaptiveHttpController(Fast with { GlobalLimit = 1 }, _ => new());
            using var a = controller.Register("nic", Url);
            using var b = controller.Register("nic", Url);
            using var first = await a.AcquireAsync(Url, CancellationToken.None);
            using var cancel = new CancellationTokenSource();
            var pending = b.AcquireAsync(Url, cancel.Token);
            cancel.Cancel(); await Throws<OperationCanceledException>(async () => await pending);
            first.Dispose();
            using var fresh = await b.AcquireAsync(Url, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Check(b.ActiveRequests == 1 && a.ActiveRequests == 0, "Canceled waiter leaked a slot");
        });

        yield return ("Adaptive lease remains active until response BODY disposal", async () =>
        {
            var controller = new AdaptiveHttpController(Fast with { GlobalLimit = 1 }, _ => new());
            using var a = controller.Register("nic", Url);
            using var b = controller.Register("nic", Url);
            using var client = new HttpClient(a.CreateHandler(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3]) }))));
            var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
            var pending = b.AcquireAsync(Url, CancellationToken.None);
            _ = await response.Content.ReadAsByteArrayAsync();
            Check(!pending.IsCompleted && a.ActiveRequests == 1, "Headers / EOF released budget prematurely");
            response.Dispose();
            using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(a.ActiveRequests == 0, "Response disposal did not release lease");
        });

        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
            yield return ($"Adaptive {(int)status} honors Retry-After without holding request capacity", async () =>
            {
                var controller = new AdaptiveHttpController(Fast with { GlobalLimit = 1 }, _ => new());
                using var session = controller.Register("a", Url);
                using var other = controller.Register("b", new("https://other.test/"));
                var attempts = 0; var watch = Stopwatch.StartNew(); var times = new ConcurrentQueue<TimeSpan>();
                using var client = new HttpClient(session.CreateHandler(new Handler((_, _) =>
                {
                    times.Enqueue(watch.Elapsed);
                    var response = new HttpResponseMessage(Interlocked.Increment(ref attempts) == 1 ? status : HttpStatusCode.OK)
                    { Content = new ByteArrayContent([1]) };
                    if (attempts == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return Task.FromResult(response);
                })));
                var download = client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
                await Task.Delay(100);
                using (await other.AcquireAsync(new("https://other.test/"), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500)))
                    Check(session.ActiveRequests == 0, "Held capacity during Retry-After");
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(4));
                Check(attempts == 2 && times.Last() - times.First() >= TimeSpan.FromMilliseconds(950), "Retry-After ignored");
            });

        yield return ("Adaptive redirects budget the target origin before sending", async () =>
        {
            var target = new Uri("https://target.test/file");
            var controller = new AdaptiveHttpController(Fast with { OriginLimit = 1 }, _ => new());
            using var a = controller.Register("a", Url);
            using var b = controller.Register("b", target);
            using var targetLease = await b.AcquireAsync(target, CancellationToken.None);
            var targetCalled = false;
            using var client = new HttpClient(a.CreateHandler(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == Url.Host)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = target;
                    return Task.FromResult(redirect);
                }
                targetCalled = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })));
            var pending = client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
            await Task.Delay(100);
            Check(!targetCalled && a.ActiveRequests == 0, "Redirect bypassed target budget / retained source lease");
            targetLease.Dispose();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(targetCalled, "Redirect never resumed");
        });

        yield return ("Adaptive cancel interrupts long Retry-After immediately", async () =>
        {
            var controller = new AdaptiveHttpController(Fast, _ => new());
            using var session = controller.Register("nic", Url);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(session.CreateHandler(new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1));
                received.TrySetResult(); return Task.FromResult(response);
            })));
            using var cancel = new CancellationTokenSource();
            var pending = client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel();
            await Throws<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Check(session.ActiveRequests == 0, "Long Retry-After leaked a request");
        });

        yield return ("Adaptive three concurrent downloads obey shared budgets through bodies and all complete", async () =>
        {
            var bytes = new byte[2 * 1024 * 1024 + 17]; new Random(122).NextBytes(bytes);
            var controller = new AdaptiveHttpController(Fast with { GlobalLimit = 2, AdapterLimit = 1, OriginLimit = 1 }, _ => new());
            var urls = new[] { Url, Url, new Uri("https://other.test/file.bin") };
            var nics = new[] { "a", "b", "b" };
            var paths = urls.Select(_ => NewPath(root)).ToArray();
            var gate = new object(); var active = 0; var maximum = 0;
            var byNic = new Dictionary<string, int>(); var byOrigin = new Dictionary<string, int>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
            {
                using var session = controller.Register(nics[i], urls[i]);
                using var client = new HttpClient(session.CreateHandler(new Handler((request, _) =>
                {
                    var origin = request.RequestUri!.Authority;
                    lock (gate)
                    {
                        active++; maximum = Math.Max(maximum, active);
                        byNic[nics[i]] = byNic.GetValueOrDefault(nics[i]) + 1;
                        byOrigin[origin] = byOrigin.GetValueOrDefault(origin) + 1;
                        Check(active <= 2 && byNic[nics[i]] <= 1 && byOrigin[origin] <= 1, "Actual HTTP body exceeded shared budgets");
                    }
                    var response = Response(request, bytes, paced: true);
                    response.Content = new TrackedContent(response.Content, () =>
                    {
                        lock (gate) { active--; byNic[nics[i]]--; byOrigin[origin]--; }
                    });
                    return Task.FromResult(response);
                }))) { Timeout = Timeout.InfiniteTimeSpan };
                await new DownloadService(client, session).DownloadAsync(urls[i], paths[i], null, timeout.Token);
                await AssertFile(paths[i], bytes);
            }));
            Check(maximum == 2 && active == 0, "Independent work was starved / body budget leaked");
        });

        yield return ("Adaptive dynamic growth and worker retirement preserve hash and partial offsets", async () =>
        {
            var bytes = new byte[16 * 1024 * 1024 + 17]; new Random(312).NextBytes(bytes);
            var path = NewPath(root); var pressure = 0; var maxActive = 0; var reduced = false;
            var requests = new ConcurrentBag<(long From, long To)>();
            var controller = new AdaptiveHttpController(Fast, _ => new(Cpu: Volatile.Read(ref pressure) == 1 ? .95 : 0));
            using var session = controller.Register("nic", Url);
            using var client = Client(session, bytes, requests, paced: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var highSince = TimeSpan.Zero; var clock = Stopwatch.StartNew();
            var progress = new Inline(p =>
            {
                maxActive = Math.Max(maxActive, p.ActiveHttpRequests ?? 0);
                if (maxActive >= 2 && p.Downloaded > 512 * 1024 && highSince == TimeSpan.Zero)
                { highSince = clock.Elapsed; Volatile.Write(ref pressure, 1); }
                if (highSince != TimeSpan.Zero && clock.Elapsed - highSince > TimeSpan.FromMilliseconds(350))
                {
                    reduced |= session.DesiredWorkers == 1;
                    Volatile.Write(ref pressure, 0);
                }
            });
            await new DownloadService(client, session).DownloadAsync(Url, path, progress, timeout.Token);
            Check(maxActive >= 2 && reduced, "Did not exercise both growth and retirement");
            Check(requests.Where(x => x.To > 0).GroupBy(x => x.To).Any(g => g.Select(x => x.From).Distinct().Count() > 1), "Retired partial worker did not resume from saved bytes");
            await AssertFile(path, bytes);
            Check(session.ActiveRequests == 0, "Completed download leaked actual requests");
        });

        yield return ("Adaptive cancellation and restart keep each actual segment offset", async () =>
        {
            var bytes = new byte[8 * 1024 * 1024]; new Random(91).NextBytes(bytes);
            var path = NewPath(root); var requests = new ConcurrentBag<(long From, long To)>();
            var controller = new AdaptiveHttpController(Fast, _ => new());
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var session = controller.Register("nic", Url))
            using (var client = Client(session, bytes, requests, paced: true))
            {
                await Throws<OperationCanceledException>(() => new DownloadService(client, session).DownloadAsync(Url, path,
                    new Inline(p => { if (p.Downloaded >= 256 * 1024 && p.ActiveHttpRequests >= 2) cancel.Cancel(); }), cancel.Token));
                Check(session.ActiveRequests == 0, "Cancel returned before responses closed");
            }
            var saved = JsonSerializer.Deserialize<DownloadMetadata>(await File.ReadAllTextAsync(path + ".part.json"))!;
            var expected = saved.Segments!.Ranges.Select((r, i) => (From: r.Start + new FileInfo(Path.Combine(path + ".part.segments", $"segment-{i:D2}.part")).Length, r.End)).ToArray();
            Check(expected.Where((r, i) => r.From > saved.Segments.Ranges[i].Start && r.From <= r.End).Any(), "Test never retained an incomplete segment");
            requests.Clear();
            using var resumed = controller.Register("nic", Url);
            using var resumeClient = Client(resumed, bytes, requests, paced: false);
            await new DownloadService(resumeClient, resumed).DownloadAsync(Url, path, null, CancellationToken.None);
            Check(requests.Where(x => x.To != 0).All(x => expected.Contains((x.From, x.To))), "Resumed segment restarted from zero");
            await AssertFile(path, bytes);
        });

        yield return ("Adaptive legacy contiguous checkpoint resumes without converting or resetting it", async () =>
        {
            var bytes = new byte[1024 * 1024]; new Random(56).NextBytes(bytes);
            var path = NewPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".part", bytes[..65536]);
            await File.WriteAllTextAsync(path + ".part.json", JsonSerializer.Serialize(new DownloadMetadata(Url.AbsoluteUri, "\"auto-v1\"", null, bytes.Length)));
            var controller = new AdaptiveHttpController(Fast, _ => new());
            using var session = controller.Register("nic", Url);
            var requests = new ConcurrentBag<(long From, long To)>();
            using var client = Client(session, bytes, requests, paced: false);
            await new DownloadService(client, session).DownloadAsync(Url, path, null, CancellationToken.None);
            Check(requests.Count == 1 && requests.Single().From == 65536, "Contiguous legacy offset was discarded");
            await AssertFile(path, bytes);
        });

        yield return ("Adaptive range rejection drains workers before single stream fallback", async () =>
        {
            var bytes = new byte[1024 * 1024]; new Random(57).NextBytes(bytes);
            var controller = new AdaptiveHttpController(Fast, _ => new());
            using var session = controller.Register("nic", Url);
            using var client = new HttpClient(session.CreateHandler(new Handler((request, _) =>
            {
                if (request.Headers.Range?.Ranges.Single().To == 0) return Task.FromResult(Response(request, bytes, false));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes), Headers = { ETag = new("\"auto-v1\"") } });
            }))) { Timeout = Timeout.InfiniteTimeSpan };
            var path = NewPath(root);
            await new DownloadService(client, session).DownloadAsync(Url, path, null, CancellationToken.None);
            Check(session.ActiveRequests == 0 && session.DesiredWorkers == 1, "Fallback did not drain / constrain requests");
            await AssertFile(path, bytes);
        });

        yield return ("Adaptive repeated throttling is bounded and never publishes an error page", async () =>
        {
            var controller = new AdaptiveHttpController(Fast, _ => new());
            using var session = controller.Register("nic", Url); var count = 0;
            using var client = new HttpClient(session.CreateHandler(new Handler((_, _) =>
            {
                Interlocked.Increment(ref count);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("not a file") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            })));
            var path = NewPath(root);
            await Throws<HttpRequestException>(() => new DownloadService(client, session).DownloadAsync(Url, path, null, CancellationToken.None));
            Check(count == 4 && session.ActiveRequests == 0 && !File.Exists(path), "Unbounded retry / error publication / slot leak");
        });
    }

    private static HttpClient Client(AdaptiveHttpSession session, byte[] bytes, ConcurrentBag<(long From, long To)> requests, bool paced) =>
        new(session.CreateHandler(new Handler((request, _) =>
        {
            var r = request.Headers.Range?.Ranges.Single();
            requests.Add((r?.From ?? 0, r?.To ?? bytes.Length - 1));
            return Task.FromResult(Response(request, bytes, paced));
        }))) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, bool paced)
    {
        var range = request.Headers.Range?.Ranges.Single();
        var from = checked((int)(range?.From ?? 0)); var to = checked((int)(range?.To ?? bytes.Length - 1));
        var content = paced && to > 0 ? (HttpContent)new StreamContent(new PacedStream(bytes[from..(to + 1)])) : new ByteArrayContent(bytes[from..(to + 1)]);
        content.Headers.ContentLength = to - from + 1;
        if (range is not null) content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
        return new(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = content, Headers = { ETag = new("\"auto-v1\"") } };
    }

    private sealed class PacedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(6, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 16 * 1024)], cancellationToken);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly Action closed;
        private int disposed;
        public TrackedContent(HttpContent inner, Action closed)
        {
            this.inner = inner; this.closed = closed;
            foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => inner.ReadAsStreamAsync(token);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { inner.Dispose(); closed(); }
            base.Dispose(disposing);
        }
    }
    private sealed class Inline(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => action(value); }
    private static string NewPath(string root) => Path.Combine(root, "adaptive-" + Guid.NewGuid().ToString("N"), "file.bin");
    private static async Task AssertFile(string path, byte[] expected) => Check(
        SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(expected)), "SHA256 mismatch");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
