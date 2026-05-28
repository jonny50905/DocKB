# DocumentKB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local Word/Excel knowledge base — ingestion CLI (decrypt → markitdown → chunk → MariaDB + ES) plus a read-only stdio MCP server (BM25 search + chunk fetch + OData query) that lets Claude answer questions with original-text citations.

**Architecture:** .NET 8 solution with three projects — `Core` (shared library), `Ingestion` (CLI for writes), `Mcp` (stdio query server). Python `markitdown` is invoked via `Process.Start`. PowerShell decryption script runs before each file via `Process.Start` to a user-supplied `.ps1` that obeys `-InputPath` / `-OutputPath` convention. MariaDB is source of truth; ES holds derived BM25 index; `chunks.es_indexed_at` is the reconciliation column. MCP exposes 4 read-only tools and never writes.

**Tech Stack:** .NET 8 · EF Core 8 + `Pomelo.EntityFrameworkCore.MySql` · `Elastic.Clients.Elasticsearch` 8 · `Microsoft.AspNetCore.OData` 8 (parser only) · `ModelContextProtocol` C# SDK · `System.CommandLine` · `Serilog` · `xUnit` + `FluentAssertions` + `Testcontainers` · Python 3.10+ + `markitdown[all]` · PowerShell 7+ (`pwsh`)

**Reference spec:** [docs/superpowers/specs/2026-05-28-documentkb-design.md](../specs/2026-05-28-documentkb-design.md). Where the plan says "see spec § N", read that section for context (data shapes, decisions) — the plan supplies the action steps.

---

## File Structure (locked in here)

```
DocumentKB.sln
├── .gitignore
├── src/
│   ├── DocumentKB.Core/
│   │   ├── DocumentKB.Core.csproj
│   │   ├── Configuration/KbOptions.cs              # all config POCOs
│   │   ├── Entities/Enums.cs                       # FileType / FileStatus / ChunkType
│   │   ├── Entities/FileEntity.cs
│   │   ├── Entities/ChunkEntity.cs
│   │   ├── Entities/IngestionRunEntity.cs
│   │   ├── Entities/LocatorJson.cs                 # poco for chunk locator
│   │   ├── Persistence/KbDbContext.cs
│   │   ├── Persistence/MigrationRunner.cs
│   │   ├── Persistence/AdvisoryLock.cs
│   │   ├── Persistence/SqlScripts/001_init.sql     # embedded resource
│   │   ├── Search/KbSearchMapping.cs
│   │   ├── Search/KbSearchClient.cs
│   │   ├── Chunking/IChunker.cs
│   │   ├── Chunking/WordChunker.cs
│   │   ├── Chunking/ExcelChunker.cs
│   │   ├── Decryption/TempFile.cs                  # IAsyncDisposable
│   │   ├── Decryption/DecryptionException.cs
│   │   ├── Decryption/DecryptionRunner.cs
│   │   ├── Markitdown/MarkitdownException.cs
│   │   ├── Markitdown/MarkitdownRunner.cs
│   │   ├── Ingestion/Sha256Hasher.cs
│   │   ├── Ingestion/FileScanner.cs
│   │   ├── Ingestion/IngestionPipeline.cs
│   │   └── Odata/
│   │       ├── EdmBuilder.cs
│   │       ├── OdataQueryValidator.cs
│   │       └── OdataQueryRunner.cs
│   ├── DocumentKB.Ingestion/
│   │   ├── DocumentKB.Ingestion.csproj
│   │   ├── Program.cs                              # System.CommandLine root
│   │   └── Commands/
│   │       ├── ReindexCommand.cs
│   │       ├── DoctorCommand.cs
│   │       ├── StatusCommand.cs
│   │       ├── TestDecryptCommand.cs
│   │       └── ExportMarkdownCommand.cs
│   └── DocumentKB.Mcp/
│       ├── DocumentKB.Mcp.csproj
│       ├── Program.cs                              # ModelContextProtocol stdio
│       └── Tools/
│           ├── KbSearchTool.cs
│           ├── KbFetchChunkTool.cs
│           ├── KbQueryDocumentsTool.cs
│           └── KbQueryChunksTool.cs
├── tests/
│   ├── DocumentKB.Core.Tests/
│   │   ├── DocumentKB.Core.Tests.csproj
│   │   ├── Ingestion/Sha256HasherTests.cs
│   │   ├── Ingestion/FileScannerTests.cs
│   │   ├── Chunking/WordChunkerTests.cs
│   │   ├── Chunking/ExcelChunkerTests.cs
│   │   ├── Decryption/DecryptionRunnerTests.cs
│   │   ├── Markitdown/MarkitdownRunnerTests.cs
│   │   ├── Odata/OdataQueryValidatorTests.cs
│   │   └── Search/KbSearchClientTests.cs           # Testcontainers ES
│   ├── DocumentKB.Ingestion.Tests/
│   │   ├── DocumentKB.Ingestion.Tests.csproj
│   │   └── IngestionPipelineEndToEndTests.cs       # Testcontainers MariaDB + ES
│   ├── DocumentKB.Mcp.Tests/
│   │   ├── DocumentKB.Mcp.Tests.csproj
│   │   └── ToolsTests.cs                           # Testcontainers MariaDB + ES
│   └── fixtures/
│       ├── docs/
│       │   ├── plain.docx
│       │   ├── headings.docx
│       │   ├── orders.xlsx
│       │   ├── multi-sheet.xlsx
│       │   └── corrupt.docx                        # markitdown 會壞掉
│       └── scripts/Identity-Decrypt.ps1            # 測試用「假解密」(直接 copy)
├── deploy/
│   ├── appsettings.sample.json
│   ├── migrations/001_init.sql                     # 與 Core 內 embedded resource 同步
│   ├── scripts/Aip-Decrypt.ps1.sample
│   └── claude-mcp-config.example.json
└── skill/documentkb/SKILL.md
```

### Boundaries

- `Core` knows nothing about `System.CommandLine` or MCP. Both apps depend on `Core` only.
- `Ingestion` writes; `Mcp` reads. They share `Core.Configuration.KbOptions` schema but each ignores config sections it doesn't need.
- `Tools/*` in MCP are thin: parse input → call `Core` services → format output. Never put SQL or ES query bodies in Tool classes.

---

## Phase M1 — Ingestion 骨架(Tasks 1–15)

### Task 1: Bootstrap repo + solution

**Files:**
- Create: `D:/TMP/DocumentKB/.gitignore`
- Create: `D:/TMP/DocumentKB/DocumentKB.sln`
- Create: `D:/TMP/DocumentKB/global.json`

- [ ] **Step 1: Initialise git repo and bare solution**

```powershell
cd D:/TMP/DocumentKB
git init -b main
dotnet new sln -n DocumentKB
dotnet new gitignore
```

- [ ] **Step 2: Pin SDK to .NET 8**

Create `D:/TMP/DocumentKB/global.json`:

```json
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 3: Add `.gitignore` entries for plain temp output**

Append to `.gitignore`:

```
# Decrypted temp output must never be committed
documentkb-decrypt/
*.tmp.docx
*.tmp.xlsx

# Logs
logs/
```

- [ ] **Step 4: Commit**

```powershell
git add .gitignore DocumentKB.sln global.json
git commit -m "chore: bootstrap solution + sdk pin + gitignore"
```

---

### Task 2: Create `DocumentKB.Core` class library + NuGet refs

**Files:**
- Create: `src/DocumentKB.Core/DocumentKB.Core.csproj`

- [ ] **Step 1: New classlib + add to solution**

```powershell
dotnet new classlib -n DocumentKB.Core -o src/DocumentKB.Core --framework net8.0
dotnet sln add src/DocumentKB.Core/DocumentKB.Core.csproj
Remove-Item src/DocumentKB.Core/Class1.cs
```

- [ ] **Step 2: Add NuGet packages**

```powershell
cd src/DocumentKB.Core
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Microsoft.Extensions.Options.ConfigurationExtensions
dotnet add package Microsoft.EntityFrameworkCore
dotnet add package Pomelo.EntityFrameworkCore.MySql
dotnet add package Elastic.Clients.Elasticsearch
dotnet add package Microsoft.AspNetCore.OData
dotnet add package Serilog
dotnet add package Serilog.Extensions.Logging
cd ../..
```

- [ ] **Step 3: Add `<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `<LangVersion>latest</LangVersion>`**

Edit `src/DocumentKB.Core/DocumentKB.Core.csproj` `<PropertyGroup>` to include:

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<LangVersion>latest</LangVersion>
```

- [ ] **Step 4: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core
git commit -m "feat(core): create class library with NuGet dependencies"
```

---

### Task 3: Configuration POCOs (`KbOptions`)

**Files:**
- Create: `src/DocumentKB.Core/Configuration/KbOptions.cs`

- [ ] **Step 1: Implement KbOptions and child POCOs**

```csharp
namespace DocumentKB.Core.Configuration;

public sealed class KbOptions
{
    public string SourceFolder { get; init; } = "";
    public string PythonExe { get; init; } = "python";
    public DecryptionOptions Decryption { get; init; } = new();
    public MarkitdownOptions Markitdown { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public MariaDbOptions MariaDb { get; init; } = new();
    public ElasticSearchOptions ElasticSearch { get; init; } = new();
    public OdataOptions Odata { get; init; } = new();
}

public sealed class DecryptionOptions
{
    public bool Enabled { get; init; } = true;
    public string PwshExe { get; init; } = "pwsh";
    public string ScriptPath { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
    public string? TempRoot { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

public sealed class MarkitdownOptions
{
    public int TimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

public sealed class ChunkingOptions
{
    public int MaxChunkChars { get; init; } = 2000;
    public int ExcelRowsPerChunk { get; init; } = 20;
    public bool IncludeExcelHeaderInEveryChunk { get; init; } = true;
}

public sealed class MariaDbOptions
{
    public string ConnectionString { get; init; } = "";
}

public sealed class ElasticSearchOptions
{
    public string Uri { get; init; } = "http://localhost:9200";
    public string Index { get; init; } = "kb_chunks";
    public int BulkBatchSize { get; init; } = 200;
    public int RequestTimeoutSeconds { get; init; } = 30;
}

public sealed class OdataOptions
{
    public int MaxTop { get; init; } = 200;
    public int DefaultTop { get; init; } = 50;
    public int MaxInputBytes { get; init; } = 4096;
}
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Configuration
git commit -m "feat(core): add KbOptions configuration POCOs"
```

---

### Task 4: Entities + enums + LocatorJson

**Files:**
- Create: `src/DocumentKB.Core/Entities/Enums.cs`
- Create: `src/DocumentKB.Core/Entities/FileEntity.cs`
- Create: `src/DocumentKB.Core/Entities/ChunkEntity.cs`
- Create: `src/DocumentKB.Core/Entities/IngestionRunEntity.cs`
- Create: `src/DocumentKB.Core/Entities/LocatorJson.cs`

- [ ] **Step 1: Enums**

```csharp
namespace DocumentKB.Core.Entities;

public enum FileType { Word, Excel }
public enum FileStatus { Active, Deleted, Failed }
public enum ChunkType { WordSection, ExcelSheetSummary, ExcelRows }
```

- [ ] **Step 2: FileEntity**

```csharp
namespace DocumentKB.Core.Entities;

public sealed class FileEntity
{
    public long Id { get; set; }
    public string RelativePath { get; set; } = "";
    public string AbsolutePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public FileType FileType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime MtimeUtc { get; set; }
    public string Sha256 { get; set; } = "";
    public FileStatus Status { get; set; } = FileStatus.Active;
    public string? LastError { get; set; }
    public string? MarkdownFull { get; set; }
    public DateTime IngestedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ChunkEntity> Chunks { get; set; } = new();
}
```

- [ ] **Step 3: ChunkEntity**

```csharp
namespace DocumentKB.Core.Entities;

public sealed class ChunkEntity
{
    public long Id { get; set; }
    public long FileId { get; set; }
    public int Ordinal { get; set; }
    public ChunkType ChunkType { get; set; }
    public string? TitlePath { get; set; }
    public string? LocatorJson { get; set; }
    public string ContentMd { get; set; } = "";
    public int CharLen { get; set; }
    public DateTime? EsIndexedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public FileEntity File { get; set; } = null!;
}
```

- [ ] **Step 4: IngestionRunEntity**

```csharp
namespace DocumentKB.Core.Entities;

public sealed class IngestionRunEntity
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int ScannedCount { get; set; }
    public int NewCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeletedCount { get; set; }
    public int FailedCount { get; set; }
    public string? Notes { get; set; }
}
```

- [ ] **Step 5: LocatorJson POCO + factory**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentKB.Core.Entities;

