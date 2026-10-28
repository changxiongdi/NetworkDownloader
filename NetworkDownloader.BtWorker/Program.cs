using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using NetworkDownloader.Core;

namespace NetworkDownloader.BtWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !args[0].StartsWith("NetworkDownloader-", StringComparison.Ordinal)) return 2;
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await pipe.ConnectAsync(connectTimeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return 3; }
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        async Task Send(BtEvent update) => await writer.WriteLineAsync(JsonSerializer.Serialize(update));
        try
        {
            using var initialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var line = await reader.ReadLineAsync(initialTimeout.Token) ?? throw new IOException("未收到下载参数。");
            if (line.Length > 65536) throw new IOException("下载参数过长。");
            var request = JsonSerializer.Deserialize<BtStartRequest>(line) ?? throw new IOException("下载参数无效。");
            var input = DownloadInput.Parse(request.Input);
            if (input.Kind == DownloadKind.Http) throw new IOException("BT 工作进程不接受 HTTP 任务。");
            if (!AdapterAvailable(request)) throw new IOException("所选网卡或 IPv4 地址已经失效，请刷新网卡。");
            var metadata = NativeBtSession.Inspect(input.Value);
            if (metadata.Files.Count > 0) BtPathPolicy.ValidateMetadata(metadata);
            using var storage = new BtTaskStorage(request.Directory, metadata.Hash);
            if (File.Exists(storage.ResumePath))
            {
                var resumed = NativeBtSession.InspectResume(storage.ResumePath);
                // Native open compares the complete v1/v2 hash set before accepting resume data.
                if (resumed.Files.Count > 0) BtPathPolicy.ValidateMetadata(resumed);
            }
            using var engine = new NativeBtSession(input.Value, storage.OutputDirectory, request.Address, storage.ResumePath);
            var clock = Stopwatch.StartNew();
            var lastSave = TimeSpan.Zero;
            var lastAdapterCheck = TimeSpan.Zero;
            var notified = false;
            var started = false;
            var readCommand = reader.ReadLineAsync();
            try
            {
                while (true)
                {
                    if (readCommand.IsCompleted)
                    {
                        var commandLine = await readCommand;
                        if (commandLine is null) { engine.Stop(); return 0; }
                        if (commandLine.Length > 100000) throw new IOException("BT 命令过长。");
                        var command = JsonSerializer.Deserialize<BtCommand>(commandLine) ?? throw new IOException("无效命令。");
                        if (command.Type == "cancel")
                        {
                            engine.Stop();
                            await Send(new("paused"));
                            return 0;
                        }
                        if (command.Type != "select" || started || !notified || metadata.Files.Count == 0)
                            throw new IOException("当前状态不接受此命令。");
                        var files = command.Files?.Distinct().ToArray() ?? [];
                        if (files.Length == 0 || files.Any(i => !metadata.Files.Any(f => f.Index == i && !f.Padding)))
                            throw new IOException("请选择有效的下载文件。");
                        BtPathPolicy.ValidateExistingTree(storage.OutputDirectory);
                        var selectedBytes = metadata.Files.Where(f => files.Contains(f.Index)).Sum(f => f.Size);
                        var drive = new DriveInfo(Path.GetPathRoot(storage.OutputDirectory)!);
                        var existingBytes = Directory.EnumerateFiles(storage.OutputDirectory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                        if (drive.AvailableFreeSpace < Math.Max(0, selectedBytes - existingBytes) + 64L * 1024 * 1024)
                            throw new IOException("磁盘剩余空间不足。");
                        storage.SaveSelection(files);
                        engine.Start(files);
                        started = true;
                        readCommand = reader.ReadLineAsync();
                    }
                    if (clock.Elapsed - lastAdapterCheck >= TimeSpan.FromSeconds(1))
                    {
                        if (!AdapterAvailable(request)) throw new IOException("所选网卡已断开或 IP 已变化，BT 下载已停止。请刷新网卡后继续。");
                        lastAdapterCheck = clock.Elapsed;
                    }
                    var status = engine.Poll();
                    if (!string.IsNullOrEmpty(status.Error)) throw new IOException("BT 下载失败：" + status.Error);
                    if (status.Metadata is not null && !notified)
                    {
                        metadata = status.Metadata with { SelectedFiles = storage.ReadSelection() };
                        BtPathPolicy.ValidateMetadata(metadata);
                        notified = true;
                        await Send(new("metadata", Metadata: metadata, OutputDirectory: storage.OutputDirectory));
                    }
                    if (started && status.Finished)
                    {
                        engine.Stop();
                        await Send(new("complete", OutputDirectory: storage.OutputDirectory));
                        return 0;
                    }
                    if (clock.Elapsed - lastSave >= TimeSpan.FromSeconds(30)) { engine.Save(); lastSave = clock.Elapsed; }
                    // Do not fill the pipe while the parent is showing the modal file picker.
                    if (!notified || started)
                    {
                        var state = !started ? "正在搜索节点并获取种子元数据" : status.Checking ? "正在恢复并校验本地分片（不是重新下载）" : "正在下载";
                        var message = $"{state} · 节点 {status.Peers} · 上传 {status.UploadRate / 1024.0:F0} KB/s";
                        if (!started && clock.Elapsed > TimeSpan.FromSeconds(60)) message += "；暂无元数据，资源可能无在线节点";
                        await Send(new("progress", Progress: new(status.Downloaded, started ? status.Total : null, status.Rate, message, status.Checking), OutputDirectory: storage.OutputDirectory));
                    }
                    await Task.Delay(300);
                }
            }
            catch
            {
                // Closing the session always stops networking. Save best-effort before reporting the original failure.
                try { engine.Stop(); } catch (IOException) { }
                throw;
            }
        }
        catch (Exception ex)
        {
            try { await Send(new("error", Message: ex is DllNotFoundException ? "缺少原生 BT 引擎，请使用完整发布包。" : ex.Message)); }
            catch (IOException) { }
            return 1;
        }
    }

    private static bool AdapterAvailable(BtStartRequest request) =>
        IPAddress.TryParse(request.Address, out var address) && NetworkAdapters.GetAvailable().Any(a => a.Id == request.AdapterId && a.Address.Equals(address));
}
