using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Blueipooth.Protocol.Control;
using Blueipooth.Protocol.Crypto;
using Blueipooth.Protocol.Discovery;
using Blueipooth.Protocol.Pairing;

namespace Blueipooth.Protocol.Transport;

public sealed record ConnectedPhoneInfo(
    string PeerId,
    string DeviceName,
    string RemoteIp,
    bool IsUsbLink,
    double RttMs);

public sealed class BlueipoothLanServer : IAsyncDisposable
{
    public int ControlPort { get; private set; }
    public int MediaUdpPort { get; private set; }

    private readonly X509Certificate2 _serverCert;
    private readonly string _certSha256Hex;
    private byte[] _activeMediaKey16;
    private string _activeQrToken;
    private MediaFrameCipher _mediaCipher;

    private TcpListener? _controlListener;
    private Socket? _udpSocket;
    private CancellationTokenSource? _cts;
    private Stream? _activeControlStream;
    private TcpClient? _activeControlClient;
    private volatile bool _connectionsPaused;
    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    private IPEndPoint? _peerMediaEndPoint;
    private uint _txSequence;

    public ConnectedPhoneInfo? CurrentPhone { get; private set; }
    public bool ConnectionsPaused => _connectionsPaused;
    public double LatestRttMs { get; private set; }

    public event Action<ConnectedPhoneInfo?>? PhoneConnectionChanged;
    public event Action<ConnectedPhoneInfo>? PhoneRttUpdated;
    public event Action<BleAdvReportMessage>? BleAdvertisementReceived;
    public event Action<HeadsetStateMessage>? HeadsetStateReceived;
    public event Action<MediaControlMessage>? MediaControlReceived;
    public event Action<DeviceStatusMessage>? DeviceStatusReceived;
    public event Action<ReadOnlyMemory<byte>, MediaPacketHeader>? PhoneMicOpusFrameReceived;

    public void SetConnectionsPaused(bool paused)
    {
        _connectionsPaused = paused;
        if (paused)
        {
            try { _activeControlClient?.Close(); } catch { }
        }
    }

    public BlueipoothLanServer(int controlPort = 49550, int mediaUdpPort = 49551)
    {
        ControlPort = controlPort;
        MediaUdpPort = mediaUdpPort;
        (_serverCert, _certSha256Hex) = IdentityCertificateStore.LoadOrCreateServerCertificate();
        _activeMediaKey16 = RandomNumberGenerator.GetBytes(16);
        _activeQrToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _mediaCipher = new MediaFrameCipher(_activeMediaKey16);
    }

    public QrPairingPayload GenerateCurrentQrPayload(string? pcName = null)
    {
        var candidates = NetworkDiscoveryService.GetLocalCandidates();
        var ips = candidates.Select(c => c.IpAddress).ToList();
        if (ips.Count == 0) ips.Add("127.0.0.1");

        return new QrPairingPayload(
            Version: 1,
            PcName: pcName ?? Environment.MachineName,
            CandidateIps: ips,
            ControlPort: ControlPort,
            MediaUdpPort: MediaUdpPort,
            CertSha256Hex: _certSha256Hex,
            SessionKeyAes128: _activeMediaKey16,
            OneTimeToken: _activeQrToken);
    }

    public string CertSha256Hex => _certSha256Hex;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();

        _controlListener = new TcpListener(IPAddress.Any, ControlPort);
        _controlListener.Start();
        ControlPort = ((IPEndPoint)_controlListener.LocalEndpoint).Port;

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.Bind(new IPEndPoint(IPAddress.Any, MediaUdpPort));
        // Set DSCP EF (0xB8) TOS byte best-effort for low-latency Wi-Fi WMM Voice/Video queue!
        try { _udpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }
        MediaUdpPort = ((IPEndPoint)_udpSocket.LocalEndPoint!).Port;

