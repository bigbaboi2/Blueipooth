using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Blueipooth.App.Services;

public sealed record PcMediaSessionSnapshot(
    bool HasSession,
    string SourceApp,
    bool IsPlaying,
    string Title,
    string Artist,
    double PositionSeconds,
    double DurationSeconds,
    DateTime SampledAtUtc,
    byte[]? ThumbnailBytes,
    string TrackKey);

public sealed partial class PcMediaSessionHelper : IDisposable
{
    private Process? _psProcess;
    private readonly CancellationTokenSource _cts = new();
    private byte[]? _cachedThumbBytes;
    private string _cachedTrackKey = "";
    private bool _disposed;

    public PcMediaSessionSnapshot Latest { get; private set; } = new(
        HasSession: false,
        SourceApp: "Browser",
        IsPlaying: false,
        Title: "Chưa có media đang phát",
        Artist: "This PC",
        PositionSeconds: 0,
        DurationSeconds: 0,
        SampledAtUtc: DateTime.UtcNow,
        ThumbnailBytes: null,
        TrackKey: "");

    public void Start()
    {
        _ = Task.Run(() => RunWorkerLoopAsync(_cts.Token));
    }

    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        const string psScript = """
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
            $asStreamMethod = ([System.IO.WindowsRuntimeStreamExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsStreamForRead' -and $_.GetParameters().Count -eq 1 })[0]
            function Await($WinRtTask, $ResultType) {
                $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
                $netTask = $asTask.Invoke($null, @($WinRtTask))
                $netTask.Wait(1500) | Out-Null
                if ($netTask.IsCompleted) { return $netTask.Result }
                return $null
            }
            [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
            [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
            $mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
            $lastKey = ""
            while ($true) {
                try {
                    $session = if ($mgr) { $mgr.GetCurrentSession() } else { $null }
                    if ($session) {
                        $props = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
                        $pb = $session.GetPlaybackInfo()
                        $tl = $session.GetTimelineProperties()
                        $title = if ($props -and $props.Title) { [string]$props.Title } else { "" }
                        $artist = if ($props -and $props.Artist) { [string]$props.Artist } else { "" }
                        $appId = [string]$session.SourceAppUserModelId
                        $status = if ($pb) { [string]$pb.PlaybackStatus } else { "Paused" }
                        $pos = if ($tl) { [double]$tl.Position.TotalSeconds } else { 0.0 }
                        $end = if ($tl) { [double]$tl.EndTime.TotalSeconds } else { 0.0 }
                        $key = "$title|$artist"
                        $thumbB64 = ""
                        if ($key -ne $lastKey -and $props -and $props.Thumbnail) {
                            try {
                                $rtStream = Await ($props.Thumbnail.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
                                if ($rtStream) {
                                    $netStream = $asStreamMethod.Invoke($null, @($rtStream))
                                    $ms = New-Object System.IO.MemoryStream
                                    $netStream.CopyTo($ms)
                                    $thumbB64 = [Convert]::ToBase64String($ms.ToArray())
                                    $ms.Dispose()
                                    $netStream.Dispose()
                                }
                            } catch {}
                            $lastKey = $key
                        } elseif ($key -ne $lastKey) {
                            $lastKey = $key
                        }
                        $obj = @{ ok = $true; app = $appId; status = $status; title = $title; artist = $artist; pos = $pos; dur = $end; thumb = $thumbB64 }
                        Write-Output ($obj | ConvertTo-Json -Compress)
                    } else {
                        $lastKey = ""
                        Write-Output '{"ok":false}'
                    }
                } catch {
                    Write-Output '{"ok":false}'
                }
                Start-Sleep -Milliseconds 650
            }
            """;

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                _psProcess = proc;
                if (proc is null) break;

                while (!ct.IsCancellationRequested && !proc.HasExited)
                {
                    string? line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ParseJsonLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Retry after short delay
            }

            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private void ParseJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
            {
                Latest = new PcMediaSessionSnapshot(
                    HasSession: false,
                    SourceApp: "This PC",
                    IsPlaying: false,
                    Title: "Chưa có media đang phát",
                    Artist: "Mở nhạc hoặc trình duyệt trên PC",
                    PositionSeconds: 0,
                    DurationSeconds: 0,
                    SampledAtUtc: DateTime.UtcNow,
                    ThumbnailBytes: null,
                    TrackKey: "");
                return;
            }

            string rawApp = root.TryGetProperty("app", out var a) ? a.GetString() ?? "" : "";
            string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string artist = root.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "";
            double pos = root.TryGetProperty("pos", out var p) && p.TryGetDouble(out double pv) ? pv : 0;
            double dur = root.TryGetProperty("dur", out var d) && d.TryGetDouble(out double dv) ? dv : 0;
            string thumbB64 = root.TryGetProperty("thumb", out var th) ? th.GetString() ?? "" : "";

            string trackKey = $"{title}|{artist}";
            if (!string.IsNullOrEmpty(thumbB64))
            {
                try { _cachedThumbBytes = Convert.FromBase64String(thumbB64); } catch { }
                _cachedTrackKey = trackKey;
            }
            else if (trackKey != _cachedTrackKey)
            {
                _cachedThumbBytes = null;
                _cachedTrackKey = trackKey;
            }

