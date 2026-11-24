using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace LightMarkdownWriter.Views;

/// <summary>Hosts the single shared WebView2 that renders the active document's HTML.</summary>
public partial class PreviewView : UserControl
{
    private const string AppHost = "app.local";   // bundled preview page + libraries
    private const string DocHost = "doc.local";   // the active document's folder, for relative images/links
    private static readonly string PageUrl = $"https://{AppHost}/index.html";

    private Task? _initTask;
    private bool _pageReady;
    private (string Html, string? Folder, int TotalLines)? _pending;
    private (string Html, string? Folder, int TotalLines)? _lastShown; // re-sent if the page ever reloads
    private string? _documentFolder; // read on every doc.local request, so switching tabs needs no reload
    private bool _dark;
    private Color _background = Colors.White;
    private Color? _accent;
    private double _syncLine;      // fractional 0-based source line at the top of the view
    private string? _syncEdge;     // "top" | "bottom" | null

    /// <summary>Raised when a link to another local markdown file is clicked (full path).</summary>
    public event Action<string>? MarkdownFileLinkClicked;

    /// <summary>Raised when the user scrolls the preview: fractional 0-based source line at the top, and edge.</summary>
    public event Action<double, string?>? PreviewScrolled;

    public PreviewView()
    {
        InitializeComponent();
    }

    public Task InitializeAsync() => _initTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        // Keep browser data out of the exe folder so the app also works from read-only locations.
        var dataFolder = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "LightMarkdownWriter", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await WebView.EnsureCoreWebView2Async(environment);

        var core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsWebMessageEnabled = true;
        // No F5/Ctrl+R reload, Ctrl+P print, Ctrl+F find etc. from the browser; app shortcuts still reach WPF.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

        // Both hosts are served by hand:
        //  - app.local: the page and libraries, embedded in the exe (single-file publish needs no Assets folder).
        //  - doc.local: the active document's folder. A folder mapping wouldn't do, since mappings only take
        //    effect on the next page load, but the folder changes whenever the user switches tabs.
        core.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter($"https://{DocHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            HandleLink(e.Uri);
        };

