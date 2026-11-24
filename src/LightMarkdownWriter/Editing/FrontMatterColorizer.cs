using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LightMarkdownWriter.Editing;

/// <summary>
/// Colors YAML front matter (a --- block at the very start of the document): delimiters dimmed, keys colored.
/// Done here rather than in Markdown.xshd because xshd rules can't anchor to the start of the document —
/// a "---" span there would also swallow the text between any two horizontal rules.
/// </summary>
public sealed class FrontMatterColorizer : DocumentColorizingTransformer
{
    private const int MaxScanLines = 200;

    private ITextSourceVersion? _cachedVersion;
    private TextDocument? _cachedDocument;
    private int _cachedEndLine;

    public Brush DelimiterBrush { get; set; } = Brushes.Gray;
    public Brush KeyBrush { get; set; } = Brushes.Teal;

    protected override void ColorizeLine(DocumentLine line)
    {
        var document = CurrentContext.Document;
        var end = EndLine(document);
        if (end == 0 || line.LineNumber > end) return;

        if (line.LineNumber == 1 || line.LineNumber == end)
        {
            if (line.Length > 0)
                ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(DelimiterBrush));
            return;
        }

        // "key: value" — color the key (indented keys too, for nested maps).
        var text = document.GetText(line);
        var colon = text.IndexOf(':');
        var start = text.Length - text.TrimStart().Length;
        if (colon > start && !text.TrimStart().StartsWith('#'))
            ChangeLinePart(line.Offset + start, line.Offset + colon, e => e.TextRunProperties.SetForegroundBrush(KeyBrush));
    }

    /// <summary>Line number of the closing delimiter, or 0 when the document has no front matter.</summary>
    private int EndLine(TextDocument document)
    {
        // Every edit creates a new version object, so this recomputes once per change, not once per line.
        if (ReferenceEquals(document, _cachedDocument) && ReferenceEquals(document.Version, _cachedVersion))
            return _cachedEndLine;

        _cachedDocument = document;
        _cachedVersion = document.Version;
        _cachedEndLine = 0;

        if (document.LineCount < 2 || document.GetText(document.GetLineByNumber(1)).TrimEnd() != "---") return 0;
        var last = Math.Min(document.LineCount, MaxScanLines);
        for (var number = 2; number <= last; number++)
        {
            var text = document.GetText(document.GetLineByNumber(number)).TrimEnd();
            if (text is "---" or "...")
            {
                _cachedEndLine = number;
                break;
            }
        }
        return _cachedEndLine;
    }
}
