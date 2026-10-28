using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetworkDownloader.Core;

public sealed record HttpResourcePressure(double Cpu = 0, double WriteMilliseconds = 0,
    double AdapterBytesPerSecond = 0, long LinkBitsPerSecond = 0);

public sealed record AdaptiveHttpOptions
{
    public int GlobalLimit { get; init; } = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
    public int AdapterLimit { get; init; } = 16;
    public int OriginLimit { get; init; } = 8;
    public int TaskLimit { get; init; } = Math.Clamp(Environment.ProcessorCount * 2, 2, 16);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan EvaluationInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(12);

    internal void Validate()
    {
        if (GlobalLimit is < 1 or > 128 || AdapterLimit is < 1 or > 128 || OriginLimit is < 1 or > 128 ||
            TaskLimit is < 1 or > 16 || SampleInterval <= TimeSpan.Zero || EvaluationInterval < SampleInterval || Cooldown < EvaluationInterval)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveHttpOptions));
    }
}

// Pure feedback policy: CPU is a safety ceiling, not an estimate of internet bandwidth.
public sealed class AdaptiveHttpPolicy(AdaptiveHttpOptions options)
{
    public int Target { get; private set; } = 1;
    public string Reason { get; private set; } = "低并发测量实际吞吐";
    private TimeSpan nextEvaluation = options.EvaluationInterval;
    private TimeSpan nextProbe;
    private double baseline;
    private bool trial;

    public void Evaluate(TimeSpan elapsed, double speed, HttpResourcePressure pressure)
    {
        if (elapsed < nextEvaluation) return;
        nextEvaluation = elapsed + options.EvaluationInterval;
        if (pressure.Cpu >= .85 || pressure.WriteMilliseconds >= 40 ||
            pressure.LinkBitsPerSecond > 0 && pressure.AdapterBytesPerSecond * 8 >= pressure.LinkBitsPerSecond * .9)
        {
            BackOff(elapsed, "CPU / 磁盘 / 网卡繁忙，降低并发");
            return;
        }
        if (elapsed < nextProbe || speed <= 0) return;
        if (trial)
        {
            trial = false;
            if (speed < baseline * 1.08)
            {
                Target = Math.Max(1, Target - 1);
                Reason = "增加连接收益不足，回退并等待再评估";
                nextProbe = elapsed + options.Cooldown * 3;
                return;
            }
        }
        if (Target < options.TaskLimit)
        {
            baseline = speed;
            Target++;
            trial = true;
            Reason = "逐步探测可用带宽";
        }
        else Reason = "已达到资源安全上限";
        nextProbe = elapsed + options.Cooldown;
    }

    public void BackOff(TimeSpan elapsed, string reason)
    {
        Target = Math.Max(1, Target / 2);
        trial = false;
        baseline = 0;
        nextProbe = elapsed + options.Cooldown;
        Reason = reason;
    }
}

// One controller is shared by every HTTP task. Leases count requests through BODY disposal,
// not just response headers. Waiting requests hold no socket / budget slot.
public sealed class AdaptiveHttpController
{
    private readonly object gate = new();
    private readonly List<AdaptiveHttpSession> sessions = [];
    private readonly List<Waiter> waiting = [];
    private readonly Dictionary<string, DateTimeOffset> blockedOrigins = [];
    private readonly AdaptiveResourceMonitor monitor = new();
    private readonly Func<string, HttpResourcePressure> resources;
    public AdaptiveHttpOptions Options { get; }

    public AdaptiveHttpController(AdaptiveHttpOptions? options = null, Func<string, HttpResourcePressure>? resources = null)
    {
        Options = options ?? new();
        Options.Validate();
        this.resources = resources ?? monitor.Sample;
    }

    public AdaptiveHttpSession Register(string adapterId, Uri url)
    {
        var session = new AdaptiveHttpSession(this, adapterId, Origin(url));
        lock (gate) sessions.Add(session);
        return session;
    }

    internal static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    internal HttpResourcePressure Resources(string adapter) => resources(adapter);

    internal int Desired(AdaptiveHttpSession session)
    {
        lock (gate) return DesiredLocked(session);
    }

