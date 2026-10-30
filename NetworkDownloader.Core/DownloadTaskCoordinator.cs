using System.Net;
using System.Security.Cryptography;

namespace NetworkDownloader.Core;

public sealed class DownloadTaskCoordinator : IAsyncDisposable
{
    private readonly DownloadTaskStore store;
    private readonly IDownloadTaskRunner runner;
    private readonly Func<IReadOnlyList<NetworkAdapter>> adapters;
    private readonly string cacheDirectory;
    private readonly DownloadLimits limits;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, DownloadTaskRecord> records = [];
    private readonly Dictionary<Guid, Run> active = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? polling;
    private bool stopping;
    private bool storageFault;
    private bool initialized;

    public event Action<DownloadTaskRecord>? TaskChanged;
    public event Action<Guid>? TaskRemoved;
    public event Action<string>? Faulted;
    public Func<DownloadTaskRecord, BtMetadata, CancellationToken, Task<int[]?>>? ChooseFilesAsync { get; set; }

    public DownloadTaskCoordinator(DownloadTaskStore store, IDownloadTaskRunner runner, string cacheDirectory,
        Func<IReadOnlyList<NetworkAdapter>>? adapters = null, DownloadLimits? limits = null, TimeProvider? clock = null)
    {
        this.store = store;
        this.runner = runner;
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.adapters = adapters ?? NetworkAdapters.GetAvailable;
        this.limits = limits ?? new();
        this.clock = clock ?? TimeProvider.System;
        this.limits.Validate();
        if (OperatingSystem.IsWindows() && runner is DownloadTaskRunner httpRunner) httpRunner.ConfigureHttpBudget(this.limits.MaxHttpConnections);
    }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (initialized) return;
            var loaded = await Task.Run(store.Load);
            await Task.Run(store.Backup);
            foreach (var saved in loaded)
            {
                var record = saved;
                if (record.IsRunning || record.State == DownloadTaskState.Queued)
                    record = record with { State = DownloadTaskState.Interrupted, BytesPerSecond = 0, Message = "上次运行已中断，等待恢复；已有数据保留" };
                if (record.State == DownloadTaskState.Completed)
                    record = record with { FilesMissing = !OutputExists(record), BytesPerSecond = 0 };
                records[record.Id] = record != saved ? await Task.Run(() => store.Save(record, true)) : record;
            }
            initialized = true;
            polling = PollAsync();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<DownloadTaskRecord>> GetTasksAsync()
    {
        await gate.WaitAsync();
        try { return records.Values.OrderBy(t => t.CreatedAt).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<DownloadTaskRecord> AddAsync(DownloadInput input, string directory, NetworkAdapter adapter, int connections = 4)
    {
        // The optional legacy argument is accepted for older callers only. Runtime
        // HTTP requests are budgeted by the shared adaptive runner, not reservations.
        if (connections is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(connections));
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("请选择保存目录。");
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory)) throw new ArgumentException("保存位置必须是目录。");
        await gate.WaitAsync();
        try
        {
            EnsureWritable();
            var available = ResolveAdapter(adapter.Id, adapter.Address.ToString());
            var id = Guid.NewGuid();
            var identity = input.InfoHash ?? input.Value;
            var originalName = input.Kind == DownloadKind.Http ? DownloadDestination.GetFileName(new(input.Value))
                : input.Kind == DownloadKind.TorrentFile ? Path.GetFileName(input.Value) : "磁力任务 · " + input.InfoHash![..12];
            byte[]? torrent = null;
            if (input.Kind == DownloadKind.TorrentFile)
            {
                torrent = await Task.Run(() =>
                {
                    using var file = File.OpenRead(input.Value);
                    if (file.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("种子文件为空或超过 16 MB。");
                    var bytes = new byte[checked((int)file.Length)];
                    file.ReadExactly(bytes);
                    return bytes;
                });
                identity = Convert.ToHexStringLower(SHA256.HashData(torrent));
            }
            if (records.Values.Any(t => SamePath(t.SaveDirectory, directory) && t.SourceIdentity == identity &&
                (t.IsBt || t.State != DownloadTaskState.Completed)))
                throw new InvalidOperationException("此目录已有相同任务，请在列表中继续或重试，避免重复下载。");
            string? output = null;
            if (input.Kind == DownloadKind.Http)
            {
                var reserved = records.Values.Where(t => !t.IsBt && t.OutputPath is not null)
                    .Select(t => t.OutputPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                output = await Task.Run(() => DownloadDestination.Resolve(new(input.Value), directory, reserved));
            }
            if (torrent is not null)
            {
                var path = Path.Combine(cacheDirectory, id.ToString("N") + ".torrent");
                await Task.Run(() => { Directory.CreateDirectory(cacheDirectory); File.WriteAllBytes(path, torrent); });
                input = input with { Value = path };
            }
            var record = new DownloadTaskRecord
            {
                Id = id, Input = input, Name = originalName, SourceIdentity = identity, InfoHash = input.InfoHash,
                SaveDirectory = directory, OutputPath = output, AdapterId = available.Id, AdapterName = available.Name,
                Address = available.Address.ToString(), Connections = 1, ElapsedMilliseconds = 0
            };
            await SaveLockedAsync(record, true);
            await PumpLockedAsync();
            return records[id];
        }
        finally { gate.Release(); }
    }

    public async Task ResumeAsync(IEnumerable<Guid> ids)
    {
        await gate.WaitAsync();
        try
        {
            EnsureWritable();
            foreach (var id in ids.Distinct())
                if (records.TryGetValue(id, out var record) && record.CanResume && !active.ContainsKey(id))
                    await SaveLockedAsync(record with { State = DownloadTaskState.Queued, Message = "等待调度", BytesPerSecond = 0 }, true);
            await PumpLockedAsync();
        }
        finally { gate.Release(); }
    }

    public async Task PauseAsync(IEnumerable<Guid> ids)
    {
        await gate.WaitAsync();
        try
        {
            foreach (var id in ids.Distinct())
            {
                if (!records.TryGetValue(id, out var record)) continue;
                if (active.TryGetValue(id, out var run))
                {
                    if (record.State == DownloadTaskState.Pausing) continue;
                    await SaveLockedAsync(record with { State = DownloadTaskState.Pausing, Message = "正在停止并保存断点…", BytesPerSecond = 0 }, true);
                    run.Cancellation.Cancel();
                }
                else if (record.State == DownloadTaskState.Queued)
                    await SaveLockedAsync(record with { State = DownloadTaskState.Paused, Message = "用户暂停，尚未开始传输", BytesPerSecond = 0 }, true);
            }
        }
        finally { gate.Release(); }
    }

    public async Task RebindAsync(Guid id, NetworkAdapter adapter)
    {
        await gate.WaitAsync();
        try
        {
            EnsureWritable();
            var record = records[id];
            if (!record.CanResume || active.ContainsKey(id)) throw new InvalidOperationException("请先暂停任务，等待断点保存后更换网卡。");
            var available = ResolveAdapter(adapter.Id, adapter.Address.ToString());
            await SaveLockedAsync(record with { AdapterId = available.Id, AdapterName = available.Name,
                Address = available.Address.ToString(), State = DownloadTaskState.Paused, Message = "网卡已更新，等待继续" }, true);
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(IEnumerable<Guid> ids)
    {
        await gate.WaitAsync();
        try
        {
            EnsureWritable();
            var targets = ids.Distinct().Where(records.ContainsKey).ToArray();
            if (targets.Any(id => active.ContainsKey(id))) throw new InvalidOperationException("请先暂停所选任务，等待断点保存后再移除。");
            foreach (var id in targets)
            {
                await Task.Run(() => store.Remove(records[id]));
                records.Remove(id);
                TaskRemoved?.Invoke(id);
            }
            // Deliberately never delete payloads, sidecars, torrent cache or directories.
        }
        finally { gate.Release(); }
    }

    private async Task PumpLockedAsync()
    {
        if (stopping || storageFault) return;
        foreach (var queued in records.Values.Where(t => t.State == DownloadTaskState.Queued).OrderBy(t => t.CreatedAt).ToArray())
        {
            if (active.Count >= limits.MaxTasks) break;
            var running = active.Keys.Select(id => records[id]).ToArray();
            if (running.Count(t => t.AdapterId == queued.AdapterId) >= limits.MaxTasksPerAdapter ||
                queued.IsBt && running.Count(t => t.IsBt) >= limits.MaxBtTasks) continue;
            NetworkAdapter adapter;
            try { adapter = ResolveAdapter(queued.AdapterId, queued.Address); }
            catch (IOException)
            {
                await SaveLockedAsync(queued with { State = DownloadTaskState.NetworkUnavailable, Message = "指定网卡已断开或 IP 改变；未切换其他网络" }, true);
                continue;
            }
            var started = queued with { State = queued.IsBt ? DownloadTaskState.ResolvingMetadata : DownloadTaskState.Connecting,
                Message = queued.IsBt ? "正在读取元数据 / 恢复记录" : "正在连接 / 读取断点", BytesPerSecond = 0,
                ElapsedMilliseconds = queued.ElapsedMilliseconds ?? 0,
                ElapsedIsPartial = queued.ElapsedIsPartial || !queued.ElapsedMilliseconds.HasValue,
                EstimatedRemainingSeconds = null, EstimatedCompletionAt = null,
                EstimateState = DownloadEstimateState.Estimating, IsFinalizing = false };
            await SaveLockedAsync(started, true);
            var run = new Run(started, clock);
            active.Add(queued.Id, run);
            _ = Task.Run(() => ExecuteAsync(queued.Id, run, adapter));
        }
    }

    private async Task ExecuteAsync(Guid id, Run run, NetworkAdapter adapter)
    {
        DownloadResult? result = null;
        Exception? error = null;
        try
        {
            DownloadTaskRecord task;
            await gate.WaitAsync();
            try { task = records[id]; }
            finally { gate.Release(); }
            result = await runner.RunAsync(task, adapter, new ProgressMailbox(run),
                (metadata, token) => SelectFilesAsync(id, run, metadata, token), run.Cancellation.Token);
        }
        catch (Exception ex) { error = ex; }
        await gate.WaitAsync();
        try
        {
            if (!IsCurrent(id, run)) return;
            var task = records[id];
            var last = Interlocked.Exchange(ref run.Latest, null);
            if (last is not null) task = ApplyProgress(task, last);
            if (result is not null)
            {
                // Completion wins a simultaneous pause only after the engine has flushed and published its output.
                task = task with { State = DownloadTaskState.Completed, OutputPath = result.OutputPath,
                    Downloaded = task.Total ?? task.Downloaded, Message = "下载完成", BytesPerSecond = 0,
                    CompletedAt = DateTimeOffset.UtcNow, FilesMissing = false };
            }
            else if (run.NetworkLost)
                task = task with { State = DownloadTaskState.NetworkUnavailable, Message = "指定网卡已断开或 IP 改变；断点已保留", BytesPerSecond = 0 };
            else if (error is OperationCanceledException)
                task = task with { State = DownloadTaskState.Paused, Message = "已暂停，断点数据已保留", BytesPerSecond = 0 };
            else
                task = task with { State = DownloadTaskState.Failed, Message = SafeError(error, task), BytesPerSecond = 0 };
            await SaveLockedAsync(task, true);
        }
        catch (Exception ex)
        {
            HandleStorageFault(ex);
            if (records.TryGetValue(id, out var stopped))
            {
                var interrupted = stopped with { State = DownloadTaskState.Interrupted, BytesPerSecond = 0,
                    Message = "引擎已停止，但任务记录未能落盘；请修复存储后重启校验恢复" };
                records[id] = interrupted;
                TaskChanged?.Invoke(interrupted);
            }
        }
        finally
        {
            active.Remove(id);
            run.Cancellation.Dispose();
            try { await PumpLockedAsync(); }
            catch (Exception ex) { HandleStorageFault(ex); }
            gate.Release();
            run.Finished.TrySetResult();
        }
    }

    private async Task<int[]?> SelectFilesAsync(Guid id, Run run, BtMetadata metadata, CancellationToken token)
    {
        BtPathPolicy.ValidateMetadata(metadata);
        DownloadTaskRecord task;
        await gate.WaitAsync(token);
        try
        {
            if (!IsCurrent(id, run)) throw new OperationCanceledException(token);
            task = records[id];
            if (records.Values.Any(t => t.Id != id && t.InfoHash == metadata.Hash && SamePath(t.SaveDirectory, task.SaveDirectory)))
                throw new IOException("列表已有相同 Hash 的 BT 任务，请继续原任务。");
            task = task with { Name = metadata.Name, InfoHash = metadata.Hash, OutputPath = Path.Combine(task.SaveDirectory, "BT-" + metadata.Hash),
                State = DownloadTaskState.AwaitingFileSelection, Message = "等待选择下载文件" };
            Interlocked.Exchange(ref run.Latest, null);
            await SaveLockedAsync(task, true);
        }
        finally { gate.Release(); }
        var selection = task.SelectedFiles ?? metadata.SelectedFiles;
        if (selection is not { Length: > 0 })
            selection = ChooseFilesAsync is null ? null : await ChooseFilesAsync(task, metadata, token);
        token.ThrowIfCancellationRequested();
        if (selection is not { Length: > 0 }) throw new OperationCanceledException("已取消文件选择。", token);
        selection = selection.Distinct().Order().ToArray();
        if (selection.Any(i => !metadata.Files.Any(f => f.Index == i && !f.Padding))) throw new InvalidDataException("保存的 BT 文件选择无效。");
        await gate.WaitAsync(token);
        try
        {
            if (!IsCurrent(id, run)) throw new OperationCanceledException(token);
            var files = metadata.Files.Where(f => selection.Contains(f.Index)).Select(f => new TaskOutputFile(f.Path, f.Size)).ToArray();
            await SaveLockedAsync(records[id] with { SelectedFiles = selection, OutputFiles = files, Total = files.Sum(f => f.Size),
                State = DownloadTaskState.Checking, Message = "正在恢复并校验已有分片" }, true);
        }
        finally { gate.Release(); }
        return selection;
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var lastNetworkCheck = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    IReadOnlyList<NetworkAdapter>? available = null;
                    if (DateTimeOffset.UtcNow - lastNetworkCheck >= TimeSpan.FromSeconds(1))
                    { available = adapters(); lastNetworkCheck = DateTimeOffset.UtcNow; }
                    foreach (var (id, run) in active.ToArray())
                    {
                        var task = records[id];
                        if (available is not null && !run.Cancellation.IsCancellationRequested &&
                            !available.Any(a => a.Id == task.AdapterId && a.Address.ToString() == task.Address))
                        {
                            run.NetworkLost = true;
                            await SaveLockedAsync(task with { State = DownloadTaskState.Pausing, Message = "网卡不可用，正在停止并保存断点…", BytesPerSecond = 0 }, true);
                            run.Cancellation.Cancel();
                            continue;
                        }
                        var progress = Interlocked.Exchange(ref run.Latest, null);
                        var now = clock.GetTimestamp();
                        if (progress is null && clock.GetElapsedTime(run.LastNotified, now) < TimeSpan.FromSeconds(1)) continue;
                        var update = progress is not null && task.State is not (DownloadTaskState.Pausing or DownloadTaskState.AwaitingFileSelection)
                            ? ApplyProgress(task, progress) : task;
                        update = run.Timing.Update(update);
                        run.LastNotified = now;
                        if (update.State != task.State || clock.GetElapsedTime(run.LastSaved, now) >= TimeSpan.FromSeconds(3))
                            await SaveLockedAsync(update, update.State != task.State);
                        else { records[id] = update; TaskChanged?.Invoke(update); }
                    }
                }
                catch (Exception ex) { HandleStorageFault(ex); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static DownloadTaskRecord ApplyProgress(DownloadTaskRecord task, DownloadProgress progress)
    {
        var state = task.State == DownloadTaskState.Pausing ? task.State : progress.IsChecking ? DownloadTaskState.Checking
            : task.IsBt && !progress.Total.HasValue ? DownloadTaskState.ResolvingMetadata : DownloadTaskState.Downloading;
        // Do not disguise a local BT hash check as a fresh 0% download.
        var downloaded = progress.IsChecking ? Math.Max(task.Downloaded, progress.Downloaded) : progress.Downloaded;
        return task with { State = state, Downloaded = downloaded, Total = task.IsBt ? progress.Total ?? task.Total : progress.Total,
            BytesPerSecond = Math.Max(0, progress.BytesPerSecond), Message = progress.Message,
            ActiveHttpRequests = progress.ActiveHttpRequests ?? 0, IsFinalizing = progress.IsFinalizing };
    }

    private async Task SaveLockedAsync(DownloadTaskRecord task, bool transition)
    {
        try
        {
            if (active.TryGetValue(task.Id, out var run))
            {
                task = run.Timing.Update(task);
                run.LastSaved = clock.GetTimestamp();
            }
            var saved = await Task.Run(() => store.Save(task, transition));
            records[saved.Id] = saved;
            TaskChanged?.Invoke(saved);
        }
        catch (Exception ex) { HandleStorageFault(ex); throw; }
    }

    private void HandleStorageFault(Exception _)
    {
        if (storageFault) return;
        storageFault = true;
        foreach (var run in active.Values) run.Cancellation.Cancel();
        Faulted?.Invoke("任务存储或协调器发生异常，已停止启动新任务并请求保存现有下载。请关闭程序，保留 tasks.db 及备份后检查磁盘权限和空间。");
    }

    private void EnsureWritable()
    {
        if (!initialized || stopping || storageFault) throw new InvalidOperationException("任务中心尚未就绪、正在退出或存储不可用。");
    }
    private bool IsCurrent(Guid id, Run run) => active.TryGetValue(id, out var current) && ReferenceEquals(current, run);
    private NetworkAdapter ResolveAdapter(string id, string address) => adapters().FirstOrDefault(a => a.Id == id && a.Address.ToString() == address)
        ?? throw new IOException("指定网卡已断开或 IP 改变，请暂停后重新选择网卡。");
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    private static bool OutputExists(DownloadTaskRecord task)
    {
        try
        {
            if (task.OutputPath is null) return false;
            if (!task.IsBt) return File.Exists(task.OutputPath) && (!task.Total.HasValue || new FileInfo(task.OutputPath).Length == task.Total);
            return Directory.Exists(task.OutputPath) && task.OutputFiles.Length > 0 && task.OutputFiles.All(f =>
            {
                BtPathPolicy.ValidateRelativePath(f.RelativePath);
                var info = new FileInfo(Path.Combine(task.OutputPath, f.RelativePath));
                return info.Exists && info.Length == f.Size;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private static string SafeError(Exception? error, DownloadTaskRecord task)
    {
        var text = error?.Message ?? "下载未完成，数据已保留";
        text = text.Replace(task.Input.Value, "[下载地址]", StringComparison.OrdinalIgnoreCase);
        // HTTP libraries and trackers may embed credential-bearing URLs in diagnostic text.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?:https?://|magnet:\?)[^\s\""<>]+", "[地址已隐藏]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return text.Length > 800 ? text[..800] : text;
    }

    public async Task ShutdownAsync()
    {
        Task[] pending;
        await gate.WaitAsync();
        try
        {
            stopping = true;
            try
            {
                foreach (var task in records.Values.Where(t => t.State == DownloadTaskState.Queued).ToArray())
                    if (!storageFault) await SaveLockedAsync(task with { State = DownloadTaskState.Paused, Message = "退出前暂停，等待继续" }, true);
            }
            finally { foreach (var run in active.Values) run.Cancellation.Cancel(); }
            pending = active.Values.Select(r => r.Finished.Task).ToArray();
        }
        finally { gate.Release(); }
        await Task.WhenAll(pending);
        lifetime.Cancel();
        if (polling is not null) await polling;
    }
    public async ValueTask DisposeAsync() { await ShutdownAsync(); lifetime.Dispose(); gate.Dispose(); }
    private sealed class Run(DownloadTaskRecord task, TimeProvider clock)
    {
        public DownloadTaskTiming Timing { get; } = new(task, clock);
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DownloadProgress? Latest;
        public long LastSaved = clock.GetTimestamp();
        public long LastNotified = clock.GetTimestamp();
        public bool NetworkLost;
    }
    private sealed class ProgressMailbox(Run run) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => Interlocked.Exchange(ref run.Latest, value);
    }
}
