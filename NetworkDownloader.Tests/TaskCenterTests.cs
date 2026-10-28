using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NetworkDownloader.BtWorker;
using NetworkDownloader.Core;

internal static class TaskCenterTests
{
    private static readonly NetworkAdapter NicA = new("a", "以太网", IPAddress.Parse("192.0.2.10"), 1, "以太网");
    private static readonly NetworkAdapter NicB = new("b", "Wi-Fi", IPAddress.Parse("192.0.2.20"), 2, "Wi-Fi");
    private static readonly WindowsTaskDataProtector Protector = new();

    public static IEnumerable<(string Name, Func<Task> Run)> Create(string root)
    {
        yield return ("Task real BT worker caches torrent, verifies local pieces and persists completion", async () =>
        {
            var directory = NewDirectory(root); var bytes = new byte[128 * 1024 + 17]; new Random(819).NextBytes(bytes);
            var (torrent, hash) = await BtTests.CreateTorrent(directory, bytes, isPrivate: true);
            var destination = Path.Combine(directory, "downloads");
            using (var storage = new BtTaskStorage(destination, hash)) File.WriteAllBytes(Path.Combine(storage.OutputDirectory, "测试数据.bin"), bytes);
            using var store = Store(directory);
            await using var center = new DownloadTaskCoordinator(store, new DownloadTaskRunner(), Path.Combine(directory, "cache"));
            var picked = 0;
            center.ChooseFilesAsync = (_, metadata, _) => { picked++; Check(metadata.Hash == hash, "Wrong BT metadata"); return Task.FromResult<int[]?>([0]); };
            await center.InitializeAsync();
            var task = await center.AddAsync(DownloadInput.Parse(torrent), destination, NetworkAdapters.GetAvailable().First());
            File.Delete(torrent);
            await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Completed);
            var saved = store.Load().Single();
            Check(picked == 1 && saved.InfoHash == hash && saved.SelectedFiles!.SequenceEqual(new[] { 0 }), "BT catalog did not preserve metadata and selection");
            Check(saved.OutputPath == Path.Combine(destination, "BT-" + hash) && saved.Downloaded == bytes.Length, "Wrong BT output or progress");
            Check(File.ReadAllBytes(Path.Combine(saved.OutputPath!, "测试数据.bin")).SequenceEqual(bytes), "Verified BT file changed");
            Check(File.Exists(saved.Input.Value), "Cached torrent disappeared");
        });
        yield return ("Task real HTTP engine resumes segmented data through catalog restart", async () =>
        {
            var directory = NewDirectory(root);
            var loopback = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            var nic = new NetworkAdapter(loopback.Id, "Loopback", IPAddress.Loopback, loopback.GetIPProperties().GetIPv4Properties()!.Index, "Test");
            var payload = new byte[4 * 1024 * 1024 + 17]; new Random(171).NextBytes(payload);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var sessions = new ConcurrentBag<Task>();
            var offsets = new ConcurrentBag<long>();
            var server = Task.Run(async () =>
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(timeout.Token);
                        sessions.Add(Serve(client));
                    }
                }
                catch (OperationCanceledException) { }
            });
            Guid id; string output;
            try
            {
                using (var store = Store(directory))
                {
                    await using var center = new DownloadTaskCoordinator(store, new DownloadTaskRunner(), Path.Combine(directory, "cache"), () => [nic]);
                    await center.InitializeAsync();
                    var task = await center.AddAsync(DownloadInput.Parse($"http://127.0.0.1:{port}/real.bin"), directory, nic, 2);
                    id = task.Id; output = task.OutputPath!;
                    await UntilAsync(async () => (await center.GetTasksAsync()).Single().Downloaded > 0);
                    await center.PauseAsync([id]);
                    await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Paused);
                    Check(!File.Exists(output) && Directory.Exists(output + ".part.segments"), "Pause lost range checkpoint");
                }
                offsets.Clear();
                using (var store = Store(directory))
                {
                    await using var center = new DownloadTaskCoordinator(store, new DownloadTaskRunner(), Path.Combine(directory, "cache"), () => [nic]);
                    await center.InitializeAsync();
                    Check((await center.GetTasksAsync()).Single().State == DownloadTaskState.Paused, "Restart auto-started task");
                    await center.ResumeAsync([id]);
                    await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Completed);
                    Check(offsets.Any(offset => offset > 0 && offset != payload.Length / 2), "Did not request existing offsets");
                    Check(SHA256.HashData(File.ReadAllBytes(output)).SequenceEqual(SHA256.HashData(payload)), "Resumed HTTP output corrupted");
                    Check(store.Load().Single().State == DownloadTaskState.Completed, "Completion not durable");
                }
            }
            finally { timeout.Cancel(); await server; await Task.WhenAll(sessions.ToArray()); }

            async Task Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        string? range = null;
                        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                            if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) range = line[13..];
                        var pieces = range?.Split('-');
                        var from = pieces is null ? 0 : long.Parse(pieces[0]);
                        var to = pieces is null || string.IsNullOrEmpty(pieces[1]) ? payload.Length - 1 : long.Parse(pieces[1]);
                        offsets.Add(from);
                        var status = range is null ? "200 OK" : "206 Partial Content";
                        var contentRange = range is null ? "" : $"Content-Range: bytes {from}-{to}/{payload.Length}\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {to - from + 1}\r\n{contentRange}ETag: \"task-v1\"\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n"), timeout.Token);
                        for (var at = from; at <= to;)
                        {
                            var length = (int)Math.Min(16 * 1024, to - at + 1);
                            await stream.WriteAsync(payload.AsMemory((int)at, length), timeout.Token);
                            at += length; await Task.Delay(10, timeout.Token);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                }
            }
        });

        yield return ("Task storage failure stops engines instead of pretending persistence succeeded", async () =>
        {
            var directory = NewDirectory(root); var protector = new FailingProtector();
            using var store = new DownloadTaskStore(Path.Combine(directory, "tasks.db"), protector); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); var faults = 0;
            center.Faulted += _ => Interlocked.Increment(ref faults);
            await center.InitializeAsync(); var task = await center.AddAsync(Http("fault.bin"), directory, NicA);
            await Until(() => runner.Started.ContainsKey(task.Id));
            protector.Fail = true;
            runner.Started[task.Id].Progress.Report(new(20, 100, 5, "正在下载"));
            await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Interrupted);
            Check(faults == 1, "Storage fault was hidden or repeatedly notified");
            await ExpectFailureAsync(() => center.AddAsync(Http("never.bin"), directory, NicB));
            Check(store.Load().Single().State != DownloadTaskState.Completed, "Storage fault published completion");
        });
        yield return ("Task catalog encrypts signed links and rejects stale versions", () =>
        {
            var directory = NewDirectory(root);
            var db = Path.Combine(directory, "tasks.db");
            var record = Record(directory) with { Input = DownloadInput.Parse("https://example.test/file.bin?token=secret-credential-12345") };
            using (var store = new DownloadTaskStore(db, Protector))
            {
                var saved = store.Save(record, true);
                Check(saved.Version == 1 && store.Load().Single().Input.Value == record.Input.Value, "Catalog roundtrip failed");
                ExpectFailure(() => store.Save(saved with { Version = 9 }, true));
                ExpectFailure(() => { using var other = new DownloadTaskStore(db, Protector); });
                store.Backup();
            }
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(db)).Contains("secret-credential-12345", StringComparison.Ordinal), "Signed URL stored in clear text");
            Check(File.Exists(db + ".bak"), "SQLite online backup missing");
            using var reopened = new DownloadTaskStore(db, Protector);
            Check(reopened.Load().Single().Version == 1, "Stale write damaged record");
            return Task.CompletedTask;
        });

        yield return ("Task catalog does not replace a damaged database with an empty one", () =>
        {
            var db = Path.Combine(NewDirectory(root), "damaged.db");
            var bytes = Encoding.UTF8.GetBytes("not a sqlite database - preserve this evidence");
            File.WriteAllBytes(db, bytes);
            ExpectFailure(() => { using var store = new DownloadTaskStore(db, Protector); });
            Check(File.ReadAllBytes(db).SequenceEqual(bytes), "Damaged database overwritten");
            return Task.CompletedTask;
        });

        yield return ("Task catalog preserves good backup when protected payload cannot be decrypted", () =>
        {
            var directory = NewDirectory(root); var db = Path.Combine(directory, "tasks.db");
            using (var store = new DownloadTaskStore(db, Protector)) { store.Save(Record(directory), true); store.Backup(); }
            var before = File.ReadAllBytes(db + ".bak");
            using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE tasks SET payload=X'00'"; command.ExecuteNonQuery();
            }
            using (var store = new DownloadTaskStore(db, Protector)) ExpectFailure(() => store.Load());
            Check(before.SequenceEqual(File.ReadAllBytes(db + ".bak")), "Good backup was overwritten");
            return Task.CompletedTask;
        });

        yield return ("Task recovery preserves paused progress and never auto-starts interrupted tasks", async () =>
        {
            var directory = NewDirectory(root);
            using var store = Store(directory);
            store.Save(Record(directory) with { State = DownloadTaskState.Downloading, Downloaded = 320, Total = 1000 }, true);
            store.Save(Record(directory) with { State = DownloadTaskState.Paused, Downloaded = 500, Total = 1000 }, true);
            store.Save(Record(directory) with { State = DownloadTaskState.Queued }, true);
            var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory);
            await center.InitializeAsync();
            var tasks = await center.GetTasksAsync();
            Check(tasks.Count(t => t.State == DownloadTaskState.Interrupted) == 2 && tasks.Single(t => t.State == DownloadTaskState.Paused).Downloaded == 500, "Wrong restored state");
            Check(runner.Started.IsEmpty && tasks.First().Downloaded == 320, "Recovery auto-started or cleared progress");
        });

        yield return ("Task recovery marks missing completed files without redownloading", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory);
            store.Save(Record(directory) with { State = DownloadTaskState.Completed, Total = 42, OutputPath = Path.Combine(directory, "gone.bin") }, true);
            await using var center = Center(store, new ControlledRunner(), directory);
            await center.InitializeAsync();
            var task = (await center.GetTasksAsync()).Single();
            Check(task.State == DownloadTaskState.Completed && task.FilesMissing, "Missing completion was restarted or not marked");
        });

        yield return ("Task scheduler enforces global, per-NIC and BT budgets", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var bt1 = await center.AddAsync(Magnet('a'), directory, NicA);
            var bt2 = await center.AddAsync(Magnet('b'), directory, NicB);
            var http1 = await center.AddAsync(Http("one.bin"), directory, NicA);
            var http2 = await center.AddAsync(Http("two.bin"), directory, NicA);
            var http3 = await center.AddAsync(Http("three.bin"), directory, NicB);
            await Until(() => runner.Started.Count == 3);
            var tasks = await center.GetTasksAsync();
            Check(tasks.Single(t => t.Id == bt2.Id).State == DownloadTaskState.Queued && tasks.Single(t => t.Id == http2.Id).State == DownloadTaskState.Queued, "Budget violated");
            Check(runner.Started.ContainsKey(bt1.Id) && runner.Started.ContainsKey(http1.Id) && runner.Started.ContainsKey(http3.Id), "Eligible queue task starved");
            runner.Complete(bt1.Id);
            await Until(() => runner.Started.ContainsKey(bt2.Id));
            Check((await center.GetTasksAsync()).Count(t => t.IsRunning) <= 3, "Global task budget exceeded");
        });

        yield return ("Task scheduler reserves output names before network I/O and rejects duplicates", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var first = await center.AddAsync(DownloadInput.Parse("https://one.test/file.bin"), directory, NicA);
            var second = await center.AddAsync(DownloadInput.Parse("https://two.test/file.bin"), directory, NicB);
            Check(first.OutputPath != second.OutputPath && Path.GetFileName(second.OutputPath) == "file (1).bin", "Pending task output collision");
            await ExpectFailureAsync(() => center.AddAsync(first.Input, directory, NicA));
            Check((await center.GetTasksAsync()).Count == 2, "Duplicate task persisted");
        });

        yield return ("Task scheduler starts eligible HTTP tasks without reserving legacy connection counts", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var first = await center.AddAsync(Http("all.bin"), directory, NicA, 16);
            var queued = await center.AddAsync(Http("later.bin"), directory, NicB, 4);
            var bt = await center.AddAsync(Magnet('a'), directory, NicB);
            await Until(() => runner.Started.ContainsKey(first.Id) && runner.Started.ContainsKey(bt.Id) && runner.Started.ContainsKey(queued.Id));
            Check((await center.GetTasksAsync()).Where(t => !t.IsBt).All(t => t.Connections == 1), "Legacy manual count was persisted as a setting");
        });

        yield return ("Task pause waits for engine flush and double resume starts only one generation", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner { DelayStop = true };
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var task = await center.AddAsync(Http("resume.bin"), directory, NicA);
            await Until(() => runner.Started.ContainsKey(task.Id));
            runner.Started[task.Id].Progress.Report(new(400, 1000, 20, "正在下载"));
            await UntilAsync(async () => (await center.GetTasksAsync()).Single().Downloaded == 400);
            await center.PauseAsync([task.Id]); await center.ResumeAsync([task.Id]);
            Check((await center.GetTasksAsync()).Single().State == DownloadTaskState.Pausing && runner.Starts == 1, "Pause acknowledged before engine flush");
            runner.AllowStop.TrySetResult();
            await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Paused);
            var old = runner.Started[task.Id];
            await Task.WhenAll(center.ResumeAsync([task.Id]), center.ResumeAsync([task.Id]));
            await Until(() => runner.Starts == 2);
            old.Progress.Report(new(999, 1000, 10, "stale callback"));
            await Task.Delay(350);
            var resumed = (await center.GetTasksAsync()).Single();
            Check(resumed.Downloaded == 400 && resumed.Message != "stale callback", "Old generation overwrote resumed state");
        });

        yield return ("Task network loss stops only its adapter and requires explicit rebind", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            IReadOnlyList<NetworkAdapter> available = [NicA, NicB];
            await using var center = new DownloadTaskCoordinator(store, runner, directory, () => available);
            await center.InitializeAsync();
            var a = await center.AddAsync(Http("a.bin"), directory, NicA); var b = await center.AddAsync(Http("b.bin"), directory, NicB);
            await Until(() => runner.Started.Count == 2);
            available = [NicB];
            await UntilAsync(async () => (await center.GetTasksAsync()).Single(t => t.Id == a.Id).State == DownloadTaskState.NetworkUnavailable);
            Check((await center.GetTasksAsync()).Single(t => t.Id == b.Id).IsRunning, "Unrelated task stopped");
            await center.ResumeAsync([a.Id]);
            Check((await center.GetTasksAsync()).Single(t => t.Id == a.Id).State == DownloadTaskState.NetworkUnavailable, "Fell back to another adapter");
            await center.RebindAsync(a.Id, NicB); await center.ResumeAsync([a.Id]);
            await Until(() => runner.Starts == 3);
            Check(runner.Started[a.Id].Adapter.Id == NicB.Id, "Rebind not applied");
        });

        yield return ("Task removal rejects active work and preserves all downloaded data", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var task = await center.AddAsync(Http("keep.bin"), directory, NicA);
            await ExpectFailureAsync(() => center.RemoveAsync([task.Id]));
            await center.PauseAsync([task.Id]); await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Paused);
            File.WriteAllText(task.OutputPath + ".part", "keep partial"); File.WriteAllText(task.OutputPath!, "keep final");
            await center.RemoveAsync([task.Id]);
            Check((await center.GetTasksAsync()).Count == 0 && store.Load().Count == 0, "Record not removed");
            Check(File.ReadAllText(task.OutputPath + ".part") == "keep partial" && File.ReadAllText(task.OutputPath!) == "keep final", "Removal deleted payload");
        });

        yield return ("Task failure is isolated and credential-bearing errors are redacted", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var a = await center.AddAsync(Http("a.bin"), directory, NicA); var b = await center.AddAsync(Http("b.bin"), directory, NicB);
            await Until(() => runner.Started.Count == 2);
            runner.Started[a.Id].Result.TrySetException(new IOException("tracker https://test.invalid/?token=secret rejected"));
            await UntilAsync(async () => (await center.GetTasksAsync()).Single(t => t.Id == a.Id).State == DownloadTaskState.Failed);
            var records = await center.GetTasksAsync();
            Check(records.Single(t => t.Id == b.Id).IsRunning && !records.Single(t => t.Id == a.Id).Message.Contains("secret", StringComparison.Ordinal), "Failure affected other task or leaked token");
        });

        yield return ("Task torrent cache survives original file removal", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            var path = Path.Combine(directory, "source.torrent"); File.WriteAllText(path, "bounded test torrent data");
            var task = await center.AddAsync(DownloadInput.Parse(path), directory, NicA);
            File.Delete(path);
            Check(File.Exists(task.Input.Value) && task.Input.Value != path, "Original torrent path retained");
            await center.PauseAsync([task.Id]);
        });

        yield return ("Task BT file selection persists and resume does not reopen the picker", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new MetadataRunner();
            await using var center = new DownloadTaskCoordinator(store, runner, directory, () => [NicA, NicB]);
            var picks = 0;
            center.ChooseFilesAsync = (_, _, _) => { picks++; return Task.FromResult<int[]?>([1]); };
            await center.InitializeAsync(); var task = await center.AddAsync(Magnet('a'), directory, NicA);
            await Until(() => runner.Selections.Count == 1);
            await center.PauseAsync([task.Id]); await UntilAsync(async () => (await center.GetTasksAsync()).Single().State == DownloadTaskState.Paused);
            await center.ResumeAsync([task.Id]); await Until(() => runner.Selections.Count == 2);
            var saved = store.Load().Single();
            Check(picks == 1 && saved.SelectedFiles!.SequenceEqual(new[] { 1 }) && saved.OutputFiles.Single().RelativePath == "chosen.bin", "BT selection lost");
        });

        yield return ("Task shutdown drains engine stops and persists queued work as paused", async () =>
        {
            var directory = NewDirectory(root); using var store = Store(directory); var runner = new ControlledRunner();
            await using var center = Center(store, runner, directory); await center.InitializeAsync();
            await center.AddAsync(Http("a.bin"), directory, NicA); await center.AddAsync(Http("b.bin"), directory, NicA); await center.AddAsync(Http("c.bin"), directory, NicA);
            await center.ShutdownAsync();
            Check(store.Load().All(t => t.State == DownloadTaskState.Paused), "Shutdown left runnable task states");
        });
    }

    private static DownloadInput Http(string name) => DownloadInput.Parse("https://example.test/" + name);
    private static DownloadInput Magnet(char hex) => DownloadInput.Parse(new string(hex, 40));
    private static string NewDirectory(string root) { var path = Path.Combine(root, "tasks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static DownloadTaskStore Store(string directory) => new(Path.Combine(directory, "tasks.db"), Protector);
    private static DownloadTaskRecord Record(string directory) => new() { Input = Http("file.bin"), Name = "file.bin", SaveDirectory = directory, AdapterId = NicA.Id, AdapterName = NicA.Name, Address = NicA.Address.ToString() };
    private static DownloadTaskCoordinator Center(DownloadTaskStore store, ControlledRunner runner, string directory) => new(store, runner, Path.Combine(directory, "cache"), () => [NicA, NicB]);
    private static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private static async Task UntilAsync(Func<Task<bool>> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!await predicate()) await Task.Delay(20, timeout.Token); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectFailure(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Expected failure"); }
    private static async Task ExpectFailureAsync(Func<Task> action) { try { await action(); } catch { return; } throw new InvalidOperationException("Expected failure"); }
    private sealed record Invocation(DownloadTaskRecord Task, NetworkAdapter Adapter, IProgress<DownloadProgress> Progress, TaskCompletionSource<DownloadResult> Result);
    private sealed class ControlledRunner : IDownloadTaskRunner
    {
        public ConcurrentDictionary<Guid, Invocation> Started { get; } = [];
        public int Starts;
        public bool DelayStop;
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DownloadResult> RunAsync(DownloadTaskRecord task, NetworkAdapter adapter, IProgress<DownloadProgress> progress, Func<BtMetadata, CancellationToken, Task<int[]?>> chooseFiles, CancellationToken token)
        {
            var result = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Started[task.Id] = new(task, adapter, progress, result); Interlocked.Increment(ref Starts);
            try { return await result.Task.WaitAsync(token); }
            catch (OperationCanceledException) { if (DelayStop) await AllowStop.Task; throw; }
        }
        public void Complete(Guid id) => Started[id].Result.TrySetResult(new(Started[id].Task.OutputPath ?? Started[id].Task.SaveDirectory));
    }
    private sealed class MetadataRunner : IDownloadTaskRunner
    {
        public ConcurrentQueue<int[]> Selections { get; } = [];
        public async Task<DownloadResult> RunAsync(DownloadTaskRecord task, NetworkAdapter adapter, IProgress<DownloadProgress> progress, Func<BtMetadata, CancellationToken, Task<int[]?>> chooseFiles, CancellationToken token)
        {
            var metadata = new BtMetadata(new string('a', 40), "test torrent", [new(0, "skip.bin", 10), new(1, "chosen.bin", 20)]);
            Selections.Enqueue((await chooseFiles(metadata, token))!);
            await Task.Delay(Timeout.Infinite, token);
            return new(task.SaveDirectory);
        }
    }
    private sealed class FailingProtector : ITaskDataProtector
    {
        public volatile bool Fail;
        public byte[] Protect(byte[] data) => Fail ? throw new IOException("Simulated storage failure") : Protector.Protect(data);
        public byte[] Unprotect(byte[] data) => Protector.Unprotect(data);
    }
}
