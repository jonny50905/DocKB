using System.ComponentModel;
using DocumentKB.Core.Search;
using ModelContextProtocol.Server;

namespace DocumentKB.Mcp.Tools;

[McpServerToolType]
public sealed class KbSearchTool(KbSearchClient client)
{
    [McpServerTool, Description("Full-text BM25 search over Word/Excel KB chunks. " +
        "Returns chunk_id list with snippet preview. Use kb_fetch_chunk to read full content.")]
    public async Task<object> kb_search(
        [Description("natural language query or keywords")] string query,
        [Description("max hits, default 8, max 30")] int top_k = 8,
        [Description("'word' | 'excel' | null")] string? file_type = null,
        [Description("relative path prefix filter, e.g. \"財務/2024/\"")] string? path_prefix = null,
        [Description("BM25 minimum score threshold")] double? min_score = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new { error = new { code = "INVALID_INPUT", message = "query required" } };
        top_k = Math.Clamp(top_k, 1, 30);
        try
        {
            var resp = await client.SearchAsync(query, top_k, file_type, path_prefix, min_score, ct);
            return new
            {
                hits = resp.Hits.Select(h => new
                {
                    chunk_id = h.ChunkId, file_id = h.FileId,
                    file_name = h.FileName, relative_path = h.RelativePath,
                    file_type = h.FileType, title_path = h.TitlePath,
                    chunk_type = h.ChunkType, score = h.Score, snippet = h.Snippet
                }),
                total_matched = resp.TotalMatched, took_ms = resp.TookMs,
                query_echo = resp.QueryEcho
            };
        }
        catch (Exception ex)
        {
            return new { error = new { code = "ES_UNAVAILABLE", message = ex.Message } };
        }
    }
}
