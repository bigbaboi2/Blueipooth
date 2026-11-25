using System.IO;
using System.Runtime.InteropServices;
using Blueipooth.Audio;
using Blueipooth.Protocol.Control;
using Blueipooth.Protocol.Discovery;
using Blueipooth.Protocol.Pairing;
using Blueipooth.Protocol.Transport;
using Blueipooth.UsbIp;

namespace Blueipooth.App.Services;

/// <summary>
/// Central coordinator wiring USB/IP Server, Bumble 5.2 Engine, Secure LAN/USB Server, and WASAPI Opus Audio.
/// </summary>
public sealed partial class BlueipoothCoordinator : IAsyncDisposable
{
    public UsbIpServer UsbIp { get; }
    public BumbleEngineHost? Bumble { get; private set; }
    public BlueipoothLanServer LanServer { get; }
    public NetworkDiscoveryService Discovery { get; }
    public WasapiOpusAudioEngine AudioEngine { get; }

    public string CurrentAudioMode { get; private set; } = "GamingOpus"; // "GamingOpus" | "NativeA2dp"
    public int CurrentJitterBufferMs { get; private set; } = 30;
    public bool IsConnectionPaused => LanServer.ConnectionsPaused;
    public bool IsBleScanning { get; private set; }
    public HeadsetStateMessage? AttachedHeadset { get; private set; }
    public DeviceStatusMessage? LatestPhoneStatus { get; private set; }
    public string? SynthesizedStereoMac { get; private set; }
    public string? SynthesizedMicMac { get; private set; }

    public event Action<string>? LogAdded;
    public event Action? StateChanged;
    public event Action<BleAdvReportMessage>? BleDeviceDiscovered;

    public BlueipoothCoordinator()
    {
        UsbIp = new UsbIpServer(listenPort: 3240, h4Host: "127.0.0.1", h4Port: 49552);
        LanServer = new BlueipoothLanServer(controlPort: 49550, mediaUdpPort: 49551);
        Discovery = new NetworkDiscoveryService(
            Environment.MachineName,
            LanServer.ControlPort,
            LanServer.MediaUdpPort,
            LanServer.CertSha256Hex,
            FindRepoToolPath(@".tools\android-sdk\platform-tools\adb.exe"));
        AudioEngine = new WasapiOpusAudioEngine();

        WireEvents();
    }

    public async Task InitializeAsync()
    {
        LanServer.Start();
        Discovery.Start();
        Log($"LAN/USB Server listening on TLS :{LanServer.ControlPort}, UDP :{LanServer.MediaUdpPort}");

        string? pyExe = FindRepoToolPath(@".tools\python\python.exe");
        string? bumbleMain = FindRepoFilePath(@"pc\bumble-engine\main.py");

        if (pyExe is not null && bumbleMain is not null)
        {
            Bumble = new BumbleEngineHost(pyExe, bumbleMain);
            Bumble.EngineLogLine += line => Log($"[Bumble] {line}");
            Bumble.BleScanRequested += OnBumbleBleScanRequested;
            Bumble.BleAdvertiseRequested += OnBumbleBleAdvRequested;
            Bumble.DualMacHeadsetSynthesized += (stereo, mic) =>
            {
                SynthesizedStereoMac = stereo;
                SynthesizedMicMac = mic;
                Log($"Dual-MAC Headset ready in Windows BT: Stereo={stereo}, HD Mic={mic ?? "N/A"}");
                StateChanged?.Invoke();
            };
            int a2dpPktCount = 0;
            Bumble.A2dpSbcPacketReceived += sbcPayload =>
            {
                if (CurrentAudioMode == "NativeA2dp" && LanServer.CurrentPhone is not null)
                {
                    if (Interlocked.Increment(ref a2dpPktCount) <= 2)
                    {
                        Log($"A2DP SBC packet #{a2dpPktCount}: {sbcPayload.Length}B, head={Convert.ToHexString(sbcPayload.AsSpan(0, Math.Min(6, sbcPayload.Length)))}");
                    }
                    AudioEngine.PushIncomingA2dpSbcPacket(sbcPayload);
                }
            };

            await Bumble.StartAsync();
            UsbIp.UpdateH4Port(Bumble.H4Port);
            Log("Bumble Bluetooth 5.2 Virtual Controller started.");

            await Bumble.SetAudioModeAsync(CurrentAudioMode);
            if (AttachedHeadset is { Connected: true } pendingHs)
            {
                await Bumble.UpdateHeadsetStateAsync(pendingHs);
            }
        }
        else
        {
            Log("Warning: Portable Python or bumble-engine/main.py not found. Run scripts/setup-dev.ps1.");
        }

        UsbIp.Start();
        Log($"Local USB/IP Bluetooth Server active at 127.0.0.1:{UsbIp.ListenPort} (BusID {BluetoothUsbDescriptors.BusId}).");
        StateChanged?.Invoke();
    }

