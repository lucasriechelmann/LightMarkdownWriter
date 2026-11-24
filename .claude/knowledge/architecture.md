# Architecture

## Projects and dependencies

```
tests/LightMarkdownWriter.Tests  ──references──▶  src/LightMarkdownWriter (WinExe, net10.0-windows, UseWPF)
```

NuGet dependencies of the app are listed in `src/LightMarkdownWriter/LightMarkdownWriter.csproj`:

| Package | Used for |
|---|---|
| WPF-UI | FluentWindow, Mica, theme manager, Fluent controls |
| AvalonEdit | Editor (`TextEditor`, `TextDocument`, undo stack, highlighting) |
| Markdig | Markdown → HTML |
| Microsoft.Web.WebView2 | Preview, plus off-screen PDF printing |
| CommunityToolkit.Mvvm | `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` |

Vendored web libraries live in `src/LightMarkdownWriter/Assets/Preview/vendor/`, at pinned versions downloaded from jsDelivr:
- github-markdown-css 5.8.1 (`github-markdown.css`, and the `-light` variant used for exports)
- highlight.js 11.11.1 (`highlight.min.js`, `hljs-github(-dark).min.css`)
- mermaid 11.4.1

`.gitattributes` marks them `linguist-vendored -diff`.

## Composition (no DI container)

`App.OnStartup` in `src/LightMarkdownWriter/App.xaml.cs` wires everything by hand, in this order:

1. **Single instance.** `SingleInstance.TryBecomePrimary()` (a named mutex). If another instance is running, the files are sent to it with `SendToPrimary` and this process exits.
2. **Settings and theme.** `SettingsService.Load()` gives `AppSettings`. The theme is applied before the first frame, using the `--theme=` argument if given, otherwise the setting.
3. **Session.** `SessionService.Load()` gives a `Session`.
4. **View model.** `new MainViewModel(new DialogService(), files, sessionService, session)` restores tabs, opens the files from the command line, then runs `CheckExternalChanges()`.
5. **Window.** `new MainWindow(viewModel, settings, themeOverride, session)` is shown. Then `SingleInstance.Listen(...)` forwards paths from later launches to `MainWindow.OpenFromOtherInstance`.

## Data entities

| Entity | Defined in | Persisted as |
|---|---|---|
| `DocumentViewModel` (one tab) | `ViewModels/DocumentViewModel.cs` | A `SessionTab` record, plus `backups/{Id:N}.md` when it has unsaved text |
| `MainViewModel` (tabs, active tab, `ViewMode`) | `ViewModels/MainViewModel.cs` | `session.json` (`Tabs`, `ActiveIndex`, `ViewMode`) |
| `Session`, `SessionTab`, `WindowPlacement` | `Services/SessionService.cs` | `%LOCALAPPDATA%\LightMarkdownWriter\session.json` |
| `AppSettings`, `AppTheme` | `Services/SettingsService.cs` | `%LOCALAPPDATA%\LightMarkdownWriter\settings.json`, with enums written as strings |
| `ViewMode` (Editor / Split / Preview) | `ViewModels/MainViewModel.cs` | In the session |
| `AssociationState`, ProgId `LightMarkdownWriter.md` | `Services/FileAssociationService.cs` | `HKCU\Software\Classes`, `HKCU\Software\LightMarkdownWriter\Capabilities`, `RegisteredApplications` |

**Key invariants:**
- **The "unsaved" flag is the undo stack's.** `DocumentViewModel.IsDirty` mirrors `!Document.UndoStack.IsOriginalFile`. A tab restored from a backup calls `DiscardOriginalFileMarker()` so it counts as unsaved.
- **Encodings record the byte-order mark (BOM).** They're stored as `utf-8` or `utf-8+bom`, because `WebName` alone loses the BOM. `SessionTab.FileWriteTimeUtc` lets the app detect edits made while it was closed.
- **Backups are only kept while needed.** `backups/*.md` exists only for tabs with `HasBackup = true`, and `SessionService.Save` deletes the rest. All writes are temp-then-rename.

## End-to-end data flow

**Typing → preview**
1. AvalonEdit's `TextDocument.TextChanged` starts a 150 ms debounce in `MainWindow`.
2. `MarkdownRenderer.ToHtml` parses with Markdig's `UseAdvancedExtensions` and stamps `data-line` (0-based source line) on every block.
3. `PreviewView.ShowHtml` posts `{type:"render", html, baseHref, totalLines, line, edge}` to `Assets/Preview/preview.js`.
4. `preview.js` swaps the HTML and runs highlight.js. It then runs Mermaid, caching each diagram's SVG by theme and source, and re-applies the scroll position.

