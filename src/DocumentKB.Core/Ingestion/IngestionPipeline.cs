using DocumentKB.Core.Chunking;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Decryption;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Markitdown;
using DocumentKB.Core.Persistence;
using DocumentKB.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentKB.Core.Ingestion;

public sealed record IngestionResult(
    long RunId, int Scanned, int New, int Updated, int Deleted, int Failed);

public sealed class IngestionPipeline(
    IOptions<KbOptions> options,
    KbDbContext db,
    FileScanner scanner,
    DecryptionRunner decrypt,
    MarkitdownRunner markitdown,
    WordChunker wordChunker,
    ExcelChunker excelChunker,
    KbSearchClient searchClient,
    ILogger<IngestionPipeline> log)
{
    public async Task<IngestionResult> RunAsync(
        string? pathPrefix, bool force, CancellationToken ct)
    {
        var root = options.Value.SourceFolder;
        var run = new IngestionRunEntity { StartedAtUtc = DateTime.UtcNow };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var scanned = scanner.Scan(root)
            .Where(f => pathPrefix is null ||
                f.RelativePath.StartsWith(pathPrefix.TrimEnd('/') + "/")
                || f.RelativePath == pathPrefix)
            .ToList();
        run.ScannedCount = scanned.Count;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scanned)
        {
            seen.Add(f.RelativePath);
            try
            {
                await ProcessOneAsync(f, run, force, ct);
            }
            catch (Exception ex)
            {
                run.FailedCount++;
                log.LogError(ex, "Failed to ingest {File}", f.RelativePath);
                await MarkFileFailedAsync(f, ex.Message, ct);
            }
        }

        // Soft delete files no longer on disk
        var prefixWithSep = pathPrefix is null ? null : pathPrefix.TrimEnd('/') + "/";
        var activeRows = await db.Files
            .Where(x => x.Status == FileStatus.Active
                && (pathPrefix == null
                    || x.RelativePath == pathPrefix
                    || x.RelativePath.StartsWith(prefixWithSep!)))
            .Select(x => new { x.Id, x.RelativePath })
            .ToListAsync(ct);
        foreach (var row in activeRows)
        {
            if (seen.Contains(row.RelativePath)) continue;
            await db.Files
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, FileStatus.Deleted)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
            await searchClient.DeleteByFileIdAsync(row.Id, ct);
            run.DeletedCount++;
        }

        // Backfill ES for chunks with es_indexed_at = NULL
        var pending = await db.Chunks
            .Include(c => c.File)
            .Where(c => c.EsIndexedAt == null)
            .Take(1000).ToListAsync(ct);
        if (pending.Count > 0)
        {
            var docs = pending.Select(c => KbSearchClient.ToDoc(c.File, c));
            await searchClient.BulkUpsertAsync(docs, ct);
            var now = DateTime.UtcNow;
            foreach (var c in pending) c.EsIndexedAt = now;
            await db.SaveChangesAsync(ct);
        }

        run.FinishedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new IngestionResult(
            run.Id, run.ScannedCount, run.NewCount, run.UpdatedCount, run.DeletedCount, run.FailedCount);
    }

    private async Task ProcessOneAsync(
        ScannedFile f, IngestionRunEntity run, bool force, CancellationToken ct)
    {
        var sha = await Sha256Hasher.HashAsync(f.AbsolutePath, ct);
        var existing = await db.Files
            .FirstOrDefaultAsync(x => x.RelativePath == f.RelativePath, ct);
        if (!force && existing is not null
            && existing.Sha256 == sha && existing.Status == FileStatus.Active) return;

        await using var tempPlain = await decrypt.DecryptAsync(f.AbsolutePath, ct);
        var md = await markitdown.ConvertAsync(tempPlain.Path, ct);

        var chunker = f.FileType == FileType.Word
            ? (IChunker)wordChunker : excelChunker;
        var drafts = chunker.Chunk(md);

        var now = DateTime.UtcNow;
        var entity = existing ?? new FileEntity
            { RelativePath = f.RelativePath, CreatedAt = now };
        entity.AbsolutePath = f.AbsolutePath;
        entity.FileName = f.FileName;
        entity.FileType = f.FileType;
        entity.SizeBytes = f.SizeBytes;
        entity.MtimeUtc = f.MtimeUtc;
        entity.Sha256 = sha;
        entity.Status = FileStatus.Active;
        entity.LastError = null;
        entity.MarkdownFull = md;
        entity.IngestedAtUtc = now;
        entity.UpdatedAt = now;
        if (existing is null) db.Files.Add(entity);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (existing is not null)
            await db.Chunks.Where(c => c.FileId == entity.Id).ExecuteDeleteAsync(ct);
        var chunkEntities = drafts.Select(d => new ChunkEntity
        {
            FileId = entity.Id,
            Ordinal = d.Ordinal,
            ChunkType = d.ChunkType,
            TitlePath = d.TitlePath,
            LocatorJson = d.Locator?.ToJsonString(),
            ContentMd = d.ContentMd,
            CharLen = d.ContentMd.Length,
            EsIndexedAt = null,
            CreatedAt = now,
        }).ToList();
        db.Chunks.AddRange(chunkEntities);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        try
        {
            await searchClient.BulkUpsertAsync(
                chunkEntities.Select(c => KbSearchClient.ToDoc(entity, c)), ct);
            var ts = DateTime.UtcNow;
            foreach (var c in chunkEntities) c.EsIndexedAt = ts;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ES bulk failed; will retry on next run");
        }

        if (existing is null) run.NewCount++; else run.UpdatedCount++;
    }

    private async Task MarkFileFailedAsync(ScannedFile f, string error, CancellationToken ct)
    {
        var existing = await db.Files
            .FirstOrDefaultAsync(x => x.RelativePath == f.RelativePath, ct);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.Files.Add(new FileEntity
            {
                RelativePath = f.RelativePath,
                AbsolutePath = f.AbsolutePath,
                FileName = f.FileName,
                FileType = f.FileType,
                SizeBytes = f.SizeBytes,
                MtimeUtc = f.MtimeUtc,
                Sha256 = "",
                Status = FileStatus.Failed,
                LastError = error,
                IngestedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Status = FileStatus.Failed;
            existing.LastError = error;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }
}
