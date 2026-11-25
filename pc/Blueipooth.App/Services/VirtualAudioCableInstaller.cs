using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Blueipooth.Audio;

namespace Blueipooth.App.Services;

public static class VirtualAudioCableInstaller
{
    private const string PackageUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    public static bool IsInstalled => WasapiOpusAudioEngine.IsVirtualMicCableInstalled;

    public static async Task<(bool Success, string Message)> InstallAsync(CancellationToken ct = default)
    {
        if (IsInstalled) return (true, "VB-CABLE is already installed.");

        string installRoot = Path.Combine(Path.GetTempPath(), $"Blueipooth-VBCABLE-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installRoot);
        string archivePath = Path.Combine(installRoot, "VBCABLE_Driver_Pack45.zip");
        string extractPath = Path.Combine(installRoot, "package");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(archivePath))
                await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);

            ZipFile.ExtractToDirectory(archivePath, extractPath);
            string? setupExe = Directory
                .EnumerateFiles(extractPath, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (setupExe is null)
                return (false, "The official VB-CABLE package did not contain its 64-bit installer.");

            var psi = new ProcessStartInfo(setupExe)
            {
                Arguments = "-i -h",
                WorkingDirectory = Path.GetDirectoryName(setupExe)!,
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(psi);
            if (process is null) return (false, "Could not start the VB-CABLE installer.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (IsInstalled)
                return (true, "VB-CABLE installed. Restart Windows if the installer requests it.");

            return (false, process.ExitCode == 0
                ? "VB-CABLE installer finished; restart Windows if requested, then check again."
                : $"VB-CABLE installer exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Windows administrator approval was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"VB-CABLE setup failed: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(installRoot, recursive: true); } catch { }
        }
    }
}
