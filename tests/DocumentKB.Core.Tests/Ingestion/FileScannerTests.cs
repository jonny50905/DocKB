using DocumentKB.Core.Entities;
using DocumentKB.Core.Ingestion;
using FluentAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Ingestion;

public class FileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "kb-scanner-" + Guid.NewGuid().ToString("N"));

    public FileScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private void Touch(string rel)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Array.Empty<byte>());
    }

    [Fact]
    public void FindsDocxAndXlsxRecursively()
    {
        Touch("a.docx");
        Touch("sub/b.xlsx");
        Touch("sub/deeper/c.docx");

        var found = new FileScanner().Scan(_root).ToList();

        found.Should().HaveCount(3);
        found.Select(f => f.FileType).Should().Contain(new[] {
            FileType.Word, FileType.Excel, FileType.Word });
    }

    [Fact]
    public void SkipsOfficeLockFiles()
    {
        Touch("a.docx");
        Touch("~$a.docx");
        var found = new FileScanner().Scan(_root).ToList();
        found.Should().ContainSingle(f => f.RelativePath == "a.docx");
    }

    [Fact]
    public void RelativePathUsesForwardSlashes()
    {
        Touch("sub/b.xlsx");
        var found = new FileScanner().Scan(_root).Single();
        found.RelativePath.Should().Be("sub/b.xlsx");
    }
}
