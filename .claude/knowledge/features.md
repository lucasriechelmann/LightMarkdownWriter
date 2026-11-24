# Feature map

Each feature, in user terms, with the code that owns it. Folder-level ownership is preferred. Individual files are named only as entry points.

| Feature | What the user gets | Owned by |
|---|---|---|
| **Tabs and files** | New, open, save, save as, close (prompts if unsaved), Ctrl+Tab, middle-click close; an unsaved dot on the tab and in the title | `ViewModels/` (entry: `MainViewModel` commands), `Services/DialogService.cs` |
| **Editor** | AvalonEdit with line numbers and word wrap, Cascadia Mono, accent-coloured links | `MainWindow.xaml` (`Editor`), `MainWindow.xaml.cs` (`ApplyEditorSettings`) |
| **Syntax colours** | Markdown highlighting in a light or dark palette | `Editing/` (`Markdown.xshd`, `MarkdownHighlighting.cs`) |
| **Formatting and lists** | Ctrl+B / I / E / K, Ctrl+Shift+K, command-bar buttons; Enter continues lists, tasks and quotes | `Editing/MarkdownFormatting.cs`, wired in `MainWindow.SetUpEditing` |
| **Find and replace** | Ctrl+F / H, F3; match case, whole word and regex (Alt+C / W / R); highlights all matches; replace all as one undo step | `Views/FindReplaceBar.*`, `Editing/SearchResultRenderer.cs` |
| **Live preview** | GFM, highlight.js, Mermaid, relative images, `.md` links open as tabs, external links open in the browser | `Services/MarkdownRenderer.cs`, `Views/PreviewView.*`, `Assets/Preview/` |
| **GitHub alerts** | `> [!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` and `[!CAUTION]` styled like GitHub in the preview, exports and clipboard | `MarkdownRenderer` (via `UseAdvancedExtensions`), `preview.css`/`export.css`, `preview.js` (`inlineStyles`) |
| **YAML front matter** | A leading `---` block shows as a collapsed "Front matter" box, is left out of exports, and its keys are coloured in the editor | `MarkdownRenderer.FrontMatterHtml`, `Editing/FrontMatterColorizer.cs`, `preview.css` |
| **Status bar** | Line and column (selection size), word count (of the selection), line endings and encoding; click to convert | `MainWindow.xaml` (status bar), `MainWindow.xaml.cs` (status bar section), `Editing/TextStats.cs`, `MainViewModel` (conversion commands) |
| **Drag and drop** | Drop `.md`/`.txt` files to open them; drop images to insert links | `MainWindow.xaml.cs` (drag-and-drop section), `MarkdownFormatting.ImageLink` |
| **Crash safety net** | Unexpected errors save everything, show a message and keep the app running; details go to `crash.log` | `App.xaml.cs` (crash safety net), `Services/CrashLog.cs`, `MainWindow.PersistSessionNow` |
| **View modes** | Editor, Split or Preview (Ctrl+1 / 2 / 3); the splitter position is remembered | `MainViewModel.ViewMode`, `MainWindow.ApplyViewMode` |
| **Scroll sync** | The editor and preview follow each other by source line | `MainWindow.xaml.cs` (scroll sync section), `Assets/Preview/preview.js` |
| **Theme** | Follow Windows, or fixed Light/Dark; accent colour in editor links and preview links; `--theme=` override | `MainWindow.ApplyThemeSetting` / `ApplyPreviewTheme`, `App.xaml.cs` |
| **Session restore** | Closing never asks; tabs, unsaved text, caret, scroll, window placement and view mode come back | `Services/SessionService.cs`, `MainViewModel` (restore/persist), `MainWindow` (session section) |
| **External changes** | Clean tabs reload silently; tabs with unsaved edits show a banner (Reload / Keep mine / Close tab) | `DocumentViewModel.CheckDisk` / `ReloadFromDisk`, the banner in `MainWindow.xaml` |
| **Single instance** | A second launch opens its files in the running window | `Services/SingleInstance.cs`, `App.xaml.cs`, `MainWindow.OpenFromOtherInstance` |
| **Export** | HTML file (Ctrl+Shift+E), PDF (Ctrl+P), copy as HTML (Ctrl+Shift+C); always light; status message with Open | `Services/ExportService.cs`, `preview.js` (export section), `MainWindow.ExportAsync`, `Assets/Preview/export.css` |
| **Settings** | Theme, font, size, word wrap and line numbers, applied live (Ctrl+,); About | `Services/SettingsService.cs`, `Views/SettingsWindow.*` |
| **File association** | Register or remove "Open with" for `.md` and `.markdown`; open Default apps | `Services/FileAssociationService.cs`, the Files section of `SettingsWindow` |
| **Keyboard while the preview has focus** | Every app shortcut still works | `MainWindow.OnPreviewKeyDown` |
| **Packaging and icon** | One portable exe; the "MD" app icon | `Properties/PublishProfiles/win-x64.pubxml`, `Services/PreviewAssets.cs`, `Assets/app.ico` (from `tools/make-icon.ps1`) |
| **Website** | Landing page with the interactive split-view demo, features, shortcuts and install steps | `docs/index.html` (GitHub Pages from `main` `/docs`) |

## Planned features

Everything not built yet is planned as tasks in the **Roadmap** section of `PLAN.md`: milestones 8–13, with sizes and "done when" checks. When a task ships, add its feature to the table above and tick it off in PLAN.md.
