using System.IO;
using System.Reflection;

namespace LightMarkdownWriter.Services;

/// <summary>
/// The preview page and its libraries, embedded in the assembly (LogicalName "Preview/...") so a single-file
/// publish is really one exe — nothing has to sit next to it.
/// </summary>
public static class PreviewAssets
{
    private static readonly Assembly Assembly = typeof(PreviewAssets).Assembly;

    // Resource names keep MSBuild's RecursiveDir separators; normalize to forward slashes for lookups.
    private static readonly Dictionary<string, string> Names = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("Preview/", StringComparison.Ordinal))
        .ToDictionary(n => n["Preview/".Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens an asset by its path under Assets/Preview (e.g. "vendor/mermaid.min.js"); null if absent.</summary>
    public static Stream? Open(string relativePath) =>
        Names.TryGetValue(relativePath.TrimStart('/'), out var name) ? Assembly.GetManifestResourceStream(name) : null;

    public static string ReadText(string relativePath)
    {
        using var stream = Open(relativePath) ?? throw new FileNotFoundException("Missing embedded asset", relativePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
