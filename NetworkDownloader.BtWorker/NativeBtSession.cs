using System.Runtime.InteropServices;
using System.Text.Json;
using NetworkDownloader.Core;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace NetworkDownloader.BtWorker;

public sealed record BtSnapshot(BtMetadata? Metadata, long Downloaded, long Total, int Rate, int UploadRate,
    int Peers, int State, bool Finished, int Port, string Error, string Warning, bool Checking = false);

public sealed class NativeBtSession : IDisposable
{
    private nint handle;
    private BtMetadata? metadata;
    private nint Handle => handle != 0 ? handle : throw new ObjectDisposedException(nameof(NativeBtSession));
    public static string Version => Marshal.PtrToStringUTF8(Native.nd_version()) ?? "unknown";
    public static BtMetadata Inspect(string input) => Read<BtMetadata>(Native.nd_inspect(input));
    public static BtMetadata InspectResume(string path) => Read<BtMetadata>(Native.nd_inspect_resume(path));

    public NativeBtSession(string input, string directory, string address, string resumePath, bool discovery = true, bool utpOnly = false)
    {
        handle = Native.nd_open(input, directory, address, resumePath, (discovery ? 1 : 0) | (utpOnly ? 2 : 0));
        if (handle == 0) throw Error();
    }

    public BtSnapshot Poll()
    {
        var snapshot = Read<BtSnapshot>(Native.nd_poll(Handle));
        metadata ??= snapshot.Metadata;
        return snapshot with { Metadata = metadata };
    }
    public void Start(int[] files) => Check(Native.nd_start(Handle, files, files.Length));
    public void Save() => Check(Native.nd_save(Handle));
    public void Stop() => Check(Native.nd_stop(Handle));
    public void ConnectPeer(string address, int port, bool tcpOnly = false) => Check(Native.nd_connect_peer(Handle, address, port, tcpOnly ? 1 : 0));
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref handle, 0);
        if (owned != 0) Native.nd_close(owned);
    }
    private static T Read<T>(nint pointer)
    {
        if (pointer == 0) throw Error();
        return JsonSerializer.Deserialize<T>(Marshal.PtrToStringUTF8(pointer)!) ?? throw new IOException("BT 引擎返回了空数据。");
    }
    private static IOException Error() => new("BT 引擎：" + Marshal.PtrToStringUTF8(Native.nd_error()));
    private static void Check(int success) { if (success == 0) throw Error(); }

    private static class Native
    {
        private const string Library = "NetworkDownloader.Native";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_error();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_inspect([MarshalAs(UnmanagedType.LPUTF8Str)] string input);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_inspect_resume([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string address, [MarshalAs(UnmanagedType.LPUTF8Str)] string resumePath, int discovery);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint nd_poll(nint job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int nd_start(nint job, int[] files, int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int nd_save(nint job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int nd_stop(nint job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void nd_close(nint job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int nd_connect_peer(nint job,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string address, int port, int tcpOnly);
    }
}
