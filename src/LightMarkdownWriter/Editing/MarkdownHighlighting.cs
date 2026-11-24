using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace LightMarkdownWriter.Editing;

/// <summary>Markdown syntax highlighting in a light and a dark palette (VS Code Light+/Dark+ inspired).</summary>
public static class MarkdownHighlighting
{
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["Heading"] = "#569CD6",
        ["Code"] = "#CE9178",
        ["Link"] = "#4EC9B0",
        ["Quote"] = "#6A9955",
        ["Marker"] = "#C586C0",
        ["Dim"] = "#858585",
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["Heading"] = "#0451A5",
        ["Code"] = "#A31515",
        ["Link"] = "#267F99",
        ["Quote"] = "#008000",
        ["Marker"] = "#AF00DB",
        ["Dim"] = "#8A8A8A",
    };

    private static IHighlightingDefinition? _dark;
    private static IHighlightingDefinition? _light;

    public static IHighlightingDefinition Get(bool dark) =>
        dark ? _dark ??= Load(DarkPalette) : _light ??= Load(LightPalette);

    private static IHighlightingDefinition Load(Dictionary<string, string> palette)
    {
        // Loaded definitions are frozen, so colors are substituted into the XML before loading.
        using var stream = typeof(MarkdownHighlighting).Assembly
            .GetManifestResourceStream("LightMarkdownWriter.Editing.Markdown.xshd")
            ?? throw new InvalidOperationException("Markdown.xshd resource missing");
        var xml = new StreamReader(stream).ReadToEnd();
        foreach (var (name, color) in palette)
            xml = xml.Replace("{" + name + "}", color, StringComparison.Ordinal);

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
