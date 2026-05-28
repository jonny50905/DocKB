using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Search;

public sealed record KbDoc(
    long chunk_id,
    long file_id,
    string file_name,
    string relative_path,
    string file_type,
    string? title_path,
    string content,
    string chunk_type,
    DateTime ingested_at);

public sealed class KbSearchClient(
    ElasticsearchClient client,
    IOptions<KbOptions> options)
{
    private string Idx => options.Value.ElasticSearch.Index;

    public async Task BulkUpsertAsync(IEnumerable<KbDoc> docs, CancellationToken ct)
    {
        // Use non-generic BulkAsync so BulkRequestDescriptor.IndexMany<TSource> is available
        var resp = await client.BulkAsync(Idx,
            b => b.IndexMany(docs, (d, doc) => d.Id(doc.chunk_id.ToString())), ct);
        if (resp.Errors)
        {
            var first = resp.Items.FirstOrDefault(i => i.Error is not null)?.Error?.Reason;
            throw new InvalidOperationException($"bulk index errors: {first}");
        }
    }

    public async Task DeleteByFileIdAsync(long fileId, CancellationToken ct)
    {
        await client.DeleteByQueryAsync<KbDoc>(Idx,
            q => q.Query(qq => qq.Term(t => t.Field("file_id").Value(fileId))), ct);
    }

    public static KbDoc ToDoc(FileEntity f, ChunkEntity c)
        => new(
            c.Id,
            f.Id,
            f.FileName,
            f.RelativePath,
            f.FileType.ToString().ToLowerInvariant(),
            c.TitlePath,
            c.ContentMd,
            c.ChunkType switch
            {
                ChunkType.WordSection        => "word_section",
                ChunkType.ExcelSheetSummary  => "excel_sheet_summary",
                ChunkType.ExcelRows          => "excel_rows",
                _                            => throw new ArgumentOutOfRangeException()
            },
            f.IngestedAtUtc);
}
