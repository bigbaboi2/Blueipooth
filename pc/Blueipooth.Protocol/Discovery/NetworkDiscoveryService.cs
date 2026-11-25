using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Blueipooth.Protocol.Discovery;

public sealed record LocalEndpointCandidate(
    string IpAddress,
    string InterfaceName,
    bool IsUsbInterface);

public sealed class NetworkDiscoveryService : IAsyncDisposable
{
    public const int DiscoveryUdpPort = 49549;
    private static readonly IPAddress MdnsMulticastIp = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly string _pcName;
    private readonly int _controlPort;
    private readonly int _mediaPort;
    private readonly string _certSha256Hex;
    private readonly string? _adbPath;

    private CancellationTokenSource? _cts;
    private Task? _beaconTask;
    private Task? _adbWatchTask;

    public bool IsUsbAdbTunnelActive { get; private set; }

    public NetworkDiscoveryService(
        string pcName,
        int controlPort,
        int mediaPort,
        string certSha256Hex,
        string? adbPath = null)
    {
        _pcName = pcName;
        _controlPort = controlPort;
        _mediaPort = mediaPort;
        _certSha256Hex = certSha256Hex;
        _adbPath = adbPath;
    }

    public static IReadOnlyList<LocalEndpointCandidate> GetLocalCandidates()
    {
        var results = new List<LocalEndpointCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            string desc = $"{nic.Name} {nic.Description}";
            if (desc.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WARP", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isUsb = desc.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("NCM", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("USB Ethernet", StringComparison.OrdinalIgnoreCase);

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    results.Add(new LocalEndpointCandidate(ip, nic.Name, isUsb));
                }
            }
        }

        // Put USB interfaces first for ~1ms ultra-low latency priority!
        return results
            .OrderByDescending(c => c.IsUsbInterface)
            .ToList();
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _beaconTask = Task.Run(() => RunDiscoveryLoopAsync(_cts.Token));
        _adbWatchTask = Task.Run(() => RunUsbAdbTunnelLoopAsync(_cts.Token));
    }

    private async Task RunDiscoveryLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPort));
        }
        catch
        {
            // Port busy; still send periodic beacons from ephemeral socket
        }

        // Also join mDNS multicast group best-effort for standard Zeroconf listeners
        using var mdnsUdp = new UdpClient();
        try
        {
            mdnsUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            mdnsUdp.JoinMulticastGroup(MdnsMulticastIp);
        }
        catch
        {
            // Ignore if OS mDNS service holds exclusive lock
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var candidates = GetLocalCandidates();
                var announcement = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    service = "_blueipooth._tcp.local.",
                    pcName = _pcName,
                    controlPort = _controlPort,
                    mediaPort = _mediaPort,
                    fp = _certSha256Hex,
                    ips = candidates.Select(c => c.IpAddress).ToArray(),
                    hasUsb = candidates.Any(c => c.IsUsbInterface) || IsUsbAdbTunnelActive
                });

                // 1. Reply immediately if Android sends a "BLUEIPOOTH_DISCOVER" probe
                while (udp.Available > 0)
                {
                    var probe = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    string text = Encoding.UTF8.GetString(probe.Buffer);
                    if (text.StartsWith("BLUEIPOOTH_DISCOVER", StringComparison.Ordinal))
                    {
                        await udp.SendAsync(announcement, probe.RemoteEndPoint, ct).ConfigureAwait(false);
                    }
                }

                // 2. Periodic subnet broadcast + mDNS multicast announcement
                await udp.SendAsync(announcement, new IPEndPoint(IPAddress.Broadcast, DiscoveryUdpPort), ct)
                    .ConfigureAwait(false);
                try
                {
                    byte[] mdnsPkt = BuildMinimalMdnsPtrTxtPacket(_pcName, _controlPort, _certSha256Hex);
                    await mdnsUdp.SendAsync(mdnsPkt, new IPEndPoint(MdnsMulticastIp, MdnsPort), ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Non-fatal
                }

                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunUsbAdbTunnelLoopAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_adbPath) || !File.Exists(_adbPath))
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, $"reverse tcp:{_controlPort} tcp:{_controlPort}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    IsUsbAdbTunnelActive = proc.ExitCode == 0;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                IsUsbAdbTunnelActive = false;
            }

            await Task.Delay(4000, ct).ConfigureAwait(false);
        }
    }

    private static byte[] BuildMinimalMdnsPtrTxtPacket(string pcName, int port, string fpHex)
    {
        // Standard DNS response header (QR=1, AA=1, ANCOUNT=1) advertising _blueipooth._tcp.local
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0); // ID
        bw.Write(IPAddress.HostToNetworkOrder((short)unchecked((short)0x8400))); // Flags: Response + Authoritative
        bw.Write((ushort)0); // QDCOUNT
        bw.Write(IPAddress.HostToNetworkOrder((short)1)); // ANCOUNT
        bw.Write((ushort)0); // NSCOUNT
        bw.Write((ushort)0); // ARCOUNT

        WriteDnsName(bw, "_blueipooth._tcp.local");
        bw.Write(IPAddress.HostToNetworkOrder((short)16)); // TXT record
        bw.Write(IPAddress.HostToNetworkOrder((short)1));  // IN
        bw.Write(IPAddress.HostToNetworkOrder(120));       // TTL 120s

        byte[] txt = Encoding.UTF8.GetBytes($"name={pcName};port={port};fp={fpHex[..Math.Min(16, fpHex.Length)]}");
        bw.Write(IPAddress.HostToNetworkOrder((short)(txt.Length + 1)));
        bw.Write((byte)txt.Length);
        bw.Write(txt);
        return ms.ToArray();
    }

    private static void WriteDnsName(BinaryWriter bw, string domain)
    {
        foreach (string label in domain.Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            bw.Write((byte)bytes.Length);
            bw.Write(bytes);
        }
        bw.Write((byte)0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_beaconTask is not null)
        {
            try { await _beaconTask.ConfigureAwait(false); } catch { }
        }
        if (_adbWatchTask is not null)
        {
            try { await _adbWatchTask.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _cts = null;
    }
}
