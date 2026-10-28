using System.Text.Json;
using NetworkDownloader.Core;

namespace NetworkDownloader.BtWorker;

public sealed class BtTaskStorage : IDisposable
{
    private readonly FileStream lease;
    public string OutputDirectory { get; }
    public string StateDirectory { get; }
    public string ResumePath => Path.Combine(StateDirectory, "resume.dat");
    public string SelectionPath => Path.Combine(StateDirectory, "selection.json");

    public BtTaskStorage(string directory, string hash)
    {
        if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("无效的任务 Hash。");
        directory = Path.GetFullPath(directory);
        BtPathPolicy.RejectReparsePoints(directory);
        var states = Path.Combine(directory, ".networkdownloader-bt");
        BtPathPolicy.RejectReparsePoints(states);
        Directory.CreateDirectory(states);
        StateDirectory = Path.Combine(states, hash);
        BtPathPolicy.RejectReparsePoints(StateDirectory);
        Directory.CreateDirectory(StateDirectory);
        var lockPath = Path.Combine(StateDirectory, "task.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("任务锁不能是链接。");
        lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var ownerPath = Path.Combine(StateDirectory, "owner.json");
            BtPathPolicy.ValidateExistingTree(StateDirectory);
            OutputDirectory = Path.Combine(directory, "BT-" + hash);
            if (File.Exists(ownerPath))
            {
                var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(ownerPath));
                if (owner?.Hash != hash || !string.Equals(owner.OutputDirectory, OutputDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("BT 任务目录标识不匹配。");
            }
            else
            {
                if (Directory.Exists(OutputDirectory) || File.Exists(OutputDirectory)) throw new IOException("BT 输出目录已存在但不属于此任务，请选择其他保存目录。");
                AtomicWrite(ownerPath, new Owner(hash, OutputDirectory));
            }
            BtPathPolicy.ValidateExistingTree(OutputDirectory);
            Directory.CreateDirectory(OutputDirectory);
        }
        catch { lease.Dispose(); throw; }
    }

    public void SaveSelection(int[] files) => AtomicWrite(SelectionPath, files);
    public int[]? ReadSelection() => File.Exists(SelectionPath) ? JsonSerializer.Deserialize<int[]>(File.ReadAllText(SelectionPath)) : null;
    public void Dispose() => lease.Dispose();
    private static void AtomicWrite<T>(string path, T value)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(file, value); file.Flush(flushToDisk: true); }
        File.Move(path + ".tmp", path, overwrite: true);
    }
    private sealed record Owner(string Hash, string OutputDirectory);
}
