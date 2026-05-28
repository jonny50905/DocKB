# DocumentKB 設計規格

**日期**: 2026-05-28
**範圍**: 本機 Word/Excel 文件知識庫 — ingestion + 純查詢 MCP + Claude SKILL
**狀態**: 設計確認中

---

## 1. 目的與範圍

把指定資料夾下的 `*.docx`、`*.xlsx` 檔案經 markitdown 轉成 markdown、切分後存入 MariaDB(真實狀態)與 ElasticSearch(BM25 索引),透過一個純查詢的 MCP server 讓 Claude 能用自然語言問答並提供 **有憑有據(原文 + 上下文)** 的引用。

### 已確定的範圍前提

- 個人 / 小團隊規模(< 5000 份文件)
- 純 BM25 全文檢索,**不使用 embedding / 向量搜尋**
- 持續更新 + 手動觸發 re-index(透過 CLI,不透過 MCP)
- 引用要包含:檔名 + 相對路徑、原文段落、位置資訊(Word heading 階層 / Excel sheet+row range)、上下相鄰段落
- MCP 採 stdio 子進程(.NET 8),供 Claude Code 啟動
- Word 與 Excel 各自設計切分策略

### 明確不做(YAGNI)

- 認證 / 授權
- 多租戶
- 即時 FileSystemWatcher
- Embedding / 語意搜尋
- HTTP / SSE remote MCP(階段二可考慮)
- MCP 端任何寫入操作(包含 reindex)
- OData `$apply` / `$compute` / `$search`
- 暴露大欄位(`MarkdownFull`、`ContentMd`)到 OData

---

## 2. 架構總覽

```
                    ┌───────────────────┐
                    │  source folder    │
                    │  *.docx, *.xlsx   │
                    └─────────┬─────────┘
                              │ enumerate + SHA256
                              ▼
┌──────────────────────────────────────────────────────────┐
│  DocumentKB.Ingestion  (.NET 8 console / CLI)            │
│                                                          │
│  1. scan folder, compare hash with files table           │
│  2. for each new/changed file:                           │
│     - spawn:  python -m markitdown <path>                │
│     - parse markdown → chunks (Word: headings,           │
│       Excel: sheet/row groups, 預設 20 列/塊)            │
│     - upsert into MariaDB (files, chunks)                │
│     - bulk index into ES (kb_chunks)                     │
│  3. for files removed from disk: soft-delete + un-index  │
└──────────────────┬──────────────────────┬────────────────┘
                   ▼                      ▼
            ┌──────────────┐       ┌─────────────────┐
            │   MariaDB    │       │  ElasticSearch  │
            │  source of   │       │  BM25 search    │
            │  truth (CRUD)│       │  index (read)   │
            └──────┬───────┘       └────────┬────────┘
                   │                        │
                   └──────────┬─────────────┘
                              │
                              ▼
            ┌──────────────────────────────────┐
            │ DocumentKB.Mcp                   │
            │ (.NET 8 stdio MCP server, 純讀)  │
            │                                  │
            │ tools:                           │
            │  - kb_search(query, top_k, ...)  │
            │  - kb_fetch_chunk(chunk_id,      │
            │                   include_neighbors)│
            │  - kb_query_documents(odata)     │
            │  - kb_query_chunks(odata)        │
            └──────────────┬───────────────────┘
                           │ stdio
                           ▼
                ┌────────────────────┐
                │ Claude Code        │
                │ + DocumentKB SKILL │
                └────────────────────┘
```

### 元件職責

| 元件 | 類型 | 職責 |
|---|---|---|
| `DocumentKB.Core` | .NET class library | 共用:EF DbContext、ES client、chunking、markitdown runner、OData EDM 與 validator、ingestion pipeline |
| `DocumentKB.Ingestion` | .NET 8 console (CLI) | **寫入端**:scan → 轉檔 → 切分 → 寫 DB + ES,advisory lock 防並行 |
| `DocumentKB.Mcp` | .NET 8 console (stdio) | **純讀端**:Claude Code 子進程,4 個 query tool |
| `markitdown` | Python CLI | 由 Ingestion 經 `Process.Start` 呼叫,只做 file → markdown |
| `documentkb` SKILL | Markdown + frontmatter | 引導 Claude 何時用 MCP、怎麼組合 tools、citation 格式紀律 |

### MariaDB / ES 職責分工

- **MariaDB = source of truth**:檔案 metadata、chunk 原文、位置資訊、ingestion 稽核狀態。所有 CRUD 在這。
- **ElasticSearch = 搜尋索引(derived)**:只儲存搜尋需要的欄位,可從 MariaDB 完全重建。
- 查詢路徑:`ES.search(query)` → `chunk_id` 列表 → SQL `SELECT ... WHERE id IN (...)` 取原文與位置 → 組成 citation 回傳。

---

## 3. 資料模型

### 3.1 MariaDB schema

