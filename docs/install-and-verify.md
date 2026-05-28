# Install & Verify

After running `dotnet publish` (output in `artifacts/Ingestion` and `artifacts/Mcp`), follow these steps to install on Windows and verify end-to-end with Claude Code.

## 1. Install

```powershell
# Pick an install root
$root = "C:\Tools\DocumentKB"
New-Item -ItemType Directory -Path $root -Force | Out-Null

Copy-Item artifacts/Ingestion $root/Ingestion -Recurse -Force
Copy-Item artifacts/Mcp $root/Mcp -Recurse -Force

# Settings — edit these to point at your actual DB / ES / docs folder / decrypt script
Copy-Item deploy/appsettings.sample.json $root/Ingestion/appsettings.json
Copy-Item deploy/appsettings.sample.json $root/Mcp/appsettings.json

# Customize decrypt script (if you use AIP/RMS)
New-Item -ItemType Directory -Path $root/scripts -Force | Out-Null
Copy-Item deploy/scripts/Aip-Decrypt.ps1.sample $root/scripts/Aip-Decrypt.ps1
```

## 2. Index your docs

```powershell
& "$root/Ingestion/DocumentKB.Ingestion.exe" doctor
& "$root/Ingestion/DocumentKB.Ingestion.exe" reindex
& "$root/Ingestion/DocumentKB.Ingestion.exe" status
```

## 3. Register the MCP

Add to `~/.claude.json`:

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

## 4. Install the SKILL

```powershell
$skillDir = "$env:USERPROFILE\.claude\skills\documentkb"
New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
Copy-Item skill/documentkb/SKILL.md "$skillDir/SKILL.md" -Force
```

## 5. Acceptance checklist

Restart Claude Code, then in a conversation:

- [ ] Ask a question whose answer lives in your KB (a real fact in one of your `.docx` or `.xlsx` files)
- [ ] Verify Claude invokes `kb_search` first (look for the tool call panel)
- [ ] Verify Claude follows up with `kb_fetch_chunk(chunk_id, include_neighbors=true)` for each chunk it cites
- [ ] Verify each fact in the answer carries a `[#chunk_id]` reference
- [ ] Verify a "引用區段" block appears after the answer, containing the original chunk's `content_md` plus prev/next neighbors
- [ ] Ask "what files are in KB?" — Claude should use `kb_query_documents`
- [ ] Ask something not in KB — Claude should say "KB 內找不到" and suggest `documentkb-ingest reindex`

If any of these fail, file a follow-up issue.
