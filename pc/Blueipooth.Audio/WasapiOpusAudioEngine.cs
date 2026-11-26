using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Blueipooth.Audio;

public sealed class WasapiOpusAudioEngine : IDisposable, IMMNotificationClient
{
    public const int TargetSampleRate = 48000;
    public const int StereoChannels = 2;
    public const int FrameDurationMs = 10;
    public const int SamplesPerChannelPerFrame = TargetSampleRate * FrameDurationMs / 1000; // 480
    public const int InterleavedSamplesPerFrame = SamplesPerChannelPerFrame * StereoChannels; // 960

    private readonly IOpusEncoder _stereoEncoder;
    private readonly IOpusDecoder _micDecoder;
    private readonly AdaptiveJitterBuffer _micJitterBuffer = new(targetDepthFrames: 3, maxDepthFrames: 8);
    private readonly SbcDecoder _sbcDecoder = new();

    private MMDevice? _captureDevice;
    private AudioClient? _loopbackAudioClient;
    private AudioCaptureClient? _loopbackCaptureClient;
    private WaveFormat? _loopbackMixFormat;
    private Thread? _loopbackThread;
    private CancellationTokenSource? _loopbackCts;
    private MMDeviceEnumerator? _deviceEnumerator;
    private string? _currentRenderDeviceId;
    private readonly object _captureLock = new();
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