    private int DesiredLocked(AdaptiveHttpSession session)
    {
        var sameNic = Math.Max(1, sessions.Count(s => s.AdapterId == session.AdapterId));
        var sameOrigin = Math.Max(1, sessions.Count(s => s.Origin == session.Origin));
        return Math.Max(1, Math.Min(Math.Min(session.Capacity, session.Policy.Target), Math.Min(
            Math.Max(1, Options.GlobalLimit / Math.Max(1, sessions.Count)), Math.Min(
            Math.Max(1, Options.AdapterLimit / sameNic), Math.Max(1, Options.OriginLimit / sameOrigin)))));
    }

    internal async Task<IDisposable> AcquireAsync(AdaptiveHttpSession session, Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var origin = Origin(uri);
        while (true)
        {
            TimeSpan delay;
            lock (gate) delay = blockedOrigins.TryGetValue(origin, out var until) ? until - DateTimeOffset.UtcNow : TimeSpan.Zero;
            if (delay <= TimeSpan.Zero) break;
            await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, token);
        }
        var waiter = new Waiter(session, origin);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(session.Disposed, session);
            waiting.Add(waiter);
            DispatchLocked();
        }
        using var registration = token.Register(() =>
        {
            lock (gate)
            {
                if (waiting.Remove(waiter)) waiter.Completion.TrySetCanceled(token);
                DispatchLocked();
            }
        });
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void DispatchLocked()
    {
        // Oldest eligible owner first, with fair per-task shares. An origin at capacity
        // must not head-of-line block work on a different adapter / origin.
        foreach (var waiter in waiting.ToArray())
        {
            var s = waiter.Session;
            if (blockedOrigins.TryGetValue(waiter.Origin, out var blocked) && blocked > DateTimeOffset.UtcNow) continue;
            if (sessions.Sum(x => x.Active) >= Options.GlobalLimit || s.Active >= DesiredLocked(s) ||
                sessions.Where(x => x.AdapterId == s.AdapterId).Sum(x => x.Active) >= Options.AdapterLimit ||
                sessions.Sum(x => x.Origins.GetValueOrDefault(waiter.Origin)) >= Options.OriginLimit) continue;
            waiting.Remove(waiter);
            s.Active++;
            s.Origins[waiter.Origin] = s.Origins.GetValueOrDefault(waiter.Origin) + 1;
            waiter.Completion.SetResult(new RequestLease(this, s, waiter.Origin));
        }
    }

    internal void Changed() { lock (gate) DispatchLocked(); }

    internal void Throttled(AdaptiveHttpSession session, Uri uri, TimeSpan delay)
    {
        var now = DateTimeOffset.UtcNow;
        if (delay > DateTimeOffset.MaxValue - now) delay = DateTimeOffset.MaxValue - now;
        var until = now + delay;
        lock (gate)
        {
            var key = Origin(uri);
            if (!blockedOrigins.TryGetValue(key, out var old) || old < until) blockedOrigins[key] = until;
        }
        session.BackOff("服务器限流，遵守 Retry-After 并退让");
        _ = WakeAfterAsync(until);
    }

    private async Task WakeAfterAsync(DateTimeOffset until)
    {
        // Recheck the deadline: timer precision / clock adjustments must not strand
        // queued requests when a wake happens a fraction of a millisecond early.
        while (until > DateTimeOffset.UtcNow)
        {
            var delay = until - DateTimeOffset.UtcNow;
            var slice = delay > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : delay;
            if (slice < TimeSpan.FromMilliseconds(1)) slice = TimeSpan.FromMilliseconds(1);
            await Task.Delay(slice).ConfigureAwait(false);
        }
        Changed();
    }

    internal void Remove(AdaptiveHttpSession session)
    {
        lock (gate)
        {
            session.Disposed = true;
            foreach (var w in waiting.Where(w => w.Session == session).ToArray())
            {
                waiting.Remove(w);
                w.Completion.TrySetException(new ObjectDisposedException(nameof(AdaptiveHttpSession)));
            }
            if (session.Active == 0) sessions.Remove(session);
            DispatchLocked();
        }
    }

    private sealed record Waiter(AdaptiveHttpSession Session, string Origin)
    {
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class RequestLease(AdaptiveHttpController owner, AdaptiveHttpSession session, string origin) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate)
            {
                session.Active--;
                session.Origins[origin]--;
                if (session.Disposed && session.Active == 0) owner.sessions.Remove(session);
                owner.DispatchLocked();
            }
        }
    }
}

