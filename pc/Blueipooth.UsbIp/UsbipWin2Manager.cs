using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Blueipooth.UsbIp;

/// <summary>
/// Manages WHQL-signed usbip-win2 installation check, Virtual USB Bluetooth attach, and clean detach.
/// </summary>
public static partial class UsbipWin2Manager
{
    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files\USBip\usbip.exe",
        @"C:\Program Files (x86)\USBip\usbip.exe",
    ];

    public static string? FindUsbipExecutable()
    {
        foreach (string p in KnownPaths)
        {
            if (File.Exists(p)) return p;
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), "usbip.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static bool IsDriverInstalled => FindUsbipExecutable() is not null;

    public static async Task<bool> InstallDriverViaWingetAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("winget")
        {
            Arguments = "install --id vadimgrn.usbip-win2 --accept-package-agreements --accept-source-agreements",
            UseShellExecute = true,
            Verb = "runas",
        };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode == 0 && IsDriverInstalled;
    }

    public static async Task<(bool Success, string Output)> AttachVirtualBluetoothAsync(
        int usbIpPort = 3240,
        CancellationToken ct = default)
    {
        string? exe = FindUsbipExecutable();
        if (exe is null)
        {
            return (false, "Chưa cài đặt driver usbip-win2. Hãy bấm 'Install Signed Driver' trước.");
        }

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = $"attach -r 127.0.0.1 -b {BluetoothUsbDescriptors.BusId} -t {usbIpPort}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return (false, "Failed to launch usbip.exe.");

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode == 0)
        {
            return (true, string.IsNullOrWhiteSpace(stdout) ? "Attached Virtual Bluetooth Adapter." : stdout.Trim());
        }

        // Retry without -t flag if using default port 3240 on older usbip-win2 CLI builds
        if (usbIpPort == 3240)
        {
            psi.Arguments = $"attach -r 127.0.0.1 -b {BluetoothUsbDescriptors.BusId}";
            using var retry = Process.Start(psi);
            if (retry is not null)
            {
                string out2 = await retry.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                string err2 = await retry.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await retry.WaitForExitAsync(ct).ConfigureAwait(false);
                if (retry.ExitCode == 0)
                    return (true, out2.Trim());
                stderr = $"{err2} {out2}".Trim();
            }
        }

        return (false, $"{stderr} {stdout}".Trim());
    }

    public static async Task DetachAllLocalVirtualDevicesAsync(CancellationToken ct = default)
    {
        string? exe = FindUsbipExecutable();
        if (exe is null) return;

        try
        {
            var listPsi = new ProcessStartInfo(exe, "port")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var listProc = Process.Start(listPsi);
            if (listProc is null) return;

            string portOutput = await listProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await listProc.WaitForExitAsync(ct).ConfigureAwait(false);

            foreach (Match m in PortRegex().Matches(portOutput))
            {
                string portNum = m.Groups[1].Value;
                using var detachProc = Process.Start(new ProcessStartInfo(exe, $"detach -p {portNum}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                if (detachProc is not null)
                    await detachProc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [GeneratedRegex(@"Port\s+(\d+):", RegexOptions.IgnoreCase)]
    private static partial Regex PortRegex();
}
