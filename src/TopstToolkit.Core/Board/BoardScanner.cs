using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TopstToolkit.Core.Board;

/// <summary>A device on the PC's network that answers on the SSH port.</summary>
/// <param name="Name">Its name in the network's DNS (the router's list), when it has one.</param>
/// <param name="Banner">What its SSH server says first, e.g. "SSH-2.0-OpenSSH_8.9".</param>
public sealed record BoardCandidate(string Address, string? Name, string Banner)
{
    /// <summary>The TOPST AI-G boards call themselves "ai-g-topst".</summary>
    public bool LooksLikeTopst => Name is { } n && n.Contains("topst", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// "보드 찾기" (design §5.10): knocks on the SSH port of every address in the PC's own networks (each a /24 around the
/// PC's address) and asks the network's DNS for the names. Nothing logs in: the key is only offered to the board the
/// user picks.
/// </summary>
public static class BoardScanner
{
    /// <summary>The addresses next to the PC's own on its real (not virtual) networks, the PC's own left out.</summary>
    public static IReadOnlyList<IPAddress> Neighbours()
    {
        var own = new HashSet<IPAddress>();
        var networks = new List<byte[]>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    continue;
                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue; // no DHCP answer: nothing to find there
                own.Add(unicast.Address);
                if (!networks.Any(n => n[0] == bytes[0] && n[1] == bytes[1] && n[2] == bytes[2]))
                    networks.Add(bytes);
            }
        }
        var result = new List<IPAddress>();
        foreach (var n in networks)
            for (var last = 1; last < 255; last++)
                if (new IPAddress([n[0], n[1], n[2], (byte)last]) is var address && !own.Contains(address))
                    result.Add(address);
        return result;
    }

    /// <summary>Every device that answers on <paramref name="port"/>, boards that look like TOPST first.</summary>
    public static async Task<IReadOnlyList<BoardCandidate>> ScanAsync(int port = 22, CancellationToken ct = default)
    {
        var addresses = Neighbours();
        var found = new List<BoardCandidate>();
        using var gate = new SemaphoreSlim(64);
        var tasks = addresses.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await ProbeAsync(address, port, ct).ConfigureAwait(false) is { } banner)
                {
                    var name = await NameOfAsync(address).ConfigureAwait(false);
                    lock (found)
                        found.Add(new BoardCandidate(address.ToString(), name, banner));
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.OrderByDescending(c => c.LooksLikeTopst).ThenBy(c => c.Name is null).ThenBy(c => Last(c.Address)).ToList();
    }

    /// <summary>The SSH greeting of a device, or null when nothing answers within a moment.</summary>
    public static async Task<string?> ProbeAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            await client.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            read.CancelAfter(TimeSpan.FromMilliseconds(800));
            var buffer = new byte[128];
            var n = await client.GetStream().ReadAsync(buffer, read.Token).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(buffer, 0, n).Split('\n')[0].Trim();
            return text.StartsWith("SSH-", StringComparison.Ordinal) ? text : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// The device's name from the network's DNS (a PTR record, which home routers keep for their DHCP clients). Only the
    /// PTR is asked for: Dns.GetHostEntry also looks the name up forwards, which takes seconds when that fails.
    /// </summary>
    private static async Task<string?> NameOfAsync(IPAddress address)
    {
        var lookup = Task.Run(() => OperatingSystem.IsWindows() ? PtrName(address) : null);
        if (await Task.WhenAny(lookup, Task.Delay(3000)).ConfigureAwait(false) != lookup)
            return null;
        var name = await lookup.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(name) ? null : name.TrimEnd('.').Split('.')[0];
    }

    private const ushort DnsTypePtr = 12;

    [System.Runtime.InteropServices.DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [System.Runtime.InteropServices.DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr list, int freeType);

    /// <summary>DnsQuery_W for "d.c.b.a.in-addr.arpa", PTR: the first answer's name, or null.</summary>
    private static string? PtrName(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
            return null;
        try
        {
            if (DnsQuery($"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa", DnsTypePtr, 0, IntPtr.Zero, out var list, IntPtr.Zero) != 0 || list == IntPtr.Zero)
                return null;
            try
            {
                // DNS_RECORDW: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, then Data (PTR: pNameHost)
                var size = IntPtr.Size;
                for (var record = list; record != IntPtr.Zero; record = System.Runtime.InteropServices.Marshal.ReadIntPtr(record))
                {
                    var type = (ushort)System.Runtime.InteropServices.Marshal.ReadInt16(record, 2 * size);
                    if (type != DnsTypePtr)
                        continue;
                    var host = System.Runtime.InteropServices.Marshal.ReadIntPtr(record, size == 8 ? 32 : 24);
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(host);
                }
                return null;
            }
            finally
            {
                DnsRecordListFree(list, 1);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static int Last(string address) => int.TryParse(address[(address.LastIndexOf('.') + 1)..], out var n) ? n : 0;
}
