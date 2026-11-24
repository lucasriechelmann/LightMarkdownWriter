using System.IO;
using LightMarkdownWriter.Editing;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;

namespace LightMarkdownWriter.Tests;

/// <summary>Recent files, reopen closed tab, go-to-line parsing and the heading outline.</summary>
public sealed class Milestone9Tests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lmw-m9-").FullName;

    private string NewFile(string name, string text = "text")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---------- 9.1 Recent files ----------

    [Fact]
    public void Recent_files_are_newest_first_without_duplicates_and_survive_a_restart()
    {
        var store = Path.Combine(_folder, "recent.json");
        var a = NewFile("a.md");
        var b = NewFile("b.md");
        var recent = new RecentFilesService(store);

        recent.Add(a);
        recent.Add(b);
        recent.Add(a.ToUpperInvariant()); // same file, different case: moves to the top, no duplicate

        Assert.Equal(2, recent.Items.Count);
        Assert.Equal(Path.GetFullPath(a), recent.Items[0], ignoreCase: true);
        Assert.Equal(new RecentFilesService(store).Items, recent.Items); // reloaded from disk
    }

    [Fact]
    public void Recent_files_are_capped_and_missing_files_are_hidden()
    {
        var recent = new RecentFilesService(Path.Combine(_folder, "recent.json"));
        for (var i = 0; i < RecentFilesService.MaxItems + 5; i++)
            recent.Add(NewFile($"f{i}.md"));
        Assert.Equal(RecentFilesService.MaxItems, recent.Items.Count);

        File.Delete(recent.Items[0]);
        Assert.Equal(RecentFilesService.MaxItems - 1, recent.Existing.Count);
    }

    [Fact]
    public void Opening_and_saving_files_records_them_as_recent()
    {
        var recent = new RecentFilesService(Path.Combine(_folder, "recent.json"));
        var opened = NewFile("opened.md");
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(dialogs, [opened], recentFiles: recent);

        Assert.Equal(Path.GetFullPath(opened), recent.Items[0], ignoreCase: true);

        var savedAs = Path.Combine(_folder, "saved-as.md");
        dialogs.SavePath = savedAs;
        vm.SaveAsCommand.Execute(null);
        Assert.Equal(Path.GetFullPath(savedAs), recent.Items[0], ignoreCase: true);
    }

    // ---------- 9.2 Reopen closed tab ----------

    [Fact]
    public async Task Reopening_brings_back_closed_tabs_in_reverse_order()
    {
        var files = new[] { NewFile("one.md"), NewFile("two.md"), NewFile("three.md") };
        var vm = new MainViewModel(new FakeDialogs(), files);

        foreach (var doc in vm.Documents.ToList())
            await vm.CloseTabCommand.ExecuteAsync(doc);
        Assert.True(vm.CanReopenClosedTab);

        vm.ReopenClosedTabCommand.Execute(null);
        Assert.Equal("three.md", vm.ActiveDocument!.DisplayName);
        vm.ReopenClosedTabCommand.Execute(null);
        vm.ReopenClosedTabCommand.Execute(null);

        Assert.Equal(["three.md", "two.md", "one.md"], vm.Documents.Select(d => d.DisplayName));
        Assert.False(vm.CanReopenClosedTab);
    }

    [Fact]
    public async Task Reopening_a_tab_closed_with_dont_save_restores_the_discarded_text()
    {
        var path = NewFile("draft.md", "saved text");
        var vm = new MainViewModel(new FakeDialogs { Choice = SaveChoice.Discard }, [path]);
        vm.ActiveDocument!.Document.Insert(vm.ActiveDocument.Document.TextLength, " plus unsaved edits");

        await vm.CloseTabCommand.ExecuteAsync(vm.ActiveDocument);
        vm.ReopenClosedTabCommand.Execute(null);

        Assert.Equal("saved text plus unsaved edits", vm.ActiveDocument!.Document.Text);
        Assert.True(vm.ActiveDocument.IsDirty);
        Assert.Equal("saved text", File.ReadAllText(path)); // the file itself was never touched
    }

    [Fact]
    public async Task An_empty_untitled_tab_is_not_worth_reopening()
    {
        var vm = new MainViewModel(new FakeDialogs(), []);
        await vm.CloseTabCommand.ExecuteAsync(vm.ActiveDocument);
        Assert.False(vm.CanReopenClosedTab);
    }

    // ---------- 9.3 Go to line ----------

    [Theory]
    [InlineData("42", true, 42, 1)]
    [InlineData(" 42:7 ", true, 42, 7)]
    [InlineData("42,7", true, 42, 7)]
    [InlineData("0", false, 0, 1)]
    [InlineData("abc", false, 0, 1)]
    [InlineData("4:x", false, 4, 1)]
    [InlineData("", false, 0, 1)]
    public void Go_to_line_input_is_parsed(string text, bool ok, int line, int column)
    {
        Assert.Equal(ok, TextStats.TryParseLineColumn(text, out var l, out var c));
        if (ok) Assert.Equal((line, column), (l, c));
    }

    // ---------- 9.4 Outline ----------

    [Fact]
    public void Outline_lists_headings_with_level_plain_text_and_source_line()
    {
        var headings = MarkdownRenderer.Render(
            "---\ntitle: x\n---\n# Title with *emphasis*\n\nText\n\n## Section `code` [link](http://x)\n\nSetext\n------\n\n> ### In a quote\n").Headings;

        Assert.Equal(
            [
                (1, "Title with emphasis", 3),
                (2, "Section code link", 7),
                (2, "Setext", 9),
                (3, "In a quote", 12),
            ],
            headings.Select(h => (h.Level, h.Text, h.Line)));
    }

    [Fact]
    public void Outline_is_empty_without_headings_and_ToHtml_still_matches_Render()
    {
        const string md = "Just text\n\n- a list";
        Assert.Empty(MarkdownRenderer.Render(md).Headings);
        Assert.Equal(MarkdownRenderer.Render(md).Html, MarkdownRenderer.ToHtml(md));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeDialogs : IDialogService
    {
        public SaveChoice Choice { get; init; } = SaveChoice.Discard;
        public string? SavePath { get; set; }
        public string[] ShowOpenDialog() => [];
        public string? ShowSaveDialog(string suggestedFileName) => SavePath;
        public string? ShowExportDialog(string suggestedFileName, string filter, string defaultExt, string? initialDirectory) => null;
        public Task<SaveChoice> AskSaveChangesAsync(string documentName) => Task.FromResult(Choice);
        public void ShowError(string message) { }
    }
}
