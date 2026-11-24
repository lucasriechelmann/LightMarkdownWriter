using System.IO;
using System.Xml.Linq;

namespace LightMarkdownWriter.Tests;

/// <summary>Quality and reach: accessible names and large-file performance.</summary>
public sealed class Milestone12Tests
{
    private static readonly string SourceFolder = FindSourceFolder();

    private static string FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "LightMarkdownWriter");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/LightMarkdownWriter not found above the test output folder.");
    }

    // ---------- 12.2 Large-file performance ----------

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\rb\r")]
    [InlineData("a\r\nb\nc")]
    [InlineData("a\rb\r\n")]
    [InlineData("a\n\rb")]
    [InlineData("single line")]
    [InlineData("")]
    public void Line_endings_from_plain_text_match_the_document_scan(string text)
    {
        // The status bar scans a text snapshot off the UI thread; it must agree with the TextDocument version.
        Assert.Equal(
            Editing.TextStats.DetectLineEnding(new ICSharpCode.AvalonEdit.Document.TextDocument(text)),
            Editing.TextStats.DetectLineEnding(text));
    }

    [Fact]
    public async Task Rendering_on_a_background_thread_gives_the_same_result()
    {
        var text = string.Concat(Enumerable.Range(0, 2000).Select(i => $"## Heading {i}\n\nText with `code` and $x^{i}$.\n\n"));
        var onThisThread = Services.MarkdownRenderer.Render(text);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Services.MarkdownRenderer.Render(text))));

        Assert.All(results, r =>
        {
            Assert.Equal(onThisThread.Html, r.Html);
            Assert.Equal(onThisThread.Headings, r.Headings);
        });
    }

    // ---------- 12.1 Accessible names ----------

    private static readonly HashSet<string> Clickable = ["Button", "RadioButton", "ToggleButton", "ToggleSwitch", "NumberBox", "ComboBox", "TextBox"];

    /// <summary>
    /// Every button, toggle and input needs a name Narrator can read: visible text (Content), or
    /// AutomationProperties.Name. A ToolTip doesn't count; screen readers announce it as help text at most.
    /// This reads the XAML itself, so a new icon-only button without a name fails here.
    /// </summary>
    [Fact]
    public void Every_control_in_the_xaml_has_a_name_a_screen_reader_can_read()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceFolder, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (var element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants())
            {
                if (!Clickable.Contains(element.Name.LocalName)) continue;
                if (element.Parent?.Name.LocalName.Contains("Template", StringComparison.Ordinal) == true
                    && element.Name.LocalName is "TextBox") continue; // template parts, not real controls

                var hasName = element.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");
                var hasText = element.Attribute("Content") is not null || element.Attribute("Header") is not null;
                if (hasName || hasText) continue;

                var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                missing.Add($"{Path.GetFileName(file)}:{line} <{element.Name.LocalName}>");
            }
        }
        Assert.Empty(missing);
    }
}
