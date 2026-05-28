using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Chunking;

public sealed record ChunkDraft(
    int Ordinal, ChunkType ChunkType, string? TitlePath,
    LocatorJson? Locator, string ContentMd);

public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(string markdown);
}