```sql
-- 檔案層級的真實狀態
CREATE TABLE files (
    id              BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    relative_path   VARCHAR(1024) NOT NULL,
    absolute_path   VARCHAR(1024) NOT NULL,
    file_name       VARCHAR(512)  NOT NULL,
    file_type       ENUM('word','excel') NOT NULL,
    size_bytes      BIGINT       NOT NULL,
    mtime_utc       DATETIME(6)  NOT NULL,
    sha256          CHAR(64)     NOT NULL,
    status          ENUM('active','deleted','failed') NOT NULL DEFAULT 'active',
    last_error      TEXT         NULL,
    markdown_full   LONGTEXT     NULL,
    ingested_at_utc DATETIME(6)  NOT NULL,
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_files_relpath (relative_path),
    KEY ix_files_sha256 (sha256),
    KEY ix_files_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- chunk 是可被搜尋的最小單位
CREATE TABLE chunks (
    id             BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    file_id        BIGINT       NOT NULL,
    ordinal        INT          NOT NULL,
    chunk_type     ENUM('word_section','excel_sheet_summary','excel_rows') NOT NULL,
    title_path     VARCHAR(1024) NULL,
    locator_json   JSON         NULL,
    content_md     MEDIUMTEXT   NOT NULL,
    char_len       INT          NOT NULL,
    es_indexed_at  DATETIME(6)  NULL,
    created_at     DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_chunks_file_ord (file_id, ordinal),
    KEY ix_chunks_file (file_id),
    CONSTRAINT fk_chunks_file FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 每次手動 re-index 的稽核紀錄
CREATE TABLE ingestion_runs (
    id              BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    started_at_utc  DATETIME(6)  NOT NULL,
    finished_at_utc DATETIME(6)  NULL,
    scanned_count   INT          NOT NULL DEFAULT 0,
    new_count       INT          NOT NULL DEFAULT 0,
    updated_count   INT          NOT NULL DEFAULT 0,
    deleted_count   INT          NOT NULL DEFAULT 0,
    failed_count    INT          NOT NULL DEFAULT 0,
    notes           TEXT         NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

### 3.2 locator_json 格式

```jsonc
// Word
{ "type": "word", "headings": ["第三章 系統架構", "3.2 ingestion 流程"], "heading_level": 2 }

// Excel — sheet summary
{ "type": "excel_sheet", "sheet": "訂單", "row_count": 152, "columns": ["訂單編號","客戶","金額"] }

// Excel — row group
{ "type": "excel_rows", "sheet": "訂單", "start_row": 31, "end_row": 50, "header_row": 1 }
```

### 3.3 ElasticSearch index `kb_chunks`

```jsonc
PUT /kb_chunks
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
      "chunk_id":   { "type": "long" },
      "file_id":    { "type": "long" },
      "file_name":  { "type": "text", "analyzer": "kb_text",
                      "fields": { "raw": { "type": "keyword" } } },
      "relative_path": { "type": "keyword" },
      "file_type":  { "type": "keyword" },
      "title_path": { "type": "text", "analyzer": "kb_text",
                      "fields": { "raw": { "type": "keyword" } } },
      "content":    { "type": "text", "analyzer": "kb_text" },
      "chunk_type": { "type": "keyword" },
      "ingested_at":{ "type": "date" }
    }
  }
}
```

ES doc `_id` = `chunks.id`(字串化),使 upsert / delete 簡單。

### 3.4 設計選擇

1. **`markdown_full` 留在 files**:轉檔貴,留著可在改變切分策略時不重轉。
2. **`es_indexed_at` 在 chunk 上**:ES 失敗的補償欄位;下次 ingestion 可掃 `IS NULL` 補。
3. **`cjk_bigram`**:零安裝、中英文混合堪用;之後可換 ICU / IK 加 reindex。
4. **`ON DELETE CASCADE`**:檔案硬刪時 chunks 自動清。

---

## 4. Ingestion 流程

### 4.1 主流程

```
1. 開新 ingestion_runs 紀錄
2. 取 MariaDB advisory lock GET_LOCK('documentkb.ingestion', 0)
   - 取不到 → 退出並提示有另一個 run 在跑
3. 遞迴掃描 source folder 取所有 *.docx, *.xlsx (跳過 ~$ 開頭 Office lock 檔)
4. 對每個檔:
   a. 算 SHA256
   b. 查 files 表 by relative_path
      - 不存在 → NEW
      - 存在且 sha256 相同 → UNCHANGED → 跳過
      - 存在但 sha256 不同 → UPDATED
   c. NEW/UPDATED:
      - **decrypt(absolutePath) → tempPlainPath**(見 4.2)
      - spawn markitdown 對 tempPlainPath,timeout = config.Markitdown.TimeoutSeconds (預設 60)
      - 解析 markdown → 切分 chunks (見 4.4 / 4.5)
      - DB transaction:upsert files、DELETE FROM chunks WHERE file_id=?、INSERT 新 chunks
      - ES bulk index (失敗 → es_indexed_at 留 NULL)
      - 失敗 → files.status='failed' + last_error,繼續下一檔
      - finally:刪除整個解密暫存子目錄(連同明文檔)
