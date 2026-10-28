using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkDownloader.Core;

var testRoot = Path.Combine(Path.GetTempPath(), "NetworkDownloaderTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var payload = new byte[256 * 1024];
new Random(42).NextBytes(payload);
var url = new Uri("https://example.test/file.bin");
var cases = new List<(string Name, Func<Task> Run)>
{
    ("URL filenames decode Chinese and exclude query strings and fragments", () =>
    {
        Check(DownloadDestination.GetFileName(new("https://example.test/files/archive.zip?token=abc#top")) == "archive.zip", "Query leaked into filename");
        Check(DownloadDestination.GetFileName(new("https://example.test/%E6%B5%8B%E8%AF%95%20%E6%96%87%E4%BB%B6.zip")) == "测试 文件.zip", "Filename was not decoded");
        Check(DownloadDestination.GetFileName(new("https://example.test/a+b.zip")) == "a+b.zip", "Plus in URL path changed");
        return Task.CompletedTask;
    }),
    ("URLs without filenames use download.bin", () =>
    {
        Check(DownloadDestination.GetFileName(new("https://example.test/")) == "download.bin", "Missing root fallback");
        Check(DownloadDestination.GetFileName(new("https://example.test/files/?token=abc")) == "download.bin", "Trailing slash treated as file");
        Check(DownloadDestination.GetFileName(new("https://example.test/%20%20")) == "download.bin", "Empty filename accepted");
        return Task.CompletedTask;
    }),
    ("Parsed names cannot escape the directory or use Windows reserved names", () =>
    {
        Check(DownloadDestination.GetFileName(new("https://example.test/folder%2Ffile%5Cname%3F.zip")) == "folder_file_name_.zip", "Encoded separators were not sanitized");
        Check(DownloadDestination.GetFileName(new("https://example.test/CON.zip")) == "_CON.zip", "Reserved CON accepted");
        Check(DownloadDestination.GetFileName(new("https://example.test/LPT1.txt")) == "_LPT1.txt", "Reserved LPT1 accepted");
        Check(DownloadDestination.GetFileName(new("https://example.test/file.zip%20%20...")) == "file.zip", "Trailing spaces or dots retained");
        var longName = DownloadDestination.GetFileName(new("https://example.test/" + new string('a', 240) + ".zip"));
        Check(longName.Length <= 180 && longName.EndsWith(".zip", StringComparison.Ordinal), "Long filename lost extension");
        return Task.CompletedTask;
    }),
    ("Download uses the chosen directory and parsed filename", async () =>
    {
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"), "folder.with.dots");
        var destination = DownloadDestination.Resolve(url, directory);
        Check(destination == Path.Combine(directory, "file.bin"), "Wrong destination");
        using var client = Fake((_, _) => Response(payload));
        await Run(client, destination);
        await AssertFile(destination, payload);
    }),
    ("Duplicate final files get numbered without overwriting", async () =>
    {
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "file.bin");
        await File.WriteAllTextAsync(original, "keep me");
        var destination = DownloadDestination.Resolve(url, directory);
        Check(Path.GetFileName(destination) == "file (1).bin", "Missing duplicate suffix");
        using var client = Fake((_, _) => Response(payload));
        await Run(client, destination);
        await AssertFile(destination, payload);
        Check(await File.ReadAllTextAsync(original) == "keep me", "Existing file overwritten");
    }),
    ("Directory resolution reuses this URL's numbered partial", async () =>
    {
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "file.bin"), "finished earlier");
        var partialDestination = Path.Combine(directory, "file (1).bin");
        await Seed(partialDestination, 8192);
        var destination = DownloadDestination.Resolve(url, directory);
        Check(destination == partialDestination, "Matching partial was not reused");
        using var client = Fake((request, _) =>
        {
            Check(request.Headers.Range?.Ranges.Single().From == 8192, "Directory-based resume lost offset");
            return Partial(8192);
        });
        await Run(client, destination);
        await AssertFile(destination, payload);
    }),
    ("Other URLs and orphan partial files are not reused", async () =>
    {
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var other = Path.Combine(directory, "file.bin");
        await Seed(other, 1024, new("https://other.test/file.bin", "\"v1\"", null, payload.Length));
        var orphan = Path.Combine(directory, "file (1).bin.part");
        await File.WriteAllTextAsync(orphan, "orphan");
        var destination = DownloadDestination.Resolve(url, directory);
        Check(Path.GetFileName(destination) == "file (2).bin", "Conflicting temporary files reused");
        Check(new FileInfo(other + ".part").Length == 1024 && await File.ReadAllTextAsync(orphan) == "orphan", "Other temporary files modified");
    }),
    ("A file cannot be selected as the save directory", async () =>
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "a file");
        await Throws<ArgumentException>(() => { DownloadDestination.Resolve(url, path); return Task.CompletedTask; });
    }),
    ("Adapter enumeration tolerates unavailable IPv4 protocols", () =>
    {
        var adapters = NetworkAdapters.GetAvailable();
        Check(adapters.All(x => x.Address.AddressFamily == AddressFamily.InterNetwork && x.InterfaceIndex > 0), "Invalid adapter");
        Console.WriteLine($"  Available IPv4 adapters: {adapters.Count}");
        return Task.CompletedTask;
    }),
    ("Full download matches SHA-256 and removes sidecar", async () =>
    {
        var path = NewPath();
        using var client = Fake((_, _) => Response(payload));
        await Run(client, path);
        await AssertFile(path, payload);
        Check(!File.Exists(path + ".part") && !File.Exists(path + ".part.json"), "Sidecar was not removed");
    }),
    ("Validated 206 appends exactly at the saved offset", async () =>
    {
        var path = NewPath();
        await Seed(path, 8192);
        using var client = Fake((request, _) =>
        {
            Check(request.Headers.Range?.Ranges.Single().From == 8192, "Missing Range");
            Check(request.Headers.IfRange?.EntityTag?.Tag == "\"v1\"", "Missing If-Range");
            Check(request.Headers.AcceptEncoding.Single().Value == "identity", "Missing identity");
            return Partial(8192);
        });
        await Run(client, path);
        await AssertFile(path, payload);
    }),
    ("200 when Range is ignored restarts without duplicated bytes", async () =>
    {
        var path = NewPath();
        await Seed(path, 12288);
        using var client = Fake((_, _) => Response(payload));
        await Run(client, path);
        await AssertFile(path, payload);
    }),
    ("Missing validators causes a fresh request", async () =>
    {
        var path = NewPath();
        await Seed(path, 1024, new(url.AbsoluteUri, null, null, payload.Length));
        using var client = Fake((request, _) =>
        {
            Check(request.Headers.Range is null, "Unsafe resume without a validator");
            return Response(payload);
        });
        await Run(client, path);
        await AssertFile(path, payload);
    }),
    ("Wrong Content-Range preserves the existing partial", async () =>
    {
        var path = NewPath();
        await Seed(path, 1024);
        using var client = Fake((_, _) => Partial(2048));
        await Throws<InvalidDataException>(() => Run(client, path));
        Check(new FileInfo(path + ".part").Length == 1024 && !File.Exists(path), "Invalid response modified the file");
    }),
    ("Changed ETag on a 206 is rejected", async () =>
    {
        var path = NewPath();
        await Seed(path, 1024);
        using var client = Fake((_, _) => { var response = Partial(1024); response.Headers.ETag = new("\"v2\""); return response; });
        await Throws<InvalidDataException>(() => Run(client, path));
        Check(!File.Exists(path), "Changed resource was published");
    }),
    ("Changed resource with a 200 safely starts over", async () =>
    {
        var path = NewPath();
        await Seed(path, 4096);
        var changed = Encoding.UTF8.GetBytes("A new version of the resource");
        using var client = Fake((_, _) => { var response = Response(changed); response.Headers.ETag = new("\"v2\""); return response; });
        await Run(client, path);
        await AssertFile(path, changed);
    }),
    ("416 triggers one fresh request, never assumes completion", async () =>
    {
        var path = NewPath();
        await Seed(path, 2048);
        var calls = 0;
        using var client = Fake((request, _) =>
        {
            calls++;
            if (calls == 1) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            Check(request.Headers.Range is null, "416 retry still has a Range");
            return Response(payload);
        });
        await Run(client, path);
        Check(calls == 2, "Unexpected request count");
        await AssertFile(path, payload);
    }),
    ("Other URL cannot reuse or destroy a partial file", async () =>
    {
        var path = NewPath();
        await Seed(path, 1024, new("https://another.test/file", "\"v1\"", null, payload.Length));
        using var client = Fake((_, _) => throw new Exception("Should not request"));
        await Throws<InvalidDataException>(() => Run(client, path));
        Check(new FileInfo(path + ".part").Length == 1024, "Other URL bytes were lost");
    }),
    ("Existing destination is never overwritten", async () =>
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "keep me");
        using var client = Fake((_, _) => throw new Exception("Should not request"));
        await Throws<IOException>(() => Run(client, path));
        Check(await File.ReadAllTextAsync(path) == "keep me", "Destination overwritten");
    }),
    ("Encoded response is rejected before file modification", async () =>
    {
        var path = NewPath();
        using var client = Fake((_, _) => { var response = Response(payload); response.Content.Headers.ContentEncoding.Add("gzip"); return response; });
        await Throws<InvalidDataException>(() => Run(client, path));
        Check(!File.Exists(path + ".part"), "Encoded bytes written");
    }),
    ("Cancel preserves bytes and validators, then resumes", async () =>
    {
        var path = NewPath();
        using (var client = Fake((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(payload)) };
            response.Content.Headers.ContentLength = payload.Length;
            response.Headers.ETag = new("\"v1\"");
            return response;
        }))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Throws<OperationCanceledException>(() => new DownloadService(client).DownloadAsync(url, path, null, cancellation.Token));
        }
        var offset = new FileInfo(path + ".part").Length;
        Check(offset > 0 && offset < payload.Length && !File.Exists(path), "Cancel did not retain a partial file");
        using var resumeClient = Fake((request, _) =>
        {
            Check(request.Headers.Range?.Ranges.Single().From == offset, "Wrong resumed offset");
            return Partial((int)offset);
        });
        await Run(resumeClient, path);
        await AssertFile(path, payload);
    }),
    ("Last-Modified works as a fallback validator", async () =>
    {
        var path = NewPath();
        var modified = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        await Seed(path, 2048, new(url.AbsoluteUri, null, modified, payload.Length));
        using var client = Fake((request, _) =>
        {
            Check(request.Headers.IfRange?.Date == modified, "Missing date validator");
            var response = Partial(2048);
            response.Headers.ETag = null;
            response.Content.Headers.LastModified = modified;
            return response;
        });
        await Run(client, path);
        await AssertFile(path, payload);
    }),
    ("Unknown-length response completes at EOF", async () =>
    {
        var path = NewPath();
        using var client = Fake((_, _) => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekStream(payload)) });
        await Run(client, path);
        await AssertFile(path, payload);
    }),
    ("404 does not publish an error page", async () =>
    {
        var path = NewPath();
        using var client = Fake((_, _) => new(HttpStatusCode.NotFound));
        await Throws<HttpRequestException>(() => Run(client, path));
        Check(!File.Exists(path) && !File.Exists(path + ".part"), "404 page was saved");
    }),
    ("Transient connection failure retries and succeeds", async () =>
    {
        var path = NewPath();
        var attempts = 0;
        using var client = Fake((_, _) => ++attempts == 1
            ? throw new HttpRequestException("Simulated network interruption") : Response(payload));
        await Run(client, path);
        Check(attempts == 2, "Connection failure was not retried");
        await AssertFile(path, payload);
    }),
    ("Truncated response never becomes a final file", async () =>
    {
        var path = NewPath();
        using var client = Fake((_, _) =>
        {
            var response = Response(payload[..4096]);
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        });
        await Throws<IOException>(() => Run(client, path));
        Check(!File.Exists(path) && new FileInfo(path + ".part").Length == 4096, "Truncated file was published");
    }),
    ("Real TCP connection binds the specified Windows interface", TestBoundConnection)
};

