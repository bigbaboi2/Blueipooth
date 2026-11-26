using System.Buffers.Binary;
using System.Net.Sockets;
using Blueipooth.Audio;
using Blueipooth.UsbIp;
using Xunit;

namespace Blueipooth.Tests;

public sealed class UsbIpAndAudioTests
{
    [Fact]
    public void AdaptiveJitterBuffer_ReordersOutOfOrderAndSignalsPlc()
    {
        var jb = new AdaptiveJitterBuffer(targetDepthFrames: 3, maxDepthFrames: 6);

        // Push out-of-order: 10, 12, 11 -> primes at depth 3
        jb.Push(10, [0xAA]);
        Assert.Equal((null, false), jb.PopNext()); // Still priming

        jb.Push(12, [0xCC]);
        jb.Push(11, [0xBB]);

        var f10 = jb.PopNext();
        Assert.NotNull(f10.OpusFrame);
        Assert.Equal(0xAA, f10.OpusFrame![0]);

        var f11 = jb.PopNext();
        Assert.NotNull(f11.OpusFrame);
        Assert.Equal(0xBB, f11.OpusFrame![0]);

        // Push 14 (skipping 13!)
        jb.Push(14, [0xEE]);

        var f12 = jb.PopNext();
        Assert.Equal(0xCC, f12.OpusFrame![0]);

        // Expect missing seq 13 -> PLC triggered!
        var f13 = jb.PopNext();
        Assert.Null(f13.OpusFrame);
        Assert.True(f13.IsMissingForPlc);

        var f14 = jb.PopNext();
        Assert.Equal(0xEE, f14.OpusFrame![0]);
    }

    [Fact]
    public void WasapiOpusAudioEngine_Encodes5msStereo48kFrame()
    {
        using var engine = new WasapiOpusAudioEngine();
        float[] sine480 = new float[WasapiOpusAudioEngine.InterleavedSamplesPerFrame];
        for (int i = 0; i < WasapiOpusAudioEngine.SamplesPerChannelPerFrame; i++)
        {
            float sample = MathF.Sin(2f * MathF.PI * 440f * i / 48000f) * 0.5f;
            sine480[i * 2] = sample;
            sine480[i * 2 + 1] = sample;
        }

        Span<byte> opusOut = stackalloc byte[256];
        int encodedBytes = engine.EncodeStereoPcm48kFrame(sine480, opusOut);
        Assert.InRange(encodedBytes, 10, 260);
    }

    [Fact]
    public async Task UsbIpServer_EndToEndWithBumbleEngine_HandlesDevlistImportAndHciReset()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string pyExe = Path.Combine(repoRoot, ".tools", "python", "python.exe");
        string mainPy = Path.Combine(repoRoot, "pc", "bumble-engine", "main.py");
        Assert.True(File.Exists(pyExe), $"Expected portable python at {pyExe}");

        await using var bumble = new BumbleEngineHost(pyExe, mainPy);
        await bumble.StartAsync(h4Port: 49652, ipcPort: 49653);

        await using var usbIp = new UsbIpServer(listenPort: 0, h4Host: "127.0.0.1", h4Port: 49652);
        usbIp.Start();

        // 1. Test OP_REQ_DEVLIST
        using (var devlistClient = new TcpClient())
        {
            await devlistClient.ConnectAsync("127.0.0.1", usbIp.ListenPort);
            var s = devlistClient.GetStream();
            byte[] req = [0x01, 0x11, 0x80, 0x05, 0x00, 0x00, 0x00, 0x00];
            await s.WriteAsync(req);

            byte[] rep = new byte[332];
            int n = await ReadFullAsync(s, rep);
            Assert.Equal(332, n);
            Assert.Equal(UsbIpServer.OpRepDevlist, BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(2, 2)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(rep.AsSpan(8, 4)));
        }

        // 2. Test OP_REQ_IMPORT + USB Device Descriptor + HCI_Reset via USB Control EP0 & Interrupt EP1!
        using var importClient = new TcpClient { NoDelay = true };
        await importClient.ConnectAsync("127.0.0.1", usbIp.ListenPort);
        var stream = importClient.GetStream();

