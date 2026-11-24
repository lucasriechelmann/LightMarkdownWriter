using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace LightMarkdownWriter.Editing;

/// <summary>
/// VS Code-style bracket and quote pairs for ( [ ` and ": typing an opener inserts the closer, typing a closer
/// that's already next steps over it, typing an opener with text selected wraps it, and Backspace between an
/// empty pair deletes both. All logic works on a <see cref="TextDocument"/> so it's unit-testable.
/// </summary>
public static class AutoPairs
{
    private static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['`'] = '`', ['"'] = '"' };

    /// <summary>An opener only auto-closes when the next character is one of these (or the line ends), so typing
    /// "(" in front of a word doesn't leave a stray ")" behind it. Same list VS Code uses.</summary>
    private const string CloseBefore = ";:.,=}])>` \t\r\n";

    /// <summary>
    /// Handles one typed character. Returns the new selection when it was handled (the caller must cancel the
    /// normal insertion), or null to let the editor insert the character as usual.
    /// </summary>
    public static (int Start, int Length)? Type(TextDocument doc, int selectionStart, int selectionLength, char typed)
    {
        var isOpener = Pairs.TryGetValue(typed, out var closer);
        var isCloser = Pairs.ContainsValue(typed);
        if (!isOpener && !isCloser) return null;

        if (selectionLength > 0)
        {
            if (!isOpener) return null;
            using (doc.RunUpdate())
            {
                doc.Insert(selectionStart + selectionLength, closer.ToString());
                doc.Insert(selectionStart, typed.ToString());
            }
            return (selectionStart + 1, selectionLength); // the wrapped text stays selected
        }

        var caret = selectionStart;
        var next = caret < doc.TextLength ? doc.GetCharAt(caret) : '\n';
        var previous = caret > 0 ? doc.GetCharAt(caret - 1) : '\n';

        // Step over a closer that's already there, e.g. the ")" auto-inserted earlier.
        if (isCloser && next == typed) return (caret + 1, 0);

        if (!isOpener || !CloseBefore.Contains(next)) return null;

        // Quotes and backticks are their own closer: after a letter or digit they're most likely closing
        // something (it's", word`), and after a backtick the user is typing a ``` fence, so type just one.
        if (typed == closer && (char.IsLetterOrDigit(previous) || previous == typed)) return null;

        doc.Insert(caret, $"{typed}{closer}");
        return (caret + 1, 0);
    }

    /// <summary>Backspace between an empty pair, like (|), deletes both characters. Returns the new caret, or null.</summary>
    public static int? Backspace(TextDocument doc, int caret)
    {
        if (caret <= 0 || caret >= doc.TextLength) return null;
        if (!Pairs.TryGetValue(doc.GetCharAt(caret - 1), out var closer) || doc.GetCharAt(caret) != closer) return null;
        doc.Remove(caret - 1, 2);
        return caret - 1;
    }

    /// <summary>Wires the pairs into an editor. <paramref name="enabled"/> is read on every key, so the setting applies live.</summary>
    public static void Attach(TextEditor editor, Func<bool> enabled)
    {
        editor.TextArea.TextEntering += (_, e) =>
        {
            if (!enabled() || e.Text is not { Length: 1 } text || editor.Document is not { } doc
                || editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return; // Alt+drag box selection: leave to the editor
            if (Type(doc, editor.SelectionStart, editor.SelectionLength, text[0]) is not { } selection) return;
            editor.Select(selection.Start, selection.Length);
            e.Handled = true; // cancels the editor's own insertion of the character
        };

        editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (!enabled() || e.Key != System.Windows.Input.Key.Back || System.Windows.Input.Keyboard.Modifiers != 0
                || editor.SelectionLength > 0 || editor.Document is not { } doc) return;
            if (Backspace(doc, editor.CaretOffset) is not { } caret) return;
            editor.CaretOffset = caret;
            e.Handled = true;
        };
    }
}
