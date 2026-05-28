# DocumentKB

Local Word/Excel knowledge base.

A .NET 8 ingestion CLI converts `*.docx` / `*.xlsx` (optionally AIP/RMS decrypted via PowerShell) to markdown via Python `markitdown`, chunks them, and indexes into MariaDB (source of truth) + ElasticSearch (BM25 full-text). A stdio MCP server exposes four read-only tools so Claude Code can answer questions with original-text citations.

## Docs

- Design spec: [`docs/superpowers/specs/2026-05-28-documentkb-design.md`](docs/superpowers/specs/2026-05-28-documentkb-design.md)
- Implementation plan: [`docs/superpowers/plans/2026-05-28-documentkb-implementation.md`](docs/superpowers/plans/2026-05-28-documentkb-implementation.md)

## Prerequisites

- .NET 8 SDK
- MariaDB 10.6+
- ElasticSearch 9.0+ (the codebase pins `Elastic.Clients.Elasticsearch` 9.x which requires server-side 9.x)
- Python 3.10+ with `pip install markitdown[all]`
- PowerShell 7+ (`pwsh`) — only when `Decryption.Enabled=true`

## Quick start

```powershell
# Build
dotnet build

# Copy + edit settings
Copy-Item deploy/appsettings.sample.json src/DocumentKB.Ingestion/appsettings.json
Copy-Item deploy/appsettings.sample.json src/DocumentKB.Mcp/appsettings.json
# Fill in real MariaDB / ES connection info, SourceFolder, etc.

# Customize decryption script
Copy-Item deploy/scripts/Aip-Decrypt.ps1.sample C:/Tools/DocumentKB/scripts/Aip-Decrypt.ps1
# Adapt body to your AIP/RMS environment

# Sanity check the environment
dotnet run --project src/DocumentKB.Ingestion -- doctor

# Index your docs
dotnet run --project src/DocumentKB.Ingestion -- reindex

# Status
dotnet run --project src/DocumentKB.Ingestion -- status
```

## Wire to Claude Code

1. `dotnet publish src/DocumentKB.Mcp -c Release -o C:\Tools\DocumentKB\Mcp`
2. Copy the published `appsettings.json` to `C:\Tools\DocumentKB\Mcp\appsettings.json`
3. Add this to `~/.claude.json` (or project `.mcp.json`):
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
4. Install the SKILL:
   ```powershell
   New-Item -ItemType Directory -Path "$env:USERPROFILE\.claude\skills\documentkb" -Force | Out-Null
   Copy-Item skill\documentkb\SKILL.md "$env:USERPROFILE\.claude\skills\documentkb\SKILL.md" -Force
   ```
5. Restart Claude Code. Ask a question about a file in your indexed folder.

## CLI commands

```
documentkb-ingest reindex                    # incremental (SHA compare)
documentkb-ingest reindex --force            # re-convert all
documentkb-ingest reindex --path "財務/2024/"
documentkb-ingest status                     # health + last run summary
documentkb-ingest doctor                     # connection/env check
documentkb-ingest test-decrypt --file <path>
documentkb-ingest export-markdown --file-id 77
```

## MCP tools (read-only)

| Tool | Purpose |
|---|---|
| `kb_search` | BM25 search → returns chunk_ids with snippet |
| `kb_fetch_chunk` | Single chunk + optional prev/next neighbors |
| `kb_query_documents` | OData over Files (filter, orderby, top, count) |
| `kb_query_chunks` | OData over Chunks metadata |

The SKILL at `skill/documentkb/SKILL.md` enforces "search → fetch → answer with original-text citation" discipline.

## License

(Pick a license appropriate for your use; this project is currently unlicensed.)
