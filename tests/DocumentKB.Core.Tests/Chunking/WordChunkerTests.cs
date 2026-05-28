using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using AwesomeAssertions;
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
        chunks.Should().HaveCount(4);
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
