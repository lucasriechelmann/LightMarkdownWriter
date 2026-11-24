using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace LightMarkdownWriter.Services;

public static class MarkdownRenderer
{
    // Advanced extensions = GFM tables, task lists, footnotes, autolinks, strikethrough, heading ids,
    // GitHub alerts (> [!NOTE]), and Diagrams (```mermaid blocks become <div class="mermaid">).
    // YAML front matter is parsed so a leading --- block isn't rendered as a rule + heading.
    // Pipelines are immutable and thread-safe.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    /// <summary>Renders HTML where every block element carries data-line="N" (0-based source line),
    /// which the preview uses to keep its scroll position in step with the editor.</summary>
    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document.Descendants<Block>())
            block.GetAttributes().AddProperty("data-line", block.Line.ToString(CultureInfo.InvariantCulture));

        var html = Markdown.ToHtml(document, Pipeline);
        return document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is { } frontMatter
            ? FrontMatterHtml(frontMatter) + html
            : html;
    }

    /// <summary>
    /// Markdig renders front matter as nothing. The preview shows it as a collapsed box instead, so it's visible
    /// but out of the way; exports drop it (preview.js removes .front-matter), since it's metadata.
    /// </summary>
    private static string FrontMatterHtml(YamlFrontMatterBlock block)
    {
        var yaml = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0) yaml.Append('\n');
            yaml.Append(block.Lines.Lines[i].Slice.ToString());
        }

        return $"<details class=\"front-matter\" data-line=\"{block.Line}\"><summary>Front matter</summary>"
               + $"<pre><code class=\"language-yaml\">{WebUtility.HtmlEncode(yaml.ToString())}</code></pre></details>\n";
    }
}
