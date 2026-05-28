using System.Text.RegularExpressions;
using DocumentKB.Core.Configuration;

namespace DocumentKB.Core.Odata;

public sealed class OdataQueryValidator(OdataOptions options)
{
    private static readonly HashSet<string> BannedClauses =
        new(StringComparer.OrdinalIgnoreCase) { "$apply", "$compute", "$search" };
    private static readonly HashSet<string> AllowedFns =
        new(StringComparer.OrdinalIgnoreCase) {
            "contains","startswith","endswith","tolower","toupper","length",
            "year","month","day","now"};
    private static readonly Regex FnCallRx = new(@"\b([a-zA-Z]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex TopRx    = new(@"\$top\s*=\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex ExpandRx = new(@"\$expand=([^&]+)", RegexOptions.IgnoreCase);

    public string? Validate(string odata)
    {
        if (string.IsNullOrEmpty(odata)) return null;
        if (System.Text.Encoding.UTF8.GetByteCount(odata) > options.MaxInputBytes)
            return $"odata exceeds max size {options.MaxInputBytes} bytes";
        foreach (var b in BannedClauses)
            if (odata.Contains(b, StringComparison.OrdinalIgnoreCase))
                return $"clause {b} is banned";
        var m = TopRx.Match(odata);
        if (m.Success)
        {
            var t = int.Parse(m.Groups[1].Value);
            if (t > options.MaxTop)
                return $"$top={t} exceeds MaxTop={options.MaxTop}";
        }
        var em = ExpandRx.Match(odata);
        if (em.Success && em.Groups[1].Value.Contains("$expand", StringComparison.OrdinalIgnoreCase))
            return "$expand depth > 1 is banned";
        foreach (Match fm in FnCallRx.Matches(odata))
        {
            var fn = fm.Groups[1].Value;
            if (IsKnownClauseOrOperator(fn)) continue;
            if (!AllowedFns.Contains(fn))
                return $"function {fn} is not allowed";
        }
        return null;
    }

    private static bool IsKnownClauseOrOperator(string token)
        => token.StartsWith("$") || token is "in" or "and" or "or" or "not"
            or "eq" or "ne" or "lt" or "le" or "gt" or "ge";
}
