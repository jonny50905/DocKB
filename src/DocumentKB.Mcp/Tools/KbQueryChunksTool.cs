using System.ComponentModel;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Odata;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DocumentKB.Mcp.Tools;

[McpServerToolType]
public sealed class KbQueryChunksTool(KbDbContext db, IOptions<KbOptions> opts)
{
    private static readonly Microsoft.OData.Edm.IEdmModel Model = EdmBuilder.Build();

    [McpServerTool, Description("OData query over Chunks (metadata only; ContentMd hidden). " +
        "Use $expand=File for file details. Use kb_fetch_chunk for full text.")]
    public async Task<object> kb_query_chunks(string odata, CancellationToken ct = default)
    {
        var validator = new OdataQueryValidator(opts.Value.Odata);
        var err = validator.Validate(odata);
        if (err is not null)
            return new { error = new { code = "INVALID_INPUT", message = err } };
        try
        {
            var runner = new OdataQueryRunner(opts.Value.Odata);
            var (q, count) = runner.Apply(
                db.Chunks.AsNoTracking().Include(c => c.File),
                Model, "Chunks", odata);
            var items = await ((IQueryable<ChunkEntity>)q).ToListAsync(ct);
            return count is null
                ? (object)new { value = items }
                : new Dictionary<string, object?>
                {
                    ["value"] = items,
                    ["@odata.count"] = count
                };
        }
        catch (Exception ex)
        {
            return new { error = new { code = "INTERNAL", message = ex.Message } };
        }
    }
}
