using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blueipooth.Protocol.Control;

namespace Blueipooth.UsbIp;

/// <summary>
/// Manages the embedded Python Bumble Bluetooth 5.2 Engine process and its NDJSON Control IPC socket.
/// </summary>
public sealed class BumbleEngineHost : IAsyncDisposable
{
    private readonly string _pythonExePath;
    private readonly string _engineMainScriptPath;
    private Process? _process;
    private TcpClient? _ipcClient;
    private StreamWriter? _ipcWriter;
    private CancellationTokenSource? _cts;

    public int H4Port { get; private set; }
    public int IpcPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public event Action<BleScanCmdMessage>? BleScanRequested;
    public event Action<BleAdvCmdMessage>? BleAdvertiseRequested;
    public event Action<string, string?>? DualMacHeadsetSynthesized;
    public event Action<string>? EngineLogLine;

    public BumbleEngineHost(string pythonExePath, string engineMainScriptPath)
    {
        _pythonExePath = pythonExePath;
        _engineMainScriptPath = engineMainScriptPath;
    }

    public async Task StartAsync(int h4Port = 0, int ipcPort = 0, CancellationToken ct = default)
    {
        if (IsRunning) return;
        H4Port = h4Port;
        IpcPort = ipcPort;
        _cts = new CancellationTokenSource();

        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var psi = new ProcessStartInfo(_pythonExePath)
        {
            Arguments = $"-u \"{_engineMainScriptPath}\" --h4-port {h4Port} --ipc-port {ipcPort}",
            WorkingDirectory = Path.GetDirectoryName(_engineMainScriptPath) ?? Environment.CurrentDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            EngineLogLine?.Invoke(e.Data);
            if (e.Data.Contains("BLUEIPOOTH_BUMBLE_READY", StringComparison.Ordinal))
            {
                Match ports = Regex.Match(e.Data, @"H4=(\d+)\s+IPC=(\d+)");
                if (!ports.Success)
                {
                    readyTcs.TrySetException(new InvalidOperationException(
                        $"Bumble reported ready without valid ports: {e.Data}"));
                    return;
                }

                H4Port = int.Parse(ports.Groups[1].Value);
                IpcPort = int.Parse(ports.Groups[2].Value);
                readyTcs.TrySetResult(true);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                EngineLogLine?.Invoke(e.Data);
        };
        _process.Exited += (_, _) =>
        {
            if (!readyTcs.Task.IsCompleted)
                readyTcs.TrySetException(new InvalidOperationException(
                    $"Bumble engine exited before becoming ready (exit code {_process.ExitCode}). See the [Bumble] traceback in the activity log."));
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        using (timeoutCts.Token.Register(() => readyTcs.TrySetCanceled()))
        {
            await readyTcs.Task.ConfigureAwait(false);
        }

        _ipcClient = new TcpClient { NoDelay = true };
        await _ipcClient.ConnectAsync("127.0.0.1", IpcPort, ct).ConfigureAwait(false);
        _ipcWriter = new StreamWriter(_ipcClient.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        _ = Task.Run(() => ReadIpcLoopAsync(_ipcClient.GetStream(), _cts.Token));
    }

    public async Task InjectBleAdvertisementAsync(BleAdvReportMessage adv)
    {
        await SendIpcJsonAsync(new
        {
            type = "ble_adv_report",
            address = adv.Address,
            addrType = adv.AddrType,
            rssi = adv.Rssi,
            connectable = adv.Connectable,
            dataHex = adv.DataHex
        }).ConfigureAwait(false);
    }

    public async Task UpdateHeadsetStateAsync(HeadsetStateMessage state)
    {
        await SendIpcJsonAsync(new
        {
            type = "headset_state",
            connected = state.Connected,
            name = state.Name,
            mac = state.Mac,
            hasMic = state.HasMic
        }).ConfigureAwait(false);
    }

    public async Task SetAudioModeAsync(string mode)
    {
        await SendIpcJsonAsync(new
        {
            type = "audio_mode",
            mode
        }).ConfigureAwait(false);
    }

    private async Task SendIpcJsonAsync(object payload)
    {
        var writer = _ipcWriter;
        if (writer is null) return;
        string json = JsonSerializer.Serialize(payload);
        try
        {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        catch
        {
            // Engine shutting down
        }
    }

    private async Task ReadIpcLoopAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string? type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;

                switch (type)
                {
                    case "ble_scan_cmd":
                        BleScanRequested?.Invoke(new BleScanCmdMessage(
                            root.GetProperty("enable").GetBoolean(),
                            root.TryGetProperty("filterDuplicates", out var fd) && fd.GetBoolean()));
                        break;
                    case "ble_adv_cmd":
                        BleAdvertiseRequested?.Invoke(new BleAdvCmdMessage(
                            root.GetProperty("enable").GetBoolean(),
                            root.TryGetProperty("advDataHex", out var ad) ? ad.GetString() ?? "" : "",
                            root.TryGetProperty("scanRspHex", out var sr) ? sr.GetString() ?? "" : ""));
                        break;
                    case "dual_mac_synthesized":
                        DualMacHeadsetSynthesized?.Invoke(
                            root.GetProperty("stereoMac").GetString() ?? "",
                            root.TryGetProperty("micMac", out var mm) && mm.ValueKind == JsonValueKind.String
                                ? mm.GetString()
                                : null);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
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

        _ipcWriter?.Dispose();
        _ipcClient?.Dispose();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
        }
        _process?.Dispose();
        _process = null;
    }
}
