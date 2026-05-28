using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using AwesomeAssertions;
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
