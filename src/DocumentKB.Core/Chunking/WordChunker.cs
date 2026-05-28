using System.Text.RegularExpressions;
using DocumentKB.Core.Configuration;
using DocumentKB.Core.Entities;

namespace DocumentKB.Core.Chunking;

public sealed class WordChunker(ChunkingOptions options) : IChunker
{
    private static readonly Regex HeadingRx =
        new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);

    public IReadOnlyList<ChunkDraft> Chunk(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sections = SplitByHeading(lines).ToList();

        if (sections.Count == 0 || sections.All(s => s.HeadingStack.Count == 0))
            return SplitByParagraph(markdown, titlePath: null, locator: null).ToList();

        var drafts = new List<ChunkDraft>();
        var ordinal = 0;
        foreach (var sec in sections)
        {
            var titlePath = sec.HeadingStack.Count > 0
                ? string.Join(" > ", sec.HeadingStack) : null;
            var locator = sec.HeadingStack.Count > 0
                ? LocatorJson.WordHeading(sec.HeadingStack, sec.Level) : null;
            var body = sec.Body.TrimEnd('\n');
            if (body.Length <= options.MaxChunkChars)
            {
                drafts.Add(new ChunkDraft(
                    ordinal++, ChunkType.WordSection, titlePath, locator, body));
            }
            else
            {
                foreach (var piece in SplitByParagraph(body, titlePath, locator))
                    drafts.Add(piece with { Ordinal = ordinal++ });
            }
        }
        return drafts;
    }

    private sealed record Section(IReadOnlyList<string> HeadingStack, int Level, string Body);

    private IEnumerable<Section> SplitByHeading(string[] lines)
    {
        var stack = new List<string>();
        var levels = new List<int>();
        var buf = new System.Text.StringBuilder();
        var curStack = new List<string>();
        var curLevel = 0;

        var sections = new List<Section>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = HeadingRx.Match(line);
            if (m.Success)
            {
                if (buf.Length > 0)
                    sections.Add(new Section(curStack.ToList(), curLevel, buf.ToString()));
                buf.Clear();

                var lvl = m.Groups[1].Value.Length;
                var title = m.Groups[2].Value;
                while (levels.Count > 0 && levels[^1] >= lvl)
                { levels.RemoveAt(levels.Count - 1); stack.RemoveAt(stack.Count - 1); }
                stack.Add(title); levels.Add(lvl);
                curStack = stack.ToList();
                curLevel = lvl;
                buf.AppendLine($"{m.Groups[1].Value} {title}");
            }
            else
            {
                buf.AppendLine(line);
            }
        }
        if (buf.Length > 0)
            sections.Add(new Section(curStack.ToList(), curLevel, buf.ToString()));
        return sections;
    }

    private IEnumerable<ChunkDraft> SplitByParagraph(
        string body, string? titlePath, LocatorJson? locator)
    {
        var paras = Regex.Split(body, @"\n\s*\n");
        var buf = new System.Text.StringBuilder();
        var ord = 0;
        foreach (var p in paras)
        {
            if (buf.Length + p.Length + 2 > options.MaxChunkChars && buf.Length > 0)
            {
                yield return new ChunkDraft(
                    ord++, ChunkType.WordSection, titlePath, locator,
                    buf.ToString().TrimEnd());
                buf.Clear();
            }
            if (buf.Length > 0) buf.Append("\n\n");
            buf.Append(p);
        }
        if (buf.Length > 0)
            yield return new ChunkDraft(
                ord, ChunkType.WordSection, titlePath, locator,
                buf.ToString().TrimEnd());
    }
}
