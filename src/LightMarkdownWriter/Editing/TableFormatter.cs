using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace LightMarkdownWriter.Editing;

/// <summary>
/// GFM pipe tables: pad the columns to equal width (keeping the alignment colons), add a row, or insert a new
/// table. Formatting only changes spacing and the outer pipes, so the rendered table stays the same.
/// </summary>
public static partial class TableFormatter
{
    private enum Align { None, Left, Center, Right }

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex SeparatorRow();

    /// <summary>The table template inserted when the caret isn't in a table.</summary>
    public static readonly string[] Template = ["| Column | Column |", "| ------ | ------ |", "|        |        |"];

    /// <summary>
    /// Formats a table given as its lines (header, separator, rows). Returns null when the lines aren't a GFM table:
    /// the second line must be a separator with as many cells as the header.
    /// </summary>
    public static string[]? Format(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2 || !SeparatorRow().IsMatch(lines[1])) return null;
        var rows = lines.Select(SplitCells).ToList();
        var columns = rows[0].Count;
        if (rows[1].Count != columns) return null; // GFM doesn't treat it as a table
        if (!Services.MarkdownRenderer.IsTable(string.Join("\n", lines))) return null; // nor does the preview

        var indent = lines[0][..(lines[0].Length - lines[0].TrimStart().Length)];
        var align = rows[1].Select(AlignmentOf).ToArray();
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
            widths[c] = Math.Max(3, rows.Where((_, r) => r != 1).Max(row => c < row.Count ? Width(row[c]) : 0));

