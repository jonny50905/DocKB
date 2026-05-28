namespace DocumentKB.Core.Configuration;

public sealed class KbOptions
{
    public string SourceFolder { get; init; } = "";
    public string PythonExe { get; init; } = "python";
    public DecryptionOptions Decryption { get; init; } = new();
    public MarkitdownOptions Markitdown { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public MariaDbOptions MariaDb { get; init; } = new();
    public ElasticSearchOptions ElasticSearch { get; init; } = new();
    public OdataOptions Odata { get; init; } = new();
}

public sealed class DecryptionOptions
{
    public bool Enabled { get; init; } = true;
    public string PwshExe { get; init; } = "pwsh";
    public string ScriptPath { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
    public string? TempRoot { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

public sealed class MarkitdownOptions
{
    public int TimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

public sealed class ChunkingOptions
{
    public int MaxChunkChars { get; init; } = 2000;
    public int ExcelRowsPerChunk { get; init; } = 20;
    public bool IncludeExcelHeaderInEveryChunk { get; init; } = true;
}

public sealed class MariaDbOptions
{
    public string ConnectionString { get; init; } = "";
}

public sealed class ElasticSearchOptions
{
    public string Uri { get; init; } = "http://localhost:9200";
    public string Index { get; init; } = "kb_chunks";
    public int BulkBatchSize { get; init; } = 200;
    public int RequestTimeoutSeconds { get; init; } = 30;
}

public sealed class OdataOptions
{
    public int MaxTop { get; init; } = 200;
    public int DefaultTop { get; init; } = 50;
    public int MaxInputBytes { get; init; } = 4096;
}
