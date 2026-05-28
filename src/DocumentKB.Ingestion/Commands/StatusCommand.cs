using System.CommandLine;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class StatusCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var cmd = new Command("status", "Show KB health summary");
        cmd.SetHandler(async () =>
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
            var active = await db.Files.CountAsync(f => f.Status == FileStatus.Active);
            var failed = await db.Files.CountAsync(f => f.Status == FileStatus.Failed);
            var chunks = await db.Chunks.CountAsync();
            var pending = await db.Chunks.CountAsync(c => c.EsIndexedAt == null);
            var last = await db.IngestionRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            Console.WriteLine($"files_active={active} files_failed={failed} " +
                $"chunks_total={chunks} chunks_pending_es={pending}");
            if (last is not null)
            {
                Console.WriteLine($"last_run id={last.Id} started={last.StartedAtUtc:O} " +
                    $"finished={last.FinishedAtUtc:O} new={last.NewCount} " +
                    $"updated={last.UpdatedCount} deleted={last.DeletedCount} " +
                    $"failed={last.FailedCount}");
            }
            if (failed > 0)
            {
                Console.WriteLine("\nfailed_files:");
                await foreach (var f in db.Files.AsNoTracking()
                    .Where(f => f.Status == FileStatus.Failed)
                    .Select(f => new { f.RelativePath, f.LastError })
                    .AsAsyncEnumerable())
                    Console.WriteLine($"  - {f.RelativePath} :: {f.LastError}");
            }
        });
        return cmd;
    }
}