5. 對 DB 內 status='active' 但本次未掃到的 relative_path:
   → status='deleted' + 從 ES 刪該 file_id 所有 chunks
6. 補做:chunks.es_indexed_at IS NULL → 補 index
7. UPDATE ingestion_runs SET finished_at_utc, 統計欄位
8. 釋放 advisory lock
```

### 4.2 解密(AIP / RMS)

每個 NEW/UPDATED 檔案在進 markitdown 之前一律先走解密步驟。**判斷是否真的需要解密由 PowerShell script 自行決定**(若不需要,script 直接複製到 output);.NET 端不 sniff 加密標頭。

**呼叫約定**

```
<PwshExe> -NoProfile -NonInteractive -ExecutionPolicy Bypass
          -File <Decryption.ScriptPath>
          -InputPath  <absolutePathOfSource>
          -OutputPath <tempPlainPath>
          [...Decryption.ExtraArgs]

exit 0   → 成功;tempPlainPath 必須已存在且可讀
exit ≠ 0 → 失敗;stderr 含錯誤訊息(前 500 字記到 files.last_error)
```

**暫存區規則**

- 暫存根目錄:`Decryption.TempRoot ?? Path.Combine(Path.GetTempPath(), "documentkb-decrypt")`
- 每檔一個 GUID 子目錄:`<TempRoot>/<guid>/<原檔名>`
- `try { ... } finally { Directory.Delete(subDir, recursive: true) }` 保證明文不留地表
- log **不**記檔案內容,只記檔名 + exit code + stderr 前 N 字

**範本 script** 放在 `deploy/scripts/Aip-Decrypt.ps1.sample`,使用者依自家 AIP/RMS 環境改寫,只要遵守上述約定即可。

**Disabled 時**:`Decryption.Enabled=false` → 跳過此步,markitdown 直接吃 absolutePath。

```csharp
internal sealed class DecryptionRunner(IOptions<KbOptions> opts, ILogger<DecryptionRunner> log)
{
    public async Task<TempFile> DecryptAsync(string absolutePath, CancellationToken ct)
    {
        var cfg = opts.Value.Decryption;
        if (!cfg.Enabled) return TempFile.Passthrough(absolutePath);

        var tempRoot = cfg.TempRoot ?? Path.Combine(Path.GetTempPath(), "documentkb-decrypt");
        var subDir   = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var outPath  = Path.Combine(subDir, Path.GetFileName(absolutePath));

        var psi = new ProcessStartInfo {
            FileName = cfg.PwshExe,
            ArgumentList = {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", cfg.ScriptPath,
                "-InputPath", absolutePath,
                "-OutputPath", outPath,
            },
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
        };
        foreach (var a in cfg.ExtraArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        using var cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        await proc.WaitForExitAsync(cts.Token);
        if (proc.ExitCode != 0) {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            Directory.Delete(subDir, recursive: true);
            throw new DecryptionException($"decrypt failed: {stderr[..Math.Min(500, stderr.Length)]}");
        }
        return TempFile.Owned(outPath, subDir);   // Dispose 時刪 subDir
    }
}
```

### 4.3 markitdown 呼叫

```csharp
var psi = new ProcessStartInfo {
    FileName = options.PythonExe,
    ArgumentList = { "-m", "markitdown", absolutePath },
    RedirectStandardOutput = true,
    RedirectStandardError  = true,
    UseShellExecute = false,
    StandardOutputEncoding = Encoding.UTF8,
};
using var proc = Process.Start(psi)!;
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(TimeSpan.FromSeconds(options.Markitdown.TimeoutSeconds));
var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
await proc.WaitForExitAsync(cts.Token);
if (proc.ExitCode != 0) throw new MarkitdownException(stderr);
return stdout;
```

### 4.4 Word 切分策略

- 從第一個 heading 開始,每遇 `#` / `##` / `###` 切一個 chunk;內容包含 heading 自身 + 該 heading 到下一個同級或更高級 heading 之間的所有內容。
- 若 chunk 超過 `Chunking.MaxChunkChars`(預設 2000)→ 按段落(空白行)再切;每片帶相同 `title_path`、不同 `ordinal`。
- `title_path` = 當前 heading stack 從根到該 heading 串接,例如 `"第三章 系統架構 > 3.2 ingestion 流程"`。
- 沒有 heading 的 Word → 每 `MaxChunkChars` 切一段,`title_path = NULL`,`chunk_type='word_section'`。

### 4.5 Excel 切分策略

每個 sheet 產出兩種 chunk:

1. **Sheet summary chunk**(每 sheet 一個,`chunk_type='excel_sheet_summary'`)
   - 內容:sheet 名 + 列數 + 欄位列表 + 首 5 列預覽 markdown table
   - `locator_json = {"type":"excel_sheet","sheet":"訂單","row_count":152,"columns":[...]}`
2. **Row-group chunks**(每 `Chunking.ExcelRowsPerChunk` 列一塊,預設 **20**,`chunk_type='excel_rows'`)
   - 每塊**重複表頭**,確保 BM25 命中時欄位名 + 資料一起出現
   - `title_path = "訂單!A2:F21"` 風格
   - `locator_json = {"type":"excel_rows","sheet":"訂單","start_row":2,"end_row":21,"header_row":1}`

### 4.6 冪等性

- SHA256(整檔 bytes)當作 unchanged 判斷,不依賴 mtime
- 每檔 = 一個 DB transaction,ES 寫入在 transaction 外靠 `es_indexed_at` 補償
- 強制重轉:`--force` 忽略 hash 比對
- 同一檔反覆 ingest 結果不變

### 4.7 失敗模式對策

| 失敗點 | 對策 |
|---|---|
| 解密 script exit ≠ 0 | `files.status='failed'` + `last_error="decrypt failed: <stderr 前 500 字>"`,**不轉檔**,繼續下一檔,temp 子目錄刪除 |
| 解密 timeout | kill process,`last_error="decrypt timeout"`,temp 子目錄刪除 |
| 暫存目錄無法建立(磁碟滿 / 權限) | 整個 ingestion run 中止 + exit code,提示權限 / 磁碟 |
| markitdown 個別檔失敗 | `files.status='failed'` + `last_error`,繼續下一檔(temp 子目錄仍會於 finally 刪除) |
| markitdown timeout | kill process,記 `last_error="markitdown timeout"` |
| ES bulk 部分失敗 | 該 chunks 留 `es_indexed_at=NULL`,run 結束前補,或下次 run 補 |
| ES bulk 全部失敗 | run 標 failed,DB chunks 已寫;下次靠 `es_indexed_at IS NULL` 補,無須重轉 |
| DB transaction 失敗 | rollback,標 failed,不寫 ES |
| 檔案在 run 中被刪 | 軟刪 + ES 刪 chunks,absolute file row 留作 audit |
| 進程被殺 | `ingestion_runs.finished_at_utc` 留 NULL,advisory lock 隨 connection 自動釋放;下次 `documentkb-ingest status` 顯示 abandoned;temp 子目錄可能殘留,doctor 可清理 |

---

## 5. MCP 介面(.NET 8 stdio,純讀)

### 5.1 Tool 清單

| Tool | 後端 | 用途 |
|---|---|---|
| `kb_search` | ES BM25 | 自然語言 / 關鍵字找 chunk 內容 |
| `kb_fetch_chunk` | SQL | 取單 chunk + 上下相鄰(citation 用) |
| `kb_query_documents` | OData → SQL (files) | 結構化篩檔案 |
| `kb_query_chunks` | OData → SQL (chunks) | 結構化篩 chunk metadata |

### 5.2 `kb_search`

**Input:**
```jsonc
{
  "query":         "string, required - 自然語言查詢或關鍵字",
  "top_k":         "int, default 8, max 30",
  "file_type":     "enum word|excel|null",
  "path_prefix":   "string|null - 相對路徑前綴",
  "min_score":     "float|null"
}
```

**Output:**
```jsonc
{
  "hits": [
    {
      "chunk_id":     12345,
      "file_id":      77,
      "file_name":    "2024-Q3-訂單彙整.xlsx",
      "relative_path":"財務/2024/2024-Q3-訂單彙整.xlsx",
      "file_type":    "excel",
      "title_path":   "訂單!A2:A21",
      "chunk_type":   "excel_rows",
      "score":        18.47,
      "snippet":      "...訂單編號 <em>A015</em> 客戶 王小明 金額 <em>1,200,000</em>..."
    }
  ],
  "total_matched":   42,
  "took_ms":         33,
  "query_echo":      "2024 訂單金額最大的客戶"
}
```

ES 查詢採 `multi_match` over `content^3, title_path^2, file_name`;`highlight` 從 `content`、`title_path` 取 snippet。

### 5.3 `kb_fetch_chunk`

**Input:** `{ "chunk_id": 12345, "include_neighbors": false }`

**Output:**
```jsonc
{
  "chunk_id":     12345,
  "file_id":      77,
  "file_name":    "2024-Q3-訂單彙整.xlsx",
  "relative_path":"財務/2024/2024-Q3-訂單彙整.xlsx",
  "absolute_path":"D:\\Docs\\財務\\2024\\2024-Q3-訂單彙整.xlsx",
  "file_type":    "excel",
  "title_path":   "訂單!A2:A21",
  "chunk_type":   "excel_rows",
  "locator":      { "type":"excel_rows","sheet":"訂單","start_row":2,"end_row":21,"header_row":1 },
  "content_md":   "| 訂單編號 | 客戶 | 金額 | 日期 |\n| --- | --- | --- | --- |\n| A001 | 王小明 | 1,200,000 | 2024-07-01 |\n...",
  "neighbors":    {
    "prev": { "chunk_id": 12344, "title_path": "訂單!Sheet summary",
              "chunk_type": "excel_sheet_summary", "content_md": "..." },
    "next": { "chunk_id": 12346, "title_path": "訂單!A22:F41",
              "chunk_type": "excel_rows", "content_md": "..." }
  },
  "ingested_at":  "2026-05-27T08:21:00Z"
}
```

`include_neighbors=true` 時一次回 prev + next 的 content_md 與位置,SKILL 不需再呼叫第二次。

### 5.4 `kb_query_documents` / `kb_query_chunks`(OData)

**EDM 模型暴露的欄位**

Files:
```
Id, RelativePath, AbsolutePath, FileName, FileType, SizeBytes,
MtimeUtc, Sha256, Status, IngestedAtUtc, CreatedAt, UpdatedAt
```
> `MarkdownFull` 不暴露。

Chunks:
```
Id, FileId, Ordinal, ChunkType, TitlePath, LocatorJson, CharLen,
EsIndexedAt, CreatedAt
// navigation: File (用 $expand=File 帶出)
```
> `ContentMd` 不暴露。要原文 → `kb_fetch_chunk`。

**Input:** `{ "odata": "$filter=...&$orderby=...&$top=...&$select=...&$count=true" }`

**Output:**
```jsonc
{
  "value": [ { "Id": 77, "FileName": "...", ... }, ... ],
  "@odata.count": 42        // 只在 $count=true 時出現
}
```

**OData 安全與限制(server 端強制)**

| 規則 | 值 |
|---|---|
| `$top` 預設 | 50 |
| `$top` 上限 | 200 |
| 沒帶 `$top` 也強制塞 50 | 是 |
| 允許 functions | `contains, startswith, endswith, tolower, toupper, length, year, month, day, now` |
| 允許 operators | `eq, ne, lt, le, gt, ge, and, or, not, in` |
| 禁用 | `$apply, $compute, $search`,server-side function call |
| `$expand` 深度 | ≤ 1 |
| `$select` 不可空字串 | 是 |
| Tool input 超過 4 KB | 拒絕 |

違規 → `error.code="INVALID_INPUT"` + 說明踩到哪條規則。

**OData 範例**

```text
# 列最近 7 天有更新的 Excel
$filter=FileType eq 'excel' and MtimeUtc gt 2026-05-21T00:00:00Z and Status eq 'active'
&$orderby=MtimeUtc desc
&$select=Id,FileName,RelativePath,MtimeUtc
&$top=20

# 檔名含「訂單」的文件,要 chunk 數
$filter=contains(FileName, '訂單') and Status eq 'active'
&$orderby=IngestedAtUtc desc
&$count=true

# 某檔的所有 chunk metadata,按順序
$filter=FileId eq 77
&$orderby=Ordinal asc
&$select=Id,Ordinal,TitlePath,ChunkType,CharLen

# 用 $expand 帶檔案資訊
$filter=ChunkType eq 'excel_sheet_summary'
&$expand=File($select=FileName,RelativePath)
&$top=50
```

### 5.5 錯誤回應

所有 tool 失敗時:
```jsonc
{ "error": { "code": "DB_UNAVAILABLE|ES_UNAVAILABLE|INVALID_INPUT|NOT_FOUND|INTERNAL",
             "message": "human readable",
             "details": {...} } }
```

### 5.6 啟動健康檢查

- 連 MariaDB 失敗 → 所有 tool 回 `DB_UNAVAILABLE`
- 連 ES 失敗 → 所有 tool 回 `ES_UNAVAILABLE`
- log **必須** 走 stderr / 檔案(stdout 留給 MCP 協定)

---

## 6. Claude SKILL 設計

### 6.1 檔案位置

`~/.claude/skills/documentkb/SKILL.md`,或專案層級 `<project>/.claude/skills/documentkb/SKILL.md`。

### 6.2 SKILL.md 內容草稿

````markdown
---
name: documentkb
description: Use when answering any question that might be covered by the local Word/Excel knowledge base — internal documents, specs, procedures, spreadsheets, reports stored in DocumentKB. Always cite evidence from the KB with original text and surrounding context.
---

# DocumentKB 問答紀律

你接到的問題只要**有 1% 可能**答案在本地 Word/Excel KB 裡,就先用本 skill。
**沒查 KB 就回答 = 違規。**

## 何時用哪個 tool

| 想做的事 | 用哪個 |
|---|---|
| 「2024 訂單金額最大的客戶是誰?」這類**內容問題** | kb_search → kb_fetch_chunk |
| 「KB 裡有沒有 XXX 相關的檔案?」 | kb_query_documents(`$filter=contains(FileName, 'XXX')`) |
| 「最近修改過哪些 Word?」 | kb_query_documents(`$filter=FileType eq 'word'&$orderby=MtimeUtc desc&$top=10`) |
| 「這份檔案有幾個 chunk?分別是什麼章節?」 | kb_query_chunks(`$filter=FileId eq 77&$select=Ordinal,TitlePath,ChunkType`) |
| 拿原文 | 只能用 kb_fetch_chunk |

## 工作流(內容問題)

### 步驟 1 — kb_search 找線索
- query 直接用使用者的自然語言問題;不要先濃縮成關鍵字
- 預設 `top_k=8`。初次結果都 < score 5 → 改寫關鍵詞再試,**最多 2 次**
- 從 `snippet` + `title_path` 選 2–5 個最相關的 chunk

### 步驟 2 — kb_fetch_chunk 拿完整原文 + 上下文
- 對每個選中的 chunk 一律呼叫 `kb_fetch_chunk(chunk_id, include_neighbors=true)`
- **禁止只憑 snippet 回答**

### 步驟 3 — 根據原文回答 + 列引用區段
- 答案只能根據 `kb_fetch_chunk` 拿到的 `content_md`,不准腦補
- 找不到 → 直接說「KB 內找不到」並列出已搜尋的關鍵字
- 每個事實後標 `[#chunk_id]`,答案結尾列引用區段(見 Citation 格式)

## Citation 格式(必須)

每個用到的 chunk 都這樣呈現一段:

---
**引用 [#chunk_id]** · [檔名](相對路徑) — *位置描述*

> ```
> <該 chunk 的 content_md 完整貼出,不省略>
> ```
>
> **上文 [#prev_chunk_id]** *位置描述*:
> ```
> <prev chunk 的 content_md;若無 prev 寫「(章節起始)」>
> ```
>
> **下文 [#next_chunk_id]** *位置描述*:
> ```
> <next chunk 的 content_md;若無 next 寫「(章節結尾)」>
> ```
---

### 範例

**問**:2024 Q3 訂單金額最高的客戶是誰?

**答**:王小明的訂單 A001 金額 1,200,000 元為當季最高 [#12345]。

---
**引用 [#12345]** · [2024-Q3-訂單彙整.xlsx](財務/2024/2024-Q3-訂單彙整.xlsx) — 訂單!A2:F21

> ```
> | 訂單編號 | 客戶 | 金額 | 日期 |
> | --- | --- | --- | --- |
> | A001 | 王小明 | 1,200,000 | 2024-07-01 |
> | A002 | 李大華 | 850,000 | 2024-07-03 |
> ```
>
> **上文 [#12344]** 訂單!Sheet summary
> ```
> Sheet「訂單」共 152 列,欄位:訂單編號 | 客戶 | 金額 | 日期 | 備註
> ```
>
> **下文 [#12346]** 訂單!A22:F41
> ```
> | A021 | 陳大同 | 760,000 | 2024-07-15 |
> ```
---

## 找不到就明說

回答模板:

> KB 內找不到。已搜尋:`<query 1>`、`<query 2>`。
> 若該文件最近才加入,請在 terminal 跑:
>   documentkb-ingest reindex
> 然後再問我一次。

## OData 寫法守則

- 字串值用單引號:`'excel'`、`'訂單'`
- 日期值用 ISO 8601:`2026-05-21T00:00:00Z`
- 不知道欄位 → 對 Files / Chunks 各跑 `$top=1` 看回傳結構
- 寫錯被擋(INVALID_INPUT)→ 看 server 回的規則訊息修正,**不要硬猜**

## 反模式

| 錯誤 | 正確做法 |
|---|---|
| 只看 snippet 就回答 | 一定要 kb_fetch_chunk 拿全文 |
| 沒找到答案就用通識補 | 直接說「KB 內找不到」 |
| 一次只查一個關鍵字 | query 用整句自然語言 |
| 用 markdown table 抄整個 Excel | 只回答問到的列,citation 指向 row range |
| 不附 citation | 違規。每個事實都要有 chunk #id + 引用區段 |

## 不主動 reindex

- 本 SKILL **沒有** reindex 工具
- 不嘗試用 shell 自行執行 ingest 指令
- 找不到答案時建議使用者跑 `documentkb-ingest reindex`
````

---

## 7. 設定檔

`appsettings.json`(Ingestion 與 MCP 共用 schema,各自取需要的區段):

```jsonc
{
  "SourceFolder": "D:/Docs/KB",
  "PythonExe": "python",
  "Decryption": {
    "Enabled":         true,
    "PwshExe":         "pwsh",
    "ScriptPath":      "C:/Tools/DocumentKB/scripts/Aip-Decrypt.ps1",
    "TimeoutSeconds":  60,
    "TempRoot":        null,
    "ExtraArgs":       []
  },
  "Markitdown": {
    "TimeoutSeconds": 60,
    "ExtraArgs": []
  },
  "Chunking": {
    "MaxChunkChars": 2000,
    "ExcelRowsPerChunk": 20,
    "IncludeExcelHeaderInEveryChunk": true
  },
  "MariaDb": {
    "ConnectionString": "Server=localhost;Database=documentkb;User=kb;Password=***;CharSet=utf8mb4"
  },
  "ElasticSearch": {
    "Uri": "http://localhost:9200",
    "Index": "kb_chunks",
    "BulkBatchSize": 200,
    "RequestTimeoutSeconds": 30
  },
  "Odata": {
    "MaxTop": 200,
    "DefaultTop": 50,
    "MaxInputBytes": 4096
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft": "Warning" }
  }
}
```

MCP 啟動時忽略 `SourceFolder`、`PythonExe`、`Decryption`、`Markitdown`、`Chunking`(只 ingestion 用)。

---

## 8. 專案結構

```
DocumentKB.sln
├── src/
│   ├── DocumentKB.Core/
│   │   ├── Entities/
│   │   ├── Persistence/        # KbDbContext, migrations runner
│   │   ├── Search/             # KbSearchClient (ES wrapper)
│   │   ├── Chunking/           # WordChunker, ExcelChunker
│   │   ├── Decryption/         # DecryptionRunner (PowerShell)
│   │   ├── Markitdown/         # MarkitdownRunner
│   │   ├── Ingestion/          # IngestionPipeline
│   │   └── Odata/              # EDM model + validator + applier
│   ├── DocumentKB.Ingestion/   # CLI
│   │   └── Program.cs          # System.CommandLine
│   └── DocumentKB.Mcp/         # stdio MCP
│       └── Program.cs          # ModelContextProtocol C# SDK
├── tests/
│   ├── DocumentKB.Core.Tests/
│   ├── DocumentKB.Ingestion.Tests/
│   ├── DocumentKB.Mcp.Tests/
│   └── fixtures/               # 樣本 docx / xlsx
├── deploy/
│   ├── appsettings.sample.json
│   ├── migrations/             # 純 SQL 檔
│   ├── scripts/
│   │   └── Aip-Decrypt.ps1.sample   # 解密腳本範本 (-InputPath/-OutputPath)
│   └── claude-mcp-config.example.json
├── skill/
│   └── documentkb/
│       └── SKILL.md
└── docs/
    └── superpowers/specs/
        └── 2026-05-28-documentkb-design.md
```

### 第三方相依

**.NET**(NuGet):
- `Microsoft.Extensions.Hosting` 8.x
- `Microsoft.EntityFrameworkCore` 8.x
- `Pomelo.EntityFrameworkCore.MySql` 8.x
- `Microsoft.AspNetCore.OData` 8.x(parser only,不起 HTTP server)
- `Elastic.Clients.Elasticsearch` 8.x
- `ModelContextProtocol`(MCP C# SDK)
- `System.CommandLine`
- `Serilog.Extensions.Hosting` + `Serilog.Sinks.File` + `Serilog.Sinks.Console`
- 測試:`xUnit`, `FluentAssertions`, `Testcontainers`

**Python**:
- `markitdown[all]`

---

## 9. 部署(Windows 個人 / 小團隊)

**前置**(一次性):
1. 裝 Python 3.10+,`pip install markitdown[all]`
2. 裝 MariaDB 10.6+,建 db `documentkb` + user
3. 裝 ElasticSearch 8.x(本機 service 或 single-node Docker)
4. 裝 .NET 8 runtime
5. 裝 PowerShell 7+(`pwsh`) 或確認 Windows PowerShell 5.1 可用;依 AIP / RMS 環境準備解密腳本(以 `deploy/scripts/Aip-Decrypt.ps1.sample` 為基底)

**安裝**:
1. `dotnet publish src/DocumentKB.Ingestion -c Release -o C:\Tools\DocumentKB\Ingestion`
2. `dotnet publish src/DocumentKB.Mcp -c Release -o C:\Tools\DocumentKB\Mcp`
3. 複製 `deploy/appsettings.sample.json` 到兩個目錄並改設定
4. 把解密腳本放到 `Decryption.ScriptPath` 指向的位置
5. 首次 `documentkb-ingest doctor` 確認連線 + markitdown + 解密腳本可叫
6. 首次 `documentkb-ingest reindex` 建索引

**Claude Code MCP 設定**(`~/.claude.json` 或 `<project>/.mcp.json`):
```jsonc
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

**SKILL 安裝**:複製 `skill/documentkb/SKILL.md` 到 `~/.claude/skills/documentkb/SKILL.md`。

**定期同步**(可選):Windows Task Scheduler 排程 `documentkb-ingest reindex`。

---

## 10. CLI 指令清單

```bash
documentkb-ingest reindex                        # 預設 SHA 比對,只處理新/異動
documentkb-ingest reindex --force                # 忽略 SHA 全部重轉
documentkb-ingest reindex --path "財務/2024/"    # 限定子資料夾
documentkb-ingest status                         # 整體統計 + 最後 run 摘要 + failed_files
documentkb-ingest doctor                         # 健康檢查 / 驗證設定(含解密腳本可叫)
documentkb-ingest test-decrypt --file <path>     # 對單一檔案跑解密,印 stdout/stderr/exit + 保留 temp 路徑
documentkb-ingest export-markdown --file-id 77   # 印 files.markdown_full(debug)
```

---

## 11. 測試策略

| 層 | 工具 | 重點 |
|---|---|---|
| Unit | xUnit + FluentAssertions | 切分器(Word heading / Excel row)、SHA 比對、OData 白名單 validator、locator_json 生成 |
| Integration (DB) | xUnit + Testcontainers (MariaDB) | EF schema 遷移、upsert、ON DELETE CASCADE、advisory lock |
| Integration (ES) | xUnit + Testcontainers (ES) | analyzer 中英混合切詞、bulk index、`es_indexed_at` 補償、_id 對齊 |
| Integration (ingest E2E) | xUnit + 樣本檔 | 5 個 fixture(2 Word + 2 Excel + 1 壞檔)走完整 pipeline,DB / ES 結果正確 |
| Integration (MCP tool) | xUnit | 4 個 tool 對 sample 的 input/output snapshot;OData 違規被擋 |
| Smoke (CI) | dotnet test | Testcontainers 自啟自關,全合集 ≤ 5 分鐘 |
| 手動驗收 | Claude Code | 真實樣本 + SKILL 對話,人工檢視 citation 正確性 |

---

## 12. 階段切分

### M1 — 骨架可跑通

- DocumentKB.Core entities、KbDbContext、migrations
- WordChunker(heading-based)+ ExcelChunker(20 列/塊 + sheet summary)
- **DecryptionRunner**(Process.Start pwsh + timeout + temp 子目錄)
- MarkitdownRunner(Process.Start + timeout)
- IngestionPipeline(scan → hash → **decrypt** → 轉檔 → 切分 → DB upsert → ES bulk)
- `documentkb-ingest` CLI:`reindex`、`doctor`
- 5 個 fixture 走完整流程(含一個 AIP 加密樣本)
- **驗收**:能對樣本(含加密檔)完成索引,DB / ES 數據正確

### M2 — MCP 查詢可用

- DocumentKB.Mcp stdio server + 3 個 query tool(`kb_search`、`kb_fetch_chunk`、`kb_query_documents`)
- ES BM25 + highlight
- OData parser + validator + EF apply
- Claude Code 接上,手動跑通 1 個對話:問題 → search → fetch → 回答 + citation
- **驗收**:Claude 用真實樣本回答並列出正確 citation

### M3 — 完整 + 韌性

- `kb_query_chunks`(第 4 個 tool)
- IngestionPipeline 失敗補償(es_indexed_at 補 index、軟刪)
- CLI:`reindex --force`、`reindex --path`、`status`、`export-markdown`、`test-decrypt`
- `doctor` 增加解密腳本可叫的檢查
- Advisory lock
- Serilog 完整 wiring
- SKILL.md 最終版 + 範例對話
- 全套 integration test
- **驗收**:壞檔(含解密失敗) + 部分 ES 失敗 + 中斷重啟,無資料損失復原、temp 明文不殘留

### M4 — 後續(不在本 spec)

- HTTP / SSE remote MCP
- ICU / IK 中文分詞
- Embedding + 向量檢索
- 移動 / 重命名偵測(`--alias-from`)
- Web UI

---

## 13. 風險與權衡備忘

| 主題 | 採用 | 為何不選另一邊 |
|---|---|---|
| 檢索後端 | 純 BM25 | 規模小、無 embedding 服務需求 |
| Markitdown 整合 | CLI 子進程 | < 5000 份規模 spawn 成本可承受;不需常駐 sidecar |
| 寫入路徑 | 獨立 CLI,MCP 完全純讀 | 職責分離、MCP 進程穩、LLM 不可能誤觸發 reindex |
| DB / ES 角色 | DB 為 source of truth,ES 為 derived | ES 可隨時重建,citation / 一致性容易維持 |
| MCP 查詢介面 | search/fetch 固定 + 結構化用 OData | 給 Claude 高彈性、不必預想所有用例;OData 標準 parser 比手 craft SQL 安全 |
| Excel 切分粒度 | 20 列/塊 + 重複表頭 | 每列一 chunk 量爆炸;30 列覺得太大,使用者選 20 |
| 中文分詞 | cjk_bigram(內建) | 零安裝;之後可換 ICU / IK 加 reindex 升級 |
| 大欄位暴露 | 不暴露於 OData | 防 response 爆;有需要走 `kb_fetch_chunk` |
| 解密 | 呼叫使用者提供的 PowerShell script,`-InputPath` / `-OutputPath` 約定 | .NET 不 sniff AIP 標頭、不綁定特定 SDK;明文僅留於 `%TEMP%` 的 GUID 子目錄,try/finally 強制清除 |
