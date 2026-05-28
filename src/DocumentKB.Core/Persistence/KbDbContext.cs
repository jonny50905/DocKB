using DocumentKB.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocumentKB.Core.Persistence;

public sealed class KbDbContext(DbContextOptions<KbDbContext> options) : DbContext(options)
{
    private static string ChunkTypeToString(ChunkType v) => v switch
    {
        ChunkType.WordSection        => "word_section",
        ChunkType.ExcelSheetSummary  => "excel_sheet_summary",
        ChunkType.ExcelRows          => "excel_rows",
        _                            => throw new InvalidOperationException()
    };

    private static ChunkType StringToChunkType(string s) => s switch
    {
        "word_section"         => ChunkType.WordSection,
        "excel_sheet_summary"  => ChunkType.ExcelSheetSummary,
        "excel_rows"           => ChunkType.ExcelRows,
        _                      => throw new InvalidOperationException()
    };

    public DbSet<FileEntity> Files => Set<FileEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<IngestionRunEntity> IngestionRuns => Set<IngestionRunEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FileEntity>(e =>
        {
            e.ToTable("files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RelativePath).HasColumnName("relative_path").IsRequired();
            e.Property(x => x.AbsolutePath).HasColumnName("absolute_path").IsRequired();
            e.Property(x => x.FileName).HasColumnName("file_name").IsRequired();
            e.Property(x => x.FileType).HasColumnName("file_type")
                .HasConversion<string>().HasMaxLength(8); // NOTE: writes "Word"/"Excel"; SQL ENUM expects 'word'/'excel' — Task 24 integration test will surface any mismatch
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.MtimeUtc).HasColumnName("mtime_utc");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status")
                .HasConversion<string>().HasMaxLength(8); // NOTE: writes "Active"/"Deleted"/"Failed"; SQL ENUM expects 'active'/'deleted'/'failed' — Task 24 integration test will surface any mismatch
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.MarkdownFull).HasColumnName("markdown_full");
            e.Property(x => x.IngestedAtUtc).HasColumnName("ingested_at_utc");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.RelativePath).IsUnique();
        });

        b.Entity<ChunkEntity>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FileId).HasColumnName("file_id");
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.ChunkType).HasColumnName("chunk_type")
                .HasConversion(new ValueConverter<ChunkType, string>(
                    v => ChunkTypeToString(v),
                    s => StringToChunkType(s)));
            e.Property(x => x.TitlePath).HasColumnName("title_path");
            e.Property(x => x.LocatorJson).HasColumnName("locator_json");
            e.Property(x => x.ContentMd).HasColumnName("content_md");
            e.Property(x => x.CharLen).HasColumnName("char_len");
            e.Property(x => x.EsIndexedAt).HasColumnName("es_indexed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.File).WithMany(f => f.Chunks).HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FileId, x.Ordinal }).IsUnique();
        });

        b.Entity<IngestionRunEntity>(e =>
        {
            e.ToTable("ingestion_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
            e.Property(x => x.ScannedCount).HasColumnName("scanned_count");
            e.Property(x => x.NewCount).HasColumnName("new_count");
            e.Property(x => x.UpdatedCount).HasColumnName("updated_count");
            e.Property(x => x.DeletedCount).HasColumnName("deleted_count");
            e.Property(x => x.FailedCount).HasColumnName("failed_count");
            e.Property(x => x.Notes).HasColumnName("notes");
        });
    }
}
