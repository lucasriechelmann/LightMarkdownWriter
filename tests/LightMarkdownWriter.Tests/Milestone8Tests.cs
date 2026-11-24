using System.IO;
using System.Text;
using ICSharpCode.AvalonEdit.Document;
using LightMarkdownWriter.Editing;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;

namespace LightMarkdownWriter.Tests;

/// <summary>Front matter, alerts, status-bar logic (words, line endings, encoding) and image links.</summary>
public sealed class Milestone8Tests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lmw-m8-").FullName;

    // ---------- 8.4 Front matter ----------

    [Fact]
    public void Front_matter_renders_as_a_collapsed_box_not_a_rule_and_heading()
    {
        var html = MarkdownRenderer.ToHtml("---\ntitle: Hello & <World>\ntags: [a, b]\n---\n\n# Body");

        Assert.StartsWith("""<details class="front-matter" data-line="0">""", html);
        Assert.Contains("title: Hello &amp; &lt;World&gt;", html); // HTML-encoded
        Assert.Contains("""<code class="language-yaml">""", html);
        Assert.DoesNotContain("<hr", html);
        Assert.DoesNotContain("<h2", html); // the old bug: YAML lines became a setext heading
        Assert.Contains("""<h1 id="body" data-line="5">""", html);
    }

    [Fact]
    public void A_document_that_merely_starts_with_a_rule_still_renders_the_rule()
    {
        var html = MarkdownRenderer.ToHtml("---\n\nText after a rule");
        Assert.Contains("<hr", html);
        Assert.DoesNotContain("front-matter", html);
    }

    // ---------- 8.5 Alerts ----------

    [Theory]
    [InlineData("NOTE", "note")]
    [InlineData("TIP", "tip")]
    [InlineData("IMPORTANT", "important")]
    [InlineData("WARNING", "warning")]
    [InlineData("CAUTION", "caution")]
    public void Github_alerts_render_with_github_classes_and_a_source_line(string marker, string kind)
    {
        var html = MarkdownRenderer.ToHtml($"Intro\n\n> [!{marker}]\n> Body text.");
        Assert.Contains($"""<div class="markdown-alert markdown-alert-{kind}" data-line="2">""", html);
        Assert.Contains("markdown-alert-title", html);
    }

    // ---------- 8.3 Status bar ----------

    [Theory]
    [InlineData("", 0)]
    [InlineData("# Hello world", 2)]
    [InlineData("**Don't** stop — it's *fine*.", 4)]
    [InlineData("- [x] item one\n- [ ] item 2", 4)]
    [InlineData("Café naïve 123", 3)]
    public void Words_are_counted_without_markdown_symbols(string text, int expected) =>
        Assert.Equal(expected, TextStats.CountWords(text));

    [Theory]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf)]
    [InlineData("a\nb\n", LineEnding.Lf)]
    [InlineData("a\r\nb\nc", LineEnding.Mixed)]
    [InlineData("single line", LineEnding.None)]
    public void Line_endings_are_detected(string text, LineEnding expected) =>
        Assert.Equal(expected, TextStats.DetectLineEnding(new TextDocument(text)));

    [Fact]
    public void Converting_line_endings_is_one_undo_step_and_changes_the_saved_bytes()
    {
        var path = Path.Combine(_folder, "eol.md");
        File.WriteAllText(path, "one\r\ntwo\nthree\r\n");
        var doc = DocumentViewModel.Load(path);

        var changed = TextStats.ConvertLineEndings(doc.Document, "\n");

        Assert.Equal(2, changed);
        Assert.True(doc.IsDirty);
        doc.SaveTo(path);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));

        doc.Document.UndoStack.Undo(); // a single undo restores every line
        Assert.Equal("one\r\ntwo\nthree\r\n", doc.Document.Text);
    }

    [Fact]
    public void Changing_the_encoding_marks_the_tab_unsaved_and_saves_with_the_new_bom()
    {
        var path = Path.Combine(_folder, "enc.md");
        File.WriteAllText(path, "text", new UTF8Encoding(false));
        var doc = DocumentViewModel.Load(path);
        Assert.Equal("UTF-8", doc.EncodingLabel);
        Assert.False(doc.IsDirty);

        doc.ChangeEncoding(new UTF8Encoding(true));

        Assert.Equal("UTF-8 with BOM", doc.EncodingLabel);
        Assert.True(doc.IsDirty);
        doc.SaveTo(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Choosing_the_current_encoding_again_changes_nothing()
    {
        var path = Path.Combine(_folder, "same.md");
        File.WriteAllText(path, "text", new UTF8Encoding(false));
        var doc = DocumentViewModel.Load(path);

        doc.ChangeEncoding(new UTF8Encoding(false));

        Assert.False(doc.IsDirty);
    }

    // ---------- 8.2 Image links ----------

    [Fact]
    public void Dropped_image_link_is_relative_with_escaped_characters()
    {
        var docFolder = Path.Combine(_folder, "notes");
        var image = Path.Combine(_folder, "notes", "img", "my shot (1).png");
        Assert.Equal("![my shot (1)](img/my%20shot%20%281%29.png)", MarkdownFormatting.ImageLink(image, docFolder));
    }

    [Fact]
    public void Dropped_image_link_goes_up_folders_when_needed()
    {
        var docFolder = Path.Combine(_folder, "notes", "deep");
        var image = Path.Combine(_folder, "assets", "logo.svg");
        Assert.Equal("![logo](../../assets/logo.svg)", MarkdownFormatting.ImageLink(image, docFolder));
    }

    [Fact]
    public void Dropped_image_link_for_an_untitled_document_is_a_file_uri()
    {
        var image = Path.Combine(_folder, "pic.png");
        Assert.StartsWith("![pic](file:///", MarkdownFormatting.ImageLink(image, documentFolder: null));
    }

    [Theory]
    [InlineData("a.PNG", true)]
    [InlineData("b.jpeg", true)]
    [InlineData("c.md", false)]
    [InlineData("d.png.txt", false)]
    public void Image_files_are_recognized_by_extension(string name, bool expected) =>
        Assert.Equal(expected, MarkdownFormatting.IsImage(name));

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