    public static bool IsVirtualMicCableInstalled
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                bool hasCableInput = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Any(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
                bool hasCableOutput = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .Any(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                return hasCableInput && hasCableOutput;
            }
            catch
            {
                return false;
            }
        }
    }

    private float _hpPrevIn;
    private float _hpPrevOut;
    private float _gateEnvelope;

    // Stateful phase-continuous resampler across WASAPI chunks (eliminates 100Hz chunk-boundary clicks on 44.1kHz devices)
    private int _resampleSrcRate;
    private double _resamplePhase;
    private float _resamplePrevL;
    private float _resamplePrevR;
    private bool _resampleHasPrev;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

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
        lock (_captureLock)
        {
            if (IsStreamingSpeaker) return;

            if (_deviceEnumerator is null)
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                try { _deviceEnumerator.RegisterEndpointNotificationCallback(this); } catch { }
            }

            StartLoopbackInternal();
            TryAttachVirtualMicCableEndpoint();
        }
    }

    private void StartLoopbackInternal()
    {
        _accumCount = 0;
        _resampleHasPrev = false;
        _resamplePhase = 0;
        _captureDevice = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _currentRenderDeviceId = _captureDevice?.ID;
        if (_captureDevice is null) return;

        AudioClient audioClient = _captureDevice.AudioClient;
        WaveFormat mixFormat = audioClient.MixFormat;
        const long hns20Ms = 20 * 10_000; // 20ms buffer, 10ms native WASAPI shared period

        EventWaitHandle? frameEvent = null;
        bool useEvent = false;
        try
        {
            frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                hns20Ms,
                0,
                mixFormat,
                Guid.Empty);
            audioClient.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle());
            useEvent = true;
        }
        catch
        {
            frameEvent?.Dispose();
            frameEvent = null;
            audioClient.Dispose();
            audioClient = _captureDevice.AudioClient;
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback,
                hns20Ms,
                0,
                mixFormat,
                Guid.Empty);
        }

        _loopbackMixFormat = mixFormat;
        _loopbackAudioClient = audioClient;
        _loopbackCaptureClient = audioClient.AudioCaptureClient;
        var cts = new CancellationTokenSource();
        _loopbackCts = cts;

        audioClient.Start();
        IsStreamingSpeaker = true;

        _loopbackThread = new Thread(() => RunLowLatencyLoopbackLoop(audioClient, _loopbackCaptureClient, mixFormat, frameEvent, useEvent, cts.Token))
        {
            IsBackground = true,
            Name = "Blueipooth-WasapiLoopback",
            Priority = ThreadPriority.Highest
        };
        _loopbackThread.Start();
    }

    private void RunLowLatencyLoopbackLoop(
        AudioClient audioClient,
        AudioCaptureClient captureClient,
        WaveFormat mixFormat,
        EventWaitHandle? frameEvent,
        bool useEvent,
        CancellationToken ct)
    {
        byte[] managedBuf = Array.Empty<byte>();
        try { _ = timeBeginPeriod(1); } catch { }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (useEvent && frameEvent is not null)
                {
                    frameEvent.WaitOne(10);
                }
                else
                {
                    Thread.Sleep(5);
                }

                if (ct.IsCancellationRequested) break;

                int packetFrames = captureClient.GetNextPacketSize();
                while (packetFrames > 0 && !ct.IsCancellationRequested)
                {
                    IntPtr dataPtr = captureClient.GetBuffer(out int numFrames, out AudioClientBufferFlags flags);
                    int byteCount = numFrames * mixFormat.BlockAlign;
                    if (byteCount > 0)
                    {
                        if (managedBuf.Length < byteCount)
                        {
                            managedBuf = new byte[byteCount];
                        }

                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Array.Clear(managedBuf, 0, byteCount);
                        }
                        else
                        {
                            Marshal.Copy(dataPtr, managedBuf, 0, byteCount);
                        }

                        Span<float> converted = ConvertWasapiChunkToStereo48k(managedBuf.AsSpan(0, byteCount), mixFormat);
                        if (!converted.IsEmpty)
                        {
                            ProcessStereo48kChunk(converted);
                        }
                    }

                    captureClient.ReleaseBuffer(numFrames);
                    packetFrames = captureClient.GetNextPacketSize();
                }
            }
        }
        catch
        {
            // Device removed or stopped
        }
        finally
        {
            try { _ = timeEndPeriod(1); } catch { }
            frameEvent?.Dispose();
        }
    }

    public void StopSpeakerCapture()
    {
        lock (_captureLock)
        {
            if (!IsStreamingSpeaker) return;
            IsStreamingSpeaker = false;
            StopLoopbackInternal();
            _virtualMicRenderOut?.Stop();
            _virtualMicRenderOut?.Dispose();
            _virtualMicRenderOut = null;
            _virtualMicWaveBuffer = null;
            ActiveVirtualMicEndpointName = null;
        }
    }

    private void StopLoopbackInternal()
    {
        _loopbackCts?.Cancel();
        try { _loopbackThread?.Join(300); } catch { }
        _loopbackThread = null;
        _loopbackCts?.Dispose();
        _loopbackCts = null;

        try { _loopbackAudioClient?.Stop(); } catch { }
        _loopbackCaptureClient?.Dispose();
        _loopbackCaptureClient = null;
        _loopbackAudioClient?.Dispose();
        _loopbackAudioClient = null;
        _loopbackMixFormat = null;

        _captureDevice?.Dispose();
        _captureDevice = null;
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed || flow != DataFlow.Render || role != Role.Multimedia) return;
        if (string.Equals(_currentRenderDeviceId, defaultDeviceId, StringComparison.Ordinal)) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            lock (_captureLock)
            {
                if (_disposed || !IsStreamingSpeaker) return;
                try
                {
                    StopLoopbackInternal();
                    StartLoopbackInternal();
                }
                catch { }
            }
        });
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

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

    public void PushIncomingA2dpSbcPacket(ReadOnlySpan<byte> rtpPayload)
    {
        if (_disposed || IsStreamingSpeaker) return;

        float[] decoded = _sbcDecoder.DecodeRtpPayload(rtpPayload);
        if (decoded.Length < 2) return;

        int srcRate = _sbcDecoder.LastSampleRate;
        Span<float> stereo48k = srcRate == TargetSampleRate
            ? decoded
            : ResampleStereo(decoded, srcRate);

        ProcessStereo48kChunk(stereo48k);
    }

    private void ProcessStereo48kChunk(ReadOnlySpan<float> converted)
    {

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

    private float[] ConvertWasapiChunkToStereo48k(ReadOnlySpan<byte> rawBytes, WaveFormat fmt)
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

        return ResampleStereoStateful(srcStereo, fmt.SampleRate);
    }

    private float[] ResampleStereoStateful(float[] srcStereo, int srcRate)
    {
        int srcFrames = srcStereo.Length / 2;
        if (srcFrames <= 0) return [];

        if (_resampleSrcRate != srcRate || !_resampleHasPrev)
        {
            _resampleSrcRate = srcRate;
            _resamplePhase = 0.0;
            _resamplePrevL = srcStereo[0];
            _resamplePrevR = srcStereo[1];
            _resampleHasPrev = true;
        }

        double step = (double)srcRate / TargetSampleRate;
        int maxDstFrames = (int)Math.Ceiling((srcFrames + 1.0) / step) + 2;
        float[] dstScratch = new float[maxDstFrames * 2];
        int dstCount = 0;

        while (_resamplePhase < srcFrames - 1)
        {
            float left, right;
            if (_resamplePhase < 0.0)
            {
                float frac = (float)(_resamplePhase + 1.0);
                left = _resamplePrevL + (srcStereo[0] - _resamplePrevL) * frac;
                right = _resamplePrevR + (srcStereo[1] - _resamplePrevR) * frac;
            }
            else
            {
                int i0 = (int)_resamplePhase;
                int i1 = i0 + 1;
                float frac = (float)(_resamplePhase - i0);
                left = srcStereo[i0 * 2] + (srcStereo[i1 * 2] - srcStereo[i0 * 2]) * frac;
                right = srcStereo[i0 * 2 + 1] + (srcStereo[i1 * 2 + 1] - srcStereo[i0 * 2 + 1]) * frac;
            }

            dstScratch[dstCount * 2] = left;
            dstScratch[dstCount * 2 + 1] = right;
            dstCount++;
            _resamplePhase += step;
        }

        _resamplePrevL = srcStereo[(srcFrames - 1) * 2];
        _resamplePrevR = srcStereo[(srcFrames - 1) * 2 + 1];
        _resamplePhase -= srcFrames;

        return dstScratch.AsSpan(0, dstCount * 2).ToArray();
    }

    private static float[] ResampleStereo(float[] srcStereo, int srcRate)
    {
        int srcFrames = srcStereo.Length / 2;
        if (srcFrames <= 0) return [];
        int dstFrames = (int)((long)srcFrames * TargetSampleRate / srcRate);
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

    public static string GetActivePcMediaSummary()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var s = sessions[i];
                if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                uint pid = s.GetProcessID;
                if (pid == 0 || pid == Environment.ProcessId) continue;
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    string title = proc.MainWindowTitle?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(title))
                        return title.Length > 48 ? title[..45] + "..." : title;
                    if (!string.IsNullOrEmpty(proc.ProcessName))
                        return $"Đang phát ({proc.ProcessName})";
                }
                catch { }
            }
        }
        catch { }
        return "Chưa có media đang phát";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSpeakerCapture();
        if (_deviceEnumerator is not null)
        {
            try { _deviceEnumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            _deviceEnumerator.Dispose();
            _deviceEnumerator = null;
        }
    }
}
