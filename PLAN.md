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

### Settings
- A separate Settings window (Ctrl+, or the gear button) in Windows 11 Settings-page style, built from CardControls.
  - It's its own top-level window rather than an overlay dialog, so the preview's native window can't cover it.
- Bound directly to `AppSettings` (theme, font, font size, word wrap, line numbers). Every change applies live and is saved to `settings.json`.
- Theme: System follows Windows live through `SystemThemeWatcher`; Light and Dark are fixed. `--theme=` overrides it for the run until the user changes it in Settings.
- The font list shows installed monospaced fonts, detected by comparing the advance widths of 'i' and 'W'.

### Packaging
- `dotnet publish src/LightMarkdownWriter -p:PublishProfile=win-x64` produces `artifacts/publish/LightMarkdownWriter.exe`. It's one file, about 15 MB, framework-dependent and ReadyToRun-compiled.
  - The preview assets are embedded resources (`LogicalName="Preview/..."`) served to WebView2 by the `WebResourceRequested` handler, so no `Assets` folder is needed.
  - `WebView2Loader.dll` is bundled via `IncludeNativeLibrariesForSelfExtract`.
- Requirements: the .NET 10 Desktop Runtime. The WebView2 runtime is part of Windows 11.
- File association (Settings → Files) writes only under HKCU, so no admin rights are needed:
  - the ProgId with icon and open command,
  - `OpenWithProgids` for .md and .markdown,
  - `Applications\LightMarkdownWriter.exe`,
  - Capabilities plus `RegisteredApplications`, so the app shows up in Default apps.
  - Afterwards it calls `SHChangeNotify`. Windows 11 won't let an app make itself the default, so a button opens `ms-settings:defaultapps?registeredAppUser=LightMarkdownWriter`.
- The app icon is a generated multi-size ICO (`Assets/app.ico`), used for the exe, window, title bar and the registered file type.

### Tests
- `tests/LightMarkdownWriter.Tests` (xUnit) covers:
  - file association, against a throwaway HKCU key that's deleted afterwards,
  - CF_HTML byte offsets,
  - embedding local images,
  - BOM preservation through session restore,
  - the `data-line` source markers.

## Milestones (all done)

1. **Skeleton** — solution, FluentWindow with Mica, tab strip, AvalonEdit in a tab, open/save/new.
2. **Preview** — WebView2 and Markdig, debounced live render, GFM, highlight.js, Mermaid, GitHub CSS, view-mode toggles.
3. **Sync & theme** — two-way scroll sync, system theme/accent in both app and preview.
4. **Editor polish** — syntax colors, formatting shortcuts, find & replace bar.
5. **Session** — restore tabs and unsaved backups, external-change detection, single instance (named pipe).
6. **Export** — HTML, PDF, Copy as HTML.
7. **Ship** — settings flyout (theme, font, wrap), file association, publish profile, README.

## Roadmap (after 1.0)

These are the suggestions collected after the 1.0 release, grouped into milestones in priority order. Each task has an ID, a size (**S** = an hour or two, **M** = about half a day, **L** = a day or more), the area it touches, and a "done when" check.

The rules in CLAUDE.md still apply to every task:
- `dotnet test` passes, and UI changes are checked in the running app.
- No overlays on the preview.
- Windows 10 keeps working.
- Update `.claude/knowledge/` when a task changes architecture or data formats.

### Milestone 8: Everyday essentials (done, apart from 8.2's manual check)

The gaps you notice in the first hour of use, plus the safety net for the "never lose text" promise.

- [x] **8.1 Crash safety net** (S, `App.xaml.cs`)
  - Handle `DispatcherUnhandledException`, `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`.
  - Persist the session immediately, show the error in a dialog, and keep running when it's recoverable.
  - *Done when:* an exception thrown on purpose doesn't close the app, and the unsaved text is in `backups/`.
  - **Outcome:** checked in the app with the debug-only shortcut **Ctrl+Alt+Shift+F12**, which throws a test error. The app kept running, the dialog appeared, and the backup was written by the handler.
  - **Decisions:**
    - **Crash log.** Errors are appended to `%LOCALAPPDATA%\LightMarkdownWriter\crash.log`. It holds exceptions only (no logging framework and no document content) and rolls over to `crash.old.log` at 1 MB.
    - **Error loop guard.** More than 5 errors within 10 s means the app is in an error loop, so it saves and lets the app close rather than showing endless dialogs.
    - **Background-thread errors** always end the process, so the handler only saves the session (waiting up to 3 s for the UI thread).