**Scroll sync**
- **Editor → preview:** `TextView.ScrollOffsetChanged` → `EditorTopPosition()` gives a fractional line → `{type:"scrollToLine"}`.
- **Preview → editor:** a scroll in the preview posts `{type:"scroll", line, edge}` → `ScrollEditorTo`.
- **Loop guard:** each side ignores its own programmatic scroll for 150 ms.
- **Interpolation:** positions between `data-line` anchors are interpolated.

**Messages between the app and `preview.js`** (`chrome.webview` messages, JSON):

| Direction | Types |
|---|---|
| App → page | `render`, `scrollToLine`, `theme` (`background`, `accent`), `export` (`id`, `forClipboard`) |
| Page → app | `ready`, `scroll`, `exportResult` (`id`, `html` or `error`) |

**Resource hosts inside WebView2:** both are served by the `WebResourceRequested` handler in `PreviewView.OnWebResourceRequested`.
- `https://app.local/`: the preview page and libraries, read from **embedded resources** through `PreviewAssets`. The resource names are `Preview/...`, set by `<EmbeddedResource LogicalName=...>` in the csproj.
- `https://doc.local/`: files from the *active document's* folder. Each request checks that the path stays inside that folder. Folder mappings aren't used, because they only apply on the next page load.

**Navigation:** only `index.html` may load in the preview.
- `.md` links open as tabs through `MarkdownFileLinkClicked`.
- http, https and mailto links open in the default browser.
- Browser accelerator keys are disabled.

**Export** (`Services/ExportService.cs`, `MainWindow.ExportAsync`)
1. `PreviewView.GetExportHtmlAsync` asks the page to export a copy of its DOM. Mermaid is re-rendered in the light theme with `htmlLabels:false`, and the `data-*` attributes are stripped.
2. The result becomes one of three outputs:
   - **HTML file:** `BuildStandaloneHtml`, which inlines the light CSS and embeds local images.
   - **PDF:** `ExportPdfAsync`, which uses a hidden `CoreWebView2Controller` with scripts disabled and calls `PrintToPdfAsync`. Page size is A4 in metric regions, Letter elsewhere.
   - **Clipboard:** `CopyHtmlToClipboard`. It builds CF_HTML with `BuildCfHtml` (UTF-8 byte offsets), handed to WPF as a stream, with inline styles, PNG diagrams and the markdown source as plain text.

**Session persistence:** `MainWindow` saves about 2 s after any change, and on close with no prompt, by calling `MainViewModel.PersistSession(decorate)`. That writes the backups for dirty tabs, then `session.json`, adding the window placement and splitter share. If saving fails, closing falls back to the save prompt.

**External changes:** `Window.Activated` calls `CheckExternalChanges()`, which calls `DocumentViewModel.CheckDisk()` for each tab.
- A clean tab reloads with `ReloadFromDisk`, which replaces only the changed span so the caret and scroll position stay.
- A tab with unsaved edits gets `ExternalChange = Modified` or `Deleted`, which shows the banner in `MainWindow.xaml`.

## External services and OS integration

- **WebView2 Runtime:** comes with Windows 11. User data goes to `%LOCALAPPDATA%\LightMarkdownWriter\WebView2`. A missing runtime raises `WebView2RuntimeNotFoundException`, and the user sees an error.
- **Single instance:** a mutex `Local\LightMarkdownWriter-{user}-{session}` plus a pipe `...-open` (`PipeOptions.CurrentUserOnly`). The sender resolves absolute paths and calls `AllowSetForegroundWindow`.
- **Registry (HKCU only):** written by `FileAssociationService`, followed by `SHChangeNotify(SHCNE_ASSOCCHANGED)`. The Default apps page is opened with `ms-settings:defaultapps?registeredAppUser=LightMarkdownWriter`.
- **GitHub:** Releases host the exe and `SHA256SUMS.txt`. Pages serves `docs/`. Both are published by hand (see CLAUDE.md).

## Packaging

The publish profile is `src/LightMarkdownWriter/Properties/PublishProfiles/win-x64.pubxml`. It's re-included by an exception in `.gitignore`, which otherwise ignores `*.pubxml`.

| Setting | Value |
|---|---|
| Output | Framework-dependent single file, win-x64 |
| ReadyToRun | On |
| Native libraries | `IncludeNativeLibrariesForSelfExtract` (the WebView2 loader is extracted to `%TEMP%\.net\LightMarkdownWriter\…` on first run) |
| Satellite assemblies | English only |
| Output folder | `artifacts/publish/` (git-ignored) |
| Size | About 15 MB |
