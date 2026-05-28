using System.ComponentModel;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace DocumentKB.Mcp.Tools;

[McpServerToolType]
public sealed class KbFetchChunkTool(KbDbContext db)
{
    [McpServerTool, Description("Fetch a chunk's full content + locator. " +
        "When include_neighbors=true, also returns prev/next chunks in the same file.")]
    public async Task<object> kb_fetch_chunk(
        long chunk_id,
        bool include_neighbors = false,
        CancellationToken ct = default)
    {
        var c = await db.Chunks.Include(x => x.File)
            .FirstOrDefaultAsync(x => x.Id == chunk_id, ct);
        if (c is null)
            return new { error = new { code = "NOT_FOUND", message = $"chunk {chunk_id}" } };

        object? prev = null, next = null;
        if (include_neighbors)
        {
            var prevE = await db.Chunks.AsNoTracking()
                .Where(x => x.FileId == c.FileId && x.Ordinal == c.Ordinal - 1)
                .FirstOrDefaultAsync(ct);
            var nextE = await db.Chunks.AsNoTracking()
                .Where(x => x.FileId == c.FileId && x.Ordinal == c.Ordinal + 1)
                .FirstOrDefaultAsync(ct);
            prev = prevE is null ? null : ToNeighbor(prevE);
            next = nextE is null ? null : ToNeighbor(nextE);
        }

        return new
        {
            chunk_id = c.Id,
            file_id = c.FileId,
            file_name = c.File.FileName,
            relative_path = c.File.RelativePath,
            absolute_path = c.File.AbsolutePath,
            file_type = c.File.FileType.ToString().ToLowerInvariant(),
            title_path = c.TitlePath,
            chunk_type = ChunkTypeToString(c.ChunkType),
            locator = LocatorJson.FromJsonString(c.LocatorJson),
            content_md = c.ContentMd,
            neighbors = include_neighbors ? new { prev, next } : null,
            ingested_at = c.File.IngestedAtUtc
        };
    }

    private static object ToNeighbor(ChunkEntity e) => new
    {
        chunk_id = e.Id,
        title_path = e.TitlePath,
        chunk_type = ChunkTypeToString(e.ChunkType),
        content_md = e.ContentMd
    };

    private static string ChunkTypeToString(ChunkType t) => t switch
    {
        ChunkType.WordSection => "word_section",
        ChunkType.ExcelSheetSummary => "excel_sheet_summary",
        ChunkType.ExcelRows => "excel_rows",
        _ => throw new ArgumentOutOfRangeException()
    };
}
