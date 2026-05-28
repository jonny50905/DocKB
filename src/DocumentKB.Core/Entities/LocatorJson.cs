using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentKB.Core.Entities;

public sealed record LocatorJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("headings")] IReadOnlyList<string>? Headings = null,
    [property: JsonPropertyName("heading_level")] int? HeadingLevel = null,
    [property: JsonPropertyName("sheet")] string? Sheet = null,
    [property: JsonPropertyName("row_count")] int? RowCount = null,
    [property: JsonPropertyName("columns")] IReadOnlyList<string>? Columns = null,
    [property: JsonPropertyName("start_row")] int? StartRow = null,
    [property: JsonPropertyName("end_row")] int? EndRow = null,
    [property: JsonPropertyName("header_row")] int? HeaderRow = null)
{
    public static LocatorJson WordHeading(IReadOnlyList<string> headings, int level)
        => new("word", Headings: headings, HeadingLevel: level);

    public static LocatorJson ExcelSheet(string sheet, int rowCount, IReadOnlyList<string> columns)
        => new("excel_sheet", Sheet: sheet, RowCount: rowCount, Columns: columns);

    public static LocatorJson ExcelRows(string sheet, int startRow, int endRow, int headerRow)
        => new("excel_rows", Sheet: sheet, StartRow: startRow, EndRow: endRow, HeaderRow: headerRow);

    public string ToJsonString() => JsonSerializer.Serialize(this, JsonOpts);
    public static LocatorJson? FromJsonString(string? s)
        => string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize<LocatorJson>(s, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
