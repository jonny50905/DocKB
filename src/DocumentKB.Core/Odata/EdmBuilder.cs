using DocumentKB.Core.Entities;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace DocumentKB.Core.Odata;

public static class EdmBuilder
{
    public static IEdmModel Build()
    {
        var b = new ODataConventionModelBuilder();
        var files = b.EntitySet<FileEntity>("Files");
        files.EntityType.HasKey(x => x.Id);
        files.EntityType.Ignore(x => x.MarkdownFull);
        files.EntityType.Ignore(x => x.Chunks);

        var chunks = b.EntitySet<ChunkEntity>("Chunks");
        chunks.EntityType.HasKey(x => x.Id);
        chunks.EntityType.Ignore(x => x.ContentMd);

        return b.GetEdmModel();
    }
}