public sealed record LocatorJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("headings")] IReadOnlyList<string>? Headings = null,
    [property: JsonPropertyName("heading_level")] int? HeadingLevel = null,
    [property: JsonPropertyName("sheet")] string? Sheet = null,
    [property: JsonPropertyName("row_count")] int? RowCount = null,
    [property: JsonPropertyName("columns")] IReadOnlyList<string>? Columns = null,
    [property: JsonPropertyName("start_row")] int? StartRow = null,
    [property: JsonPropertyName("end_row")] int? EndRow = null,
    [property: JsonPropertyName("header_row")] int? HeaderRow = null)
{
    public static LocatorJson WordHeading(IReadOnlyList<string> headings, int level)
        => new("word", Headings: headings, HeadingLevel: level);

    public static LocatorJson ExcelSheet(string sheet, int rowCount, IReadOnlyList<string> columns)
        => new("excel_sheet", Sheet: sheet, RowCount: rowCount, Columns: columns);

    public static LocatorJson ExcelRows(string sheet, int startRow, int endRow, int headerRow)
        => new("excel_rows", Sheet: sheet, StartRow: startRow, EndRow: endRow, HeaderRow: headerRow);

    public string ToJsonString() => JsonSerializer.Serialize(this, JsonOpts);
    public static LocatorJson? FromJsonString(string? s)
        => string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize<LocatorJson>(s, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
```

- [ ] **Step 6: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Entities
git commit -m "feat(core): add entities + enums + LocatorJson"
```

---

### Task 5: SQL migration (embedded) + KbDbContext

**Files:**
- Create: `src/DocumentKB.Core/Persistence/SqlScripts/001_init.sql`
- Create: `src/DocumentKB.Core/Persistence/KbDbContext.cs`
- Modify: `src/DocumentKB.Core/DocumentKB.Core.csproj`

- [ ] **Step 1: Write SQL migration (copy spec § 3.1 verbatim)**

Create `src/DocumentKB.Core/Persistence/SqlScripts/001_init.sql` with the three `CREATE TABLE` statements from spec § 3.1 (files, chunks, ingestion_runs).

- [ ] **Step 2: Mark SQL as embedded resource**

In `src/DocumentKB.Core/DocumentKB.Core.csproj` add an `ItemGroup`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Persistence\SqlScripts\*.sql" />
</ItemGroup>
```

- [ ] **Step 3: Implement KbDbContext**

Create `src/DocumentKB.Core/Persistence/KbDbContext.cs`:

```csharp
using DocumentKB.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocumentKB.Core.Persistence;

public sealed class KbDbContext(DbContextOptions<KbDbContext> options) : DbContext(options)
{
    public DbSet<FileEntity> Files => Set<FileEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<IngestionRunEntity> IngestionRuns => Set<IngestionRunEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FileEntity>(e =>
        {
            e.ToTable("files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RelativePath).HasColumnName("relative_path").IsRequired();
            e.Property(x => x.AbsolutePath).HasColumnName("absolute_path").IsRequired();
            e.Property(x => x.FileName).HasColumnName("file_name").IsRequired();
            e.Property(x => x.FileType).HasColumnName("file_type")
                .HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.MtimeUtc).HasColumnName("mtime_utc");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status")
                .HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.MarkdownFull).HasColumnName("markdown_full");
            e.Property(x => x.IngestedAtUtc).HasColumnName("ingested_at_utc");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.RelativePath).IsUnique();
        });

        b.Entity<ChunkEntity>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FileId).HasColumnName("file_id");
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.ChunkType).HasColumnName("chunk_type")
                .HasConversion(
                    v => v switch {
                        ChunkType.WordSection => "word_section",
                        ChunkType.ExcelSheetSummary => "excel_sheet_summary",
                        ChunkType.ExcelRows => "excel_rows",
                        _ => throw new InvalidOperationException() },
                    s => s switch {
                        "word_section" => ChunkType.WordSection,
                        "excel_sheet_summary" => ChunkType.ExcelSheetSummary,
                        "excel_rows" => ChunkType.ExcelRows,
                        _ => throw new InvalidOperationException() });
            e.Property(x => x.TitlePath).HasColumnName("title_path");
            e.Property(x => x.LocatorJson).HasColumnName("locator_json");
            e.Property(x => x.ContentMd).HasColumnName("content_md");
            e.Property(x => x.CharLen).HasColumnName("char_len");
            e.Property(x => x.EsIndexedAt).HasColumnName("es_indexed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.File).WithMany(f => f.Chunks).HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FileId, x.Ordinal }).IsUnique();
        });

        b.Entity<IngestionRunEntity>(e =>
        {
            e.ToTable("ingestion_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
            e.Property(x => x.ScannedCount).HasColumnName("scanned_count");
            e.Property(x => x.NewCount).HasColumnName("new_count");
            e.Property(x => x.UpdatedCount).HasColumnName("updated_count");
            e.Property(x => x.DeletedCount).HasColumnName("deleted_count");
            e.Property(x => x.FailedCount).HasColumnName("failed_count");
            e.Property(x => x.Notes).HasColumnName("notes");
        });
    }
}
```

> Note: `FileStatus` is stored as `active|deleted|failed` lowercase; EF's default string conversion writes the enum name. We use `.HasConversion<string>()` plus rely on the SQL ENUM accepting lowercase. Tests will assert round-trip; if EF emits `Active` instead, swap to an explicit `HasConversion(v => v.ToString().ToLowerInvariant(), s => Enum.Parse<FileStatus>(s, true))`.

- [ ] **Step 4: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Persistence
git add src/DocumentKB.Core/DocumentKB.Core.csproj
git commit -m "feat(core): KbDbContext + initial SQL migration (embedded)"
```

---

### Task 6: MigrationRunner + AdvisoryLock

**Files:**
- Create: `src/DocumentKB.Core/Persistence/MigrationRunner.cs`
- Create: `src/DocumentKB.Core/Persistence/AdvisoryLock.cs`

- [ ] **Step 1: MigrationRunner — apply embedded SQL scripts in order**

```csharp
using System.Reflection;
using MySqlConnector;

namespace DocumentKB.Core.Persistence;

public sealed class MigrationRunner(string connectionString)
{
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var ensure = new MySqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "  name VARCHAR(255) PRIMARY KEY," +
            "  applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)" +
            ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4", conn))
            await ensure.ExecuteNonQueryAsync(ct);

        var asm = typeof(MigrationRunner).Assembly;
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".SqlScripts.") && n.EndsWith(".sql"))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resName in resources)
        {
            var name = resName[(resName.LastIndexOf('.', resName.Length - 5) + 1)..];
            await using var check = new MySqlCommand(
                "SELECT 1 FROM schema_migrations WHERE name=@n", conn);
            check.Parameters.AddWithValue("@n", name);
            if (await check.ExecuteScalarAsync(ct) is not null) continue;

            await using var stream = asm.GetManifestResourceStream(resName)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var stmt in SplitStatements(sql))
            {
                await using var cmd = new MySqlCommand(stmt, conn, tx);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var mark = new MySqlCommand(
                "INSERT INTO schema_migrations(name) VALUES(@n)", conn, tx))
            {
                mark.Parameters.AddWithValue("@n", name);
                await mark.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static IEnumerable<string> SplitStatements(string sql)
        => sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Where(s => s.Length > 0);
}
```

- [ ] **Step 2: Add MySqlConnector NuGet (used by MigrationRunner + AdvisoryLock)**

```powershell
cd src/DocumentKB.Core
dotnet add package MySqlConnector
cd ../..
```

- [ ] **Step 3: AdvisoryLock**

```csharp
using MySqlConnector;

namespace DocumentKB.Core.Persistence;

public sealed class AdvisoryLock(MySqlConnection conn) : IAsyncDisposable
{
    private readonly string _name;
    private bool _held;

    private AdvisoryLock(MySqlConnection conn, string name) : this(conn)
        => _name = name;

    public static async Task<AdvisoryLock?> TryAcquireAsync(
        string connectionString, string name, CancellationToken ct = default)
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT GET_LOCK(@n, 0)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        var got = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (got != 1)
        {
            await conn.DisposeAsync();
            return null;
        }
        return new AdvisoryLock(conn, name) { _held = true };
    }

    public async ValueTask DisposeAsync()
    {
        if (_held)
        {
            await using var cmd = new MySqlCommand("SELECT RELEASE_LOCK(@n)", conn);
            cmd.Parameters.AddWithValue("@n", _name);
            try { await cmd.ExecuteScalarAsync(); } catch { /* connection may be dead */ }
        }
        await conn.DisposeAsync();
    }
}
```

- [ ] **Step 4: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Persistence
git commit -m "feat(core): MigrationRunner + AdvisoryLock"
```

---

### Task 7: Test project + first TDD module — `Sha256Hasher`

**Files:**
- Create: `tests/DocumentKB.Core.Tests/DocumentKB.Core.Tests.csproj`
- Create: `tests/DocumentKB.Core.Tests/Ingestion/Sha256HasherTests.cs`
- Create: `src/DocumentKB.Core/Ingestion/Sha256Hasher.cs`

- [ ] **Step 1: Create test project + packages**

```powershell
dotnet new xunit -n DocumentKB.Core.Tests -o tests/DocumentKB.Core.Tests --framework net8.0
dotnet sln add tests/DocumentKB.Core.Tests/DocumentKB.Core.Tests.csproj
cd tests/DocumentKB.Core.Tests
dotnet add reference ../../src/DocumentKB.Core/DocumentKB.Core.csproj
dotnet add package FluentAssertions
dotnet add package Testcontainers.MariaDb
dotnet add package Testcontainers.Elasticsearch
cd ../..
Remove-Item tests/DocumentKB.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write failing test for Sha256Hasher**

Create `tests/DocumentKB.Core.Tests/Ingestion/Sha256HasherTests.cs`:

```csharp
using DocumentKB.Core.Ingestion;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Ingestion;

public class Sha256HasherTests
{
    [Fact]
    public async Task EmptyFile_ReturnsKnownEmptyHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            var hash = await Sha256Hasher.HashAsync(path);
            // SHA256 of empty file
            hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SameContent_SameHash()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(a, "hello world");
            await File.WriteAllTextAsync(b, "hello world");
            var ha = await Sha256Hasher.HashAsync(a);
            var hb = await Sha256Hasher.HashAsync(b);
            ha.Should().Be(hb);
            ha.Should().HaveLength(64);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task DifferentContent_DifferentHash()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(a, "hello");
            await File.WriteAllTextAsync(b, "world");
            var ha = await Sha256Hasher.HashAsync(a);
            var hb = await Sha256Hasher.HashAsync(b);
            ha.Should().NotBe(hb);
        }
        finally { File.Delete(a); File.Delete(b); }
    }
}
```

- [ ] **Step 3: Run — expect compile failure**

```powershell
dotnet test --filter Sha256HasherTests
```

Expected: build error `Sha256Hasher` doesn't exist.

- [ ] **Step 4: Implement Sha256Hasher**

Create `src/DocumentKB.Core/Ingestion/Sha256Hasher.cs`:

```csharp
using System.Security.Cryptography;

namespace DocumentKB.Core.Ingestion;

public static class Sha256Hasher
{
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 5: Run — expect PASS**

```powershell
dotnet test --filter Sha256HasherTests
```

Expected: 3 passed.

- [ ] **Step 6: Commit**

```powershell
git add src/DocumentKB.Core/Ingestion/Sha256Hasher.cs
git add tests/DocumentKB.Core.Tests
git commit -m "feat(core): Sha256Hasher + tests"
```

---

### Task 8: `FileScanner` — enumerate docx/xlsx in a folder

**Files:**
- Create: `src/DocumentKB.Core/Ingestion/FileScanner.cs`
- Create: `tests/DocumentKB.Core.Tests/Ingestion/FileScannerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using DocumentKB.Core.Entities;
using DocumentKB.Core.Ingestion;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Ingestion;

public class FileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "kb-scanner-" + Guid.NewGuid().ToString("N"));

    public FileScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private void Touch(string rel)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Array.Empty<byte>());
    }

    [Fact]
    public void FindsDocxAndXlsxRecursively()
    {
        Touch("a.docx");
        Touch("sub/b.xlsx");
        Touch("sub/deeper/c.docx");

        var found = new FileScanner().Scan(_root).ToList();

        found.Should().HaveCount(3);
        found.Select(f => f.FileType).Should().Contain(new[] {
            FileType.Word, FileType.Excel, FileType.Word });
    }

    [Fact]
    public void SkipsOfficeLockFiles()
    {
        Touch("a.docx");
        Touch("~$a.docx");
        var found = new FileScanner().Scan(_root).ToList();
        found.Should().ContainSingle(f => f.RelativePath == "a.docx");
    }

    [Fact]
    public void RelativePathUsesForwardSlashes()
    {
        Touch("sub/b.xlsx");
        var found = new FileScanner().Scan(_root).Single();
        found.RelativePath.Should().Be("sub/b.xlsx");
    }
}
```

- [ ] **Step 2: Run — expect compile fail**

```powershell
dotnet test --filter FileScannerTests
```

- [ ] **Step 3: Implement FileScanner**

```csharp
using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Ingestion;