        byte[] importReq = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(importReq.AsSpan(0, 2), UsbIpServer.UsbIpVersion);
        BinaryPrimitives.WriteUInt16BigEndian(importReq.AsSpan(2, 2), UsbIpServer.OpReqImport);
        System.Text.Encoding.ASCII.GetBytes("1-1", importReq.AsSpan(8, 3));
        await stream.WriteAsync(importReq);

        byte[] importRep = new byte[320];
        Assert.Equal(320, await ReadFullAsync(stream, importRep));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(importRep.AsSpan(4, 4)));

        // Submit Pending Interrupt IN URB on EP 1 (waiting for HCI Command Complete Event!)
        byte[] intrInUrb = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(intrInUrb.AsSpan(0, 4), UsbIpServer.UsbIpCmdSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(intrInUrb.AsSpan(4, 4), 100); // seqnum = 100
        BinaryPrimitives.WriteUInt32BigEndian(intrInUrb.AsSpan(12, 4), 1);  // DIR_IN
        BinaryPrimitives.WriteUInt32BigEndian(intrInUrb.AsSpan(16, 4), 1);  // EP 1
        BinaryPrimitives.WriteInt32BigEndian(intrInUrb.AsSpan(24, 4), 260); // MaxLength
        await stream.WriteAsync(intrInUrb);

        // Submit HCI_Reset Command (0x03, 0x0C, 0x00) on EP 0 Class Control OUT (bmRequestType = 0x20)
        byte[] hciResetPayload = [0x03, 0x0C, 0x00];
        byte[] ctrlOutUrb = new byte[48 + hciResetPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(ctrlOutUrb.AsSpan(0, 4), UsbIpServer.UsbIpCmdSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(ctrlOutUrb.AsSpan(4, 4), 101); // seqnum = 101
        BinaryPrimitives.WriteUInt32BigEndian(ctrlOutUrb.AsSpan(12, 4), 0);  // DIR_OUT
        BinaryPrimitives.WriteUInt32BigEndian(ctrlOutUrb.AsSpan(16, 4), 0);  // EP 0
        BinaryPrimitives.WriteInt32BigEndian(ctrlOutUrb.AsSpan(24, 4), hciResetPayload.Length);
        ctrlOutUrb[40] = 0x20; // Setup[0] = Class Host-to-Device
        ctrlOutUrb[46] = 0x03; // wLength = 3
        Buffer.BlockCopy(hciResetPayload, 0, ctrlOutUrb, 48, 3);
        await stream.WriteAsync(ctrlOutUrb);

        // Read two RET_SUBMIT packets (one for seq 101 Control ACK, one for seq 100 Interrupt HCI Event!)
        bool gotControlAck = false;
        bool gotHciCommandComplete = false;

        for (int i = 0; i < 2; i++)
        {
            byte[] retHdr = new byte[48];
            Assert.Equal(48, await ReadFullAsync(stream, retHdr));
            Assert.Equal(UsbIpServer.UsbIpRetSubmit, BinaryPrimitives.ReadUInt32BigEndian(retHdr.AsSpan(0, 4)));
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(retHdr.AsSpan(4, 4));
            int status = BinaryPrimitives.ReadInt32BigEndian(retHdr.AsSpan(20, 4));
            int actualLen = BinaryPrimitives.ReadInt32BigEndian(retHdr.AsSpan(24, 4));
            Assert.Equal(0, status);

            if (seq == 101)
            {
                gotControlAck = true;
            }
            else if (seq == 100)
            {
                byte[] evtBytes = new byte[actualLen];
                Assert.Equal(actualLen, await ReadFullAsync(stream, evtBytes));
                // HCI Command Complete Event Code = 0x0E!
                Assert.Equal(0x0E, evtBytes[0]);
                gotHciCommandComplete = true;
            }
        }

        Assert.True(gotControlAck);
        Assert.True(gotHciCommandComplete);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
