namespace NetworkDownloader.Core;

public enum DownloadKind { Http, Magnet, TorrentFile }

public sealed record DownloadInput(DownloadKind Kind, string Value, string? InfoHash = null)
{
    public static DownloadInput Parse(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length is 0 or > 32768) throw new ArgumentException("下载地址为空或过长。");
        if (IsHex(text, 40)) return Magnet(text.ToLowerInvariant());
        if (text.Length == 32 && text.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(char.ToUpperInvariant(c))))
        {
            var bytes = new List<byte>();
            var bits = 0;
            var value = 0;
            foreach (var c in text.ToUpperInvariant())
            {
                value = (value << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);
                bits += 5;
                if (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); }
            }
            return Magnet(Convert.ToHexStringLower(bytes.ToArray()));
        }
        if (text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            string? hash = null;
            string? v2 = null;
            var parameters = text[8..].Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                var pair = parameter.Split('=', 2);
                if (!pair[0].Equals("xt", StringComparison.OrdinalIgnoreCase) || pair.Length != 2) continue;
                var xt = Uri.UnescapeDataString(pair[1]);
                if (xt.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                {
                    if (xt.Length is not (41 or 49)) throw new ArgumentException("磁力链接中的 BT v1 Hash 长度无效。");
                    var parsed = Parse(xt[9..]);
                    if (parsed.Kind != DownloadKind.Magnet || parsed.InfoHash is null || parsed.InfoHash.Length != 40)
                        throw new ArgumentException("磁力链接中的 BT v1 Hash 无效。");
                    if (hash is not null && hash != parsed.InfoHash) throw new ArgumentException("磁力链接包含冲突的 Hash。");
                    hash = parsed.InfoHash;
                    parameters[index] = "xt=urn:btih:" + hash;
                }
                else if (xt.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase) && IsHex(xt[13..], 64))
                {
                    var next = xt[13..].ToLowerInvariant();
                    if (v2 is not null && v2 != next) throw new ArgumentException("磁力链接包含冲突的 Hash。");
                    v2 = next;
                    parameters[index] = "xt=urn:btmh:1220" + v2;
                }
            }
            if (hash is null && v2 is null) throw new ArgumentException("磁力链接必须包含有效的 btih 或 btmh Hash。");
            return new(DownloadKind.Magnet, "magnet:?" + string.Join('&', parameters), hash ?? v2);
        }
        if (Path.IsPathFullyQualified(text) && Path.GetExtension(text).Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            return new(DownloadKind.TorrentFile, Path.GetFullPath(text));
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo))
            return new(DownloadKind.Http, uri.AbsoluteUri);
        throw new ArgumentException("请输入 HTTP/HTTPS 地址、磁力链接、40 位十六进制 / 32 位 Base32 Hash，或本地 .torrent 文件路径。");
    }

    private static DownloadInput Magnet(string hash) => new(DownloadKind.Magnet, "magnet:?xt=urn:btih:" + hash, hash);
    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
}
