namespace NetworkDownloader.Core;

public enum DownloadTaskState
{
    Queued, Connecting, ResolvingMetadata, AwaitingFileSelection, Checking, Downloading,
    Pausing, Paused, Completed, Failed, NetworkUnavailable, Interrupted
}

public sealed record TaskOutputFile(string RelativePath, long Size);

// Immutable snapshots cross the UI/engine boundary. Only the coordinator changes task state.
public sealed record DownloadTaskRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Version { get; init; }
    public required DownloadInput Input { get; init; }
    public required string Name { get; init; }
    public required string SaveDirectory { get; init; }
    public required string AdapterId { get; init; }
    public required string AdapterName { get; init; }
    public required string Address { get; init; }
    public string? OutputPath { get; init; }
    public string? InfoHash { get; init; }
    public string? SourceIdentity { get; init; }
    // Legacy catalog field only; adaptive HTTP ignores its former manual setting.
    public int Connections { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public int ActiveHttpRequests { get; init; }
    public int[]? SelectedFiles { get; init; }
    public TaskOutputFile[] OutputFiles { get; init; } = [];
    public DownloadTaskState State { get; init; } = DownloadTaskState.Queued;
    public long Downloaded { get; init; }
    public long? Total { get; init; }
    public double BytesPerSecond { get; init; }
    // Null means the pre-upgrade duration is unknown, never inferred from CreatedAt.
    public long? ElapsedMilliseconds { get; init; }
    public bool ElapsedIsPartial { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double? EstimatedRemainingSeconds { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? EstimatedCompletionAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DownloadEstimateState EstimateState { get; init; } = DownloadEstimateState.Inactive;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinalizing { get; init; }
    public string Message { get; init; } = "等待调度";
    public bool FilesMissing { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public bool IsBt => Input.Kind != DownloadKind.Http;
    public bool IsRunning => State is DownloadTaskState.Connecting or DownloadTaskState.ResolvingMetadata
        or DownloadTaskState.AwaitingFileSelection or DownloadTaskState.Checking or DownloadTaskState.Downloading or DownloadTaskState.Pausing;
    public bool CanResume => State is DownloadTaskState.Paused or DownloadTaskState.Interrupted
        or DownloadTaskState.Failed or DownloadTaskState.NetworkUnavailable;
}

public sealed record DownloadResult(string OutputPath);

public interface IDownloadTaskRunner
{
    Task<DownloadResult> RunAsync(DownloadTaskRecord task, NetworkAdapter adapter,
        IProgress<DownloadProgress> progress,
        Func<BtMetadata, CancellationToken, Task<int[]?>> chooseFiles, CancellationToken cancellationToken);
}

public sealed record DownloadLimits(int MaxTasks = 3, int MaxBtTasks = 1, int MaxTasksPerAdapter = 2, int MaxHttpConnections = 16)
{
    public void Validate()
    {
        if (MaxTasks is < 1 or > 32 || MaxBtTasks < 1 || MaxBtTasks > MaxTasks ||
            MaxTasksPerAdapter < 1 || MaxTasksPerAdapter > MaxTasks || MaxHttpConnections is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(DownloadLimits));
    }
}