public sealed class AdaptiveHttpSession : IDisposable
{
    private readonly AdaptiveHttpController owner;
    private readonly object sampleGate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastSample;
    private long bytes, lastBytes;
    private double writeMs, speed;
    internal AdaptiveHttpPolicy Policy { get; }
    internal string AdapterId { get; }
    internal string Origin { get; }
    internal int Active;
    internal int Capacity = 16;
    internal bool Disposed;
    internal Dictionary<string, int> Origins { get; } = [];
    public int DesiredWorkers => owner.Desired(this);
    public int ActiveRequests => Volatile.Read(ref Active);
    public double BytesPerSecond { get { lock (sampleGate) return speed; } }
    public string Status => $"自动并发 · 当前 {ActiveRequests} 个请求 · {Policy.Reason}";

    internal AdaptiveHttpSession(AdaptiveHttpController owner, string adapterId, string origin)
    { this.owner = owner; AdapterId = adapterId; Origin = origin; Policy = new(owner.Options); }

    public Task<IDisposable> AcquireAsync(Uri uri, CancellationToken token) => owner.AcquireAsync(this, uri, token);
    public void SetWorkerCapacity(int maximum)
    {
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        Volatile.Write(ref Capacity, maximum);
        owner.Changed();
    }
    public HttpMessageHandler CreateHandler(HttpMessageHandler inner) => new AdaptiveHttpHandler(owner, this, inner);
    public void RecordWrite(int count, TimeSpan duration)
    {
        lock (sampleGate)
        {
            bytes += count;
            writeMs = writeMs == 0 ? duration.TotalMilliseconds : writeMs * .8 + duration.TotalMilliseconds * .2;
        }
        Tick();
    }
    public void Tick()
    {
        lock (sampleGate)
        {
            var elapsed = clock.Elapsed;
            var delta = elapsed - lastSample;
            if (delta < owner.Options.SampleInterval) return;
            var current = (bytes - lastBytes) / delta.TotalSeconds;
            speed = speed == 0 ? current : speed * .4 + current * .6;
            lastBytes = bytes;
            lastSample = elapsed;
            var pressure = owner.Resources(AdapterId) with { WriteMilliseconds = writeMs };
            Policy.Evaluate(elapsed, speed, pressure);
        }
        owner.Changed();
    }
    public void BackOff(string reason)
    {
        lock (sampleGate) Policy.BackOff(clock.Elapsed, reason);
        owner.Changed();
    }
    public void Dispose() => owner.Remove(this);
}

internal sealed class AdaptiveResourceMonitor
{
    private readonly object gate = new();
    private TimeSpan lastCpu;
    private long timestamp;
    private double cpu;
    private readonly Dictionary<string, (long Timestamp, long Bytes, HttpResourcePressure Sample)> adapters = [];

    public HttpResourcePressure Sample(string adapterId)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (timestamp == 0 || Stopwatch.GetElapsedTime(timestamp, now).TotalSeconds >= 1)
            {
                using var process = Process.GetCurrentProcess();
                var currentCpu = process.TotalProcessorTime;
                cpu = timestamp == 0 ? 0 : Math.Clamp((currentCpu - lastCpu).TotalSeconds /
                    Stopwatch.GetElapsedTime(timestamp, now).TotalSeconds / Environment.ProcessorCount, 0, 1);
                timestamp = now; lastCpu = currentCpu;
            }
            var previous = adapters.GetValueOrDefault(adapterId);
            if (previous.Timestamp != 0 && Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds < 1)
                return previous.Sample with { Cpu = cpu };
            var result = new HttpResourcePressure(cpu);
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
                if (nic is not null)
                {
                    var stats = nic.GetIPv4Statistics();
                    // Includes BT and other processes using the selected NIC. Link speed is
                    // only a saturation guard, never an estimate of available WAN bandwidth.
                    var received = stats.BytesReceived;
                    var rate = previous.Timestamp == 0 ? 0 : Math.Max(0, received - previous.Bytes) /
                        Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                    result = new(cpu, 0, rate, nic.Speed);
                    adapters[adapterId] = (now, received, result);
                }
            }
            catch (NetworkInformationException) { }
            return result;
        }
    }
}
