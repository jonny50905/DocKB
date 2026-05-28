using System.Collections;
using DocumentKB.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.OData.Edm;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryRunner(OdataOptions options)
{
    /// <summary>
    /// Applies an OData query string to <paramref name="source"/> and materializes the result.
    /// </summary>
    /// <remarks>
    /// When the query contains <c>$select</c>/<c>$expand</c>, <see cref="ODataQueryOptions{T}.ApplyTo"/>
    /// returns an <c>IQueryable</c> whose element type is an OData projection wrapper
    /// (<see cref="ISelectExpandWrapper"/>, e.g. <c>SelectSome&lt;T&gt;</c>) backed by an in-memory
    /// <c>EnumerableQuery</c> — NOT the original <typeparamref name="T"/> and NOT an EF async source.
    /// So we must (a) avoid casting back to <c>IQueryable&lt;T&gt;</c>, and (b) enumerate synchronously
    /// rather than via <c>ToListAsync</c>. Each projection wrapper is flattened to a plain dictionary
    /// so it serializes as a normal JSON object. Result sets are bounded by <c>$top</c>/PageSize, so
    /// synchronous enumeration is acceptable.
    /// </remarks>
    public (IReadOnlyList<object> Items, long? Count) Apply<T>(
        IQueryable<T> source, IEdmModel model, string entitySetName, string odata)
    {
        var entitySet = model.FindDeclaredEntitySet(entitySetName)
            ?? throw new InvalidOperationException($"entity set {entitySetName} missing");
        var path = new Microsoft.OData.UriParser.ODataPath(
            new Microsoft.OData.UriParser.EntitySetSegment(entitySet));
        var ctx = new ODataQueryContext(model, typeof(T), path);

        // Without $select/$expand, ApplyTo returns the raw CLR entity, which System.Text.Json
        // would serialize in full — INCLUDING large columns the EDM model deliberately Ignores
        // (FileEntity.MarkdownFull, ChunkEntity.ContentMd). EDM Ignore only constrains OData
        // projection, not CLR serialization. To honour the "large fields never leave via OData"
        // contract (design §5.4), inject a default $select of every EDM-exposed structural
        // property so the result always flows through projection wrappers (which omit ignored
        // fields). $expand alone also yields a wrapper, so only inject when neither is present.
        var effectiveOData = odata;
        var hasSelect = odata.Contains("$select", StringComparison.OrdinalIgnoreCase);
        var hasExpand = odata.Contains("$expand", StringComparison.OrdinalIgnoreCase);
        if (!hasSelect && !hasExpand)
        {
            var exposed = string.Join(",", entitySet.EntityType.StructuralProperties().Select(p => p.Name));
            effectiveOData = string.IsNullOrEmpty(odata) ? $"$select={exposed}" : $"{odata}&$select={exposed}";
        }

        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = $"/{entitySetName}";
        http.Request.QueryString = new QueryString("?" + effectiveOData);

        var opts = new ODataQueryOptions<T>(ctx, http.Request);

        // $count must reflect the filtered set BEFORE paging ($top/$skip). GetEntityCount on the
        // raw source would ignore $filter, so apply just the filter first when a count is requested.
        long? count = null;
        if (opts.Count?.Value == true)
        {
            IQueryable filtered = opts.Filter is not null
                ? opts.Filter.ApplyTo(source, new ODataQuerySettings())
                : source;
            count = opts.Count.GetEntityCount(filtered);
        }

        var settings = new ODataQuerySettings
        {
            PageSize = Math.Min(options.MaxTop, options.DefaultTop),
            EnsureStableOrdering = true,
        };
        var applied = opts.ApplyTo(source, settings, AllowedQueryOptions.None);

        var items = new List<object>();
        foreach (var item in (IEnumerable)applied)
        {
            if (item is null) continue;
            items.Add(item is ISelectExpandWrapper wrapper ? wrapper.ToDictionary() : item);
        }
        return (items, count);
    }
}
