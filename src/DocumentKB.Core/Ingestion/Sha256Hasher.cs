using System.Security.Cryptography;

namespace DocumentKB.Core.Ingestion;

public static class Sha256Hasher
{
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
