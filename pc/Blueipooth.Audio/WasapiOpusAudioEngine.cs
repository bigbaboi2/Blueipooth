using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Blueipooth.Audio;

/// <summary>
/// Real-time 48kHz Low-Latency Audio Engine:
/// - Captures Windows speaker audio via WASAPI Loopback, normalizes/resamples to 48kHz Stereo,
///   and encodes 5ms (240 samples/ch) Opus frames using Concentus RESTRICTED_LOWDELAY.
/// - Decodes incoming 48kHz Phone Mic Opus frames via AdaptiveJitterBuffer + PLC, routing automatically
///   to a Virtual Audio Cable ("CABLE Input") if present and exposing live RMS meters.
/// </summary>
public sealed class WasapiOpusAudioEngine : IDisposable
{
    public const int TargetSampleRate = 48000;
    public const int StereoChannels = 2;
    public const int FrameDurationMs = 10;
    public const int SamplesPerChannelPerFrame = TargetSampleRate * FrameDurationMs / 1000; // 480
    public const int InterleavedSamplesPerFrame = SamplesPerChannelPerFrame * StereoChannels; // 960

    private readonly IOpusEncoder _stereoEncoder;
    private readonly IOpusDecoder _micDecoder;
    private readonly AdaptiveJitterBuffer _micJitterBuffer = new(targetDepthFrames: 3, maxDepthFrames: 8);

    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiOut? _virtualMicRenderOut;
    private BufferedWaveProvider? _virtualMicWaveBuffer;

    private readonly float[] _accumBuffer = new float[InterleavedSamplesPerFrame * 16];
    private int _accumCount;
    private bool _disposed;

    public bool IsStreamingSpeaker { get; private set; }
    public bool IsMicEnabled { get; set; } = false;
    public bool IsMicNoiseFilterEnabled { get; set; } = true;
    public float MicGainBoost { get; set; } = 2.2f;
    public string? ActiveVirtualMicEndpointName { get; private set; }

    private float _hpPrevIn;
    private float _hpPrevOut;
    private float _gateEnvelope;

    public event Action<ReadOnlyMemory<byte>>? EncodedStereoOpusFrameReady;
    public event Action<double>? SpeakerPeakLevelChanged;
    public event Action<double>? MicPeakLevelChanged;