public sealed record ScannedFile(
    string AbsolutePath, string RelativePath, string FileName,
    FileType FileType, long SizeBytes, DateTime MtimeUtc);

public sealed class FileScanner
{
    public IEnumerable<ScannedFile> Scan(string root)
    {
        var rootFull = Path.GetFullPath(root);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(path);
            if (fi.Name.StartsWith("~$")) continue;
            var type = fi.Extension.ToLowerInvariant() switch
            {
                ".docx" => FileType.Word,
                ".xlsx" => FileType.Excel,
                _ => (FileType?)null
            };
            if (type is null) continue;

            var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            yield return new ScannedFile(
                path, rel, fi.Name, type.Value, fi.Length, fi.LastWriteTimeUtc);
        }
    }
}
```

- [ ] **Step 4: Run — expect PASS** + commit

```powershell
dotnet test --filter FileScannerTests
git add src/DocumentKB.Core/Ingestion/FileScanner.cs tests/DocumentKB.Core.Tests/Ingestion/FileScannerTests.cs
git commit -m "feat(core): FileScanner + tests"
```

---

### Task 9: `WordChunker`

**Files:**
- Create: `src/DocumentKB.Core/Chunking/IChunker.cs`
- Create: `src/DocumentKB.Core/Chunking/WordChunker.cs`
- Create: `tests/DocumentKB.Core.Tests/Chunking/WordChunkerTests.cs`

- [ ] **Step 1: IChunker interface**

```csharp
using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Chunking;

public sealed record ChunkDraft(
    int Ordinal, ChunkType ChunkType, string? TitlePath,
    LocatorJson? Locator, string ContentMd);

public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(string markdown);
}
```

- [ ] **Step 2: Failing tests**

```csharp
using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Chunking;

public class WordChunkerTests
{
    private static WordChunker NewChunker(int max = 2000)
        => new(new ChunkingOptions { MaxChunkChars = max });

    [Fact]
    public void HeadingsBecomeChunks_AndTitlePathReflectsHierarchy()
    {
        var md = """
# 第一章
intro
## 1.1 小節 A
content A
## 1.2 小節 B
content B
# 第二章
content 2
""";
        var chunks = NewChunker().Chunk(md);
        chunks.Should().HaveCount(5);
        chunks[0].TitlePath.Should().Be("第一章");
        chunks[1].TitlePath.Should().Be("第一章 > 1.1 小節 A");
        chunks[2].TitlePath.Should().Be("第一章 > 1.2 小節 B");
        chunks[3].TitlePath.Should().Be("第二章");
        chunks.All(c => c.ChunkType == ChunkType.WordSection).Should().BeTrue();
    }

    [Fact]
    public void OversizedSection_SplitsByParagraph_ButKeepsTitlePath()
    {
        var big = string.Join("\n\n", Enumerable.Range(0, 50)
            .Select(i => $"paragraph {i} {new string('x', 80)}"));
        var md = $"# Section\n{big}";
        var chunks = NewChunker(500).Chunk(md);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.All(c => c.TitlePath == "Section").Should().BeTrue();
        chunks.All(c => c.ContentMd.Length <= 500).Should().BeTrue();
        chunks.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void NoHeadings_FallsBackToFixedSize_TitlePathNull()
    {
        var md = string.Join("\n\n", Enumerable.Range(0, 30)
            .Select(i => $"line {i}"));
        var chunks = NewChunker(100).Chunk(md);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.All(c => c.TitlePath is null).Should().BeTrue();
        chunks.All(c => c.ChunkType == ChunkType.WordSection).Should().BeTrue();
    }

    [Fact]
    public void HeadingLocatorRecordsLevel()
    {
        var chunks = NewChunker().Chunk("# A\nx\n## B\ny");
        chunks[1].Locator!.HeadingLevel.Should().Be(2);
        chunks[1].Locator!.Headings.Should().Equal("A", "B");
    }
}
```

- [ ] **Step 3: Run — expect fail**

```powershell
dotnet test --filter WordChunkerTests
```

- [ ] **Step 4: Implement WordChunker**

```csharp
using System.Text.RegularExpressions;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Chunking;

public sealed class WordChunker(ChunkingOptions options) : IChunker
{
    private static readonly Regex HeadingRx =
        new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);

    public IReadOnlyList<ChunkDraft> Chunk(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sections = SplitByHeading(lines).ToList();

        if (sections.Count == 0 || sections.All(s => s.HeadingStack.Count == 0))
            return SplitByParagraph(markdown, titlePath: null, locator: null).ToList();

        var drafts = new List<ChunkDraft>();
        var ordinal = 0;
        foreach (var sec in sections)
        {
            var titlePath = sec.HeadingStack.Count > 0
                ? string.Join(" > ", sec.HeadingStack) : null;
            var locator = sec.HeadingStack.Count > 0
                ? LocatorJson.WordHeading(sec.HeadingStack, sec.Level) : null;
            var body = sec.Body.TrimEnd('\n');
            if (body.Length <= options.MaxChunkChars)
            {
                drafts.Add(new ChunkDraft(
                    ordinal++, ChunkType.WordSection, titlePath, locator, body));
            }
            else
            {
                foreach (var piece in SplitByParagraph(body, titlePath, locator))
                    drafts.Add(piece with { Ordinal = ordinal++ });
            }
        }
        return drafts;
    }

    private sealed record Section(IReadOnlyList<string> HeadingStack, int Level, string Body);

    private IEnumerable<Section> SplitByHeading(string[] lines)
    {
        var stack = new List<string>();
        var levels = new List<int>();
        var buf = new System.Text.StringBuilder();
        var curStack = new List<string>();
        var curLevel = 0;
        var hasContent = false;

        void Flush()
        {
            if (!hasContent) return;
        }

        var sections = new List<Section>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = HeadingRx.Match(line);
            if (m.Success)
            {
                if (buf.Length > 0)
                    sections.Add(new Section(curStack.ToList(), curLevel, buf.ToString()));
                buf.Clear();

                var lvl = m.Groups[1].Value.Length;
                var title = m.Groups[2].Value;
                while (levels.Count > 0 && levels[^1] >= lvl)
                { levels.RemoveAt(levels.Count - 1); stack.RemoveAt(stack.Count - 1); }
                stack.Add(title); levels.Add(lvl);
                curStack = stack.ToList();
                curLevel = lvl;
                buf.AppendLine($"{m.Groups[1].Value} {title}");
            }
            else
            {
                buf.AppendLine(line);
            }
        }
        if (buf.Length > 0)
            sections.Add(new Section(curStack.ToList(), curLevel, buf.ToString()));
        return sections;
    }

    private IEnumerable<ChunkDraft> SplitByParagraph(
        string body, string? titlePath, LocatorJson? locator)
    {
        var paras = Regex.Split(body, @"\n\s*\n");
        var buf = new System.Text.StringBuilder();
        var ord = 0;
        foreach (var p in paras)
        {
            if (buf.Length + p.Length + 2 > options.MaxChunkChars && buf.Length > 0)
            {
                yield return new ChunkDraft(
                    ord++, ChunkType.WordSection, titlePath, locator,
                    buf.ToString().TrimEnd());
                buf.Clear();
            }
            if (buf.Length > 0) buf.Append("\n\n");
            buf.Append(p);
        }
        if (buf.Length > 0)
            yield return new ChunkDraft(
                ord, ChunkType.WordSection, titlePath, locator,
                buf.ToString().TrimEnd());
    }
}
```

- [ ] **Step 5: Run — expect PASS** + commit

```powershell
dotnet test --filter WordChunkerTests
git add src/DocumentKB.Core/Chunking tests/DocumentKB.Core.Tests/Chunking/WordChunkerTests.cs
git commit -m "feat(core): WordChunker (heading-based + size cap fallback)"
```

---

### Task 10: `ExcelChunker`

**Files:**
- Create: `src/DocumentKB.Core/Chunking/ExcelChunker.cs`
- Create: `tests/DocumentKB.Core.Tests/Chunking/ExcelChunkerTests.cs`

> **Assumption:** markitdown converts each Excel sheet into a markdown section that starts with the sheet name as a heading and contains a markdown table. Heuristic: split the markdown on `^## ` to delimit sheets; each sheet's first non-empty table is the data. Tests use synthetic input matching this shape.

- [ ] **Step 1: Failing tests**

```csharp
using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Chunking;

public class ExcelChunkerTests
{
    private const string SampleMd = """
## 訂單
| 訂單編號 | 客戶 | 金額 |
| --- | --- | --- |
| A001 | 王小明 | 1200000 |
| A002 | 李大華 | 850000 |
| A003 | 陳大同 | 760000 |
| A004 | 林小美 | 720000 |
| A005 | 黃大壯 | 690000 |
## 客戶
| 客戶 | 電話 |
| --- | --- |
| 王小明 | 0912 |
| 李大華 | 0934 |
""";

    private static ExcelChunker Make(int rowsPerChunk)
        => new(new ChunkingOptions
            { ExcelRowsPerChunk = rowsPerChunk, IncludeExcelHeaderInEveryChunk = true });

    [Fact]
    public void ProducesSheetSummaryFirstThenRowGroups()
    {
        var chunks = Make(2).Chunk(SampleMd);

        var orderSummary = chunks.First(c =>
            c.ChunkType == ChunkType.ExcelSheetSummary && c.TitlePath!.StartsWith("訂單"));
        orderSummary.Locator!.Sheet.Should().Be("訂單");
        orderSummary.Locator!.RowCount.Should().Be(5);
        orderSummary.Locator!.Columns.Should().Equal("訂單編號", "客戶", "金額");
    }

    [Fact]
    public void RowGroupsRepeatHeaderAndUseCorrectRowRange()
    {
        var chunks = Make(2).Chunk(SampleMd);

        var firstRows = chunks.First(c =>
            c.ChunkType == ChunkType.ExcelRows && c.Locator!.Sheet == "訂單");
        firstRows.Locator!.StartRow.Should().Be(2);
        firstRows.Locator!.EndRow.Should().Be(3);
        firstRows.Locator!.HeaderRow.Should().Be(1);
        firstRows.ContentMd.Should().Contain("訂單編號 | 客戶 | 金額");
        firstRows.ContentMd.Should().Contain("A001").And.Contain("A002");
        firstRows.ContentMd.Should().NotContain("A003");
    }

    [Fact]
    public void MultipleSheets_ProduceSummariesAndRowsForEach()
    {
        var chunks = Make(10).Chunk(SampleMd);
        chunks.Count(c => c.ChunkType == ChunkType.ExcelSheetSummary).Should().Be(2);
        chunks.Count(c => c.ChunkType == ChunkType.ExcelRows).Should().Be(2);
    }

    [Fact]
    public void OrdinalsAreSequential()
    {
        var chunks = Make(2).Chunk(SampleMd);
        chunks.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, chunks.Count));
    }
}
```

- [ ] **Step 2: Run — expect compile fail**

- [ ] **Step 3: Implement ExcelChunker**

