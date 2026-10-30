using System.Text.Json;
using NetworkDownloader.App;
using NetworkDownloader.Core;

internal static class TimingTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Create(string root)
    {
        yield return ("Timing counts execution but excludes queue, selection, pause and downtime", () =>
        {
            var clock = new ManualTimeProvider(); var task = Record(root);
            var timing = new DownloadTaskTiming(task, clock);
            clock.Advance(60); task = timing.Update(task); Check(task.ElapsedMilliseconds == 0, "Queue counted");
            task = timing.Update(task with { State = DownloadTaskState.Connecting });
            clock.Advance(2); task = timing.Update(task with { State = DownloadTaskState.ResolvingMetadata });
            clock.Advance(3); task = timing.Update(task with { State = DownloadTaskState.AwaitingFileSelection });
            clock.Advance(90); task = timing.Update(task); Check(task.ElapsedMilliseconds == 5000, "Picker wait counted");
            task = timing.Update(task with { State = DownloadTaskState.Checking });
            clock.Advance(4); task = timing.Update(task with { State = DownloadTaskState.Downloading });
            clock.Advance(5); task = timing.Update(task with { State = DownloadTaskState.Pausing });
            clock.Advance(1); task = timing.Update(task with { State = DownloadTaskState.Paused });
            clock.Advance(500); task = timing.Update(task);
            Check(task.ElapsedMilliseconds == 15000, "Wrong cumulative execution / flush time");
            clock.Advance(86400);
            var resumed = new DownloadTaskTiming(task with { State = DownloadTaskState.Connecting }, clock);
            clock.Advance(2); task = resumed.Update(task with { State = DownloadTaskState.Completed });
            clock.Advance(50); task = resumed.Update(task);
            Check(task.ElapsedMilliseconds == 17000, "Downtime counted / completed timer advanced");
            return Task.CompletedTask;
        });

        yield return ("Timing monotonic elapsed survives forward and backward wall clock changes", () =>
        {
            var clock = new ManualTimeProvider(); var task = Record(root) with { State = DownloadTaskState.Downloading };
            var timing = new DownloadTaskTiming(task, clock);
            clock.Advance(5); clock.ShiftUtc(TimeSpan.FromDays(7)); task = timing.Update(task);
            Check(task.ElapsedMilliseconds == 5000, "Forward wall clock affected elapsed");
            clock.Advance(7); clock.ShiftUtc(TimeSpan.FromDays(-14)); task = timing.Update(task);
            Check(task.ElapsedMilliseconds == 12000, "Backward wall clock affected elapsed");
            return Task.CompletedTask;
        });

        yield return ("Timing ETA uses recent byte deltas and excludes resumed checkpoint bytes", () =>
        {
            var clock = new ManualTimeProvider();
            var task = Record(root) with { State = DownloadTaskState.Downloading, Downloaded = 1000, Total = 2000, BytesPerSecond = 900000 };
            var timing = new DownloadTaskTiming(task, clock); task = timing.Update(task);
            Check(task.EstimateState == DownloadEstimateState.Estimating, "Did not warm up");
            for (var i = 0; i < 4; i++) { clock.Advance(1); task = timing.Update(task with { Downloaded = task.Downloaded + 100 }); }
            Check(task.EstimateState == DownloadEstimateState.Available && task.EstimatedRemainingSeconds == 6, "Checkpoint / engine lifetime speed polluted ETA");
            Check(task.EstimatedCompletionAt == clock.GetUtcNow().AddSeconds(6), "Wrong estimated date");
            return Task.CompletedTask;
        });

        yield return ("Timing ETA expires without callbacks and warms up after data returns", () =>
        {
            var clock = new ManualTimeProvider(); var task = Record(root) with { State = DownloadTaskState.Downloading, Total = 10000 };
            var timing = new DownloadTaskTiming(task, clock); task = timing.Update(task);
            for (var i = 0; i < 4; i++) { clock.Advance(1); task = timing.Update(task with { Downloaded = task.Downloaded + 100 }); }
            clock.Advance(6); task = timing.Update(task);
            Check(task.EstimateState == DownloadEstimateState.WaitingForData && task.EstimatedRemainingSeconds is null && task.EstimatedCompletionAt is null, "Stale speed still gives countdown");
            Check(task.ElapsedMilliseconds == 10000, "Time stopped without callbacks");
            task = timing.Update(task with { Downloaded = 500 });
            Check(task.EstimateState == DownloadEstimateState.Estimating, "Stale pre-gap samples reused");
            for (var i = 0; i < 4; i++) { clock.Advance(1); task = timing.Update(task with { Downloaded = task.Downloaded + 100 }); }
            Check(task.EstimateState == DownloadEstimateState.Available, "Data return never resumed estimation");
            return Task.CompletedTask;
        });

        yield return ("Timing ETA window adapts to bandwidth changes and resets for changed resources", () =>
        {
            var clock = new ManualTimeProvider(); var task = Record(root) with { State = DownloadTaskState.Downloading, Total = 1000000 };
            var timing = new DownloadTaskTiming(task, clock); task = timing.Update(task);
            for (var i = 0; i < 20; i++) { clock.Advance(1); task = timing.Update(task with { Downloaded = task.Downloaded + 100 }); }
            var oldEta = task.EstimatedRemainingSeconds;
            for (var i = 0; i < 20; i++) { clock.Advance(1); task = timing.Update(task with { Downloaded = task.Downloaded + 1000 }); }
            Check(task.EstimatedRemainingSeconds < oldEta / 5, "Window still uses lifetime speed");
            task = timing.Update(task with { Downloaded = 0 });
            Check(task.EstimateState == DownloadEstimateState.Estimating && task.EstimatedRemainingSeconds is null, "Restart mixed old and new bytes");
            clock.Advance(1); task = timing.Update(task with { Total = 2000000 });
            Check(task.EstimateState == DownloadEstimateState.Estimating, "Changed total did not reset estimate");
            return Task.CompletedTask;
        });

        yield return ("Timing unknown length, checking and finalizing never imply zero seconds to completion", () =>
        {
            var clock = new ManualTimeProvider(); var task = Record(root) with { State = DownloadTaskState.Downloading };
            var timing = new DownloadTaskTiming(task, clock); task = timing.Update(task);
            Check(task.EstimateState == DownloadEstimateState.UnknownSize, "Unknown size guessed");
            clock.Advance(3); task = timing.Update(task with { State = DownloadTaskState.Checking, Downloaded = 100, Total = 100 });
            Check(task.EstimateState == DownloadEstimateState.Checking && task.EstimatedRemainingSeconds is null, "Local check treated as transfer");
            clock.Advance(2); task = timing.Update(task with { State = DownloadTaskState.Downloading, IsFinalizing = true });
            Check(task.EstimateState == DownloadEstimateState.Finalizing && task.EstimatedRemainingSeconds is null, "Merge advertised 0 sec");
            clock.Advance(4); task = timing.Update(task with { State = DownloadTaskState.Completed });
            Check(task.ElapsedMilliseconds == 9000, "Check / merge time omitted");
            return Task.CompletedTask;
        });

        yield return ("Timing legacy JSON remains unknown while resumed legacy tasks mark partial duration", () =>
        {
            var task = Record(root) with { ElapsedMilliseconds = null, State = DownloadTaskState.Completed };
            var json = JsonSerializer.Serialize(task);
            var legacy = JsonSerializer.Deserialize<DownloadTaskRecord>(json)!;
            Check(new DownloadTaskViewModel(legacy).ElapsedText == "历史用时未知", "Fabricated historical duration");
            var clock = new ManualTimeProvider(); var timing = new DownloadTaskTiming(legacy with { State = DownloadTaskState.Connecting }, clock);
            clock.Advance(3); var resumed = timing.Update(legacy with { State = DownloadTaskState.Downloading });
            Check(resumed.ElapsedMilliseconds == 3000 && resumed.ElapsedIsPartial, "Legacy elapsed not marked partial");
            Check(new DownloadTaskViewModel(resumed).ElapsedText.StartsWith("升级后", StringComparison.Ordinal), "Partial duration mislabeled");
            return Task.CompletedTask;
        });

        yield return ("Timing persisted JSON includes elapsed but never stale estimates or merge stage", () =>
        {
            var task = Record(root) with { ElapsedMilliseconds = 123000, ElapsedIsPartial = true,
                EstimatedRemainingSeconds = 12, EstimatedCompletionAt = DateTimeOffset.UtcNow, EstimateState = DownloadEstimateState.Available, IsFinalizing = true };
            var loaded = JsonSerializer.Deserialize<DownloadTaskRecord>(JsonSerializer.Serialize(task))!;
            Check(loaded.ElapsedMilliseconds == 123000 && loaded.ElapsedIsPartial, "Elapsed did not persist");
            Check(loaded.EstimatedRemainingSeconds is null && loaded.EstimatedCompletionAt is null && !loaded.IsFinalizing && loaded.EstimateState == DownloadEstimateState.Inactive, "Runtime estimate persisted");
            return Task.CompletedTask;
        });

        yield return ("Timing compact labels round estimates up and preserve day-scale elapsed accuracy", () =>
        {
            Check(DownloadTaskViewModel.FormatDuration(59, true) == "不足1分钟", "Bad sub-minute estimate");
            Check(DownloadTaskViewModel.FormatDuration(61, true) == "2分钟", "ETA rounded down");
            Check(DownloadTaskViewModel.FormatDuration(3661, true) == "1小时2分", "Bad hour estimate");
            var task = Record(root) with { ElapsedMilliseconds = 90061000, State = DownloadTaskState.Paused };
            var model = new DownloadTaskViewModel(task);
            Check(model.ElapsedDetail == "1天 01:01:01" && model.RemainingText == "已暂停", "Day duration / pause label wrong");
            model.Update(task with { State = DownloadTaskState.Downloading, EstimateState = DownloadEstimateState.Finalizing, IsFinalizing = true });
            Check(model.Status == "合并中" && model.RemainingText == "正在合并", "Merge labels wrong");
            return Task.CompletedTask;
        });
    }

    private static DownloadTaskRecord Record(string root) => new()
    {
        Input = DownloadInput.Parse("https://timing.test/file.bin"), Name = "file.bin", SaveDirectory = root,
        AdapterId = "nic", AdapterName = "Test", Address = "127.0.0.1", ElapsedMilliseconds = 0
    };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private long ticks;
    private long utcShift;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero)
        .AddTicks(Interlocked.Read(ref ticks)).AddTicks(Interlocked.Read(ref utcShift));
    public void Advance(double seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    public void ShiftUtc(TimeSpan amount) => Interlocked.Add(ref utcShift, amount.Ticks);
}
