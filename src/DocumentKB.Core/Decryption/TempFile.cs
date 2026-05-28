namespace DocumentKB.Core.Decryption;

public sealed class TempFile : IAsyncDisposable
{
    public string Path { get; }
    private readonly string? _ownedDirectory;

    private TempFile(string path, string? ownedDirectory)
    {
        Path = path;
        _ownedDirectory = ownedDirectory;
    }

    public static TempFile Passthrough(string path) => new(path, null);
    public static TempFile Owned(string path, string ownedDirectory) => new(path, ownedDirectory);

    public ValueTask DisposeAsync()
    {
        if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
        {
            try { Directory.Delete(_ownedDirectory, recursive: true); }
            catch { }
        }
        return ValueTask.CompletedTask;
    }
}
