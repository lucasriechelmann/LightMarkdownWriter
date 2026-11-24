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

    /// <summary>
    /// Where a pasted image is saved: <c>images/image-20261124-153012.png</c> next to the document, with -2, -3…
    /// added when that name is taken (two pastes in the same second). The folder isn't created here.
    /// </summary>
    public static string PastedImagePath(string documentFolder, DateTime now)
    {
        var folder = System.IO.Path.Combine(documentFolder, "images");
        var name = $"image-{now:yyyyMMdd-HHmmss}";
        var path = System.IO.Path.Combine(folder, name + ".png");
        for (var n = 2; System.IO.File.Exists(path); n++)
            path = System.IO.Path.Combine(folder, $"{name}-{n}.png");
        return path;
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

    /// <summary>
    /// Tab / Shift+Tab on list items: see <see cref="ShiftListItems(TextDocument, int, int, bool)"/>.
    /// Returns false when the caret or selection isn't on list lines, so the editor's normal Tab runs.
    /// </summary>
    public static bool ShiftListItems(TextEditor editor, bool outdent)
    {
        if (editor.Document is not { } doc) return false;
        if (ShiftListItems(doc, editor.SelectionStart, editor.SelectionLength, outdent) is not { } selection) return false;
        editor.Select(selection.Start, selection.Length);
        return true;
    }

    /// <summary>
    /// Indents (or outdents) the list items on the selected lines by one nesting level, as one undo step.
    /// <list type="bullet">
    /// <item>Indent: to the content column of the item above at the same level ("- " → 2 spaces, "10. " → 4),
    /// which is what makes markdown nest it; the first item of a level uses its own marker width.</item>
    /// <item>Outdent: back to the indent of the parent item (the nearest item above with less indent), or 0.</item>
    /// <item>A tab-indented item moves by one tab. With several lines, the shallowest one sets the step and every
    /// line moves by the same amount, so nested children keep their shape.</item>
    /// </list>
    /// Applies only when every non-blank line in range is a list item; returns the new selection, or null.
    /// </summary>
    public static (int Start, int Length)? ShiftListItems(TextDocument doc, int selectionStart, int selectionLength, bool outdent)
    {
        var first = doc.GetLineByOffset(selectionStart);
        var selectionEnd = selectionStart + selectionLength;
        var last = doc.GetLineByOffset(selectionEnd);
        if (selectionLength > 0 && last.Offset == selectionEnd && last.LineNumber > first.LineNumber)
            last = last.PreviousLine; // selection ends at the start of a line: don't include that line

        var items = new List<(DocumentLine Line, Match Item)>();
        for (var line = first; line is not null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            var text = doc.GetText(line);
            if (text.Trim().Length == 0) continue;
            var item = ListItemPrefix().Match(text);
            if (!item.Success) return null;
            items.Add((line, item));
        }
        if (items.Count == 0) return null;

        var (baseLine, baseItem) = items.MinBy(i => i.Item.Groups["indent"].Length);
        var indent = baseItem.Groups["indent"].Value;
        var usesTabs = indent.Contains('\t');

        string add = "";
        var remove = 0;
        if (!outdent)
        {
            add = usesTabs ? "\t" : new string(' ', ContentColumn(SiblingAbove(doc, baseLine, indent.Length) ?? baseItem) - indent.Length);
        }
        else if (indent.Length > 0)
        {
            remove = usesTabs ? 1 : indent.Length - (ParentAbove(doc, baseLine, indent.Length)?.Groups["indent"].Length ?? 0);
        }

        // Anchors keep the selection on the same text: its start stays before inserted indentation when it sits at
        // a line start (so the new indent is selected too), its end and a bare caret move along with the text.
        var startAnchor = doc.CreateAnchor(selectionStart);
        startAnchor.MovementType = selectionLength > 0 ? AnchorMovementType.BeforeInsertion : AnchorMovementType.AfterInsertion;
        var endAnchor = doc.CreateAnchor(selectionEnd);
        endAnchor.MovementType = AnchorMovementType.AfterInsertion;

        using (doc.RunUpdate())
        {
            foreach (var (line, item) in items)
            {
                if (add.Length > 0)
                    doc.Insert(line.Offset, add);
                else if (remove > 0)
                    doc.Remove(line.Offset, Math.Min(remove, item.Groups["indent"].Length));
            }
        }
        return (startAnchor.Offset, Math.Max(0, endAnchor.Offset - startAnchor.Offset));
    }

    /// <summary>The column an item's text starts at, e.g. 2 for "- x", 5 for "  1. x" (task boxes not counted).</summary>
    private static int ContentColumn(Match item) =>
        item.Groups["space"].Index + item.Groups["space"].Length;

    /// <summary>The nearest list item above at exactly <paramref name="indent"/>, stopping at its parent or the list's end.</summary>
    private static Match? SiblingAbove(TextDocument doc, DocumentLine line, int indent)
    {
        foreach (var item in ItemsAbove(doc, line))
        {
            var itemIndent = item.Groups["indent"].Length;
            if (itemIndent == indent) return item;
            if (itemIndent < indent) return null; // reached the parent: this is the first child
        }
        return null;
    }

    /// <summary>The nearest list item above with less indent than <paramref name="indent"/>.</summary>
    private static Match? ParentAbove(TextDocument doc, DocumentLine line, int indent) =>
        ItemsAbove(doc, line).FirstOrDefault(item => item.Groups["indent"].Length < indent);

    /// <summary>List items above a line, nearest first. Blank and indented continuation lines are skipped; an
    /// unindented non-list line ends the list.</summary>
    private static IEnumerable<Match> ItemsAbove(TextDocument doc, DocumentLine line)
    {
        for (var previous = line.PreviousLine; previous is not null; previous = previous.PreviousLine)
        {
            var text = doc.GetText(previous);
            if (ListItemPrefix().Match(text) is { Success: true } item) yield return item;
            else if (text.Length > 0 && !char.IsWhiteSpace(text[0])) yield break;
        }
    }
}
