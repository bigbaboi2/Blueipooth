using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Web;

namespace Blueipooth.Protocol.Pairing;

public sealed record QrPairingPayload(
    int Version,
    string PcName,
    IReadOnlyList<string> CandidateIps,
    int ControlPort,
    int MediaUdpPort,
    string CertSha256Hex,
    byte[] SessionKeyAes128,
    string OneTimeToken)
{
    public const string Scheme = "blueipooth";

    public string ToUri()
    {
        var nameEnc = Uri.EscapeDataString(PcName);
        var ipsCsv = string.Join(",", CandidateIps);
        var keyB64 = Base64UrlEncode(SessionKeyAes128);
        return $"{Scheme}://pair?v={Version}&name={nameEnc}&ips={Uri.EscapeDataString(ipsCsv)}&port={ControlPort}&uport={MediaUdpPort}&fp={CertSha256Hex}&key={keyB64}&tok={OneTimeToken}";
    }

    public static QrPairingPayload Parse(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Invalid Blueipooth QR pairing URI.");
        }

        var q = HttpUtility.ParseQueryString(uri.Query);
        int version = int.TryParse(q["v"], out var v) ? v : 1;
        string name = q["name"] ?? "PC";
        var ips = (q["ips"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int port = int.TryParse(q["port"], out var p) ? p : 49550;
        int uport = int.TryParse(q["uport"], out var up) ? up : 49551;
        string fp = (q["fp"] ?? "").ToUpperInvariant();
        byte[] key = Base64UrlDecode(q["key"] ?? throw new FormatException("Missing session key."));
        if (key.Length != 16)
        {
            throw new FormatException("AES-128 session key must be 16 bytes.");
        }
        string tok = q["tok"] ?? throw new FormatException("Missing pairing token.");

        return new QrPairingPayload(version, name, ips, port, uport, fp, key, tok);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

public sealed record TrustedPeerRecord(
    string PeerId,
    string DeviceName,
    string ReconnectToken,
    string MediaKeyBase64,
    DateTimeOffset LastSeenUtc);

public static class IdentityCertificateStore
{
    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueipooth");

    public static (X509Certificate2 Certificate, string Sha256Hex) LoadOrCreateServerCertificate()
    {
        Directory.CreateDirectory(AppDataDir);
        string pfxPath = Path.Combine(AppDataDir, "server_identity.pfx");

        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    pfxPath,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(7))
                {
                    return (existing, ComputeSha256Hex(existing));
                }
            }
            catch
            {
                // Regenerate if corrupted
            }
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            $"CN=Blueipooth-{Environment.MachineName}",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, false));

        using var ephemeral = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));

        byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx);
        File.WriteAllBytes(pfxPath, pfxBytes);

        // Reload with PersistKeySet so Windows SChannel (SslStream) can access the private key!
        var persisted = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        return (persisted, ComputeSha256Hex(persisted));
    }

    public static string ComputeSha256Hex(X509Certificate2 cert)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(cert.RawData, hash);
        return Convert.ToHexString(hash);
    }

    public static List<TrustedPeerRecord> LoadTrustedPeers()
    {
        string path = Path.Combine(AppDataDir, "trusted_peers.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TrustedPeerRecord>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveTrustedPeer(TrustedPeerRecord peer)
    {
        Directory.CreateDirectory(AppDataDir);
        string path = Path.Combine(AppDataDir, "trusted_peers.json");
        var list = LoadTrustedPeers();
        list.RemoveAll(p => string.Equals(p.PeerId, peer.PeerId, StringComparison.OrdinalIgnoreCase));
        list.Add(peer);
        File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}
