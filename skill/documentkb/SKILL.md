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
| 「最近修改過哪些 Word?」 | kb_query_documents(`$filter=endswith(FileName,'.docx')&$orderby=MtimeUtc desc&$top=10`) |
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

- 字串值用單引號:`contains(FileName, '訂單')`、`endswith(FileName, '.xlsx')`
- 日期值用 ISO 8601:`MtimeUtc gt 2026-05-21T00:00:00Z`
- **`FileType` / `Status` 是 enum,不是字串** — 不能寫 `FileType eq 'word'`(會回 INVALID_INPUT/ODataException)。改用其中一種:
  - 用副檔名篩:Word → `endswith(FileName,'.docx')`,Excel → `endswith(FileName,'.xlsx')`(最直覺,推薦)
  - 或用 enum 字面值:`FileType eq DocumentKB.Core.Entities.FileType'Word'`(成員名 `Word`/`Excel` 大小寫須正確);Status 同理 `Status eq DocumentKB.Core.Entities.FileStatus'Active'`
- 投影/排序欄位用實體屬性名(PascalCase):`Id`、`FileName`、`RelativePath`、`MtimeUtc`、`Ordinal`、`TitlePath`、`ChunkType`
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
