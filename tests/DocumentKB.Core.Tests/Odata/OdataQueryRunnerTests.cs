using AwesomeAssertions;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;
using DocumentKB.Core.Odata;
using Xunit;

namespace DocumentKB.Core.Tests.Odata;

public class OdataQueryRunnerTests
{
    private static readonly OdataOptions Opts =
        new() { MaxTop = 200, DefaultTop = 50, MaxInputBytes = 4096 };

    private static IQueryable<FileEntity> SampleFiles() => new[]
    {
        new FileEntity { Id = 1, FileName = "a.docx", RelativePath = "a.docx",
            FileType = FileType.Word, Status = FileStatus.Active, MtimeUtc = new DateTime(2026, 1, 1) },
        new FileEntity { Id = 2, FileName = "b.xlsx", RelativePath = "b.xlsx",
            FileType = FileType.Excel, Status = FileStatus.Active, MtimeUtc = new DateTime(2026, 2, 1) },
        new FileEntity { Id = 3, FileName = "訂單.xlsx", RelativePath = "fin/訂單.xlsx",
            FileType = FileType.Excel, Status = FileStatus.Active, MtimeUtc = new DateTime(2026, 3, 1) },
    }.AsQueryable();

    private static OdataQueryRunner Runner() => new(Opts);

    [Fact]
    public void Filter_ByStringFunction_ReturnsMatchingEntities()
    {
        var (items, count) = Runner().Apply(
            SampleFiles(), EdmBuilder.Build(), "Files", "$filter=endswith(FileName,'xlsx')");

        items.Should().HaveCount(2);
        count.Should().BeNull();
        items.Should().AllBeOfType<FileEntity>();
    }

    [Fact]
    public void Filter_ByEnum_UsesNamespaceQualifiedLiteral()
    {
        // FileType is a CLR enum, so the EDM exposes it as an enum type. OData therefore
        // requires the namespace-qualified enum-literal form, NOT a bare string 'excel'.
        var (items, _) = Runner().Apply(
            SampleFiles(), EdmBuilder.Build(), "Files",
            "$filter=FileType eq DocumentKB.Core.Entities.FileType'Excel'");

        items.Should().HaveCount(2);
        items.Should().AllBeOfType<FileEntity>();
    }

    [Fact]
    public void Select_ReturnsProjectionDictionaries_NotEntities()
    {
        // Regression: with $select, ApplyTo yields SelectSome<FileEntity> wrappers backed by
        // EnumerableQuery. The runner must flatten them to dictionaries instead of casting
        // back to IQueryable<FileEntity> (which threw InvalidCastException before the fix).
        var (items, count) = Runner().Apply(
            SampleFiles(), EdmBuilder.Build(), "Files",
            "$filter=endswith(FileName,'xlsx')&$select=Id,FileName&$orderby=Id asc");

        items.Should().HaveCount(2);
        count.Should().BeNull();

        var first = items[0].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        first.Should().ContainKey("Id");
        first.Should().ContainKey("FileName");
        // hidden / unselected columns must NOT leak into the projection
        first.Should().NotContainKey("MarkdownFull");
        first.Should().NotContainKey("MtimeUtc");
        first["Id"].Should().Be(2L);
        first["FileName"].Should().Be("b.xlsx");
    }

    [Fact]
    public void Count_IsReturnedWhenRequested()
    {
        var (items, count) = Runner().Apply(
            SampleFiles(), EdmBuilder.Build(), "Files",
            "$filter=endswith(FileName,'xlsx')&$count=true");

        count.Should().Be(2);
        items.Should().HaveCount(2);
    }

    [Fact]
    public void OrderByAndTop_AreApplied()
    {
        var (items, _) = Runner().Apply(
            SampleFiles(), EdmBuilder.Build(), "Files",
            "$orderby=MtimeUtc desc&$top=1");

        items.Should().HaveCount(1);
        items[0].Should().BeOfType<FileEntity>()
            .Which.Id.Should().Be(3); // newest MtimeUtc
    }
}