    public async Task<(bool Ok, string Message)> SetVirtualBluetoothCardEnabledAsync(bool enable)
    {
        if (enable)
        {
            var res = await UsbipWin2Manager.AttachVirtualBluetoothAsync(UsbIp.ListenPort);
            Log(res.Success ? $"Virtual BT Attached: {res.Output}" : $"Attach failed: {res.Output}");
            StateChanged?.Invoke();
            return res;
        }
        else
        {
            await UsbipWin2Manager.DetachAllLocalVirtualDevicesAsync();
            Log("Detached Virtual Bluetooth Adapter.");
            StateChanged?.Invoke();
            return (true, "Detached");
        }
    }

    public async Task SetAudioModeAsync(string mode, bool micEnabled, int jitterBufferMs = 30)
    {
        CurrentAudioMode = mode;
        CurrentJitterBufferMs = Math.Clamp(jitterBufferMs, 15, 80);
        AudioEngine.IsMicEnabled = micEnabled;

        if (Bumble is not null)
        {
            await Bumble.SetAudioModeAsync(mode);
        }

        UpdateAudioCaptureState();

        await LanServer.SendControlMessageAsync(ControlMessageType.AudioConfig, new AudioConfigMessage(
            Mode: mode,
            SampleRate: WasapiOpusAudioEngine.TargetSampleRate,
            Channels: WasapiOpusAudioEngine.StereoChannels,
            FrameMs: WasapiOpusAudioEngine.FrameDurationMs,
            MicEnabled: micEnabled,
            JitterBufferMs: CurrentJitterBufferMs));

        StateChanged?.Invoke();
    }

    public QrPairingPayload GetPairingQrPayload() => LanServer.GenerateCurrentQrPayload();

    public void SetConnectionPaused(bool paused)
    {
        LanServer.SetConnectionsPaused(paused);
        Log(paused ? "Phone connection paused from PC." : "Phone connection resumed from PC.");
        StateChanged?.Invoke();
    }

    private CancellationTokenSource? _bleScanTimeoutCts;

