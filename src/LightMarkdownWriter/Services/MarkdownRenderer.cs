using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LightMarkdownWriter.Services;

/// <summary>A heading in the document: level 1–6, visible text, 0-based source line.</summary>
public sealed record HeadingInfo(int Level, string Text, int Line);

public sealed record RenderResult(string Html, IReadOnlyList<HeadingInfo> Headings);

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
    public static string ToHtml(string markdown) => Render(markdown).Html;

    /// <summary>HTML for the preview plus the document's headings (for the outline), from a single parse.</summary>
    public static RenderResult Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document.Descendants<Block>())
            block.GetAttributes().AddProperty("data-line", SourceLine(block, markdown).ToString(CultureInfo.InvariantCulture));

        var headings = document.Descendants<HeadingBlock>()
            .Select(h => new HeadingInfo(h.Level, PlainText(h.Inline), SourceLine(h, markdown)))
            .Where(h => h.Text.Length > 0)
            .ToList();

        var html = Markdown.ToHtml(document, Pipeline);
        if (document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is { } frontMatter)
            html = FrontMatterHtml(frontMatter) + html;
        return new RenderResult(html, headings);
    }

    /// <summary>
    /// The 0-based line a block starts on. Markdig's Line is right except for setext headings ("Title" underlined
    /// with ---/===), where it points at the underline; their span starts at the text, so count lines up to it.
    /// </summary>
    private static int SourceLine(Block block, string markdown)
    {
        if (block is not HeadingBlock { IsSetext: true } || block.Span.Start <= 0 || block.Span.Start > markdown.Length)
            return block.Line;
        var line = 0;
        for (var i = 0; i < block.Span.Start; i++)
            if (markdown[i] == '\n') line++;
        return line;
    }

    /// <summary>A heading's visible text: markdown syntax (emphasis, links, code ticks) removed.</summary>
    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var text = new StringBuilder();
        foreach (var child in inline.Descendants())
        {
            switch (child)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded.ToString()); break;
                case AutolinkInline link: text.Append(link.Url); break;
                case LineBreakInline: text.Append(' '); break;
            }
        }
        return text.ToString().Trim();
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