        _ = Task.Run(() => AcceptControlClientsLoopAsync(_cts.Token));
        _ = Task.Run(() => ReceiveMediaUdpLoopAsync(_cts.Token));
    }

    public void SendPcStereoOpusFrame(ReadOnlySpan<byte> opusFrame, bool gamingDirect = true)
    {
        var socket = _udpSocket;
        var remote = _peerMediaEndPoint;
        if (socket is null || remote is null || CurrentPhone is null)
            return;

        uint seq = Interlocked.Increment(ref _txSequence);
        ulong tsUs = (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
        var hdr = new MediaPacketHeader(
            MediaPacketHeader.MagicValue,
            MediaStreamId.PcStereoOpus,
            gamingDirect ? MediaPacketFlags.GamingDirect : MediaPacketFlags.None,
            seq,
            tsUs);

        Span<byte> packet = stackalloc byte[MediaPacketHeader.OverheadBytes + opusFrame.Length];
        int total = _mediaCipher.EncryptFrame(hdr, opusFrame, packet);
        try
        {
            socket.SendTo(packet[..total], SocketFlags.None, remote);
        }
        catch
        {
            // Ignore transient UDP socket congestion
        }
    }

    public async Task SendControlMessageAsync<T>(
        ControlMessageType type,
        T message,
        CancellationToken ct = default)
    {
        var stream = _activeControlStream;
        if (stream is null) return;

        await _controlWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ControlFrameCodec.WriteFrameAsync(stream, type, message, ct).ConfigureAwait(false);
        }
        catch
        {
            // Disconnected
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    private async Task AcceptControlClientsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _controlListener is not null)
        {
            try
            {
                TcpClient client = await _controlListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandlePhoneSessionAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandlePhoneSessionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            try
            {
                if (_connectionsPaused) return;
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                }, ct).ConfigureAwait(false);

                var firstFrame = await ControlFrameCodec.ReadFrameAsync(ssl, ct).ConfigureAwait(false);
                if (firstFrame is null || firstFrame.Value.Type != ControlMessageType.PairHello)
                    return;
                if (_connectionsPaused) return;

                var hello = ControlFrameCodec.Deserialize<PairHelloMessage>(firstFrame.Value.JsonPayload);
                byte[] sessionMediaKey;
                string reconnectToken;

                if (!hello.IsReconnect)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.ASCII.GetBytes(hello.Token),
                            System.Text.Encoding.ASCII.GetBytes(_activeQrToken)))
                    {
                        return;
                    }
                    sessionMediaKey = _activeMediaKey16;
                    reconnectToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                    IdentityCertificateStore.SaveTrustedPeer(new TrustedPeerRecord(
                        hello.PeerId,
                        hello.DeviceName,
                        reconnectToken,
                        Convert.ToBase64String(sessionMediaKey),
                        DateTimeOffset.UtcNow));

                    // Rotate QR one-time token for future new pairings
                    _activeQrToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                }
                else
                {
                    var trusted = IdentityCertificateStore.LoadTrustedPeers()
                        .FirstOrDefault(p => string.Equals(p.PeerId, hello.PeerId, StringComparison.OrdinalIgnoreCase));
                    if (trusted is null ||
                        !CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.ASCII.GetBytes(hello.Token),
                            System.Text.Encoding.ASCII.GetBytes(trusted.ReconnectToken)))
                    {
                        return;
                    }

                    // Fresh session media key on reconnect
                    sessionMediaKey = RandomNumberGenerator.GetBytes(16);
                    reconnectToken = trusted.ReconnectToken;
                    _activeMediaKey16 = sessionMediaKey;
                    _mediaCipher.Dispose();
                    _mediaCipher = new MediaFrameCipher(_activeMediaKey16);
                }

                var remoteTcpEp = (IPEndPoint)client.Client.RemoteEndPoint!;
                if (_connectionsPaused) return;
                _peerMediaEndPoint = new IPEndPoint(remoteTcpEp.Address, hello.UdpPort > 0 ? hello.UdpPort : MediaUdpPort);
                _activeControlStream = ssl;
                _activeControlClient = client;

                bool isUsb = IPAddress.IsLoopback(remoteTcpEp.Address) ||
                             NetworkDiscoveryService.GetLocalCandidates()
                                 .Any(c => c.IsUsbInterface && remoteTcpEp.Address.ToString().StartsWith(
                                     string.Join('.', c.IpAddress.Split('.')[..3]), StringComparison.Ordinal));

                await SendControlMessageAsync(ControlMessageType.PairAccept, new PairAcceptMessage(
                    Environment.MachineName,
                    reconnectToken,
                    Convert.ToBase64String(sessionMediaKey),
                    MediaUdpPort), ct).ConfigureAwait(false);

                CurrentPhone = new ConnectedPhoneInfo(hello.PeerId, hello.DeviceName, remoteTcpEp.Address.ToString(), isUsb, 0);
                PhoneConnectionChanged?.Invoke(CurrentPhone);

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(() => RunPingLoopAsync(pingCts.Token), pingCts.Token);

                while (!ct.IsCancellationRequested)
                {
                    var frame = await ControlFrameCodec.ReadFrameAsync(ssl, ct).ConfigureAwait(false);
                    if (frame is null) break;
                    HandleControlFrame(frame.Value.Type, frame.Value.JsonPayload);
                }

                await pingCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Connection closed
            }
            finally
            {
                if (ReferenceEquals(_activeControlStream, ssl))
                {
                    _activeControlStream = null;
                    _activeControlClient = null;
                    _peerMediaEndPoint = null;
                    CurrentPhone = null;
                    PhoneConnectionChanged?.Invoke(null);
                }
            }
        }
    }

    private void HandleControlFrame(ControlMessageType type, string json)
    {
        switch (type)
        {
            case ControlMessageType.Ping:
            {
                var ping = ControlFrameCodec.Deserialize<PingMessage>(json);
                long nowUs = (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
                _ = SendControlMessageAsync(ControlMessageType.Pong, new PongMessage(ping.ClientTimestampUs, nowUs));
                break;
            }
            case ControlMessageType.Pong:
            {
                var pong = ControlFrameCodec.Deserialize<PongMessage>(json);
                long nowUs = (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
                double rttMs = Math.Max(0.1, (nowUs - pong.ClientTimestampUs) / 1000.0);
                LatestRttMs = rttMs;
                if (CurrentPhone is not null)
                {
                    CurrentPhone = CurrentPhone with { RttMs = Math.Round(rttMs, 1) };
                    PhoneRttUpdated?.Invoke(CurrentPhone);
                }
                break;
            }
            case ControlMessageType.BleAdvReport:
                BleAdvertisementReceived?.Invoke(ControlFrameCodec.Deserialize<BleAdvReportMessage>(json));
                break;
            case ControlMessageType.HeadsetState:
                HeadsetStateReceived?.Invoke(ControlFrameCodec.Deserialize<HeadsetStateMessage>(json));
                break;
            case ControlMessageType.MediaControl:
                MediaControlReceived?.Invoke(ControlFrameCodec.Deserialize<MediaControlMessage>(json));
                break;
            case ControlMessageType.DeviceStatus:
                DeviceStatusReceived?.Invoke(ControlFrameCodec.Deserialize<DeviceStatusMessage>(json));
                break;
        }
    }

    private async Task RunPingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long nowUs = (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
            await SendControlMessageAsync(ControlMessageType.Ping, new PingMessage(nowUs), ct).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveMediaUdpLoopAsync(CancellationToken ct)
    {
        byte[] recvBuf = new byte[2048];
        byte[] plainBuf = new byte[2048];
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested && _udpSocket is not null)
        {
            try
            {
                var res = await _udpSocket.ReceiveFromAsync(recvBuf, SocketFlags.None, anyEp, ct).ConfigureAwait(false);
                if (res.ReceivedBytes < MediaPacketHeader.OverheadBytes) continue;

                if (_mediaCipher.TryDecryptFrame(
                        recvBuf.AsSpan(0, res.ReceivedBytes),
                        plainBuf,
                        out var header,
                        out int plainLen))
                {
                    // Update NAT/ephemeral UDP port dynamically upon authenticated packet
                    if (res.RemoteEndPoint is IPEndPoint remoteIpEp)
                    {
                        _peerMediaEndPoint = remoteIpEp;
                    }

                    if (header.StreamId == MediaStreamId.PhoneMicOpus)
                    {
                        byte[] copy = plainBuf.AsSpan(0, plainLen).ToArray();
                        PhoneMicOpusFrameReceived?.Invoke(copy, header);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        _controlListener?.Stop();
        _udpSocket?.Dispose();
        _mediaCipher.Dispose();
        _serverCert.Dispose();
        _controlWriteLock.Dispose();
    }
}
