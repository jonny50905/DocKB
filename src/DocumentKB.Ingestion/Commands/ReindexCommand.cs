using System.CommandLine;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Ingestion;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocumentKB.Ingestion.Commands;

public static class ReindexCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var force = new Option<bool>("--force", () => false, "Ignore SHA, re-convert all");
        var path = new Option<string?>("--path", () => null, "Restrict to relative subfolder");
        var cmd = new Command("reindex", "Scan + ingest changed files");
        cmd.AddOption(force);
        cmd.AddOption(path);
        cmd.SetHandler(async (bool f, string? p) =>
        {
            await using var scope = sp.CreateAsyncScope();
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<KbOptions>>().Value;
            await new MigrationRunner(opts.MariaDb.ConnectionString).ApplyAsync();
            await scope.ServiceProvider.GetRequiredService<KbSearchMapping>().EnsureIndexAsync();

            await using var locker = await AdvisoryLock.TryAcquireAsync(
                opts.MariaDb.ConnectionString, "documentkb.ingestion");
            if (locker is null)
            {
                await Console.Error.WriteLineAsync("Another ingestion is running. Aborting.");
                Environment.Exit(2);
                return;
            }

            var pipe = scope.ServiceProvider.GetRequiredService<IngestionPipeline>();
            var result = await pipe.RunAsync(p, f, default);
            Console.WriteLine($"run_id={result.RunId} scanned={result.Scanned} " +
                $"new={result.New} updated={result.Updated} " +
                $"deleted={result.Deleted} failed={result.Failed}");
        }, force, path);
        return cmd;
    }
}
