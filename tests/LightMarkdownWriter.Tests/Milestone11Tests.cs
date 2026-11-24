using System.IO;
using ICSharpCode.AvalonEdit.Document;
using LightMarkdownWriter.Editing;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;

namespace LightMarkdownWriter.Tests;

/// <summary>Editing comfort: list indenting, auto-close pairs, tables, tab management, pasted images.</summary>
public sealed class Milestone11Tests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lmw-m11-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string NewFile(string name, string text = "text")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private sealed class CountingDialogs : IDialogService
    {
        public List<string> Asked { get; } = [];
        public SaveChoice Choice { get; set; } = SaveChoice.Discard;
        public string? SavePath { get; set; }
        public string[] ShowOpenDialog() => [];
        public string? ShowSaveDialog(string suggestedFileName) => SavePath;
        public string? ShowExportDialog(string suggestedFileName, string filter, string defaultExt, string? initialDirectory) => null;
        public Task<SaveChoice> AskSaveChangesAsync(string documentName) { Asked.Add(documentName); return Task.FromResult(Choice); }
        public void ShowError(string message) { }
    }

    // ---------- 11.6 Paste images ----------

    [Fact]
    public void Pasted_images_get_timestamped_names_in_an_images_folder_and_never_overwrite()
    {
        var now = new DateTime(2026, 11, 24, 15, 30, 12);
        var first = MarkdownFormatting.PastedImagePath(_folder, now);
        Assert.Equal(Path.Combine(_folder, "images", "image-20261124-153012.png"), first);

        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, [1]);
        Assert.EndsWith("image-20261124-153012-2.png", MarkdownFormatting.PastedImagePath(_folder, now));

        // The inserted link is relative to the document, so it works when the folder moves.
        Assert.Equal("![image-20261124-153012](images/image-20261124-153012.png)", MarkdownFormatting.ImageLink(first, _folder));
    }

    // ---------- 11.4 Tab management ----------

    [Fact]
    public async Task Close_others_asks_only_about_unsaved_tabs_and_keeps_the_chosen_one()
    {
        var dialogs = new CountingDialogs();
        var vm = new MainViewModel(dialogs, [NewFile("a.md"), NewFile("b.md"), NewFile("c.md"), NewFile("d.md")]);
        vm.Documents[2].Document.Insert(0, "edit "); // only c.md is unsaved

        await vm.CloseOthersCommand.ExecuteAsync(vm.Documents[1]);

        Assert.Equal(["c.md"], dialogs.Asked);
        Assert.Equal(["b.md"], vm.Documents.Select(d => d.DisplayName));
        Assert.Equal("b.md", vm.ActiveDocument!.DisplayName);
    }

    [Fact]
    public async Task Cancelling_a_prompt_stops_closing_and_keeps_the_rest()
    {
        var dialogs = new CountingDialogs { Choice = SaveChoice.Cancel };
        var vm = new MainViewModel(dialogs, [NewFile("a.md"), NewFile("b.md"), NewFile("c.md")]);
        vm.Documents[1].Document.Insert(0, "edit ");

        await vm.CloseToTheRightCommand.ExecuteAsync(vm.Documents[0]);

        Assert.Equal(["a.md", "b.md", "c.md"], vm.Documents.Select(d => d.DisplayName));
    }

    [Fact]
    public async Task Close_to_the_right_leaves_the_left_side_alone()
    {
        var vm = new MainViewModel(new CountingDialogs(), [NewFile("a.md"), NewFile("b.md"), NewFile("c.md")]);
        await vm.CloseToTheRightCommand.ExecuteAsync(vm.Documents[1]);
        Assert.Equal(["a.md", "b.md"], vm.Documents.Select(d => d.DisplayName));
        Assert.True(vm.CanReopenClosedTab); // closed tabs can still come back with Ctrl+Shift+T
    }

    [Fact]
    public void Save_all_writes_every_unsaved_tab()
    {
        var a = NewFile("a.md", "A");
        var b = NewFile("b.md", "B");
        var dialogs = new CountingDialogs { SavePath = Path.Combine(_folder, "new.md") };
        var vm = new MainViewModel(dialogs, [a, b]);
        vm.NewCommand.Execute(null);
        foreach (var doc in vm.Documents) doc.Document.Insert(doc.Document.TextLength, "!");

        vm.SaveAllCommand.Execute(null);

        Assert.Equal("A!", File.ReadAllText(a));
        Assert.Equal("B!", File.ReadAllText(b));
        Assert.Equal("!", File.ReadAllText(dialogs.SavePath)); // the untitled tab asked for a name
        Assert.All(vm.Documents, d => Assert.False(d.IsDirty));
    }

    [Fact]
    public void Moving_a_tab_changes_the_order_saved_in_the_session()
    {
        var sessionFolder = Path.Combine(_folder, "session");
        var sessions = new SessionService(sessionFolder);
        var vm = new MainViewModel(new CountingDialogs(), [NewFile("a.md"), NewFile("b.md"), NewFile("c.md")], sessions);

        vm.MoveTab(vm.Documents[2], 0);
        Assert.Equal(["c.md", "a.md", "b.md"], vm.Documents.Select(d => d.DisplayName));
        Assert.Equal("c.md", vm.ActiveDocument!.DisplayName);

        vm.PersistSession();
        var restored = new MainViewModel(new CountingDialogs(), [], sessions, sessions.Load());
        Assert.Equal(["c.md", "a.md", "b.md"], restored.Documents.Select(d => d.DisplayName));
    }

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

    // ---------- 11.2 Auto-close pairs ----------

    /// <summary>Types the characters one by one at the '|' caret (or "|…|" selection), the way the editor would:
    /// AutoPairs first, and a plain insert when it declines.</summary>
    private static string TypeKeys(string marked, string keys)
    {
        var first = marked.IndexOf('|');
        var second = marked.IndexOf('|', first + 1);
        var doc = new TextDocument(marked.Replace("|", ""));
        var (start, length) = (first, second < 0 ? 0 : second - first - 1);

        foreach (var key in keys)
        {
            if (key == '\b')
            {
                if (AutoPairs.Backspace(doc, start) is { } caret) start = caret;
                else { doc.Remove(start - 1, 1); start--; }
                continue;
            }
            if (AutoPairs.Type(doc, start, length, key) is { } selection) (start, length) = selection;
            else
            {
                doc.Replace(start, length, key.ToString());
                (start, length) = (start + 1, 0);
            }
        }
        return length == 0 ? doc.Text.Insert(start, "|") : doc.Text.Insert(start + length, "|").Insert(start, "|");
    }

    [Theory]
    [InlineData("|", "(", "(|)")]
    [InlineData("|", "[", "[|]")]
    [InlineData("|", "\"", "\"|\"")]
    [InlineData("|", "`", "`|`")]
    [InlineData("|", "(x)", "(x)|")]               // the typed ")" steps over the inserted one
    [InlineData("- |", "[ ] ", "- [ ] |")]        // a task box types naturally
    [InlineData("|", "```", "```|")]               // a code fence: no stray backticks
    [InlineData("see |", "[docs](url)", "see [docs](url)|")]
    [InlineData("|word", "(", "(|word")]           // before a word: no closer
    [InlineData("it|", "\"", "it\"|")]             // after a letter a quote is probably closing
    [InlineData("a|", "`", "a`|")]
    [InlineData("x |)", ")", "x )|")]              // a closer already there is stepped over
    [InlineData("|", "(\b", "|")]                  // Backspace in an empty pair removes both
    [InlineData("(a|)", "\b", "(|)")]              // ...but only when the pair is empty
    public void Typing_pairs_behaves_like_vs_code(string before, string keys, string after)
    {
        Assert.Equal(after, TypeKeys(before, keys));
    }

    [Theory]
    [InlineData("|word|", "(", "(|word|)")]
    [InlineData("|word|", "`", "`|word|`")]
    [InlineData("|two words|", "\"", "\"|two words|\"")]
    [InlineData("|word|", ")", ")|")]              // a closer replaces the selection as normal typing does
    public void Typing_an_opener_over_a_selection_wraps_it(string before, string keys, string after)
    {
        Assert.Equal(after, TypeKeys(before, keys));
    }

    [Fact]
    public void Wrapping_is_one_undo_step()
    {
        var doc = new TextDocument("word");
        AutoPairs.Type(doc, 0, 4, '(');
        Assert.Equal("(word)", doc.Text);
        doc.UndoStack.Undo();
        Assert.Equal("word", doc.Text);
    }

    // ---------- 11.3 Table formatting ----------

    [Fact]
    public void A_messy_table_comes_out_aligned_with_its_alignment_colons()
    {
        string[] messy =
        [
            "Name|Qty|  Price |Note",
            ":--|:-:|--:|---",
            "  apple | 3 | 1.50|fresh",
            "| kiwi |12|0.2 | |",
        ];

        Assert.Equal(
            [
                "| Name  | Qty | Price | Note  |",
                "| :---- | :-: | ----: | ----- |",
                "| apple |  3  |  1.50 | fresh |",
                "| kiwi  | 12  |   0.2 |       |",
            ],
            TableFormatter.Format(messy)!);
    }

    [Fact]
    public void Formatting_never_changes_the_rendered_table()
    {
        string[] messy = ["a|b", "-|-", "1|", "x \\| y|2|extra", "`c|d`|z"];
        var formatted = TableFormatter.Format(messy)!;

        Assert.Equal(
            Services.MarkdownRenderer.ToHtml(string.Join("\n", messy)),
            Services.MarkdownRenderer.ToHtml(string.Join("\n", formatted)));
        Assert.Equal("| x \\| y | 2   | extra |", formatted[3]); // escaped pipe kept; extra cell kept as it was
    }

    [Theory]
    [InlineData("日本", 4)]
    [InlineData("🎉 ok", 5)]
    [InlineData("é", 1)]   // e + combining accent is one character
    [InlineData("✔️", 2)]   // emoji presentation selector
    [InlineData("abc", 3)]
    public void Width_counts_columns_not_utf16_units(string text, int width)
    {
        Assert.Equal(width, TableFormatter.Width(text));
    }

    [Fact]
    public void Cjk_and_emoji_cells_line_up()
    {
        var formatted = TableFormatter.Format(["| a | b |", "|---|---|", "| 日本語 | 🎉 |"])!;
        Assert.Equal(["| a      | b   |", "| ------ | --- |", "| 日本語 | 🎉  |"], formatted);
    }

    [Theory]
    [InlineData("| a |\n| b |")]           // no separator row
    [InlineData("| a | b |\n| --- |")]     // separator cell count differs from the header
    [InlineData("just text")]
    [InlineData("a|b\n-|-\n1")]            // looks like one, but the preview renders it as a paragraph
    public void Things_that_are_not_tables_are_left_alone(string text)
    {
        Assert.Null(TableFormatter.Format(text.Split('\n')));
    }

    [Fact]
    public void Format_at_the_caret_reformats_only_the_table_and_keeps_the_caret_in_its_cell()
    {
        var doc = new TextDocument("Intro\n\na|bb\n-|-\nccc|d\n\nAfter");
        var caret = TableFormatter.FormatOrInsert(doc, doc.Text.IndexOf("d\n", StringComparison.Ordinal))!.Value;

        Assert.Equal("Intro\n\n| a   | bb  |\n| --- | --- |\n| ccc | d   |\n\nAfter", doc.Text);
        Assert.Equal('d', doc.GetCharAt(caret));
    }

    [Fact]
    public void Add_row_goes_below_the_caret_row_or_below_the_separator_from_the_header()
    {
        var doc = new TextDocument("| a | b |\n| - | - |\n| 1 | 2 |");
        var caret = TableFormatter.AddRow(doc, 2)!.Value; // caret on the header

        Assert.Equal("| a   | b   |\n| --- | --- |\n|     |     |\n| 1   | 2   |", doc.Text);
        Assert.Equal(doc.Text.IndexOf("\n|     |", StringComparison.Ordinal) + 3, caret);

        Assert.Null(TableFormatter.AddRow(new TextDocument("no table"), 0));
    }

    [Fact]
    public void On_a_blank_line_a_template_table_is_inserted_and_elsewhere_nothing_happens()
    {
        var doc = new TextDocument("Text\n\n");
        Assert.NotNull(TableFormatter.FormatOrInsert(doc, doc.TextLength));
        Assert.Equal("Text\n\n" + string.Join("\n", TableFormatter.Template), doc.Text);
        Assert.NotNull(TableFormatter.Format(TableFormatter.Template));

        Assert.Null(TableFormatter.FormatOrInsert(new TextDocument("plain text"), 3));
    }
}
