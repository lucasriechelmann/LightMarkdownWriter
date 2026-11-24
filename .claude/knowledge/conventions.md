# Conventions

Each pattern below names a real file to copy from.

## Naming and layout

- Namespaces follow folders: `LightMarkdownWriter.ViewModels`, `.Views`, `.Services`, `.Editing`, `.Converters`.
  - `Editing` (not `Editor`) avoids clashing with the `Editor` control field in `MainWindow`.
- View models end in `ViewModel`; services end in `Service`, or are named for their job (`MarkdownRenderer`, `PreviewAssets`, `SingleInstance`, `MediaTypes`).
- Private fields use `_camelCase`. Comments explain *why* rather than restating the code. Long-lived decisions go in PLAN.md.
- Paths use forward slashes in docs. In code, use `Path.Combine`, and `/` only for embedded resource paths.

## MVVM and commands

- **View models** use CommunityToolkit.Mvvm: `[ObservableProperty]` fields, `[RelayCommand]` methods, and `partial void On…Changed` hooks.
  - Reference: `ViewModels/MainViewModel.cs`, `ViewModels/DocumentViewModel.cs`.
- **Where commands go (canonical split):**
  - Document and tab state (new, open, save, close, tab switching, view mode, reload/keep) → a `[RelayCommand]` on `MainViewModel`, bound in `MainWindow.xaml` (`KeyBinding` or `Command=`).
  - Anything that needs the editor or WebView control (formatting, find/replace, export, settings window) → code-behind in `MainWindow.xaml.cs`. Shortcuts go through `AddKeyBinding(InputBindings | Editor.TextArea.InputBindings, …)`, buttons through `Click` handlers.
  - Editor-only shortcuts, such as formatting, go on `Editor.TextArea.InputBindings`. Shortcuts that must also work while the preview has focus go on the window's `InputBindings`.
- **View-model state the window reacts to** is wired in `MainWindow.OnViewModelPropertyChanged`, which handles `ActiveDocument` and `ViewMode`.
- **Settings** are bound directly to `AppSettings` (`Views/SettingsWindow.xaml`). `MainWindow.OnSettingsChanged` applies them live, and `SettingsService` saves on every `PropertyChanged`.

## Dialogs (canonical)

- **View models** receive `IDialogService` through their constructor, as `MainViewModel` does. It's injected in `App.xaml.cs` and keeps view models testable.
- **Views** (windows and controls) may create `new DialogService()` directly, as `MainWindow` and `SettingsWindow` do.
- Message boxes are `Wpf.Ui.Controls.MessageBox`. Write the full name, because `System.Windows.MessageBox` is ambiguous.

## Error handling

- Catch **specific** exceptions with filters, never a bare `catch`. For example: `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`.
  - References: `ViewModels/MainViewModel.cs` (`TrySave`, `OpenPath`), `Services/SessionService.cs`.
- **User-facing failures** (open, save, export, registry) → `DialogService.ShowError` with what failed and the OS message.
- **Background failures** (session or settings save, backup cleanup) are swallowed with a comment saying why. The next attempt retries.
  - If the session save fails when closing, the app falls back to asking whether to save (`MainWindow.OnClosing`). Never lose text silently.
- **No logging framework.** There's no logger or tracing. Don't add one without a decision in PLAN.md.
  - The one exception is `CrashLog.Write` (`Services/CrashLog.cs`), used only by the crash handlers in `App.xaml.cs`, for exceptions.
- **Unhandled exceptions** are caught globally (see `App.InstallCrashHandlers`). That's a safety net, not a substitute for the specific catches above.
  - `MainWindow.PersistSessionNow()` is the only place that catches *every* exception, because a failure while saving during a crash must not cause a second crash.
- **Testing the safety net:** debug builds throw a test error on **Ctrl+Alt+Shift+F12** (guarded by `#if DEBUG` in `MainWindow.SetUpExport`).

## Files and persistence

- **Writes are atomic:** temp file, then `File.Move(..., overwrite: true)`. Reference: `SessionService.WriteAtomic`.
- **Reading user files** uses `FileShare.ReadWrite | FileShare.Delete`, with UTF-8 without BOM as the default encoding and BOM detection on. Keep the detected encoding for saving. Reference: `DocumentViewModel.ReadFile`.
- **App data** lives under `%LOCALAPPDATA%\LightMarkdownWriter\`, as JSON via `System.Text.Json`.

## UI (Windows 11)

- Windows are `ui:FluentWindow` with `ExtendsContentIntoTitleBar`, `WindowBackdropType="Mica"` and a `ui:TitleBar`. Reference: `MainWindow.xaml`, `Views/SettingsWindow.xaml`.
- Colours come from WPF-UI theme resources via `DynamicResource`, such as `SolidBackgroundFillColorTertiaryBrush`, `CardStrokeColorDefaultBrush` and `AccentTextFillColorPrimaryBrush`. Never hard-code theme colours in XAML.
  - The preview page gets matching colours through the `theme` message.
- Settings-style pages use `ui:CardControl` rows (`SettingsWindow.xaml`).
- Icons are `ui:SymbolIcon` / `SymbolRegular`.
  - **Check the name exists** first: `[Enum]::IsDefined` via reflection on Wpf.Ui.dll. Grepping the DLL's bytes is unreliable, because suffix names are merged in the string table.
  - **Some enum values have no glyph** in the bundled font (for example `LayoutColumnTwo20`). Look at the result in the app.
- **Airspace:** nothing may be drawn over the preview's HWND. Status messages go in the command bar (`StatusPanel`), dialogs in their own window.

## Preview page (`Assets/Preview/`)

- Plain JS in an IIFE (`preview.js`), with no build step and no framework.
  - New features add a `type` to the message switch at the bottom.
  - Update the message table in `architecture.md` when you do.
- The CSP in `index.html` only allows scripts from `https://app.local`. Never add `unsafe-inline` to `script-src`.
- New assets go in `Assets/Preview/`. They're embedded automatically by the csproj wildcard and served by `PreviewAssets.Open`.

