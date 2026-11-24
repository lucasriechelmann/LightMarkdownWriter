# LightMarkdownWriter — Plan

A light, fast Windows 11 markdown editor with a GitHub-quality live preview.

## Decisions

| Area | Choice |
|---|---|
| Framework | WPF on .NET 10 (LTS) |
| Win11 look | [WPF-UI](https://github.com/lepoco/wpfui) — `FluentWindow`, Mica backdrop, Fluent controls, follows system theme + accent |
| Editor | AvalonEdit with custom markdown highlighting (`.xshd`) |
| Markdown → HTML | Markdig (`UseAdvancedExtensions` = GFM tables, task lists, footnotes, autolinks, strikethrough) |
| Preview | One shared WebView2 control, rendering the active tab |
| Preview extras | highlight.js (code), Mermaid (diagrams), github-markdown-css (light/dark) — all bundled locally, works offline |
| Layout | Split view (default) with toggles: Editor only / Split / Preview only |
| Documents | Tabs |
| Session | Restore all tabs **including unsaved edits** (Notepad++-style backups) |
| Export | HTML file, PDF, Copy as HTML (clipboard) |
| Packaging | Framework-dependent single-file `win-x64` exe, portable; optional `.md` "Open with" registration |

## Architecture

```
LightMarkdownWriter/
  App.xaml(.cs)              single-instance + startup args, theme
  MainWindow.xaml(.cs)       FluentWindow: title bar, tab strip, toolbar, split host
  Views/
    EditorView               AvalonEdit + shortcuts + find/replace bar
    PreviewView              WebView2 host, JS bridge
  ViewModels/                (CommunityToolkit.Mvvm)
    MainViewModel            tabs, commands, view mode
    DocumentViewModel        path, text, IsDirty, caret/scroll, backup id
  Services/
    MarkdownRenderer         Markdig pipeline, adds data-line attrs
    SessionService           session.json + unsaved backups
    ExportService            HTML / PDF / clipboard (CF_HTML)
    SingleInstanceService    named pipe: second launch → open tab in first
    FileAssociationService   HKCU ProgId + OpenWithProgids
  Assets/
    Preview/index.html, preview.js, github-markdown.css, highlight.min.js, mermaid.min.js
    Markdown.xshd
```

### Preview pipeline
1. Editor text changes → debounce (~150 ms) → `MarkdownRenderer.Render(text)`.
2. Markdig output includes `data-line="N"` on block elements (`UsePreciseSourceLocation`).
3. C# sends the HTML through `PostWebMessageAsJson`. `preview.js` swaps the `<article>` innerHTML, runs highlight.js and then Mermaid, and keeps the scroll position. There is no page reload, so the preview doesn't flicker.
4. Assets are served with `SetVirtualHostNameToFolderMapping("app.local", …)`, so nothing is downloaded at runtime.
5. Relative images load from the document's folder through `https://doc.local/`. A `WebResourceRequested` handler serves these files, reading the active document's folder on every request and refusing paths outside it. A folder mapping wouldn't work here, because mappings only take effect on the next page load.
6. The preview uses the standard (HWND) WebView2, not `WebView2CompositionControl`, which needs the ~25 MB Windows SDK projection. Because the standard control is opaque, both the preview page and the editor card use the theme's solid `SolidBackgroundFillColorTertiary` colour.

### Scroll sync (fixes the "clunky workflow")
- When the editor scrolls, its first visible line is sent to JS, which scrolls to the nearest `[data-line]` element (interpolated between elements).
- When the preview scrolls, JS sends its top `data-line` back, and the editor scrolls to that line.
- A short "who's driving" lock prevents feedback loops.

### Editor
- Markdown syntax colours: headings, emphasis, inline/fenced code, links, lists, quotes, with light and dark palettes based on VS Code's Light+/Dark+.
  - The rules live in `Editing/Markdown.xshd`. Colours are substituted into the XML at load time, because loaded definitions are frozen.
  - xshd gotchas: `<Rule>` content can't be CDATA. Rules are compiled with ExplicitCapture, so backreferences need named groups.
- Shortcuts:
  - Formatting: Ctrl+B bold, Ctrl+I italic, Ctrl+E inline code, Ctrl+K link, Ctrl+Shift+K code block. Each toggles and is a single undo step. These buttons are also in the command bar.
  - Files: Ctrl+N/O/S/W, Ctrl+Tab
  - View: Ctrl+1/2/3 (switch view mode)
- Enter continues bullet, numbered (incrementing), task (`- [ ] `) and quote lines. Enter on an empty item ends the list.
- Find & replace: a custom bar (AvalonEdit's SearchPanel can't replace).
  - Shortcuts: Ctrl+F / Ctrl+H to open, F3 / Shift+F3 for next/previous, Alt+C / Alt+W / Alt+R for case, whole word and regex.
  - It highlights all matches, shows "n of N" and reports invalid regexes. Replace all is a single undo step.
- Word wrap on by default. The font is Cascadia Mono, because Cascadia Code's ligatures disguise markdown syntax. Font configurable later.

### Session restore (incl. unsaved)
- Stored under `%LOCALAPPDATA%\LightMarkdownWriter\`:
  - `session.json` holds the tab order, paths, active tab, caret/scroll position, view mode and window bounds.
  - `backups\{guid}.md` holds the text of dirty or untitled tabs. It is written on a debounce (~2 s) and on close.
- On close, nothing prompts. Everything is kept and restored on the next start (Notepad++ behaviour).
  - If the session can't be written (disk full, permissions), closing falls back to the save prompt, so nothing is lost silently.
- The session is also saved about 2 s after changes, for crash safety. Writes go to a temp file first and are then renamed (atomic).
- Closing a single dirty tab does prompt (Save / Don't save / Cancel).
- Restored tabs:
  - Unsaved text is restored and the tab is marked unsaved via `UndoStack.DiscardOriginalFileMarker()`. Undo history isn't kept across restarts.
  - Each tab's caret and scroll position are remembered, both across restarts and when switching tabs.
  - The window position, size and maximized state are restored, but only if the window would still be on a screen. So is the splitter position.
- Encoding is stored as `utf-8` or `utf-8+bom`. `Encoding.WebName` alone loses the byte-order mark (BOM), and restoring from it would add a BOM to files that had none.
- External changes are checked when the window is activated, and for edits made while the app was closed (via the file timestamp stored in the session).
  - A clean tab reloads silently. Only the changed span is replaced, so the caret and scroll position stay put.
  - A tab with unsaved edits shows a banner: Reload from disk / Keep my version. A deleted file offers Keep / Close tab.
- Single instance: a named mutex plus a named pipe (`CurrentUserOnly`), scoped per user and logon session.
  - A second launch sends absolute paths (resolved in the sender, since its working directory differs) and exits.
  - It calls `AllowSetForegroundWindow` so the running window may come to the front.

### Export
- Exports are always in the **light theme**. The command bar has an Export menu, with shortcuts Ctrl+Shift+E (HTML), Ctrl+P (PDF) and Ctrl+Shift+C (Copy as HTML).
- The preview page builds the export from a copy of its rendered DOM. Mermaid is re-rendered in light colours with `htmlLabels: false` (pure SVG text), and internal `data-*` attributes are stripped.
- **HTML**: a standalone file with no scripts.
  - The `github-markdown-light.css`, `hljs-github` and `export.css` styles are inlined.
  - Mermaid diagrams are inline SVG, and local images are embedded as data URIs (up to 20 MB each).
  - The title comes from the first `<h1>`.
- **PDF**: a hidden `CoreWebView2Controller` with scripts disabled loads the standalone HTML and calls `PrintToPdfAsync`.
  - Page size is A4 in metric regions, Letter otherwise. Margins are 0.6 in.
  - Print CSS wraps long code lines and avoids breaking inside blocks.
- **Copy as HTML**: CF_HTML with UTF-8 byte offsets, handed to WPF as a byte stream so it isn't re-encoded, plus the markdown source as plain text.
  - Styles are inlined per element, because Outlook and Word drop `<style>` blocks.
  - Diagrams are rasterized to 2× PNG, because Office doesn't render inline SVG.
- The export result appears in the command bar with an **Open** button, not as a toast: the preview's native window would cover anything drawn on top of it.

### Theme
- Follows the Windows light/dark setting and accent color live, through the WPF-UI theme watcher. Settings offer System / Light / Dark.
- The theme reaches the preview through `CoreWebView2.Profile.PreferredColorScheme`. The GitHub CSS, the highlight.js theme and Mermaid all follow `prefers-color-scheme` from that. The page background and the link accent are sent as messages.
- `--theme=light|dark` on the command line forces a theme. It's used for testing now, and the settings override will build on it later.

### Keyboard focus in the preview
- The WebView2 is a native child window, so the window's `InputBindings` never see keys while it has focus. The control forwards accelerator keys as a `KeyDown` on itself, and `MainWindow` matches those against its own `KeyBinding`s. Browser accelerators such as F5, Ctrl+R and Ctrl+P are disabled.

### Packaging
- Build command: `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`
- Needs the .NET 10 Desktop Runtime. The WebView2 runtime is already part of Windows 11.
- A Settings option, "Register for .md files", writes the HKCU ProgId and OpenWithProgids entries (no admin rights needed). Windows 11 won't let an app set itself as the default, so the app then opens the Settings → Default apps page.

## Milestones

1. **Skeleton** — solution, FluentWindow with Mica, tab strip, AvalonEdit in a tab, open/save/new.
2. **Preview** — WebView2 and Markdig, debounced live render, GFM, highlight.js, Mermaid, GitHub CSS, view-mode toggles.
3. **Sync & theme** — two-way scroll sync, system theme/accent in both app and preview.
4. **Editor polish** — syntax colors, formatting shortcuts, find & replace bar.
5. **Session** — restore tabs and unsaved backups, external-change detection, single instance (named pipe).
6. **Export** — HTML, PDF, Copy as HTML.
7. **Ship** — settings flyout (theme, font, wrap), file association, publish profile, README.

## Out of scope (for now)
Math/KaTeX, image paste, folder sidebar, spell check, cross-platform. All could be added later. Math is a one-line Markdig extension plus KaTeX.
