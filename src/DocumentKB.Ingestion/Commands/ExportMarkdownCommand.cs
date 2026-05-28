using System.CommandLine;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class ExportMarkdownCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var fileId = new Option<long>("--file-id", "files.id to export") { IsRequired = true };
        var cmd = new Command("export-markdown", "Print files.markdown_full to stdout") { fileId };
        cmd.SetHandler(async (long id) =>
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
            var md = await db.Files.Where(f => f.Id == id)
                .Select(f => f.MarkdownFull).FirstOrDefaultAsync();
            if (md is null) { await Console.Error.WriteLineAsync("not found"); Environment.Exit(1); }
            Console.WriteLine(md);
        }, fileId);
        return cmd;
    }
}
