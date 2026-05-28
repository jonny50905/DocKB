using System.Diagnostics;
using DocumentKB.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Decryption;

public sealed class DecryptionRunner(
    IOptions<KbOptions> options,
    ILogger<DecryptionRunner> logger)
{
    public async Task<TempFile> DecryptAsync(string absolutePath, CancellationToken ct)
    {
        var cfg = options.Value.Decryption;
        if (!cfg.Enabled) return TempFile.Passthrough(absolutePath);

        var tempRoot = cfg.TempRoot
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "documentkb-decrypt");
        var subDir = System.IO.Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var outPath = System.IO.Path.Combine(subDir, System.IO.Path.GetFileName(absolutePath));

        var psi = new ProcessStartInfo
        {
            FileName = cfg.PwshExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", cfg.ScriptPath,
            "-InputPath", absolutePath,
            "-OutputPath", outPath })
            psi.ArgumentList.Add(a);
        foreach (var a in cfg.ExtraArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new DecryptionException("failed to spawn pwsh");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            SafeDelete(subDir);
            throw new DecryptionException("decrypt timeout");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            SafeDelete(subDir);
            var snippet = stderr.Length > 500 ? stderr[..500] : stderr;
            logger.LogWarning("Decryption failed for {File}: exit={Exit}, stderr={Stderr}",
                System.IO.Path.GetFileName(absolutePath), proc.ExitCode, snippet);
            throw new DecryptionException($"decrypt failed: {snippet}");
        }

        return TempFile.Owned(outPath, subDir);
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