        var result = new string[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var text = new StringBuilder(indent).Append('|');
            for (var c = 0; c < columns; c++)
            {
                var cell = r == 1 ? Dashes(widths[c], align[c]) : Pad(c < row.Count ? row[c] : "", widths[c], align[c]);
                text.Append(' ').Append(cell).Append(" |");
            }
            // Cells beyond the header's count aren't rendered by GFM; keep them as they were rather than drop text.
            foreach (var extra in row.Skip(columns))
                text.Append(' ').Append(extra).Append(" |");
            result[r] = text.ToString();
        }
        return result;
    }

    /// <summary>Formats the table around the caret, or inserts <see cref="Template"/> on a blank line when there's none.
    /// Returns the new caret offset, or null when nothing was done (caret on a non-blank line that isn't a table).</summary>
    public static int? FormatOrInsert(TextDocument doc, int caret)
    {
        var line = doc.GetLineByOffset(caret);
        if (FindTable(doc, line) is { } table)
        {
            var cell = CellIndex(doc.GetText(line), caret - line.Offset);
            return Replace(doc, table, Format(table.Lines)!, line.LineNumber - table.First.LineNumber, cell);
        }

        if (doc.GetText(line).Trim().Length > 0) return null;
        var newLine = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
        doc.Replace(line.Offset, line.Length, string.Join(newLine, Template));
        return line.Offset + 2; // in the first header cell, ready to rename it
    }

    /// <summary>Adds an empty row below the caret's row (below the separator when the caret is on the header),
    /// re-formats the table, and returns the caret in the new row's first cell; null when not in a table.</summary>
    public static int? AddRow(TextDocument doc, int caret)
    {
        var line = doc.GetLineByOffset(caret);
        if (FindTable(doc, line) is not { } table) return null;

        var columns = SplitCells(table.Lines[0]).Count;
        var at = Math.Max(line.LineNumber - table.First.LineNumber, 1) + 1;
        var lines = table.Lines.ToList();
        lines.Insert(at, "|" + string.Concat(Enumerable.Repeat(" |", columns)));
        return Replace(doc, table, Format(lines)!, at, 0);
    }

    /// <summary>Wires the editor commands: format (or insert) and add a row. Both are single undo steps.</summary>
    public static bool FormatOrInsert(TextEditor editor) => Apply(editor, FormatOrInsert);

    public static bool AddRow(TextEditor editor) => Apply(editor, AddRow);

    private static bool Apply(TextEditor editor, Func<TextDocument, int, int?> action)
    {
        if (editor.Document is not { } doc) return false;
        int? caret;
        using (doc.RunUpdate()) caret = action(doc, editor.CaretOffset);
        if (caret is null) return false;
        editor.Select(caret.Value, 0);
        return true;
    }

    private sealed record Table(DocumentLine First, DocumentLine Last, IReadOnlyList<string> Lines);

    /// <summary>The block of non-blank lines containing '|' around the line, if it's a GFM table.</summary>
    private static Table? FindTable(TextDocument doc, DocumentLine line)
    {
        static bool IsRow(TextDocument d, DocumentLine l) { var t = d.GetText(l); return t.Contains('|') && t.Trim().Length > 0; }
        if (!IsRow(doc, line)) return null;

        var first = line;
        while (first.PreviousLine is { } p && IsRow(doc, p)) first = p;
        var last = line;
        while (last.NextLine is { } n && IsRow(doc, n)) last = n;

        var lines = new List<string>();
        for (var l = first; l != last.NextLine; l = l.NextLine!) lines.Add(doc.GetText(l));
        return Format(lines) is null ? null : new Table(first, last, lines);
    }

    /// <summary>Replaces the table's lines and returns the offset of the given cell's content in the given row.</summary>
    private static int Replace(TextDocument doc, Table table, string[] formatted, int row, int cell)
    {
        var newLine = TextUtilities.GetNewLineFromDocument(doc, table.First.LineNumber);
        var start = table.First.Offset;
        doc.Replace(start, table.Last.EndOffset - start, string.Join(newLine, formatted));

        var offset = start + formatted.Take(row).Sum(l => l.Length + newLine.Length);
        var text = formatted[Math.Min(row, formatted.Length - 1)];
        var pipes = CellPipes(text);
        return offset + (cell < pipes.Count ? Math.Min(pipes[cell] + 2, text.Length) : text.Length);
    }

    /// <summary>Which cell (0-based) a column of a row is in.</summary>
    private static int CellIndex(string line, int column)
    {
        var before = CellPipes(line).Count(p => p < column);
        return Math.Max(0, line.TrimStart().StartsWith('|') ? before - 1 : before);
    }

    /// <summary>A row's cells, trimmed.</summary>
    private static List<string> SplitCells(string line)
    {
        var text = line.Trim();
        var pipes = CellPipes(text);
        var cells = new List<string>();
        var from = 0;
        foreach (var pipe in pipes)
        {
            if (pipe > 0) cells.Add(text[from..pipe].Trim()); // a leading pipe opens the row rather than ending a cell
            from = pipe + 1;
        }
        if (from < text.Length || pipes.Count == 0 || pipes[^1] != text.Length - 1)
            cells.Add(text[from..].Trim()); // the last cell, unless a trailing pipe closed the row
        return cells;
    }

    /// <summary>
    /// The positions of the pipes that separate cells: not escaped (\|) and not inside a `code span`.
    /// Markdig, which renders the preview, keeps pipes in code spans as text (GitHub would split there).
    /// </summary>
    private static List<int> CellPipes(string line)
    {
        var pipes = new List<int>();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '`')
            {
                var run = 1;
                while (i + run < line.Length && line[i + run] == '`') run++;
                var fence = new string('`', run);
                var close = line.IndexOf(fence, i + run, StringComparison.Ordinal);
                i = close < 0 ? i + run - 1 : close + run - 1; // an unclosed run is plain text
                continue;
            }
            if (line[i] == '|') pipes.Add(i);
        }
        return pipes;
    }

    private static Align AlignmentOf(string separatorCell) => (separatorCell.StartsWith(':'), separatorCell.EndsWith(':')) switch
    {
        (true, true) => Align.Center,
        (true, false) => Align.Left,
        (false, true) => Align.Right,
        _ => Align.None,
    };

    private static string Dashes(int width, Align align) => align switch
    {
        Align.Left => ":" + new string('-', width - 1),
        Align.Right => new string('-', width - 1) + ":",
        Align.Center => ":" + new string('-', width - 2) + ":",
        _ => new string('-', width),
    };

    private static string Pad(string cell, int width, Align align)
    {
        var space = width - Width(cell);
        return align switch
        {
            Align.Right => new string(' ', space) + cell,
            Align.Center => new string(' ', space / 2) + cell + new string(' ', space - space / 2),
            _ => cell + new string(' ', space),
        };
    }

    /// <summary>
    /// How many monospace columns text takes: one per user-perceived character, two for CJK and emoji, which
    /// fonts draw double-width. Counting <c>.Length</c> would get surrogate pairs and combining marks wrong.
    /// </summary>
    public static int Width(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var rune = Rune.GetRuneAt(element, 0).Value;
            width += IsWide(rune) || element.Contains('️') ? 2 : 1; // U+FE0F asks for emoji presentation
        }
        return width;
    }

    private static bool IsWide(int c) =>
        c is >= 0x1100 and <= 0x115F            // Hangul Jamo
          or >= 0x2E80 and <= 0x303E            // CJK radicals, punctuation
          or >= 0x3041 and <= 0x33FF            // kana, CJK symbols
          or >= 0x3400 and <= 0x4DBF            // CJK extension A
          or >= 0x4E00 and <= 0x9FFF            // CJK unified ideographs
          or >= 0xA000 and <= 0xA4CF            // Yi
          or >= 0xAC00 and <= 0xD7A3            // Hangul syllables
          or >= 0xF900 and <= 0xFAFF            // CJK compatibility ideographs
          or >= 0xFE30 and <= 0xFE4F            // CJK compatibility forms
          or >= 0xFF00 and <= 0xFF60            // full-width forms
          or >= 0xFFE0 and <= 0xFFE6
          or >= 0x1F300 and <= 0x1FAFF          // emoji and pictographs
          or >= 0x20000 and <= 0x3FFFD;         // CJK extensions B and later
}
