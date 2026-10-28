using System.Runtime.Versioning;

namespace NetworkDownloader.Core;

[SupportedOSPlatform("windows")]
public sealed class DownloadTaskRunner : IDownloadTaskRunner
{
    public AdaptiveHttpController HttpController { get; private set; } = new();

    // Called once before scheduling; the controller is shared across all HTTP runs.
    internal void ConfigureHttpBudget(int maximum) => HttpController = new(new AdaptiveHttpOptions
    { GlobalLimit = Math.Min(maximum, Math.Clamp(Environment.ProcessorCount * 2, 4, 32)) });

    public async Task<DownloadResult> RunAsync(DownloadTaskRecord task, NetworkAdapter adapter,
        IProgress<DownloadProgress> progress, Func<BtMetadata, CancellationToken, Task<int[]?>> chooseFiles,
        CancellationToken cancellationToken)
    {
        if (task.IsBt)
            return new(await new BtWorkerClient().DownloadAsync(task.Input, task.SaveDirectory, adapter, progress, chooseFiles, cancellationToken));
        using var session = HttpController.Register(adapter.Id, new(task.Input.Value));
        using var client = BoundHttpClientFactory.Create(adapter, session);
        // Legacy stored Connections is retained for catalog compatibility, never used as a setting.
        await new DownloadService(client, session).DownloadAsync(new(task.Input.Value), task.OutputPath!, progress, cancellationToken);
        return new(task.OutputPath!);
    }
}
