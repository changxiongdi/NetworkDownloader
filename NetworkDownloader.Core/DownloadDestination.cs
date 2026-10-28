using System.Text.Json;

namespace NetworkDownloader.Core;

public static class DownloadDestination
{
    public static string GetFileName(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 下载地址。", nameof(url));

        // Split before decoding so an encoded slash can never turn into a directory.
        var segment = url.AbsolutePath[(url.AbsolutePath.LastIndexOf('/') + 1)..];
        var decoded = Uri.UnescapeDataString(segment);
        const string invalid = "<>:\"/\\|?*";
        var name = new string(decoded.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";

        var stem = name.Split('.')[0].TrimEnd();
        if (IsReservedName(stem)) name = "_" + name;

        // Leave room for a duplicate suffix and the resume sidecars.
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 20) extension = "";
            var limit = 180 - extension.Length;
            if (char.IsHighSurrogate(name[limit - 1])) limit--;
            name = name[..limit].TrimEnd('.', ' ') + extension;
        }
        return name;
    }

    public static string Resolve(Uri url, string directory, IReadOnlySet<string>? reservedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("请选择保存目录。", nameof(directory));
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory)) throw new ArgumentException("保存到必须是目录，不能是文件路径。", nameof(directory));
        var name = GetFileName(url);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 0; index < 10000; index++)
        {
            var filename = index == 0 ? name : $"{stem} ({index}){extension}";
            var candidate = Path.Combine(directory, filename);
            if (reservedPaths?.Contains(candidate) == true) continue;
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            var partial = candidate + ".part";
            var metadata = partial + ".json";
            if (Directory.Exists(partial) || Directory.Exists(metadata)) continue;
            if (!File.Exists(partial) && !File.Exists(metadata) && !Directory.Exists(partial + ".segments")) return candidate;

            // Reuse only this URL's temporary files; leave other downloads untouched.
            try
            {
                if (File.Exists(metadata) &&
                    JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(metadata))?.Url == url.AbsoluteUri)
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        throw new IOException("目录中同名文件过多，请选择其他保存目录。");
    }

    private static bool IsReservedName(string stem)
    {
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
}