## Tests

- xUnit in `tests/LightMarkdownWriter.Tests/`, for non-UI logic in services and view models. `dotnet test` must pass.
- UI changes are verified in the running app.
- Registry tests use a throwaway `HKCU\Software\LightMarkdownWriter.Tests\{guid}` key, deleted in `Dispose`. Never touch the real file associations. Reference: `FileAssociationServiceTests.cs`.
- Filesystem tests use `Directory.CreateTempSubdirectory`. Reference: `ExportAndDocumentTests.cs`.
- Editor features take a `TextDocument` plus offsets and return the new selection (`MarkdownFormatting.ShiftListItems`, `AutoPairs.Type`, `TableFormatter.FormatOrInsert`); a thin `TextEditor` overload applies it. `TextEditor` needs a UI thread, `TextDocument` doesn't. Text anchors (`doc.CreateAnchor`) keep the caret on the same text through edits.
- `SessionService(root)` and `UpdateService.CheckAsync(…, handler, now)` take a folder / fake HTTP handler / clock for tests (`Milestone11Tests`, `Milestone13Tests`). Never hit the real network in tests.
- `Milestone12Tests` parses every `.xaml` and fails on a button, toggle or input without visible text or `AutomationProperties.Name`. Name new icon-only buttons.
- Pure logic that sits next to an OS call gets split out so it can be tested. For example, `ExportService.BuildCfHtml` is separate from `CopyHtmlToClipboard`.

## Gotchas (hard-won)

**AvalonEdit `.xshd`**
- `<Rule>` content can't be CDATA. Escape `<` as `&lt;` instead; `<Begin>` and `<End>` do accept CDATA.
- Rules are compiled with `ExplicitCapture`, so backreferences need named groups (`(?<m>…)\k<m>`).
- Loaded definitions are frozen, so palettes are substituted into the XML text before loading (`MarkdownHighlighting.cs`).

**WebView2**
- `SetVirtualHostNameToFolderMapping` only applies on the next navigation. That's why both hosts are served by `WebResourceRequested` instead.
- `WebView2CompositionControl` needs the roughly 25 MB Windows SDK projection, so the HWND `WebView2` is used. The consequence is airspace: no transparency, no clipping, no overlays.
- Accelerator keys reach WPF only as a `KeyDown` on the control. `MainWindow.OnPreviewKeyDown` replays the window's `KeyBinding`s from there.

**Text and clipboard**
- `Encoding.WebName` hides whether there's a BOM, and `GetEncoding("utf-8")` writes one.
- CF_HTML offsets are in UTF-8 bytes. .NET Framework readers decode the data as ANSI, so don't trust them when checking offsets.

**Editor colouring the `.xshd` can't express**
- Anything tied to the document's *start*, such as YAML front matter, goes in a `DocumentColorizingTransformer` added to `TextView.LineTransformers` (reference: `Editing/FrontMatterColorizer.cs`). Cache results per `document.Version`.
- Theme brushes come from `MarkdownHighlighting.Brush(name, dark)`.

**WPF context menus and markdown extras**
- A `ContextMenu` lives outside the visual tree. When opening one from a `Click` handler, set `PlacementTarget` and `DataContext` explicitly (`MainWindow.OnStatusMenuClick`), or command bindings won't resolve.
- **Alert icons:** Markdig's alert SVGs lack GitHub's `.octicon` class. Without the `fill: currentColor` rule in `preview.css`/`export.css` they render black.
- **Browsers rewrite inline colours.** When checking clipboard or export HTML produced by `preview.js`, inline colours come out as `rgb(…)`, not the `#hex` written in the code.

**Editor font**
- Cascadia **Code** ligatures disguise markdown syntax (`=>`, `-->`, `|---`), so the default is Cascadia **Mono**.

**UI tests on the real desktop** (see PLAN.md for context)
- **`keybd_event` and `mouse_event` go to whatever window is in front, not to the app.** If the developer switches windows mid-run, the keystrokes land in their window (it happened during 11.x: Ctrl+Z, Ctrl+S and Enter reached a Remote Desktop session). Before sending input, check `GetForegroundWindow` belongs to LightMarkdownWriter, and stop if it doesn't. Only run input-driven tests when the developer agrees they're away from the keyboard.
- **Typing text without that risk:** post `WM_CHAR` to the app's window handle (`PostMessage(hwnd, 0x0102, ch, 1)`). WPF turns it into `TextInput`, so `(` and `"` need no Shift and nothing goes to another window. It can't send shortcuts (Ctrl+…).
- WScript `SendKeys` drops modifiers. Shift+navigation keys become numpad keys when NumLock is on, and Ctrl sometimes goes missing too. Use `keybd_event` and press each modifier explicitly.
- Test windows grab focus, so the developer's own typing can end up in them. Launch with `-WindowStyle Minimized` when you're only checking state.
- Inspect the preview with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` and Chrome DevTools Protocol `Runtime.evaluate`.
- Capture windows with `PrintWindow` (`PW_RENDERFULLCONTENT`), so other windows on screen are never captured.
- In PowerShell P/Invoke, pass `[NullString]::Value`, not `$null`, for a string that should be NULL.