            Latest = new PcMediaSessionSnapshot(
                HasSession: !string.IsNullOrWhiteSpace(title),
                SourceApp: FormatAppName(rawApp),
                IsPlaying: string.Equals(status, "Playing", StringComparison.OrdinalIgnoreCase),
                Title: string.IsNullOrWhiteSpace(title) ? "Chưa có media đang phát" : title,
                Artist: string.IsNullOrWhiteSpace(artist) ? FormatAppName(rawApp) : artist,
                PositionSeconds: Math.Max(0, pos),
                DurationSeconds: Math.Max(0, dur),
                SampledAtUtc: DateTime.UtcNow,
                ThumbnailBytes: _cachedThumbBytes,
                TrackKey: trackKey);
        }
        catch
        {
        }
    }

    private static string FormatAppName(string rawApp)
    {
        if (string.IsNullOrWhiteSpace(rawApp)) return "Browser";
        string lower = rawApp.ToLowerInvariant();
        if (lower.Contains("chrome") || lower.Contains("msedge") || lower.Contains("edge") ||
            lower.Contains("firefox") || lower.Contains("brave") || lower.Contains("opera") ||
            lower.Contains("vivaldi") || lower.Contains("coccoc") || lower.Contains("browser"))
            return "Browser";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("zune") || lower.Contains("mediaplayer")) return "Media Player";
        string name = System.IO.Path.GetFileNameWithoutExtension(rawApp);
        return string.IsNullOrWhiteSpace(name) ? "This PC" : name;
    }

    public static void TogglePlayPause() => SendMediaVk(0xB3); // VK_MEDIA_PLAY_PAUSE
    public static void SkipNext() => SendMediaVk(0xB0);        // VK_MEDIA_NEXT_TRACK
    public static void SkipPrevious() => SendMediaVk(0xB1);    // VK_MEDIA_PREV_TRACK

    public void SeekToSeconds(double targetSeconds)
    {
        var cur = Latest;
        double clamped = cur.DurationSeconds > 0
            ? Math.Clamp(targetSeconds, 0, cur.DurationSeconds)
            : Math.Max(0, targetSeconds);
        Latest = cur with { PositionSeconds = clamped, SampledAtUtc = DateTime.UtcNow };
        long ticks = (long)(clamped * 10_000_000.0);

        _ = Task.Run(() =>
        {
            try
            {
                string script = $$"""
                    Add-Type -AssemblyName System.Runtime.WindowsRuntime
                    $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
                    function Await($WinRtTask, $ResultType) {
                        $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
                        $netTask = $asTask.Invoke($null, @($WinRtTask))
                        $netTask.Wait(1000) | Out-Null
                        if ($netTask.IsCompleted) { return $netTask.Result }
                        return $null
                    }
                    [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
                    $mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
                    $s = if ($mgr) { $mgr.GetCurrentSession() } else { $null }
                    if ($s) { Await ($s.TryChangePlaybackPositionAsync([long]{{ticks}})) ([bool]) | Out-Null }
                    """;
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {enc}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(1500);
            }
            catch { }
        });
    }

    public void SkipBySeconds(double deltaSeconds)
    {
        var cur = Latest;
        double currentPos = cur.IsPlaying && cur.DurationSeconds > 0
            ? Math.Min(cur.DurationSeconds, cur.PositionSeconds + (DateTime.UtcNow - cur.SampledAtUtc).TotalSeconds)
            : cur.PositionSeconds;
        SeekToSeconds(currentPos + deltaSeconds);
    }

    private volatile int _cachedPcVolumePct = 70;
    private volatile bool _cachedPcMuted;

    public int CachedPcVolumePercent => _cachedPcVolumePct;
    public bool CachedPcMuted => _cachedPcMuted;

    public void RefreshPcVolumeInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var dev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                _cachedPcVolumePct = (int)Math.Round(dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0);
                _cachedPcMuted = dev.AudioEndpointVolume.Mute;
            }
            catch { }
        });
    }

    public void SetPcVolumePercent(int pct)
    {
        int clamped = Math.Clamp(pct, 0, 100);
        _cachedPcVolumePct = clamped;
        _ = Task.Run(() =>
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var dev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                dev.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100.0f;
                if (clamped > 0 && dev.AudioEndpointVolume.Mute)
                {
                    dev.AudioEndpointVolume.Mute = false;
                    _cachedPcMuted = false;
                }
            }
            catch { }
        });
    }

    public void TogglePcMute()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var dev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                bool nextMute = !dev.AudioEndpointVolume.Mute;
                dev.AudioEndpointVolume.Mute = nextMute;
                _cachedPcMuted = nextMute;
            }
            catch { }
        });
    }

    private static void SendMediaVk(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, 0);
            keybd_event(vk, 0, 0x0002, 0);
        }
        catch { }
    }

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            if (_psProcess is { HasExited: false })
            {
                _psProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _psProcess?.Dispose();
        _cts.Dispose();
    }
}
