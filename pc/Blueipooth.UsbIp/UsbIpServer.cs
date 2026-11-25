using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Blueipooth.UsbIp;

/// <summary>
/// Production-grade USB/IP v1.1.1 (0x0111) Server exposing a Virtual USB Bluetooth 5.2 Adapter
/// on 127.0.0.1:3240 for Windows 10/11 usbip-win2 + inbox BthUsb.sys.
/// Bridges USB URBs <-> Bluetooth HCI H4 stream (127.0.0.1:49552).
/// </summary>
public sealed class UsbIpServer : IAsyncDisposable
{
    public const ushort UsbIpVersion = 0x0111;
    public const ushort OpReqDevlist = 0x8005;
    public const ushort OpRepDevlist = 0x0005;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;

    public const uint UsbIpCmdSubmit = 0x00000001;
    public const uint UsbIpCmdUnlink = 0x00000002;
    public const uint UsbIpRetSubmit = 0x00000003;
    public const uint UsbIpRetUnlink = 0x00000004;

    private readonly string _bindIp;
    private readonly string _h4Host;
    private int _h4Port;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private long _totalHciCommands;
    private long _totalHciEvents;

    public int ListenPort { get; private set; }
    public bool IsDriverAttached { get; private set; }
    public long TotalHciCommands => Interlocked.Read(ref _totalHciCommands);
    public long TotalHciEvents => Interlocked.Read(ref _totalHciEvents);

    public event Action<bool>? DriverAttachmentChanged;

    private sealed record PendingInUrb(
        uint SeqNum,
        uint Ep,
        int MaxLength);

    public UsbIpServer(int listenPort = 3240, string h4Host = "127.0.0.1", int h4Port = 49552, string bindIp = "127.0.0.1")
    {
        ListenPort = listenPort;
        _h4Host = h4Host;
        _h4Port = h4Port;
        _bindIp = bindIp;
    }

