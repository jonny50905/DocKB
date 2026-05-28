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