- [ ] **8.2 Drag and drop files** (S, `MainWindow`)
  - Dropping `.md`/`.markdown`/`.txt` files on the window opens them as tabs.
  - Dropping an image on the editor inserts `![](relative/path)`, relative to the document's folder, or an absolute path for untitled tabs.
  - Watch out: the preview is a native window, so drops onto it need their own handling, or must be blocked there.
  - *Done when:* dropping files onto the editor, the tab strip and the preview area behaves as described.
  - **Status: built; waiting for a manual drop test.** The link building is unit-tested: relative paths with `%20`/`%28`/`%29` escaping, `../`, and a `file:///` URI for untitled documents. I couldn't safely automate a real OLE drag, because it takes over the mouse.
  - **How it works:**
    - The window's *tunnelling* `PreviewDragOver`/`PreviewDrop` events handle file drops, so AvalonEdit's text drag-and-drop is unaffected.
    - `WebView2.AllowExternalDrop = false`, so a drop onto the preview falls through to the window instead of the browser navigating to the file.
    - Images are inserted at the drop point (or the caret) in the active tab.
- [x] **8.3 Status bar** (M, `MainWindow.xaml`, `DocumentViewModel`)
  - Shows line and column, word count (and selection count), encoding (UTF-8 / UTF-8 BOM / other) and line endings (CRLF / LF).
  - Clicking the encoding or line-ending field converts the document. Converting marks the tab unsaved; the change is written on the next save.
  - Placement: below the editor/preview grid, never over the preview.
  - *Done when:* the values update as you type, and converting then saving changes the bytes on disk as expected (unit-test the conversion).
  - **Outcome:** checked in the app. Converting to LF and saving left 0 CR bytes; switching to UTF-8 with BOM wrote `EF BB BF`.
  - **How it works:**
    - Conversions are `MainViewModel` commands. Line endings change in one undo step. Changing the encoding marks the tab unsaved, but undo doesn't revert it.
    - The word count skips markdown symbols and task checkboxes.
    - The logic lives in `Editing/TextStats.cs` and is unit-tested.
- [x] **8.4 YAML front matter** (S, `MarkdownRenderer`, `preview.css`)
  - Enable Markdig's `UseYamlFrontMatter`, so a `---` block at the top no longer renders as a rule and text.
  - Show it as a small collapsed "Front matter" box in the preview, or hide it. Decide while building, and record the choice here.
  - Add highlighting for the block in `Markdown.xshd`.
  - *Done when:* a Jekyll/Hugo-style file previews cleanly, and a unit test covers the rendering.
  - **The old bug:** front matter used to render as a horizontal rule followed by a heading made of the YAML lines.
  - **Decision:** the preview shows it as a collapsed `<details class="front-matter">` with YAML highlighting. Exports leave it out, since it's metadata.
  - **Changed from the plan:** editor colouring is done by `Editing/FrontMatterColorizer.cs`, not `.xshd`. xshd rules can't anchor to the start of the document, so a `---` span would also swallow the text between any two horizontal rules.
- [x] **8.5 GitHub alerts** (S, `MarkdownRenderer`, `preview.css`, `export.css`)
  - Render `> [!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` and `[!CAUTION]` like GitHub does (`UseAlertBlocks`, if it isn't already part of `UseAdvancedExtensions`).
  - Style them in the preview, both themes, and in exports. Copy-as-HTML needs inline styles.
  - *Done when:* all five render in the preview, in HTML/PDF exports and in clipboard HTML.
  - **Outcome:** `UseAdvancedExtensions` already includes alerts, with GitHub's own HTML and icons. Checked in the preview (dark), in an HTML export, and on the clipboard (inline borders and title colours in GitHub's palette).
  - **Fix needed:** Markdig's icon SVGs lack GitHub's `.octicon` class, so they rendered black. A `fill: currentColor` rule was added in `preview.css` and `export.css`.

### Milestone 9: Finding your way around (done)

