using System.Globalization;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace LightMarkdownWriter.Services;

public static class MarkdownRenderer
{
    // Advanced extensions = GFM tables, task lists, footnotes, autolinks, strikethrough, heading ids,
    // and Diagrams (```mermaid blocks become <div class="mermaid">). Pipelines are immutable and thread-safe.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Renders HTML where every block element carries data-line="N" (0-based source line),
    /// which the preview uses to keep its scroll position in step with the editor.</summary>
    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document.Descendants<Block>())
            block.GetAttributes().AddProperty("data-line", block.Line.ToString(CultureInfo.InvariantCulture));
        return Markdown.ToHtml(document, Pipeline);
    }
}