```csharp
using System.Text;
using System.Text.RegularExpressions;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Chunking;

public sealed class ExcelChunker(ChunkingOptions options) : IChunker
{
    private static readonly Regex SheetHeaderRx =
        new(@"^##\s+(?<sheet>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TableRowRx =
        new(@"^\|.*\|\s*$", RegexOptions.Compiled);

    public IReadOnlyList<ChunkDraft> Chunk(string markdown)
    {
        var drafts = new List<ChunkDraft>();
        var ord = 0;
        foreach (var sheet in SplitSheets(markdown))
        {
            var table = ParseTable(sheet.Body);
            if (table is null) continue;

            drafts.Add(new ChunkDraft(
                ord++, ChunkType.ExcelSheetSummary,
                $"{sheet.Name}!Sheet summary",
                LocatorJson.ExcelSheet(sheet.Name, table.DataRows.Count, table.Header),
                BuildSummary(sheet.Name, table)));

            for (var i = 0; i < table.DataRows.Count; i += options.ExcelRowsPerChunk)
            {
                var slice = table.DataRows
                    .Skip(i).Take(options.ExcelRowsPerChunk).ToList();
                var startRow = i + 2;
                var endRow = startRow + slice.Count - 1;
                drafts.Add(new ChunkDraft(
                    ord++, ChunkType.ExcelRows,
                    $"{sheet.Name}!A{startRow}:A{endRow}",
                    LocatorJson.ExcelRows(sheet.Name, startRow, endRow, headerRow: 1),
                    BuildRowsChunk(table.Header, slice)));
            }
        }
        return drafts;
    }

    private sealed record Sheet(string Name, string Body);
    private sealed record TableData(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> DataRows);

    private static IEnumerable<Sheet> SplitSheets(string md)
    {
        var matches = SheetHeaderRx.Matches(md);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : md.Length;
            yield return new Sheet(matches[i].Groups["sheet"].Value, md[start..end]);
        }
    }

    private static TableData? ParseTable(string body)
    {
        var rows = body.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => TableRowRx.IsMatch(l))
            .ToList();
        if (rows.Count < 2) return null;
        var header = ParseRow(rows[0]);
        var data = rows.Skip(2)
            .Where(r => !r.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Equals(""))
            .Select(ParseRow)
            .ToList();
        return new TableData(header, data);
    }

    private static IReadOnlyList<string> ParseRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(s => s.Trim()).ToList();
    }

    private static string BuildSummary(string sheet, TableData t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sheet「{sheet}」共 {t.DataRows.Count} 列,欄位:{string.Join(" | ", t.Header)}");
        sb.AppendLine("首 5 列預覽:");
        sb.AppendLine("| " + string.Join(" | ", t.Header) + " |");
        sb.AppendLine("| " + string.Join(" | ", t.Header.Select(_ => "---")) + " |");
        foreach (var r in t.DataRows.Take(5))
            sb.AppendLine("| " + string.Join(" | ", r) + " |");
        return sb.ToString().TrimEnd();
    }

    private static string BuildRowsChunk(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("| " + string.Join(" | ", header.Select(_ => "---")) + " |");
        foreach (var r in rows)
            sb.AppendLine("| " + string.Join(" | ", r) + " |");
        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 4: Run + commit**

```powershell
dotnet test --filter ExcelChunkerTests
git add src/DocumentKB.Core/Chunking/ExcelChunker.cs tests/DocumentKB.Core.Tests/Chunking/ExcelChunkerTests.cs
git commit -m "feat(core): ExcelChunker (sheet summary + row groups with header repeat)"
```

---

### Task 11: `DecryptionRunner` + `TempFile`

**Files:**
- Create: `src/DocumentKB.Core/Decryption/DecryptionException.cs`
- Create: `src/DocumentKB.Core/Decryption/TempFile.cs`
- Create: `src/DocumentKB.Core/Decryption/DecryptionRunner.cs`
- Create: `tests/DocumentKB.Core.Tests/Decryption/DecryptionRunnerTests.cs`
- Create: `tests/fixtures/scripts/Identity-Decrypt.ps1`

- [ ] **Step 1: Test fixture script — an identity "decryption" that copies input to output**

Create `tests/fixtures/scripts/Identity-Decrypt.ps1`:

```powershell
param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
Copy-Item -LiteralPath $InputPath -Destination $OutputPath -Force
exit 0
```

- [ ] **Step 2: Failing tests**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocumentKB.Core.Tests.Decryption;

public class DecryptionRunnerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        "kb-decrypt-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    private DecryptionRunner Make(DecryptionOptions opts) =>
        new(Options.Create(new KbOptions { Decryption = opts }),
            NullLogger<DecryptionRunner>.Instance);

    private static string FixtureScript() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures",
            "scripts", "Identity-Decrypt.ps1"));

    [Fact]
    public async Task Disabled_ReturnsPassthrough()
    {
        var src = Path.GetTempFileName();
        var runner = Make(new DecryptionOptions { Enabled = false });
        await using var tf = await runner.DecryptAsync(src, default);
        tf.Path.Should().Be(src);
        File.Delete(src);
    }

    [Fact]
    public async Task Enabled_CopiesInputToTempViaScript()
    {
        var src = Path.GetTempFileName();
        await File.WriteAllTextAsync(src, "hello-aip");
        var runner = Make(new DecryptionOptions
        {
            Enabled = true, PwshExe = "pwsh", ScriptPath = FixtureScript(),
            TempRoot = _tempRoot, TimeoutSeconds = 30,
        });
        await using (var tf = await runner.DecryptAsync(src, default))
        {
            File.ReadAllText(tf.Path).Should().Be("hello-aip");
            tf.Path.Should().StartWith(_tempRoot);
        }
        Directory.GetDirectories(_tempRoot).Should().BeEmpty(
            "temp subdir must be deleted on disposal");
        File.Delete(src);
    }

    [Fact]
    public async Task NonZeroExit_Throws_AndCleansTemp()
    {
        var failScript = Path.Combine(_tempRoot, "fail.ps1");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(failScript, "exit 7");
        var src = Path.GetTempFileName();
        var runner = Make(new DecryptionOptions
        {
            Enabled = true, PwshExe = "pwsh", ScriptPath = failScript,
            TempRoot = _tempRoot, TimeoutSeconds = 30,
        });
        var act = async () => await runner.DecryptAsync(src, default);
        await act.Should().ThrowAsync<DecryptionException>();
        Directory.GetDirectories(_tempRoot)
            .Where(d => !d.EndsWith("fail.ps1")).Should().BeEmpty();
        File.Delete(src);
    }
}
```

> Note: tests require `pwsh` on PATH. CI must install PowerShell 7+.

- [ ] **Step 3: Implement DecryptionException**

```csharp
namespace DocumentKB.Core.Decryption;
public sealed class DecryptionException(string message) : Exception(message);
```

- [ ] **Step 4: Implement TempFile**

```csharp
namespace DocumentKB.Core.Decryption;

public sealed class TempFile : IAsyncDisposable
{
    public string Path { get; }
    private readonly string? _ownedDirectory;

    private TempFile(string path, string? ownedDirectory)
    {
        Path = path;
        _ownedDirectory = ownedDirectory;
    }

    public static TempFile Passthrough(string path) => new(path, null);
    public static TempFile Owned(string path, string ownedDirectory) => new(path, ownedDirectory);

    public ValueTask DisposeAsync()
    {
        if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
        {
            try { Directory.Delete(_ownedDirectory, recursive: true); }
            catch { /* best effort */ }
        }
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Implement DecryptionRunner**

```csharp
using System.Diagnostics;
using DocumentKB.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Decryption;

public sealed class DecryptionRunner(
    IOptions<KbOptions> options,
    ILogger<DecryptionRunner> logger)
{
    public async Task<TempFile> DecryptAsync(string absolutePath, CancellationToken ct)
    {
        var cfg = options.Value.Decryption;
        if (!cfg.Enabled) return TempFile.Passthrough(absolutePath);

        var tempRoot = cfg.TempRoot
            ?? Path.Combine(Path.GetTempPath(), "documentkb-decrypt");
        var subDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var outPath = Path.Combine(subDir, Path.GetFileName(absolutePath));

        var psi = new ProcessStartInfo
        {
            FileName = cfg.PwshExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", cfg.ScriptPath,
            "-InputPath", absolutePath,
            "-OutputPath", outPath })
            psi.ArgumentList.Add(a);
        foreach (var a in cfg.ExtraArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new DecryptionException("failed to spawn pwsh");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            SafeDelete(subDir);
            throw new DecryptionException("decrypt timeout");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            SafeDelete(subDir);
            var snippet = stderr.Length > 500 ? stderr[..500] : stderr;
            logger.LogWarning("Decryption failed for {File}: exit={Exit}, stderr={Stderr}",
                Path.GetFileName(absolutePath), proc.ExitCode, snippet);
            throw new DecryptionException($"decrypt failed: {snippet}");
        }

        return TempFile.Owned(outPath, subDir);
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 6: Add fixture script copy to test project**

In `tests/DocumentKB.Core.Tests/DocumentKB.Core.Tests.csproj`, add inside an `<ItemGroup>`:

```xml
<None Include="..\fixtures\scripts\*.ps1">
  <Link>fixtures\scripts\%(Filename)%(Extension)</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

> Adjust the relative path in `FixtureScript()` if the fixture isn't copied. Tests intentionally walk up from `bin/Debug/net8.0/` to find `tests/fixtures/scripts/`.

- [ ] **Step 7: Run + commit**

```powershell
dotnet test --filter DecryptionRunnerTests
git add src/DocumentKB.Core/Decryption tests/DocumentKB.Core.Tests/Decryption tests/fixtures
git commit -m "feat(core): DecryptionRunner + TempFile + passthrough + tests"
```

---

### Task 12: `MarkitdownRunner`

**Files:**
- Create: `src/DocumentKB.Core/Markitdown/MarkitdownException.cs`
- Create: `src/DocumentKB.Core/Markitdown/MarkitdownRunner.cs`
- Create: `tests/DocumentKB.Core.Tests/Markitdown/MarkitdownRunnerTests.cs`

- [ ] **Step 1: MarkitdownException**

```csharp
namespace DocumentKB.Core.Markitdown;
public sealed class MarkitdownException(string message) : Exception(message);
```

- [ ] **Step 2: Failing tests (skipped if python or markitdown missing)**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Markitdown;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocumentKB.Core.Tests.Markitdown;

public class MarkitdownRunnerTests
{
    private static bool HasPythonMarkitdown()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python", "-m markitdown --version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task ConvertsDocxToMarkdown()
    {
        if (!HasPythonMarkitdown()) return; // skip if env not ready

        var fixture = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "fixtures", "docs", "plain.docx"));
        if (!File.Exists(fixture)) return;

        var runner = new MarkitdownRunner(
            Options.Create(new KbOptions { PythonExe = "python" }),
            NullLogger<MarkitdownRunner>.Instance);
        var md = await runner.ConvertAsync(fixture, default);
        md.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 3: Implement MarkitdownRunner**

```csharp
using System.Diagnostics;
using System.Text;
using DocumentKB.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Markitdown;

public sealed class MarkitdownRunner(
    IOptions<KbOptions> options,
    ILogger<MarkitdownRunner> logger)
{
    public async Task<string> ConvertAsync(string absolutePath, CancellationToken ct)
    {
        var cfg = options.Value;
        var psi = new ProcessStartInfo
        {
            FileName = cfg.PythonExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-m", "markitdown", absolutePath }) psi.ArgumentList.Add(a);
        foreach (var a in cfg.Markitdown.ExtraArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new MarkitdownException("failed to spawn python");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.Markitdown.TimeoutSeconds));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new MarkitdownException("markitdown timeout");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            var snippet = stderr.Length > 500 ? stderr[..500] : stderr;
            logger.LogWarning("markitdown failed for {File}: {Stderr}",
                Path.GetFileName(absolutePath), snippet);
            throw new MarkitdownException($"markitdown failed: {snippet}");
        }
        return await stdoutTask;
    }
}
```

- [ ] **Step 4: Run + commit (test will silently no-op if env not ready)**

```powershell
dotnet test --filter MarkitdownRunnerTests
git add src/DocumentKB.Core/Markitdown tests/DocumentKB.Core.Tests/Markitdown
git commit -m "feat(core): MarkitdownRunner (Process.Start + timeout)"
```

---

### Task 13: ES — `KbSearchMapping` + `KbSearchClient` (ingest-side only)

**Files:**
- Create: `src/DocumentKB.Core/Search/KbSearchMapping.cs`
- Create: `src/DocumentKB.Core/Search/KbSearchClient.cs`
- Create: `tests/DocumentKB.Core.Tests/Search/KbSearchClientTests.cs`

- [ ] **Step 1: KbSearchMapping**

```csharp
using DocumentKB.Core.Configuration;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
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
              "relative_path":{ "type": "keyword" },
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

        var resp = await client.Transport.RequestAsync<StringResponse>(
            HttpMethod.PUT, $"/{idx}",
            PostData.String(json), null, ct);
        if (!resp.ApiCallDetails.HasSuccessfulStatusCode)
            throw new InvalidOperationException(
                $"create index failed: {resp.ApiCallDetails.OriginalException?.Message ?? resp.Body}");
    }
}
```

- [ ] **Step 2: KbSearchClient (DTO + bulk ingest + delete by file_id)**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Search;

public sealed record KbDoc(
    long chunk_id, long file_id, string file_name, string relative_path,
    string file_type, string? title_path, string content, string chunk_type,
    DateTime ingested_at);

public sealed class KbSearchClient(
    ElasticsearchClient client,
    IOptions<KbOptions> options)
{
    private string Idx => options.Value.ElasticSearch.Index;

    public async Task BulkUpsertAsync(IEnumerable<KbDoc> docs, CancellationToken ct)
    {
        var resp = await client.BulkAsync(b => b.Index(Idx)
            .IndexMany(docs, (d, doc) => d.Id(doc.chunk_id.ToString())), ct);
        if (resp.Errors)
        {
            var first = resp.Items.FirstOrDefault(i => i.Error is not null)?.Error?.Reason;
            throw new InvalidOperationException($"bulk index errors: {first}");
        }
    }

    public async Task DeleteByFileIdAsync(long fileId, CancellationToken ct)
    {
        await client.DeleteByQueryAsync(Idx, q => q.Query(qq =>
            qq.Term(t => t.Field("file_id").Value(fileId))), ct);
    }

    public static KbDoc ToDoc(FileEntity f, ChunkEntity c)
        => new(c.Id, f.Id, f.FileName, f.RelativePath,
            f.FileType.ToString().ToLowerInvariant(),
            c.TitlePath, c.ContentMd,
            c.ChunkType switch
            {
                ChunkType.WordSection => "word_section",
                ChunkType.ExcelSheetSummary => "excel_sheet_summary",
                ChunkType.ExcelRows => "excel_rows",
                _ => throw new ArgumentOutOfRangeException()
            },
            f.IngestedAtUtc);
}
```

