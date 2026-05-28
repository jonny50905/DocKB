using DocumentKB.Core.Configuration;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Transport;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Search;

public sealed class KbSearchMapping(
    ElasticsearchClient client,
    IOptions<KbOptions> options)
{
    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var idx = options.Value.ElasticSearch.Index;
        var exists = await client.Indices.ExistsAsync(idx, ct);
        if (exists.Exists) return;

        // Use raw JSON via Transport.RequestAsync (9.x: ref EndpointPath)
        var json = $$"""
        {
          "settings": {
            "analysis": {
              "analyzer": {
                "kb_text": {
                  "type": "custom",
                  "tokenizer": "standard",
                  "filter": ["lowercase", "cjk_bigram"]
                }
              }
            }
          },
          "mappings": {
            "properties": {
              "chunk_id":     { "type": "long" },
              "file_id":      { "type": "long" },
              "file_name":    { "type": "text", "analyzer": "kb_text",
                                "fields": { "raw": { "type": "keyword" } } },
              "relative_path": { "type": "keyword" },
              "file_type":    { "type": "keyword" },
              "title_path":   { "type": "text", "analyzer": "kb_text",
                                "fields": { "raw": { "type": "keyword" } } },
              "content":      { "type": "text", "analyzer": "kb_text" },
              "chunk_type":   { "type": "keyword" },
              "ingested_at":  { "type": "date" }
            }
          }
        }
        """;

        // Elastic.Transport 9.x: RequestAsync takes EndpointPath by ref
        var path = new EndpointPath(Elastic.Transport.HttpMethod.PUT, $"/{idx}");
        var resp = await client.Transport.RequestAsync<StringResponse>(
            in path, PostData.String(json), null, null, ct);

        if (!resp.ApiCallDetails.HasSuccessfulStatusCode)
            throw new InvalidOperationException(
                $"create index failed: {resp.ApiCallDetails.OriginalException?.Message ?? "non-2xx"}");
    }
}
