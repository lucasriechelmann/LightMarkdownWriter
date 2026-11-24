using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace LightMarkdownWriter.Editing;

/// <summary>Paints every find match behind the text. Only matches inside the visible lines are drawn.</summary>
public sealed class SearchResultRenderer : IBackgroundRenderer
{
    private static readonly Brush MatchBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xEA, 0x5C, 0x00)));

    /// <summary>Matches sorted by offset.</summary>
    public IReadOnlyList<(int Offset, int Length)> Matches { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        var viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        var viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var textLength = textView.Document.TextLength;

        // Binary search for the first match that ends inside the view.
        int lo = 0, hi = Matches.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Matches[mid].Offset + Matches[mid].Length < viewStart) lo = mid + 1; else hi = mid;
        }

        for (var i = lo; i < Matches.Count && Matches[i].Offset <= viewEnd; i++)
        {
            var (offset, length) = Matches[i];
            if (offset + length > textLength) break; // stale until the next refresh

            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            builder.AddSegment(textView, new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = offset, Length = length });
            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(MatchBrush, null, geometry);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
