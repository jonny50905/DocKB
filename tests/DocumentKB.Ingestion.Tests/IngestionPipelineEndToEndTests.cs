using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using DocumentKB.Core.Ingestion;
using DocumentKB.Core.Markitdown;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Elasticsearch;
using Testcontainers.MariaDb;
using Xunit;

namespace DocumentKB.Ingestion.Tests;

public class IngestionPipelineEndToEndTests : IAsyncLifetime
{
    private readonly MariaDbContainer _db =
        new MariaDbBuilder("mariadb:11").Build();
    private readonly ElasticsearchContainer _es =
        new ElasticsearchBuilder("docker.elastic.co/elasticsearch/elasticsearch:9.0.1").Build();
    private string _sampleDir = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _es.StartAsync());
        _sampleDir = Path.Combine(Path.GetTempPath(), "kb-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sampleDir);
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_db.DisposeAsync().AsTask(), _es.DisposeAsync().AsTask());
        if (Directory.Exists(_sampleDir)) Directory.Delete(_sampleDir, true);
    }

    [Fact]
    public async Task ReindexEmptyFolder_CompletesWithZeroCounts()
    {
        var opts = MakeOpts();
        await new MigrationRunner(opts.MariaDb.ConnectionString).ApplyAsync();
        await using var ctx = MakeCtx(opts);

        var settings = new ElasticsearchClientSettings(new Uri(opts.ElasticSearch.Uri))
            .ServerCertificateValidationCallback((_, _, _, _) => true);
        var es = new ElasticsearchClient(settings);
        await new KbSearchMapping(es, Options.Create(opts)).EnsureIndexAsync();

        var pipe = MakePipeline(ctx, es, opts);
        var result = await pipe.RunAsync(null, force: false, default);
        result.Scanned.Should().Be(0);
        result.New.Should().Be(0);
        result.Failed.Should().Be(0);
    }

    private KbOptions MakeOpts() => new()
    {
        SourceFolder = _sampleDir,
        PythonExe = "python",
        Decryption = new DecryptionOptions { Enabled = false },
        MariaDb = new MariaDbOptions { ConnectionString = _db.GetConnectionString() },
        ElasticSearch = new ElasticSearchOptions { Uri = _es.GetConnectionString(), Index = "kb_chunks" }
    };

    private static KbDbContext MakeCtx(KbOptions opts)
    {
        var b = new DbContextOptionsBuilder<KbDbContext>();
        b.UseMySql(opts.MariaDb.ConnectionString, ServerVersion.AutoDetect(opts.MariaDb.ConnectionString));
        return new KbDbContext(b.Options);
    }

    private static IngestionPipeline MakePipeline(KbDbContext ctx, ElasticsearchClient es, KbOptions opts)
        => new(Options.Create(opts), ctx,
            new FileScanner(),
            new DecryptionRunner(Options.Create(opts), NullLogger<DecryptionRunner>.Instance),
            new MarkitdownRunner(Options.Create(opts), NullLogger<MarkitdownRunner>.Instance),
            new WordChunker(opts.Chunking),
            new ExcelChunker(opts.Chunking),
            new KbSearchClient(es, Options.Create(opts)),
            NullLogger<IngestionPipeline>.Instance);
}
