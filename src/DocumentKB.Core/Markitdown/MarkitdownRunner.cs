using System.Diagnostics;
using System.Text;
using DocumentKB.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Markitdown;

public sealed class MarkitdownRunner(
    IOptions<KbOptions> options,
    ILogger<MarkitdownRunner> logger)
{
    public async Task<string> ConvertAsync(string absolutePath, CancellationToken ct)
    {
        var cfg = options.Value;
        var psi = new ProcessStartInfo
        {
            FileName = cfg.PythonExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-m", "markitdown", absolutePath }) psi.ArgumentList.Add(a);
        foreach (var a in cfg.Markitdown.ExtraArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new MarkitdownException("failed to spawn python");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.Markitdown.TimeoutSeconds));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new MarkitdownException("markitdown timeout");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            var snippet = stderr.Length > 500 ? stderr[..500] : stderr;
            logger.LogWarning("markitdown failed for {File}: {Stderr}",
                System.IO.Path.GetFileName(absolutePath), snippet);
            throw new MarkitdownException($"markitdown failed: {snippet}");
        }
        return await stdoutTask;
    }
}