    public async Task RequestBleScanAsync(bool enable)
    {
        _bleScanTimeoutCts?.Cancel();
        _bleScanTimeoutCts?.Dispose();
        _bleScanTimeoutCts = null;

        IsBleScanning = enable;
        Log(enable ? "Sending BLE scan START request to Android (15s timeout)..." : "Sending BLE scan STOP request to Android...");
        StateChanged?.Invoke();
        await LanServer.SendControlMessageAsync(
            ControlMessageType.BleScanCmd,
            new BleScanCmdMessage(Enable: enable, FilterDuplicates: false)).ConfigureAwait(false);

        if (enable)
        {
            var cts = new CancellationTokenSource();
            _bleScanTimeoutCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                    if (IsBleScanning && !cts.IsCancellationRequested)
                    {
                        await RequestBleScanAsync(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    private void WireEvents()
    {
        UsbIp.DiagLog += msg => Log($"[UsbIp] {msg}");
        UsbIp.DriverAttachmentChanged += attached =>
        {
            Log(attached
                ? "Windows BthUsb.sys connected to Virtual Bluetooth 5.2 Adapter!"
                : "Windows BthUsb.sys disconnected.");
            StateChanged?.Invoke();
        };

        LanServer.PhoneRttUpdated += _ => StateChanged?.Invoke();

        LanServer.PhoneConnectionChanged += phone =>
        {
            if (phone is not null)
            {
                string linkKind = phone.IsUsbLink ? "USB Cable (~1ms)" : "Wi-Fi / LAN";
                Log($"Connected to Android '{phone.DeviceName}' ({phone.RemoteIp}) via {linkKind}.");
                _ = LanServer.SendControlMessageAsync(ControlMessageType.AudioConfig, new AudioConfigMessage(
                    CurrentAudioMode, 48000, 2, 5, AudioEngine.IsMicEnabled, CurrentJitterBufferMs));
            }
            else
            {
                Log("Android companion disconnected.");
                IsBleScanning = false;
                AttachedHeadset = null;
                LatestPhoneStatus = null;
                SynthesizedStereoMac = null;
                SynthesizedMicMac = null;
            }
            UpdateAudioCaptureState();
            StateChanged?.Invoke();
        };

        LanServer.BleAdvertisementReceived += adv =>
        {
            BleDeviceDiscovered?.Invoke(adv);
            if (Bumble is not null)
            {
                _ = Bumble.InjectBleAdvertisementAsync(adv);
            }
        };

        LanServer.HeadsetStateReceived += hs =>
        {
            bool wasConnected = AttachedHeadset?.Connected == true;
            AttachedHeadset = hs.Connected ? hs : null;
            if (hs.Connected)
            {
                Log($"Android bridged Bluetooth audio device: '{hs.Name}' ({hs.Mac})");
            }
            else if (wasConnected)
            {
                Log("Bluetooth audio device disconnected on Android.");
            }
            if (Bumble is not null)
            {
                _ = Bumble.UpdateHeadsetStateAsync(hs);
            }
            UpdateAudioCaptureState();
            StateChanged?.Invoke();
        };

        LanServer.MediaControlReceived += mc =>
        {
            EmulateWindowsMediaKey(mc.Key);
            Log($"Executed media key from headset: {mc.Key}");
        };

        LanServer.DeviceStatusReceived += ds =>
        {
            LatestPhoneStatus = ds;
            StateChanged?.Invoke();
        };

        LanServer.PhoneMicOpusFrameReceived += (opusBytes, hdr) =>
        {
            AudioEngine.PushIncomingPhoneMicOpusPacket(hdr.SequenceNumber, opusBytes.Span);
        };

        AudioEngine.EncodedStereoOpusFrameReady += opusBytes =>
        {
            LanServer.SendPcStereoOpusFrame(opusBytes.Span, gamingDirect: CurrentAudioMode == "GamingOpus");
        };
    }

    private void UpdateAudioCaptureState()
    {
        bool shouldStreamDirect = LanServer.CurrentPhone is not null && CurrentAudioMode == "GamingOpus";
        if (shouldStreamDirect && !AudioEngine.IsStreamingSpeaker)
        {
            try
            {
                AudioEngine.StartSpeakerCapture();
                Log("Started WASAPI 48kHz Opus 5ms ultra-low-latency audio stream.");
            }
            catch (Exception ex)
            {
                Log($"WASAPI capture warning: {ex.Message}");
            }
        }
        else if (!shouldStreamDirect && AudioEngine.IsStreamingSpeaker)
        {
            AudioEngine.StopSpeakerCapture();
        }
    }

    private void OnBumbleBleScanRequested(BleScanCmdMessage cmd)
    {
        IsBleScanning = cmd.Enable;
        Log($"Windows requested BLE 2.4GHz Scan: {(cmd.Enable ? "START" : "STOP")}");
        _ = LanServer.SendControlMessageAsync(ControlMessageType.BleScanCmd, cmd);
        StateChanged?.Invoke();
    }

    private void OnBumbleBleAdvRequested(BleAdvCmdMessage cmd)
    {
        Log($"Windows requested BLE 2.4GHz Advertise: {(cmd.Enable ? "START" : "STOP")} ({cmd.AdvDataHex.Length / 2}B)");
        _ = LanServer.SendControlMessageAsync(ControlMessageType.BleAdvCmd, cmd);
    }

    private void Log(string message) =>
        LogAdded?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static string? FindRepoFilePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindRepoToolPath(string relativePath) => FindRepoFilePath(relativePath);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private static void EmulateWindowsMediaKey(string key)
    {
        byte vk = key.ToUpperInvariant() switch
        {
            "PLAY_PAUSE" => 0xB3, // VK_MEDIA_PLAY_PAUSE
            "NEXT" => 0xB0,       // VK_MEDIA_NEXT_TRACK
            "PREV" => 0xB1,       // VK_MEDIA_PREV_TRACK
            "VOL_UP" => 0xAF,     // VK_VOLUME_UP
            "VOL_DOWN" => 0xAE,   // VK_VOLUME_DOWN
            _ => 0
        };
        if (vk == 0) return;
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 0x0002, 0); // KEYEVENTF_KEYUP
    }

    public async ValueTask DisposeAsync()
    {
        AudioEngine.Dispose();
        await UsbipWin2Manager.DetachAllLocalVirtualDevicesAsync();
        await Discovery.DisposeAsync();
        await LanServer.DisposeAsync();
        await UsbIp.DisposeAsync();
        if (Bumble is not null)
        {
            await Bumble.DisposeAsync();
        }
    }
}
