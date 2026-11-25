using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Blueipooth.Protocol.Crypto;

public enum MediaStreamId : byte
{
    PcStereoOpus = 0x01,
    PhoneMicOpus = 0x02,
}

[Flags]
public enum MediaPacketFlags : byte
{
    None = 0x00,
    GamingDirect = 0x01,
    Discontinuity = 0x02,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct MediaPacketHeader(
    ushort Magic,
    MediaStreamId StreamId,
    MediaPacketFlags Flags,
    uint SequenceNumber,
    ulong TimestampUs)
{
    public const ushort MagicValue = 0xB17E;
    public const int HeaderSize = 16;
    public const int TagSize = 16;
    public const int OverheadBytes = HeaderSize + TagSize;

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], Magic);
        destination[2] = (byte)StreamId;
        destination[3] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], SequenceNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], TimestampUs);
    }

    public static bool TryReadFrom(ReadOnlySpan<byte> source, out MediaPacketHeader header)
    {
        if (source.Length < HeaderSize)
        {
            header = default;
            return false;
        }

        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(source[0..2]);
        if (magic != MagicValue)
        {
            header = default;
            return false;
        }

        header = new MediaPacketHeader(
            magic,
            (MediaStreamId)source[2],
            (MediaPacketFlags)source[3],
            BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..16]));
        return true;
    }

    public void DeriveNonce(Span<byte> nonce12)
    {
        nonce12[0] = (byte)StreamId;
        nonce12[1] = (byte)Flags;
        nonce12[2] = 0;
        nonce12[3] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce12[4..12], TimestampUs);

        Span<byte> seqBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seqBytes, SequenceNumber);
        for (int i = 0; i < 4; i++)
        {
            nonce12[i] ^= seqBytes[i];
        }
    }
}

public sealed class MediaFrameCipher : IDisposable
{
    private readonly byte[] _key16;
    private readonly Lock _sync = new();
    private AesGcm? _aesGcm;

    public MediaFrameCipher(ReadOnlySpan<byte> key16)
    {
        if (key16.Length != 16)
            throw new ArgumentException("AES-128-GCM key must be exactly 16 bytes.", nameof(key16));
        _key16 = key16.ToArray();
        _aesGcm = new AesGcm(_key16, MediaPacketHeader.TagSize);
    }

    public int EncryptFrame(
        MediaPacketHeader header,
        ReadOnlySpan<byte> opusPlaintext,
        Span<byte> packetOut)
    {
        int totalLen = MediaPacketHeader.OverheadBytes + opusPlaintext.Length;
        if (packetOut.Length < totalLen)
            throw new ArgumentException("Output buffer too small.", nameof(packetOut));

        header.WriteTo(packetOut[..MediaPacketHeader.HeaderSize]);
        ReadOnlySpan<byte> aad = packetOut[..MediaPacketHeader.HeaderSize];
        Span<byte> tag = packetOut.Slice(MediaPacketHeader.HeaderSize, MediaPacketHeader.TagSize);
        Span<byte> ciphertext = packetOut.Slice(MediaPacketHeader.OverheadBytes, opusPlaintext.Length);

        Span<byte> nonce = stackalloc byte[12];
        header.DeriveNonce(nonce);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_aesGcm is null, this);
            _aesGcm.Encrypt(nonce, opusPlaintext, ciphertext, tag, aad);
        }

        return totalLen;
    }

    public bool TryDecryptFrame(
        ReadOnlySpan<byte> packetIn,
        Span<byte> opusPlaintextOut,
        out MediaPacketHeader header,
        out int plaintextLength)
    {
        header = default;
        plaintextLength = 0;

        if (packetIn.Length < MediaPacketHeader.OverheadBytes ||
            !MediaPacketHeader.TryReadFrom(packetIn, out header))
        {
            return false;
        }

        int cipherLen = packetIn.Length - MediaPacketHeader.OverheadBytes;
        if (opusPlaintextOut.Length < cipherLen)
        {
            return false;
        }

        ReadOnlySpan<byte> aad = packetIn[..MediaPacketHeader.HeaderSize];
        ReadOnlySpan<byte> tag = packetIn.Slice(MediaPacketHeader.HeaderSize, MediaPacketHeader.TagSize);
        ReadOnlySpan<byte> ciphertext = packetIn.Slice(MediaPacketHeader.OverheadBytes, cipherLen);
        Span<byte> plainSlice = opusPlaintextOut[..cipherLen];

        Span<byte> nonce = stackalloc byte[12];
        header.DeriveNonce(nonce);

        try
        {
            lock (_sync)
            {
                if (_aesGcm is null) return false;
                _aesGcm.Decrypt(nonce, ciphertext, tag, plainSlice, aad);
            }
            plaintextLength = cipherLen;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _aesGcm?.Dispose();
            _aesGcm = null;
        }
    }
}