cases.AddRange(SegmentedTests.Create(testRoot));
var failed = 0;
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}\n{ex}"); }
}
Console.WriteLine($"\n{cases.Count - failed}/{cases.Count} tests passed. Test files: {testRoot}");
return failed == 0 ? 0 : 1;

string NewPath() => Path.Combine(testRoot, Guid.NewGuid().ToString("N") + ".bin");
Task Run(HttpClient client, string path) => new DownloadService(client).DownloadAsync(url, path, null, CancellationToken.None);
async Task Seed(string path, int length, DownloadMetadata? metadata = null)
{
    await File.WriteAllBytesAsync(path + ".part", payload[..length]);
    await File.WriteAllTextAsync(path + ".part.json", JsonSerializer.Serialize(metadata ?? new(url.AbsoluteUri, "\"v1\"", null, payload.Length)));
}
HttpResponseMessage Response(byte[] bytes)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    response.Headers.ETag = new("\"v1\"");
    return response;
}
HttpResponseMessage Partial(int offset)
{
    var response = Response(payload[offset..]);
    response.StatusCode = HttpStatusCode.PartialContent;
    response.Content.Headers.ContentRange = new(offset, payload.Length - 1, payload.Length);
    return response;
}
async Task AssertFile(string path, byte[] expected) =>
    Check(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(expected)), "SHA-256 mismatch");
