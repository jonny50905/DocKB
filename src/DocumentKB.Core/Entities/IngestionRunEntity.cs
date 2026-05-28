namespace DocumentKB.Core.Entities;

public sealed class IngestionRunEntity
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int ScannedCount { get; set; }
    public int NewCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeletedCount { get; set; }
    public int FailedCount { get; set; }
    public string? Notes { get; set; }
}
