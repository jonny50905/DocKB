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