    public WasapiOpusAudioEngine()
    {
        _stereoEncoder = OpusCodecFactory.CreateEncoder(
            TargetSampleRate,
            StereoChannels,
            OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _stereoEncoder.Bitrate = 160_000;
        _stereoEncoder.Complexity = 5;
        _stereoEncoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;

        _micDecoder = OpusCodecFactory.CreateDecoder(TargetSampleRate, 1);
    }

    public void StartSpeakerCapture()
    {
        if (IsStreamingSpeaker) return;

        _accumCount = 0;
        _loopbackCapture = new WasapiLoopbackCapture();
        _loopbackCapture.DataAvailable += OnWasapiDataAvailable;
        _loopbackCapture.RecordingStopped += (_, _) => IsStreamingSpeaker = false;
        _loopbackCapture.StartRecording();
        IsStreamingSpeaker = true;

        TryAttachVirtualMicCableEndpoint();
    }

    public void StopSpeakerCapture()
    {
        if (!IsStreamingSpeaker) return;
        IsStreamingSpeaker = false;

        if (_loopbackCapture is not null)
        {
            try { _loopbackCapture.StopRecording(); } catch { }
            _loopbackCapture.DataAvailable -= OnWasapiDataAvailable;
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }

        _virtualMicRenderOut?.Stop();
        _virtualMicRenderOut?.Dispose();
        _virtualMicRenderOut = null;
        _virtualMicWaveBuffer = null;
        ActiveVirtualMicEndpointName = null;
    }

    public void PushIncomingPhoneMicOpusPacket(uint sequenceNumber, ReadOnlySpan<byte> opusBytes)
    {
        if (!IsMicEnabled) return;

        if (_virtualMicWaveBuffer is null)
        {
            TryAttachVirtualMicCableEndpoint();
        }

        _micJitterBuffer.Push(sequenceNumber, opusBytes);
        Span<short> pcmMono = stackalloc short[SamplesPerChannelPerFrame];

        while (true)
        {
            var (frame, isPlc) = _micJitterBuffer.PopNext();
            if (frame is null && !isPlc) break;

            int decodedSamples = frame is not null
                ? _micDecoder.Decode(frame, pcmMono, SamplesPerChannelPerFrame, false)
                : _micDecoder.Decode(ReadOnlySpan<byte>.Empty, pcmMono, SamplesPerChannelPerFrame, false);

            if (decodedSamples <= 0) break;

            ApplyMicProcessing(pcmMono[..decodedSamples]);

            // Compute RMS peak for UI meter
            double sumSq = 0;
            for (int i = 0; i < decodedSamples; i++)
            {
                double norm = pcmMono[i] / 32768.0;
                sumSq += norm * norm;
            }
            double rms = Math.Min(1.0, Math.Sqrt(sumSq / decodedSamples) * 3.0);
            MicPeakLevelChanged?.Invoke(rms);

            // Feed Virtual Audio Cable ("CABLE Input") if installed so Discord/Zoom can pick it directly
            if (_virtualMicWaveBuffer is not null)
            {
                byte[] pcmBytes = new byte[decodedSamples * 2];
                Buffer.BlockCopy(pcmMono[..decodedSamples].ToArray(), 0, pcmBytes, 0, pcmBytes.Length);
                _virtualMicWaveBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
            }

            if (_micJitterBuffer.CurrentDepth < 2) break;
        }
    }

    /// <summary>
    /// Standalone helper used by both live capture and automated roundtrip tests.
    /// </summary>
    private void ApplyMicProcessing(Span<short> pcmMono)
    {
        // 1-pole High-Pass Filter (~100Hz @ 48kHz) removes wind, AC hum, and desk thumps
        const float hpAlpha = 0.987f;
        // Noise Gate threshold (~ -36 dBFS) suppresses background fan & light keyboard clicks
        const float gateThreshold = 0.015f;

        Span<float> filtered = stackalloc float[pcmMono.Length];
        float framePeak = 0f;

        for (int i = 0; i < pcmMono.Length; i++)
        {
            float x = pcmMono[i] / 32768f;
            if (IsMicNoiseFilterEnabled)
            {
                float y = hpAlpha * (_hpPrevOut + x - _hpPrevIn);
                _hpPrevIn = x;
                _hpPrevOut = y;
                filtered[i] = y;
                float abs = MathF.Abs(y);
                if (abs > framePeak) framePeak = abs;
            }
            else
            {
                filtered[i] = x;
            }
        }

        if (IsMicNoiseFilterEnabled)
        {
            float targetGate = framePeak >= gateThreshold ? 1.0f : 0.0f;
            float coeff = targetGate > _gateEnvelope ? 0.35f : 0.08f; // Fast attack, smooth release
            _gateEnvelope += (targetGate - _gateEnvelope) * coeff;
        }
        else
        {
            _gateEnvelope = 1.0f;
        }

        float gain = MicGainBoost * _gateEnvelope;
        for (int i = 0; i < pcmMono.Length; i++)
        {
            float boosted = MathF.Tanh(filtered[i] * gain); // Soft limiter prevents clipping!
            pcmMono[i] = (short)Math.Clamp(boosted * 32767f, short.MinValue, short.MaxValue);
        }
    }

    public int EncodeStereoPcm48kFrame(ReadOnlySpan<float> interleaved480, Span<byte> opusOut)
    {
        if (interleaved480.Length < InterleavedSamplesPerFrame)
            throw new ArgumentException($"Expected {InterleavedSamplesPerFrame} interleaved float samples.");

        return _stereoEncoder.Encode(interleaved480[..InterleavedSamplesPerFrame], SamplesPerChannelPerFrame, opusOut, opusOut.Length);
    }

    private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _loopbackCapture is null) return;

        WaveFormat fmt = _loopbackCapture.WaveFormat;
        Span<float> converted = ConvertWasapiChunkToStereo48k(e.Buffer.AsSpan(0, e.BytesRecorded), fmt);
        if (converted.IsEmpty) return;

        // Compute speaker meter RMS
        double sumSq = 0;
        int step = Math.Max(1, converted.Length / 64);
        int samplesCounted = 0;
        for (int i = 0; i < converted.Length; i += step)
        {
            sumSq += converted[i] * converted[i];
            samplesCounted++;
        }
        if (samplesCounted > 0)
        {
            SpeakerPeakLevelChanged?.Invoke(Math.Min(1.0, Math.Sqrt(sumSq / samplesCounted) * 2.5));
        }

        int offset = 0;
        byte[] opusScratch = new byte[512];

