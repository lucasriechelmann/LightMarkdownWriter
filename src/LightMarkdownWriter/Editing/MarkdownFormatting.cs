using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace LightMarkdownWriter.Editing;

/// <summary>Markdown editing commands for the AvalonEdit editor. Each runs as a single undo step.</summary>
public static partial class MarkdownFormatting
{
    /// <summary>
    /// Toggles an inline marker (**, *, `, ~~) around the selection, or around the word at the caret.
    /// Detects an existing wrap by counting the run of marker characters on each side, so italic inside
    /// bold (***word***) toggles correctly.
    /// </summary>
    public static void ToggleInline(TextEditor editor, string marker)
    {
        if (editor.Document is not { } doc) return;
        int start = editor.SelectionStart, length = editor.SelectionLength, m = marker.Length;

        if (length == 0 && WordAt(doc, start) is var (wordStart, wordEnd) && wordEnd > wordStart)
        {
            start = wordStart;
            length = wordEnd - wordStart;
        }

        var c = marker[0];
        var before = CountRun(doc, start - 1, -1, c);
        var after = CountRun(doc, start + length, +1, c);
        var selected = doc.GetText(start, length);

        using (doc.RunUpdate())
        {
            if (IsWrapped(before, m) && IsWrapped(after, m))
            {
                doc.Remove(start + length, m);
                doc.Remove(start - m, m);
                editor.Select(start - m, length);
            }
            else if (length >= 2 * m && selected.StartsWith(marker, StringComparison.Ordinal)
                                     && selected.EndsWith(marker, StringComparison.Ordinal))
            {
                doc.Replace(start, length, selected[m..^m]);
                editor.Select(start, length - 2 * m);
            }
            else
            {
                doc.Insert(start + length, marker);
                doc.Insert(start, marker);
                editor.Select(start + m, length);
            }
        }
    }

    // A run of 1 or 3 '*' means italic is present; a run of 2+ means bold/strike/etc.
    private static bool IsWrapped(int run, int markerLength) =>
        markerLength == 1 ? run % 2 == 1 : run >= markerLength;

    private static int CountRun(TextDocument doc, int offset, int step, char c)
    {
        var count = 0;
        while (offset >= 0 && offset < doc.TextLength && doc.GetCharAt(offset) == c)
        {
            count++;
            offset += step;
        }
        return count;
    }

    private static (int Start, int End) WordAt(TextDocument doc, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && char.IsLetterOrDigit(doc.GetCharAt(start - 1))) start--;
        while (end < doc.TextLength && char.IsLetterOrDigit(doc.GetCharAt(end))) end++;
        return (start, end);
    }

    /// <summary>[selection](url) with "url" selected; a selected URL becomes [](url) with the caret in the brackets.</summary>
    public static void InsertLink(TextEditor editor)
    {
        if (editor.Document is not { } doc) return;
        var start = editor.SelectionStart;
        var selected = editor.SelectedText;
        var trimmed = selected.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            doc.Replace(start, selected.Length, $"[]({trimmed})");
            editor.Select(start + 1, 0);
            return;
        }

        var label = selected.Length > 0 ? selected : "text";
        doc.Replace(start, selected.Length, $"[{label}](url)");
        if (selected.Length > 0)
            editor.Select(start + label.Length + 3, 3); // "url"
        else
            editor.Select(start + 1, label.Length);     // "text"
    }

    /// <summary>Wraps the selected lines in a ``` fence, or inserts an empty fenced block; caret goes where the language name is typed.</summary>
    public static void InsertCodeBlock(TextEditor editor)
    {
        if (editor.Document is not { } doc) return;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        var newLine = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);

        using (doc.RunUpdate())
        {
            if (editor.SelectionLength > 0)
            {
                var first = doc.GetLineByOffset(editor.SelectionStart);
                var selectionEnd = editor.SelectionStart + editor.SelectionLength;
                var last = doc.GetLineByOffset(selectionEnd);
                if (last.Offset == selectionEnd && last.PreviousLine is { } previous && previous.LineNumber >= first.LineNumber)
                    last = previous; // selection ends at the start of a line: don't include that line

                doc.Insert(last.EndOffset, newLine + "```");
                doc.Insert(first.Offset, "```" + newLine);
                editor.Select(first.Offset + 3, 0);
            }
            else if (doc.GetText(line).Trim().Length == 0)
            {
                doc.Replace(line.Offset, line.Length, "```" + newLine + newLine + "```");
                editor.Select(line.Offset + 3, 0);
            }
            else
            {
                var at = line.EndOffset;
                doc.Insert(at, newLine + "```" + newLine + newLine + "```");
                editor.Select(at + newLine.Length + 3, 0);
            }
        }
    }

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".avif"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>![name](path)</c> for an image file: relative to the document's folder (forward slashes, spaces and
    /// parentheses escaped so the link survives), or a file:/// URI when the document is untitled.
    /// </summary>
    public static string ImageLink(string imagePath, string? documentFolder)
    {
        var alt = System.IO.Path.GetFileNameWithoutExtension(imagePath).Replace('[', '(').Replace(']', ')');
        string target;
        if (documentFolder is null)
        {
            target = new Uri(imagePath).AbsoluteUri;
        }
        else
        {
            target = System.IO.Path.GetRelativePath(documentFolder, imagePath).Replace('\\', '/');
            target = target.Replace("%", "%25").Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
        }
        return $"![{alt}]({target})";
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:(?<bullet>[-*+])|(?<num>\d{1,9})(?<delim>[.)]))(?<space>[ \t]+)(?<task>\[[ xX]\][ \t]+)?")]
    private static partial Regex ListItemPrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<quote>(?:>[ \t]?)+)")]
    private static partial Regex QuotePrefix();

    /// <summary>
    /// Enter inside a list item / quote continues it ("- ", "2. ", "- [ ] ", "> "). Enter on an empty item
    /// removes the marker instead, ending the list. Returns false when Enter should behave normally.
    /// </summary>
    public static bool ContinueListOnEnter(TextEditor editor)
    {
        if (editor.Document is not { } doc || editor.SelectionLength > 0) return false;

        var caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var beforeCaret = doc.GetText(line.Offset, caret - line.Offset);
        var afterCaret = doc.GetText(caret, line.EndOffset - caret);

        string prefix, next;
        var item = ListItemPrefix().Match(beforeCaret);
        if (item.Success)
        {
            prefix = item.Value;
            var marker = item.Groups["bullet"].Success
                ? item.Groups["bullet"].Value
                : (long.Parse(item.Groups["num"].Value) + 1) + item.Groups["delim"].Value;
            next = item.Groups["indent"].Value + marker + item.Groups["space"].Value
                   + (item.Groups["task"].Success ? "[ ] " : "");
        }
        else if (QuotePrefix().Match(beforeCaret) is { Success: true } quote)
        {
            prefix = quote.Value;
            next = quote.Value;
        }
        else
        {
            return false;
        }

        if (prefix.Length == beforeCaret.Length && afterCaret.Trim().Length == 0)
        {
            // Empty item: end the list by clearing the marker (like GitHub and VS Code).
            doc.Remove(line.Offset, line.Length);
            editor.CaretOffset = line.Offset;
            return true;
        }

        var insert = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber) + next;
        doc.Insert(caret, insert);
        editor.CaretOffset = caret + insert.Length;
        return true;
    }
}
