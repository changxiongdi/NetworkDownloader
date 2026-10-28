using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetworkDownloader.BtWorker;
using NetworkDownloader.Core;

internal static class BtTests
{
    private const string Hash = "d7d7c2536baa0e5e00f859a44e4a3e2e838c7844";
    public static IEnumerable<(string Name, Func<Task> Run)> Create(string root)
    {
        yield return ("BT bare hex, magnet and Base32 identify the same v1 hash", () =>
        {
            Check(DownloadInput.Parse("  " + Hash.ToUpperInvariant() + "  ").InfoHash == Hash, "Hex parsing failed");
            var magnet = DownloadInput.Parse("magnet:?xt=urn:btih:" + Hash + "&tr=udp%3A%2F%2Ftracker.test%3A80&tr=https%3A%2F%2Ftracker.test%2Fa");
            Check(magnet.InfoHash == Hash && magnet.Value.Split("&tr=").Length == 3, "Magnet lost hash or trackers");
            Check(DownloadInput.Parse("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").InfoHash == new string('0', 40), "Base32 decoding failed");
            Check(DownloadInput.Parse("magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").InfoHash == new string('0', 40), "Magnet Base32 failed");
            Check(DownloadInput.Parse("MAGNET:?XT=URN:BTIH:" + Hash.ToUpperInvariant()).Value == "magnet:?xt=urn:btih:" + Hash, "Noncanonical magnet not normalized");
            return Task.CompletedTask;
        });
        yield return ("BT input routes HTTP, local torrent and v2 magnet without guessing invalid hashes", () =>
        {
            Check(DownloadInput.Parse("https://example.test/a.torrent").Kind == DownloadKind.Http, "HTTP file misclassified");
            Check(DownloadInput.Parse(Path.Combine(root, "local.torrent")).Kind == DownloadKind.TorrentFile, "Torrent file not recognized");
            Check(DownloadInput.Parse("magnet:?xt=urn:btmh:1220" + new string('a', 64)).InfoHash == new string('a', 64), "V2 magnet rejected");
            foreach (var bad in new[] { "", "hello", new string('a', 39), new string('g', 40), new string('a', 64),
                         "magnet:?dn=hello", "magnet:?xt=urn:btih:bad", "magnet:?xt=urn:btih:" + Hash + "&xt=urn:btih:" + new string('0', 40),
                         "magnet:?xt=urn:btih:magnet:?xt=urn:btih:" + Hash })
                ExpectFailure(() => DownloadInput.Parse(bad));
            return Task.CompletedTask;
        });
        yield return ("BT path policy rejects traversal, drive paths, reserved names and collisions", () =>
        {
            foreach (var bad in new[] { "../bad", "a/../../bad", "C:\\bad", "\\\\server\\file", "/root", "a/CON.txt", "a/LPT1", "a/file:stream", "a/b.", "a/b ", "a//b" })
                ExpectFailure(() => BtPathPolicy.ValidateRelativePath(bad));
            BtPathPolicy.ValidateRelativePath("合法目录/测试 文件.bin");
            ExpectFailure(() => BtPathPolicy.ValidateMetadata(new(Hash, "bad", [new(0, "A.bin", 1), new(1, "a.bin", 2)])));
            ExpectFailure(() => BtPathPolicy.ValidateMetadata(new(Hash, "bad", [new(0, "a", 1), new(1, "a/b", 2)])));
            return Task.CompletedTask;
        });
        yield return ("BT storage is exclusively owned and never adopts existing output", () =>
        {
            var directory = Path.Combine(root, "bt-storage");
            using (var storage = new BtTaskStorage(directory, Hash))
            {
                ExpectFailure(() => { using var other = new BtTaskStorage(directory, Hash); });
                storage.SaveSelection([0, 2]);
                Check(storage.ReadSelection()!.SequenceEqual(new[] { 0, 2 }), "Selection not saved");
            }
            using (var storage = new BtTaskStorage(directory, Hash)) Check(Directory.Exists(storage.OutputDirectory), "Resume lost output");
            var otherDirectory = Path.Combine(root, "bt-existing");
            Directory.CreateDirectory(Path.Combine(otherDirectory, "BT-" + Hash));
            ExpectFailure(() => { using var other = new BtTaskStorage(otherDirectory, Hash); });
            return Task.CompletedTask;
        });
        yield return ("Native BT engine inspects real metadata and rejects corrupt torrent bytes", async () =>
        {
            Console.WriteLine("  libtorrent " + NativeBtSession.Version);
            var directory = Path.Combine(root, "bt-inspect");
            Directory.CreateDirectory(directory);
            var bytes = new byte[1024 * 1024 + 37]; new Random(147).NextBytes(bytes);
            var (torrent, hash) = await CreateTorrent(directory, bytes);
            var metadata = NativeBtSession.Inspect(torrent);
            Check(metadata.Hash == hash && metadata.Files.Single().Path == "测试数据.bin" && metadata.Files[0].Size == bytes.Length, "Incorrect native metadata");
            BtPathPolicy.ValidateMetadata(metadata);
            var corrupt = Path.Combine(directory, "corrupt.torrent");
            await File.WriteAllTextAsync(corrupt, "this is not bencode");
            ExpectFailure(() => NativeBtSession.Inspect(corrupt));
        });
        yield return ("BT worker IPC rejects a vanished adapter and exits without networking", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await new BtWorkerClient().DownloadAsync(DownloadInput.Parse(Hash), Path.Combine(root, "bt-ipc"),
                    new NetworkAdapter("does-not-exist", "missing", IPAddress.Parse("192.0.2.77"), 1, "test"),
                    new Progress<DownloadProgress>(), (_, _) => throw new Exception("Unexpected metadata"), timeout.Token);
            }
            catch (IOException ex) when (ex.Message.Contains("网卡", StringComparison.Ordinal)) { return; }
            throw new Exception("Worker accepted nonexistent adapter");
        });
        foreach (var mode in new[] { "complete", "dismiss", "cancel" })
        yield return ($"BT worker IPC {mode} closes cleanly and preserves private torrent state", async () =>
        {
            var directory = Path.Combine(root, "bt-worker-" + mode);
            var bytes = new byte[128 * 1024 + 7]; new Random(815).NextBytes(bytes);
            var (torrent, hash) = await CreateTorrent(directory, bytes, isPrivate: true);
            var destination = Path.Combine(directory, "output");
            string output;
            using (var storage = new BtTaskStorage(destination, hash))
            {
                output = storage.OutputDirectory;
                if (mode == "complete") await File.WriteAllBytesAsync(Path.Combine(output, "测试数据.bin"), bytes);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var chose = false;
            try
            {
                var result = await new BtWorkerClient().DownloadAsync(DownloadInput.Parse(torrent), destination,
                    NetworkAdapters.GetAvailable().First(), new Progress<DownloadProgress>(), async (metadata, token) =>
                    {
                        chose = true;
                        Check(metadata.Hash == hash && metadata.Files.Count == 1, "Incorrect metadata over IPC");
                        if (mode == "cancel")
                        {
                            timeout.CancelAfter(200);
                            await Task.Delay(Timeout.Infinite, token);
                        }
                        return mode == "complete" ? new[] { 0 } : null;
                    }, timeout.Token);
                Check(mode == "complete" && result == output, "Worker completed a cancelled task");
                var actual = await File.ReadAllBytesAsync(Path.Combine(result, "测试数据.bin"));
                Check(actual.SequenceEqual(bytes), "Worker changed validated bytes");
            }
            catch (OperationCanceledException) when (mode != "complete" && chose) { }
            Check(chose, "Worker never delivered metadata");
            using var reopened = new BtTaskStorage(destination, hash);
            Check(File.Exists(reopened.ResumePath) && NativeBtSession.InspectResume(reopened.ResumePath).Hash == hash, "Worker did not flush state or release its task lock");
        });
        yield return ("BT resume restores unfinished blocks before connecting any peers", async () =>
        {
            var directory = Path.Combine(root, "bt-partial-block-resume");
            var seedPath = Path.Combine(directory, "seed");
            var downloadPath = Path.Combine(directory, "download");
            Directory.CreateDirectory(seedPath); Directory.CreateDirectory(downloadPath);
            var bytes = new byte[8 * 1024 * 1024 + 17]; new Random(912).NextBytes(bytes);
            var (torrent, _) = await CreateTorrent(directory, bytes, pieceLength: 2 * 1024 * 1024);
            await File.WriteAllBytesAsync(Path.Combine(seedPath, "测试数据.bin"), bytes);
            using var seed = new NativeBtSession(torrent, seedPath, "127.0.0.1", Path.Combine(directory, "seed.resume"), discovery: false);
            seed.Start([0]);
            var seeded = await Wait(seed, s => s.Finished && s.Port > 0);
            var resume = Path.Combine(directory, "download.resume");
            long saved;
            using (var download = new NativeBtSession(torrent, downloadPath, "127.0.0.1", resume, discovery: false))
            {
                await Wait(download, s => s.Metadata is not null);
                download.Start([0]);
                await Wait(download, s => s.State == 3);
                download.ConnectPeer("127.0.0.1", seeded.Port, tcpOnly: true);
                await Wait(download, s => s.Downloaded >= 256 * 1024);
                download.Stop();
                saved = download.Poll().Downloaded;
                Check(saved > 0 && saved < 2 * 1024 * 1024, $"Fixture must pause before a single whole piece completes (saved {saved})");
            }
            using (var download = new NativeBtSession(torrent, downloadPath, "127.0.0.1", resume, discovery: false))
            {
                await Wait(download, s => s.Metadata is not null);
                download.Start([0]);
                var resumed = await Wait(download, s => s.State == 3 && !s.Checking);
                Check(resumed.Peers == 0 && resumed.Downloaded >= saved, $"Resume lost unfinished blocks without peers: {saved} -> {resumed.Downloaded}");
                download.ConnectPeer("127.0.0.1", seeded.Port, tcpOnly: true);
                await Wait(download, s => s.Finished);
                download.Stop();
            }
            Check((await File.ReadAllBytesAsync(Path.Combine(downloadPath, "测试数据.bin"))).SequenceEqual(bytes), "Partial resume produced incorrect data");
            seed.Stop();
        });
        foreach (var utpOnly in new[] { false, true })
        yield return ($"Real local BT {(utpOnly ? "uTP" : "TCP")} metadata exchange, piece validation and durable restart", async () =>
        {
            var directory = Path.Combine(root, "bt-transfer-" + (utpOnly ? "utp" : "tcp"));
            var seedPath = Path.Combine(directory, "seed");
            var downloadPath = Path.Combine(directory, "download");
            Directory.CreateDirectory(seedPath); Directory.CreateDirectory(downloadPath);
            var bytes = new byte[4 * 1024 * 1024 + 17]; new Random(257).NextBytes(bytes);
            var (torrent, hash) = await CreateTorrent(directory, bytes);
            await File.WriteAllBytesAsync(Path.Combine(seedPath, "测试数据.bin"), bytes);
            using var seed = new NativeBtSession(torrent, seedPath, "127.0.0.1", Path.Combine(directory, "seed.resume"), discovery: false, utpOnly: utpOnly);
            seed.Start([0]);
            var seeded = await Wait(seed, s => s.Finished && s.Port > 0);
            var resume = Path.Combine(directory, "download.resume");
            long pausedBytes;
            using (var download = new NativeBtSession("magnet:?xt=urn:btih:" + hash, downloadPath, "127.0.0.1", resume, discovery: false, utpOnly: utpOnly))
            {
                await Wait(download, s => s.State == 2);
                download.ConnectPeer("127.0.0.1", seeded.Port, tcpOnly: !utpOnly);
                var status = await Wait(download, s => s.Metadata is not null);
                Check(status.Metadata!.Hash == hash, "Metadata exchange mismatch");
                download.Start([0]);
                await Wait(download, s => s.Downloaded >= 256 * 1024 || s.Finished);
                download.Stop();
                pausedBytes = download.Poll().Downloaded;
            }
            Check(File.Exists(resume) && NativeBtSession.InspectResume(resume).Hash == hash, "No durable resume metadata");
            using (var download = new NativeBtSession(torrent, downloadPath, "127.0.0.1", resume, discovery: false, utpOnly: utpOnly))
            {
                await Wait(download, s => s.Metadata is not null);
                download.Start([0]);
                var ready = await Wait(download, s => !s.Checking && (s.State == 3 || s.Finished));
                Check(ready.Downloaded >= pausedBytes && ready.Peers == 0, $"Resume re-downloaded saved bytes: {pausedBytes} -> {ready.Downloaded}");
                if (!ready.Finished) download.ConnectPeer("127.0.0.1", seeded.Port, tcpOnly: !utpOnly);
                await Wait(download, s => s.Finished);
                download.Stop();
            }
            var actual = await File.ReadAllBytesAsync(Path.Combine(downloadPath, "测试数据.bin"));
            Check(SHA256.HashData(actual).SequenceEqual(SHA256.HashData(bytes)), "BT download hash mismatch");
            // A forged complete marker must never hide a later on-disk edit.
            actual[12345] ^= 0xff;
            await File.WriteAllBytesAsync(Path.Combine(downloadPath, "测试数据.bin"), actual);
            using (var repaired = new NativeBtSession(torrent, downloadPath, "127.0.0.1", resume, discovery: false, utpOnly: utpOnly))
            {
                await Wait(repaired, s => s.Metadata is not null);
                repaired.Start([0]);
                var checkedData = await Wait(repaired, s => !s.Checking && (s.State == 3 || s.Finished));
                Check(!checkedData.Finished && checkedData.Downloaded < bytes.Length, "Modified data was considered complete before any peer connected");
                repaired.ConnectPeer("127.0.0.1", seeded.Port, tcpOnly: !utpOnly);
                await Wait(repaired, s => s.Finished);
                repaired.Stop();
            }
            actual = await File.ReadAllBytesAsync(Path.Combine(downloadPath, "测试数据.bin"));
            Check(SHA256.HashData(actual).SequenceEqual(SHA256.HashData(bytes)), "Recheck accepted modified data as complete");
            seed.Stop();
        });
        yield return ("BT TCP peer socket binds the selected local source address", async () =>
        {
            var adapter = NetworkAdapters.GetAvailable().First();
            var directory = Path.Combine(root, "bt-source-ip"); Directory.CreateDirectory(directory);
            var listener = new TcpListener(adapter.Address, 0); listener.Start();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var engine = new NativeBtSession("magnet:?xt=urn:btih:" + Hash, directory, adapter.Address.ToString(), Path.Combine(directory, "resume.dat"), discovery: false);
                await Wait(engine, s => s.State == 2 && s.Port > 0);
                var accepted = listener.AcceptTcpClientAsync(timeout.Token);
                engine.ConnectPeer(adapter.Address.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, tcpOnly: true);
                using var peer = await accepted;
                Check(((IPEndPoint)peer.Client.RemoteEndPoint!).Address.Equals(adapter.Address), "Peer connected using another source IP");
                engine.Stop();
            }
            finally { listener.Stop(); }
        });
        yield return ("BT refuses a nonexistent source IP instead of falling back", async () =>
        {
            var directory = Path.Combine(root, "bt-no-adapter"); Directory.CreateDirectory(directory);
            using var engine = new NativeBtSession("magnet:?xt=urn:btih:" + Hash, directory, "192.0.2.123", Path.Combine(directory, "resume.dat"), discovery: false);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                var state = engine.Poll();
                if (!string.IsNullOrEmpty(state.Error)) { Check(state.Port == 0, "Bound another interface"); return; }
                await Task.Delay(100);
            }
            throw new Exception("Missing adapter did not report binding failure");
        });
    }

    private static async Task<BtSnapshot> Wait(NativeBtSession engine, Func<BtSnapshot, bool> condition)
    {
        var clock = Stopwatch.StartNew();
        BtSnapshot? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(50))
        {
            status = engine.Poll();
            if (!string.IsNullOrEmpty(status.Error)) throw new Exception(status.Error);
            if (condition(status)) return status;
            await Task.Delay(100);
        }
        throw new TimeoutException($"BT timeout: state={status?.State}, peers={status?.Peers}, done={status?.Downloaded}/{status?.Total}, port={status?.Port}");
    }
    internal static async Task<(string Path, string Hash)> CreateTorrent(string directory, byte[] bytes, bool isPrivate = false, int pieceLength = 32768)
    {
        Directory.CreateDirectory(directory);
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < bytes.Length; offset += pieceLength)
            pieces.Write(SHA1.HashData(bytes.AsSpan(offset, Math.Min(pieceLength, bytes.Length - offset))));
        using var info = new MemoryStream();
        Write(info, "d6:lengthi" + bytes.Length + "e4:name"); WriteString(info, Encoding.UTF8.GetBytes("测试数据.bin"));
        Write(info, "12:piece lengthi" + pieceLength + "e6:pieces"); WriteString(info, pieces.ToArray());
        if (isPrivate) Write(info, "7:privatei1e");
        Write(info, "e");
        var data = info.ToArray();
        var hash = Convert.ToHexStringLower(SHA1.HashData(data));
        using var torrent = new MemoryStream(); Write(torrent, "d4:info"); torrent.Write(data); Write(torrent, "e");
        var path = Path.Combine(directory, "local.torrent");
        await File.WriteAllBytesAsync(path, torrent.ToArray());
        return (path, hash);
    }
    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
    private static void WriteString(Stream stream, byte[] bytes) { Write(stream, bytes.Length + ":"); stream.Write(bytes); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or OverflowException) { return; }
        throw new Exception("Unsafe input was accepted");
    }
}
