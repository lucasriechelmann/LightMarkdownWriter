using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Document;

namespace LightMarkdownWriter.Editing;

public enum LineEnding { None, CrLf, Lf, Cr, Mixed }

/// <summary>Word counts and line-ending detection/conversion for the status bar. Pure logic, unit-tested.</summary>
public static partial class TextStats
{
    // Runs of letters/digits, allowing inner apostrophes (don't, it’s). Markdown symbols don't count as words.
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex Word();

    // Task-list checkboxes ("- [x] item"): the x isn't a word of the text.
    [GeneratedRegex(@"\[[ xX]\]")]
    private static partial Regex TaskMarker();

    public static int CountWords(string text) => Word().Count(TaskMarker().Replace(text, ""));

    public static LineEnding DetectLineEnding(TextDocument document)
    {
        var found = LineEnding.None;
        foreach (var line in document.Lines)
        {
            if (line.DelimiterLength == 0) continue;
            var delimiter = document.GetText(line.EndOffset, line.DelimiterLength) switch
            {
                "\r\n" => LineEnding.CrLf,
                "\n" => LineEnding.Lf,
                _ => LineEnding.Cr,
            };
            if (found == LineEnding.None) found = delimiter;
            else if (found != delimiter) return LineEnding.Mixed;
        }
        return found;
    }

    /// <summary>Parses the Go to line box: "42", "42:7" or "42,7" (line, then optional column; 1-based).</summary>
    public static bool TryParseLineColumn(string? text, out int line, out int column)
    {
        line = column = 1;
        var parts = (text ?? "").Trim().Split([':', ','], StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 2 || !int.TryParse(parts[0], out line) || line < 1) return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out column) || column < 1)) return false;
        return true;
    }

    public static string Label(LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "Windows (CRLF)",
        LineEnding.Lf => "Unix (LF)",
        LineEnding.Cr => "Macintosh (CR)",
        LineEnding.Mixed => "Mixed line endings",
        // A single line: Enter will insert the system default.
        _ => Environment.NewLine == "\r\n" ? "Windows (CRLF)" : "Unix (LF)",
    };

    /// <summary>Rewrites every line delimiter as <paramref name="newLine"/> in one undo step. Returns how many changed.</summary>
    public static int ConvertLineEndings(TextDocument document, string newLine)
    {
        var changed = 0;
        using (document.RunUpdate())
        {
            // Back to front, so offsets of lines not yet visited stay valid.
            for (var number = document.LineCount; number >= 1; number--)
            {
                var line = document.GetLineByNumber(number);
                if (line.DelimiterLength == 0) continue;
                if (document.GetText(line.EndOffset, line.DelimiterLength) == newLine) continue;
                document.Replace(line.EndOffset, line.DelimiterLength, newLine);
                changed++;
            }
        }
        return changed;
    }
}
