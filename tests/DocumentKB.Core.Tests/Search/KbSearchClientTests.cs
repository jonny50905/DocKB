using DocumentKB.Core.Configuration;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Testcontainers.Elasticsearch;

namespace DocumentKB.Core.Tests.Search;

public class KbSearchClientTests : IAsyncLifetime
{
    // Use ES 9.x image to match Elastic.Clients.Elasticsearch 9.x (which sends compatible-with=9).
    // ES 8.x rejects this header; ES 9.x accepts it.
    private readonly ElasticsearchContainer _es =
        new ElasticsearchBuilder("docker.elastic.co/elasticsearch/elasticsearch:9.0.1")
            .Build();

    private ElasticsearchClient _client = null!;
    private KbOptions _opts = null!;

    public async Task InitializeAsync()
    {
        await _es.StartAsync();
        // ES 9.x uses HTTPS with a self-signed cert; bypass SSL validation for tests.
        var settings = new ElasticsearchClientSettings(new Uri(_es.GetConnectionString()))
            .DisableDirectStreaming()
            .ServerCertificateValidationCallback((_, _, _, _) => true);
        _client = new ElasticsearchClient(settings);
        _opts = new KbOptions
        {
            ElasticSearch = new ElasticSearchOptions
            {
                Uri = _es.GetConnectionString(),
                Index = "kb_chunks"
            }
        };
    }

    public async Task DisposeAsync() => await _es.DisposeAsync();

    [Fact]
    public async Task EnsureIndex_ThenBulkUpsert_ThenSearchFinds()
    {
        var map = new KbSearchMapping(_client, Options.Create(_opts));
        await map.EnsureIndexAsync();

        var sut = new KbSearchClient(_client, Options.Create(_opts));
        await sut.BulkUpsertAsync(new[]
        {
            new KbDoc(1, 77, "x.docx", "x.docx", "word", "Intro",
                "Hello 訂單 World", "word_section", DateTime.UtcNow),
        }, default);

        await _client.Indices.RefreshAsync("kb_chunks");

        var resp = await _client.SearchAsync<KbDoc>(s => s
            .Indices("kb_chunks")
            .Query(q => q.Match(m => m.Field("content").Query("訂單"))));

        resp.Hits.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteByFileId_RemovesAllChunksForThatFile()
    {
        var map = new KbSearchMapping(_client, Options.Create(_opts));
        await map.EnsureIndexAsync();

        var sut = new KbSearchClient(_client, Options.Create(_opts));
        await sut.BulkUpsertAsync(new[]
        {
            new KbDoc(1, 77, "x.docx", "x.docx", "word", "A", "a", "word_section", DateTime.UtcNow),
            new KbDoc(2, 77, "x.docx", "x.docx", "word", "B", "b", "word_section", DateTime.UtcNow),
        }, default);

        await _client.Indices.RefreshAsync("kb_chunks");

        await sut.DeleteByFileIdAsync(77, default);

        await _client.Indices.RefreshAsync("kb_chunks");

        var resp = await _client.CountAsync<KbDoc>(c => c
            .Indices("kb_chunks")
            .Query(q => q.Term(t => t.Field("file_id").Value(77))));

        resp.Count.Should().Be(0);
    }
}
