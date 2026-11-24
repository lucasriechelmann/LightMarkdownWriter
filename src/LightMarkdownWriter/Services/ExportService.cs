using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LightMarkdownWriter.Services;

/// <summary>Turns the preview's rendered HTML into a standalone .html file, a PDF, or clipboard HTML.</summary>
public static partial class ExportService
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private const long MaxEmbeddedImageBytes = 20 * 1024 * 1024;

    // ---------- Standalone HTML ----------

    /// <summary>A self-contained page: light GitHub styling inlined and local images embedded as data URIs.</summary>
    public static string BuildStandaloneHtml(string contentHtml, string title, string? documentFolder)
    {
        var css = string.Concat(
            ReadAsset("vendor", "github-markdown-light.css"), "\n",
            ReadAsset("vendor", "hljs-github.min.css"), "\n",
            ReadAsset("export.css"));
        if (contentHtml.Contains("class=\"katex", StringComparison.Ordinal))
            css += "\n" + KatexCss.Value; // only documents with math pay for the ~350 KB of fonts

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{WebUtility.HtmlEncode(title)}</title>
            <style>
            {css}
            </style>
            </head>
            <body>
            <article class="markdown-body">
            {EmbedLocalImages(contentHtml, documentFolder)}
            </article>
            </body>
            </html>
            """;
    }

    public static void WriteHtmlFile(string path, string html) => File.WriteAllText(path, html, Utf8NoBom);

    /// <summary>The first heading's text, or the file name.</summary>
    public static string TitleFor(string contentHtml, string fallback)
    {
        var match = FirstHeading().Match(contentHtml);
        var text = match.Success ? WebUtility.HtmlDecode(Tags().Replace(match.Groups[1].Value, "")).Trim() : "";
        return text.Length > 0 ? text : fallback;
    }

    /// <summary>Replaces relative &lt;img src&gt; paths with data URIs so the output doesn't depend on files next to it.</summary>
    public static string EmbedLocalImages(string html, string? documentFolder)
    {
        if (documentFolder is null) return html;
        return ImgSrc().Replace(html, match =>
        {
            var src = WebUtility.HtmlDecode(match.Groups["src"].Value);
            if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("//", StringComparison.Ordinal)
                || Uri.TryCreate(src, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
                return match.Value;

            try
            {
                var relative = Uri.UnescapeDataString(src.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(documentFolder, relative));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxEmbeddedImageBytes) return match.Value;
                var dataUri = $"data:{MediaTypes.ForPath(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
                return match.Groups["before"].Value + dataUri + match.Groups["after"].Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return match.Value; // leave the original reference
            }
        });
    }

    // ---------- PDF ----------

    /// <summary>
    /// Prints the standalone HTML to PDF with an off-screen WebView (scripts disabled: the export has none).
    /// A4 in metric regions, Letter elsewhere.
    /// </summary>
    public static async Task ExportPdfAsync(CoreWebView2Environment environment, IntPtr parentWindow, string standaloneHtml, string pdfPath)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"LightMarkdownWriter-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tempFile, standaloneHtml, Utf8NoBom);
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow);
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.IsScriptEnabled = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(new Uri(tempFile).AbsoluteUri);
            if (!await loaded.Task) throw new IOException("Could not load the document for printing.");

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.6; // inches
            if (RegionInfo.CurrentRegion.IsMetric)
            {
                settings.PageWidth = 8.27;  // A4
                settings.PageHeight = 11.69;
            }

            if (!await core.PrintToPdfAsync(pdfPath, settings))
                throw new IOException("Printing to PDF failed.");
        }
        finally
        {
            controller?.Close();
            try { File.Delete(tempFile); } catch (IOException) { /* temp folder cleanup will get it */ }
        }
    }

    // ---------- Clipboard ----------

    /// <summary>
    /// Puts the fragment on the clipboard as CF_HTML ("HTML Format") — which needs a header with UTF-8 byte offsets,
    /// otherwise Office pastes raw tags — plus the markdown source as plain text for plain-text targets.
    /// </summary>
    public static void CopyHtmlToClipboard(string fragmentHtml, string plainText)
    {
        // Hand WPF raw bytes (a stream) so it doesn't re-encode the string and break the offsets.
        var data = new DataObject();
        data.SetData(DataFormats.Html, new MemoryStream(BuildCfHtml(fragmentHtml)));
        data.SetData(DataFormats.UnicodeText, plainText);
        Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>The CF_HTML payload: header with UTF-8 byte offsets, the fragment wrapped in a minimal document, NUL-terminated.</summary>
    public static byte[] BuildCfHtml(string fragmentHtml)
    {
        const string headerFormat = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";

        // Offsets are fixed-width, so the header's length doesn't depend on the numbers in it.
        var startHtml = Utf8NoBom.GetByteCount(string.Format(CultureInfo.InvariantCulture, headerFormat, 0, 0, 0, 0));
        var startFragment = startHtml + Utf8NoBom.GetByteCount(prefix);
        var endFragment = startFragment + Utf8NoBom.GetByteCount(fragmentHtml);
        var endHtml = endFragment + Utf8NoBom.GetByteCount(suffix);
        var cfHtml = string.Format(CultureInfo.InvariantCulture, headerFormat, startHtml, endHtml, startFragment, endFragment)
                     + prefix + fragmentHtml + suffix;

        return Utf8NoBom.GetBytes(cfHtml + "\0");
    }

    // ---------- Helpers ----------

    private static string ReadAsset(params string[] parts) => PreviewAssets.ReadText(string.Join('/', parts));

    /// <summary>KaTeX's stylesheet with its WOFF2 fonts inlined as data URIs, so exported math renders anywhere.</summary>
    private static readonly Lazy<string> KatexCss = new(() =>
        KatexFont().Replace(ReadAsset("vendor", "katex", "katex.min.css"), match =>
        {
            using var font = PreviewAssets.Open("vendor/katex/fonts/" + match.Groups[1].Value);
            if (font is null) return match.Value;
            using var buffer = new MemoryStream();
            font.CopyTo(buffer);
            return $"url(data:font/woff2;base64,{Convert.ToBase64String(buffer.ToArray())})";
        }));

    [GeneratedRegex(@"url\(fonts/([^)]+\.woff2)\)")]
    private static partial Regex KatexFont();

    [GeneratedRegex(@"(?<before><img\b[^>]*?\bsrc="")(?<src>[^""]*)(?<after>"")", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FirstHeading();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
