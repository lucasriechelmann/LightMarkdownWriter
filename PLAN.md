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

### Milestone 9: Finding your way around

- [ ] **9.1 Recent files** (M, `SettingsService` or a new `RecentFilesService`, command bar)
  - Keep the last 15 opened files, most recent first, and drop missing ones when shown.
  - Show them in an Open drop-down, and in the Windows jump list on the taskbar (`JumpList`).
  - *Done when:* the list survives restarts, and picking a file opens it (or switches to it if it's already open).
- [ ] **9.2 Reopen closed tab** (S, `MainViewModel`)
  - Ctrl+Shift+T reopens the last closed tabs in order, including unsaved text discarded at close (held in memory for the session).
  - *Done when:* closing three tabs and pressing Ctrl+Shift+T three times brings them back in reverse order.
- [ ] **9.3 Go to line** (S, `MainWindow`)
  - Ctrl+G opens a small line-number box in the command bar (not over the preview), then jumps there and centres the line.
  - *Done when:* it works from the editor and from the preview.
- [ ] **9.4 Heading outline** (M, new view, `MarkdownRenderer`)
  - A toggleable panel listing the document's headings, built from the Markdig syntax tree.
  - Clicking a heading scrolls both panes, reusing the scroll-sync lines.
  - *Done when:* the outline updates as you type and clicking a heading lands on it in both panes.

### Milestone 10: Markdown and preview extras

- [ ] **10.1 Emoji shortcodes** (S, `MarkdownRenderer`)
  - Enable `UseEmojiAndSmiley`, so `:tada:` shows 🎉.
  - Note that it also turns smileys like `:)` into emoji. Decide whether to keep that, matching GitHub, which only converts shortcodes.
  - *Done when:* shortcodes render, and a unit test covers them.
- [ ] **10.2 Math** (M, `MarkdownRenderer`, `Assets/Preview/vendor/`, `preview.js`, export)
  - Render `$inline$` and `$$block$$` with KaTeX.
  - Vendor the pinned KaTeX JS, CSS and fonts. `UseMathematics` is already on through `UseAdvancedExtensions` and outputs `span.math` / `div.math`.
  - Exports must include KaTeX's CSS and fonts, or pre-render the formulas.
  - *Done when:* formulas render in the preview and in HTML/PDF exports. The CSP is unchanged (KaTeX needs no inline scripts).
- [ ] **10.3 Heading links across files** (S, `PreviewView.HandleLink`, `MainWindow`)
  - `other.md#setup` opens the file and scrolls both panes to that heading. Today it only opens the file.
  - *Done when:* the link lands on the heading in an already-open tab and in a newly opened one.
- [ ] **10.4 Jump from the preview to the source** (S, `preview.js`, `MainWindow`)
  - Double-clicking an element in the preview moves the editor caret to its `data-line` and focuses the editor.
  - *Done when:* double-clicking a paragraph, table row or list item puts the caret on that source line.
- [ ] **10.5 Copy button on code blocks** (S, `preview.js`, `preview.css`)
  - A hover button on each `<pre>` that copies the code, using a message to the app so the CSP stays strict.
  - Exports must not include the button.
  - *Done when:* it copies the exact code text and never appears in exports.

### Milestone 11: Editing comfort

- [ ] **11.1 Indent and outdent list items** (S, `MarkdownFormatting`)
  - Tab and Shift+Tab on a list line (or on selected list lines) indent or outdent by the list's own indent unit.
  - Elsewhere, Tab keeps AvalonEdit's normal behaviour.
  - *Done when:* nested bullets, numbered lists and tasks work, with unit tests.
- [ ] **11.2 Auto-close pairs** (S, `MarkdownFormatting`, Settings toggle)
  - Typing `(`, `[`, `` ` `` or `"` with text selected wraps the selection. With nothing selected, it inserts the closing character, and typing that character steps over it.
  - It can be turned off in Settings.
  - *Done when:* it behaves like VS Code for these characters, and turning it off restores plain typing.
- [ ] **11.3 Table formatting** (M, `MarkdownFormatting`)
  - A command (Ctrl+Shift+F, or a command-bar button) that pads a pipe table's columns to equal width and keeps the alignment colons.
  - Plus a command that adds a row below the caret.
  - Must count characters correctly for emoji and CJK text (use string info, not `.Length`).
  - *Done when:* the messy tables in the tests come out aligned, and the rendered output is unchanged.
- [ ] **11.4 Tab management** (M, `MainWindow.xaml`, `MainViewModel`)
  - Reorder tabs by dragging. A tab right-click menu with Close, Close others, Close to the right, Copy path and Reveal in Explorer.
  - Save all (Ctrl+Alt+S).
  - *Done when:* the new order persists in the session, and Close others prompts only for unsaved tabs.
- [ ] **11.5 Zoom** (S, `MainWindow`, `AppSettings`)
  - Ctrl+scroll wheel and Ctrl+= / Ctrl+- / Ctrl+0 change the editor font size, saved to the settings.
  - Ctrl+wheel over the preview keeps WebView2's own zoom.
  - *Done when:* zoom survives a restart and matches the Settings value.
- [ ] **11.6 Paste images** (M, `MainWindow`, `MarkdownFormatting`)
  - Pasting an image from the clipboard (for example a screenshot) saves it as a PNG next to the document, in an `images/` subfolder with a timestamped name, and inserts the link.
  - For untitled tabs, ask the user to save first.
  - *Done when:* the pasted image appears in the preview, and undo removes the link (the file stays; record that rule here).

### Milestone 12: Quality and reach

- [ ] **12.1 Accessible names** (S, all XAML)
  - Set `AutomationProperties.Name` on every icon-only button (Bold, Italic, Find, Settings, view modes, tab close, find-bar buttons).
  - *Done when:* Narrator reads every button, and Accessibility Insights FastPass is clean.
- [ ] **12.2 Large-file performance** (M, `MainWindow` render pipeline)
  - Measure a 1 MB and a 5 MB markdown file: time per keystroke and per render.
  - If it's slow: render off the UI thread (Markdig is thread-safe), stretch the debounce for big files, and skip unchanged Mermaid diagrams (already cached).
  - *Done when:* typing in a 1 MB file stays smooth, with the measured numbers recorded here.
- [ ] **12.3 Windows 10 and high contrast** (S, manual test)
  - Run the release exe on Windows 10 (a VM is fine) and in a high-contrast theme.
  - Fix anything broken. Windows 10 is supported, as the README says.
  - *Done when:* the main flows work on both. Record the results here.
- [ ] **12.4 Test Copy as HTML in Office and Teams** (S, manual test)
  - Paste the demo document into Outlook, Word and Teams; check code colours, tables, the diagram PNG and images.
  - *Done when:* each target is checked and the results recorded. Fix inline styles where a target drops them.

### Milestone 13: Distribution

- [ ] **13.1 Release workflow** (M, `.github/workflows/release.yml`)
  - Pushing a `v*` tag runs `dotnet test`, publishes with the `win-x64` profile and generates `SHA256SUMS.txt`.
  - It adds a build attestation (`actions/attest-build-provenance`) and creates the GitHub release with both files.
  - After this, update the release steps in CLAUDE.md.
  - *Done when:* tagging `v1.0.1` produces a release whose exe passes `gh attestation verify`.
- [ ] **13.2 Code signing** (M, release workflow; needs an account)
  - Apply to SignPath Foundation (free for open source), or use Azure Trusted Signing (about $10/month; check eligibility).
  - Sign the exe in the release workflow.
  - *Done when:* the released exe has a valid Authenticode signature. SmartScreen reputation still builds up over the first downloads.
- [ ] **13.3 Update check** (S, new service, Settings)
  - At most once a day, ask the GitHub releases API for the latest tag. If it's newer than the running version, show "Version X available" with a Download button in the command bar.
  - It can be turned off in Settings. No automatic installing.
  - *Done when:* faking an older version shows the prompt, and the check fails silently when offline.
- [ ] **13.4 winget package** (S, a PR to `microsoft/winget-pkgs`)
  - Submit a manifest so `winget install LightMarkdownWriter` works.
  - Best done after 13.1 and 13.2, so updates are routine and the exe is signed.
  - *Done when:* the manifest is merged and `winget install` works on a clean machine.
- [ ] **13.5 Website: Windows 10 note** (S, `docs/index.html`)
  - Mention "Windows 10 1809 or later" in the install section, to match the README.
  - *Done when:* the live site says it.

### Housekeeping

- [ ] **H.1 Context drift check:** re-run `/onboard-repo` after each milestone above, so `CLAUDE.md` and `.claude/knowledge/` match the code.

### Later (undecided)

- **Spell check:** WPF's built-in spell check doesn't work in AvalonEdit. It would need a custom spell-check layer, for example using Windows' own spell-check service. That's a large job.
- **Folder sidebar:** a file tree of the current folder. It moves the app toward a notes app, so only add it if the app should go that way.

### Out of scope

- **Cross-platform:** the app is WPF and WebView2 by design.