- [x] **9.1 Recent files** (M, `SettingsService` or a new `RecentFilesService`, command bar)
  - Keep the last 15 opened files, most recent first, and drop missing ones when shown.
  - Show them in an Open drop-down, and in the Windows jump list on the taskbar (`JumpList`).
  - *Done when:* the list survives restarts, and picking a file opens it (or switches to it if it's already open).
- [x] **9.2 Reopen closed tab** (S, `MainViewModel`)
  - Ctrl+Shift+T reopens the last closed tabs in order, including unsaved text discarded at close (held in memory for the session).
  - *Done when:* closing three tabs and pressing Ctrl+Shift+T three times brings them back in reverse order.
- [x] **9.3 Go to line** (S, `MainWindow`)
  - Ctrl+G opens a small line-number box in the command bar (not over the preview), then jumps there and centres the line.
  - *Done when:* it works from the editor and from the preview.
- [x] **9.4 Heading outline** (M, new view, `MarkdownRenderer`)
  - A toggleable panel listing the document's headings, built from the Markdig syntax tree.
  - Clicking a heading scrolls both panes, reusing the scroll-sync lines.
  - *Done when:* the outline updates as you type and clicking a heading lands on it in both panes.
  - **Outcomes** (all checked in the running app, with 15 new unit tests):
    - **9.1** `Services/RecentFilesService.cs` keeps 15 files in `recent.json`, newest first, matched case-insensitively. Missing files are kept but hidden.
      - Files are recorded on open and on save. The History button next to Open builds its menu on each click (recent files, Reopen closed tab, Clear).
      - `Services/TaskbarJumpList.cs` fills a "Recent" jump-list category; each entry launches the exe with the file, and single instance turns that into a tab.
    - **9.2** Closed tabs are kept in memory (up to 20) in the session's snapshot format (`SessionTab` plus text), so a tab closed with "Don't save" comes back with the discarded text as unsaved. Empty untitled tabs aren't kept.
    - **9.3** Ctrl+G opens a box in the command bar. It accepts `line`, `line:col` or `line,col` (`TextStats.TryParseLineColumn`) and centres the line. In preview-only view it scrolls the preview.
    - **9.4** `MarkdownRenderer.Render` returns the HTML and the headings from a single parse, as plain text without markdown syntax.
      - The outline is a panel left of the editor, toggled with Ctrl+Shift+O or its button; the choice is saved as `AppSettings.ShowOutline`. It also updates in editor-only mode.
      - Clicking a heading, or pressing Enter, puts it at the top of both panes. The section containing the caret is highlighted.
    - **Bug fixed along the way:** for setext headings (text underlined with `---`/`===`), Markdig's `Line` points at the underline. `MarkdownRenderer.SourceLine` now uses the block's span, which also corrects their `data-line` scroll anchors.

### Milestone 10: Markdown and preview extras (done)

- [x] **10.1 Emoji shortcodes** (S, `MarkdownRenderer`)
  - Enable `UseEmojiAndSmiley`, so `:tada:` shows 🎉.
  - *Done when:* shortcodes render, and a unit test covers them.
  - **Decision:** `UseEmojiAndSmiley(enableSmileys: false)`. GitHub only converts `:shortcodes:`, so `:)` stays as typed. Unknown codes and times like `10:30:45` are left alone (unit-tested).
- [x] **10.2 Math** (M, `MarkdownRenderer`, `Assets/Preview/vendor/`, `preview.js`, export)
  - Render `$inline$` and `$$block$$` with KaTeX. `UseMathematics` was already on through `UseAdvancedExtensions` and outputs `span.math` / `div.math`.
  - *Done when:* formulas render in the preview and in exports. The CSP is unchanged.
  - **Vendoring:** KaTeX 0.16.11 in `vendor/katex/`: `katex.min.js`, `katex.min.css` and only the 20 WOFF2 fonts (every WebView2 reads WOFF2, so the TTF/WOFF copies would only add weight). `MediaTypes` maps `.woff2` → `font/woff2`.
  - **How it works:**
    - `preview.js` `renderMath` keeps the TeX source in `data-tex` (so a re-render doesn't re-parse KaTeX output) and renders with `throwOnError: false`, so a typo shows red instead of breaking the page.
    - **HTML/PDF export:** the formulas are already rendered in the DOM. `ExportService.BuildStandaloneHtml` appends KaTeX's CSS, with the fonts inlined as base64 data URIs, *only* when the HTML contains `class="katex` (a few hundred KB of fonts; plain documents stay small).
    - **Copy as HTML:** pasted into Word or mail there is no KaTeX CSS, so the clipboard copy re-renders math as MathML only (`output: 'mathml'`), which Word and browsers understand natively.
  - Dollar amounts such as `$5 and $10` are not treated as math (unit-tested).
- [x] **10.3 Heading links across files** (S, `PreviewView.HandleLink`, `MainWindow`)
  - `other.md#setup` opens the file (or switches to its tab) and puts that heading at the top of both panes.
  - **How it works:** `HeadingInfo` now carries Markdig's auto-identifier `Id` (the same id the HTML uses, e.g. `getting-started-1` for a repeat). `MarkdownFileLinkClicked(path, headingId)` → `MainWindow.OpenAtHeading` finds the heading case-insensitively and navigates at `ContextIdle` priority, after the new tab's editor has loaded its text.
  - **Outcome:** checked in the app; the link opened `other.md` with the caret on `## Setup` and the preview showing it at the top.
- [x] **10.4 Jump from the preview to the source** (S, `preview.js`, `MainWindow`)
  - Double-clicking an element posts `{type:"jumpToLine", line}` with the nearest `data-line`; the editor gets the caret and focus. In preview-only view it switches to Split first.
  - **Outcome:** checked in the app (double-clicking a paragraph on source line 5 gave `Ln 5, Col 1`).
- [x] **10.5 Copy button on code blocks** (S, `preview.js`, `preview.css`)
  - A hover button at the top right of each `<pre>` (not front matter, not Mermaid errors). It posts `{type:"copyText", text}` and the app sets the clipboard, so the page needs no clipboard permission and the CSP stays strict. The label changes to "Copied" for 1.5 s.
  - Exports remove `.copy-code` along with `.front-matter`.
  - **Outcome:** checked in the app; the clipboard held the exact code text.
### Milestone 11: Editing comfort (done)

- [x] **11.1 Indent and outdent list items** (S, `MarkdownFormatting`)
  - Tab and Shift+Tab on a list line (or on selected list lines) indent or outdent by the list's own indent unit.
  - Elsewhere, Tab keeps AvalonEdit's normal behaviour.
  - *Done when:* nested bullets, numbered lists and tasks work, with unit tests.
  - **Outcome:** checked in the app (nest, double nest, Shift+Tab back, and a plain line still gets a normal tab), plus 18 unit tests.
  - **How it works** (`MarkdownFormatting.ShiftListItems`, called from the editor's `PreviewKeyDown`):
    - **Indent step:** markdown nests an item when it starts at the *content column* of the item above, so the step is that sibling's marker width: `- ` → 2, `1. ` → 3, `10. ` → 4. The first item of a level uses its own marker width. Tab-indented lists move by one tab.
    - **Outdent:** back to the parent item's indent (the nearest item above with less indent), or 0.
    - **Selections:** applies only when every non-blank selected line is a list item. The shallowest line sets the step and every line moves by the same amount, so children keep their shape. One undo step.
    - **Decision:** on a list line, Tab nests the item wherever the caret is (like VS Code and GitHub's editor), so a literal tab can't be typed mid-item. That's rarely wanted in markdown.
    - **Not done:** numbered items keep their number when nested (`2.` under `1.` stays `2.`, and renders as a sub-list starting at 2). Renumbering could be added later.
    - The logic takes a `TextDocument`, not a `TextEditor`, so it's unit-testable without a UI thread; text anchors keep the caret and selection on the same text.
- [x] **11.2 Auto-close pairs** (S, `Editing/AutoPairs.cs`, Settings toggle)
  - Typing `(`, `[`, `` ` `` or `"` with text selected wraps the selection. With nothing selected, it inserts the closing character, and typing that character steps over it.
  - It can be turned off in Settings.
  - *Done when:* it behaves like VS Code for these characters, and turning it off restores plain typing.
  - **Outcome:** checked in the app (a sentence with `( ) [ ] " "` and backticks came out exactly as typed), plus unit tests. Setting: `AppSettings.AutoClosePairs` (on by default), "Close brackets and quotes" in Settings.
  - **Rules** (VS Code's, adapted):
    - An opener only auto-closes before whitespace, the end of the line or `;:.,=}])>`` ` ``, so `(` typed in front of a word stays single.
    - Quotes and backticks don't auto-close after a letter or digit (`it"`, closing a word), or after another backtick, so typing ```` ``` ```` for a fence gives exactly three.
    - A typed closer steps over the same character when it's next. **Decision:** it steps over any matching closer, not only auto-inserted ones (VS Code tracks which were auto-inserted); simpler, and right in practice.
    - Backspace between an empty pair deletes both. Box (Alt+drag) selections are left to the editor.
  - Hooked into AvalonEdit's `TextArea.TextEntering` (setting `Handled` cancels the normal insert); logic on `TextDocument` for tests.
- [x] **11.3 Table formatting** (M, `Editing/TableFormatter.cs`)
  - A command (Ctrl+Shift+F, or a command-bar button) that pads a pipe table's columns to equal width and keeps the alignment colons.
  - Plus a command that adds a row below the caret.
  - Must count characters correctly for emoji and CJK text (use string info, not `.Length`).
  - *Done when:* the messy tables in the tests come out aligned, and the rendered output is unchanged.
  - **Outcome:** checked in the app and by unit tests, including CJK/emoji widths and "rendered HTML is identical before and after".
  - **How it works:**
    - Ctrl+Shift+F or the Table button formats the table at the caret; on a blank line it inserts a 2×2 template instead. Ctrl+Shift+Enter adds an empty row below the caret's row (below the separator from the header) and puts the caret in its first cell. One undo step each.
    - Widths: one column per text element (`StringInfo`), two for CJK and emoji (`TableFormatter.Width`).
    - Cells beyond the header's count (GFM ignores them) are kept as they were rather than dropped.
  - **Markdig, not GitHub, decides what's a table.** Two differences found by the tests:
    - Markdig doesn't split cells on a pipe inside a `code span` (GitHub does), so the formatter doesn't either.
    - A row without any pipe turns Markdig's whole block into a paragraph. So the formatter checks `MarkdownRenderer.IsTable` first and leaves such text alone; formatting can never change what the preview shows.
  - Known gap: an added row is padded, so typing into it pushes its closing pipe out until the next Ctrl+Shift+F.
- [x] **11.4 Tab management** (M, `MainWindow.xaml`, `MainViewModel`)
  - Reorder tabs by dragging. A tab right-click menu with Close, Close others, Close to the right, Copy path and Reveal in Explorer.
  - Save all (Ctrl+Alt+S).
  - *Done when:* the new order persists in the session, and Close others prompts only for unsaved tabs.
  - **Outcome:** dragging checked in the app; the commands and session order are unit-tested. The right-click menu itself wasn't checked in the app (the test run was stopped, see the note under 11.6).
  - **How it works:**
    - Dragging is plain mouse tracking on the tab strip (`OnTabStripMouseMove` → `MainViewModel.MoveTab`), not OLE drag-and-drop: the window already handles file drops, and nothing needs transferring. A tab swaps only when the mouse passes the middle of its neighbour, so tabs of different widths don't swap back and forth.
    - Close others / Close to the right (`MainViewModel`) ask only about unsaved tabs; Cancel stops and keeps the rest. Closed tabs go to the Ctrl+Shift+T list. The menu also has Save all; Copy path and Reveal are disabled for untitled tabs.
    - Save all saves every unsaved tab; untitled ones ask for a name, and cancelling skips just that one.
  - **Caveat:** on keyboard layouts where AltGr = Ctrl+Alt (Polish, for example), Ctrl+Alt+S takes AltGr+S (ś). Kept as planned; revisit if anyone reports it.
- [x] **11.5 Zoom** (S, `MainWindow`, `AppSettings`)
  - Ctrl+scroll wheel and Ctrl+= / Ctrl+- / Ctrl+0 change the editor font size, saved to the settings.
  - Ctrl+wheel over the preview keeps WebView2's own zoom.
  - *Done when:* zoom survives a restart and matches the Settings value.
  - **Outcome:** checked in the app: Ctrl+= twice wrote `FontSize: 16` to settings.json and showed "Editor zoom 114% (16 pt)" in the command bar; Ctrl+0 went back to 14.
  - Zoom *is* the Font size setting (one value, so they always match), clamped to 8–40. Numpad +/−/0 work too. The keys are window-level, so they also work while the preview has focus (they zoom the editor; WebView2's own key zoom is off with its accelerator keys).
- [x] **11.6 Paste images** (M, `MainWindow`, `MarkdownFormatting`)
  - Pasting an image from the clipboard (for example a screenshot) saves it as a PNG next to the document, in an `images/` subfolder with a timestamped name, and inserts the link.
  - For untitled tabs, ask the user to save first.
  - *Done when:* the pasted image appears in the preview, and undo removes the link (the file stays; record that rule here).
  - **Outcome:** checked in the app: the PNG went to `images/image-20261124-145525.png`, the relative link was inserted and the preview showed it. Undo after a paste wasn't checked in the app (stopped, see below); the link is inserted as one document change, which undo removes.
  - **Rules:**
    - **Undo removes the link; the image file stays.** Deleting files on undo could lose a picture used elsewhere.
    - Text on the clipboard always wins (Word and browsers put text and a picture there together).
    - Image *files* copied in File Explorer are linked where they are, like dropping them.
    - The clipboard's "PNG" stream (screenshot tools provide it) is written as is, keeping transparency; otherwise the bitmap is encoded as PNG.
    - Names: `image-yyyyMMdd-HHmmss.png`, with `-2`, `-3`… when taken (`MarkdownFormatting.PastedImagePath`).
    - Untitled tab: a dialog offers Save as…; cancelling it cancels the paste.
  - Hooked in with `CommandManager.AddPreview(CanExecute|Executed)Handler` on the text area, because AvalonEdit's own paste only handles text.
  - **Testing note (11.2–11.6):** the in-app run sent keystrokes with `keybd_event`, which goes to whatever window is in front. Partway through, the developer switched to a Remote Desktop window and a few keys (Ctrl+Z, Ctrl+S, a right-click, Down, Down, Enter) went there. The run was stopped. See the gotcha in `.claude/knowledge/conventions.md`.

### Milestone 12: Quality and reach (12.1–12.2 done; 12.3–12.4 are manual tests)

- [x] **12.1 Accessible names** (S, all XAML)
  - Set `AutomationProperties.Name` on every icon-only button (Bold, Italic, Find, Settings, view modes, tab close, find-bar buttons).
  - *Done when:* Narrator reads every button, and Accessibility Insights FastPass is clean.
  - **Outcome:** names added to every icon-only button, the view-mode toggles, each tab's close button ("Close notes.md"), the find bar (boxes, toggles, buttons), and the Settings controls whose label is in a separate card header.
    - The line-ending and update buttons set their name in code with the value included ("Line endings: Unix (LF)"), because a name replaces the visible text for screen readers.
    - **Guard:** `Milestone12Tests` reads every `.xaml` and fails when a button, toggle or input has neither visible text nor a name. It caught the line-ending button.
  - **Not done:** the Narrator and Accessibility Insights pass needs a person at the PC; still to do by hand.
- [x] **12.2 Large-file performance** (M, `MainWindow` render pipeline)
  - Measure a 1 MB and a 5 MB markdown file: time per keystroke and per render.
  - If it's slow: render off the UI thread (Markdig is thread-safe), stretch the debounce for big files, and skip unchanged Mermaid diagrams (already cached).
  - *Done when:* typing in a 1 MB file stays smooth, with the measured numbers recorded here.
  - **Measured** (Release build, generated markdown mixing headings, prose, lists, code, tables and math; best of 3):

    | Step | 1 MB (58k lines) | 5 MB (288k lines) |
    |---|---|---|
    | AvalonEdit: insert a character | 0.005 ms | 0.005 ms |
    | `Document.Text` snapshot after an edit | 0.8 ms | 3 ms |
    | `MarkdownRenderer.Render` (preview + outline) | **191 ms** | **930 ms** |
    | Word count | 29 ms | 48 ms |
    | Line endings (TextDocument scan / string scan) | 6 / 0.4 ms | 39 / 2 ms |
    | JSON message to the preview (HTML is 3.8 / 19.4 MB) | 6 ms | 63 ms |

  - **Before:** the render ran on the UI thread after every 150 ms pause, freezing typing for 0.2 s (1 MB) to 0.9 s (5 MB).
  - **Changes:**
    - Documents over 100,000 characters render on a background thread from a text snapshot; a result is dropped if the text or tab changed meanwhile (`_renderGeneration`). Smaller ones stay synchronous (≤ ~20 ms), so nothing changes for normal files.
    - The pause before rendering grows with size: 150 ms, 400 ms from 100k characters, 800 ms from 1M.
    - Word count and line endings run off the UI thread the same way (`TextStats.DetectLineEnding(string)`).
  - **After:** per pause the UI thread spends about 2 ms (1 MB) / 10 ms (5 MB) plus the preview message (6 / 63 ms). Typing in 1 MB stays smooth.
  - **Left:** at 5 MB the 63 ms message serialization is still on the UI thread (once per pause), and the browser takes a while to lay out ~19 MB of HTML in its own process. Not worth more code until someone edits files that big.
  - Not checked by typing in the running app (UI testing was paused); measured in isolation.
- [ ] **12.3 Windows 10 and high contrast** (S, manual test)
  - Run the release exe on Windows 10 (a VM is fine) and in a high-contrast theme.
  - Fix anything broken. Windows 10 is supported, as the README says.
  - *Done when:* the main flows work on both. Record the results here.
- [ ] **12.4 Test Copy as HTML in Office and Teams** (S, manual test)
  - Paste the demo document into Outlook, Word and Teams; check code colours, tables, the diagram PNG and images.
  - *Done when:* each target is checked and the results recorded. Fix inline styles where a target drops them.

### Milestone 13: Distribution

- [~] **13.1 Release workflow** (M, `.github/workflows/release.yml`). Written, **not yet run.**
  - Pushing a `v*` tag runs `dotnet test`, publishes with the `win-x64` profile and generates `SHA256SUMS.txt`.
  - It adds a build attestation (`actions/attest-build-provenance`) and creates the GitHub release with both files.
  - After this, update the release steps in CLAUDE.md.
  - *Done when:* tagging `v1.0.1` produces a release whose exe passes `gh attestation verify`.
  - **What it does:** on a `v*` tag (windows-latest): checks the tag equals `v<Version>` from the csproj, `dotnet test -c Release`, the portable exe, Inno Setup via Chocolatey and `build-installer.ps1`, `SHA256SUMS.txt` for both exes, provenance attestations for both, then `gh release create … --draft --generate-notes --verify-tag`.
  - **Decision: the release is a draft.** The developer reviews the notes and publishes it on GitHub; the website's `/releases/latest/download/…` links only follow published releases, so nothing goes public by itself.
  - The tag name reaches scripts through `env:`, never pasted into the script text.
  - **First run (v1.2.0):** stopped at the version check, because the tag was pushed without bumping `<Version>` (still 1.1.0); nothing was built. **Changed:** the tag is now the source of the version. The workflow stamps it into the runner's csproj (a mismatch only warns), and a manual run (`workflow_dispatch` with a `tag` input) rebuilds an existing tag without moving it. The csproj on main was bumped to 1.2.0 as well.
  - **To finish:** push the next version tag, check the run, then `gh attestation verify LightMarkdownWriter.exe -R lucasriechelmann/LightMarkdownWriter`. CLAUDE.md lists the workflow first and keeps the manual steps as the fallback until then.
- [ ] **13.2 Code signing** (M, release workflow; needs an account)
  - Apply to SignPath Foundation (free for open source), or use Azure Trusted Signing (about $10/month; check eligibility).
  - Sign the exe in the release workflow.
  - *Done when:* the released exe has a valid Authenticode signature. SmartScreen reputation still builds up over the first downloads.
- [x] **13.3 Update check** (S, `Services/UpdateService.cs`, Settings)
  - At most once a day, ask the GitHub releases API for the latest tag. If it's newer than the running version, show "Version X available" with a Download button in the command bar.
  - It can be turned off in Settings. No automatic installing.
  - *Done when:* faking an older version shows the prompt, and the check fails silently when offline.
  - **Outcome:** unit-tested with a fake HTTP handler (newer / same / older tags, once-a-day caching, turned off = no request, offline, HTTP 403 rate limit, drafts, pre-releases, bad JSON). **Not checked in the app** (UI testing paused): run a debug build with `LMW_FAKE_VERSION=1.0.0` to see the button.
  - **How it works:**
    - After the window loads, `UpdateService.CheckAsync` calls `GET /repos/lucasriechelmann/LightMarkdownWriter/releases/latest` with a generic `User-Agent: LightMarkdownWriter/<version>` (GitHub requires one) and a 10 s timeout. Nothing else is sent.
    - The result is cached in settings.json (`LastUpdateCheckUtc`, `LatestReleaseTag`, `LatestReleaseUrl`), so GitHub is asked at most once a day and a known update still shows on later starts. A failed check isn't recorded, so the next start retries.
    - Versions compare as major.minor.patch (`v1.2.0-beta` → 1.2.0). Drafts and pre-releases are ignored.
    - The "Version X available" button sits left of the outline button and opens the release page in the browser. Only `https://github.com/…` URLs from the response are used; anything else falls back to the releases page.
    - Settings → Updates → "Check for updates" (`AppSettings.CheckForUpdates`, on by default). Turning it on checks immediately.
- [ ] **13.4 winget package** (S, a PR to `microsoft/winget-pkgs`)
  - Submit a manifest so `winget install LightMarkdownWriter` works.
  - Best done after 13.1 and 13.2, so updates are routine and the exe is signed.
  - *Done when:* the manifest is merged and `winget install` works on a clean machine.
- [x] **13.6 Installer** (M, `installer/`, `win-x64-installer.pubxml`). Added on request; it wasn't in the original list.
  - Inno Setup 6 script (`installer/LightMarkdownWriter.iss`), built by `installer/build-installer.ps1`, giving `artifacts/installer/LightMarkdownWriter-Setup.exe` plus `.sha256`. The name has no version so the website can link to `/releases/latest/download/LightMarkdownWriter-Setup.exe`; the version is in the file properties.
  - **Decisions** (from the developer):
    - **Bundles .NET** (self-contained), so nothing else needs installing.
    - **Per-user install**, no admin: `%LOCALAPPDATA%\Programs\LightMarkdownWriter`.
    - **`.md` association is an optional task**, ticked by default.
  - **Size and startup** (measured):
    - The self-contained single-file exe is 180 MB uncompressed. With `EnableCompressionInSingleFile` it's 77 MB, with about 0.9 s to show a document (against 0.66 s for the framework-dependent exe). Compression was chosen.
    - The setup exe is 71.6 MB. Inno's LZMA can't shrink an already-compressed bundle much.
  - **Double-click on `.md`:**
    - Setup and the app's Settings → Register set the `.md`/`.markdown` default *only when no other app is the default*. Uninstall removes it only if it's still ours.
    - Windows doesn't let any installer override a user's existing choice. If another app owns `.md`, setup's last page offers `ms-settings:defaultapps?registeredAppUser=LightMarkdownWriter`, where the user clicks Set default.
    - Settings shows whether the app really is the default (it reads `UserChoice`).
  - **Uninstall** removes every association key (also ones created from Settings) but keeps `%LOCALAPPDATA%\LightMarkdownWriter`: tabs, settings and backups.
  - **Upgrades:** setup closes a running copy with the Restart Manager. The app now saves its session on `Application.SessionEnding` (which is also how Windows shutdown and sign-out reach it), because WPF doesn't raise `Window.Closing` there.
  - **Released in v1.1.0.** The website's main download button now points to the installer, with the portable exe as a secondary link.
- [x] **13.5 Website: Windows 10 note** (S, `docs/index.html`). Done with the v1.1.0 site update, which also made the installer the main download.
  - Mention "Windows 10 1809 or later" in the install section, to match the README.
  - *Done when:* the live site says it.

### Housekeeping

- [ ] **H.1 Context drift check:** re-run `/onboard-repo` after each milestone above, so `CLAUDE.md` and `.claude/knowledge/` match the code.

### Later (undecided)

- **Spell check:** WPF's built-in spell check doesn't work in AvalonEdit. It would need a custom spell-check layer, for example using Windows' own spell-check service. That's a large job.
- **Folder sidebar:** a file tree of the current folder. It moves the app toward a notes app, so only add it if the app should go that way.

### Out of scope

- **Cross-platform:** the app is WPF and WebView2 by design.
