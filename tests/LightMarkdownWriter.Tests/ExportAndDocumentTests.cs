using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;

namespace LightMarkdownWriter.Tests;

public sealed class ExportAndDocumentTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lmw-tests-").FullName;

    [Fact]
    public void Cf_html_offsets_are_utf8_byte_positions()
    {
        // Multi-byte characters make byte offsets differ from character offsets.
        const string fragment = "<div>Café ✅ ↩ — done</div>";
        var bytes = ExportService.BuildCfHtml(fragment);
        var text = Encoding.UTF8.GetString(bytes);

        int Offset(string name) => int.Parse(Regex.Match(text, name + @":(\d+)").Groups[1].Value);
        var start = Offset("StartFragment");
        var end = Offset("EndFragment");

        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, start, end - start));
        Assert.StartsWith("<html", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 5));
        Assert.Equal("</html>", Encoding.UTF8.GetString(bytes, Offset("EndHTML") - 7, 7));
        Assert.Equal(0, bytes[^1]); // NUL-terminated
    }

    [Fact]
    public void Local_images_are_embedded_and_remote_ones_left_alone()
    {
        File.WriteAllBytes(Path.Combine(_folder, "pic.png"), [0x89, 0x50, 0x4E, 0x47]);
        const string html = """<p><img src="pic.png" alt="a"><img src="https://example.com/x.png"><img src="missing.png"></p>""";

        var result = ExportService.EmbedLocalImages(html, _folder);

        Assert.Contains("""src="data:image/png;base64,iVBORw==""", result);
        Assert.Contains("""src="https://example.com/x.png""", result);
        Assert.Contains("""src="missing.png""", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restored_unsaved_tab_saves_with_the_original_bom_setting(bool withBom)
    {
        var path = Path.Combine(_folder, withBom ? "bom.md" : "nobom.md");
        File.WriteAllText(path, "original", new UTF8Encoding(withBom));

        // Open, edit, persist as a session tab with a backup, restore, save.
        var doc = DocumentViewModel.Load(path);
        doc.Document.Insert(doc.Document.TextLength, " edited");
        var tab = doc.ToSessionTab(hasBackup: true);
        var restored = DocumentViewModel.Restore(tab, doc.Document.Text)!;
        Assert.True(restored.IsDirty);
        restored.SaveTo(path);

        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.Equal(withBom, hasBom);
        Assert.Equal("original edited", new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
    }

    [Fact]
    public void Rendered_blocks_carry_their_source_line()
    {
        var html = MarkdownRenderer.ToHtml("# Title\n\nParagraph\n\n- item");
        Assert.Contains("""<h1 id="title" data-line="0">""", html);
        Assert.Contains("""<p data-line="2">""", html);
        Assert.Contains("""data-line="4">""", html);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