        core.Navigate(PageUrl);
    }

    /// <summary>Light/dark for the page's CSS, the solid background it should paint (matches the editor card),
    /// and the accent color used for links.</summary>
    public void SetTheme(bool dark, Color background, Color? accent)
    {
        _dark = dark;
        _background = background;
        _accent = accent;
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, background.R, background.G, background.B);
        if (WebView.CoreWebView2 is { } core)
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        if (_pageReady) SendTheme();
    }

    private void SendTheme() =>
        WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "theme",
            background = ToHex(_background),
            accent = _accent is { } a ? ToHex(a) : null,
        }));

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Shows rendered HTML. <paramref name="documentFolder"/> resolves relative image paths (null for untitled).</summary>
    public void ShowHtml(string html, string? documentFolder, int totalLines)
    {
        _pending = (html, documentFolder, totalLines);
        if (_pageReady) Flush(); // otherwise delivered when the page says it's ready
    }

    /// <summary>Scrolls the preview so <paramref name="line"/> (fractional, 0-based) is at the top.</summary>
    public void SyncToLine(double line, string? edge)
    {
        _syncLine = line;
        _syncEdge = edge;
        if (_pageReady)
            WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scrollToLine", line, edge }));
    }

    private void Flush()
    {
        if (_pending is not var (html, folder, totalLines)) return;
        _pending = null;
        _lastShown = (html, folder, totalLines);

        _documentFolder = folder is not null && Directory.Exists(folder) ? folder : null;
        var baseHref = _documentFolder is null ? $"https://{AppHost}/" : $"https://{DocHost}/";
        WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "render",
            html,
            baseHref,
            totalLines,
            line = _syncLine,
            edge = _syncEdge,
        }));
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = WebView.CoreWebView2.Environment;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            e.Response = env.CreateWebResourceResponse(null, 400, "Bad Request", "");
            return;
        }

        if (uri.Host.Equals(AppHost, StringComparison.OrdinalIgnoreCase))
        {
            var asset = Uri.UnescapeDataString(uri.AbsolutePath);
            e.Response = LightMarkdownWriter.Services.PreviewAssets.Open(asset) is { } stream
                ? env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentTypeFor(asset)}")
                : env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        var path = ResolveDocumentPath(uri);
        if (path is null)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        try
        {
            // WebView2 takes ownership of the stream and disposes it after reading.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentTypeFor(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }

    /// <summary>Maps https://doc.local/... to a file inside the document folder; null if outside it or missing.</summary>
    private string? ResolveDocumentPath(Uri uri)
    {
        if (_documentFolder is null || !uri.Host.Equals(DocHost, StringComparison.OrdinalIgnoreCase)) return null;

        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var root = Path.TrimEndingDirectorySeparator(_documentFolder) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath) ? fullPath : null;
    }

    private static string ContentTypeFor(string path) => LightMarkdownWriter.Services.MediaTypes.ForPath(path);

    // ---------- Export ----------

    private readonly Dictionary<int, TaskCompletionSource<string>> _exportRequests = [];
    private int _nextExportId;

    /// <summary>The browser environment, for creating an off-screen WebView (PDF export).</summary>
    public CoreWebView2Environment? Environment => WebView.CoreWebView2?.Environment;

    /// <summary>
    /// The rendered document as HTML for export, in the light theme: highlighted code, Mermaid diagrams as SVG
    /// (or, <paramref name="forClipboard"/>, as PNG with inline styles so it pastes into email/Word).
    /// </summary>
    public Task<string> GetExportHtmlAsync(bool forClipboard)
    {
        if (!_pageReady) throw new InvalidOperationException("The preview isn't ready yet.");
        var id = ++_nextExportId;
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exportRequests[id] = request;
        WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "export", id, forClipboard }));
        return request.Task;
    }

    private void CompleteExport(JsonElement message)
    {
        var id = message.GetProperty("id").GetInt32();
        if (!_exportRequests.Remove(id, out var request)) return;
        if (message.TryGetProperty("error", out var error))
            request.SetException(new InvalidOperationException(error.GetString()));
        else
            request.SetResult(message.GetProperty("html").GetString() ?? "");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var json = JsonDocument.Parse(e.WebMessageAsJson);
        var root = json.RootElement;
        switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "ready":
                _pageReady = true;
                SendTheme();
                _pending ??= _lastShown; // after an unexpected reload, show the last document again
                Flush();
                break;

            case "scroll":
                _syncLine = root.GetProperty("line").GetDouble();
                _syncEdge = root.TryGetProperty("edge", out var edge) && edge.ValueKind == JsonValueKind.String
                    ? edge.GetString()
                    : null;
                PreviewScrolled?.Invoke(_syncLine, _syncEdge);
                break;

            case "exportResult":
                CompleteExport(root);
                break;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The preview page itself is the only thing allowed to load in the WebView.
        if (e.Uri == PageUrl) return;
        e.Cancel = true;
        HandleLink(e.Uri);
    }

    private void HandleLink(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target)) return;

        // Relative link inside the document folder: open .md files as tabs, anything else with its default app.
        if (target.Host.Equals(DocHost, StringComparison.OrdinalIgnoreCase))
        {
            if (ResolveDocumentPath(target) is not { } localPath) return;

            var ext = Path.GetExtension(localPath).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".mdown" or ".mkd" or ".txt")
                MarkdownFileLinkClicked?.Invoke(localPath);
            else
                ShellOpen(localPath);
            return;
        }

        if (target.Scheme is "http" or "https" or "mailto")
            ShellOpen(target.AbsoluteUri);
    }

    private static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No handler registered; nothing sensible to do.
        }
    }
}
