using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using DocumentKB.Core.Ingestion;
using DocumentKB.Core.Markitdown;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using DocumentKB.Ingestion.Commands;
using Elastic.Clients.Elasticsearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System.CommandLine;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddEnvironmentVariables(prefix: "DOCUMENTKB_");

builder.Services.Configure<KbOptions>(builder.Configuration);
builder.Services.AddDbContextPool<KbDbContext>((sp, o) =>
{
    var cs = builder.Configuration.GetSection("MariaDb:ConnectionString").Value!;
    o.UseMySql(cs, ServerVersion.AutoDetect(cs));
});
builder.Services.AddSingleton<ElasticsearchClient>(sp =>
{
    var uri = builder.Configuration.GetSection("ElasticSearch:Uri").Value!;
    return new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(uri)));
});
builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<DecryptionRunner>();
builder.Services.AddSingleton<MarkitdownRunner>();
builder.Services.AddSingleton<WordChunker>(sp =>
    new WordChunker(sp.GetRequiredService<IOptions<KbOptions>>().Value.Chunking));
builder.Services.AddSingleton<ExcelChunker>(sp =>
    new ExcelChunker(sp.GetRequiredService<IOptions<KbOptions>>().Value.Chunking));
builder.Services.AddSingleton<KbSearchMapping>();
builder.Services.AddSingleton<KbSearchClient>();
builder.Services.AddScoped<IngestionPipeline>();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File("logs/ingestion-.log", rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 14)
    .CreateLogger();
builder.Services.AddSerilog();

using var host = builder.Build();

var root = new RootCommand("DocumentKB ingestion CLI");
root.AddCommand(ReindexCommand.Build(host.Services));
root.AddCommand(DoctorCommand.Build(host.Services));
return await root.InvokeAsync(args);
