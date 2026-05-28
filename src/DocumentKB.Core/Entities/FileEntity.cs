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