static HttpClient Fake(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) => new(new FakeHandler(send));
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected exception " + typeof(T).Name);
}

async Task TestBoundConnection()
{
    var loopback = NetworkInterface.GetAllNetworkInterfaces().First(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
    var adapter = new NetworkAdapter(loopback.Id, loopback.Name, IPAddress.Loopback,
        loopback.GetIPProperties().GetIPv4Properties()!.Index, "Loopback");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var server = Serve();
    using var client = BoundHttpClientFactory.Create(adapter);
    var path = NewPath();
    await new DownloadService(client).DownloadAsync(new Uri($"http://127.0.0.1:{port}/file"), path, null, timeout.Token);
    await server;
    await AssertFile(path, payload);

    async Task Serve()
    {
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Check(((IPEndPoint)peer.Client.RemoteEndPoint!).Address.Equals(IPAddress.Loopback), "Unexpected source IP");
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, timeout.Token);
        await stream.WriteAsync(payload, timeout.Token);
    }
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request, cancellationToken));
}

class NonSeekStream(byte[] bytes) : Stream
{
    private int position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var length = Math.Min(count, bytes.Length - position);
        bytes.AsSpan(position, length).CopyTo(buffer.AsSpan(offset, length));
        position += length;
        return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = Math.Min(buffer.Length, bytes.Length - position);
        bytes.AsMemory(position, length).CopyTo(buffer);
        position += length;
        return ValueTask.FromResult(length);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class SlowStream(byte[] bytes) : NonSeekStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken);
        return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
    }
}