        while (offset < converted.Length)
        {
            int space = _accumBuffer.Length - _accumCount;
            int toCopy = Math.Min(space, converted.Length - offset);
            converted.Slice(offset, toCopy).CopyTo(_accumBuffer.AsSpan(_accumCount, toCopy));
            _accumCount += toCopy;
            offset += toCopy;

            int readIdx = 0;
            while (_accumCount - readIdx >= InterleavedSamplesPerFrame)
            {
                ReadOnlySpan<float> frameSpan = _accumBuffer.AsSpan(readIdx, InterleavedSamplesPerFrame);
                int encodedLen = _stereoEncoder.Encode(frameSpan, SamplesPerChannelPerFrame, opusScratch, opusScratch.Length);
                if (encodedLen > 0)
                {
                    byte[] payload = opusScratch.AsSpan(0, encodedLen).ToArray();
                    EncodedStereoOpusFrameReady?.Invoke(payload);
                }
                readIdx += InterleavedSamplesPerFrame;
            }

            if (readIdx > 0)
            {
                int rem = _accumCount - readIdx;
                if (rem > 0)
                {
                    Array.Copy(_accumBuffer, readIdx, _accumBuffer, 0, rem);
                }
                _accumCount = rem;
            }
        }
    }

    private static readonly Guid IeeeFloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private static float[] ConvertWasapiChunkToStereo48k(ReadOnlySpan<byte> rawBytes, WaveFormat fmt)
    {
        int srcChannels = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) return [];

        int frameSize = srcChannels * bytesPerSample;
        int srcFrames = rawBytes.Length / frameSize;
        if (srcFrames == 0) return [];

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubType);

        // Extract stereo frames at source sample rate
        float[] srcStereo = new float[srcFrames * 2];
        for (int f = 0; f < srcFrames; f++)
        {
            int baseIdx = f * frameSize;
            float left = ReadNormalizedSample(rawBytes, baseIdx, bytesPerSample, isFloat);
            float right = srcChannels >= 2
                ? ReadNormalizedSample(rawBytes, baseIdx + bytesPerSample, bytesPerSample, isFloat)
                : left;
            srcStereo[f * 2] = Math.Clamp(left, -1f, 1f);
            srcStereo[f * 2 + 1] = Math.Clamp(right, -1f, 1f);
        }

        if (fmt.SampleRate == TargetSampleRate)
        {
            return srcStereo;
        }

        // Fast linear resample (e.g. 44.1kHz / 96kHz -> 48kHz)
        int dstFrames = (int)((long)srcFrames * TargetSampleRate / fmt.SampleRate);
        if (dstFrames <= 0) return [];

        float[] dstStereo = new float[dstFrames * 2];
        double ratio = (double)(srcFrames - 1) / Math.Max(1, dstFrames - 1);
        for (int d = 0; d < dstFrames; d++)
        {
            double pos = d * ratio;
            int idx0 = Math.Min((int)pos, srcFrames - 1);
            int idx1 = Math.Min(idx0 + 1, srcFrames - 1);
            float frac = (float)(pos - idx0);
            dstStereo[d * 2] = srcStereo[idx0 * 2] + (srcStereo[idx1 * 2] - srcStereo[idx0 * 2]) * frac;
            dstStereo[d * 2 + 1] = srcStereo[idx0 * 2 + 1] + (srcStereo[idx1 * 2 + 1] - srcStereo[idx0 * 2 + 1]) * frac;
        }
        return dstStereo;
    }

    private static float ReadNormalizedSample(ReadOnlySpan<byte> buf, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample == 4)
            return BitConverter.ToSingle(buf.Slice(offset, 4));
        if (bytesPerSample == 2)
            return BitConverter.ToInt16(buf.Slice(offset, 2)) / 32768f;
        if (bytesPerSample == 4)
            return BitConverter.ToInt32(buf.Slice(offset, 4)) / 2147483648f;
        return 0f;
    }

    private void TryAttachVirtualMicCableEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var cableDevice = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d =>
                    d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                    d.FriendlyName.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase));

            if (cableDevice is null) return;

            _virtualMicWaveBuffer = new BufferedWaveProvider(new WaveFormat(TargetSampleRate, 16, 1))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(120),
            };
            _virtualMicRenderOut = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, 15);
            _virtualMicRenderOut.Init(_virtualMicWaveBuffer);
            _virtualMicRenderOut.Play();
            ActiveVirtualMicEndpointName = cableDevice.FriendlyName;
        }
        catch
        {
            ActiveVirtualMicEndpointName = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSpeakerCapture();
    }
}
