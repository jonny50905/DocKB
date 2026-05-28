using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
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

    public async Task<KbSearchResponse> SearchAsync(
        string query, int topK, string? fileType, string? pathPrefix,
        double? minScore, CancellationToken ct)
    {
        var filters = new List<Query>();
        if (!string.IsNullOrEmpty(fileType))
            filters.Add(new TermQuery { Field = "file_type", Value = fileType });
        if (!string.IsNullOrEmpty(pathPrefix))
            filters.Add(new PrefixQuery { Field = "relative_path", Value = pathPrefix });

        var boolQuery = new BoolQuery
        {
            Must = new List<Query>
            {
                new MultiMatchQuery
                {
                    Query = query,
                    Fields = new[] { "content^3", "title_path^2", "file_name" }
                }
            },
            Filter = filters.Count > 0 ? filters : null
        };

        var highlight = new Highlight
        {
            Fields = new Dictionary<Field, HighlightField>
            {
                [new Field("content")] = new HighlightField
                {
                    PreTags = new[] { "<em>" },
                    PostTags = new[] { "</em>" },
                    NumberOfFragments = 2,
                    FragmentSize = 120
                }
            }
        };

        Query boolAsQuery = boolQuery;

        var resp = await client.SearchAsync<KbDoc>(s => s
            .Indices(Idx)
            .Size(topK)
            .TrackTotalHits(new TrackHits(true))
            .Query(boolAsQuery)
            .Highlight(highlight),
            ct);

        var hits = resp.Hits
            .Where(h => minScore is null || (h.Score ?? 0) >= minScore)
            .Select(h => new KbHit(
                h.Source!.chunk_id, h.Source.file_id, h.Source.file_name,
                h.Source.relative_path, h.Source.file_type, h.Source.title_path,
                h.Source.chunk_type, h.Score ?? 0,
                h.Highlight?.GetValueOrDefault("content")?.FirstOrDefault() ?? ""))
            .ToList();

        return new KbSearchResponse(hits, resp.Total, resp.Took, query);
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

public sealed record KbHit(
    long ChunkId, long FileId, string FileName, string RelativePath,
    string FileType, string? TitlePath, string ChunkType,
    double Score, string Snippet);

public sealed record KbSearchResponse(
    IReadOnlyList<KbHit> Hits, long TotalMatched, long TookMs, string QueryEcho);
