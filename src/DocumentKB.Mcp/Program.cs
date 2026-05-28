using DocumentKB.Core.Configuration;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
var appsettings = Environment.GetEnvironmentVariable("DOCUMENTKB_APPSETTINGS")
    ?? "appsettings.json";
builder.Configuration.AddJsonFile(appsettings, optional: false);
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
builder.Services.AddSingleton<KbSearchClient>();

// CRITICAL: stdout is reserved for MCP protocol. NEVER write logs to stdout.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.File("logs/mcp-.log", rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 14)
    .CreateLogger();
builder.Services.AddSerilog();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
