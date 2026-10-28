using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NetworkDownloader.Core;

public sealed class BtWorkerClient
{
    public async Task<string> DownloadAsync(DownloadInput input, string directory, NetworkAdapter adapter,
        IProgress<DownloadProgress> progress,
        Func<BtMetadata, CancellationToken, Task<int[]?>> chooseFiles, CancellationToken cancellationToken)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "bt-worker", "NetworkDownloader.BtWorker.exe");
        if (!File.Exists(worker) || !File.Exists(Path.Combine(Path.GetDirectoryName(worker)!, "NetworkDownloader.Native.dll")))
            throw new IOException("BT 引擎未安装。请使用包含 bt-worker 文件夹的完整发布包，或先运行 scripts/build.ps1。");
        var pipeName = "NetworkDownloader-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
        start.ArgumentList.Add(pipeName);
        // The random pipe name is the only command-line argument. Links and tracker credentials stay off the command line.
        using var process = new Process { StartInfo = start };
        using var commands = new SemaphoreSlim(1, 1);
        StreamWriter? writer = null;
        StreamReader? reader = null;
        using var lifetime = new CancellationTokenSource();
        Task? cancelTask = null;
        var started = false;
        async Task SendAsync(BtCommand command)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await commands.WaitAsync(sendTimeout.Token);
            try { if (writer is not null && pipe.IsConnected) await writer.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), sendTimeout.Token); }
            finally { commands.Release(); }
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("无法启动 BT 工作进程。");
            started = true;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await pipe.WaitForConnectionAsync(connectTimeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new IOException("BT 工作进程启动超时，请检查发布包和 .NET 10 Runtime。"); }
            }
            reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BtStartRequest(input.Value, Path.GetFullPath(directory), adapter.Id, adapter.Address.ToString())));
            cancelTask = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                try { await Task.Delay(Timeout.Infinite, linked.Token); }
                catch (OperationCanceledException) { }
                if (!cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested) return;
                try { await SendAsync(new("cancel")); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                // The child owns its files. Give it time to flush; a hung child is then stopped by the finally block.
                try { await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token); }
                catch (OperationCanceledException) { return; }
                pipe.Dispose();
            });
            var lastEvent = DateTime.UtcNow;
            while (true)
            {
                string? line;
                try
                {
                    using var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    line = await reader.ReadLineAsync(idleTimeout.Token);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is IOException or ObjectDisposedException)
                { throw new OperationCanceledException(cancellationToken); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new IOException($"BT 工作进程超过 60 秒未响应（最后响应 {lastEvent:T}）。"); }
                if (line is null) { cancellationToken.ThrowIfCancellationRequested(); throw new IOException("BT 工作进程意外退出；已保留下载数据，再次开始会校验恢复。"); }
                if (line.Length > 8 * 1024 * 1024) throw new IOException("BT 工作进程返回的数据过大。");
                lastEvent = DateTime.UtcNow;
                var update = JsonSerializer.Deserialize<BtEvent>(line) ?? throw new IOException("BT 工作进程返回空消息。");
                switch (update.Type)
                {
                    case "progress":
                        if (update.Progress is not null) progress.Report(update.Progress);
                        break;
                    case "metadata":
                        var metadata = update.Metadata ?? throw new IOException("种子元数据缺失。");
                        BtPathPolicy.ValidateMetadata(metadata);
                        int[]? files;
                        try { files = await chooseFiles(metadata, cancellationToken); }
                        catch (OperationCanceledException) { await SendAsync(new("cancel")); throw; }
                        await SendAsync(files is { Length: > 0 } && !cancellationToken.IsCancellationRequested ? new("select", files) : new("cancel"));
                        break;
                    case "complete": return update.OutputDirectory ?? directory;
                    case "paused": throw new OperationCanceledException("BT 下载已暂停。", cancellationToken);
                    case "error": throw new IOException(update.Message ?? "BT 下载失败。");
                    default: throw new IOException("未知的 BT 工作进程消息。");
                }
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is IOException or ObjectDisposedException)
        { throw new OperationCanceledException("BT 下载已暂停。", ex, cancellationToken); }
        finally
        {
            lifetime.Cancel();
            if (cancelTask is not null) await cancelTask;
            if (started && !process.HasExited)
            {
                try { await SendAsync(new("cancel")); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
                pipe.Dispose();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(exitTimeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            // The cancellation sender must finish before disposing the shared writer.
            // A timeout may already have closed the underlying pipe; flushing must not mask the original result.
            try { writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            reader?.Dispose();
        }
    }
}
