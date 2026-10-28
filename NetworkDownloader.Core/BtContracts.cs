namespace NetworkDownloader.Core;

public sealed record BtFile(int Index, string Path, long Size, bool Padding = false);
public sealed record BtMetadata(string Hash, string Name, IReadOnlyList<BtFile> Files, int[]? SelectedFiles = null);
public sealed record BtStartRequest(string Input, string Directory, string AdapterId, string Address);
public sealed record BtCommand(string Type, int[]? Files = null);
public sealed record BtEvent(string Type, string? Message = null, BtMetadata? Metadata = null,
    DownloadProgress? Progress = null, string? OutputDirectory = null);

public static class BtPathPolicy
{
    public static void ValidateMetadata(BtMetadata metadata)
    {
        if (metadata.Hash.Length is not (40 or 64) || !metadata.Hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("种子 Hash 无效。");
        if (metadata.Files.Count is 0 or > 10000) throw new InvalidDataException("种子文件数量必须在 1–10000 之间。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new HashSet<int>();
        long total = 0;
        foreach (var file in metadata.Files)
        {
            if (file.Index < 0 || file.Index >= metadata.Files.Count || !indices.Add(file.Index)) throw new InvalidDataException("种子文件索引无效或重复。");
            ValidateRelativePath(file.Path);
            if (file.Size < 0 || file.Size > 8L * 1024 * 1024 * 1024 * 1024) throw new InvalidDataException("种子文件过大或长度无效（单文件上限 8 TiB）。");
            total = checked(total + file.Size);
            if (total > 16L * 1024 * 1024 * 1024 * 1024) throw new InvalidDataException("种子总大小超过 16 TiB 限制。");
            if (!paths.Add(file.Path.Replace('/', '\\'))) throw new InvalidDataException("种子包含重复文件路径。");
        }
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.Contains(parent)) throw new InvalidDataException("种子包含文件与目录路径冲突。");
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    public static void ValidateRelativePath(string path)
    {
        if (path.Length is 0 or > 220 || Path.IsPathRooted(path)) throw new InvalidDataException("种子文件路径过长或不是相对路径。");
        foreach (var part in path.Split(['/', '\\']))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new InvalidDataException("种子包含不安全的文件路径。");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "123456789¹²³".Contains(stem[3]))) throw new InvalidDataException("种子包含 Windows 保留文件名。");
        }
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("BT 保存路径不能包含符号链接或目录联接。");
    }

    public static void ValidateExistingTree(string root)
    {
        RejectReparsePoints(root);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            if (!System.IO.Directory.Exists(directory)) continue;
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("BT 目录中存在符号链接或目录联接。");
                if (item is DirectoryInfo) directories.Push(item.FullName);
            }
        }
    }
}
