using ICSharpCode.AvalonEdit.Document;
using LightMarkdownWriter.Editing;

namespace LightMarkdownWriter.Tests;

/// <summary>Editing comfort: indenting and outdenting list items.</summary>
public sealed class Milestone11Tests
{
    // ---------- 11.1 Indent and outdent list items ----------

    /// <summary>Runs the shift with the caret (or selection) given by '|' markers: one '|' is a caret, two mark a selection.</summary>
    private static (string Text, string Marked) Shift(string marked, bool outdent = false)
    {
        var first = marked.IndexOf('|');
        var second = marked.IndexOf('|', first + 1);
        var text = marked.Replace("|", "");
        var start = first;
        var length = second < 0 ? 0 : second - first - 1;

        var doc = new TextDocument(text);
        var result = MarkdownFormatting.ShiftListItems(doc, start, length, outdent);
        if (result is not { } sel) return (doc.Text, "(not a list)");

        var withMarks = sel.Length == 0
            ? doc.Text.Insert(sel.Start, "|")
            : doc.Text.Insert(sel.Start + sel.Length, "|").Insert(sel.Start, "|");
        return (doc.Text, withMarks);
    }

    [Theory]
    [InlineData("- a\n- b|", "- a\n  - b|")]                  // bullet: under "- " is 2 spaces
    [InlineData("1. a\n2. b|", "1. a\n   2. b|")]              // numbered: under "1. " is 3 spaces
    [InlineData("10. a\n11. b|", "10. a\n    11. b|")]         // wider marker, wider step
    [InlineData("- [ ] a\n- [ ] b|", "- [ ] a\n  - [ ] b|")]  // task box isn't part of the step
    [InlineData("- a\n  - b\n  - c|", "- a\n  - b\n    - c|")] // second level: under "  - " is 4
    [InlineData("* a\n* |b", "* a\n  * |b")]                   // caret mid-line stays on the same text
    [InlineData("\t- a\n\t- b|", "\t- a\n\t\t- b|")]           // tab-indented lists move by a tab
    public void Tab_nests_an_item_by_the_lists_own_indent(string before, string after)
    {
        Assert.Equal(after, Shift(before).Marked);
    }

    [Theory]
    [InlineData("- a\n  - b|", "- a\n- b|")]
    [InlineData("1. a\n   1. b\n      - c|", "1. a\n   1. b\n   - c|")] // back to the parent's indent
    [InlineData("- a|", "- a|")]                                       // already at the top: unchanged
    [InlineData("\t\t- a|", "\t- a|")]
    public void Shift_tab_moves_an_item_back_to_its_parents_level(string before, string after)
    {
        Assert.Equal(after, Shift(before, outdent: true).Marked);
    }

    [Fact]
    public void A_selection_moves_all_items_together_and_keeps_their_nesting()
    {
        var (_, indented) = Shift("- a\n|- b\n  - c\n|- d");
        Assert.Equal("- a\n|  - b\n    - c\n|- d", indented); // the line after the selection end is untouched

        var (_, outdented) = Shift("- a\n|  - b\n    - c|", outdent: true);
        Assert.Equal("- a\n|- b\n  - c|", outdented);
    }

    [Fact]
    public void Blank_lines_inside_the_selection_are_left_empty()
    {
        Assert.Equal("- a\n|  - b\n\n  - c|", Shift("- a\n|- b\n\n- c|").Marked);
    }

    [Theory]
    [InlineData("Just a paragraph|")]
    [InlineData("|- a\nplain text|")]     // a selection that includes a non-list line
    [InlineData("    indented code|")]
    public void Lines_that_are_not_list_items_keep_the_normal_tab(string before)
    {
        var (text, marked) = Shift(before);
        Assert.Equal("(not a list)", marked);
        Assert.Equal(before.Replace("|", ""), text);
    }

    [Fact]
    public void A_shift_is_a_single_undo_step()
    {
        var doc = new TextDocument("- a\n- b\n- c");
        MarkdownFormatting.ShiftListItems(doc, 4, doc.TextLength - 4, outdent: false);
        Assert.Equal("- a\n  - b\n  - c", doc.Text);

        doc.UndoStack.Undo();
        Assert.Equal("- a\n- b\n- c", doc.Text);
    }

    [Fact]
    public void Crlf_documents_work_the_same()
    {
        Assert.Equal("- a\r\n  - b|", Shift("- a\r\n- b|").Marked);
    }
}
