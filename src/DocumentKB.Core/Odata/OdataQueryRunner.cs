using DocumentKB.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.Edm;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryRunner(OdataOptions options)
{
    public (IQueryable Result, long? Count) Apply<T>(
        IQueryable<T> source, IEdmModel model, string entitySetName, string odata)
    {
        var entitySet = model.FindDeclaredEntitySet(entitySetName)
            ?? throw new InvalidOperationException($"entity set {entitySetName} missing");
        var path = new Microsoft.OData.UriParser.ODataPath(
            new Microsoft.OData.UriParser.EntitySetSegment(entitySet));
        var ctx = new ODataQueryContext(model, typeof(T), path);

        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = $"/{entitySetName}";
        http.Request.QueryString = new QueryString("?" + odata);

        var opts = new ODataQueryOptions<T>(ctx, http.Request);
        long? count = null;
        if (opts.Count?.Value == true) count = opts.Count.GetEntityCount(source);

        var settings = new ODataQuerySettings
        {
            PageSize = Math.Min(options.MaxTop, options.DefaultTop),
            EnsureStableOrdering = true,
        };
        var result = opts.ApplyTo(source, settings, AllowedQueryOptions.None);
        return (result, count);
    }
}