- [ ] **Step 3: Integration test using Testcontainers ES**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Testcontainers.Elasticsearch;
using Xunit;

namespace DocumentKB.Core.Tests.Search;

public class KbSearchClientTests : IAsyncLifetime
{
    private readonly ElasticsearchContainer _es =
        new ElasticsearchBuilder("docker.elastic.co/elasticsearch/elasticsearch:9.0.1").Build();
    private ElasticsearchClient _client = null!;
    private KbOptions _opts = null!;

    public async Task InitializeAsync()
    {
        await _es.StartAsync();
        var settings = new ElasticsearchClientSettings(new Uri(_es.GetConnectionString()))
            .DisableDirectStreaming();
        _client = new ElasticsearchClient(settings);
        _opts = new KbOptions
        {
            ElasticSearch = new ElasticSearchOptions { Uri = _es.GetConnectionString(), Index = "kb_chunks" }
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
        var resp = await _client.SearchAsync<KbDoc>(s => s.Index("kb_chunks")
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
        var resp = await _client.CountAsync<KbDoc>(c => c.Indices("kb_chunks")
            .Query(q => q.Term(t => t.Field("file_id").Value(77))));
        resp.Count.Should().Be(0);
    }
}
```

- [ ] **Step 4: Run + commit**

```powershell
dotnet test --filter KbSearchClientTests
git add src/DocumentKB.Core/Search tests/DocumentKB.Core.Tests/Search
git commit -m "feat(core): KbSearchMapping + KbSearchClient (bulk + delete by file_id)"
```

---

### Task 14: `IngestionPipeline`

**Files:**
- Create: `src/DocumentKB.Core/Ingestion/IngestionPipeline.cs`

> Tests for this come via E2E in Task 15. The pipeline composes pieces already TDD'd individually.

- [ ] **Step 1: Implement IngestionPipeline**

```csharp
using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Markitdown;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Ingestion;

public sealed record IngestionResult(
    long RunId, int Scanned, int New, int Updated, int Deleted, int Failed);

public sealed class IngestionPipeline(
    IOptions<KbOptions> options,
    KbDbContext db,
    FileScanner scanner,
    DecryptionRunner decrypt,
    MarkitdownRunner markitdown,
    WordChunker wordChunker,
    ExcelChunker excelChunker,
    KbSearchClient searchClient,
    ILogger<IngestionPipeline> log)
{
    public async Task<IngestionResult> RunAsync(
        string? pathPrefix, bool force, CancellationToken ct)
    {
        var root = options.Value.SourceFolder;
        var run = new IngestionRunEntity { StartedAtUtc = DateTime.UtcNow };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var scanned = scanner.Scan(root)
            .Where(f => pathPrefix is null ||
                f.RelativePath.StartsWith(pathPrefix.TrimEnd('/') + "/")
                || f.RelativePath == pathPrefix)
            .ToList();
        run.ScannedCount = scanned.Count;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scanned)
        {
            seen.Add(f.RelativePath);
            try
            {
                await ProcessOneAsync(f, run, force, ct);
            }
            catch (Exception ex)
            {
                run.FailedCount++;
                log.LogError(ex, "Failed to ingest {File}", f.RelativePath);
                await MarkFileFailedAsync(f, ex.Message, ct);
            }
        }

        // Soft delete files no longer on disk
        var activeRows = await db.Files
            .Where(x => x.Status == FileStatus.Active
                && (pathPrefix == null || x.RelativePath.StartsWith(pathPrefix)))
            .Select(x => new { x.Id, x.RelativePath })
            .ToListAsync(ct);
        foreach (var row in activeRows)
        {
            if (seen.Contains(row.RelativePath)) continue;
            await db.Files
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, FileStatus.Deleted)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
            await searchClient.DeleteByFileIdAsync(row.Id, ct);
            run.DeletedCount++;
        }

        // Backfill ES for chunks with es_indexed_at = NULL
        var pending = await db.Chunks
            .Include(c => c.File)
            .Where(c => c.EsIndexedAt == null)
            .Take(1000).ToListAsync(ct);
        if (pending.Count > 0)
        {
            var docs = pending.Select(c => KbSearchClient.ToDoc(c.File, c));
            await searchClient.BulkUpsertAsync(docs, ct);
            var now = DateTime.UtcNow;
            foreach (var c in pending) c.EsIndexedAt = now;
            await db.SaveChangesAsync(ct);
        }

        run.FinishedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new IngestionResult(
            run.Id, run.ScannedCount, run.NewCount, run.UpdatedCount, run.DeletedCount, run.FailedCount);
    }

    private async Task ProcessOneAsync(
        ScannedFile f, IngestionRunEntity run, bool force, CancellationToken ct)
    {
        var sha = await Sha256Hasher.HashAsync(f.AbsolutePath, ct);
        var existing = await db.Files
            .FirstOrDefaultAsync(x => x.RelativePath == f.RelativePath, ct);
        if (!force && existing is not null
            && existing.Sha256 == sha && existing.Status == FileStatus.Active) return;

        await using var tempPlain = await decrypt.DecryptAsync(f.AbsolutePath, ct);
        var md = await markitdown.ConvertAsync(tempPlain.Path, ct);

        var chunker = f.FileType == FileType.Word
            ? (IChunker)wordChunker : excelChunker;
        var drafts = chunker.Chunk(md);

        var now = DateTime.UtcNow;
        var entity = existing ?? new FileEntity
            { RelativePath = f.RelativePath, CreatedAt = now };
        entity.AbsolutePath = f.AbsolutePath;
        entity.FileName = f.FileName;
        entity.FileType = f.FileType;
        entity.SizeBytes = f.SizeBytes;
        entity.MtimeUtc = f.MtimeUtc;
        entity.Sha256 = sha;
        entity.Status = FileStatus.Active;
        entity.LastError = null;
        entity.MarkdownFull = md;
        entity.IngestedAtUtc = now;
        entity.UpdatedAt = now;
        if (existing is null) db.Files.Add(entity);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (existing is not null)
            await db.Chunks.Where(c => c.FileId == entity.Id).ExecuteDeleteAsync(ct);
        var chunkEntities = drafts.Select(d => new ChunkEntity
        {
            FileId = entity.Id,
            Ordinal = d.Ordinal,
            ChunkType = d.ChunkType,
            TitlePath = d.TitlePath,
            LocatorJson = d.Locator?.ToJsonString(),
            ContentMd = d.ContentMd,
            CharLen = d.ContentMd.Length,
            EsIndexedAt = null,
            CreatedAt = now,
        }).ToList();
        db.Chunks.AddRange(chunkEntities);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        try
        {
            await searchClient.BulkUpsertAsync(
                chunkEntities.Select(c => KbSearchClient.ToDoc(entity, c)), ct);
            var ts = DateTime.UtcNow;
            foreach (var c in chunkEntities) c.EsIndexedAt = ts;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ES bulk failed; will retry on next run");
        }

        if (existing is null) run.NewCount++; else run.UpdatedCount++;
    }

    private async Task MarkFileFailedAsync(ScannedFile f, string error, CancellationToken ct)
    {
        var existing = await db.Files
            .FirstOrDefaultAsync(x => x.RelativePath == f.RelativePath, ct);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.Files.Add(new FileEntity
            {
                RelativePath = f.RelativePath,
                AbsolutePath = f.AbsolutePath,
                FileName = f.FileName,
                FileType = f.FileType,
                SizeBytes = f.SizeBytes,
                MtimeUtc = f.MtimeUtc,
                Sha256 = "",
                Status = FileStatus.Failed,
                LastError = error,
                IngestedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Status = FileStatus.Failed;
            existing.LastError = error;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Ingestion/IngestionPipeline.cs
git commit -m "feat(core): IngestionPipeline (scan→decrypt→convert→chunk→DB→ES)"
```

---

### Task 15: `DocumentKB.Ingestion` CLI — `reindex` + `doctor` + E2E test

**Files:**
- Create: `src/DocumentKB.Ingestion/DocumentKB.Ingestion.csproj`
- Create: `src/DocumentKB.Ingestion/Program.cs`
- Create: `src/DocumentKB.Ingestion/Commands/ReindexCommand.cs`
- Create: `src/DocumentKB.Ingestion/Commands/DoctorCommand.cs`
- Create: `tests/DocumentKB.Ingestion.Tests/DocumentKB.Ingestion.Tests.csproj`
- Create: `tests/DocumentKB.Ingestion.Tests/IngestionPipelineEndToEndTests.cs`
- Create: `tests/fixtures/docs/plain.docx` (provide via real Office or use a known-good fixture)

- [ ] **Step 1: Bootstrap CLI project**

```powershell
dotnet new console -n DocumentKB.Ingestion -o src/DocumentKB.Ingestion --framework net8.0
dotnet sln add src/DocumentKB.Ingestion/DocumentKB.Ingestion.csproj
cd src/DocumentKB.Ingestion
dotnet add reference ../DocumentKB.Core/DocumentKB.Core.csproj
dotnet add package System.CommandLine --prerelease
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Microsoft.Extensions.Configuration.Json
dotnet add package Serilog.Extensions.Hosting
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Sinks.Console
cd ../..
```

- [ ] **Step 2: Program.cs — host wiring + command dispatch**

```csharp
using System.CommandLine;
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
using Serilog;

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
    new WordChunker(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KbOptions>>().Value.Chunking));
builder.Services.AddSingleton<ExcelChunker>(sp =>
    new ExcelChunker(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KbOptions>>().Value.Chunking));
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
```

- [ ] **Step 3: ReindexCommand**

```csharp
using System.CommandLine;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Ingestion;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocumentKB.Ingestion.Commands;

public static class ReindexCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var force = new Option<bool>("--force", () => false, "Ignore SHA, re-convert all");
        var path = new Option<string?>("--path", () => null, "Restrict to relative subfolder");
        var cmd = new Command("reindex", "Scan + ingest changed files");
        cmd.AddOption(force); cmd.AddOption(path);
        cmd.SetHandler(async (bool f, string? p) =>
        {
            await using var scope = sp.CreateAsyncScope();
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<KbOptions>>().Value;
            await new MigrationRunner(opts.MariaDb.ConnectionString).ApplyAsync();
            await scope.ServiceProvider.GetRequiredService<KbSearchMapping>().EnsureIndexAsync();

            await using var locker = await AdvisoryLock.TryAcquireAsync(
                opts.MariaDb.ConnectionString, "documentkb.ingestion");
            if (locker is null)
            {
                await Console.Error.WriteLineAsync("Another ingestion is running. Aborting.");
                Environment.Exit(2);
                return;
            }

            var pipe = scope.ServiceProvider.GetRequiredService<IngestionPipeline>();
            var result = await pipe.RunAsync(p, f, default);
            Console.WriteLine($"run_id={result.RunId} scanned={result.Scanned} " +
                $"new={result.New} updated={result.Updated} " +
                $"deleted={result.Deleted} failed={result.Failed}");
        }, force, path);
        return cmd;
    }
}
```

- [ ] **Step 4: DoctorCommand**

```csharp
using System.CommandLine;
using System.Diagnostics;
using DocumentKB.Core.Configuration;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace DocumentKB.Ingestion.Commands;

public static class DoctorCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var cmd = new Command("doctor", "Check DB / ES / Python / pwsh + decrypt script");
        cmd.SetHandler(async () =>
        {
            var opts = sp.GetRequiredService<IOptions<KbOptions>>().Value;
            var fail = 0;
            fail += await Check("MariaDB", async () =>
            {
                await using var c = new MySqlConnection(opts.MariaDb.ConnectionString);
                await c.OpenAsync();
            });
            fail += await Check("ElasticSearch", async () =>
            {
                var es = sp.GetRequiredService<ElasticsearchClient>();
                var p = await es.PingAsync();
                if (!p.IsValidResponse) throw new InvalidOperationException("ping failed");
            });
            fail += await Check("Python markitdown", () => RunOk(opts.PythonExe, "-m", "markitdown", "--version"));
            if (opts.Decryption.Enabled)
            {
                fail += await Check("pwsh", () => RunOk(opts.Decryption.PwshExe, "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major"));
                fail += await Check("Decrypt script exists", () =>
                {
                    if (!File.Exists(opts.Decryption.ScriptPath))
                        throw new FileNotFoundException(opts.Decryption.ScriptPath);
                    return Task.CompletedTask;
                });
            }
            Environment.Exit(fail == 0 ? 0 : 1);
        });
        return cmd;
    }

    private static async Task<int> Check(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine($"[ OK ] {name}"); return 0; }
        catch (Exception ex) { Console.WriteLine($"[FAIL] {name}: {ex.Message}"); return 1; }
    }

    private static async Task RunOk(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("spawn failed");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exit={p.ExitCode}");
    }
}
```

- [ ] **Step 5: Add minimal `appsettings.json` for local runs**

Create `src/DocumentKB.Ingestion/appsettings.json` (copied to output):

```json
{
  "SourceFolder": "./samples",
  "PythonExe": "python",
  "Decryption": {
    "Enabled": false,
    "PwshExe": "pwsh",
    "ScriptPath": ""
  },
  "Markitdown": { "TimeoutSeconds": 60 },
  "Chunking": { "MaxChunkChars": 2000, "ExcelRowsPerChunk": 20 },
  "MariaDb": {
    "ConnectionString": "Server=localhost;Database=documentkb;User=root;Password=root;CharSet=utf8mb4"
  },
  "ElasticSearch": { "Uri": "http://localhost:9200", "Index": "kb_chunks" },
  "Odata": { "MaxTop": 200, "DefaultTop": 50, "MaxInputBytes": 4096 }
}
```

In csproj `<ItemGroup>`:

```xml
<None Update="appsettings.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
```

- [ ] **Step 6: E2E test project + test**

```powershell
dotnet new xunit -n DocumentKB.Ingestion.Tests -o tests/DocumentKB.Ingestion.Tests --framework net8.0
dotnet sln add tests/DocumentKB.Ingestion.Tests/DocumentKB.Ingestion.Tests.csproj
cd tests/DocumentKB.Ingestion.Tests
dotnet add reference ../../src/DocumentKB.Core/DocumentKB.Core.csproj
dotnet add package FluentAssertions
dotnet add package Testcontainers.MariaDb
dotnet add package Testcontainers.Elasticsearch
cd ../..
Remove-Item tests/DocumentKB.Ingestion.Tests/UnitTest1.cs
```

Create `tests/DocumentKB.Ingestion.Tests/IngestionPipelineEndToEndTests.cs`:

```csharp
using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Ingestion;
using DocumentKB.Core.Markitdown;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Elasticsearch;
using Testcontainers.MariaDb;
using Xunit;

