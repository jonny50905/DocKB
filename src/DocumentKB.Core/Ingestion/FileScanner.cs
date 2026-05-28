using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Ingestion;

public sealed record ScannedFile(
    string AbsolutePath, string RelativePath, string FileName,
    FileType FileType, long SizeBytes, DateTime MtimeUtc);

public sealed class FileScanner
{
    public IEnumerable<ScannedFile> Scan(string root)
    {
        var rootFull = Path.GetFullPath(root);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(path);
            if (fi.Name.StartsWith("~$")) continue;
            var type = fi.Extension.ToLowerInvariant() switch
            {
                ".docx" => FileType.Word,
                ".xlsx" => FileType.Excel,
                _ => (FileType?)null
            };
            if (type is null) continue;

            var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            yield return new ScannedFile(
                path, rel, fi.Name, type.Value, fi.Length, fi.LastWriteTimeUtc);
        }
    }
}