    public void UpdateH4Port(int h4Port) => _h4Port = h4Port;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(_bindIp), ListenPort);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient usbClient, CancellationToken ct)
    {
        using (usbClient)
        await using (NetworkStream stream = usbClient.GetStream())
        {
            byte[] opHdr = new byte[8];
            if (await ReadExactAsync(stream, opHdr, ct).ConfigureAwait(false) < 8)
                return;

            ushort version = BinaryPrimitives.ReadUInt16BigEndian(opHdr.AsSpan(0, 2));
            ushort opCode = BinaryPrimitives.ReadUInt16BigEndian(opHdr.AsSpan(2, 2));
            _ = version;

            if (opCode == OpReqDevlist)
            {
                byte[] reply = BuildDevlistReply();
                await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }

            if (opCode == OpReqImport)
            {
                byte[] busIdBytes = new byte[32];
                if (await ReadExactAsync(stream, busIdBytes, ct).ConfigureAwait(false) < 32)
                    return;

                string requestedBusId = ReadNullTerminatedAscii(busIdBytes);
                bool match = string.Equals(requestedBusId, BluetoothUsbDescriptors.BusId, StringComparison.Ordinal);
                byte[] importReply = BuildImportReply(match);
                await stream.WriteAsync(importReply, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                if (!match) return;

                await RunImportedUrbSessionAsync(stream, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunImportedUrbSessionAsync(NetworkStream usbStream, CancellationToken ct)
    {
        using var h4Client = new TcpClient { NoDelay = true };
        await h4Client.ConnectAsync(_h4Host, _h4Port, ct).ConfigureAwait(false);
        await using NetworkStream h4Stream = h4Client.GetStream();

        var writeLock = new SemaphoreSlim(1, 1);
        var pendingEventUrbs = new ConcurrentQueue<PendingInUrb>();
        var pendingAclInUrbs = new ConcurrentQueue<PendingInUrb>();
        var unlinkedSeqs = new ConcurrentDictionary<uint, byte>();
        var eventBacklog = new ConcurrentQueue<byte[]>();
        var aclBacklog = new ConcurrentQueue<byte[]>();

        IsDriverAttached = true;
        DriverAttachmentChanged?.Invoke(true);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task SendRetSubmitAsync(uint seqNum, int status, ReadOnlyMemory<byte> inData, int isoPackets = 0)
        {
            byte[] pkt = new byte[48 + inData.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(0, 4), UsbIpRetSubmit);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4, 4), seqNum);
            // devid (8..12), direction (12..16), ep (16..20) = 0 per Linux stub_tx spec
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(20, 4), status);
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(24, 4), inData.Length);
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(28, 4), 0); // start_frame
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(32, 4), isoPackets);
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(36, 4), 0); // error_count
            if (!inData.IsEmpty)
            {
                inData.Span.CopyTo(pkt.AsSpan(48));
            }

            await writeLock.WaitAsync(sessionCts.Token).ConfigureAwait(false);
            try
            {
                await usbStream.WriteAsync(pkt, sessionCts.Token).ConfigureAwait(false);
                await usbStream.FlushAsync(sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        async Task FlushMatchesAsync(ConcurrentQueue<PendingInUrb> urbQueue, ConcurrentQueue<byte[]> packetQueue)
        {
            while (!urbQueue.IsEmpty && !packetQueue.IsEmpty)
            {
                if (!urbQueue.TryDequeue(out var urb))
                    break;

                if (unlinkedSeqs.TryRemove(urb.SeqNum, out _))
                    continue;

                if (!packetQueue.TryDequeue(out var payload))
                {
                    // Re-queue URB at front not needed since single consumer; push back cleanly
                    urbQueue.Enqueue(urb);
                    break;
                }

                int sendLen = Math.Min(payload.Length, urb.MaxLength);
                await SendRetSubmitAsync(urb.SeqNum, 0, payload.AsMemory(0, sendLen)).ConfigureAwait(false);
            }
        }

        // Pump H4 packets from Bumble -> USB IN Endpoints (EP1 Interrupt Events, EP2 Bulk ACL)
        Task h4ReadTask = Task.Run(async () =>
        {
            try
            {
                byte[] hdrBuf = new byte[5];
                while (!sessionCts.Token.IsCancellationRequested)
                {
                    int b0 = await ReadExactAsync(h4Stream, hdrBuf.AsMemory(0, 1), sessionCts.Token).ConfigureAwait(false);
                    if (b0 == 0) break;

                    byte pktType = hdrBuf[0];
                    if (pktType == 0x04) // HCI Event: [0x04][EventCode 1B][ParamLen 1B][Params...]
                    {
                        if (await ReadExactAsync(h4Stream, hdrBuf.AsMemory(1, 2), sessionCts.Token).ConfigureAwait(false) < 2)
                            break;
                        int paramLen = hdrBuf[2];
                        byte[] usbEvent = new byte[2 + paramLen];
                        usbEvent[0] = hdrBuf[1];
                        usbEvent[1] = hdrBuf[2];
                        if (paramLen > 0 &&
                            await ReadExactAsync(h4Stream, usbEvent.AsMemory(2, paramLen), sessionCts.Token).ConfigureAwait(false) < paramLen)
                        {
                            break;
                        }

                        Interlocked.Increment(ref _totalHciEvents);
                        eventBacklog.Enqueue(usbEvent);
                        await FlushMatchesAsync(pendingEventUrbs, eventBacklog).ConfigureAwait(false);
                    }
                    else if (pktType == 0x02) // HCI ACL: [0x02][HandleFlags 2B][DataLen 2B LE][Data...]
                    {
                        if (await ReadExactAsync(h4Stream, hdrBuf.AsMemory(1, 4), sessionCts.Token).ConfigureAwait(false) < 4)
                            break;
                        int aclLen = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf.AsSpan(3, 2));
                        byte[] usbAcl = new byte[4 + aclLen];
                        Buffer.BlockCopy(hdrBuf, 1, usbAcl, 0, 4);
                        if (aclLen > 0 &&
                            await ReadExactAsync(h4Stream, usbAcl.AsMemory(4, aclLen), sessionCts.Token).ConfigureAwait(false) < aclLen)
                        {
                            break;
                        }

                        aclBacklog.Enqueue(usbAcl);
                        await FlushMatchesAsync(pendingAclInUrbs, aclBacklog).ConfigureAwait(false);
                    }
                    else if (pktType == 0x03) // HCI SCO: [0x03][Handle 2B][Len 1B][Data...]
                    {
                        if (await ReadExactAsync(h4Stream, hdrBuf.AsMemory(1, 3), sessionCts.Token).ConfigureAwait(false) < 3)
                            break;
                        int scoLen = hdrBuf[3];
                        if (scoLen > 0)
                        {
                            byte[] discard = new byte[scoLen];
                            await ReadExactAsync(h4Stream, discard, sessionCts.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch
            {
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, sessionCts.Token);

        try
        {
            byte[] urbHdr = new byte[48];
            while (!sessionCts.Token.IsCancellationRequested)
            {
                if (await ReadExactAsync(usbStream, urbHdr, sessionCts.Token).ConfigureAwait(false) < 48)
                    break;

                uint cmd = BinaryPrimitives.ReadUInt32BigEndian(urbHdr.AsSpan(0, 4));
                uint seqNum = BinaryPrimitives.ReadUInt32BigEndian(urbHdr.AsSpan(4, 4));
                uint direction = BinaryPrimitives.ReadUInt32BigEndian(urbHdr.AsSpan(12, 4)); // 0=OUT, 1=IN
                uint ep = BinaryPrimitives.ReadUInt32BigEndian(urbHdr.AsSpan(16, 4)) & 0x0F;

                if (cmd == UsbIpCmdUnlink)
                {
                    uint targetSeq = BinaryPrimitives.ReadUInt32BigEndian(urbHdr.AsSpan(20, 4));
                    unlinkedSeqs[targetSeq] = 1;

                    byte[] retUnlink = new byte[48];
                    BinaryPrimitives.WriteUInt32BigEndian(retUnlink.AsSpan(0, 4), UsbIpRetUnlink);
                    BinaryPrimitives.WriteUInt32BigEndian(retUnlink.AsSpan(4, 4), seqNum);
                    BinaryPrimitives.WriteInt32BigEndian(retUnlink.AsSpan(20, 4), -104); // -ECONNRESET
                    await writeLock.WaitAsync(sessionCts.Token).ConfigureAwait(false);
                    try
                    {
                        await usbStream.WriteAsync(retUnlink, sessionCts.Token).ConfigureAwait(false);
                        await usbStream.FlushAsync(sessionCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                    continue;
                }

                if (cmd != UsbIpCmdSubmit)
                    break;

                int transferBufLen = BinaryPrimitives.ReadInt32BigEndian(urbHdr.AsSpan(24, 4));
                int numIsoPackets = BinaryPrimitives.ReadInt32BigEndian(urbHdr.AsSpan(32, 4));
                byte[] setup = urbHdr.AsSpan(40, 8).ToArray();

                byte[] outData = [];
                if (direction == 0 && transferBufLen > 0)
                {
                    outData = new byte[transferBufLen];
                    if (await ReadExactAsync(usbStream, outData, sessionCts.Token).ConfigureAwait(false) < transferBufLen)
                        break;
                }

                if (numIsoPackets > 0 && numIsoPackets < 1024)
                {
                    // Drain 16-byte usbip_iso_packet_descriptor entries
                    byte[] isoDescs = new byte[numIsoPackets * 16];
                    if (await ReadExactAsync(usbStream, isoDescs, sessionCts.Token).ConfigureAwait(false) < isoDescs.Length)
                        break;
                }

                if (ep == 0)
                {
                    await HandleEndpoint0ControlAsync(setup, direction, transferBufLen, outData, seqNum, h4Stream, SendRetSubmitAsync, sessionCts.Token)
                        .ConfigureAwait(false);
                }
                else if (ep == 1 && direction == 1) // Interrupt IN -> HCI Events
                {
                    pendingEventUrbs.Enqueue(new PendingInUrb(seqNum, ep, transferBufLen));
                    await FlushMatchesAsync(pendingEventUrbs, eventBacklog).ConfigureAwait(false);
                }
                else if (ep == 2 && direction == 0) // Bulk OUT -> HCI ACL TX
                {
                    byte[] h4Acl = new byte[1 + outData.Length];
                    h4Acl[0] = 0x02;
                    Buffer.BlockCopy(outData, 0, h4Acl, 1, outData.Length);
                    await h4Stream.WriteAsync(h4Acl, sessionCts.Token).ConfigureAwait(false);
                    await h4Stream.FlushAsync(sessionCts.Token).ConfigureAwait(false);
                    await SendRetSubmitAsync(seqNum, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                }
                else if (ep == 2 && direction == 1) // Bulk IN -> HCI ACL RX
                {
                    pendingAclInUrbs.Enqueue(new PendingInUrb(seqNum, ep, transferBufLen));
                    await FlushMatchesAsync(pendingAclInUrbs, aclBacklog).ConfigureAwait(false);
                }
                else
                {
                    // Isochronous SCO or auxiliary endpoints -> acknowledge cleanly
                    await SendRetSubmitAsync(seqNum, 0, ReadOnlyMemory<byte>.Empty, Math.Max(0, numIsoPackets)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            try { await h4ReadTask.ConfigureAwait(false); } catch { }
            writeLock.Dispose();
            IsDriverAttached = false;
            DriverAttachmentChanged?.Invoke(false);
        }
    }

    private async Task HandleEndpoint0ControlAsync(
        byte[] setup,
        uint direction,
        int transferBufLen,
        byte[] outData,
        uint seqNum,
        Stream h4Stream,
        Func<uint, int, ReadOnlyMemory<byte>, int, Task> sendRetSubmitAsync,
        CancellationToken ct)
    {
        byte bmRequestType = setup[0];
        byte bRequest = setup[1];
        ushort wValue = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(2, 2));
        ushort wLength = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6, 2));

        // Class-specific Host-to-Device request on EP0 = Bluetooth HCI Command! (USB BT Spec)
        if ((bmRequestType & 0x60) == 0x20 && direction == 0)
        {
            Interlocked.Increment(ref _totalHciCommands);
            byte[] h4Cmd = new byte[1 + outData.Length];
            h4Cmd[0] = 0x01; // H4 HCI Command Indicator
            Buffer.BlockCopy(outData, 0, h4Cmd, 1, outData.Length);
            await h4Stream.WriteAsync(h4Cmd, ct).ConfigureAwait(false);
            await h4Stream.FlushAsync(ct).ConfigureAwait(false);
            await sendRetSubmitAsync(seqNum, 0, ReadOnlyMemory<byte>.Empty, 0).ConfigureAwait(false);
            return;
        }

        // Standard GET_DESCRIPTOR (0x80, 0x06)
        if (bmRequestType == 0x80 && bRequest == 0x06)
        {
            byte descType = (byte)(wValue >> 8);
            byte descIdx = (byte)(wValue & 0xFF);
            byte[]? desc = descType switch
            {
                0x01 => BluetoothUsbDescriptors.DeviceDescriptor,
                0x02 => BluetoothUsbDescriptors.ConfigurationDescriptor,
                0x03 => BluetoothUsbDescriptors.GetStringDescriptor(descIdx),
                _ => null
            };

            if (desc is null)
            {
                // Unsupported optional descriptor (e.g., Device Qualifier 0x06) -> STALL (-EPIPE = -32)
                await sendRetSubmitAsync(seqNum, -32, ReadOnlyMemory<byte>.Empty, 0).ConfigureAwait(false);
                return;
            }

            int maxLen = Math.Min(desc.Length, Math.Min(wLength, transferBufLen));
            await sendRetSubmitAsync(seqNum, 0, desc.AsMemory(0, maxLen), 0).ConfigureAwait(false);
            return;
        }

        // GET_STATUS (0x80 / 0x81 / 0x82, 0x00)
        if ((bmRequestType & 0x80) != 0 && bRequest == 0x00)
        {
            byte[] statusBytes = [0x00, 0x00];
            await sendRetSubmitAsync(seqNum, 0, statusBytes.AsMemory(0, Math.Min(2, transferBufLen)), 0).ConfigureAwait(false);
            return;
        }

        // GET_CONFIGURATION (0x80, 0x08)
        if (bmRequestType == 0x80 && bRequest == 0x08)
        {
            byte[] cfg = [0x01];
            await sendRetSubmitAsync(seqNum, 0, cfg.AsMemory(0, Math.Min(1, transferBufLen)), 0).ConfigureAwait(false);
            return;
        }

        // SET_CONFIGURATION (0x09), SET_INTERFACE (0x0B), CLEAR_FEATURE (0x01), SET_FEATURE (0x03)
        await sendRetSubmitAsync(seqNum, 0, ReadOnlyMemory<byte>.Empty, 0).ConfigureAwait(false);
    }

    public static byte[] BuildDevlistReply()
    {
        // 12B header + 312B usbip_usb_device + 2 * 4B usbip_usb_interface = 332 bytes
        byte[] buf = new byte[12 + 312 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), UsbIpVersion);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), OpRepDevlist);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0); // status = OK
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), 1); // 1 exported device

        WriteUsbDeviceStruct(buf.AsSpan(12, 312));

        // Interface 0: Wireless / RF / Bluetooth
        buf[324] = 0xE0; buf[325] = 0x01; buf[326] = 0x01; buf[327] = 0x00;
        // Interface 1: Wireless / RF / Bluetooth (SCO)
        buf[328] = 0xE0; buf[329] = 0x01; buf[330] = 0x01; buf[331] = 0x00;
        return buf;
    }

    public static byte[] BuildImportReply(bool ok)
    {
        if (!ok)
        {
            byte[] err = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(err.AsSpan(0, 2), UsbIpVersion);
            BinaryPrimitives.WriteUInt16BigEndian(err.AsSpan(2, 2), OpRepImport);
            BinaryPrimitives.WriteUInt32BigEndian(err.AsSpan(4, 4), 1); // Error
            return err;
        }

        byte[] buf = new byte[8 + 312];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), UsbIpVersion);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), OpRepImport);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0); // OK
        WriteUsbDeviceStruct(buf.AsSpan(8, 312));
        return buf;
    }

    private static void WriteUsbDeviceStruct(Span<byte> dest312)
    {
        dest312.Clear();
        Encoding.ASCII.GetBytes(BluetoothUsbDescriptors.SysPath, dest312[..256]);
        Encoding.ASCII.GetBytes(BluetoothUsbDescriptors.BusId, dest312.Slice(256, 32));

        BinaryPrimitives.WriteUInt32BigEndian(dest312.Slice(288, 4), 1); // busnum = 1
        BinaryPrimitives.WriteUInt32BigEndian(dest312.Slice(292, 4), 2); // devnum = 2
        BinaryPrimitives.WriteUInt32BigEndian(dest312.Slice(296, 4), 2); // speed = USB_SPEED_FULL (2)
        BinaryPrimitives.WriteUInt16BigEndian(dest312.Slice(300, 2), BluetoothUsbDescriptors.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(dest312.Slice(302, 2), BluetoothUsbDescriptors.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(dest312.Slice(304, 2), BluetoothUsbDescriptors.BcdDevice);
        dest312[306] = 0xE0; // bDeviceClass
        dest312[307] = 0x01; // bDeviceSubClass
        dest312[308] = 0x01; // bDeviceProtocol
        dest312[309] = 1;    // bConfigurationValue
        dest312[310] = 1;    // bNumConfigurations
        dest312[311] = 2;    // bNumInterfaces
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }

    private static async ValueTask<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        _listener?.Stop();
    }
}