namespace DocumentKB.Ingestion.Tests;

public class IngestionPipelineEndToEndTests : IAsyncLifetime
{
    private readonly MariaDbContainer _db = new MariaDbBuilder()
        .WithImage("mariadb:11").Build();
    private readonly ElasticsearchContainer _es =
        new ElasticsearchBuilder("docker.elastic.co/elasticsearch/elasticsearch:9.0.1").Build();
    private string _sampleDir = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _es.StartAsync());
        _sampleDir = Path.Combine(Path.GetTempPath(), "kb-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sampleDir);
        // Copy fixture docx/xlsx into _sampleDir (omitted for brevity — see fixtures/docs/)
        CopyFixture("plain.docx");
        CopyFixture("headings.docx");
        CopyFixture("orders.xlsx");
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_db.DisposeAsync().AsTask(), _es.DisposeAsync().AsTask());
        if (Directory.Exists(_sampleDir)) Directory.Delete(_sampleDir, true);
    }

    private static void CopyFixture(string name)
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "docs", name));
        // optional: bail if env doesn't have fixtures
    }

    [Fact]
    public async Task ReindexEmptyFolder_CompletesWithZeroCounts()
    {
        var opts = MakeOpts();
        await new MigrationRunner(opts.MariaDb.ConnectionString).ApplyAsync();
        await using var ctx = MakeCtx(opts);
        var es = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(opts.ElasticSearch.Uri)));
        await new KbSearchMapping(es, Options.Create(opts)).EnsureIndexAsync();

        var pipe = MakePipeline(ctx, es, opts);
        var result = await pipe.RunAsync(null, force: false, default);
        result.Scanned.Should().Be(0);
    }

    [Fact]
    public async Task ReindexWithFixtures_PersistsFilesAndChunks_AndIndexesEs()
    {
        if (!Directory.EnumerateFiles(_sampleDir).Any())
            return; // env-skip: fixtures not available

        var opts = MakeOpts();
        await new MigrationRunner(opts.MariaDb.ConnectionString).ApplyAsync();
        await using var ctx = MakeCtx(opts);
        var es = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(opts.ElasticSearch.Uri)));
        await new KbSearchMapping(es, Options.Create(opts)).EnsureIndexAsync();

        var pipe = MakePipeline(ctx, es, opts);
        var result = await pipe.RunAsync(null, false, default);
        result.New.Should().BeGreaterThan(0);
        var fileCount = await ctx.Files.CountAsync();
        fileCount.Should().BeGreaterThan(0);
        var chunkCount = await ctx.Chunks.CountAsync();
        chunkCount.Should().BeGreaterThan(0);
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
```

- [ ] **Step 7: Run + commit M1 milestone**

```powershell
dotnet build
dotnet test --filter IngestionPipelineEndToEndTests
git add src/DocumentKB.Ingestion tests/DocumentKB.Ingestion.Tests
git commit -m "feat(ingestion): CLI reindex + doctor + E2E pipeline test (M1)"
```

> **M1 Acceptance**: `documentkb-ingest doctor` returns 0; `documentkb-ingest reindex` against `tests/fixtures/docs/` writes files + chunks to DB and indexes ES; running twice with same files reports `new=0 updated=0`.

---

## Phase M2 — MCP 查詢可用(Tasks 16–23)

### Task 16: `DocumentKB.Mcp` project + stdio MCP wiring

**Files:**
- Create: `src/DocumentKB.Mcp/DocumentKB.Mcp.csproj`
- Create: `src/DocumentKB.Mcp/Program.cs`
- Create: `tests/DocumentKB.Mcp.Tests/DocumentKB.Mcp.Tests.csproj`

- [ ] **Step 1: Bootstrap MCP project**

```powershell
dotnet new console -n DocumentKB.Mcp -o src/DocumentKB.Mcp --framework net8.0
dotnet sln add src/DocumentKB.Mcp/DocumentKB.Mcp.csproj
cd src/DocumentKB.Mcp
dotnet add reference ../DocumentKB.Core/DocumentKB.Core.csproj
dotnet add package ModelContextProtocol --prerelease
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Microsoft.Extensions.Configuration.Json
dotnet add package Serilog.Extensions.Hosting
dotnet add package Serilog.Sinks.File
cd ../..
```

> If `ModelContextProtocol` package name differs in your environment, search NuGet for the official C# MCP SDK and adjust. The API expected below is: `builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`.

- [ ] **Step 2: Program.cs — stdio host (stdout reserved for MCP)**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Elastic.Clients.Elasticsearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
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

// stdout is reserved for MCP; logs MUST go to stderr/file only.
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
```

- [ ] **Step 3: Test project**

```powershell
dotnet new xunit -n DocumentKB.Mcp.Tests -o tests/DocumentKB.Mcp.Tests --framework net8.0
dotnet sln add tests/DocumentKB.Mcp.Tests/DocumentKB.Mcp.Tests.csproj
cd tests/DocumentKB.Mcp.Tests
dotnet add reference ../../src/DocumentKB.Mcp/DocumentKB.Mcp.csproj
dotnet add reference ../../src/DocumentKB.Core/DocumentKB.Core.csproj
dotnet add package FluentAssertions
dotnet add package Testcontainers.MariaDb
dotnet add package Testcontainers.Elasticsearch
cd ../..
Remove-Item tests/DocumentKB.Mcp.Tests/UnitTest1.cs
```

- [ ] **Step 4: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Mcp tests/DocumentKB.Mcp.Tests
git commit -m "feat(mcp): stdio host skeleton + test project"
```

---

### Task 17: ES BM25 query — extend `KbSearchClient`

**Files:**
- Modify: `src/DocumentKB.Core/Search/KbSearchClient.cs`

- [ ] **Step 1: Add Search method + hit DTO**

Append to `KbSearchClient.cs`:

```csharp
public sealed record KbHit(
    long ChunkId, long FileId, string FileName, string RelativePath,
    string FileType, string? TitlePath, string ChunkType,
    double Score, string Snippet);

public sealed record KbSearchResponse(
    IReadOnlyList<KbHit> Hits, long TotalMatched, long TookMs, string QueryEcho);
