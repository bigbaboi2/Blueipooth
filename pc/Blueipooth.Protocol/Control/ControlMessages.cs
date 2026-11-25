using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blueipooth.Protocol.Control;

public enum ControlMessageType : byte
{
    PairHello = 0x01,
    PairAccept = 0x02,
    Ping = 0x03,
    Pong = 0x04,

    BleScanCmd = 0x10,
    BleAdvReport = 0x11,
    BleAdvCmd = 0x12,
    BleGattReq = 0x13,
    BleGattRsp = 0x14,

    HeadsetState = 0x20,
    AudioConfig = 0x21,
    MediaControl = 0x22,
}

public sealed record PairHelloMessage(
    [property: JsonPropertyName("peerId")] string PeerId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("isReconnect")] bool IsReconnect,
    [property: JsonPropertyName("udpPort")] int UdpPort,
    [property: JsonPropertyName("androidSdk")] int AndroidSdk);

public sealed record PairAcceptMessage(
    [property: JsonPropertyName("pcName")] string PcName,
    [property: JsonPropertyName("reconnectToken")] string ReconnectToken,
    [property: JsonPropertyName("mediaKeyBase64")] string MediaKeyBase64,
    [property: JsonPropertyName("udpPort")] int UdpPort);

public sealed record PingMessage(
    [property: JsonPropertyName("clientTimestampUs")] long ClientTimestampUs);

public sealed record PongMessage(
    [property: JsonPropertyName("clientTimestampUs")] long ClientTimestampUs,
    [property: JsonPropertyName("remoteTimestampUs")] long RemoteTimestampUs);

public sealed record BleScanCmdMessage(
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("filterDuplicates")] bool FilterDuplicates);

public sealed record BleAdvReportMessage(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("addrType")] int AddrType,
    [property: JsonPropertyName("rssi")] int Rssi,
    [property: JsonPropertyName("connectable")] bool Connectable,
    [property: JsonPropertyName("dataHex")] string DataHex);

public sealed record BleAdvCmdMessage(
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("advDataHex")] string AdvDataHex,
    [property: JsonPropertyName("scanRspHex")] string ScanRspHex);

public sealed record HeadsetStateMessage(
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mac")] string Mac,
    [property: JsonPropertyName("hasMic")] bool HasMic);

public sealed record AudioConfigMessage(
    [property: JsonPropertyName("mode")] string Mode, // "GamingOpus" | "NativeA2dp"
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("channels")] int Channels,
    [property: JsonPropertyName("frameMs")] int FrameMs,
    [property: JsonPropertyName("micEnabled")] bool MicEnabled);

public sealed record MediaControlMessage(
    [property: JsonPropertyName("key")] string Key);

public static class ControlFrameCodec
{
    public const int MaxFramePayloadBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async ValueTask WriteFrameAsync<T>(
        Stream stream,
        ControlMessageType type,
        T message,
        CancellationToken ct = default)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        byte[] buffer = new byte[5 + jsonBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)jsonBytes.Length);
        buffer[4] = (byte)type;
        Buffer.BlockCopy(jsonBytes, 0, buffer, 5, jsonBytes.Length);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<(ControlMessageType Type, string JsonPayload)?> ReadFrameAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        byte[] header = new byte[5];
        int read = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < 5) throw new EndOfStreamException("Truncated control frame header.");

        uint len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (len > MaxFramePayloadBytes)
            throw new InvalidDataException($"Control frame payload too large: {len} bytes.");

        var type = (ControlMessageType)header[4];
        byte[] payload = new byte[len];
        if (len > 0)
        {
            int bodyRead = await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
            if (bodyRead < len) throw new EndOfStreamException("Truncated control frame payload.");
        }

        return (type, Encoding.UTF8.GetString(payload));
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOpts)
        ?? throw new InvalidDataException($"Failed to deserialize {typeof(T).Name}.");

    private static async ValueTask<int> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken ct)
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
}
