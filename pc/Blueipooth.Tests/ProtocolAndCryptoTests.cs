using System.Security.Cryptography;
using Blueipooth.Protocol.Control;
using Blueipooth.Protocol.Crypto;
using Blueipooth.Protocol.Pairing;
using Xunit;

namespace Blueipooth.Tests;

public sealed class ProtocolAndCryptoTests
{
    [Fact]
    public void QrPairingPayload_RoundtripsUriCleanly()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        var original = new QrPairingPayload(
            Version: 1,
            PcName: "Viet-Gaming-PC",
            CandidateIps: ["192.168.1.25", "192.168.42.100"],
            ControlPort: 49550,
            MediaUdpPort: 49551,
            CertSha256Hex: "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            SessionKeyAes128: key,
            OneTimeToken: "0123456789ABCDEF");

        string uri = original.ToUri();
        var parsed = QrPairingPayload.Parse(uri);

        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.PcName, parsed.PcName);
        Assert.Equal(original.CandidateIps, parsed.CandidateIps);
        Assert.Equal(original.ControlPort, parsed.ControlPort);
        Assert.Equal(original.MediaUdpPort, parsed.MediaUdpPort);
        Assert.Equal(original.CertSha256Hex, parsed.CertSha256Hex);
        Assert.Equal(original.SessionKeyAes128, parsed.SessionKeyAes128);
        Assert.Equal(original.OneTimeToken, parsed.OneTimeToken);
    }

    [Fact]
    public void MediaFrameCipher_EncryptsAndRejectsTampering()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        using var cipher = new MediaFrameCipher(key);

        byte[] fakeOpus = [0xFC, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
        var hdr = new MediaPacketHeader(
            MediaPacketHeader.MagicValue,
            MediaStreamId.PcStereoOpus,
            MediaPacketFlags.GamingDirect,
            SequenceNumber: 42,
            TimestampUs: 123_456_789UL);

        byte[] wirePacket = new byte[MediaPacketHeader.OverheadBytes + fakeOpus.Length];
        int len = cipher.EncryptFrame(hdr, fakeOpus, wirePacket);
        Assert.Equal(wirePacket.Length, len);

        byte[] decrypted = new byte[64];
        Assert.True(cipher.TryDecryptFrame(wirePacket, decrypted, out var parsedHdr, out int plainLen));
        Assert.Equal(hdr, parsedHdr);
        Assert.Equal(fakeOpus, decrypted.AsSpan(0, plainLen).ToArray());

        // Tamper with AAD header sequence number -> GCM authentication MUST reject!
        wirePacket[4] ^= 0xFF;
        Assert.False(cipher.TryDecryptFrame(wirePacket, decrypted, out _, out _));
    }

    [Fact]
    public async Task ControlFrameCodec_FramesMessagesAccurately()
    {
        using var ms = new MemoryStream();
        var msg = new HeadsetStateMessage(true, "AirPods Pro", "AA:BB:CC:11:22:33", true);
        await ControlFrameCodec.WriteFrameAsync(ms, ControlMessageType.HeadsetState, msg);

        ms.Position = 0;
        var read = await ControlFrameCodec.ReadFrameAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(ControlMessageType.HeadsetState, read.Value.Type);

        var decoded = ControlFrameCodec.Deserialize<HeadsetStateMessage>(read.Value.JsonPayload);
        Assert.Equal("AirPods Pro", decoded.Name);
        Assert.True(decoded.Connected);
        Assert.True(decoded.HasMic);
    }
}