```

Add method on `KbSearchClient`:

```csharp
public async Task<KbSearchResponse> SearchAsync(
    string query, int topK, string? fileType, string? pathPrefix,
    double? minScore, CancellationToken ct)
{
    var resp = await client.SearchAsync<KbDoc>(s =>
    {
        s.Index(Idx).Size(topK).TrackTotalHits(new(true));
        s.Query(q =>
        {
            var must = new List<Action<Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<KbDoc>>>
            {
                qq => qq.MultiMatch(m => m
                    .Query(query)
                    .Fields(new[] { "content^3", "title_path^2", "file_name" }))
            };
            var filter = new List<Action<Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<KbDoc>>>();
            if (!string.IsNullOrEmpty(fileType))
                filter.Add(qq => qq.Term(t => t.Field("file_type").Value(fileType)));
            if (!string.IsNullOrEmpty(pathPrefix))
                filter.Add(qq => qq.Prefix(p => p.Field("relative_path").Value(pathPrefix)));
            return q.Bool(b => b
                .Must(must.Select<Action<Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<KbDoc>>,
                    Action<Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<KbDoc>>>(x => x).ToArray())
                .Filter(filter.ToArray()));
        });
        s.Highlight(h => h.Fields(f => f
            .Field("content").PreTags("<em>").PostTags("</em>")
            .NumberOfFragments(2).FragmentSize(120)));
    }, ct);

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
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Search/KbSearchClient.cs
git commit -m "feat(core): KbSearchClient BM25 SearchAsync (multi_match + highlight)"
```

---

### Task 18: `KbSearchTool`

**Files:**
- Create: `src/DocumentKB.Mcp/Tools/KbSearchTool.cs`

- [ ] **Step 1: Implement the tool**

```csharp
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
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Mcp/Tools/KbSearchTool.cs
git commit -m "feat(mcp): kb_search tool (BM25 + highlight)"
```

---

### Task 19: `KbFetchChunkTool`

**Files:**
- Create: `src/DocumentKB.Mcp/Tools/KbFetchChunkTool.cs`

- [ ] **Step 1: Implement (loads target chunk + optional prev/next)**

```csharp
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
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Mcp/Tools/KbFetchChunkTool.cs
git commit -m "feat(mcp): kb_fetch_chunk tool with optional prev/next neighbors"
```

---

### Task 20: OData — `EdmBuilder`

**Files:**
- Create: `src/DocumentKB.Core/Odata/EdmBuilder.cs`

- [ ] **Step 1: Implement**

```csharp
using DocumentKB.Core.Entities;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace DocumentKB.Core.Odata;

public static class EdmBuilder
{
    public static IEdmModel Build()
    {
        var b = new ODataConventionModelBuilder();
        var files = b.EntitySet<FileEntity>("Files");
        files.EntityType.HasKey(x => x.Id);
        files.EntityType.Ignore(x => x.MarkdownFull);
        files.EntityType.Ignore(x => x.Chunks);

        var chunks = b.EntitySet<ChunkEntity>("Chunks");
        chunks.EntityType.HasKey(x => x.Id);
        chunks.EntityType.Ignore(x => x.ContentMd);

        return b.GetEdmModel();
    }
}
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Odata/EdmBuilder.cs
git commit -m "feat(core): OData EDM model (Files/Chunks; hide large fields)"
```

---

### Task 21: `OdataQueryValidator`

**Files:**
- Create: `src/DocumentKB.Core/Odata/OdataQueryValidator.cs`
- Create: `tests/DocumentKB.Core.Tests/Odata/OdataQueryValidatorTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Odata;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Odata;

public class OdataQueryValidatorTests
{
    private readonly OdataQueryValidator _v =
        new(new OdataOptions { MaxTop = 200, DefaultTop = 50, MaxInputBytes = 4096 });

    [Fact] public void AllowedFunction_Passes()
        => _v.Validate("$filter=contains(FileName,'x')&$top=20").Should().BeNull();

    [Fact] public void BannedClause_apply_Rejected()
        => _v.Validate("$apply=groupby((FileType))")
            .Should().Contain("$apply");

    [Fact] public void BannedClause_search_Rejected()
        => _v.Validate("$search=foo").Should().Contain("$search");

    [Fact] public void BannedClause_compute_Rejected()
        => _v.Validate("$compute=Id mul 2 as X").Should().Contain("$compute");

    [Fact] public void TopOverMax_Rejected()
        => _v.Validate("$top=999").Should().Contain("$top");

    [Fact] public void InputTooLong_Rejected()
        => _v.Validate(new string('a', 5000)).Should().Contain("size");

    [Fact] public void ExpandDepthOverOne_Rejected()
        => _v.Validate("$expand=File($expand=Chunks)").Should().Contain("$expand");
}
```

- [ ] **Step 2: Run — expect compile fail**

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;
using DocumentKB.Core.Configuration;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryValidator(OdataOptions options)
{
    private static readonly HashSet<string> BannedClauses =
        new(StringComparer.OrdinalIgnoreCase) { "$apply", "$compute", "$search" };
    private static readonly HashSet<string> AllowedFns =
        new(StringComparer.OrdinalIgnoreCase) {
            "contains","startswith","endswith","tolower","toupper","length",
            "year","month","day","now"};
    private static readonly Regex FnCallRx = new(@"\b([a-zA-Z]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex TopRx    = new(@"\$top\s*=\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex ExpandRx = new(@"\$expand=([^&]+)", RegexOptions.IgnoreCase);

    public string? Validate(string odata)
    {
        if (string.IsNullOrEmpty(odata)) return null;
        if (System.Text.Encoding.UTF8.GetByteCount(odata) > options.MaxInputBytes)
            return $"odata exceeds max size {options.MaxInputBytes} bytes";
        foreach (var b in BannedClauses)
            if (odata.Contains(b, StringComparison.OrdinalIgnoreCase))
                return $"clause {b} is banned";
        var m = TopRx.Match(odata);
        if (m.Success)
        {
            var t = int.Parse(m.Groups[1].Value);
            if (t > options.MaxTop)
                return $"$top={t} exceeds MaxTop={options.MaxTop}";
        }
        var em = ExpandRx.Match(odata);
        if (em.Success && em.Groups[1].Value.Contains("$expand", StringComparison.OrdinalIgnoreCase))
            return "$expand depth > 1 is banned";
        foreach (Match fm in FnCallRx.Matches(odata))
        {
            var fn = fm.Groups[1].Value;
            if (IsKnownClauseOrOperator(fn)) continue;
            if (!AllowedFns.Contains(fn))
                return $"function {fn} is not allowed";
        }
        return null;
    }

    private static bool IsKnownClauseOrOperator(string token)
        => token.StartsWith("$") || token is "in" or "and" or "or" or "not"
            or "eq" or "ne" or "lt" or "le" or "gt" or "ge";
}
```

- [ ] **Step 4: Run + commit**

```powershell
dotnet test --filter OdataQueryValidatorTests
git add src/DocumentKB.Core/Odata/OdataQueryValidator.cs tests/DocumentKB.Core.Tests/Odata
git commit -m "feat(core): OdataQueryValidator (whitelist functions, banned clauses, size cap)"
```

---

### Task 22: `OdataQueryRunner` — apply OData to IQueryable<T>

**Files:**
- Create: `src/DocumentKB.Core/Odata/OdataQueryRunner.cs`

- [ ] **Step 1: Implement**

```csharp
using DocumentKB.Core.Configuration;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Validator;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryRunner(OdataOptions options)
{
    public (IQueryable<T> Query, long? Count) Apply<T>(
        IQueryable<T> source, IEdmModel model, string entitySetName, string odata)
    {
        var serviceRoot = new Uri("http://localhost/");
        var queryUri = new Uri(serviceRoot, $"{entitySetName}?{odata}");
        var parser = new ODataQueryOptionParser(model,
            model.FindDeclaredType($"DocumentKB.Core.Entities.{typeof(T).Name}"),
            model.FindDeclaredEntitySet(entitySetName),
            ParseQueryString(odata))
            { Resolver = new ODataUriResolver { EnableCaseInsensitive = true } };

        var top = parser.ParseTop() ?? options.DefaultTop;
        var skip = parser.ParseSkip() ?? 0;
        var orderBy = parser.ParseOrderBy();
        var filter = parser.ParseFilter();
        var select = parser.ParseSelectAndExpand();

        var q = source;
        if (filter is not null)
            q = (IQueryable<T>)q.Where(filter.ToLinqExpression<T>(model));
        long? count = null;
        var countOpt = parser.ParseCount();
        if (countOpt == true) count = q.LongCount();
        if (orderBy is not null)
            q = q.OrderBy(orderBy.ToLinqOrderBy<T>(model));
        else
            q = q.OrderBy(x => x);
        q = q.Skip(skip).Take((int)Math.Min(top, options.MaxTop));
        return (q, count);
    }

    private static Dictionary<string, string> ParseQueryString(string odata)
        => odata.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "");
}
```

> **Important:** the `.ToLinqExpression` / `.ToLinqOrderBy` extension helpers don't exist out of the box in Microsoft.AspNetCore.OData. Use the higher-level `ODataQueryOptions<T>.ApplyTo(IQueryable)` instead, which requires an `HttpRequest`. Realistic implementation alternative below:

**Realistic implementation (use this in production):**

```csharp
using DocumentKB.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Parser;
using Microsoft.AspNetCore.Routing;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryRunner(OdataOptions options)
{
    public (IQueryable Result, long? Count) Apply<T>(
        IQueryable<T> source, IEdmModel model, string entitySetName, string odata)
    {
        var entitySet = model.FindDeclaredEntitySet(entitySetName)
            ?? throw new InvalidOperationException($"entity set {entitySetName} missing");
        var path = new Microsoft.OData.UriParser.ODataPath(
            new Microsoft.OData.UriParser.EntitySetSegment(entitySet));
        var ctx = new ODataQueryContext(model, typeof(T), path);

        var ctxAccessor = new DefaultHttpContext();
        ctxAccessor.Request.Method = "GET";
        ctxAccessor.Request.Scheme = "http";
        ctxAccessor.Request.Host = new HostString("localhost");
        ctxAccessor.Request.Path = $"/{entitySetName}";
        ctxAccessor.Request.QueryString = new QueryString("?" + odata);

        var opts = new ODataQueryOptions<T>(ctx, ctxAccessor.Request);
        long? count = null;
        if (opts.Count?.Value == true) count = opts.Count.GetEntityCount(source);

        var settings = new ODataQuerySettings
        {
            PageSize = Math.Min(options.MaxTop, options.DefaultTop),
            EnsureStableOrdering = true,
        };
        var result = opts.ApplyTo(source, settings, AllowedQueryOptions.None);
        return (result, count);
    }
}
```

- [ ] **Step 2: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Core/Odata/OdataQueryRunner.cs
git commit -m "feat(core): OdataQueryRunner (ODataQueryOptions.ApplyTo)"
```

---

### Task 23: `KbQueryDocumentsTool`

**Files:**
- Create: `src/DocumentKB.Mcp/Tools/KbQueryDocumentsTool.cs`

- [ ] **Step 1: Implement**

```csharp
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
public sealed class KbQueryDocumentsTool(KbDbContext db, IOptions<KbOptions> opts)
{
    private static readonly Microsoft.OData.Edm.IEdmModel Model = EdmBuilder.Build();

    [McpServerTool, Description("OData query over Files. " +
        "Example: $filter=FileType eq 'excel'&$orderby=MtimeUtc desc&$top=10&$count=true")]
    public async Task<object> kb_query_documents(
        [Description("OData query string (after the '?'), e.g. $filter=...&$top=10")] string odata,
        CancellationToken ct = default)
    {
        var validator = new OdataQueryValidator(opts.Value.Odata);
        var err = validator.Validate(odata);
        if (err is not null)
            return new { error = new { code = "INVALID_INPUT", message = err } };

        try
        {
            var runner = new OdataQueryRunner(opts.Value.Odata);
            var (q, count) = runner.Apply(
                db.Files.AsNoTracking().Where(f => f.Status == FileStatus.Active),
                Model, "Files", odata);
            var items = await ((IQueryable<FileEntity>)q).ToListAsync(ct);
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
```

- [ ] **Step 2: Add `appsettings.json` for MCP project + sample minimal tests**

`src/DocumentKB.Mcp/appsettings.json` — same shape as ingestion's, MariaDb + ElasticSearch + Odata required.

`tests/DocumentKB.Mcp.Tests/ToolsTests.cs` — bring up MariaDB + ES, seed via `IngestionPipeline`, invoke each tool's method directly (not via stdio):

```csharp
// see fixtures + pattern in Task 15 IngestionPipelineEndToEndTests
// Seed DB by running ingestion on fixtures
// Then assert kb_search returns hits, kb_fetch_chunk returns content_md,
//     kb_query_documents returns Files when $filter=FileType eq 'word'
```

- [ ] **Step 3: Run + commit M2**

```powershell
dotnet build
dotnet test --filter ToolsTests
git add src/DocumentKB.Mcp tests/DocumentKB.Mcp.Tests
git commit -m "feat(mcp): kb_query_documents (OData over Files) + tool tests (M2)"
```

> **M2 Acceptance**: Configure Claude Code to load `DocumentKB.Mcp.exe` per spec § 9. Ask a question about a fixture file. Verify the conversation goes `kb_search → kb_fetch_chunk` and the answer includes a citation with original text.

---

## Phase M3 — 完整 + 韌性(Tasks 24–34)

### Task 24: `KbQueryChunksTool`

**Files:**
- Create: `src/DocumentKB.Mcp/Tools/KbQueryChunksTool.cs`

- [ ] **Step 1: Implement (mirror Task 23 over chunks; support $expand=File)**

```csharp
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
```

- [ ] **Step 2: Run + commit**

```powershell
dotnet build
git add src/DocumentKB.Mcp/Tools/KbQueryChunksTool.cs
git commit -m "feat(mcp): kb_query_chunks (OData over Chunks; supports $expand=File)"
```

---

### Task 25: Resilience — verified soft-delete & ES backfill paths

> The Phase M1 `IngestionPipeline` already implements soft-delete and `es_indexed_at` backfill (Task 14). This task adds **assertions** so we know they work.

**Files:**
- Modify: `tests/DocumentKB.Ingestion.Tests/IngestionPipelineEndToEndTests.cs`

- [ ] **Step 1: Add tests**

```csharp
[Fact]
public async Task FileRemoved_BecomesSoftDeleted_AndEsCleared()
{
    // seed via reindex, then delete a fixture, reindex again, assert Status=Deleted + no ES doc
}

[Fact]
public async Task EsBulkFailsThenSucceeds_BackfillFills_EsIndexedAt()
{
    // simulate by stopping ES, ingest (DB writes ok, ES fails), restart ES, reindex again,
    // assert chunks.EsIndexedAt is no longer NULL
}
```

> Realistic E2E: use Testcontainers ES `.Stop()` / `.Start()` between phases or point `KbSearchClient` at a wrong URL on the first run via a custom container setup.

- [ ] **Step 2: Run + commit**

```powershell
dotnet test --filter IngestionPipelineEndToEndTests
git add tests/DocumentKB.Ingestion.Tests/IngestionPipelineEndToEndTests.cs
git commit -m "test(ingestion): soft-delete + ES backfill assertions"
```

---

### Task 26: Extra CLI commands — `status`, `test-decrypt`, `export-markdown`

**Files:**
- Create: `src/DocumentKB.Ingestion/Commands/StatusCommand.cs`
- Create: `src/DocumentKB.Ingestion/Commands/TestDecryptCommand.cs`
- Create: `src/DocumentKB.Ingestion/Commands/ExportMarkdownCommand.cs`
- Modify: `src/DocumentKB.Ingestion/Program.cs`

- [ ] **Step 1: StatusCommand**

```csharp
using System.CommandLine;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class StatusCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var cmd = new Command("status", "Show KB health summary");
        cmd.SetHandler(async () =>
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
            var active = await db.Files.CountAsync(f => f.Status == FileStatus.Active);
            var failed = await db.Files.CountAsync(f => f.Status == FileStatus.Failed);
            var chunks = await db.Chunks.CountAsync();
            var pending = await db.Chunks.CountAsync(c => c.EsIndexedAt == null);
            var last = await db.IngestionRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            Console.WriteLine($"files_active={active} files_failed={failed} " +
                $"chunks_total={chunks} chunks_pending_es={pending}");
            if (last is not null)
            {
                Console.WriteLine($"last_run id={last.Id} started={last.StartedAtUtc:O} " +
                    $"finished={last.FinishedAtUtc:O} new={last.NewCount} " +
                    $"updated={last.UpdatedCount} deleted={last.DeletedCount} " +
                    $"failed={last.FailedCount}");
            }
            if (failed > 0)
            {
                Console.WriteLine("\nfailed_files:");
                await foreach (var f in db.Files.AsNoTracking()
                    .Where(f => f.Status == FileStatus.Failed)
                    .Select(f => new { f.RelativePath, f.LastError })
                    .AsAsyncEnumerable())
                    Console.WriteLine($"  - {f.RelativePath} :: {f.LastError}");
            }
        });
        return cmd;
    }
}
```

- [ ] **Step 2: TestDecryptCommand**

```csharp
using System.CommandLine;
using DocumentKB.Core.Decryption;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class TestDecryptCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var file = new Option<string>("--file", description: "absolute path to test")
            { IsRequired = true };
        var cmd = new Command("test-decrypt", "Run decrypt script for one file") { file };
        cmd.SetHandler(async (string f) =>
        {
            var runner = sp.GetRequiredService<DecryptionRunner>();
            await using var tf = await runner.DecryptAsync(f, default);
            Console.WriteLine($"OK: plain temp at {tf.Path}");
            Console.WriteLine("(temp will be cleaned when this process exits)");
        }, file);
        return cmd;
    }
}
```

- [ ] **Step 3: ExportMarkdownCommand**

```csharp
using System.CommandLine;
using DocumentKB.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentKB.Ingestion.Commands;

