using System.CommandLine;
using System.Diagnostics;
using DocumentKB.Core.Configuration;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace DocumentKB.Ingestion.Commands;

public static class DoctorCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var cmd = new Command("doctor", "Check DB / ES / Python / pwsh + decrypt script");
        cmd.SetHandler(async () =>
        {
            var opts = sp.GetRequiredService<IOptions<KbOptions>>().Value;
            var fail = 0;
            fail += await Check("MariaDB", async () =>
            {
                await using var c = new MySqlConnection(opts.MariaDb.ConnectionString);
                await c.OpenAsync();
            });
            fail += await Check("ElasticSearch", async () =>
            {
                var es = sp.GetRequiredService<ElasticsearchClient>();
                var p = await es.PingAsync();
                if (!p.IsValidResponse) throw new InvalidOperationException("ping failed");
            });
            fail += await Check("Python markitdown", () => RunOk(opts.PythonExe, "-m", "markitdown", "--version"));
            if (opts.Decryption.Enabled)
            {
                fail += await Check("pwsh", () => RunOk(opts.Decryption.PwshExe, "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major"));
                fail += await Check("Decrypt script exists", () =>
                {
                    if (!File.Exists(opts.Decryption.ScriptPath))
                        throw new FileNotFoundException(opts.Decryption.ScriptPath);
                    return Task.CompletedTask;
                });
            }
            Environment.Exit(fail == 0 ? 0 : 1);
        });
        return cmd;
    }

    private static async Task<int> Check(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine($"[ OK ] {name}"); return 0; }
        catch (Exception ex) { Console.WriteLine($"[FAIL] {name}: {ex.Message}"); return 1; }
    }

    private static async Task RunOk(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("spawn failed");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exit={p.ExitCode}");
    }
}
