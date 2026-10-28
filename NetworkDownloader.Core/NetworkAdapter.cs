using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace NetworkDownloader.Core;

public sealed record NetworkAdapter(string Id, string Name, IPAddress Address, int InterfaceIndex,
    string Kind)
{
    public string DisplayName => $"{Name} · {Address}";
    public override string ToString() => DisplayName;
}

public static class NetworkAdapters
{
    public static IReadOnlyList<NetworkAdapter> GetAvailable()
    {
        var result = new List<NetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) ||
                    !nic.Supports(NetworkInterfaceComponent.IPv4))
                    continue;

                var properties = nic.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 is null) continue;
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address.Address) || address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;

                    result.Add(new(nic.Id, nic.Name, address.Address, ipv4.Index,
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "以太网"));
                }
            }
            // An IPv6-only/virtual/broken adapter must not hide the other usable adapters.
            catch (NetworkInformationException) { }
            catch (SocketException) { }
        }
        return result.OrderBy(x => x.Name).ToArray();
    }
}

public static class BoundHttpClientFactory
{
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;

    [SupportedOSPlatform("windows")]
    public static HttpClient Create(NetworkAdapter adapter)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                // System DNS is intentionally separate from the bound data connection.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,
                    AddressFamily.InterNetwork, timeout.Token);
                Exception? lastError = null;
                foreach (var remote in addresses)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        // Windows IP_UNICAST_IF uses a network-byte-order IPv4 interface index.
                        socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface,
                            IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
                        socket.Bind(new IPEndPoint(adapter.Address, 0));
                        await socket.ConnectAsync(new IPEndPoint(remote, context.DnsEndPoint.Port), timeout.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        if (timeout.IsCancellationRequested) throw;
                        lastError = ex;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
                throw new HttpRequestException("所选网卡无法连接目标服务器（需要 IPv4 地址）。", lastError);
            }
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }
}