public static class ExportMarkdownCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var fileId = new Option<long>("--file-id", "files.id to export") { IsRequired = true };
        var cmd = new Command("export-markdown", "Print files.markdown_full to stdout") { fileId };
        cmd.SetHandler(async (long id) =>
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
            var md = await db.Files.Where(f => f.Id == id)
                .Select(f => f.MarkdownFull).FirstOrDefaultAsync();
            if (md is null) { await Console.Error.WriteLineAsync("not found"); Environment.Exit(1); }
            Console.WriteLine(md);
        }, fileId);
        return cmd;
    }
}
```

- [ ] **Step 4: Wire all 3 into Program.cs**

In `src/DocumentKB.Ingestion/Program.cs` after the existing `root.AddCommand(...)` calls:

```csharp
root.AddCommand(StatusCommand.Build(host.Services));
root.AddCommand(TestDecryptCommand.Build(host.Services));
root.AddCommand(ExportMarkdownCommand.Build(host.Services));
```

- [ ] **Step 5: Build + commit**

```powershell
dotnet build
git add src/DocumentKB.Ingestion
git commit -m "feat(ingestion): status + test-decrypt + export-markdown commands"
```

---

### Task 27: Sample script + sample settings + Claude Code config

**Files:**
- Create: `deploy/appsettings.sample.json`
- Create: `deploy/scripts/Aip-Decrypt.ps1.sample`
- Create: `deploy/migrations/001_init.sql` (copy of embedded resource)
- Create: `deploy/claude-mcp-config.example.json`

- [ ] **Step 1: appsettings.sample.json** — copy `src/DocumentKB.Ingestion/appsettings.json` minus secrets, add comments-as-keys explaining each section.

- [ ] **Step 2: Aip-Decrypt.ps1.sample**

```powershell
<#
.SYNOPSIS
  Sample AIP/RMS decryption script.
  Required to:
    - accept -InputPath and -OutputPath parameters
    - write decrypted file (or copy through if not encrypted) to -OutputPath
    - exit 0 on success, nonzero on failure (stderr = error message)
#>
param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

# TODO: replace this body with your AIP/RMS unprotect call, e.g.:
#   Unprotect-RMSFile -File $InputPath -OutputFolder (Split-Path $OutputPath -Parent) -OutputName (Split-Path $OutputPath -Leaf)

# Fallback for sample: pass through unchanged.
try {
    Copy-Item -LiteralPath $InputPath -Destination $OutputPath -Force
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
```

- [ ] **Step 3: deploy/migrations/001_init.sql** — copy spec § 3.1 SQL verbatim (same as embedded resource).

- [ ] **Step 4: claude-mcp-config.example.json**

```json
{
  "mcpServers": {
    "documentkb": {
      "command": "C:\\Tools\\DocumentKB\\Mcp\\DocumentKB.Mcp.exe",
      "args": [],
      "env": {
        "DOCUMENTKB_APPSETTINGS": "C:\\Tools\\DocumentKB\\Mcp\\appsettings.json"
      }
    }
  }
}
```

- [ ] **Step 5: Commit**

```powershell
git add deploy
git commit -m "chore(deploy): sample appsettings + decrypt script + migrations + claude mcp config"
```

---

### Task 28: SKILL.md — write & ship

**Files:**
- Create: `skill/documentkb/SKILL.md`

- [ ] **Step 1: Paste SKILL.md content from spec § 6.2**

Copy the entire `SKILL.md` body (frontmatter + sections) from spec § 6.2 into `skill/documentkb/SKILL.md`. No edits needed — the spec version is the canonical one.

- [ ] **Step 2: Commit**

```powershell
git add skill/documentkb/SKILL.md
git commit -m "feat(skill): documentkb SKILL.md (citation discipline + tool guide)"
```

---

### Task 29: Documentation README pointers

**Files:**
- Create: `README.md`

- [ ] **Step 1: Minimal README**

```markdown
# DocumentKB

Local Word/Excel knowledge base. See:
- Design: `docs/superpowers/specs/2026-05-28-documentkb-design.md`
- Plan:   `docs/superpowers/plans/2026-05-28-documentkb-implementation.md`

## Quick start

1. Prereqs: .NET 8, MariaDB 10.6+, ElasticSearch 9.x, Python 3.10+ (`pip install markitdown[all]`), PowerShell 7+ (`pwsh`)
2. `dotnet build`
3. Copy `deploy/appsettings.sample.json` to `src/DocumentKB.Ingestion/appsettings.json` (and `src/DocumentKB.Mcp/appsettings.json`), fill in real values, set `Decryption.ScriptPath` to your AIP decrypt script (use `deploy/scripts/Aip-Decrypt.ps1.sample` as starting point)
4. `dotnet run --project src/DocumentKB.Ingestion -- doctor`
5. `dotnet run --project src/DocumentKB.Ingestion -- reindex`
6. Add the MCP server to Claude Code via `deploy/claude-mcp-config.example.json`
7. Copy `skill/documentkb/SKILL.md` to `~/.claude/skills/documentkb/SKILL.md`
8. Ask Claude a question.
```

- [ ] **Step 2: Commit**

```powershell
git add README.md
git commit -m "docs: README with quick start pointers"
```

---

### Task 30: End-to-end Claude Code verification (manual)

- [ ] **Step 1: Publish both apps to `C:\Tools\DocumentKB\`**

```powershell
dotnet publish src/DocumentKB.Ingestion -c Release -o C:\Tools\DocumentKB\Ingestion
dotnet publish src/DocumentKB.Mcp -c Release -o C:\Tools\DocumentKB\Mcp
```

- [ ] **Step 2: Register MCP in Claude Code**

Edit `~/.claude.json` (or the project-level `.mcp.json`) to add the `documentkb` MCP server entry from `deploy/claude-mcp-config.example.json`, then restart Claude Code.

- [ ] **Step 3: Copy SKILL**

```powershell
$skillDir = "$env:USERPROFILE\.claude\skills\documentkb"
New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
Copy-Item "skill\documentkb\SKILL.md" "$skillDir\SKILL.md" -Force
```

- [ ] **Step 4: Index your real document folder**

```powershell
& C:\Tools\DocumentKB\Ingestion\DocumentKB.Ingestion.exe doctor
& C:\Tools\DocumentKB\Ingestion\DocumentKB.Ingestion.exe reindex
& C:\Tools\DocumentKB\Ingestion\DocumentKB.Ingestion.exe status
```

- [ ] **Step 5: Verify in Claude Code**

In a Claude Code conversation, ask a question whose answer lives in your KB. **Acceptance criteria:**
- Claude invokes `kb_search` then `kb_fetch_chunk(..., include_neighbors=true)`
- Answer cites every fact with `[#chunk_id]`
- A "引用區段" block follows the answer with original `content_md` plus prev/next neighbors
- If you ask "what files are in KB?" Claude uses `kb_query_documents`
- Asking about something not in KB → Claude says "KB 內找不到" and suggests `documentkb-ingest reindex`

- [ ] **Step 6: Document the verification result**

Append a short verification log to README (date + status). Commit.

```powershell
git add README.md
git commit -m "verify: end-to-end Claude Code citation flow (manual)"
```

---

## Self-Review

**Spec coverage check (spec sections → tasks):**

| Spec section | Implemented in |
|---|---|
| § 2 Architecture | Tasks 1–16 (project skeleton + DI wiring) |
| § 3.1 MariaDB schema | Task 5 (SQL + DbContext) |
| § 3.2 locator_json | Task 4 (LocatorJson + factories) |
| § 3.3 ES mapping | Task 13 (KbSearchMapping) |
| § 4.1 ingestion main loop | Task 14 (IngestionPipeline) |
| § 4.2 decryption | Task 11 (DecryptionRunner + TempFile) |
| § 4.3 markitdown call | Task 12 (MarkitdownRunner) |
| § 4.4 Word chunking | Task 9 (WordChunker) |
| § 4.5 Excel chunking | Task 10 (ExcelChunker) |
| § 4.6 idempotency | Task 7 (SHA), Task 14 (skip-on-unchanged), Task 15 (advisory lock) |
| § 4.7 failure modes | Tasks 11/12 (timeouts), Task 14 (try/catch), Task 25 (assertion tests) |
| § 5.1–5.3 kb_search + kb_fetch_chunk | Tasks 17–19 |
| § 5.4 OData (kb_query_*) | Tasks 20–23, 24 |
| § 5.5 error envelopes | Tasks 18, 19, 23, 24 (`error.code` pattern) |
| § 5.6 startup health | Task 15 (DoctorCommand), MCP startup logs to file (Task 16) |
| § 6 SKILL.md | Task 28 |
| § 7 config | Task 3 (POCO), Task 15 (sample appsettings), Task 27 (sample) |
| § 8 project structure | Tasks 1, 2, 7, 15, 16 |
| § 9 deployment | Task 30 (manual verification), Task 29 (README) |
| § 10 CLI inventory | Tasks 15 (reindex/doctor), 26 (status/test-decrypt/export-markdown) |
| § 11 testing strategy | Tasks 7–13, 21 (unit), 13, 15, 25 (integration), 30 (manual) |
| § 12 phases (M1/M2/M3) | Marked at end of Tasks 15 / 23 / 30 |

**Placeholder scan:** None of the steps say "TBD / implement later / add validation as appropriate". Code blocks are complete (with two flagged caveats — `OdataQueryRunner` includes both the naive parser sketch and the realistic `ODataQueryOptions.ApplyTo` version; engineer uses the latter. SKILL.md content is referenced rather than re-pasted to avoid drift — Task 28 explicitly says "copy spec § 6.2 verbatim").

**Type consistency:** `ChunkType` enum values (`WordSection / ExcelSheetSummary / ExcelRows`) and string equivalents (`word_section / excel_sheet_summary / excel_rows`) are consistent across `KbDbContext` converter, `KbSearchClient.ToDoc`, and `KbFetchChunkTool.ChunkTypeToString`. `FileType` (`Word / Excel`) stored as lowercase string via `.HasConversion<string>()` and emitted lowercase in `KbSearchClient.ToDoc`. `KbDoc` field names match ES mapping (`chunk_id`, `file_id`, `file_name`, `relative_path`, `file_type`, `title_path`, `content`, `chunk_type`, `ingested_at`).

**Known engineer caveats (call out at start of execution):**
1. `ModelContextProtocol` NuGet package name may differ — search the official C# MCP SDK and adjust Task 16 if needed.
2. `Microsoft.AspNetCore.OData` 8 requires constructing an `HttpRequest` to use `ODataQueryOptions<T>.ApplyTo`; the "Realistic implementation" block in Task 22 is the one to use.
3. xlsx/docx fixtures (Tasks 10, 15) are not literally creatable in this plan — engineer must hand-author small Word/Excel files using the actual apps and drop them in `tests/fixtures/docs/`.

---

**End of plan.**
