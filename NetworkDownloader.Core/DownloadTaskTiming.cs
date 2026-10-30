namespace NetworkDownloader.Core;

public enum DownloadEstimateState { Estimating, Available, WaitingForData, UnknownSize, Checking, Finalizing, Inactive }

// One instance per engine generation, accessed only under the coordinator gate.
// Accounting uses monotonic time; the wall clock is used only for a display date.
public sealed class DownloadTaskTiming
{
    private readonly TimeProvider clock;
    private readonly bool partial;
    private long elapsedTicks;
    private long lastTimestamp;
    private bool counting;
    private readonly Queue<(long Timestamp, long Bytes)> samples = new();
    private long previousBytes;
    private long? previousTotal;
    private long lastDataTimestamp;
    private long lastSampleTimestamp;
    private bool observing;
    private bool stale;

    public DownloadTaskTiming(DownloadTaskRecord initial, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        var milliseconds = initial.ElapsedMilliseconds ?? 0;
        if (milliseconds < 0 || milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            throw new ArgumentOutOfRangeException(nameof(initial));
        elapsedTicks = milliseconds * TimeSpan.TicksPerMillisecond;
        partial = initial.ElapsedIsPartial || !initial.ElapsedMilliseconds.HasValue;
        lastTimestamp = this.clock.GetTimestamp();
        counting = CountsTime(initial.State);
    }

    public DownloadTaskRecord Update(DownloadTaskRecord task)
    {
        var now = clock.GetTimestamp();
        if (counting)
        {
            var delta = Math.Max(0, clock.GetElapsedTime(lastTimestamp, now).Ticks);
            elapsedTicks += Math.Min(delta, TimeSpan.MaxValue.Ticks - elapsedTicks);
        }
        lastTimestamp = now;
        counting = CountsTime(task.State);
        var state = Estimate(task, now, out var seconds);
        DateTimeOffset? completion = null;
        var utcNow = clock.GetUtcNow();
        if (seconds is { } remaining && remaining <= (DateTimeOffset.MaxValue - utcNow).TotalSeconds)
            completion = utcNow.AddSeconds(remaining);
        return task with
        {
            ElapsedMilliseconds = elapsedTicks / TimeSpan.TicksPerMillisecond, ElapsedIsPartial = partial,
            EstimateState = state, EstimatedRemainingSeconds = seconds, EstimatedCompletionAt = completion
        };
    }

    private DownloadEstimateState Estimate(DownloadTaskRecord task, long now, out double? seconds)
    {
        seconds = null;
        if (task.State != DownloadTaskState.Downloading || task.IsFinalizing)
        {
            observing = false; samples.Clear();
            return task.IsFinalizing && task.IsRunning ? DownloadEstimateState.Finalizing
                : task.State == DownloadTaskState.Checking ? DownloadEstimateState.Checking : DownloadEstimateState.Inactive;
        }
        if (!task.Total.HasValue || task.Total <= 0)
        {
            observing = false; samples.Clear();
            return DownloadEstimateState.UnknownSize;
        }
        if (task.Downloaded >= task.Total)
        {
            observing = false; samples.Clear();
            return DownloadEstimateState.Finalizing;
        }
        if (!observing || task.Downloaded < previousBytes || task.Total != previousTotal)
        {
            Reset(task, now);
            return DownloadEstimateState.Estimating;
        }
        if (task.Downloaded > previousBytes)
        {
            if (stale) Reset(task, now);
            lastDataTimestamp = now;
        }
        previousBytes = task.Downloaded;
        if (clock.GetElapsedTime(lastDataTimestamp, now) >= TimeSpan.FromSeconds(5))
        {
            samples.Clear(); stale = true;
            return DownloadEstimateState.WaitingForData;
        }
        if (clock.GetElapsedTime(lastSampleTimestamp, now) >= TimeSpan.FromSeconds(1))
        {
            samples.Enqueue((now, task.Downloaded)); lastSampleTimestamp = now;
        }
        // A bounded 15-second sliding window smooths both HTTP and BT byte deltas,
        // excluding pre-existing checkpoint bytes and local hash-check progress.
        while (samples.Count > 1 && clock.GetElapsedTime(samples.Peek().Timestamp, now) > TimeSpan.FromSeconds(15)) samples.Dequeue();
        var first = samples.Peek();
        var duration = clock.GetElapsedTime(first.Timestamp, now).TotalSeconds;
        var bytes = task.Downloaded - first.Bytes;
        if (duration < 3 || bytes <= 0) return DownloadEstimateState.Estimating;
        var estimate = (task.Total.Value - task.Downloaded) / (bytes / duration);
        if (!double.IsFinite(estimate) || estimate < 0) return DownloadEstimateState.Estimating;
        seconds = estimate;
        return DownloadEstimateState.Available;
    }

    private void Reset(DownloadTaskRecord task, long now)
    {
        observing = true; stale = false;
        samples.Clear(); samples.Enqueue((now, task.Downloaded));
        previousBytes = task.Downloaded; previousTotal = task.Total;
        lastDataTimestamp = lastSampleTimestamp = now;
    }

    private static bool CountsTime(DownloadTaskState state) => state is DownloadTaskState.Connecting or
        DownloadTaskState.ResolvingMetadata or DownloadTaskState.Checking or DownloadTaskState.Downloading or DownloadTaskState.Pausing;
}
